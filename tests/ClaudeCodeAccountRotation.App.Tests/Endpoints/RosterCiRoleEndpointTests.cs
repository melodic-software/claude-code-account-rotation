using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>Editing an account's CI roles by hand, and how the dashboard shows them.</summary>
public sealed class RosterCiRoleEndpointTests
{
    private const string LiveEmail = "live@example.com";
    private const string NewEmail = "new@example.com";

    private static readonly Uri _accounts = new("/api/accounts", UriKind.Relative);
    private static readonly CiRole _automations = new("AUTOMATIONS", CiSecretScope.Organization, "octo");
    private static readonly CiRole _reviews = new("REVIEWS", CiSecretScope.Organization, "octo");

    private static Uri Account(string email) => new("/api/accounts/" + Uri.EscapeDataString(email), UriKind.Relative);

    private static async Task<AppFactory> LiveOnAsync(CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        factory.Cli.Email = LiveEmail;
        return factory;
    }

    private static async Task<RosterEntry> StoredAsync(AppFactory factory, CancellationToken cancellationToken)
    {
        using RosterFile file = new(factory.AppData);
        return (await file.ReadAsync(cancellationToken)).Find(AccountEmail.Parse(NewEmail).Value)!;
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string json)
    {
        using StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");
        return await client.PatchAsync(Account(NewEmail), content, TestContext.Current.CancellationToken);
    }

    private static Task<Roster> SeedAsync(AppFactory factory, Func<Roster, Roster> change, string via = LaneVia.Edit) =>
        factory.Services.GetRequiredService<RosterFile>().UpdateAsync(change, TestContext.Current.CancellationToken, via);

    [Fact]
    public async Task ARoleHalfTheAccountDoesNotHoldIsRefusedAndNothingChanges()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await PatchAsync(
            client,
            """{ "ciRoles": [ { "prefix": "AUTOMATIONS", "organization": "octo", "keySetOn": "2026-10-01" } ] }""");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("RoleHalfNotHeld");
        (await StoredAsync(factory, TestContext.Current.CancellationToken)).CiRoles.ShouldBeEmpty();
    }

    [Fact]
    public async Task AStaleFormThatUnticksOneRoleLeavesTheRoleAnotherAccountTookSinceWithIt()
    {
        // A backs R and S; B is then assigned R; A's form, drawn before that, unticks S.
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        AccountEmail a = AccountEmail.Parse(NewEmail).Value;
        AccountEmail b = AccountEmail.Parse("other@example.com").Value;
        DateOnly january = new(2026, 1, 15);
        await SeedAsync(factory, roster => roster.With(new RosterEntry(a)
            .ClaimingToken(_automations, january, null).ClaimingKey(_automations, january, null)
            .ClaimingToken(_reviews, january, null).ClaimingKey(_reviews, january, null)));
        DateTimeOffset assignedAt = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            factory,
            roster => roster.With(new RosterEntry(b).ClaimingToken(_automations, new DateOnly(2026, 10, 6), assignedAt).ClaimingKey(_automations, new DateOnly(2026, 10, 6), assignedAt)),
            LaneVia.Assign);

        // The older whole-list body, as a stale page drew it, still names R with A's dates.
        using HttpResponseMessage stale = await PatchAsync(
            client,
            """{ "ciRoles": [ { "prefix": "AUTOMATIONS", "organization": "octo", "tokenSetOn": "2026-01-15", "keySetOn": "2026-01-15" } ] }""");
        using HttpResponseMessage removed = await PatchAsync(
            client,
            """{ "removeCiRoles": [ { "tokenSecret": "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", "organization": "octo" } ] }""");

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        Roster roster = await factory.Services.GetRequiredService<RosterFile>().ReadAsync(TestContext.Current.CancellationToken);
        roster.Find(a)!.CiRoles.ShouldBeEmpty();
        CiRoleAssignment kept = roster.Find(b)!.CiRoles.Single();
        kept.Role.ShouldBe(_automations);
        kept.TokenSetAt.ShouldBe(assignedAt);
        kept.KeySetAt.ShouldBe(assignedAt);
        JsonArray history = (await client.GetFromJsonAsync<JsonArray>(new Uri("/api/lane-history", UriKind.Relative), TestContext.Current.CancellationToken))!;
        List<(string?, string?)> moves = [.. history.Select(static line => ((string?)line!["previous"], (string?)line["account"]))];
        moves.ShouldContain((a.Value, b.Value));
        moves.ShouldNotContain((b.Value, a.Value));
    }

    [Theory]
    [InlineData("""{ "ciRoles": [ { "prefix": "AUTOMATIONS", "organization": "octo", "tokenSetAt": "2026-10-01T10:00:00Z" } ] }""")]
    [InlineData("""{ "ciRoles": [ { "prefix": "AUTOMATIONS", "organization": "octo" } ] }""")]
    [InlineData("""{ "ciRoles": [ { "prefix": "", "organization": "octo", "tokenSetOn": "2026-10-01", "keySetOn": "2026-10-01" } ] }""")]
    [InlineData("""{ "ciRoles": [ { "prefix": "AUTOMATIONS", "organization": "octo", "tokenSetOn": "October" } ] }""")]
    [InlineData("""{ "ciRoles": "AUTOMATIONS" }""")]
    public async Task ATokenHalfWithoutItsDateOrARoleWithNoHalfIsRefused(string json)
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await PatchAsync(client, json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StoredAsync(factory, TestContext.Current.CancellationToken)).CiRoles.ShouldBeEmpty();
    }

    [Fact]
    public async Task UntickingTheNewestRoleLowersTheTokenDateAndTheLastTokenHalfClearsIt()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);
        await SeedAsync(factory, roster => roster.With(roster.Find(AccountEmail.Parse(NewEmail).Value)!
            .ClaimingToken(_automations, new DateOnly(2026, 10, 6), null)
            .ClaimingToken(_reviews, new DateOnly(2026, 9, 1), null).ClaimingKey(_reviews, new DateOnly(2026, 9, 1), null)));

        (await PatchAsync(
            client,
            """{ "removeCiRoles": [ { "prefix": "AUTOMATIONS", "organization": "octo" } ] }""")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StoredAsync(factory, TestContext.Current.CancellationToken)).CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 9, 1));

        // The whole-list body may still drop a half the account holds.
        (await PatchAsync(
            client,
            """{ "ciRoles": [ { "prefix": "REVIEWS", "organization": "octo", "keySetOn": "2026-09-01" } ] }""")).StatusCode.ShouldBe(HttpStatusCode.OK);
        RosterEntry stored = await StoredAsync(factory, TestContext.Current.CancellationToken);
        stored.CiTokenGeneratedOn.ShouldBeNull();
        stored.CiRoles.Single().KeySetOn.ShouldBe(new DateOnly(2026, 9, 1));
    }

    [Fact]
    public async Task OneEditCanUntickTheLastTokenRoleAndNameItsReplacement()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);
        await SeedAsync(factory, roster => roster.With(roster.Find(AccountEmail.Parse(NewEmail).Value)!.ClaimingToken(_automations, new DateOnly(2026, 1, 15), null)));

        using HttpResponseMessage response = await PatchAsync(
            client,
            """{ "ciTokenGeneratedOn": "2026-10-06", "removeCiRoles": [ { "prefix": "AUTOMATIONS", "organization": "octo" } ], "ciTokenSecret": { "name": "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", "organization": "octo" } }""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        RosterEntry stored = await StoredAsync(factory, TestContext.Current.CancellationToken);
        stored.CiRoles.ShouldBe([new CiRoleAssignment(_reviews, new DateOnly(2026, 10, 6))]);
        stored.CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 10, 6));
    }

    [Fact]
    public async Task AnEditWithNoDateKeepsTokenHalvesSetSinceTheFormWasDrawn()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);
        await SeedAsync(factory, roster => roster.With(roster.Find(AccountEmail.Parse(NewEmail).Value)!.ClaimingToken(_automations, new DateOnly(2026, 10, 6), null)), LaneVia.Assign);

        (await PatchAsync(client, """{ "notes": "edited" }""")).StatusCode.ShouldBe(HttpStatusCode.OK);

        RosterEntry stored = await StoredAsync(factory, TestContext.Current.CancellationToken);
        stored.CiRoles.ShouldBe([new CiRoleAssignment(_automations, new DateOnly(2026, 10, 6))]);
        stored.CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 10, 6));
    }

    [Fact]
    public async Task TheTokenDateIsTheNewestTokenHalfWhenTheBodySendsAnOlderOne()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);
        await SeedAsync(factory, roster => roster.With(roster.Find(AccountEmail.Parse(NewEmail).Value)!
            .ClaimingToken(_automations, new DateOnly(2026, 10, 6), null)
            .ClaimingToken(_reviews, new DateOnly(2026, 3, 1), null)));

        using HttpResponseMessage response = await PatchAsync(client, """{ "ciTokenGeneratedOn": "2026-01-15" }""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StoredAsync(factory, TestContext.Current.CancellationToken)).CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 10, 6));
    }

    [Fact]
    public async Task TheDashboardKeepsTheSingleSecretFieldsAndAddsTheRoles()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);
        await SeedAsync(factory, roster => roster.With(roster.Find(AccountEmail.Parse(NewEmail).Value)!
            .ClaimingToken(_automations, new DateOnly(2026, 10, 6), new DateTimeOffset(2026, 10, 6, 14, 30, 5, TimeSpan.Zero))
            .ClaimingKey(_automations, new DateOnly(2026, 10, 2), null)
            .ClaimingToken(new CiRole(string.Empty, CiSecretScope.Repository, "octo/lane-one"), new DateOnly(2026, 9, 1), null)));

        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken))!;
        JsonNode roster = dashboard["accounts"]!.AsArray().Single(card => card!["email"]!.GetValue<string>() == NewEmail)!["roster"]!;

        roster["ciTokenGeneratedOn"]!.GetValue<string>().ShouldBe("2026-10-06");
        roster["ciTokenSecret"]!["name"]!.GetValue<string>().ShouldBe("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN");
        roster["ciTokenSecret"]!["organization"]!.GetValue<string>().ShouldBe("octo");
        JsonArray roles = roster["ciRoles"]!.AsArray();
        roles.Count.ShouldBe(2);
        roles[0]!["name"]!.GetValue<string>().ShouldBe("AUTOMATIONS");
        roles[0]!["keySecret"]!.GetValue<string>().ShouldBe("AUTOMATIONS_CLAUDE_API_KEY");
        roles[0]!["keySetOn"]!.GetValue<string>().ShouldBe("2026-10-02");
        roles[1]!["name"]!.GetValue<string>().ShouldBe("legacy (review lanes)");
        roles[1]!["repository"]!.GetValue<string>().ShouldBe("octo/lane-one");
        roles[1]!["keySecret"].ShouldBeNull();
    }
}
