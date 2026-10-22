using System.Globalization;
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
/// repositories; per repository a page of 100 failed runs of the last 7 days,
/// then one GraphQL batch of their check suites, every call from a budget of
/// 30; the budget, the point floor, a rate limit or a cut-off read reads
/// partial and says how far back each repository was read. The app's clock
/// reads 2026-09-07 12:00 UTC, so the window opens 2026-08-31 12:00 UTC.
/// </summary>
public sealed class LimitStopEndpointTests
{
    private const string A = "a@example.com";
    private const string B = "b@example.com";
    private const string Since = "2026-08-31T12:00:00Z";
    private const string TokenRecord = "Claude lane stopped. lane=intake class=rate-limit credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN resets=2026-09-05T14:00:00Z";

    private static readonly Uri _scan = new("/api/roles/limit-stops", UriKind.Relative);
    private static readonly CiRole _automations = new("AUTOMATIONS", CiSecretScope.Organization, "octo");
    private static readonly DateTimeOffset _newest = new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(AppFactory Factory, SpyReader Reader, SpyGraphQl GraphQl)> StartAsync(params string[] laneRepositories)
    {
        AppFactory factory = new();
        if (laneRepositories.Length > 0)
        {
            JsonObject configuration = JsonNode.Parse(await File.ReadAllTextAsync(factory.ConfigPath, Token))!.AsObject();
            configuration["laneRepositories"] = new JsonArray([.. laneRepositories.Select(static repository => (JsonNode)repository)]);
            await File.WriteAllTextAsync(factory.ConfigPath, configuration.ToJsonString(), Token);
        }

        SpyReader reader = new();
        SpyGraphQl graphQl = new();
        factory.Overrides = services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGhApiReader>(reader));
            services.Replace(ServiceDescriptor.Singleton<IGhGraphQlReader>(graphQl));
        };
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", Token);
        await factory.WriteStateFileAsync("live@example.com", Token);
        factory.Cli.Email = "live@example.com";
        return (factory, reader, graphQl);
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

    private static async Task<JsonObject> ScanAsync(AppFactory factory, object from)
    {
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(_scan, new { from }, Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!;
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string RunsPath(string repository, int page = 1, DateTimeOffset? before = null) =>
        "repos/" + repository + "/actions/runs?status=failure&created="
        + (before is DateTimeOffset upTo ? Since + ".." + Stamp(upTo) : "%3E%3D" + Since)
        + "&per_page=100&page=" + page.ToString(CultureInfo.InvariantCulture);

    /// <summary>One failed run; its check suite's node id is made from its id. A fork's run names the fork as <paramref name="HeadRepository"/>.</summary>
    private sealed record Run(long Id, DateTimeOffset CreatedAt, string? HeadRepository = null)
    {
        public string Suite => "CS_" + Id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Run <paramref name="index"/> of a steady stream, newest first, two minutes apart.</summary>
    private static Run Nth(int index) => new(100_000 + index, _newest.AddMinutes(-2 * index));

    /// <summary>"List workflow runs for a repository", documented shape: https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-repository.</summary>
    private static Result<JsonNode, GhApiFailure> Runs(string repository, params Run[] runs) => Result<JsonNode, GhApiFailure>.Success(new JsonObject
    {
        ["total_count"] = runs.Length,
        ["workflow_runs"] = new JsonArray([.. runs.Select(run => (JsonNode)new JsonObject
        {
            ["id"] = run.Id,
            ["name"] = "intake",
            ["status"] = "completed",
            ["conclusion"] = "failure",
            ["created_at"] = Stamp(run.CreatedAt),
            ["html_url"] = "https://github.com/" + repository + "/actions/runs/" + run.Id,
            ["run_attempt"] = 1,
            ["check_suite_node_id"] = run.Suite,
            ["head_repository"] = new JsonObject { ["full_name"] = run.HeadRepository ?? repository },
        })]),
    });

    /// <summary>A page of the steady stream: runs <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> - 1.</summary>
    private static Result<JsonNode, GhApiFailure> Stream(string repository, int first, int count = 100) =>
        Runs(repository, [.. Enumerable.Range(first, count).Select(Nth)]);

    /// <summary>
    /// One check run as the batch query reads it. <paramref name="Name"/> and
    /// <paramref name="Conclusion"/> say which job it stands for; the query
    /// asks for neither, so they are not in the answer.
    /// </summary>
    private sealed record CheckRun(string Name, string Conclusion, string[] Messages, bool MoreAnnotations = false, long DatabaseId = 555);

    /// <summary>A check suite node in the shape of the batch query (GitHub GraphQL CheckSuite.checkRuns, CheckRun.annotations).</summary>
    private static JsonObject Suite(Run run, params CheckRun[] checkRuns) => Suite(run, false, checkRuns);

    private static JsonObject Suite(Run run, bool moreCheckRuns, params CheckRun[] checkRuns) => new()
    {
        ["checkRuns"] = new JsonObject
        {
            ["pageInfo"] = new JsonObject { ["hasNextPage"] = moreCheckRuns },
            ["nodes"] = new JsonArray([.. checkRuns.Select(static checkRun => (JsonNode)new JsonObject
            {
                ["databaseId"] = checkRun.DatabaseId,
                ["annotations"] = new JsonObject
                {
                    ["pageInfo"] = new JsonObject { ["hasNextPage"] = checkRun.MoreAnnotations },
                    ["nodes"] = new JsonArray([.. checkRun.Messages.Select(static message => (JsonNode)new JsonObject { ["message"] = message })]),
                },
            })]),
        },
    };

    private static Result<JsonNode, GhApiFailure> Batch(int remaining, params JsonNode?[] suites) => Result<JsonNode, GhApiFailure>.Success(new JsonObject
    {
        ["rateLimit"] = new JsonObject { ["remaining"] = remaining },
        ["nodes"] = new JsonArray([.. suites]),
    });

    /// <summary>"List check run annotations", documented shape (a bare array): https://docs.github.com/en/rest/checks/runs#list-check-run-annotations.</summary>
    private static Result<JsonNode, GhApiFailure> Annotations(IEnumerable<string> messages) => Result<JsonNode, GhApiFailure>.Success(new JsonArray([.. messages.Select(static message => (JsonNode)new JsonObject
    {
        ["path"] = ".github",
        ["annotation_level"] = "notice",
        ["title"] = "",
        ["message"] = message,
    })]));

    private static Result<JsonNode, GhApiFailure> RateLimited() =>
        Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(GhApiReader.RateLimitMessage, RateLimited: true, 403));

    private static JsonObject Coverage(JsonObject body, int index) => body["coverage"]!.AsArray()[index]!.AsObject();

    private static DateTimeOffset? CoveredBack(JsonObject coverage) =>
        coverage["coveredBack"] is JsonValue value ? DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture) : null;

    private static string[] Problems(JsonObject body) => [.. body["problems"]!.AsArray().Select(static problem => problem!.GetValue<string>())];

    [Fact]
    public async Task WithNoLaneRepositoriesNothingIsReadAndTheResultIsNotChecked()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync();
        await using AppFactory _ = factory;

        JsonObject body = await ScanAsync(factory, A);

        body["outcome"]!.GetValue<string>().ShouldBe("notChecked");
        body["hits"]!.AsArray().ShouldBeEmpty();
        reader.Paths.ShouldBeEmpty();
        graphQl.Batches.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIntakeRunWithItsRecordOnAGreenJobAndItsStatusJobRedIsAHit()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        await HistoryAsync(factory);
        Run run = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", run));
        graphQl.Answers.Enqueue(Batch(4000, Suite(
            run,
            new CheckRun("claude", "SUCCESS", [TokenRecord]),
            new CheckRun("status", "FAILURE", ["Process completed with exit code 1."]))));

        JsonObject body = await ScanAsync(factory, A);

        reader.Paths.ShouldBe([RunsPath("octo/lanes")]);
        graphQl.Batches.ShouldBe([["CS_101"]]);
        body["outcome"]!.GetValue<string>().ShouldBe("checked");
        body["runsRead"]!.GetValue<int>().ShouldBe(1);
        body["calls"]!.GetValue<int>().ShouldBe(2);
        JsonObject hit = body["hits"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        hit["label"]!.GetValue<string>().ShouldBe("subscription limit");
        hit["credential"]!.GetValue<string>().ShouldBe("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN");
        hit["repository"]!.GetValue<string>().ShouldBe("octo/lanes");
        hit["url"]!.GetValue<string>().ShouldBe("https://github.com/octo/lanes/actions/runs/101");
        Coverage(body, 0)["complete"]!.GetValue<bool>().ShouldBeTrue();
        CoveredBack(Coverage(body, 0)).ShouldBe(run.CreatedAt);
    }

    [Fact]
    public async Task AJsonCreditStopRecordOnAnApiKeySecretReadsCreditStop()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        await HistoryAsync(factory);
        Run run = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", run));
        graphQl.Answers.Enqueue(Batch(4000, Suite(run, new CheckRun("claude", "FAILURE", [
            "Claude lane stopped on a credit stop.\nlane-outcome {\"v\":1,\"lane\":\"intake-triage\",\"outcome\":\"failure\",\"class\":\"credit-stop\",\"credential\":{\"secret\":\"AUTOMATIONS_CLAUDE_API_KEY\",\"kind\":\"api-key\",\"role\":\"AUTOMATIONS\"},\"handoff\":true}",
        ]))));

        JsonObject body = await ScanAsync(factory, A);

        JsonObject hit = body["hits"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        hit["label"]!.GetValue<string>().ShouldBe("credit stop");
        hit["half"]!.GetValue<string>().ShouldBe("key");
    }

    [Fact]
    public async Task TwelveFullPagesNarrowTheDateRangeOnTheEleventhCallAndDropTheRunSeenTwice()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;

        // Pages 1-10 hold runs 0-999. The narrowed range is inclusive, so its
        // page 1 starts again at run 999; then 1099-1198 and a short 1199-1203.
        for (int page = 0; page < 10; page++)
        {
            reader.Answers.Enqueue(Stream("octo/lanes", page * 100));
        }

        reader.Answers.Enqueue(Stream("octo/lanes", 999));
        reader.Answers.Enqueue(Stream("octo/lanes", 1099));
        reader.Answers.Enqueue(Stream("octo/lanes", 1199, 5));

        JsonObject body = await ScanAsync(factory, A);

        reader.Paths.Count.ShouldBe(13);
        reader.Paths[9].ShouldBe(RunsPath("octo/lanes", 10));
        reader.Paths[10].ShouldBe(RunsPath("octo/lanes", 1, Nth(999).CreatedAt));
        reader.Paths[11].ShouldBe(RunsPath("octo/lanes", 2, Nth(999).CreatedAt));
        graphQl.Batches[10].Count.ShouldBe(99);
        graphQl.Batches[10].ShouldNotContain(Nth(999).Suite);
        body["outcome"]!.GetValue<string>().ShouldBe("checked");
        body["runsRead"]!.GetValue<int>().ShouldBe(1204);
        body["calls"]!.GetValue<int>().ShouldBe(26);
        Coverage(body, 0)["complete"]!.GetValue<bool>().ShouldBeTrue();
        CoveredBack(Coverage(body, 0)).ShouldBe(Nth(1203).CreatedAt);
    }

    [Fact]
    public async Task ThirtyCallsEndTheScanAsPartialCoveredBackToTheOldestRunReadInFull()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes", "octo/other");
        await using AppFactory _ = factory;

        // Twenty full pages are there to read; 15 pages and their 15 batches spend the budget.
        reader.Fallback = (call, _) => Stream("octo/lanes", call <= 10 ? (call - 1) * 100 : 999 + ((call - 11) * 100));

        JsonObject body = await ScanAsync(factory, A);

        (reader.Paths.Count + graphQl.Batches.Count).ShouldBe(30);
        reader.Paths.Count.ShouldBe(15);
        body["calls"]!.GetValue<int>().ShouldBe(30);
        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        Coverage(body, 0)["complete"]!.GetValue<bool>().ShouldBeFalse();
        CoveredBack(Coverage(body, 0)).ShouldBe(Nth(1498).CreatedAt);
        Coverage(body, 1)["repository"]!.GetValue<string>().ShouldBe("octo/other");
        Coverage(body, 1)["complete"]!.GetValue<bool>().ShouldBeFalse();
        CoveredBack(Coverage(body, 1)).ShouldBeNull();
        Problems(body).ShouldBe([
            "the check used its 30 GitHub calls and stopped there",
            "octo/other: not reached, as the check stopped before it",
        ]);
        reader.Paths.ShouldAllBe(static path => path.StartsWith("repos/octo/lanes/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARunOlderThanSevenDaysIsNotReadAndOneAtExactlySevenDaysIs()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;

        // The window opens at 2026-08-31 12:00:00 UTC, 7 days before the app's clock.
        Run edge = new(101, new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero));
        Run old = new(100, new DateTimeOffset(2026, 8, 31, 11, 59, 59, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", edge, old));

        JsonObject body = await ScanAsync(factory, A);

        graphQl.Batches.ShouldBe([[edge.Suite]]);
        body["runsRead"]!.GetValue<int>().ShouldBe(1);
        body["outcome"]!.GetValue<string>().ShouldBe("checked");
        CoveredBack(Coverage(body, 0)).ShouldBe(edge.CreatedAt);
    }

    [Fact]
    public async Task ABudgetSpentExactlyAsTheNextRepositoryStartsNamesItAsNotReached()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes", "octo/other");
        await using AppFactory _ = factory;

        // Fourteen full pages and a short fifteenth, each with its batch: the 30th call ends octo/lanes.
        reader.Fallback = (call, path) => path.StartsWith("repos/octo/lanes/", StringComparison.Ordinal)
            ? Stream("octo/lanes", call <= 10 ? (call - 1) * 100 : 999 + ((call - 11) * 100), call == 15 ? 5 : 100)
            : Runs("octo/other");

        JsonObject body = await ScanAsync(factory, A);

        body["calls"]!.GetValue<int>().ShouldBe(30);
        reader.Paths.ShouldAllBe(static path => path.StartsWith("repos/octo/lanes/", StringComparison.Ordinal));
        Coverage(body, 0)["complete"]!.GetValue<bool>().ShouldBeTrue();
        Coverage(body, 1)["complete"]!.GetValue<bool>().ShouldBeFalse();
        CoveredBack(Coverage(body, 1)).ShouldBeNull();
        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        Problems(body).ShouldBe([
            "the check used its 30 GitHub calls and stopped there",
            "octo/other: not reached, as the check stopped before it",
        ]);
    }

    [Fact]
    public async Task ABatchLeavingFewerThanAThousandPointsEndsTheScanWithNoSecondBatch()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        reader.Fallback = (call, _) => Stream("octo/lanes", (call - 1) * 100);
        graphQl.Remaining = 900;

        JsonObject body = await ScanAsync(factory, A);

        reader.Paths.Count.ShouldBe(1);
        graphQl.Batches.Count.ShouldBe(1);
        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        Problems(body).ShouldHaveSingleItem().ShouldContain(LimitStopScan.GraphQlPointFloor.ToString("N0", CultureInfo.InvariantCulture));
        CoveredBack(Coverage(body, 0)).ShouldBe(Nth(99).CreatedAt);
    }

    [Fact]
    public async Task GraphQlErrorsReadPartialNeverChecked()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        reader.Answers.Enqueue(Stream("octo/lanes", 0, 3));
        graphQl.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub's GraphQL API answered the check suite annotations query with errors (MAX_NODE_LIMIT_EXCEEDED)", RateLimited: false, 200)));

        JsonObject body = await ScanAsync(factory, A);

        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        Problems(body).ShouldBe(["octo/lanes: GitHub's GraphQL API answered the check suite annotations query with errors (MAX_NODE_LIMIT_EXCEEDED)"]);
        CoveredBack(Coverage(body, 0)).ShouldBeNull();
    }

    [Fact]
    public async Task AGraphQlRateLimitEndsTheScanAsPartialWithNoFurtherRead()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes", "octo/other");
        await using AppFactory _ = factory;
        reader.Fallback = (call, _) => Stream("octo/lanes", (call - 1) * 100);
        graphQl.Answers.Enqueue(RateLimited());

        JsonObject body = await ScanAsync(factory, A);

        reader.Paths.ShouldBe([RunsPath("octo/lanes")]);
        graphQl.Batches.Count.ShouldBe(1);
        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        Problems(body).ShouldBe([GhApiReader.RateLimitMessage, "octo/other: not reached, as the check stopped before it"]);
        body["runsRead"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public async Task ARateLimitWhileListingRunsEndsTheScanAsPartialWithNoFurtherRead()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes", "octo/other");
        await using AppFactory _ = factory;
        reader.Answers.Enqueue(RateLimited());

        JsonObject body = await ScanAsync(factory, A);

        reader.Paths.ShouldBe([RunsPath("octo/lanes")]);
        graphQl.Batches.ShouldBeEmpty();
        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        Problems(body).ShouldBe([GhApiReader.RateLimitMessage, "octo/other: not reached, as the check stopped before it"]);
    }

    [Fact]
    public async Task ACheckRunWithSixtyAnnotationsIsFinishedWithOneRestReadAndTheScanStaysChecked()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        await HistoryAsync(factory);
        Run run = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        List<string> sixty = [.. Enumerable.Range(1, 60).Select(static n => "warning " + n.ToString(CultureInfo.InvariantCulture))];
        sixty[54] = TokenRecord;
        reader.Answers.Enqueue(Runs("octo/lanes", run));
        reader.Answers.Enqueue(Annotations(sixty));
        graphQl.Answers.Enqueue(Batch(4000, Suite(run, new CheckRun("claude", "SUCCESS", [.. sixty.Take(50)], MoreAnnotations: true, DatabaseId: 777))));

        JsonObject body = await ScanAsync(factory, A);

        reader.Paths.ShouldBe([RunsPath("octo/lanes"), "repos/octo/lanes/check-runs/777/annotations?per_page=100"]);
        body["outcome"]!.GetValue<string>().ShouldBe("checked");
        body["calls"]!.GetValue<int>().ShouldBe(3);
        body["hits"]!.AsArray().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AFollowUpThatIsItselfCutOffReadsPartialCoveredBackAboveThatRun()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        Run newer = new(102, new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero));
        Run older = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", newer, older));
        reader.Answers.Enqueue(Annotations(Enumerable.Repeat("warning", 100)));
        graphQl.Answers.Enqueue(Batch(
            4000,
            Suite(newer, new CheckRun("claude", "SUCCESS", ["ok"])),
            Suite(older, new CheckRun("claude", "SUCCESS", [.. Enumerable.Repeat("warning", 50)], MoreAnnotations: true, DatabaseId: 778))));

        JsonObject body = await ScanAsync(factory, A);

        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        body["runsRead"]!.GetValue<int>().ShouldBe(1);
        Problems(body).ShouldHaveSingleItem().ShouldStartWith("octo/lanes: run 101 ");
        Coverage(body, 0)["complete"]!.GetValue<bool>().ShouldBeFalse();
        CoveredBack(Coverage(body, 0)).ShouldBe(newer.CreatedAt);
    }

    [Fact]
    public async Task ARateLimitOnTheAnnotationsFollowUpEndsTheScanAsPartialWithNoFurtherListing()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes", "octo/other");
        await using AppFactory _ = factory;
        Run run = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", run));
        reader.Answers.Enqueue(RateLimited());
        graphQl.Answers.Enqueue(Batch(4000, Suite(run, new CheckRun("claude", "SUCCESS", [.. Enumerable.Repeat("warning", 50)], MoreAnnotations: true, DatabaseId: 779))));

        JsonObject body = await ScanAsync(factory, A);

        reader.Paths.ShouldBe([RunsPath("octo/lanes"), "repos/octo/lanes/check-runs/779/annotations?per_page=100"]);
        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        Problems(body).ShouldBe([GhApiReader.RateLimitMessage, "octo/other: not reached, as the check stopped before it"]);
        CoveredBack(Coverage(body, 0)).ShouldBeNull();
    }

    [Fact]
    public async Task MoreThanFiftyCheckRunsInASuiteIsANamedProblem()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        Run run = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", run));
        graphQl.Answers.Enqueue(Batch(4000, Suite(run, moreCheckRuns: true, new CheckRun("claude", "SUCCESS", []))));

        JsonObject body = await ScanAsync(factory, A);

        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        Problems(body).ShouldHaveSingleItem().ShouldStartWith("octo/lanes: run 101 ");
        CoveredBack(Coverage(body, 0)).ShouldBeNull();
    }

    [Fact]
    public async Task ARunDeletedSinceItWasListedIsSkippedAndTheScanStaysChecked()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        Run deleted = new(102, new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero));
        Run kept = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", deleted, kept));
        graphQl.Answers.Enqueue(Batch(4000, null, Suite(kept, new CheckRun("claude", "SUCCESS", ["ok"]))));

        JsonObject body = await ScanAsync(factory, A);

        body["outcome"]!.GetValue<string>().ShouldBe("checked");
        body["runsRead"]!.GetValue<int>().ShouldBe(2);
        Problems(body).ShouldBeEmpty();
        CoveredBack(Coverage(body, 0)).ShouldBe(kept.CreatedAt);
    }

    [Fact]
    public async Task OnlyTheRecordBeforeLastSdkResultIsReadNotASecondMarkerAfterIt()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        await HistoryAsync(factory);
        Run run = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", run));
        graphQl.Answers.Enqueue(Batch(4000, Suite(run, new CheckRun("claude", "FAILURE", [
            "Claude lane failed.\nlane-outcome {\"v\":1,\"lane\":\"intake-triage\",\"outcome\":\"failure\",\"class\":\"credit-stop\",\"credential\":{\"secret\":\"AUTOMATIONS_CLAUDE_API_KEY\",\"kind\":\"api-key\"}}\n"
            + "Last SDK result: lane-outcome {\"v\":1,\"lane\":\"intake-triage\",\"outcome\":\"failure\",\"class\":\"rate-limit\",\"credential\":{\"secret\":\"AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN\",\"kind\":\"oauth-token\"}}",
        ]))));

        JsonObject body = await ScanAsync(factory, A);

        body["hits"]!.AsArray().ShouldHaveSingleItem()!["label"]!.GetValue<string>().ShouldBe("credit stop");
    }

    /// <summary>
    /// A re-run's attempts share one check suite (live read of a re-run run in
    /// claude-code-plugins, 2026-10-22: the suite listed both attempts' check
    /// runs with <c>checkType: ALL</c>), so a stop on the first attempt is
    /// read even when the second went green.
    /// </summary>
    [Fact]
    public async Task AStopOnTheFirstAttemptOfAReRunRunIsAHit()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        await HistoryAsync(factory);
        Run run = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", run));
        graphQl.Answers.Enqueue(Batch(4000, Suite(
            run,
            new CheckRun("claude", "SUCCESS", ["ok"], DatabaseId: 902),
            new CheckRun("claude", "FAILURE", [TokenRecord], DatabaseId: 901))));

        JsonObject body = await ScanAsync(factory, A);

        graphQl.Queries.ShouldAllBe(static query => query == GhReadQueries.CheckSuiteAnnotations);
        body["hits"]!.AsArray().ShouldHaveSingleItem();
        body["outcome"]!.GetValue<string>().ShouldBe("checked");
    }

    [Fact]
    public async Task AForksRunIsSkippedSoItsRecordNamingARosterSecretIsNoHit()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        await HistoryAsync(factory);
        Run fork = new(102, new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero), HeadRepository: "someone/lanes");
        Run own = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", fork, own));
        graphQl.Answers.Enqueue(Batch(4000, Suite(own, new CheckRun("claude", "SUCCESS", ["ok"]))));
        graphQl.Answers.Enqueue(Batch(4000, Suite(fork, new CheckRun("claude", "FAILURE", [TokenRecord]))));

        JsonObject body = await ScanAsync(factory, A);

        graphQl.Batches.ShouldBe([[own.Suite]]);
        body["hits"]!.AsArray().ShouldBeEmpty();
        body["skipped"]!.GetValue<int>().ShouldBe(1);
        body["outcome"]!.GetValue<string>().ShouldBe("checked");
        CoveredBack(Coverage(body, 0)).ShouldBe(own.CreatedAt);
    }

    [Fact]
    public async Task ARepositoryAnsweringNotFoundIsANamedProblemAndTheNextIsStillRead()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/gone", "octo/lanes");
        await using AppFactory _ = factory;
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 404 for " + RunsPath("octo/gone"), RateLimited: false, 404)));
        reader.Answers.Enqueue(Runs("octo/lanes"));

        JsonObject body = await ScanAsync(factory, A);

        reader.Paths.ShouldBe([RunsPath("octo/gone"), RunsPath("octo/lanes")]);
        body["outcome"]!.GetValue<string>().ShouldBe("partial");
        Problems(body).ShouldHaveSingleItem().ShouldStartWith("octo/gone: GitHub answered HTTP 404");
        Coverage(body, 0)["complete"]!.GetValue<bool>().ShouldBeFalse();
        Coverage(body, 1)["complete"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task TwoAccountsAreJudgedFromOneReadEachWithItsOwnHits()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        DateTimeOffset at = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await factory.Services.GetRequiredService<RosterFile>().History.AppendAsync(
            [
                new LaneHistoryEntry(at, _automations, LaneHistoryEntry.TokenHalf, A, null, LaneVia.Assign),
                new LaneHistoryEntry(at, _automations, LaneHistoryEntry.KeyHalf, B, null, LaneVia.Assign),
            ],
            Token);
        Run run = new(101, new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero));
        reader.Answers.Enqueue(Runs("octo/lanes", run));
        graphQl.Answers.Enqueue(Batch(4000, Suite(run, new CheckRun("claude", "FAILURE", [
            "lane=intake class=rate-limit credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN",
            "lane=intake class=rate-limit credential=AUTOMATIONS_CLAUDE_API_KEY",
        ]))));

        JsonObject body = await ScanAsync(factory, new[] { A, B });

        (reader.Paths.Count + graphQl.Batches.Count).ShouldBe(2);
        body["accounts"]!.AsArray().Select(static account => account!.GetValue<string>()).ShouldBe([A, B]);
        body["hits"]!.AsArray().Select(static hit => (hit!["account"]!.GetValue<string>(), hit["label"]!.GetValue<string>())).ShouldBe([
            (A, "subscription limit"),
            (B, "credit stop"),
        ]);
    }

    [Fact]
    public async Task AFromThatIsNoAddressIsRefusedBeforeAnyRead()
    {
        (AppFactory factory, SpyReader reader, SpyGraphQl graphQl) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(_scan, new { from = "not an address" }, Token);
        using HttpResponseMessage inList = await client.PostAsJsonAsync(_scan, new { from = new[] { A, "not an address" } }, Token);
        using HttpResponseMessage empty = await client.PostAsJsonAsync(_scan, new { from = Array.Empty<string>() }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        inList.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        reader.Paths.ShouldBeEmpty();
        graphQl.Batches.ShouldBeEmpty();
    }

    /// <summary>
    /// Records every path it is asked for and gives the scripted answers in
    /// order, then <see cref="Fallback"/>'s, given the 1-based number of the call.
    /// </summary>
    private sealed class SpyReader : IGhApiReader
    {
        public List<string> Paths { get; } = [];

        public Queue<Result<JsonNode, GhApiFailure>> Answers { get; } = new();

        public Func<int, string, Result<JsonNode, GhApiFailure>>? Fallback { get; set; }

        public Task<Result<JsonNode, GhApiFailure>> GetAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(Answers.TryDequeue(out Result<JsonNode, GhApiFailure> answer) ? answer
                : Fallback is { } fallback ? fallback(Paths.Count, path)
                : Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("no answer scripted", RateLimited: false)));
        }
    }

    /// <summary>
    /// Records each batch's ids and gives the scripted answers in order, then
    /// a batch in which every suite has no check runs and <see cref="Remaining"/> points are left.
    /// </summary>
    private sealed class SpyGraphQl : IGhGraphQlReader
    {
        public List<List<string>> Batches { get; } = [];

        public List<GhReadQuery> Queries { get; } = [];

        public Queue<Result<JsonNode, GhApiFailure>> Answers { get; } = new();

        public int Remaining { get; set; } = 4000;

        public Task<Result<JsonNode, GhApiFailure>> QueryAsync(GhReadQuery query, IReadOnlyDictionary<string, JsonNode?> variables, CancellationToken cancellationToken)
        {
            List<string> ids = [.. variables["ids"]!.AsArray().Select(static id => id!.GetValue<string>())];
            Queries.Add(query);
            Batches.Add(ids);
            return Task.FromResult(Answers.TryDequeue(out Result<JsonNode, GhApiFailure> answer)
                ? answer
                : Batch(Remaining, [.. ids.Select(static _ => (JsonNode)new JsonObject
                {
                    ["checkRuns"] = new JsonObject { ["pageInfo"] = new JsonObject { ["hasNextPage"] = false }, ["nodes"] = new JsonArray() },
                })]));
        }
    }
}
