using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// Reads the GitHub GraphQL API through <c>gh api graphql</c>, so the request
/// carries gh's own User-Agent and sign-in. Queries only: a document holding
/// <c>mutation</c> is refused before gh runs. Not a writer, so it never goes
/// through the write gate.
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
    /// The check runs of up to 100 check suites and the messages of their
    /// annotations, with the points left. <c>checkType: ALL</c> reads the
    /// check runs of every attempt of a re-run run, which share one suite; the
    /// default gives only the latest check run of each name. Basis: one live
    /// read of a re-run run's suite, 2026-10-22: 11 check runs by default, 22
    /// with ALL. 100 ids, 50 check runs and 50 annotations each, is 5,101
    /// connection requests, about 51 points, and 255,100 nodes, under the
    /// 500,000 a call may ask for. Basis: https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api.
    /// </summary>
    public static readonly GhReadQuery CheckSuiteAnnotations = new(
        "check suite annotations",
        "query($ids: [ID!]!) { rateLimit { remaining cost resetAt } "
        + "nodes(ids: $ids) { ... on CheckSuite { id checkRuns(first: 50, filterBy: {checkType: ALL}) { pageInfo { hasNextPage } "
        + "nodes { databaseId name conclusion annotations(first: 50) { pageInfo { hasNextPage } nodes { message } } } } } } }");
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
        if (query.Document.Contains("mutation", StringComparison.OrdinalIgnoreCase))
        {
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("the " + query.Name + " query was refused: this reader sends no mutation", RateLimited: false));
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
            return Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub's GraphQL API answered the " + name + " query with errors (" + types + ")", RateLimited: false, status));
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
