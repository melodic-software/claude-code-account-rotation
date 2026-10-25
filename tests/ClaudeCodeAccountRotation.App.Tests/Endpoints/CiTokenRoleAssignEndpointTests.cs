using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// An account's CI token kept in the OS store beside its Console key, by paste
/// or by Generate CI token, and Assign to role sending either stored value, or
/// both, to one role's secrets with no new sign-in.
/// </summary>
public sealed partial class CiTokenRoleEndpointTests
{
    // Built from pieces, so the source holds nothing a secret scanner would take for a real token.
    private static readonly string _pastedToken = SetupTokenScript.FakeToken('P', 60);

    private static KeyStore StoreOf(AppFactory factory) => (KeyStore)factory.Services.GetRequiredService<ISecretStore>();

    private static Task<HttpResponseMessage> PasteTokenAsync(HttpClient client, string token) =>
        client.PutAsJsonAsync(Account(Email, "/ci-token-value"), new { token }, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> AssignRoleAsync(HttpClient client, object role, string halves) =>
        client.PostAsJsonAsync(Account(Email, "/assign"), Confirmed(new { role, halves }), TestContext.Current.CancellationToken);

    private static async Task<IReadOnlyList<LaneHistoryEntry>> HistoryAsync(AppFactory factory) =>
        await factory.Services.GetRequiredService<RosterFile>().History.ReadAsync(null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task APastedTokenIsKeptInTheStoreAndOnlyItsDayIsAnswered()
    {
        (AppFactory factory, _) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage pasted = await PasteTokenAsync(client, "  " + _pastedToken + "\n");
        string body = await pasted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        pasted.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        StoreOf(factory).Items[(SecretKind.CiToken, Email)].ShouldBe(_pastedToken);
        body.ShouldNotContain(_pastedToken);
        JsonNode.Parse(body)!["roster"]!["ciTokenStoredOn"]!.GetValue<string>().ShouldBe("2026-09-07");
        factory.Logs.Lines.ShouldNotContain(line => line.Contains(_pastedToken, StringComparison.Ordinal));
        // Pasting a token is not giving one to a role: no consoleApiKeySetOn, no role.
        JsonNode.Parse(body)!["roster"]!["consoleApiKeySetOn"].ShouldBeNull();
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingTheStoredTokenRemovesItAndItsDay()
    {
        (AppFactory factory, _) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage pasted = await PasteTokenAsync(client, _pastedToken))
        {
            pasted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage deleted = await client.DeleteAsync(Account(Email, "/ci-token-value"), TestContext.Current.CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
        StoreOf(factory).Items.ShouldNotContainKey((SecretKind.CiToken, Email));
        (await RosterAsync(factory)).Find(Parse(Email))!.CiTokenStoredOn.ShouldBeNull();
    }

    [Theory]
    [InlineData("sk-ant-api03-not-a-setup-token")]
    [InlineData("not a token")]
    public async Task APasteThatIsNotASetupTokenIsRefusedAndNothingIsStored(string pasted)
    {
        (AppFactory factory, _) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await PasteTokenAsync(client, pasted);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        StoreOf(factory).Items.ShouldNotContainKey((SecretKind.CiToken, Email));
    }

    [Fact]
    public async Task GenerateCiTokenKeepsTheTokenItMadeInTheStore()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "PR_UNBLOCK_CLAUDE_API_KEY", "PR_VERIFY_CLAUDE_CODE_OAUTH_TOKEN", "PR_VERIFY_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        List<string> bodies = await AssignAsync(client, _bothRoles, TestContext.Current.CancellationToken);

        StoreOf(factory).Items[(SecretKind.CiToken, Email)].ShouldBe(SetupTokenScript.Token);
        (await RosterAsync(factory)).Find(Parse(Email))!.CiTokenStoredOn.ShouldBe(new DateOnly(2026, 9, 7));
        bodies.ShouldAllBe(body => !body.Contains(SetupTokenScript.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATokenNoSecretTookIsStillKeptSoAssignToRoleCanSendItLater()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN");
        factory.Secrets.SetError = "gh secret set failed (stand-in)";
        using HttpClient client = factory.CreateMutatingClient();

        // REVIEWS is token-only with no catalog, so no Console key is needed.
        List<string> bodies = await AssignAsync(client, new { roles = new[] { new { prefix = "REVIEWS", organization = "octo" } } }, TestContext.Current.CancellationToken);
        JsonNode last = JsonNode.Parse(bodies[^1])!;

        last["state"]!.GetValue<string>().ShouldBe("Failed");
        last["message"]!.GetValue<string>().ShouldContain("Assign to role");
        StoreOf(factory).Items[(SecretKind.CiToken, Email)].ShouldBe(SetupTokenScript.Token);
        (await RosterAsync(factory)).Find(Parse(Email))!.CiTokenStoredOn.ShouldBe(new DateOnly(2026, 9, 7));
    }

    [Fact]
    public async Task AssignSendsBothStoredValuesToOneRoleAndRecordsBothHalves()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "PR_UNBLOCK_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage pasted = await PasteTokenAsync(client, _pastedToken))
        {
            pasted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Ten days on: the token half keeps the day the token was stored, so
        // its expiry is reckoned from when the token was made, not sent.
        factory.Clock.Advance(TimeSpan.FromDays(10));
        using HttpResponseMessage assigned = await AssignRoleAsync(client, new { prefix = "PR_UNBLOCK", organization = "octo" }, "both");
        string body = await assigned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        assigned.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        factory.Secrets.Sets.Select(static set => (set.Secret.Name, set.Visibility, set.Value)).ShouldBe(
        [
            ("PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", OrgSecretVisibility.All, _pastedToken),
            ("PR_UNBLOCK_CLAUDE_API_KEY", OrgSecretVisibility.All, PlantedKey),
        ]);
        body.ShouldNotContain(_pastedToken);
        body.ShouldNotContain(PlantedKey);
        factory.Logs.Lines.ShouldNotContain(line => line.Contains(_pastedToken, StringComparison.Ordinal) || line.Contains(PlantedKey, StringComparison.Ordinal));
        CiRoleAssignment held = (await RosterAsync(factory)).Find(Parse(Email))!.CiRoles.Single();
        held.Role.ShouldBe(_prUnblock);
        held.TokenSetOn.ShouldBe(new DateOnly(2026, 9, 7));
        held.TokenSetAt.ShouldBe(factory.Clock.GetUtcNow());
        held.KeySetOn.ShouldBe(new DateOnly(2026, 9, 17));
        (await HistoryAsync(factory)).Select(static line => (line.Role.Prefix, line.Half, line.Account, line.Via)).ShouldBe(
        [
            ("PR_UNBLOCK", LaneHistoryEntry.TokenHalf, Email, LaneVia.Assign),
            ("PR_UNBLOCK", LaneHistoryEntry.KeyHalf, Email, LaneVia.Assign),
        ]);
    }

    [Fact]
    public async Task AssignTakesTheHalfOffTheAccountThatHeldIt()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN");
        await SeedAsync(factory, new RosterEntry(Parse(Previous)).ClaimingToken(_prUnblock, new DateOnly(2026, 6, 1), null));
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage pasted = await PasteTokenAsync(client, _pastedToken))
        {
            pasted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage assigned = await AssignRoleAsync(client, new { prefix = "PR_UNBLOCK", organization = "octo" }, "token");

        assigned.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Secrets.Sets.Select(static set => (set.Secret.Name, set.Value)).ShouldBe([("PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", _pastedToken)]);
        Roster roster = await RosterAsync(factory);
        roster.Find(Parse(Previous))!.CiRoles.ShouldBeEmpty();
        roster.Find(Parse(Email))!.CiRoles.Single().HasKey.ShouldBeFalse();
        (await HistoryAsync(factory))[^1].Previous.ShouldBe(Previous);
    }

    [Fact]
    public async Task AssignRefusesATokenHalfWhenNoTokenIsStoredAndSendsNothing()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "PR_UNBLOCK_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage assigned = await AssignRoleAsync(client, new { prefix = "PR_UNBLOCK", organization = "octo" }, "both");

        assigned.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await assigned.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("NoStoredToken");
        factory.Secrets.Sets.ShouldBeEmpty();
        (await RosterAsync(factory)).Find(Parse(Email))!.CiRoles.ShouldBeEmpty();
    }

    [Fact]
    public async Task AssignRefusesAKeyHalfWhenNoConsoleKeyIsStored()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken);
        await using AppFactory host = factory;
        AnswerAll(reader, "PR_UNBLOCK_CLAUDE_CODE_OAUTH_TOKEN", "PR_UNBLOCK_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage pasted = await PasteTokenAsync(client, _pastedToken))
        {
            pasted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage assigned = await AssignRoleAsync(client, new { prefix = "PR_UNBLOCK", organization = "octo" }, "both");

        assigned.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await assigned.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("NoConsoleKey");
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task AssignRefusesAKeyForARoleWhoseLanesReadNoKey()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        AnswerAll(reader, "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "AUTOMATIONS_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage assigned = await AssignRoleAsync(client, new { prefix = "AUTOMATIONS", organization = "octo" }, "key");

        assigned.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await assigned.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("NoKeySecret");
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task AssignRefusesARoleTheCatalogDoesNotList()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        AnswerAll(reader, "TYPO_CLAUDE_CODE_OAUTH_TOKEN");
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage pasted = await PasteTokenAsync(client, _pastedToken))
        {
            pasted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage assigned = await AssignRoleAsync(client, new { prefix = "TYPO", organization = "octo" }, "token");

        assigned.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.Secrets.Sets.ShouldBeEmpty();
    }

    // The two secrets outside the catalog that a stored token may go to by
    // default: the shared review token other repositories read, and the
    // central fallback. Both in the catalog's organization, token only.
    [Theory]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("FALLBACK_CLAUDE_CODE_OAUTH_TOKEN")]
    public async Task TheExtraTargetsTakeAStoredTokenAndNeverAKey(string secret)
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: true, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        AnswerAll(reader, secret, "FALLBACK_CLAUDE_API_KEY");
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage pasted = await PasteTokenAsync(client, _pastedToken))
        {
            pasted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage keyed = await AssignRoleAsync(client, new { tokenSecret = secret, organization = "octo" }, "both");
        using HttpResponseMessage assigned = await AssignRoleAsync(client, new { tokenSecret = secret, organization = "octo" }, "token");

        keyed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await keyed.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("NoKeySecret");
        assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        factory.Secrets.Sets.Select(static set => (set.Secret.Name, set.Value)).ShouldBe([(secret, _pastedToken)]);
        (await RosterAsync(factory)).Find(Parse(Email))!.CiRoles.Single().Role.TokenSecretName.ShouldBe(secret);
    }

    [Fact]
    public async Task TheCatalogViewListsTheExtraTargetsAsTokenOnly()
    {
        (AppFactory factory, PathReader reader) = await StartAsync(withKey: false, TestContext.Current.CancellationToken, withCatalog: true);
        await using AppFactory host = factory;
        AnswerCatalog(reader);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage loaded = await client.PostAsync(new Uri("/api/lane-catalog/refresh", UriKind.Relative), null, TestContext.Current.CancellationToken);
        JsonObject view = (await loaded.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;

        view["roles"]!.AsArray()
            .Select(static role => (role!["tokenSecret"]!.GetValue<string>(), role["keyed"]!.GetValue<bool>(), role["keySecret"]?.GetValue<string>()))
            .ShouldBe(
            [
                ("REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", true, "REVIEWS_CLAUDE_API_KEY"),
                ("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", false, null),
                ("CLAUDE_CODE_OAUTH_TOKEN", false, null),
                ("FALLBACK_CLAUDE_CODE_OAUTH_TOKEN", false, null),
            ]);
    }
}
