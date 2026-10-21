using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

public sealed class LaneHistoryEndpointTests
{
    private const string LiveEmail = "live@example.com";
    private const string NewEmail = "new@example.com";

    private static readonly Uri _accounts = new("/api/accounts", UriKind.Relative);
    private static readonly Uri _account = new("/api/accounts/" + NewEmail, UriKind.Relative);

    private static async Task<AppFactory> LiveOnAsync(CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        factory.Cli.Email = LiveEmail;
        return factory;
    }

    [Fact]
    public async Task APatchThatAssignsARoleThenARemovalAreReadBackWithTheirVia()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);
        using StringContent roles = new(
            """{ "ciTokenGeneratedOn": "2026-10-01", "ciTokenSecret": { "name": "CLAUDE_CODE_OAUTH_TOKEN", "organization": "octo" } }""",
            System.Text.Encoding.UTF8,
            "application/json");
        (await client.PatchAsync(_account, roles, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.DeleteAsync(_account, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonArray history = (await client.GetFromJsonAsync<JsonArray>(new Uri("/api/lane-history", UriKind.Relative), TestContext.Current.CancellationToken))!;

        history.Count.ShouldBe(2);
        history[0]!["account"]!.GetValue<string>().ShouldBe(NewEmail);
        history[0]!["via"]!.GetValue<string>().ShouldBe("edit");
        history[1]!["account"].ShouldBeNull();
        history[1]!["previous"]!.GetValue<string>().ShouldBe(NewEmail);
        history[1]!["via"]!.GetValue<string>().ShouldBe("delete");
    }

    [Fact]
    public async Task SinceFiltersByTimeAndAnUnparsableValueIsRefused()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        JsonArray none = (await client.GetFromJsonAsync<JsonArray>(new Uri("/api/lane-history?since=2999-01-01", UriKind.Relative), TestContext.Current.CancellationToken))!;
        using HttpResponseMessage bad = await client.GetAsync(new Uri("/api/lane-history?since=lately", UriKind.Relative), TestContext.Current.CancellationToken);

        none.ShouldBeEmpty();
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
