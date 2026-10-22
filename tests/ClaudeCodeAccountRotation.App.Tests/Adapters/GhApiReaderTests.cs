using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The <c>gh api --include</c> reader: a rate limit told from any other refusal
/// by the status and headers GitHub documents, and the body returned only on success.
/// </summary>
[Collection(StandInScripts.Name)]
public sealed class GhApiReaderTests : IDisposable
{
    private const string Path404 = "orgs/zz-no-such-org/actions/secrets?per_page=100&page=1";

    /// <summary>
    /// Recorded from <c>gh api -i --method GET</c> against an organization that
    /// does not exist (gh 2.x, 2026-10-20), headers trimmed: gh printed this on
    /// standard output and exited 1.
    /// </summary>
    private const string Recorded404 =
        "HTTP/2.0 404 Not Found\r\n"
        + "Content-Type: application/json; charset=utf-8\r\n"
        + "X-Ratelimit-Limit: 5000\r\n"
        + "X-Ratelimit-Remaining: 4794\r\n"
        + "\r\n"
        + "{\"message\":\"Not Found\",\"documentation_url\":\"https://docs.github.com/rest/actions/secrets#list-organization-secrets\",\"status\":\"404\"}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public GhApiReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public static bool OnUnix => !OperatingSystem.IsWindows();

    [Fact]
    public void TheReadIsAnExplicitGetWithNoFields() =>
        GhApiReader.Arguments("orgs/octo/actions/secrets?per_page=100&page=1")
            .ShouldBe(["api", "--include", "--method", "GET", "orgs/octo/actions/secrets?per_page=100&page=1"]);

    [Fact]
    public void ANotFoundIsAnOrdinaryFailureNamingItsStatus()
    {
        Result<JsonNode, GhApiFailure> read = GhApiReader.Read(Path404, new GhExit(1, Recorded404, "gh: Not Found (HTTP 404)"));

        read.Error.RateLimited.ShouldBeFalse();
        read.Error.Message.ShouldContain("HTTP 404");
        read.Error.Message.ShouldNotContain("gh: Not Found");
    }

    // Status and headers per https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api:
    // 403 or 429, with x-ratelimit-remaining 0 for the primary limit and retry-after for a secondary one.
    [Theory]
    [InlineData("HTTP/2.0 403 Forbidden\nX-Ratelimit-Remaining: 0\n\n{\"message\":\"x\"}")]
    [InlineData("HTTP/2.0 403 Forbidden\nRetry-After: 60\n\n{\"message\":\"x\"}")]
    [InlineData("HTTP/2.0 429 Too Many Requests\n\n{\"message\":\"x\"}")]
    [InlineData("HTTP/2.0 403 Forbidden\nX-Ratelimit-Remaining: 12\n\n{\"message\":\"You have exceeded a secondary rate limit.\"}")]
    public void ARateLimitIsToldApart(string output)
    {
        Result<JsonNode, GhApiFailure> read = GhApiReader.Read(Path404, new GhExit(1, output, string.Empty));

        read.Error.RateLimited.ShouldBeTrue();
        read.Error.Message.ShouldBe(GhApiReader.RateLimitMessage);
    }

    [Fact]
    public void AForbiddenThatIsNoRateLimitIsAnOrdinaryFailure() =>
        GhApiReader.Read(Path404, new GhExit(1, "HTTP/2.0 403 Forbidden\nX-Ratelimit-Remaining: 4000\n\n{\"message\":\"Must have admin rights\"}", string.Empty))
            .Error.RateLimited.ShouldBeFalse();

    [Fact]
    public void ASuccessReturnsTheBody()
    {
        Result<JsonNode, GhApiFailure> read = GhApiReader.Read(
            Path404,
            new GhExit(0, "HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n\r\n{\"total_count\":0,\"secrets\":[]}", string.Empty));

        read.Value["total_count"]!.GetValue<int>().ShouldBe(0);
    }

    // The created filter takes the search syntax's >= or a .. range of UTC times
    // (https://docs.github.com/en/search-github/getting-started-with-searching-on-github/understanding-the-search-syntax#query-for-dates),
    // and a page holds at most 100 runs (https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-repository).
    [Fact]
    public async Task AFailedRunsPageAsksForItsDateRangeAndPageAndCarriesEachRunsCheckSuite()
    {
        DateTimeOffset since = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        OneAnswer reader = new(new JsonObject
        {
            ["total_count"] = 1,
            ["workflow_runs"] = new JsonArray(new JsonObject
            {
                ["id"] = 101,
                ["created_at"] = "2026-09-05T09:00:00Z",
                ["check_suite_node_id"] = "CS_kwDOTCGFQM8AAAAZDrCXnA",
            }),
        });

        Result<RunPage, GhApiFailure> open = await reader.ListFailedRunsPageAsync("octo/lanes", since, null, 3, TestContext.Current.CancellationToken);
        await reader.ListFailedRunsPageAsync("octo/lanes", since, new DateTimeOffset(2026, 9, 2, 8, 30, 15, TimeSpan.FromHours(2)), 1, TestContext.Current.CancellationToken);

        reader.Paths.ShouldBe([
            "repos/octo/lanes/actions/runs?status=failure&created=%3E%3D2026-08-31T12:00:00Z&per_page=100&page=3",
            "repos/octo/lanes/actions/runs?status=failure&created=2026-08-31T12:00:00Z..2026-09-02T06:30:15Z&per_page=100&page=1",
        ]);
        open.Value.Full.ShouldBeFalse();
        LaneRun run = open.Value.Runs.ShouldHaveSingleItem();
        run.CheckSuiteNodeId.ShouldBe("CS_kwDOTCGFQM8AAAAZDrCXnA");
        run.Url.ShouldBe(new Uri("https://github.com/octo/lanes/actions/runs/101"));
    }

    [Fact]
    public async Task AHundredRunsIsAFullPage()
    {
        OneAnswer reader = new(new JsonObject
        {
            ["workflow_runs"] = new JsonArray([.. Enumerable.Range(1, 100).Select(static id => (JsonNode)new JsonObject { ["id"] = id, ["created_at"] = "2026-09-05T09:00:00Z" })]),
        });

        Result<RunPage, GhApiFailure> page = await reader.ListFailedRunsPageAsync("octo/lanes", DateTimeOffset.UnixEpoch, null, 1, TestContext.Current.CancellationToken);

        page.Value.Full.ShouldBeTrue();
        page.Value.Runs.Count.ShouldBe(100);
    }

    private sealed class OneAnswer(JsonNode answer) : IGhApiReader
    {
        public List<string> Paths { get; } = [];

        public Task<Result<JsonNode, GhApiFailure>> GetAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(Result<JsonNode, GhApiFailure>.Success(answer.DeepClone()));
        }
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "The stand-in gh is a shell script")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task AStandInGhIsRunWithTheReadsArgumentsAndItsAnswerRead()
    {
        string recorded = Path.Combine(_root, "recorded");
        string gh = Path.Combine(_root, "gh");
        await File.WriteAllTextAsync(
            gh,
            "#!/bin/sh\n"
            + "echo \"$*\" > '" + recorded + ".args'\n"
            + "printf 'HTTP/2.0 200 OK\\r\\nContent-Type: application/json\\r\\n\\r\\n{\"total_count\":0,\"secrets\":[]}'\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Result<JsonNode, GhApiFailure> read = await new GhApiReader(gh, TimeSpan.FromSeconds(30))
            .GetAsync("orgs/octo/actions/secrets?per_page=100&page=1", TestContext.Current.CancellationToken);

        read.Value["secrets"]!.AsArray().Count.ShouldBe(0);
        (await File.ReadAllTextAsync(recorded + ".args", TestContext.Current.CancellationToken))
            .ShouldBe("api --include --method GET orgs/octo/actions/secrets?per_page=100&page=1\n");
    }
}
