using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// Why a <c>gh api</c> call gave no answer. A rate limit ends whatever asked;
/// nothing retries it. <paramref name="Status"/> is the HTTP status GitHub
/// answered with, or null when none was seen.
/// </summary>
internal sealed record GhApiFailure(string Message, bool RateLimited, int? Status = null);

/// <summary>Reads the GitHub REST API through <c>gh api</c>, one GET per call, so the request carries gh's own User-Agent and sign-in.</summary>
internal interface IGhApiReader
{
    /// <param name="path">A REST path with its query, such as <c>orgs/octo/actions/secrets?per_page=100</c>.</param>
    Task<Result<JsonNode, GhApiFailure>> GetAsync(string path, CancellationToken cancellationToken);
}

/// <summary>The reader when no <c>gh</c> could be found: every read fails with the reason.</summary>
internal sealed class UnavailableGhApiReader(string reason) : IGhApiReader
{
    public Task<Result<JsonNode, GhApiFailure>> GetAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(reason, RateLimited: false)));
}

/// <summary>
/// <c>gh api --include --method GET &lt;path&gt;</c>. The status line and headers
/// tell a rate limit from any other refusal; the body is returned only on
/// success. Nothing gh prints on its error stream is relayed.
/// </summary>
internal sealed class GhApiReader(string executable, TimeSpan timeout) : IGhApiReader
{
    public const string RateLimitMessage =
        "GitHub's API rate limit was reached, so the check stopped there and nothing retried it. Wait for the limit to reset, then check again.";

    /// <summary>
    /// <c>--method GET</c> is explicit, and no field is ever passed: gh turns a
    /// request with fields into a POST.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string path) => ["api", "--include", "--method", "GET", path];

    public async Task<Result<JsonNode, GhApiFailure>> GetAsync(string path, CancellationToken cancellationToken)
    {
        Result<GhExit, string> run = await GhProcess.RunAsync(executable, Arguments(path), input: null, timeout, cancellationToken);
        return run.IsFailure
            ? Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(run.Error, RateLimited: false))
            : Read(path, run.Value);
    }

    /// <summary>
    /// The answer in <c>--include</c> output: a status line, headers, a blank
    /// line, and the body. GitHub answers an exceeded limit with 403 or 429: a
    /// primary limit with <c>x-ratelimit-remaining: 0</c>, a secondary one
    /// with <c>retry-after</c> or a message naming a secondary rate limit.
    /// Basis: https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api.
    /// </summary>
    public static Result<JsonNode, GhApiFailure> Read(string path, GhExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        string text = exit.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal);
        int split = text.IndexOf("\n\n", StringComparison.Ordinal);
        string[] head = (split < 0 ? text : text[..split]).Split('\n');
        string body = split < 0 ? string.Empty : text[(split + 2)..];
        string[] statusLine = head[0].Split(' ', 3);
        int status = statusLine.Length >= 2 && statusLine[0].StartsWith("HTTP/", StringComparison.Ordinal)
            && int.TryParse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;

        if (status is 403 or 429 && RateLimited(status, head, body))
        {
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(RateLimitMessage, RateLimited: true));
        }

        if (exit.ExitCode != 0 || status is < 200 or >= 300)
        {
            string answered = status == 0 ? "gh api exited with code " + exit.ExitCode : "GitHub answered HTTP " + status;
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(
                answered + " for " + path + "; check `gh auth status`, the name, and that the sign-in may read it (admin:org for organization secrets)",
                RateLimited: false,
                status == 0 ? null : status));
        }

        try
        {
            return JsonNode.Parse(body) is JsonNode node
                ? Result<JsonNode, GhApiFailure>.Success(node)
                : Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("gh api returned no body for " + path, RateLimited: false));
        }
        catch (JsonException)
        {
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("gh api returned something other than JSON for " + path, RateLimited: false));
        }
    }

    /// <summary>Whether a 403 or 429 answer is a rate limit, by its headers (the status line first) and its body's message.</summary>
    internal static bool RateLimited(int status, string[] head, string body)
    {
        if (status == 429 || Header(head, "x-ratelimit-remaining") == "0" || Header(head, "retry-after") is not null)
        {
            return true;
        }

        try
        {
            return JsonNode.Parse(body) is JsonObject answer
                && answer["message"] is JsonValue message
                && message.TryGetValue(out string? text)
                && text.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Header(string[] head, string name)
    {
        foreach (string line in head.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && string.Equals(line[..colon].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }
}

/// <summary>The reads the role check makes through <see cref="IGhApiReader"/>.</summary>
internal static class GhApiReads
{
    /// <summary>The most a page of the listing holds. Basis: https://docs.github.com/en/rest/actions/secrets#list-organization-secrets.</summary>
    public const int PageSize = 100;

    /// <summary>The most pages one listing reads: 5,000 secrets. Basis: judgment, far above what an organization holds.</summary>
    public const int MaxPages = 50;

    /// <summary>
    /// Every Actions secret of <paramref name="organization"/>, a page per call
    /// until a page comes back short of <see cref="PageSize"/>. That, not
    /// <c>total_count</c>, ends the listing, so a count that is too low cannot
    /// cut it off. The first failure ends it, and so does reaching
    /// <see cref="MaxPages"/>, as a failure: a cut-off listing would make a
    /// secret past the cut read as missing. The organization must already be
    /// held to GitHub's login rules: it is part of the path. A secret whose
    /// <c>updated_at</c> cannot be read is kept with no time.
    /// </summary>
    public static async Task<Result<OrganizationSecrets, GhApiFailure>> ListOrganizationSecretsAsync(
        this IGhApiReader reader,
        string organization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        List<ListedSecret> secrets = [];
        for (int page = 1; page <= MaxPages; page++)
        {
            string path = "orgs/" + organization + "/actions/secrets?per_page=" + PageSize.ToString(CultureInfo.InvariantCulture)
                + "&page=" + page.ToString(CultureInfo.InvariantCulture);
            Result<JsonNode, GhApiFailure> read = await reader.GetAsync(path, cancellationToken);
            if (read.IsFailure)
            {
                return Result<OrganizationSecrets, GhApiFailure>.Failure(read.Error);
            }

            if (read.Value["secrets"] is not JsonArray listed)
            {
                return Result<OrganizationSecrets, GhApiFailure>.Failure(
                    new GhApiFailure("GitHub's secret list for " + organization + " was not in the shape its documentation gives", RateLimited: false));
            }

            foreach (JsonNode? item in listed)
            {
                if (item?["name"] is JsonValue name && name.TryGetValue(out string? secretName))
                {
                    DateTimeOffset? updatedAt = item["updated_at"] is JsonValue updated && updated.TryGetValue(out string? stamp)
                        && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
                            ? parsed
                            : null;
                    secrets.Add(new ListedSecret(secretName, updatedAt));
                }
            }

            if (listed.Count < PageSize)
            {
                return Result<OrganizationSecrets, GhApiFailure>.Success(new OrganizationSecrets(organization, secrets));
            }
        }

        return Result<OrganizationSecrets, GhApiFailure>.Failure(new GhApiFailure(
            "the secret list for " + organization + " ran past " + MaxPages.ToString(CultureInfo.InvariantCulture)
            + " pages, so the check read none of it rather than part",
            RateLimited: false));
    }

    /// <summary>
    /// Who may read <paramref name="secret"/> now, so that setting it again
    /// passes that back: <c>gh secret set</c> makes an organization secret
    /// private when told nothing. A repository secret has no visibility. A
    /// secret GitHub answers 404 for does not exist yet and is created visible
    /// to all repositories, as the organization's role secrets are. Any other
    /// failure is returned, never guessed past, and so is a selected secret
    /// with no repositories, which no <c>--repos</c> list can express.
    /// Basis: https://docs.github.com/en/rest/actions/secrets#get-an-organization-secret and
    /// https://docs.github.com/en/rest/actions/secrets#list-selected-repositories-for-an-organization-secret.
    /// </summary>
    public static async Task<Result<SecretTarget, GhApiFailure>> ReadAccessAsync(
        this IGhApiReader reader,
        CiTokenSecret secret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Scope == CiSecretScope.Repository)
        {
            return Result<SecretTarget, GhApiFailure>.Success(new SecretTarget(secret, null, []));
        }

        string path = "orgs/" + secret.Owner + "/actions/secrets/" + secret.Name;
        Result<JsonNode, GhApiFailure> read = await reader.GetAsync(path, cancellationToken);
        if (read.IsFailure)
        {
            return read.Error.Status == 404 && !read.Error.RateLimited
                ? Result<SecretTarget, GhApiFailure>.Success(new SecretTarget(secret, OrgSecretVisibility.All, []))
                : Result<SecretTarget, GhApiFailure>.Failure(read.Error);
        }

        string? visibility = read.Value["visibility"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        switch (visibility)
        {
            case "all":
                return Result<SecretTarget, GhApiFailure>.Success(new SecretTarget(secret, OrgSecretVisibility.All, []));
            case "private":
                return Result<SecretTarget, GhApiFailure>.Success(new SecretTarget(secret, OrgSecretVisibility.Private, []));
            case "selected":
                break;
            default:
                return Result<SecretTarget, GhApiFailure>.Failure(
                    new GhApiFailure("GitHub gave " + secret + " no visibility this tool knows, so it was not set", RateLimited: false));
        }

        List<string> repositories = [];
        for (int page = 1; page <= MaxPages; page++)
        {
            Result<JsonNode, GhApiFailure> listed = await reader.GetAsync(
                path + "/repositories?per_page=" + PageSize.ToString(CultureInfo.InvariantCulture) + "&page=" + page.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
            if (listed.IsFailure)
            {
                return Result<SecretTarget, GhApiFailure>.Failure(listed.Error);
            }

            if (listed.Value["repositories"] is not JsonArray items)
            {
                return Result<SecretTarget, GhApiFailure>.Failure(
                    new GhApiFailure("GitHub's repository list for " + secret + " was not in the shape its documentation gives", RateLimited: false));
            }

            repositories.AddRange(items.Select(static item => item?["name"] is JsonValue name && name.TryGetValue(out string? repository) ? repository : null).OfType<string>());
            if (items.Count < PageSize)
            {
                return repositories.Count == 0
                    ? Result<SecretTarget, GhApiFailure>.Failure(new GhApiFailure(
                        secret + " is visible to selected repositories but lists none; set it by hand so its visibility is kept",
                        RateLimited: false))
                    : Result<SecretTarget, GhApiFailure>.Success(new SecretTarget(secret, OrgSecretVisibility.Selected, repositories));
            }
        }

        return Result<SecretTarget, GhApiFailure>.Failure(new GhApiFailure(
            "the repository list for " + secret + " ran past " + MaxPages.ToString(CultureInfo.InvariantCulture) + " pages, so it was not set",
            RateLimited: false));
    }

    /// <summary>
    /// The value of one Actions variable at <paramref name="path"/>
    /// (<c>orgs/{org}/actions/variables/{name}</c> or
    /// <c>repos/{owner}/{repo}/actions/variables/{name}</c>), or null when
    /// GitHub answers 404: the variable does not exist. Every name in the path
    /// must already be held to GitHub's rules.
    /// Basis: https://docs.github.com/en/rest/actions/variables#get-an-organization-variable.
    /// </summary>
    public static async Task<Result<string?, GhApiFailure>> GetVariableAsync(
        this IGhApiReader reader,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Result<JsonNode, GhApiFailure> read = await reader.GetAsync(path, cancellationToken);
        if (read.IsFailure)
        {
            return read.Error.Status == 404 && !read.Error.RateLimited
                ? Result<string?, GhApiFailure>.Success(null)
                : Result<string?, GhApiFailure>.Failure(read.Error);
        }

        return read.Value["value"] is JsonValue value && value.TryGetValue(out string? text)
            ? Result<string?, GhApiFailure>.Success(text)
            : Result<string?, GhApiFailure>.Failure(new GhApiFailure("GitHub's answer for " + path + " was not in the shape its documentation gives", RateLimited: false));
    }

    /// <summary>
    /// The current visibility of an organization variable: <c>all</c>,
    /// <c>private</c>, or <c>selected</c> with every selected repository's id,
    /// a page per call until a page comes back short. Any failure, a 404
    /// included, is returned as it came, so the caller writes nothing.
    /// Basis: https://docs.github.com/en/rest/actions/variables#get-an-organization-variable and
    /// https://docs.github.com/en/rest/actions/variables#list-selected-repositories-for-an-organization-variable.
    /// </summary>
    public static async Task<Result<VariableVisibility, GhApiFailure>> GetOrganizationVariableVisibilityAsync(
        this IGhApiReader reader,
        string organization,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string path = "orgs/" + organization + "/actions/variables/" + name;
        Result<JsonNode, GhApiFailure> read = await reader.GetAsync(path, cancellationToken);
        if (read.IsFailure)
        {
            return Result<VariableVisibility, GhApiFailure>.Failure(read.Error);
        }

        string? kind = read.Value["visibility"] is JsonValue visibility && visibility.TryGetValue(out string? text) ? text : null;
        if (kind is not ("all" or "private" or "selected"))
        {
            return Result<VariableVisibility, GhApiFailure>.Failure(new GhApiFailure("GitHub gave no visibility GitHub documents for " + path, RateLimited: false));
        }

        if (kind != "selected")
        {
            return Result<VariableVisibility, GhApiFailure>.Success(new VariableVisibility(kind, []));
        }

        List<long> ids = [];
        for (int page = 1; page <= MaxPages; page++)
        {
            string listing = path + "/repositories?per_page=" + PageSize.ToString(CultureInfo.InvariantCulture) + "&page=" + page.ToString(CultureInfo.InvariantCulture);
            Result<JsonNode, GhApiFailure> listed = await reader.GetAsync(listing, cancellationToken);
            if (listed.IsFailure)
            {
                return Result<VariableVisibility, GhApiFailure>.Failure(listed.Error);
            }

            if (listed.Value["repositories"] is not JsonArray repositories)
            {
                return Result<VariableVisibility, GhApiFailure>.Failure(new GhApiFailure("GitHub's repository list for " + path + " was not in the shape its documentation gives", RateLimited: false));
            }

            foreach (JsonNode? repository in repositories)
            {
                // Read from its JSON text, so an id held as any integer type reads the same.
                if (repository?["id"] is not JsonValue id
                    || id.GetValueKind() != JsonValueKind.Number
                    || !long.TryParse(id.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out long number))
                {
                    return Result<VariableVisibility, GhApiFailure>.Failure(new GhApiFailure("GitHub listed a repository with no id for " + path, RateLimited: false));
                }

                ids.Add(number);
            }

            if (repositories.Count < PageSize)
            {
                return Result<VariableVisibility, GhApiFailure>.Success(new VariableVisibility(kind, ids));
            }
        }

        return Result<VariableVisibility, GhApiFailure>.Failure(new GhApiFailure(
            "the repository list for " + path + " ran past " + MaxPages.ToString(CultureInfo.InvariantCulture) + " pages, so nothing was written",
            RateLimited: false));
    }

    /// <summary>
    /// The failed runs of <paramref name="repository"/> (<c>owner/name</c>,
    /// already held to GitHub's rules) created on or after the UTC date of
    /// <paramref name="since"/>, newest first, one page of
    /// <see cref="LimitStopScan.MaxRuns"/>: no scan reads more runs than that
    /// in total. <c>created</c> takes the search syntax's <c>&gt;=YYYY-MM-DD</c>,
    /// percent-encoded; the date is a day wider than the window, which
    /// <see cref="LimitStopScan.Newest"/> then trims. A run with no readable id
    /// or <c>created_at</c> is skipped.
    /// Basis: https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-repository and
    /// https://docs.github.com/en/search-github/getting-started-with-searching-on-github/understanding-the-search-syntax#query-for-dates.
    /// </summary>
    public static async Task<Result<IReadOnlyList<LaneRun>, GhApiFailure>> ListFailedRunsAsync(
        this IGhApiReader reader,
        string repository,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string path = "repos/" + repository + "/actions/runs?status=failure&created=%3E%3D"
            + since.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            // One page of 30, GitHub's documented default (max 100): the scan
            // reads at most 30 runs in all, so the newest 30 of each repository
            // hold the newest 30 overall. Basis:
            // https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-repository.
            + "&per_page=" + LimitStopScan.MaxRuns.ToString(CultureInfo.InvariantCulture);
        Result<JsonNode, GhApiFailure> read = await reader.GetAsync(path, cancellationToken);
        if (read.IsFailure)
        {
            return Result<IReadOnlyList<LaneRun>, GhApiFailure>.Failure(read.Error);
        }

        if (read.Value["workflow_runs"] is not JsonArray listed)
        {
            return Result<IReadOnlyList<LaneRun>, GhApiFailure>.Failure(new GhApiFailure("GitHub's run list for " + repository + " was not in the shape its documentation gives", RateLimited: false));
        }

        List<LaneRun> runs = [];
        foreach (JsonNode? item in listed)
        {
            if (Id(item?["id"]) is long id
                && item!["created_at"] is JsonValue created && created.TryGetValue(out string? stamp)
                && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset createdAt))
            {
                // The link the page shows is built here, never taken from the answer.
                runs.Add(new LaneRun(repository, id, createdAt, new Uri("https://github.com/" + repository + "/actions/runs/" + id.ToString(CultureInfo.InvariantCulture))));
            }
        }

        return Result<IReadOnlyList<LaneRun>, GhApiFailure>.Success(runs);
    }

    /// <summary>
    /// The check run of each failed job of <paramref name="run"/>, every
    /// attempt's, from one page of up to 100 jobs. The id comes from the job's
    /// <c>check_run_url</c>, since the docs do not promise it equals the job
    /// id, and only from one that points at a check run of the same
    /// repository: the URL is GitHub's answer, never passed to gh as given.
    /// Basis: https://docs.github.com/en/rest/actions/workflow-jobs#list-jobs-for-a-workflow-run.
    /// </summary>
    public static async Task<Result<IReadOnlyList<long>, GhApiFailure>> ListFailedJobCheckRunsAsync(
        this IGhApiReader reader,
        LaneRun run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(run);
        string path = "repos/" + run.Repository + "/actions/runs/" + run.Id.ToString(CultureInfo.InvariantCulture) + "/jobs?filter=all&per_page=" + PageSize.ToString(CultureInfo.InvariantCulture);
        Result<JsonNode, GhApiFailure> read = await reader.GetAsync(path, cancellationToken);
        if (read.IsFailure)
        {
            return Result<IReadOnlyList<long>, GhApiFailure>.Failure(read.Error);
        }

        if (read.Value["jobs"] is not JsonArray jobs)
        {
            return Result<IReadOnlyList<long>, GhApiFailure>.Failure(new GhApiFailure("GitHub's job list for run " + run.Id.ToString(CultureInfo.InvariantCulture) + " in " + run.Repository + " was not in the shape its documentation gives", RateLimited: false));
        }

        string expected = "/repos/" + run.Repository + "/check-runs/";
        List<long> checkRuns = [];
        foreach (JsonNode? job in jobs)
        {
            if (job?["conclusion"] is JsonValue conclusion && conclusion.TryGetValue(out string? outcome) && outcome == "failure"
                && job["check_run_url"] is JsonValue url && url.TryGetValue(out string? link)
                && Uri.TryCreate(link, UriKind.Absolute, out Uri? parsed)
                && parsed.Scheme == Uri.UriSchemeHttps
                && parsed.AbsolutePath.LastIndexOf(expected, StringComparison.OrdinalIgnoreCase) is int at and >= 0
                && parsed.AbsolutePath[(at + expected.Length)..] is { Length: > 0 } digits
                && digits.All(char.IsAsciiDigit)
                && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            {
                checkRuns.Add(id);
            }
        }

        return Result<IReadOnlyList<long>, GhApiFailure>.Success(checkRuns);
    }

    /// <summary>
    /// The messages of a check run's annotations, one page of up to 100. The
    /// answer is a bare array; an annotation with no message is skipped.
    /// Basis: https://docs.github.com/en/rest/checks/runs#list-check-run-annotations.
    /// </summary>
    public static async Task<Result<IReadOnlyList<string>, GhApiFailure>> ListAnnotationMessagesAsync(
        this IGhApiReader reader,
        string repository,
        long checkRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string path = "repos/" + repository + "/check-runs/" + checkRun.ToString(CultureInfo.InvariantCulture) + "/annotations?per_page=" + PageSize.ToString(CultureInfo.InvariantCulture);
        Result<JsonNode, GhApiFailure> read = await reader.GetAsync(path, cancellationToken);
        if (read.IsFailure)
        {
            return Result<IReadOnlyList<string>, GhApiFailure>.Failure(read.Error);
        }

        return read.Value is JsonArray annotations
            ? Result<IReadOnlyList<string>, GhApiFailure>.Success([.. annotations
                .Select(static annotation => annotation?["message"] is JsonValue message && message.TryGetValue(out string? text) ? text : null)
                .OfType<string>()])
            : Result<IReadOnlyList<string>, GhApiFailure>.Failure(new GhApiFailure("GitHub's annotations for check run " + checkRun.ToString(CultureInfo.InvariantCulture) + " in " + repository + " were not in the shape its documentation gives", RateLimited: false));
    }

    /// <summary>A JSON number read from its text, so an id held as any integer type reads the same; else null.</summary>
    private static long? Id(JsonNode? node) =>
        node is JsonValue id && id.GetValueKind() == JsonValueKind.Number
            && long.TryParse(id.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out long number)
                ? number
                : null;
}
