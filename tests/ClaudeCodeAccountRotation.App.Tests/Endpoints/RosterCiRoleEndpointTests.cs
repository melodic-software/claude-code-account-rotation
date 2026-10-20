using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>Editing an account's CI roles by hand, and how the dashboard shows them.</summary>
public sealed class RosterCiRoleEndpointTests
{
    private const string LiveEmail = "live@example.com";
    private const string NewEmail = "new@example.com";

    private static readonly Uri _accounts = new("/api/accounts", UriKind.Relative);

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

    [Fact]
    public async Task AKeyHalfNeedsNoTokenDate()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await PatchAsync(
            client,
            """{ "ciRoles": [ { "prefix": "AUTOMATIONS", "organization": "octo", "keySetOn": "2026-10-01" } ] }""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        RosterEntry stored = await StoredAsync(factory, TestContext.Current.CancellationToken);
        stored.CiRoles.ShouldBe([new CiRoleAssignment(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"), KeySetOn: new DateOnly(2026, 10, 1))]);
        stored.CiTokenGeneratedOn.ShouldBeNull();
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
        (await PatchAsync(
            client,
            """
            { "ciTokenGeneratedOn": "2026-10-06", "ciRoles": [
              { "prefix": "AUTOMATIONS", "organization": "octo", "tokenSetOn": "2026-10-06" },
              { "prefix": "REVIEWS", "organization": "octo", "tokenSetOn": "2026-09-01", "keySetOn": "2026-09-01" } ] }
            """)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The Edit form sends the date field back on every save.
        (await PatchAsync(
            client,
            """
            { "ciTokenGeneratedOn": "2026-10-06", "ciRoles": [
              { "prefix": "REVIEWS", "organization": "octo", "tokenSetOn": "2026-09-01", "keySetOn": "2026-09-01" } ] }
            """)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StoredAsync(factory, TestContext.Current.CancellationToken)).CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 9, 1));

        (await PatchAsync(
            client,
            """
            { "ciTokenGeneratedOn": "2026-10-06", "ciRoles": [
              { "prefix": "REVIEWS", "organization": "octo", "keySetOn": "2026-09-01" } ] }
            """)).StatusCode.ShouldBe(HttpStatusCode.OK);
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
        (await PatchAsync(
            client,
            """{ "ciRoles": [ { "prefix": "AUTOMATIONS", "organization": "octo", "tokenSetOn": "2026-01-15" } ] }""")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage response = await PatchAsync(
            client,
            """{ "ciTokenGeneratedOn": "2026-10-06", "ciRoles": [], "ciTokenSecret": { "name": "REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", "organization": "octo" } }""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        RosterEntry stored = await StoredAsync(factory, TestContext.Current.CancellationToken);
        stored.CiRoles.ShouldBe([new CiRoleAssignment(new CiRole("REVIEWS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 10, 6))]);
        stored.CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 10, 6));
    }

    [Fact]
    public async Task TheTokenDateIsTheNewestTokenHalfWhenTheBodySendsAnOlderOne()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await PatchAsync(
            client,
            """
            { "ciTokenGeneratedOn": "2026-01-15", "ciRoles": [
              { "prefix": "AUTOMATIONS", "organization": "octo", "tokenSetOn": "2026-10-06" },
              { "prefix": "REVIEWS", "organization": "octo", "tokenSetOn": "2026-03-01" } ] }
            """);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StoredAsync(factory, TestContext.Current.CancellationToken)).CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 10, 6));
    }

    [Fact]
    public async Task TheDashboardKeepsTheSingleSecretFieldsAndAddsTheRoles()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);
        (await PatchAsync(
            client,
            """
            { "ciRoles": [
              { "prefix": "AUTOMATIONS", "organization": "octo", "tokenSetOn": "2026-10-06", "tokenSetAt": "2026-10-06T14:30:05+00:00", "keySetOn": "2026-10-02" },
              { "tokenSecret": "CLAUDE_CODE_OAUTH_TOKEN", "repository": "octo/lane-one", "tokenSetOn": "2026-09-01" } ] }
            """)).StatusCode.ShouldBe(HttpStatusCode.OK);

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
