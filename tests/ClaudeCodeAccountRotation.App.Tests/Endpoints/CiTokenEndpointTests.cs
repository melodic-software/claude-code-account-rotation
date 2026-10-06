using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// "Generate CI token" end to end over the real runner and a scripted
/// <c>setup-token</c>: the browser it opens, the secret it sets, the roster
/// marker it leaves, and the token it never hands back.
/// </summary>
public sealed class CiTokenEndpointTests
{
    private const string Email = "lane@example.com";
    private const string Code = "one-time-code#state";

    private static readonly Uri _accounts = new("/api/accounts", UriKind.Relative);
    private static readonly string[] _laneRepositories = ["lane-one", "lane-two"];

    private static Uri Start(string email = Email) => new("/api/accounts/" + Uri.EscapeDataString(email) + "/ci-token", UriKind.Relative);

    private static Uri CodePath(string id) => new("/api/ci-token-sessions/" + id + "/code", UriKind.Relative);

    private static async Task<AppFactory> RosteredAsync(CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage added = await client.PostAsJsonAsync(
            _accounts,
            new { email = Email, browser = "edge", browserProfileDirectory = "Profile 2" },
            cancellationToken);
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        return factory;
    }

    private static async Task<Roster> StoredRosterAsync(AppFactory factory, CancellationToken cancellationToken) =>
        await factory.Services.GetRequiredService<RosterFile>().ReadAsync(cancellationToken);

    [Fact]
    public async Task TheTokenGoesToGhAndTheRosterRecordsTheSecretButThePageNeverSeesIt()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage started = await client.PostAsJsonAsync(
            Start(),
            new { secretName = "CLAUDE_CODE_OAUTH_TOKEN", organization = "octo", visibility = "selected", repositories = _laneRepositories },
            TestContext.Current.CancellationToken);
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject session = (await started.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;

        (BrowserFamily browser, string? profile, Uri url) = factory.Browser.Launched.Single();
        browser.ShouldBe(BrowserFamily.Edge);
        profile.ShouldBe("Profile 2");
        url.AbsoluteUri.ShouldBe(SetupTokenScript.SignInUrl);

        using HttpResponseMessage answered = await client.PostAsJsonAsync(
            CodePath(session["id"]!.GetValue<string>()),
            new { code = Code },
            TestContext.Current.CancellationToken);
        string body = await answered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        answered.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(body)!["state"]!.GetValue<string>().ShouldBe("Completed");
        body.ShouldNotContain("sk-ant");
        body.ShouldNotContain(Code);

        (CiTokenSecret secret, OrgSecretVisibility? visibility, IReadOnlyList<string> repositories, string value) = factory.Secrets.Sets.Single();
        value.ShouldBe(SetupTokenScript.Token);
        secret.ShouldBe(new CiTokenSecret("CLAUDE_CODE_OAUTH_TOKEN", CiSecretScope.Organization, "octo"));
        visibility.ShouldBe(OrgSecretVisibility.Selected);
        repositories.ShouldBe(["lane-one", "lane-two"]);

        RosterEntry entry = (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(AccountEmail.Parse(Email).Value)!;
        entry.CiTokenSecret.ShouldBe(secret);
        entry.CiTokenGeneratedOn.ShouldNotBeNull();

        string dashboard = await client.GetStringAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);
        dashboard.ShouldContain("\"ciTokenSecret\":{\"name\":\"CLAUDE_CODE_OAUTH_TOKEN\",\"repository\":null,\"organization\":\"octo\"}");
        factory.Logs.Lines.ShouldAllBe(line => !line.Contains("sk-ant", StringComparison.Ordinal) && !line.Contains(Code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachLaneKeepsItsOwnSecretHolder()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage added = await client.PostAsJsonAsync(_accounts, new { email = "other@example.com" }, TestContext.Current.CancellationToken))
        {
            added.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        foreach ((string email, string repository) in new[] { (Email, "octo/lane-one"), ("other@example.com", "octo/lane-two") })
        {
            using HttpResponseMessage started = await client.PostAsJsonAsync(Start(email), new { secretName = "CLAUDE_CODE_OAUTH_TOKEN", repository }, TestContext.Current.CancellationToken);
            JsonObject session = (await started.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
            using HttpResponseMessage answered = await client.PostAsJsonAsync(CodePath(session["id"]!.GetValue<string>()), new { code = Code }, TestContext.Current.CancellationToken);
            answered.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Roster roster = await StoredRosterAsync(factory, TestContext.Current.CancellationToken);
        roster.Find(AccountEmail.Parse(Email).Value)!.CiTokenSecret!.Owner.ShouldBe("octo/lane-one");
        roster.Find(AccountEmail.Parse("other@example.com").Value)!.CiTokenSecret!.Owner.ShouldBe("octo/lane-two");
    }

    [Theory]
    [InlineData("{\"secretName\":\"GITHUB_TOKEN\",\"repository\":\"octo/repo\"}")]
    [InlineData("{\"secretName\":\"1BAD\",\"repository\":\"octo/repo\"}")]
    [InlineData("{\"secretName\":\"OK\"}")]
    [InlineData("{\"secretName\":\"OK\",\"repository\":\"octo/repo\",\"organization\":\"octo\"}")]
    [InlineData("{\"secretName\":\"OK\",\"repository\":\"octo/repo; rm -rf\"}")]
    [InlineData("{\"secretName\":\"OK\",\"repository\":\"octo/repo\",\"visibility\":\"all\"}")]
    [InlineData("{\"secretName\":\"OK\",\"organization\":\"octo\",\"visibility\":\"selected\"}")]
    [InlineData("{\"secretName\":\"OK\",\"organization\":\"octo\",\"visibility\":\"public\"}")]
    public async Task ATargetGitHubWouldRefuseIsRefusedBeforeAnythingStarts(string json)
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(
            Start(),
            content,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.SetupToken.Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAccountOffTheRosterIsRefused()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            Start("stranger@example.com"),
            new { secretName = "CLAUDE_CODE_OAUTH_TOKEN", repository = "octo/repo" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        factory.SetupToken.Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACrossOriginStartIsRefused()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            Start(),
            new { secretName = "CLAUDE_CODE_OAUTH_TOKEN", repository = "octo/repo" },
            TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeFalse();
        factory.SetupToken.Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnknownSessionIsNotFound()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(CodePath("nope"), new { code = Code }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
