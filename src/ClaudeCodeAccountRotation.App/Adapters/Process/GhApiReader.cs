using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>Why a <c>gh api</c> read gave no answer. A rate limit ends whatever asked; nothing retries it.</summary>
internal sealed record GhApiFailure(string Message, bool RateLimited);

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
                RateLimited: false));
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

    private static bool RateLimited(int status, string[] head, string body)
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
}
