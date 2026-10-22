using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// <c>POST /api/accounts/{email}/console-page</c>: which Console page the
/// mapped browser profile is started on, and every way it refuses before or
/// instead of starting one.
/// </summary>
public sealed class ConsolePageEndpointTests
{
    private const string Mapped = "mapped@example.com";
    private const string Unmapped = "unmapped@example.com";

    private static Uri ConsolePage(string email) =>
        new("/api/accounts/" + Uri.EscapeDataString(email) + "/console-page", UriKind.Relative);

    /// <summary>One account mapped to a Brave profile and one with no browser.</summary>
    private static async Task<AppFactory> RosteredAsync(CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage mapped = await client.PostAsJsonAsync(
            new Uri("/api/accounts", UriKind.Relative),
            new { email = Mapped, browser = "brave", browserProfileDirectory = "Profile 3" },
            cancellationToken);
        mapped.StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage unmapped = await client.PostAsJsonAsync(new Uri("/api/accounts", UriKind.Relative), new { email = Unmapped }, cancellationToken);
        unmapped.StatusCode.ShouldBe(HttpStatusCode.OK);
        return factory;
    }

    // The expected URLs are the pages the Console serves, confirmed live before
    // this route existed, not the constants the route reads.
    [Theory]
    [InlineData("keys", "https://platform.claude.com/settings/keys")]
    [InlineData("admin-keys", "https://platform.claude.com/settings/admin-keys")]
    public async Task TheMappedProfileIsStartedOnTheFixedConsolePage(string page, string expected)
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(ConsolePage(Mapped), new { page }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Browser.Launched.Count.ShouldBe(1);
        (BrowserFamily browser, string? profile, Uri url) = factory.Browser.Launched[0];
        browser.ShouldBe(BrowserFamily.Brave);
        profile.ShouldBe("Profile 3");
        url.AbsoluteUri.ShouldBe(expected);
    }

    [Fact]
    public async Task AnAccountWithNoBrowserMappedIsAConflictThatSaysSoAndOpensNothing()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(ConsolePage(Unmapped), new { page = "keys" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["message"]!.GetValue<string>().ShouldContain("no browser is mapped to this account");
        factory.Browser.Launched.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALaunchThatFailsIsAConflictCarryingTheLaunchersMessage()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        factory.Browser.Error = "brave is not installed at the configured path";
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(ConsolePage(Mapped), new { page = "admin-keys" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["message"]!.GetValue<string>().ShouldBe("brave is not installed at the configured path");
    }

    [Theory]
    [InlineData("billing")]
    [InlineData("KEYS")]
    [InlineData("https://evil.example/settings/keys")]
    [InlineData("")]
    public async Task AnyOtherPageIsABadRequestAndOpensNothing(string page)
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(ConsolePage(Mapped), new { page }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.Browser.Launched.ShouldBeEmpty();
    }

    [Fact]
    public async Task ABodyWithNoPageIsABadRequestAndOpensNothing()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(ConsolePage(Mapped), new { other = "keys" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.Browser.Launched.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAccountNotOnTheRosterIsAConflictAndOpensNothing()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(ConsolePage("stranger@example.com"), new { page = "keys" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        factory.Browser.Launched.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACrossOriginPostIsForbiddenAndOpensNothing()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        using HttpResponseMessage response = await client.PostAsJsonAsync(ConsolePage(Mapped), new { page = "keys" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Browser.Launched.ShouldBeEmpty();
    }

    [Fact]
    public async Task APostWithoutTheMutationHeaderIsForbiddenAndOpensNothing()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(ConsolePage(Mapped), new { page = "keys" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Browser.Launched.ShouldBeEmpty();
    }
}
