using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Switching;
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

    private static readonly CiRole _prUnblock = new("PR_UNBLOCK", CiSecretScope.Organization, "octo");
    private static readonly CiRole _prVerify = new("PR_VERIFY", CiSecretScope.Organization, "octo");
    private static readonly object _bothRoles = new
    {
        roles = new[] { new { prefix = "PR_UNBLOCK", organization = "octo" }, new { prefix = "PR_VERIFY", organization = "octo" } },
    };

    private static Uri Account(string email, string path) => new("/api/accounts/" + Uri.EscapeDataString(email) + path, UriKind.Relative);

    private static AccountEmail Parse(string email) => AccountEmail.Parse(email).Value;

    /// <summary>A rostered account with a mapped browser and, when asked, its Console key stored.</summary>
    private static async Task<(AppFactory Factory, PathReader Reader)> StartAsync(bool withKey, CancellationToken cancellationToken, bool withCatalog = false)
    {
        AppFactory factory = new();
        if (withCatalog)
        {
            JsonObject configuration = JsonNode.Parse(await File.ReadAllTextAsync(factory.ConfigPath, cancellationToken))!.AsObject();
            configuration["laneCatalog"] = new JsonObject { ["repository"] = "octo/lanes", ["path"] = "lane-funding.json" };
            await File.WriteAllTextAsync(factory.ConfigPath, configuration.ToJsonString(), cancellationToken);
        }

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

    /// <summary>The owner's answer that both cost settings are off, as the page sends it (issue #317 item 1).</summary>
    private static JsonObject BothOff() => new() { ["autoReload"] = "off", ["extraUsage"] = "off" };

    /// <summary><paramref name="body"/> with the owner's both-off answer added, unless it carries its own.</summary>
    private static JsonObject Confirmed(object? body)
    {
        JsonObject node = body is null ? [] : System.Text.Json.JsonSerializer.SerializeToNode(body)!.AsObject();
        if (!node.ContainsKey("costSettings"))
        {
            node["costSettings"] = BothOff();
        }

        return node;
    }

    /// <summary>Push API key with the owner's both-off answer, and the roles when given.</summary>
    private static Task<HttpResponseMessage> PushAsync(HttpClient client, object? body = null) =>
        client.PostAsJsonAsync(Account(Email, "/api-key/push"), Confirmed(body), TestContext.Current.CancellationToken);

    /// <summary>Starts the session, pastes the code, and returns every body the page got back, the last one the session's end.</summary>
    private static async Task<List<string>> AssignAsync(HttpClient client, object body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage started = await client.PostAsJsonAsync(Account(Email, "/ci-token"), Confirmed(body), cancellationToken);
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

    private const string CatalogPath = "repos/octo/lanes/contents/lane-funding.json";

    // AUTOMATIONS says keyed:false and no lane reads its key; REVIEWS says
    // keyed:true. The fixed list says the opposite of both, so a test that
    // passes here follows the catalog.
    private const string CatalogJson = """
        { "version": 1, "org": "octo", "roles": [
          { "id": "reviews", "secretPrefix": "REVIEWS", "tokenSecret": "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", "keySecret": "REVIEWS_CLAUDE_API_KEY",
            "keyed": true, "category": "review", "lanes": [ { "id": "pr-review", "workflow": "pr-review-hosted.yml", "launcher": "run-claude-cli", "currentSecrets": [] } ] },
          { "id": "automations", "secretPrefix": "AUTOMATIONS", "tokenSecret": "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "keySecret": "AUTOMATIONS_CLAUDE_API_KEY",
            "keyed": false, "category": "maintenance", "lanes": [ { "id": "judge", "workflow": "j.yml", "launcher": "claude-code-action", "currentSecrets": ["AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"] } ] } ] }
        """;

    /// <summary>The catalog as "Get repository content" answers for a file: https://docs.github.com/en/rest/repos/contents#get-repository-content.</summary>
    private static void AnswerCatalog(PathReader reader) =>
        reader.Answers[CatalogPath] = Result<JsonNode, GhApiFailure>.Success(new JsonObject
        {
            ["type"] = "file",
            ["encoding"] = "base64",
            ["content"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(CatalogJson)),
        });

    private static readonly object _reviewsAndAutomations = new
    {
        roles = new[] { new { prefix = "REVIEWS", organization = "octo" }, new { prefix = "AUTOMATIONS", organization = "octo" } },
    };

    [Fact]
    public async Task WithACatalogItDecidesWhichRolesGetAKey()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        AnswerAll(reader, "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", "REVIEWS_CLAUDE_API_KEY", "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN");
        using HttpClient client = factory.CreateMutatingClient();

        await AssignAsync(client, _reviewsAndAutomations, TestContext.Current.CancellationToken);

        factory.Secrets.Sets.Select(static set => (set.Secret.Name, set.Value)).ShouldBe(
        [
            ("REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", SetupTokenScript.Token),
            ("REVIEWS_CLAUDE_API_KEY", PlantedKey),
            ("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", SetupTokenScript.Token),
        ]);
        reader.Paths.ShouldContain(CatalogPath);
    }

    [Fact]
    public async Task PushFollowsTheCatalog()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        AnswerAll(reader, "REVIEWS_CLAUDE_API_KEY", "AUTOMATIONS_CLAUDE_API_KEY");
        DateOnly before = new(2026, 6, 1);
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held
            .ClaimingToken(new CiRole("REVIEWS", CiSecretScope.Organization, "octo"), before, null)
            .ClaimingToken(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"), before, null));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client);

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Secrets.Sets.Select(static set => set.Secret.Name).ShouldBe(["REVIEWS_CLAUDE_API_KEY"]);
    }

    // A configured catalog that cannot be read refuses before any token or key
    // moves, rather than guessing which roles are keyed.
    [Fact]
    public async Task AnUnreadableCatalogRefusesGenerateAndPush()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        reader.Answers[CatalogPath] = Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 404", RateLimited: false, Status: 404));
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "PR_UNBLOCK_CLAUDE_API_KEY");
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held.ClaimingToken(_prUnblock, new DateOnly(2026, 6, 1), null));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage started = await client.PostAsJsonAsync(Account(Email, "/ci-token"), new { roles = new[] { new { prefix = "PR_UNBLOCK", organization = "octo" } } }, TestContext.Current.CancellationToken);
        using HttpResponseMessage pushed = await PushAsync(client);

        started.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("no lane catalog at lane-funding.json in octo/lanes");
        pushed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await pushed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("no lane catalog at lane-funding.json in octo/lanes");
        factory.SetupToken.Children.ShouldBeEmpty();
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    // A catalog held from an earlier read does not outlive a refresh that
    // failed: writes refuse until a read succeeds.
    [Fact]
    public async Task AFailedRefreshRefusesWritesUntilAReadSucceeds()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        AnswerAll(reader, "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", "REVIEWS_CLAUDE_API_KEY");
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held.ClaimingToken(new CiRole("REVIEWS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 6, 1), null));
        using HttpClient client = factory.CreateMutatingClient();
        Uri refresh = new("/api/lane-catalog/refresh", UriKind.Relative);
        using (HttpResponseMessage loaded = await client.PostAsync(refresh, content: null, TestContext.Current.CancellationToken))
        {
            loaded.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        reader.Answers[CatalogPath] = Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 502", RateLimited: false, Status: 502));
        using HttpResponseMessage failed = await client.PostAsync(refresh, content: null, TestContext.Current.CancellationToken);
        using HttpResponseMessage started = await client.PostAsJsonAsync(Account(Email, "/ci-token"), new { roles = new[] { new { prefix = "REVIEWS", organization = "octo" } } }, TestContext.Current.CancellationToken);
        using HttpResponseMessage pushed = await PushAsync(client);

        failed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        started.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("HTTP 502");
        pushed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        factory.SetupToken.Children.ShouldBeEmpty();
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    // Every card view an endpoint answers with keys roles as the dashboard does.
    [Fact]
    public async Task AnEditAnswersWithTheCatalogsKeyedRoles()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held
            .ClaimingToken(new CiRole("REVIEWS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 6, 1), null)
            .ClaimingToken(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 6, 1), null));
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage loaded = await client.PostAsync(new Uri("/api/lane-catalog/refresh", UriKind.Relative), content: null, TestContext.Current.CancellationToken))
        {
            loaded.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage patched = await client.PatchAsJsonAsync(Account(Email, string.Empty), new { alias = "renamed" }, TestContext.Current.CancellationToken);

        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject view = (await patched.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        view["ciRoles"]!.AsArray().Select(static role => (role!["prefix"]!.GetValue<string>(), role["keySecret"]?.GetValue<string>()))
            .ShouldBe([("REVIEWS", "REVIEWS_CLAUDE_API_KEY"), ("AUTOMATIONS", null)]);
    }

    [Fact]
    public async Task TheCatalogIsReadOnlyOnRefreshAndThenServedAsRead()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        using HttpClient client = factory.CreateMutatingClient();
        Uri catalog = new("/api/lane-catalog", UriKind.Relative);

        JsonObject before = (await client.GetFromJsonAsync<JsonObject>(catalog, TestContext.Current.CancellationToken))!;
        using HttpResponseMessage refreshed = await client.PostAsync(new Uri("/api/lane-catalog/refresh", UriKind.Relative), content: null, TestContext.Current.CancellationToken);
        JsonObject after = (await client.GetFromJsonAsync<JsonObject>(catalog, TestContext.Current.CancellationToken))!;
        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken))!;

        before["configured"]!.GetValue<bool>().ShouldBeTrue();
        before["roles"]!.AsArray().ShouldBeEmpty();
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        after["organization"]!.GetValue<string>().ShouldBe("octo");
        after["roles"]!.AsArray().Select(static role => (role!["prefix"]!.GetValue<string>(), role["keyed"]!.GetValue<bool>(), role["keySecret"]?.GetValue<string>()))
            .ShouldBe([("REVIEWS", true, "REVIEWS_CLAUDE_API_KEY"), ("AUTOMATIONS", false, null)]);
        reader.Paths.Count(static path => path == CatalogPath).ShouldBe(1);
        dashboard["tokenOnlyRolePrefixes"]!.AsArray().Select(static prefix => prefix!.GetValue<string>()).ShouldBe(["AUTOMATIONS"]);
        dashboard["laneCatalog"]!["roles"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public async Task WithNoCatalogConfiguredTheViewSaysSoAndNothingIsRead()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        using HttpClient client = factory.CreateMutatingClient();

        JsonObject view = (await client.GetFromJsonAsync<JsonObject>(new Uri("/api/lane-catalog", UriKind.Relative), TestContext.Current.CancellationToken))!;
        using HttpResponseMessage refreshed = await client.PostAsync(new Uri("/api/lane-catalog/refresh", UriKind.Relative), content: null, TestContext.Current.CancellationToken);

        view["configured"]!.GetValue<bool>().ShouldBeFalse();
        refreshed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        reader.Paths.ShouldBeEmpty();
    }

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
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "PR_UNBLOCK_CLAUDE_API_KEY", "PR_VERIFY_CLAUDE_CODE_OAUTH_TOKEN", "PR_VERIFY_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        List<string> bodies = await AssignAsync(client, _bothRoles, TestContext.Current.CancellationToken);

        JsonNode.Parse(bodies[^1])!["state"]!.GetValue<string>().ShouldBe("Completed");
        factory.SetupToken.Children.Count.ShouldBe(1);
        factory.Secrets.Sets.Select(static set => (set.Secret.Name, set.Value)).ShouldBe(
        [
            ("PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", SetupTokenScript.Token),
            ("PR_UNBLOCK_CLAUDE_API_KEY", PlantedKey),
            ("PR_VERIFY_CLAUDE_CODE_OAUTH_TOKEN", SetupTokenScript.Token),
            ("PR_VERIFY_CLAUDE_API_KEY", PlantedKey),
        ]);
        RosterEntry entry = (await RosterAsync(factory)).Find(Parse(Email))!;
        entry.CiRoles.Select(static held => (held.Role, held.HasToken, held.HasKey)).ShouldBe([(_prUnblock, true, true), (_prVerify, true, true)]);
    }

    [Fact]
    public async Task EachSecretIsSetAgainWithWhoMayReadItAndANewOneIsVisibleToAll()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        reader.Answers["orgs/octo/actions/secrets/PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN"] = PathReader.Secret("PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "selected");
        reader.Answers["orgs/octo/actions/secrets/PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN/repositories?per_page=100&page=1"] = PathReader.Repositories("a", "b");
        reader.Answers["orgs/octo/actions/secrets/PR_UNBLOCK_CLAUDE_API_KEY"] = PathReader.Secret("PR_UNBLOCK_CLAUDE_API_KEY", "private");
        reader.Answers["orgs/octo/actions/secrets/PR_VERIFY_CLAUDE_CODE_OAUTH_TOKEN"] = PathReader.Secret("PR_VERIFY_CLAUDE_CODE_OAUTH_TOKEN", "all");
        reader.Answers["orgs/octo/actions/secrets/PR_VERIFY_CLAUDE_API_KEY"] = PathReader.NotFound();
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
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN");
        reader.Answers["orgs/octo/actions/secrets/PR_UNBLOCK_CLAUDE_API_KEY"] =
            Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 403 for that path", RateLimited: false));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage started = await client.PostAsJsonAsync(
            Account(Email, "/ci-token"),
            Confirmed(new { roles = new[] { new { prefix = "PR_UNBLOCK", organization = "octo" } } }),
            TestContext.Current.CancellationToken);

        started.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("HTTP 403");
        factory.SetupToken.Children.ShouldBeEmpty();
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOrganizationSecretNamedWithNoVisibilityKeepsTheOneGitHubHas()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        reader.Answers["orgs/octo/actions/secrets/MY_TOKEN"] = PathReader.Secret("MY_TOKEN", "selected");
        reader.Answers["orgs/octo/actions/secrets/MY_TOKEN/repositories?per_page=100&page=1"] = PathReader.Repositories("a", "b");
        using HttpClient client = factory.CreateMutatingClient();

        await AssignAsync(client, new { secretName = "MY_TOKEN", organization = "octo" }, TestContext.Current.CancellationToken);

        (CiTokenSecret secret, OrgSecretVisibility? visibility, IReadOnlyList<string> repositories, _) = factory.Secrets.Sets.Single();
        string.Join(' ', GhSecretWriter.Arguments(secret, visibility, repositories)).ShouldEndWith("--org octo --repos a,b");
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
    public async Task ARoleWithAKeyHalfIsRefusedBeforeSetupTokenWhenTheStoreHasLostTheKeyTheRosterRecords()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        RosterEntry entry = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, entry with { ConsoleApiKeySetOn = new DateOnly(2026, 9, 1) });
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage started = await client.PostAsJsonAsync(Account(Email, "/ci-token"), _bothRoles, TestContext.Current.CancellationToken);

        started.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("paste this account's Console key first");
        factory.SetupToken.Children.ShouldBeEmpty();
        reader.Paths.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("MY_TOKEN", "--visibility private")]
    [InlineData("PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "--visibility all")]
    public async Task ANewSecretNamedByTheOldBodyIsPrivateUnlessItIsARoles(string name, string flags)
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        reader.Answers["orgs/octo/actions/secrets/" + name] = PathReader.NotFound();
        using HttpClient client = factory.CreateMutatingClient();

        await AssignAsync(client, new { secretName = name, organization = "octo" }, TestContext.Current.CancellationToken);

        (CiTokenSecret secret, OrgSecretVisibility? visibility, IReadOnlyList<string> repositories, _) = factory.Secrets.Sets.Single();
        string.Join(' ', GhSecretWriter.Arguments(secret, visibility, repositories)).ShouldEndWith("--org octo " + flags);
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

    // No role, prefix, case or extra field lets a Claude role secret into one repository.
    [Theory]
    [InlineData("""{"roles":[{"prefix":"INTAKE","repository":"octo/repo"}]}""")]
    [InlineData("""{"roles":[{"prefix":"","repository":"octo/repo"}]}""")]
    [InlineData("""{"roles":[{"tokenSecret":"reviews_claude_code_oauth_token","repository":"octo/repo"}],"force":true}""")]
    [InlineData("""{"roles":[{"prefix":"PR_VERIFY","organization":"octo"},{"prefix":"PR_UNBLOCK","repository":"octo/repo","allowRepoScope":true}]}""")]
    [InlineData("""{"roles":[{"tokenSecret":"LANE_TOKEN","repository":"octo/repo"}]}""")]
    public async Task ARoleInOneRepositoryIsRefusedBeforeAnythingStarts(string json)
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage started = await client.PostAsync(Account(Email, "/ci-token"), content, TestContext.Current.CancellationToken);

        started.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await started.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["error"]!.GetValue<string>()
            .ShouldBe("roles: " + CiTokenSecret.RepositorySecretRefusal);
        factory.SetupToken.Children.ShouldBeEmpty();
        reader.Paths.ShouldBeEmpty();
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    // Issue #259: the roles whose lanes run on claude-code-action read a token
    // only, so no Console key is asked for, read, or written for them.
    [Theory]
    [InlineData("REVIEWS", false)]
    [InlineData("REVIEWS_SECURITY", false)]
    [InlineData("ISSUE_LANE", true)]
    [InlineData("REVIEWS", true)]
    public async Task ATokenOnlyRoleNeedsNoKeyAndGetsOnlyItsToken(string prefix, bool withKey)
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, prefix + "_CLAUDE_CODE_OAUTH_TOKEN");
        using HttpClient client = factory.CreateMutatingClient();

        List<string> bodies = await AssignAsync(client, new { roles = new[] { new { prefix, organization = "octo" } } }, TestContext.Current.CancellationToken);

        JsonNode.Parse(bodies[^1])!["state"]!.GetValue<string>().ShouldBe("Completed");
        factory.Secrets.Sets.Select(static set => (set.Secret.Name, set.Value)).ShouldBe([(prefix + "_CLAUDE_CODE_OAUTH_TOKEN", SetupTokenScript.Token)]);
        reader.Paths.ShouldBe(["orgs/octo/actions/secrets/" + prefix + "_CLAUDE_CODE_OAUTH_TOKEN"]);
        (await RosterAsync(factory)).Find(Parse(Email))!.CiRoles.Select(static held => (held.Role.Prefix, held.HasToken, held.HasKey)).ShouldBe([(prefix, true, false)]);
    }

    [Fact]
    public async Task PushSkipsATokenOnlyRoleTheAccountRecordsAKeyFor()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        DateOnly before = new(2026, 6, 1);
        CiRole reviews = new("REVIEWS", CiSecretScope.Organization, "octo");
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held.ClaimingToken(reviews, before, null).ClaimingKey(reviews, before, null).ClaimingToken(_prUnblock, before, null));
        AnswerAll(reader, "REVIEWS_CLAUDE_API_KEY", "PR_UNBLOCK_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client);

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Secrets.Sets.Select(static set => set.Secret.Name).ShouldBe(["PR_UNBLOCK_CLAUDE_API_KEY"]);
    }

    [Fact]
    public async Task PushIsRefusedForAnAccountThatBacksOnlyTokenOnlyRoles()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held.ClaimingToken(new CiRole("REVIEWS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 6, 1), null));
        AnswerAll(reader, "REVIEWS_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client);

        pushed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await pushed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("backs no role with a key half");
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    // A roster an earlier release wrote may hold a role in one repository; Push
    // reads it but sends that role nothing.
    [Fact]
    public async Task PushSendsNothingToARoleInOneRepositoryAnEarlierRosterHolds()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        DateOnly before = new(2026, 6, 1);
        CiRole inRepository = new("PR_UNBLOCK", CiSecretScope.Repository, "octo/one");
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held.ClaimingToken(inRepository, before, null).ClaimingKey(inRepository, before, null).ClaimingToken(_prVerify, before, null));
        AnswerAll(reader, "PR_VERIFY_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client);

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Secrets.Sets.Select(static set => set.Secret.ToString()).ShouldBe(["PR_VERIFY_CLAUDE_API_KEY in org octo"]);
        (await RosterAsync(factory)).Find(Parse(Email))!.CiRoles.Single(role => role.Role.Equals(inRepository)).KeySetOn.ShouldBe(before);
    }

    [Fact]
    public async Task WhenTheSecondOfFourSetsFailsTheRosterHoldsTheThreeThatWereSetAndNamesTheSplit()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        DateOnly before = new(2026, 6, 1);
        await SeedAsync(factory, new RosterEntry(Parse(Previous))
            .ClaimingToken(_prUnblock, before, null).ClaimingKey(_prUnblock, before, null)
            .ClaimingToken(_prVerify, before, null).ClaimingKey(_prVerify, before, null));
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "PR_UNBLOCK_CLAUDE_API_KEY", "PR_VERIFY_CLAUDE_CODE_OAUTH_TOKEN", "PR_VERIFY_CLAUDE_API_KEY");
        factory.Secrets.SetErrorFor = static secret => secret.Name == "PR_UNBLOCK_CLAUDE_API_KEY" ? "gh secret set exited with code 1" : null;
        using HttpClient client = factory.CreateMutatingClient();

        List<string> bodies = await AssignAsync(client, _bothRoles, TestContext.Current.CancellationToken);

        factory.Secrets.Sets.Count.ShouldBe(4);
        Roster roster = await RosterAsync(factory);
        roster.Find(Parse(Email))!.CiRoles.Select(static held => (held.Role, held.HasToken, held.HasKey)).ShouldBe([(_prUnblock, true, false), (_prVerify, true, true)]);
        // The key secret still holds the previous holder's key, so that half stays theirs.
        roster.Find(Parse(Previous))!.CiRoles.ShouldBe([new CiRoleAssignment(_prUnblock, KeySetOn: before)]);
        string message = JsonNode.Parse(bodies[^1])!["message"]!.GetValue<string>();
        message.ShouldContain("PR_UNBLOCK key (PR_UNBLOCK_CLAUDE_API_KEY in org octo): gh secret set exited with code 1");
        message.ShouldContain("split: token from " + Email + ", key from " + Previous);
    }

    [Fact]
    public async Task AFullAssignmentByANewAccountTakesBothHalvesOffThePreviousHolder()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        DateOnly before = new(2026, 6, 1);
        await SeedAsync(factory, new RosterEntry(Parse(Previous)).ClaimingToken(_prUnblock, before, null).ClaimingKey(_prUnblock, before, null));
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "PR_UNBLOCK_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        await AssignAsync(client, new { roles = new[] { new { prefix = "PR_UNBLOCK", organization = "octo" } } }, TestContext.Current.CancellationToken);

        Roster roster = await RosterAsync(factory);
        roster.Find(Parse(Previous))!.CiRoles.ShouldBeEmpty();
        roster.Find(Parse(Email))!.CiRoles.Select(static held => (held.Role, held.HasToken, held.HasKey)).ShouldBe([(_prUnblock, true, true)]);
    }

    [Fact]
    public async Task PushFromTheKeyHolderOfASplitRoleLeavesTheOtherAccountsTokenHalf()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        DateOnly before = new(2026, 6, 1);
        RosterEntry keyHolder = (await RosterAsync(factory)).Find(Parse(Email))! with { CiRoles = [new CiRoleAssignment(_prUnblock, KeySetOn: before)] };
        RosterEntry tokenHolder = new RosterEntry(Parse(Previous)).ClaimingToken(_prUnblock, before, null);
        await factory.Services.GetRequiredService<RosterFile>().UpdateAsync(_ => new Roster([keyHolder, tokenHolder]), TestContext.Current.CancellationToken);
        string history = Path.Combine(factory.AppData, LaneHistoryFile.FileName);
        File.Delete(history);
        reader.Answers["orgs/octo/actions/secrets/PR_UNBLOCK_CLAUDE_API_KEY"] = PathReader.Secret("PR_UNBLOCK_CLAUDE_API_KEY", "all");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client);

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Secrets.Sets.Single().Secret.Name.ShouldBe("PR_UNBLOCK_CLAUDE_API_KEY");
        Roster roster = await RosterAsync(factory);
        roster.Find(Parse(Previous))!.CiRoles.ShouldBe([new CiRoleAssignment(_prUnblock, before)]);
        roster.Find(Parse(Email))!.CiRoles.Single().HasKey.ShouldBeTrue();
        // The key stayed with the same account and the token never moved, so no half changed hands.
        (File.Exists(history) ? await File.ReadAllTextAsync(history, TestContext.Current.CancellationToken) : string.Empty).ShouldNotContain("\"half\":\"token\"");
    }

    [Fact]
    public async Task TheOldBodyNamingARolesTokenSecretLeavesTheOtherAccountItsKeyHalf()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        DateOnly before = new(2026, 6, 1);
        await SeedAsync(factory, new RosterEntry(Parse(Previous)).ClaimingToken(_prUnblock, before, null).ClaimingKey(_prUnblock, before, null));
        using HttpClient client = factory.CreateMutatingClient();

        await AssignAsync(client, new { secretName = "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", organization = "octo", visibility = "all" }, TestContext.Current.CancellationToken);

        Roster roster = await RosterAsync(factory);
        roster.Find(Parse(Previous))!.CiRoles.ShouldBe([new CiRoleAssignment(_prUnblock, KeySetOn: before)]);
        roster.Find(Parse(Email))!.CiRoles.Select(static held => (held.Role, held.HasToken, held.HasKey)).ShouldBe([(_prUnblock, true, false)]);
    }

    [Fact]
    public async Task TheEditRepairNamingARolesTokenSecretLeavesTheOtherAccountItsKeyHalf()
    {
        (AppFactory factory, _) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        DateOnly before = new(2026, 6, 1);
        await SeedAsync(factory, new RosterEntry(Parse(Previous)).ClaimingToken(_prUnblock, before, null).ClaimingKey(_prUnblock, before, null));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage patched = await client.PatchAsJsonAsync(
            Account(Email, string.Empty),
            new { ciTokenGeneratedOn = "2026-09-01", ciTokenSecret = new { name = "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", organization = "octo" } },
            TestContext.Current.CancellationToken);

        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        Roster roster = await RosterAsync(factory);
        roster.Find(Parse(Previous))!.CiRoles.ShouldBe([new CiRoleAssignment(_prUnblock, KeySetOn: before)]);
        roster.Find(Parse(Email))!.CiRoles.Single().HasToken.ShouldBeTrue();
    }

    [Fact]
    public async Task AnEditThatSendsNoRolesKeepsTheRolesTheRosterHolds()
    {
        (AppFactory factory, _) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        DateOnly before = new(2026, 6, 1);
        RosterEntry entry = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, entry.ClaimingToken(_prUnblock, before, null));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage patched = await client.PatchAsJsonAsync(Account(Email, string.Empty), new { alias = "renamed" }, TestContext.Current.CancellationToken);

        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        RosterEntry after = (await RosterAsync(factory)).Find(Parse(Email))!;
        after.Alias.ShouldBe("renamed");
        after.CiRoles.ShouldBe([new CiRoleAssignment(_prUnblock, before)]);
    }

    [Fact]
    public async Task ARateLimitStopsEveryWriteAfterIt()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "PR_UNBLOCK_CLAUDE_API_KEY", "PR_VERIFY_CLAUDE_CODE_OAUTH_TOKEN", "PR_VERIFY_CLAUDE_API_KEY");
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
        await SeedAsync(factory, held.ClaimingToken(_prUnblock, new DateOnly(2026, 6, 1), null).ClaimingToken(legacy, new DateOnly(2026, 6, 1), null));
        reader.Answers["orgs/octo/actions/secrets/PR_UNBLOCK_CLAUDE_API_KEY"] = PathReader.Secret("PR_UNBLOCK_CLAUDE_API_KEY", "all");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client);
        string body = await pushed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        factory.SetupToken.Children.ShouldBeEmpty();
        (CiTokenSecret secret, OrgSecretVisibility? visibility, _, string value) = factory.Secrets.Sets.Single();
        secret.Name.ShouldBe("PR_UNBLOCK_CLAUDE_API_KEY");
        visibility.ShouldBe(OrgSecretVisibility.All);
        value.ShouldBe(PlantedKey);
        body.ShouldNotContain(PlantedKey);
        (await RosterAsync(factory)).Find(Parse(Email))!.CiRoles.Single(role => role.Role.Equals(_prUnblock)).HasKey.ShouldBeTrue();
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

        using HttpResponseMessage pushed = await PushAsync(client);

        pushed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheTokenAndTheKeyAppearInNoResponseLogFileOrArgumentList()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "PR_UNBLOCK_CLAUDE_API_KEY", "PR_VERIFY_CLAUDE_CODE_OAUTH_TOKEN", "PR_VERIFY_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        List<string> bodies = await AssignAsync(client, _bothRoles, TestContext.Current.CancellationToken);
        using (HttpResponseMessage pushed = await PushAsync(client))
        {
            bodies.Add(await pushed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        bodies.Add(await client.GetStringAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken));
        bodies.Add(await client.GetStringAsync(new Uri("/api/lane-history", UriKind.Relative), TestContext.Current.CancellationToken));
        factory.Secrets.Sets.Count.ShouldBe(6);
        // Every file the run left, but the instance lock, which the host holds open and which holds only its own token.
        string[] files = [.. Directory.EnumerateFiles(factory.Root, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetFileName(path) != InstanceLock.FileName)
            .Select(ReadShared)];
        string[] arguments = [.. factory.Secrets.Sets.Select(static set => string.Join(' ', GhSecretWriter.Arguments(set.Secret, set.Visibility, set.Repositories)))];
        foreach (string planted in new[] { SetupTokenScript.Token, PlantedKey })
        {
            bodies.ShouldAllBe(body => !body.Contains(planted, StringComparison.Ordinal));
            factory.Logs.Lines.ShouldAllBe(line => !line.Contains(planted, StringComparison.Ordinal));
            files.ShouldAllBe(text => !text.Contains(planted, StringComparison.Ordinal));
            arguments.ShouldAllBe(text => !text.Contains(planted, StringComparison.Ordinal));
        }
    }

    // Issue #317 item 1: a key goes live only once the owner has said both
    // auto-reload and extra usage are off; no answer, or half of one, is unknown.
    [Theory]
    [InlineData(null)]
    [InlineData("""{}""")]
    [InlineData("""{"costSettings":{"autoReload":"off"}}""")]
    [InlineData("""{"costSettings":{"autoReload":"off","extraUsage":"maybe"}}""")]
    public async Task PushIsRefusedWhileTheCostSettingsAreUnknown(string? json)
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held.ClaimingToken(_prVerify, new DateOnly(2026, 6, 1), null));
        AnswerAll(reader, "PR_VERIFY_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        using StringContent? content = json is null ? null : new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage pushed = await client.PostAsync(Account(Email, "/api-key/push"), content, TestContext.Current.CancellationToken);

        pushed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await pushed.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("CostSettingsUnconfirmed");
        factory.Secrets.Sets.ShouldBeEmpty();
        reader.Paths.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("on", "off")]
    [InlineData("off", "on")]
    [InlineData("ON", "on")]
    public async Task PushIsRefusedWhenTheOwnerSaysACostSettingIsOnAndTheRecordIsCleared(string autoReload, string extraUsage)
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held.ClaimingToken(_prVerify, new DateOnly(2026, 6, 1), null) with { CostSettingsConfirmedOn = new DateOnly(2026, 8, 1) });
        AnswerAll(reader, "PR_VERIFY_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client, new { costSettings = new { autoReload, extraUsage } });

        pushed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await pushed.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("CostSettingOn");
        factory.Secrets.Sets.ShouldBeEmpty();
        (await RosterAsync(factory)).Find(Parse(Email))!.CostSettingsConfirmedOn.ShouldBeNull();
    }

    // The usage response's extra_usage block is the one setting this tool can
    // read; a read that says it is on outranks the owner's answer.
    [Fact]
    public async Task PushIsRefusedWhenTheUsageReadSaysExtraUsageIsOnWhateverTheOwnerAnswered()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        RosterEntry held = (await RosterAsync(factory)).Find(Parse(Email))!;
        await SeedAsync(factory, held.ClaimingToken(_prVerify, new DateOnly(2026, 6, 1), null));
        AnswerAll(reader, "PR_VERIFY_CLAUDE_API_KEY");
        factory.Services.GetRequiredService<ClaudeCodeAccountRotation.App.Quota.QuotaState>().RecordSnapshot(new ClaudeCodeAccountRotation.Core.Quota.UsageSnapshot(
            Parse(Email), factory.Clock.GetUtcNow(), ClaudeCodeAccountRotation.Core.Quota.QuotaSource.OnDemandRefresh, [], new ClaudeCodeAccountRotation.Core.Quota.ExtraUsageState(IsEnabled: true, null, SpendLimitReached: false)));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client);

        pushed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await pushed.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("ExtraUsageOn");
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    // Both off, and a usage read that says extra usage is off: the picked role's
    // key secret is set once, in the organization, visible to all repositories,
    // and the day is recorded on the account.
    [Fact]
    public async Task PushToAPickedRoleSetsItsKeySecretOnceWhenBothAreOffAndRecordsTheDay()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        reader.Answers["orgs/octo/actions/secrets/PR_VERIFY_CLAUDE_API_KEY"] = PathReader.NotFound();
        factory.Services.GetRequiredService<ClaudeCodeAccountRotation.App.Quota.QuotaState>().RecordSnapshot(new ClaudeCodeAccountRotation.Core.Quota.UsageSnapshot(
            Parse(Email), factory.Clock.GetUtcNow(), ClaudeCodeAccountRotation.Core.Quota.QuotaSource.OnDemandRefresh, [], new ClaudeCodeAccountRotation.Core.Quota.ExtraUsageState(IsEnabled: false, null, SpendLimitReached: false)));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client, new { roles = new[] { new { prefix = "PR_VERIFY", organization = "octo" } } });
        string body = await pushed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        (CiTokenSecret secret, OrgSecretVisibility? visibility, IReadOnlyList<string> repositories, string value) = factory.Secrets.Sets.Single();
        secret.ToString().ShouldBe("PR_VERIFY_CLAUDE_API_KEY in org octo");
        string.Join(' ', GhSecretWriter.Arguments(secret, visibility, repositories).Skip(5)).ShouldBe("--org octo --visibility all");
        value.ShouldBe(PlantedKey);
        factory.SetupToken.Children.ShouldBeEmpty();
        RosterEntry entry = (await RosterAsync(factory)).Find(Parse(Email))!;
        entry.CiRoles.Select(static held => (held.Role, held.HasToken, held.HasKey)).ShouldBe([(_prVerify, false, true)]);
        entry.CostSettingsConfirmedOn.ShouldBe(new DateOnly(2026, 9, 7));
        body.ShouldNotContain(PlantedKey);
    }

    [Fact]
    public async Task APickedRoleTakesTheKeyHalfOffItsPreviousHolderAndLeavesItsToken()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        DateOnly before = new(2026, 6, 1);
        await SeedAsync(factory, new RosterEntry(Parse(Previous)).ClaimingToken(_prVerify, before, null).ClaimingKey(_prVerify, before, null));
        AnswerAll(reader, "PR_VERIFY_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client, new { roles = new[] { new { prefix = "PR_VERIFY", organization = "octo" } } });

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK);
        Roster roster = await RosterAsync(factory);
        roster.Find(Parse(Previous))!.CiRoles.ShouldBe([new CiRoleAssignment(_prVerify, before)]);
        roster.Find(Parse(Email))!.CiRoles.Single().HasKey.ShouldBeTrue();
    }

    // The picker's roles keep Generate CI token's refusal of one repository,
    // whatever extra field the body carries.
    [Theory]
    [InlineData("""{"roles":[{"prefix":"PR_VERIFY","repository":"octo/repo"}]}""")]
    [InlineData("""{"roles":[{"prefix":"PR_VERIFY","organization":"octo"},{"prefix":"INTAKE","repository":"octo/repo","allowRepoScope":true}],"force":true}""")]
    public async Task APickedRoleInOneRepositoryIsRefusedBeforeAnythingIsRead(string json)
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject body = JsonNode.Parse(json)!.AsObject();
        body["costSettings"] = BothOff();

        using HttpResponseMessage pushed = await client.PostAsJsonAsync(Account(Email, "/api-key/push"), body, TestContext.Current.CancellationToken);

        pushed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await pushed.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["error"]!.GetValue<string>()
            .ShouldBe("roles: " + CiTokenSecret.RepositorySecretRefusal);
        reader.Paths.ShouldBeEmpty();
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    // Only a keyed role takes a key: the catalog's keyed:false AUTOMATIONS, a
    // role the catalog does not list, and, with no catalog, the fixed list's
    // token-only REVIEWS are each refused.
    [Theory]
    [InlineData(true, "AUTOMATIONS", "takes no API key")]
    [InlineData(true, "PR_VERIFY", "is not in the lane catalog")]
    [InlineData(false, "REVIEWS", "takes no API key")]
    public async Task APickedRoleThatTakesNoKeyIsRefused(bool withCatalog, string prefix, string reason)
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken, withCatalog);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        AnswerAll(reader, prefix + "_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client, new { roles = new[] { new { prefix, organization = "octo" } } });

        pushed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await pushed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(reason);
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task APickedKeyedCatalogRoleIsSent()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        AnswerAll(reader, "REVIEWS_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pushed = await PushAsync(client, new { roles = new[] { new { prefix = "REVIEWS", organization = "octo" } } });

        pushed.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Secrets.Sets.Select(static set => set.Secret.Name).ShouldBe(["REVIEWS_CLAUDE_API_KEY"]);
    }

    // Generate CI token puts the key live too when a role is keyed, so it
    // passes the same gate before any secret is read or token made.
    [Theory]
    [InlineData(null)]
    [InlineData("on")]
    public async Task KeyedGenerateIsRefusedBeforeSetupTokenUnlessBothAreOff(string? autoReload)
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        using HttpClient client = factory.CreateMutatingClient();
        object body = autoReload is null
            ? new { roles = new[] { new { prefix = "PR_UNBLOCK", organization = "octo" } } }
            : new { roles = new[] { new { prefix = "PR_UNBLOCK", organization = "octo" } }, costSettings = new { autoReload, extraUsage = "off" } };

        using HttpResponseMessage started = await client.PostAsJsonAsync(Account(Email, "/ci-token"), body, TestContext.Current.CancellationToken);

        started.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await started.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>()
            .ShouldBe(autoReload is null ? "CostSettingsUnconfirmed" : "CostSettingOn");
        factory.SetupToken.Children.ShouldBeEmpty();
        reader.Paths.ShouldBeEmpty();
    }

    // A token-only role sends no key, so it asks no cost question.
    [Fact]
    public async Task TokenOnlyGenerateNeedsNoCostAnswer()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage started = await client.PostAsJsonAsync(Account(Email, "/ci-token"), new { roles = new[] { new { prefix = "REVIEWS", organization = "octo" } } }, TestContext.Current.CancellationToken);

        started.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>A file's text, read beside the host, which may still hold it open.</summary>
    private static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
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
            Result<JsonNode, GhApiFailure>.Failure(new GhApiFailure("GitHub answered HTTP 404", RateLimited: false, Status: 404));

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
