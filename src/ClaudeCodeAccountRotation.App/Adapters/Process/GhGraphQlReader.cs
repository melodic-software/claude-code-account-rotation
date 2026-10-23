using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// Reads the GitHub GraphQL API through <c>gh api graphql</c>, so the request
/// carries gh's own User-Agent and sign-in. It sends only the documents in
/// <see cref="GhReadQueries.All"/>; any other is refused before gh runs. Not a
/// writer, so it never goes through the write gate.
/// </summary>
internal interface IGhGraphQlReader
{
    /// <summary>The <c>data</c> object of the answer to <paramref name="query"/>, or why there is none.</summary>
    Task<Result<JsonNode, GhApiFailure>> QueryAsync(GhReadQuery query, IReadOnlyDictionary<string, JsonNode?> variables, CancellationToken cancellationToken);
}

/// <summary>A fixed read-only GraphQL document and the name failures call it by.</summary>
internal sealed record GhReadQuery(string Name, string Document);

/// <summary>The GraphQL documents the tool sends: fixed text, never built from input.</summary>
internal static class GhReadQueries
{
    /// <summary>
    /// The check suites one batch reads at first: half a page of the run
    /// listing. GitHub cut off a batch of 75 or 100 claude-code-plugins suites
    /// for its resource limits and passed 50, with no documented threshold
    /// (#318); a cut-off batch is read again in halves.
    /// Basis: https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api#other-resource-limits.
    /// </summary>
    public const int SuitesPerBatch = LimitStopScan.RunsPerPage / 2;

    /// <summary>The check runs read of one suite; more is a cut-off read.</summary>
    public const int CheckRunsPerSuite = 50;

    /// <summary>The annotations read of one check run; more gets a REST follow-up.</summary>
    public const int AnnotationsPerCheckRun = 50;

    /// <summary>
    /// The check runs of up to <see cref="SuitesPerBatch"/> check suites and
    /// the messages of their annotations, with the points left.
    /// <c>checkType: ALL</c> reads the check runs of every attempt of a re-run
    /// run, which share one suite; the default gives only the latest check run
    /// of each name. Basis: one live read of a re-run run's suite, 2026-10-22.
    /// The page sizes keep a batch's points and nodes well inside GitHub's
    /// limits. Basis: https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api.
    /// </summary>
    public static readonly GhReadQuery CheckSuiteAnnotations = new(
        "check suite annotations",
        "query($ids: [ID!]!) { rateLimit { remaining } "
        + "nodes(ids: $ids) { ... on CheckSuite { checkRuns(first: " + CheckRunsPerSuite.ToString(CultureInfo.InvariantCulture)
        + ", filterBy: {checkType: ALL}) { pageInfo { hasNextPage } "
        + "nodes { databaseId annotations(first: " + AnnotationsPerCheckRun.ToString(CultureInfo.InvariantCulture)
        + ") { pageInfo { hasNextPage } nodes { message } } } } } } }");

    /// <summary>Every document <see cref="GhGraphQlReader"/> sends.</summary>
    public static readonly IReadOnlyList<GhReadQuery> All = [CheckSuiteAnnotations];
}

/// <summary>One check run as the batch read it: <paramref name="MoreAnnotations"/> when its annotations ran past the page.</summary>
internal sealed record CheckRunRead(long Id, bool MoreAnnotations, IReadOnlyList<string> Messages);

/// <summary>One check suite as the batch read it: <paramref name="MoreCheckRuns"/> when its check runs ran past the page.</summary>
internal sealed record SuiteRead(bool MoreCheckRuns, IReadOnlyList<CheckRunRead> CheckRuns);

/// <summary>
/// One batch's answer: the GraphQL points left and, in the order of the ids
/// asked for, each suite, null for one deleted since it was listed.
/// </summary>
internal sealed record SuiteBatch(int Remaining, IReadOnlyList<SuiteRead?> Suites);

/// <summary>The reads the limit-stop check makes through <see cref="IGhGraphQlReader"/>, and the parsing of their answers.</summary>
internal static class GhGraphQlReads
{
    /// <summary>
    /// The suites of <paramref name="ids"/> (at most <see cref="GhReadQueries.SuitesPerBatch"/>)
    /// in one call. An answer missing a field the query asks for is a failure,
    /// never read as whole.
    /// </summary>
    public static async Task<Result<SuiteBatch, GhApiFailure>> ReadCheckSuitesAsync(this IGhGraphQlReader reader, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ids.Count, GhReadQueries.SuitesPerBatch);
        Result<JsonNode, GhApiFailure> read = await reader.QueryAsync(
            GhReadQueries.CheckSuiteAnnotations,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["ids"] = new JsonArray([.. ids.Select(static id => (JsonNode)id)]) },
            cancellationToken);
        return read.IsFailure ? Result<SuiteBatch, GhApiFailure>.Failure(read.Error) : ParseCheckSuites(read.Value, ids.Count);
    }

    /// <summary>The <c>data</c> of a <see cref="GhReadQueries.CheckSuiteAnnotations"/> answer for <paramref name="count"/> ids.</summary>
    public static Result<SuiteBatch, GhApiFailure> ParseCheckSuites(JsonNode data, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data["rateLimit"]?["remaining"] is not JsonValue left || !left.TryGetValue(out int remaining)
            || data["nodes"] is not JsonArray nodes || nodes.Count != count)
        {
            return Unexpected();
        }

        List<SuiteRead?> suites = [];
        foreach (JsonNode? node in nodes)
        {
            if (node is null)
            {
                suites.Add(null);
                continue;
            }

            if (Page(node["checkRuns"]) is not (bool moreCheckRuns, JsonArray checkRuns))
            {
                return Unexpected();
            }

            List<CheckRunRead> read = [];
            foreach (JsonNode? checkRun in checkRuns)
            {
                if (checkRun?["databaseId"] is not JsonValue number || !number.TryGetValue(out long id)
                    || Page(checkRun["annotations"]) is not (bool moreAnnotations, JsonArray annotations))
                {
                    return Unexpected();
                }

                read.Add(new CheckRunRead(id, moreAnnotations, [.. annotations
                    .Select(static annotation => annotation?["message"] is JsonValue message && message.TryGetValue(out string? text) ? text : null)
                    .OfType<string>()]));
            }

            suites.Add(new SuiteRead(moreCheckRuns, read));
        }

        return Result<SuiteBatch, GhApiFailure>.Success(new SuiteBatch(remaining, suites));

        static Result<SuiteBatch, GhApiFailure> Unexpected() => Result<SuiteBatch, GhApiFailure>.Failure(new GhApiFailure(
            "GitHub's answer to the " + GhReadQueries.CheckSuiteAnnotations.Name + " query was not in the shape the query asks for",
            RateLimited: false));
    }

    /// <summary>A connection's <c>pageInfo.hasNextPage</c> and <c>nodes</c>, or null when either is missing.</summary>
    private static (bool More, JsonArray Nodes)? Page(JsonNode? connection) =>
        connection?["pageInfo"]?["hasNextPage"] is JsonValue more && more.TryGetValue(out bool next) && connection["nodes"] is JsonArray nodes
            ? (next, nodes)
            : null;
}

/// <summary>The reader when no <c>gh</c> could be found: every read fails with the reason.</summary>
internal sealed class UnavailableGhGraphQlReader(string reason) : IGhGraphQlReader
{
    public Task<Result<JsonNode, GhApiFailure>> QueryAsync(GhReadQuery query, IReadOnlyDictionary<string, JsonNode?> variables, CancellationToken cancellationToken) =>
        Task.FromResult(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(reason, RateLimited: false)));
}

/// <summary>
/// <c>gh api graphql --method POST --include --input -</c>, the body
/// <c>{"query", "variables"}</c> on standard input. GraphQL is a POST with a
/// body, which the GET-only <see cref="GhApiReader"/> never sends. Nothing gh
/// prints on its error stream is relayed.
/// </summary>
internal sealed class GhGraphQlReader(string executable, TimeSpan timeout) : IGhGraphQlReader
{
    public static IReadOnlyList<string> Arguments { get; } = ["api", "graphql", "--method", "POST", "--include", "--input", "-"];

    public async Task<Result<JsonNode, GhApiFailure>> QueryAsync(GhReadQuery query, IReadOnlyDictionary<string, JsonNode?> variables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(variables);
        if (!GhReadQueries.All.Contains(query))
        {
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("the " + query.Name + " query was refused: this reader sends only its own fixed queries", RateLimited: false));
        }

        JsonObject body = new()
        {
            ["query"] = query.Document,
            ["variables"] = new JsonObject([.. variables.Select(static pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))]),
        };
        Result<GhExit, string> run = await GhProcess.RunAsync(executable, Arguments, body.ToJsonString(), timeout, cancellationToken);
        return run.IsFailure
            ? Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(run.Error, RateLimited: false))
            : Read(query.Name, run.Value);
    }

    /// <summary>
    /// The answer in <c>--include</c> output. A 403 or 429 is told apart as
    /// <see cref="GhApiReader.Read"/> does. A 200 may still carry an
    /// <c>errors</c> array, and then gh exits 1 with the headers and the whole
    /// body on standard output (one live read, 2026-10-22), so the exit code
    /// alone decides nothing. GitHub answers an exceeded GraphQL limit with a
    /// 200, an error and <c>x-ratelimit-remaining: 0</c>; an error typed
    /// <c>RATE_LIMITED</c> or naming a rate limit reads the same. Any other
    /// error is a failure, with one exception: when every error is <c>NOT_FOUND</c>
    /// on a top-level <c>nodes</c> entry that came back null (a run deleted
    /// since it was listed), the data is returned and the caller sees the null.
    /// Basis: https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api.
    /// </summary>
    public static Result<JsonNode, GhApiFailure> Read(string name, GhExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        (int status, string[] head, string text) = GhApiReader.Split(exit.StandardOutput);
        if (status is 403 or 429 && GhApiReader.RateLimited(status, head, text))
        {
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(GhApiReader.RateLimitMessage, RateLimited: true, status));
        }

        if (status is < 200 or >= 300)
        {
            string answered = status == 0 ? "gh api graphql exited with code " + exit.ExitCode : "GitHub answered HTTP " + status;
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(answered + " for the " + name + " query; check `gh auth status`", RateLimited: false, status == 0 ? null : status));
        }

        JsonObject? answer;
        try
        {
            answer = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            answer = null;
        }

        if (answer is null)
        {
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("gh api graphql returned something other than a JSON object for the " + name + " query", RateLimited: false));
        }

        var data = answer["data"] as JsonObject;
        List<JsonObject> errors = answer["errors"] is JsonArray listed ? [.. listed.OfType<JsonObject>()] : [];
        if (errors.Count > 0 && (GhApiReader.RateLimited(status, head, text) || errors.Exists(static error => Type(error) == "RATE_LIMITED" || Message(error).Contains("rate limit", StringComparison.OrdinalIgnoreCase))))
        {
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(GhApiReader.RateLimitMessage, RateLimited: true, status));
        }

        if (errors.Count > 0 && (data is null || !errors.TrueForAll(error => DeletedNode(error, data))))
        {
            string types = string.Join(", ", errors.Select(static error => Type(error) ?? "untyped").Distinct(StringComparer.Ordinal));
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(
                "GitHub's GraphQL API answered the " + name + " query with errors (" + types + ")",
                RateLimited: false,
                status,
                ResourceLimited: errors.Exists(static error => Type(error) == "RESOURCE_LIMITS_EXCEEDED")));
        }

        if (data is null)
        {
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub's GraphQL answer to the " + name + " query had no data", RateLimited: false, status));
        }

        return exit.ExitCode == 0 || errors.Count > 0
            ? Result<JsonNode, GhApiFailure>.Success(data)
            : Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("gh api graphql exited with code " + exit.ExitCode + " for the " + name + " query", RateLimited: false, status));
    }

    private static string Message(JsonObject error) =>
        error["message"] is JsonValue message && message.TryGetValue(out string? text) ? text ?? string.Empty : string.Empty;

    private static string? Type(JsonObject error) =>
        error["type"] is JsonValue type && type.TryGetValue(out string? text) ? text : null;

    /// <summary>A <c>NOT_FOUND</c> error on <c>["nodes", i]</c> whose entry in the data is null.</summary>
    private static bool DeletedNode(JsonObject error, JsonObject data) =>
        Type(error) == "NOT_FOUND"
        && error["path"] is JsonArray { Count: 2 } path
        && path[0] is JsonValue first && first.TryGetValue(out string? field) && field == "nodes"
        && path[1] is JsonValue second && second.TryGetValue(out int index)
        && data["nodes"] is JsonArray nodes && index >= 0 && index < nodes.Count && nodes[index] is null;
}
