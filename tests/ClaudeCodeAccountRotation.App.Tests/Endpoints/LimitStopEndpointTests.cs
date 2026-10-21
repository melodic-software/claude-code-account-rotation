using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// <c>POST /api/roles/limit-stops</c>: nothing is read without lane
/// repositories, every runs request asks for the last 7 days of failures, at
/// most 30 runs are read, and the first rate-limit answer ends the scan.
/// The app's clock reads 2026-09-07 12:00 UTC, so the window opens on 2026-08-31.
/// </summary>
public sealed class LimitStopEndpointTests
{
    private const string A = "a@example.com";

    private static readonly Uri _scan = new("/api/roles/limit-stops", UriKind.Relative);
    private static readonly CiRole _automations = new("AUTOMATIONS", CiSecretScope.Organization, "octo");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(AppFactory Factory, SpyReader Reader)> StartAsync(params string[] laneRepositories)
    {
        AppFactory factory = new();
        if (laneRepositories.Length > 0)
        {
            JsonObject configuration = JsonNode.Parse(await File.ReadAllTextAsync(factory.ConfigPath, Token))!.AsObject();
            configuration["laneRepositories"] = new JsonArray([.. laneRepositories.Select(static repository => (JsonNode)repository)]);
            await File.WriteAllTextAsync(factory.ConfigPath, configuration.ToJsonString(), Token);
        }

        SpyReader reader = new();
        factory.Overrides = services => services.Replace(ServiceDescriptor.Singleton<IGhApiReader>(reader));
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", Token);
        await factory.WriteStateFileAsync("live@example.com", Token);
        factory.Cli.Email = "live@example.com";
        return (factory, reader);
    }

    /// <summary>A backed both halves of AUTOMATIONS from 2026-09-01, by the lane history.</summary>
    private static Task HistoryAsync(AppFactory factory)
    {
        DateTimeOffset at = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        return factory.Services.GetRequiredService<RosterFile>().History.AppendAsync(
            [
                new LaneHistoryEntry(at, _automations, LaneHistoryEntry.TokenHalf, A, null, LaneVia.Assign),
                new LaneHistoryEntry(at, _automations, LaneHistoryEntry.KeyHalf, A, null, LaneVia.Assign),
            ],
            Token);
    }

    private static string RunsPath(string repository) =>
        "repos/" + repository + "/actions/runs?status=failure&created=%3E%3D2026-08-31&per_page=30";

    /// <summary>"List workflow runs for a repository", documented shape: https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-repository.</summary>
    private static Result<JsonNode, GhApiFailure> Runs(string repository, params (long Id, string CreatedAt)[] runs) => Result<JsonNode, GhApiFailure>.Success(new JsonObject
    {
        ["total_count"] = runs.Length,
        ["workflow_runs"] = new JsonArray([.. runs.Select(run => (JsonNode)new JsonObject
        {
            ["id"] = run.Id,
            ["name"] = "intake",
            ["status"] = "completed",
            ["conclusion"] = "failure",
            ["created_at"] = run.CreatedAt,
            ["html_url"] = "https://github.com/" + repository + "/actions/runs/" + run.Id,
            ["run_attempt"] = 1,
        })]),
    });

    /// <summary>"List jobs for a workflow run", documented shape: https://docs.github.com/en/rest/actions/workflow-jobs#list-jobs-for-a-workflow-run.</summary>
    private static Result<JsonNode, GhApiFailure> Jobs(params (string Conclusion, string CheckRunUrl)[] jobs) => Result<JsonNode, GhApiFailure>.Success(new JsonObject
    {
        ["total_count"] = jobs.Length,
        ["jobs"] = new JsonArray([.. jobs.Select(static (job, index) => (JsonNode)new JsonObject
        {
            ["id"] = 9000 + index,
            ["status"] = "completed",
            ["conclusion"] = job.Conclusion,
            ["check_run_url"] = job.CheckRunUrl,
        })]),
    });

    /// <summary>"List check run annotations", documented shape (a bare array): https://docs.github.com/en/rest/checks/runs#list-check-run-annotations.</summary>
    private static Result<JsonNode, GhApiFailure> Annotations(params string[] messages) => Result<JsonNode, GhApiFailure>.Success(new JsonArray([.. messages.Select(static message => (JsonNode)new JsonObject
    {
        ["path"] = ".github",
        ["annotation_level"] = "failure",
        ["title"] = "",
        ["message"] = message,
    })]));

    private static Result<JsonNode, GhApiFailure> RateLimited() =>
        Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(GhApiReader.RateLimitMessage, RateLimited: true, 403));

    [Fact]
    public async Task WithNoLaneRepositoriesNothingIsReadAndTheResultIsNotChecked()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(_scan, new { from = A }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!;
        body["outcome"]!.GetValue<string>().ShouldBe("notChecked");
        body["hits"]!.AsArray().ShouldBeEmpty();
        reader.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARateLimitAnnotationOnAHalfABackedIsAHitAndOnlyFailedJobsOfThisRepositoryAreRead()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await HistoryAsync(factory);
        reader.Answers.Enqueue(Runs("octo/lanes", (101, "2026-09-05T09:00:00Z")));
        reader.Answers.Enqueue(Jobs(
            ("failure", "https://api.github.com/repos/octo/lanes/check-runs/555"),
            ("success", "https://api.github.com/repos/octo/lanes/check-runs/556"),
            ("failure", "https://api.github.com/repos/octo/elsewhere/check-runs/557")));
        reader.Answers.Enqueue(Annotations(
            "Process completed with exit code 1.",
            "lane=intake class=rate-limit credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN resets=2026-09-05T14:00:00Z"));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_scan, new { from = A }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        reader.Paths.ShouldBe([
            RunsPath("octo/lanes"),
            "repos/octo/lanes/actions/runs/101/jobs?filter=all&per_page=100",
            "repos/octo/lanes/check-runs/555/annotations?per_page=100",
        ]);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!;
        body["outcome"]!.GetValue<string>().ShouldBe("checked");
        body["runsRead"]!.GetValue<int>().ShouldBe(1);
        JsonObject hit = body["hits"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        hit["label"]!.GetValue<string>().ShouldBe("subscription limit");
        hit["credential"]!.GetValue<string>().ShouldBe("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN");
        hit["repository"]!.GetValue<string>().ShouldBe("octo/lanes");
        hit["url"]!.GetValue<string>().ShouldBe("https://github.com/octo/lanes/actions/runs/101");
    }

    [Fact]
    public async Task AtMostThirtyRunsAreReadAcrossEveryRepositoryAndEachRunsRequestAsksForTheLastSevenDays()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync("octo/lanes", "octo/other");
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        reader.Answers.Enqueue(Runs("octo/lanes", [.. Enumerable.Range(1, 20).Select(static day => ((long)day, "2026-09-0" + (1 + (day % 6)) + "T10:00:00Z"))]));
        reader.Answers.Enqueue(Runs("octo/other", [.. Enumerable.Range(21, 20).Select(static day => ((long)day, "2026-09-0" + (1 + (day % 6)) + "T11:00:00Z"))]));
        for (int run = 0; run < 40; run++)
        {
            reader.Answers.Enqueue(Jobs());
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync(_scan, new { from = A }, Token);

        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!;
        body["outcome"]!.GetValue<string>().ShouldBe("checked");
        body["runsRead"]!.GetValue<int>().ShouldBe(30);
        reader.Paths.Where(static path => path.Contains("/actions/runs?", StringComparison.Ordinal)).ShouldBe([RunsPath("octo/lanes"), RunsPath("octo/other")]);
        reader.Paths.Count(static path => path.EndsWith("/jobs?filter=all&per_page=100", StringComparison.Ordinal)).ShouldBe(30);
    }

    [Fact]
    public async Task TheFirstRateLimitAnswerEndsTheScanAsPartialWithNoRetry()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync("octo/lanes", "octo/other");
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await HistoryAsync(factory);
        reader.Answers.Enqueue(Runs("octo/lanes", (101, "2026-09-05T09:00:00Z"), (102, "2026-09-04T09:00:00Z")));
        reader.Answers.Enqueue(Runs("octo/other"));
        reader.Answers.Enqueue(RateLimited());
        reader.Answers.Enqueue(Jobs(("failure", "https://api.github.com/repos/octo/lanes/check-runs/555")));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_scan, new { from = A }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        reader.Paths.ShouldBe([
            RunsPath("octo/lanes"),
            RunsPath("octo/other"),
            "repos/octo/lanes/actions/runs/101/jobs?filter=all&per_page=100",
        ]);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!;
        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        body["runsRead"]!.GetValue<int>().ShouldBe(0);
        body["problems"]!.AsArray().Select(static problem => problem!.GetValue<string>()).ShouldBe([GhApiReader.RateLimitMessage]);
    }

    [Fact]
    public async Task AFromThatIsNoAddressIsRefusedBeforeAnyRead()
    {
        (AppFactory factory, SpyReader reader) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(_scan, new { from = "not an address" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        reader.Paths.ShouldBeEmpty();
    }

    /// <summary>Records every path it is asked for and gives the scripted answers in order.</summary>
    private sealed class SpyReader : IGhApiReader
    {
        public List<string> Paths { get; } = [];

        public Queue<Result<JsonNode, GhApiFailure>> Answers { get; } = new();

        public Task<Result<JsonNode, GhApiFailure>> GetAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(Answers.TryDequeue(out Result<JsonNode, GhApiFailure> answer)
                ? answer
                : Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("no answer scripted", RateLimited: false)));
        }
    }
}
