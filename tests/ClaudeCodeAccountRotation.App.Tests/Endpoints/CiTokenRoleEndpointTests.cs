using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// Generate CI token by role, over the real runner and a scripted
/// <c>setup-token</c>: one token and the stored Console key written to each
/// role's two secrets, with who may read each secret passed back, and Push API
/// key re-sending the key alone.
/// </summary>
public sealed class CiTokenRoleEndpointTests
{
    private const string Email = "lane@example.com";
    private const string Previous = "previous@example.com";
    private const string Code = "one-time-code#state";

    // Built from pieces, so the source holds nothing a secret scanner would take for a real key.
    private const string PlantedKey = "sk-ant-" + "api03-planted-role-key-9d1f";

    private static readonly CiRole _automations = new("AUTOMATIONS", CiSecretScope.Organization, "octo");
    private static readonly CiRole _issueLane = new("ISSUE_LANE", CiSecretScope.Organization, "octo");
    private static readonly object _bothRoles = new
    {
        roles = new[] { new { prefix = "AUTOMATIONS", organization = "octo" }, new { prefix = "ISSUE_LANE", organization = "octo" } },
    };

    private static Uri Account(string email, string path) => new("/api/accounts/" + Uri.EscapeDataString(email) + path, UriKind.Relative);

    private static AccountEmail Parse(string email) => AccountEmail.Parse(email).Value;

    /// <summary>A rostered account with a mapped browser and, when asked, its Console key stored.</summary>
    private static async Task<(AppFactory Factory, PathReader Reader)> StartAsync(bool withKey, CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        PathReader reader = new();
        KeyStore store = new();
        factory.Overrides = services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGhApiReader>(reader));
            services.Replace(ServiceDescriptor.Singleton<ISecretStore>(store));
        };
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage added = await client.PostAsJsonAsync(
            new Uri("/api/accounts", UriKind.Relative),
            new { email = Email, browser = "edge", browserProfileDirectory = "Profile 2" },
            cancellationToken))
        {
            added.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        if (withKey)
        {
            using HttpResponseMessage stored = await client.PutAsJsonAsync(Account(Email, "/api-key"), new { key = PlantedKey }, cancellationToken);
            stored.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        return (factory, reader);
    }

    /// <summary>Starts the session, pastes the code, and returns every body the page got back, the last one the session's end.</summary>
    private static async Task<List<string>> AssignAsync(HttpClient client, object body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage started = await client.PostAsJsonAsync(Account(Email, "/ci-token"), body, cancellationToken);
        string startBody = await started.Content.ReadAsStringAsync(cancellationToken);
        started.StatusCode.ShouldBe(HttpStatusCode.OK, startBody);
        string id = JsonNode.Parse(startBody)!["id"]!.GetValue<string>();
        using HttpResponseMessage answered = await client.PostAsJsonAsync(new Uri("/api/ci-token-sessions/" + id + "/code", UriKind.Relative), new { code = Code }, cancellationToken);
        answered.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [startBody, await answered.Content.ReadAsStringAsync(cancellationToken)];
    }

    private static Task<Roster> RosterAsync(AppFactory factory) =>
        factory.Services.GetRequiredService<RosterFile>().ReadAsync(TestContext.Current.CancellationToken);

    private static Task<Roster> SeedAsync(AppFactory factory, RosterEntry entry) =>
        factory.Services.GetRequiredService<RosterFile>().UpdateAsync(roster => roster.With(entry), TestContext.Current.CancellationToken);

    private static void AnswerAll(PathReader reader, params string[] names)
    {
        foreach (string name in names)
        {
            reader.Answers["orgs/octo/actions/secrets/" + name] = PathReader.Secret(name, "all");
        }
    }

    [Fact]
    public async Task TwoRolesRunSetupTokenOnceThenSetEachRolesTokenAndKeyInTurn()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "AUTOMATIONS_CLAUDE_API_KEY", "ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", "ISSUE_LANE_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        List<string> bodies = await AssignAsync(client, _bothRoles, TestContext.Current.CancellationToken);

        JsonNode.Parse(bodies[^1])!["state"]!.GetValue<string>().ShouldBe("Completed");
        factory.SetupToken.Children.Count.ShouldBe(1);
        factory.Secrets.Sets.Select(static set => (set.Secret.Name, set.Value)).ShouldBe(
        [
            ("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", SetupTokenScript.Token),
            ("AUTOMATIONS_CLAUDE_API_KEY", PlantedKey),
            ("ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", SetupTokenScript.Token),
            ("ISSUE_LANE_CLAUDE_API_KEY", PlantedKey),
        ]);
        RosterEntry entry = (await RosterAsync(factory)).Find(Parse(Email))!;
        entry.CiRoles.Select(static held => (held.Role, held.HasToken, held.HasKey)).ShouldBe([(_automations, true, true), (_issueLane, true, true)]);
    }

    [Fact]
    public async Task EachSecretIsSetAgainWithWhoMayReadItAndANewOneIsVisibleToAll()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        reader.Answers["orgs/octo/actions/secrets/AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"] = PathReader.Secret("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "selected");
        reader.Answers["orgs/octo/actions/secrets/AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN/repositories?per_page=100&page=1"] = PathReader.Repositories("a", "b");
        reader.Answers["orgs/octo/actions/secrets/AUTOMATIONS_CLAUDE_API_KEY"] = PathReader.Secret("AUTOMATIONS_CLAUDE_API_KEY", "private");
        reader.Answers["orgs/octo/actions/secrets/ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN"] = PathReader.Secret("ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", "all");
        reader.Answers["orgs/octo/actions/secrets/ISSUE_LANE_CLAUDE_API_KEY"] = PathReader.NotFound();
        using HttpClient client = factory.CreateMutatingClient();

        await AssignAsync(client, _bothRoles, TestContext.Current.CancellationToken);

        IReadOnlyList<string>[] arguments = [.. factory.Secrets.Sets.Select(static set => GhSecretWriter.Arguments(set.Secret, set.Visibility, set.Repositories))];
        arguments.Select(static list => string.Join(' ', list.Skip(5))).ShouldBe(
        [
            "--org octo --repos a,b",
            "--org octo --visibility private",
            "--org octo --visibility all",
            "--org octo --visibility all",
        ]);
        arguments.ShouldAllBe(list => list.Contains("--visibility") || list.Contains("--repos"));
    }

    [Fact]
    public async Task ASecretWhoseVisibilityCannotBeReadRefusesBeforeAnyTokenIsMade()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN");
        reader.Answers["orgs/octo/actions/secrets/AUTOMATIONS_CLAUDE_API_KEY"] =
            Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 403 for that path", RateLimited: false));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage started = await client.PostAsJsonAsync(
            Account(Email, "/ci-token"),
            new { roles = new[] { new { prefix = "AUTOMATIONS", organization = "octo" } } },
            TestContext.Current.CancellationToken);

        started.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("HTTP 403");
        factory.SetupToken.Children.ShouldBeEmpty();
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARoleWithAKeyHalfIsRefusedBeforeSetupTokenWhenTheAccountHasNoKey()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage started = await client.PostAsJsonAsync(Account(Email, "/ci-token"), _bothRoles, TestContext.Current.CancellationToken);

        started.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("paste this account's Console key first");
        factory.SetupToken.Children.ShouldBeEmpty();
        reader.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheLegacyRoleNeedsNoKey()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "CLAUDE_CODE_OAUTH_TOKEN");
        using HttpClient client = factory.CreateMutatingClient();

        await AssignAsync(client, new { roles = new[] { new { prefix = string.Empty, organization = "octo" } } }, TestContext.Current.CancellationToken);

        (CiTokenSecret secret, OrgSecretVisibility? visibility, _, string value) = factory.Secrets.Sets.Single();
        secret.ShouldBe(new CiTokenSecret("CLAUDE_CODE_OAUTH_TOKEN", CiSecretScope.Organization, "octo"));
        visibility.ShouldBe(OrgSecretVisibility.All);
        value.ShouldBe(SetupTokenScript.Token);
    }

    [Fact]
    public async Task WhenTheSecondOfFourSetsFailsTheRosterHoldsTheThreeThatWereSetAndNamesTheSplit()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        DateOnly before = new(2026, 6, 1);
        await SeedAsync(factory, new RosterEntry(Parse(Previous))
            .ClaimingToken(_automations, before, null).ClaimingKey(_automations, before, null)
            .ClaimingToken(_issueLane, before, null).ClaimingKey(_issueLane, before, null));
        AnswerAll(reader, "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "AUTOMATIONS_CLAUDE_API_KEY", "ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", "ISSUE_LANE_CLAUDE_API_KEY");
        factory.Secrets.SetErrorFor = static secret => secret.Name == "AUTOMATIONS_CLAUDE_API_KEY" ? "gh secret set exited with code 1" : null;
        using HttpClient client = factory.CreateMutatingClient();

        List<string> bodies = await AssignAsync(client, _bothRoles, TestContext.Current.CancellationToken);

        factory.Secrets.Sets.Count.ShouldBe(4);
        Roster roster = await RosterAsync(factory);
        roster.Find(Parse(Email))!.CiRoles.Select(static held => (held.Role, held.HasToken, held.HasKey)).ShouldBe([(_automations, true, false), (_issueLane, true, true)]);
        roster.Find(Parse(Previous))!.CiRoles.ShouldBeEmpty();
        string message = JsonNode.Parse(bodies[^1])!["message"]!.GetValue<string>();
        message.ShouldContain("AUTOMATIONS key (AUTOMATIONS_CLAUDE_API_KEY in org octo): gh secret set exited with code 1");
        message.ShouldContain("split: token from " + Email + ", key from " + Previous);
    }

    [Fact]
    public async Task ARateLimitStopsEveryWriteAfterIt()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "AUTOMATIONS_CLAUDE_API_KEY", "ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", "ISSUE_LANE_CLAUDE_API_KEY");
        factory.Secrets.SetError = GhSecretWriter.RateLimitMessage;
        using HttpClient client = factory.CreateMutatingClient();

        List<string> bodies = await AssignAsync(client, _bothRoles, TestContext.Current.CancellationToken);

        factory.Secrets.Sets.Count.ShouldBe(1);
        JsonNode.Parse(bodies[^1])!["message"]!.GetValue<string>().ShouldContain("an earlier write hit GitHub's rate limit");
        (await RosterAsync(factory)).Find(Parse(Email))!.CiRoles.ShouldBeEmpty();
    }

    [Fact]
    public async Task PushSendsTheStoredKeyToTheKeyHalvesTheAccountBacksWithNoSetupToken()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        CiRole legacy = new(string.Empty, CiSecretScope.Organization, "octo");
        await SeedAsync(factory, held.ClaimingToken(_automations, new DateOnly(2026, 6, 1), null).ClaimingToken(legacy, new DateOnly(2026, 6, 1), null));
        reader.Answers["orgs/octo/actions/secrets/AUTOMATIONS_CLAUDE_API_KEY"] = PathReader.Secret("AUTOMATIONS_CLAUDE_API_KEY", "all");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await client.PostAsync(Account(Email, "/api-key/push"), content: null, TestContext.Current.CancellationToken);
        string body = await pushed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        factory.SetupToken.Children.ShouldBeEmpty();
        (CiTokenSecret secret, OrgSecretVisibility? visibility, _, string value) = factory.Secrets.Sets.Single();
        secret.Name.ShouldBe("AUTOMATIONS_CLAUDE_API_KEY");
        visibility.ShouldBe(OrgSecretVisibility.All);
        value.ShouldBe(PlantedKey);
        body.ShouldNotContain(PlantedKey);
        (await RosterAsync(factory)).Find(Parse(Email))!.CiRoles.Single(role => role.Role.Equals(_automations)).HasKey.ShouldBeTrue();
        string history = await File.ReadAllTextAsync(Path.Combine(factory.AppData, LaneHistoryFile.FileName), TestContext.Current.CancellationToken);
        history.ShouldContain("\"half\":\"key\"");
        history.ShouldContain("\"via\":\"push\"");
    }

    [Fact]
    public async Task PushIsRefusedForAnAccountThatBacksNoKeyHalf()
    {
        (AppFactory factory, _) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await client.PostAsync(Account(Email, "/api-key/push"), content: null, TestContext.Current.CancellationToken);

        pushed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheTokenAndTheKeyAppearInNoResponseLogFileOrArgumentList()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "AUTOMATIONS_CLAUDE_API_KEY", "ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", "ISSUE_LANE_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        List<string> bodies = await AssignAsync(client, _bothRoles, TestContext.Current.CancellationToken);
        using (HttpResponseMessage pushed = await client.PostAsync(Account(Email, "/api-key/push"), content: null, TestContext.Current.CancellationToken))
        {
            bodies.Add(await pushed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        bodies.Add(await client.GetStringAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken));
        bodies.Add(await client.GetStringAsync(new Uri("/api/lane-history", UriKind.Relative), TestContext.Current.CancellationToken));
        factory.Secrets.Sets.Count.ShouldBe(6);
        // Every file the run left, but the instance lock, which the host holds open and which holds only its own token.
        string[] files = [.. Directory.EnumerateFiles(factory.Root, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetFileName(path) != "instance.lock")
            .Select(File.ReadAllText)];
        string[] arguments = [.. factory.Secrets.Sets.Select(static set => string.Join(' ', GhSecretWriter.Arguments(set.Secret, set.Visibility, set.Repositories)))];
        foreach (string planted in new[] { SetupTokenScript.Token, PlantedKey })
        {
            bodies.ShouldAllBe(body => !body.Contains(planted, StringComparison.Ordinal));
            factory.Logs.Lines.ShouldAllBe(line => !line.Contains(planted, StringComparison.Ordinal));
            files.ShouldAllBe(text => !text.Contains(planted, StringComparison.Ordinal));
            arguments.ShouldAllBe(text => !text.Contains(planted, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Answers each path from a table and records every path asked for; a path
    /// with no answer fails the read. The bodies follow GitHub's documented
    /// shapes for "Get an organization secret" and "List selected repositories
    /// for an organization secret":
    /// https://docs.github.com/en/rest/actions/secrets#get-an-organization-secret.
    /// </summary>
    private sealed class PathReader : IGhApiReader
    {
        public Dictionary<string, Result<JsonNode, GhApiFailure>> Answers { get; } = new(StringComparer.Ordinal);

        public List<string> Paths { get; } = [];

        public static Result<JsonNode, GhApiFailure> Secret(string name, string visibility) => Result<JsonNode, GhApiFailure>.Success(new JsonObject
        {
            ["name"] = name,
            ["created_at"] = "2026-01-01T00:00:00Z",
            ["updated_at"] = "2026-01-01T00:00:00Z",
            ["visibility"] = visibility,
            ["selected_repositories_url"] = "https://api.github.com/orgs/octo/actions/secrets/" + name + "/repositories",
        });

        public static Result<JsonNode, GhApiFailure> Repositories(params string[] names) => Result<JsonNode, GhApiFailure>.Success(new JsonObject
        {
            ["total_count"] = names.Length,
            ["repositories"] = new JsonArray([.. names.Select(static name => (JsonNode)new JsonObject { ["name"] = name, ["full_name"] = "octo/" + name })]),
        });

        public static Result<JsonNode, GhApiFailure> NotFound() =>
            Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 404", RateLimited: false, NotFound: true));

        public Task<Result<JsonNode, GhApiFailure>> GetAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(Answers.TryGetValue(path, out Result<JsonNode, GhApiFailure> answer)
                ? answer
                : Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("no answer scripted for " + path, RateLimited: false)));
        }
    }

    /// <summary>The OS store as a dictionary that always answers.</summary>
    private sealed class KeyStore : ISecretStore
    {
        private readonly Dictionary<(SecretKind, string), string> _items = [];

        public Task<Result<Unit, string>> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Result<Unit, string>.Success(Unit.Value));

        public Task<Result<Unit, string>> WriteAsync(SecretKind kind, AccountEmail email, string value, CancellationToken cancellationToken)
        {
            _items[(kind, email.Value)] = value;
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }

        public Task<Result<string?, string>> ReadAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken) =>
            Task.FromResult(Result<string?, string>.Success(_items.GetValueOrDefault((kind, email.Value))));

        public Task<Result<Unit, string>> DeleteAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken)
        {
            _items.Remove((kind, email.Value));
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }
    }
}
