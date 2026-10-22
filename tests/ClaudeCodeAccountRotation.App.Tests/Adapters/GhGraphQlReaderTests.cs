using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The <c>gh api graphql</c> reader: a POST whose body travels on standard
/// input, an <c>errors</c> array read as a failure whatever gh's exit code,
/// a rate limit told apart, and a deleted node passed through for the caller.
/// </summary>
[Collection(StandInScripts.Name)]
public sealed class GhGraphQlReaderTests : IDisposable
{
    private const string Head =
        "HTTP/2.0 200 OK\r\n"
        + "Content-Type: application/json; charset=utf-8\r\n"
        + "X-Github-Media-Type: github.v4; format=json\r\n"
        + "X-Ratelimit-Limit: 5000\r\n"
        + "X-Ratelimit-Remaining: 4881\r\n"
        + "X-Ratelimit-Resource: graphql\r\n"
        + "\r\n";

    /// <summary>
    /// Recorded from <c>gh api graphql --method POST --include --input -</c>
    /// with <c>nodes(ids:)</c> holding one real check suite and one bogus id
    /// (gh 2.x, 2026-10-22), headers and check runs trimmed: gh printed this
    /// on standard output, the line in <see cref="RecordedStandardError"/> on
    /// its error stream, and exited 1.
    /// </summary>
    private const string RecordedNotFound = Head
        + "{\"data\":{\"rateLimit\":{\"remaining\":4881,\"cost\":1,\"resetAt\":\"2026-10-22T04:37:55Z\"},"
        + "\"nodes\":[{\"id\":\"CS_kwDOTCGFQM8AAAAZDrCXnA\",\"checkRuns\":{\"pageInfo\":{\"hasNextPage\":false},\"nodes\":[]}},null]},"
        + "\"errors\":[{\"type\":\"NOT_FOUND\",\"path\":[\"nodes\",1],\"locations\":[{\"line\":1,\"column\":58}],"
        + "\"message\":\"Could not resolve to a node with the global id of 'CS_kwDOTCGFQM8AAAAZbogusbogus'\"}]}";

    private const string RecordedStandardError = "gh: Could not resolve to a node with the global id of 'CS_kwDOTCGFQM8AAAAZbogusbogus'\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public GhGraphQlReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public static bool OnUnix => !OperatingSystem.IsWindows();

    private static Result<JsonNode, GhApiFailure> Read(int exitCode, string output) =>
        GhGraphQlReader.Read("test", new GhExit(exitCode, output, RecordedStandardError));

    [Fact]
    public void TheReadIsAPostWithItsBodyOnStandardInput() =>
        GhGraphQlReader.Arguments.ShouldBe(["api", "graphql", "--method", "POST", "--include", "--input", "-"]);

    [Fact]
    public void TheRecordedDeletedNodeIsPassedThroughWithItsNullAndGhsExitCodeDecidesNothing()
    {
        Result<JsonNode, GhApiFailure> read = Read(1, RecordedNotFound);

        read.IsSuccess.ShouldBeTrue();
        JsonArray nodes = read.Value["nodes"]!.AsArray();
        nodes.Count.ShouldBe(2);
        nodes[0]!["id"]!.GetValue<string>().ShouldBe("CS_kwDOTCGFQM8AAAAZDrCXnA");
        nodes[1].ShouldBeNull();
        read.Value["rateLimit"]!["remaining"]!.GetValue<int>().ShouldBe(4881);
    }

    [Theory]
    [InlineData("{\"data\":{\"nodes\":[null]},\"errors\":[{\"type\":\"NOT_FOUND\",\"path\":[\"nodes\",0,\"checkRuns\"],\"message\":\"x\"}]}")]
    [InlineData("{\"data\":{\"nodes\":[{\"id\":\"CS_1\"}]},\"errors\":[{\"type\":\"NOT_FOUND\",\"path\":[\"nodes\",0],\"message\":\"x\"}]}")]
    [InlineData("{\"data\":{\"nodes\":[null]},\"errors\":[{\"type\":\"FORBIDDEN\",\"path\":[\"nodes\",0],\"message\":\"x\"}]}")]
    [InlineData("{\"data\":null,\"errors\":[{\"message\":\"Something went wrong while executing your query.\"}]}")]
    [InlineData("{\"errors\":[{\"type\":\"NOT_FOUND\",\"path\":[\"nodes\",0],\"message\":\"x\"}]}")]
    public void AnyOtherErrorIsAFailureThatIsNoRateLimitAndRelaysNothingGhPrinted(string body)
    {
        Result<JsonNode, GhApiFailure> read = Read(1, Head + body);

        read.IsFailure.ShouldBeTrue();
        read.Error.RateLimited.ShouldBeFalse();
        read.Error.Message.ShouldNotContain("gh:");
        read.Error.Message.ShouldNotContain("Something went wrong");
    }

    // GitHub answers an exceeded GraphQL primary limit with a 200, an error and
    // x-ratelimit-remaining 0: https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api.
    // The RATE_LIMITED type is the plan's contract for the error's type.
    [Theory]
    [InlineData("HTTP/2.0 200 OK\nX-Ratelimit-Remaining: 12\n\n{\"data\":null,\"errors\":[{\"type\":\"RATE_LIMITED\",\"message\":\"API rate limit exceeded\"}]}")]
    [InlineData("HTTP/2.0 200 OK\nX-Ratelimit-Remaining: 0\n\n{\"data\":null,\"errors\":[{\"message\":\"x\"}]}")]
    [InlineData("HTTP/2.0 403 Forbidden\nX-Ratelimit-Remaining: 0\n\n{\"message\":\"x\"}")]
    [InlineData("HTTP/2.0 403 Forbidden\nRetry-After: 60\n\n{\"message\":\"x\"}")]
    public void ARateLimitIsToldApart(string output)
    {
        Result<JsonNode, GhApiFailure> read = Read(1, output);

        read.Error.RateLimited.ShouldBeTrue();
        read.Error.Message.ShouldBe(GhApiReader.RateLimitMessage);
    }

    [Fact]
    public void ASuccessReturnsTheDataObject() =>
        Read(0, Head + "{\"data\":{\"rateLimit\":{\"remaining\":4000}}}").Value["rateLimit"]!["remaining"]!.GetValue<int>().ShouldBe(4000);

    [Theory]
    [InlineData(0, "HTTP/2.0 502 Bad Gateway\n\n{\"message\":\"x\"}")]
    [InlineData(1, "")]
    [InlineData(0, "HTTP/2.0 200 OK\n\nnot json")]
    [InlineData(1, "HTTP/2.0 200 OK\n\n{\"data\":{}}")]
    public void NoUsableAnswerIsAFailure(int exitCode, string output) =>
        Read(exitCode, output).IsFailure.ShouldBeTrue();

    [Fact]
    public async Task AMutationIsRefusedBeforeGhRuns()
    {
        string gh = Path.Combine(_root, "never-run");
        GhReadQuery mutation = new("write", "mutation { addStar(input: {starrableId: \"x\"}) { clientMutationId } }");

        Result<JsonNode, GhApiFailure> read = await new GhGraphQlReader(gh, TimeSpan.FromSeconds(30))
            .QueryAsync(mutation, new Dictionary<string, JsonNode?>(), TestContext.Current.CancellationToken);

        read.Error.Message.ShouldContain("refused");
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task AStandInGhGetsTheArgumentsAndTheBodyOnStandardInput()
    {
        string recorded = Path.Combine(_root, "recorded");
        string answer = Path.Combine(_root, "answer");
        await File.WriteAllTextAsync(answer, RecordedNotFound, TestContext.Current.CancellationToken);
        string gh = Path.Combine(_root, "gh");
        await File.WriteAllTextAsync(
            gh,
            "#!/bin/sh\n"
            + "echo \"$*\" > '" + recorded + ".args'\n"
            + "cat > '" + recorded + ".stdin'\n"
            + "cat '" + answer + "'\n"
            + "printf '%s' \"" + RecordedStandardError.TrimEnd('\n') + "\" >&2\n"
            + "exit 1\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Result<JsonNode, GhApiFailure> read = await new GhGraphQlReader(gh, TimeSpan.FromSeconds(30)).QueryAsync(
            GhReadQueries.CheckSuiteAnnotations,
            new Dictionary<string, JsonNode?> { ["ids"] = new JsonArray("CS_kwDOTCGFQM8AAAAZDrCXnA", "CS_kwDOTCGFQM8AAAAZbogusbogus") },
            TestContext.Current.CancellationToken);

        read.Value["nodes"]!.AsArray()[1].ShouldBeNull();
        (await File.ReadAllTextAsync(recorded + ".args", TestContext.Current.CancellationToken))
            .ShouldBe("api graphql --method POST --include --input -\n");
        JsonObject body = JsonNode.Parse(await File.ReadAllTextAsync(recorded + ".stdin", TestContext.Current.CancellationToken))!.AsObject();
        body["query"]!.GetValue<string>().ShouldBe(GhReadQueries.CheckSuiteAnnotations.Document);
        body["variables"]!["ids"]!.AsArray().Select(static id => id!.GetValue<string>()).ShouldBe(["CS_kwDOTCGFQM8AAAAZDrCXnA", "CS_kwDOTCGFQM8AAAAZbogusbogus"]);
    }

    // cant-fail-ok: pins the external contract the re-run live read settled (attempts
    // share one check suite; only checkType ALL lists the earlier attempts' check runs).
    [Fact]
    public void TheBatchQueryReadsEveryAttemptsCheckRunsAndNoMutation()
    {
        GhReadQueries.CheckSuiteAnnotations.Document.ShouldContain("filterBy: {checkType: ALL}");
        GhReadQueries.CheckSuiteAnnotations.Document.ShouldContain("rateLimit { remaining cost resetAt }");
        GhReadQueries.CheckSuiteAnnotations.Document.ShouldNotContain("mutation", Case.Insensitive);
    }
}
