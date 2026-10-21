using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Endpoints;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// The kill switches: read on the Check roles click, three organization
/// switches the page may flip and one repository switch it only shows, and a
/// toggle that sends only the value of an organization variable that exists.
/// </summary>
public sealed class KillSwitchEndpointTests
{
    private const string HolderEmail = "holder@example.com";

    private static readonly Uri _check = new("/api/roles/check", UriKind.Relative);
    private static readonly Uri _toggle = new("/api/kill-switches", UriKind.Relative);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(AppFactory Factory, SpyReader Reader, SpyWriter Writer)> StartAsync(params string[] laneRepositories)
    {
        AppFactory factory = new();
        if (laneRepositories.Length > 0)
        {
            JsonObject configuration = JsonNode.Parse(await File.ReadAllTextAsync(factory.ConfigPath, Token))!.AsObject();
            configuration["laneRepositories"] = new JsonArray([.. laneRepositories.Select(static repository => (JsonNode)repository)]);
            await File.WriteAllTextAsync(factory.ConfigPath, configuration.ToJsonString(), Token);
        }

        SpyReader reader = new();
        SpyWriter writer = new();
        factory.Overrides = services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGhApiReader>(reader));
            services.Replace(ServiceDescriptor.Singleton<IGhVariableWriter>(writer));
        };
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", Token);
        await factory.WriteStateFileAsync("live@example.com", Token);
        factory.Cli.Email = "live@example.com";
        return (factory, reader, writer);
    }

    /// <summary>"List organization secrets", documented shape, with one role secret.</summary>
    private static JsonObject SecretsPage() => new()
    {
        ["total_count"] = 1,
        ["secrets"] = new JsonArray(new JsonObject
        {
            ["name"] = "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN",
            ["created_at"] = "2026-01-01T00:00:00Z",
            ["updated_at"] = "2026-10-01T09:00:00Z",
            ["visibility"] = "all",
        }),
    };

    /// <summary>"Get an organization variable", documented shape: https://docs.github.com/en/rest/actions/variables#get-an-organization-variable.</summary>
    private static Result<JsonNode, GhApiFailure> Variable(string name, string value, string visibility = "all") => Result<JsonNode, GhApiFailure>.Success(new JsonObject
    {
        ["name"] = name,
        ["value"] = value,
        ["created_at"] = "2026-01-01T00:00:00Z",
        ["updated_at"] = "2026-10-01T00:00:00Z",
        ["visibility"] = visibility,
    });

    private static Result<JsonNode, GhApiFailure> NotFound() =>
        Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 404", RateLimited: false, 404));

    private static Task<Roster> HoldAsync(AppFactory factory) =>
        factory.Services.GetRequiredService<RosterFile>().UpdateAsync(
            roster => roster.With(new RosterEntry(AccountEmail.Parse(HolderEmail).Value).WithCiRoles(
                [new CiRoleAssignment(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 10, 1))])),
            Token);

    private static List<(string Name, string Owner, string? State, bool Writable)> Switches(JsonObject body) =>
        [.. body["killSwitches"]!.AsArray().Select(static item => (
            item!["name"]!.GetValue<string>(),
            item["owner"]!.GetValue<string>(),
            item["state"]?.GetValue<string>(),
            item["writable"]!.GetValue<bool>()))];

    [Fact]
    public async Task TheCheckReadsTheThreeOrganizationSwitchesAndEachLaneRepositorysSwitch()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync("octo/lanes", "octo/other");
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await HoldAsync(factory);
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Success(SecretsPage()));
        reader.Answers.Enqueue(Variable("CLAUDE_LANES_DISABLED", "false"));
        reader.Answers.Enqueue(Variable("CLAUDE_REVIEW_DISABLED", "true"));
        reader.Answers.Enqueue(NotFound());
        reader.Answers.Enqueue(Variable("CLAUDE_LANES_REPO_DISABLED", "false"));
        reader.Answers.Enqueue(NotFound());

        using HttpResponseMessage response = await client.PostAsJsonAsync(_check, new { }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        reader.Paths.ShouldBe([
            "orgs/octo/actions/secrets?per_page=100&page=1",
            "orgs/octo/actions/variables/CLAUDE_LANES_DISABLED",
            "orgs/octo/actions/variables/CLAUDE_REVIEW_DISABLED",
            "orgs/octo/actions/variables/CLAUDE_SECURITY_REVIEW_DISABLED",
            "repos/octo/lanes/actions/variables/CLAUDE_LANES_REPO_DISABLED",
            "repos/octo/other/actions/variables/CLAUDE_LANES_REPO_DISABLED",
        ]);
        Switches((await response.Content.ReadFromJsonAsync<JsonObject>(Token))!).ShouldBe([
            ("CLAUDE_LANES_DISABLED", "octo", "running", true),
            ("CLAUDE_REVIEW_DISABLED", "octo", "stopped", true),
            ("CLAUDE_SECURITY_REVIEW_DISABLED", "octo", "running", true),
            ("CLAUDE_LANES_REPO_DISABLED", "octo/lanes", "running", false),
            ("CLAUDE_LANES_REPO_DISABLED", "octo/other", "stopped", false),
        ]);
        writer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASwitchThatCannotBeReadSaysWhyAndShowsNoState()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await HoldAsync(factory);
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Success(SecretsPage()));
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 403", RateLimited: false, 403)));
        reader.Answers.Enqueue(Variable("CLAUDE_REVIEW_DISABLED", "false"));
        reader.Answers.Enqueue(Variable("CLAUDE_SECURITY_REVIEW_DISABLED", "false"));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_check, new { }, Token);

        JsonObject lanes = (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!["killSwitches"]![0]!.AsObject();
        lanes["state"].ShouldBeNull();
        lanes["error"]!.GetValue<string>().ShouldBe("GitHub answered HTTP 403");
        writer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARateLimitOnASwitchEndsTheCheckAfterThatCall()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync("octo/lanes");
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await HoldAsync(factory);
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Success(SecretsPage()));
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure(GhApiReader.RateLimitMessage, RateLimited: true, 403)));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_check, new { }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        reader.Paths.Count.ShouldBe(2);
        writer.Calls.ShouldBeEmpty();
    }

    // The state table of the plan's kill-switch design, value by value. A null
    // value is a variable GitHub answers 404 for.
    [Theory]
    [InlineData("CLAUDE_LANES_DISABLED", "false", true)]
    [InlineData("CLAUDE_LANES_DISABLED", "False", false)]
    [InlineData("CLAUDE_LANES_DISABLED", " false", false)]
    [InlineData("CLAUDE_LANES_DISABLED", "true", false)]
    [InlineData("CLAUDE_LANES_DISABLED", null, false)]
    [InlineData("CLAUDE_REVIEW_DISABLED", "false", true)]
    [InlineData("CLAUDE_REVIEW_DISABLED", "False", true)]
    [InlineData("CLAUDE_REVIEW_DISABLED", " false", true)]
    [InlineData("CLAUDE_REVIEW_DISABLED", "true", false)]
    [InlineData("CLAUDE_REVIEW_DISABLED", null, true)]
    [InlineData("CLAUDE_SECURITY_REVIEW_DISABLED", "false", true)]
    [InlineData("CLAUDE_SECURITY_REVIEW_DISABLED", "False", true)]
    [InlineData("CLAUDE_SECURITY_REVIEW_DISABLED", " false", true)]
    [InlineData("CLAUDE_SECURITY_REVIEW_DISABLED", "true", false)]
    [InlineData("CLAUDE_SECURITY_REVIEW_DISABLED", null, true)]
    [InlineData("CLAUDE_LANES_REPO_DISABLED", "false", true)]
    [InlineData("CLAUDE_LANES_REPO_DISABLED", "False", false)]
    [InlineData("CLAUDE_LANES_REPO_DISABLED", " false", false)]
    [InlineData("CLAUDE_LANES_REPO_DISABLED", "true", false)]
    [InlineData("CLAUDE_LANES_REPO_DISABLED", null, false)]
    public async Task EachSwitchReadsRunningOrStoppedByItsOwnRule(string name, string? value, bool running)
    {
        KillSwitch kill = KillSwitches.All.Single(candidate => candidate.Name == name);
        SpyReader reader = new();
        reader.Answers.Enqueue(value is null ? NotFound() : Variable(name, value));

        var view = KillSwitchView.Of(kill, "octo", await reader.GetVariableAsync(kill.PathIn("octo"), Token));

        view.State.ShouldBe(running ? "running" : "stopped");
        view.Exists.ShouldBe(value is not null);
    }

    [Theory]
    [InlineData("CLAUDE_LANES_REPO_DISABLED")]
    [InlineData("claude_lanes_disabled")]
    [InlineData(" CLAUDE_LANES_DISABLED")]
    [InlineData("SOME_OTHER_VARIABLE")]
    [InlineData("")]
    public async Task ANameOutsideTheThreeOrganizationSwitchesIsRefusedWithNoGhCall(string name)
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(_toggle, new { org = "octo", name, value = "true" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        writer.Calls.ShouldBeEmpty();
        reader.Paths.ShouldBeEmpty();
    }

    // Each the JSON text of the value sent.
    [Theory]
    [InlineData("\"True\"")]
    [InlineData("\" true\"")]
    [InlineData("\"false \"")]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task AValueThatIsNotExactlyTrueOrFalseIsRefusedWithNoGhCall(string json)
    {
        var value = JsonNode.Parse(json);
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            _toggle,
            new JsonObject { ["org"] = "octo", ["name"] = "CLAUDE_LANES_DISABLED", ["value"] = value },
            Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        writer.Calls.ShouldBeEmpty();
        reader.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOrganizationThatIsNotALoginIsRefusedWithNoGhCall()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(_toggle, new { org = "octo/../repos/octo/lanes", name = "CLAUDE_LANES_DISABLED", value = "true" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        writer.Calls.ShouldBeEmpty();
        reader.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task AToggleReadsTheVisibilityWritesTheValueWithItThenReadsTheSwitchBack()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        reader.Answers.Enqueue(Variable("CLAUDE_LANES_DISABLED", "false", "private"));
        reader.Answers.Enqueue(Variable("CLAUDE_LANES_DISABLED", "true", "private"));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_toggle, new { org = "octo", name = "CLAUDE_LANES_DISABLED", value = "true" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        writer.Calls.Count.ShouldBe(1);
        writer.Calls[0].ShouldBe(("octo", "CLAUDE_LANES_DISABLED", true, "private", ""));
        reader.Paths.ShouldBe(["orgs/octo/actions/variables/CLAUDE_LANES_DISABLED", "orgs/octo/actions/variables/CLAUDE_LANES_DISABLED"]);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!;
        body["state"]!.GetValue<string>().ShouldBe("stopped");
        body["value"]!.GetValue<string>().ShouldBe("true");
        factory.Logs.Lines.ShouldContain("Information kill switch CLAUDE_LANES_DISABLED in org octo set to True ");
    }

    [Fact]
    public async Task ASelectedVariableSendsBackEveryRepositoryId()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        reader.Answers.Enqueue(Variable("CLAUDE_REVIEW_DISABLED", "false", "selected"));
        // "List selected repositories for an organization variable", documented shape.
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Success(new JsonObject
        {
            ["total_count"] = 2,
            ["repositories"] = new JsonArray(
                new JsonObject { ["id"] = 1296269, ["name"] = "lanes", ["full_name"] = "octo/lanes" },
                new JsonObject { ["id"] = 1296270, ["name"] = "other", ["full_name"] = "octo/other" }),
        }));
        reader.Answers.Enqueue(Variable("CLAUDE_REVIEW_DISABLED", "true", "selected"));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_toggle, new { org = "octo", name = "CLAUDE_REVIEW_DISABLED", value = true }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        writer.Calls.Count.ShouldBe(1);
        writer.Calls[0].ShouldBe(("octo", "CLAUDE_REVIEW_DISABLED", true, "selected", "1296269,1296270"));
        reader.Paths[1].ShouldBe("orgs/octo/actions/variables/CLAUDE_REVIEW_DISABLED/repositories?per_page=100&page=1");
    }

    [Fact]
    public async Task AVisibilityReadThatFailsWritesNothing()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 403", RateLimited: false, 403)));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_toggle, new { org = "octo", name = "CLAUDE_LANES_DISABLED", value = "true" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!["error"]!.GetValue<string>().ShouldStartWith("nothing was written");
        writer.Calls.ShouldBeEmpty();
        factory.Logs.Lines.ShouldContain("Warning kill switch CLAUDE_LANES_DISABLED in org octo not set to True: GitHub answered HTTP 403 ");
    }

    [Fact]
    public async Task ASelectedVariableWhoseRepositoriesCannotBeListedWritesNothing()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        reader.Answers.Enqueue(Variable("CLAUDE_LANES_DISABLED", "false", "selected"));
        reader.Answers.Enqueue(Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 500", RateLimited: false, 500)));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_toggle, new { org = "octo", name = "CLAUDE_LANES_DISABLED", value = "true" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        writer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AVariableThatDoesNotExistIsReportedAndNothingIsWritten()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        reader.Answers.Enqueue(NotFound());

        using HttpResponseMessage response = await client.PostAsJsonAsync(_toggle, new { org = "octo", name = "CLAUDE_REVIEW_DISABLED", value = false }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        string error = (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!["error"]!.GetValue<string>();
        error.ShouldContain("not set in org octo or not visible to this sign-in");
        error.ShouldContain("never creates it");
        writer.Calls.ShouldBeEmpty();
        reader.Paths.ShouldBe(["orgs/octo/actions/variables/CLAUDE_REVIEW_DISABLED"]);
    }

    [Fact]
    public async Task ANotFoundOnThePatchIsReportedWithNoReRead()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        reader.Answers.Enqueue(Variable("CLAUDE_REVIEW_DISABLED", "false", "all"));
        writer.Answer = Result<Unit, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 404: the variable does not exist", RateLimited: false, 404));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_toggle, new { org = "octo", name = "CLAUDE_REVIEW_DISABLED", value = false }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        writer.Calls.Count.ShouldBe(1);
        reader.Paths.Count.ShouldBe(1);
        factory.Logs.Lines.ShouldContain(line => line.StartsWith("Warning kill switch CLAUDE_REVIEW_DISABLED in org octo not set to False: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARateLimitOnToggleIsAConflictWithNoReRead()
    {
        (AppFactory factory, SpyReader reader, SpyWriter writer) = await StartAsync();
        await using AppFactory _ = factory;
        using HttpClient client = factory.CreateMutatingClient();
        reader.Answers.Enqueue(Variable("CLAUDE_LANES_DISABLED", "true", "all"));
        writer.Answer = Result<Unit, GhApiFailure>.Failure(new GhApiFailure(GhApiReader.RateLimitMessage, RateLimited: true, 403));

        using HttpResponseMessage response = await client.PostAsJsonAsync(_toggle, new { org = "octo", name = "CLAUDE_LANES_DISABLED", value = "false" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        writer.Calls.Count.ShouldBe(1);
        reader.Paths.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFollowerHasNoToggleRoute()
    {
        await using FollowerAppFactory factory = new();
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(_toggle, new { org = "octo", name = "CLAUDE_LANES_DISABLED", value = "true" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Records every path it is asked for and answers from a queue; an empty queue fails the read.</summary>
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

    /// <summary>Records every write it is handed, the visibility as its kind and joined ids, and gives the one scripted answer.</summary>
    private sealed class SpyWriter : IGhVariableWriter
    {
        public List<(string Organization, string Name, bool Value, string Visibility, string Ids)> Calls { get; } = [];

        public Result<Unit, GhApiFailure> Answer { get; set; } = Result<Unit, GhApiFailure>.Success(Unit.Value);

        public Task<Result<Unit, GhApiFailure>> SetOrganizationVariableAsync(string organization, string name, bool value, VariableVisibility visibility, CancellationToken cancellationToken)
        {
            Calls.Add((organization, name, value, visibility.Kind, string.Join(',', visibility.SelectedRepositoryIds)));
            return Task.FromResult(Answer);
        }
    }
}
