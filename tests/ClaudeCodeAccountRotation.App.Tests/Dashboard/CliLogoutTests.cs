using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests.Dashboard;

/// <summary>
/// The live credential file can lose its tokens while the app is running. The
/// dashboard refresh is the read that already watches that file: one Error log,
/// one recorded time, and a blocking card until a later read parses a pair again.
/// </summary>
public sealed class CliLogoutTests
{
    private const string LiveEmail = "a@example.com";
    private const string ParkedEmail = "b@example.com";
    private const string SameDaySentence = "The CLI logged out at 12:00.";
    private const string DatedSentence = "The CLI logged out at 12:00 on 2026-09-07.";
    private const string AgainSentence = "The CLI logged out at 16:00.";

    [Fact]
    public async Task ALivePairThatLosesItsTokensIsLoggedOnceAndBlocksSwitchUntilAPairReturns()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        await factory.ParkedProfileAsync(ParkedEmail, "refresh-b", cancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        JsonElement before = await DashboardAsync(client, cancellationToken);
        Card(before, ParkedEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeTrue();
        Card(before, LiveEmail).GetProperty("cliLoggedOut").ValueKind.ShouldBe(JsonValueKind.Null);
        LogoutLines(factory).ShouldBeEmpty();

        await StripTokensAsync(factory, cancellationToken);
        JsonElement loggedOut = await DashboardAsync(client, cancellationToken);
        JsonElement live = Card(loggedOut, LiveEmail);
        live.GetProperty("cliLoggedOut").GetString().ShouldBe(SameDaySentence);
        live.GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
        live.GetProperty("hasCredentials").GetBoolean().ShouldBeFalse();
        Card(loggedOut, ParkedEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
        Card(loggedOut, ParkedEmail).GetProperty("cliLoggedOut").ValueKind.ShouldBe(JsonValueKind.Null);
        loggedOut.GetProperty("banner").ValueKind.ShouldBe(JsonValueKind.Null);
        AssertLoggedOnce(factory, "the CLI logged out of " + LiveEmail);

        using HttpResponseMessage refused = await client.PostAsync(SwitchUri(ParkedEmail), content: null, cancellationToken);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await refused.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe("CliLoggedOut");
        body["message"]!.GetValue<string>().ShouldContain("The CLI logged out");
        AssertLoggedOnce(factory, "the CLI logged out of " + LiveEmail);

        // Three hours later the clock reads 15:00. Re-recording would move the sentence.
        factory.Clock.Advance(TimeSpan.FromHours(3));
        JsonElement held = await DashboardAsync(client, cancellationToken);
        Card(held, LiveEmail).GetProperty("cliLoggedOut").GetString().ShouldBe(SameDaySentence);
        AssertLoggedOnce(factory, "the CLI logged out of " + LiveEmail);

        // The next UTC day needs the date; a time of day alone would read as today.
        factory.Clock.Advance(TimeSpan.FromDays(1));
        JsonElement dated = await DashboardAsync(client, cancellationToken);
        Card(dated, LiveEmail).GetProperty("cliLoggedOut").GetString().ShouldBe(DatedSentence);
        AssertLoggedOnce(factory, "the CLI logged out of " + LiveEmail);

        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", cancellationToken);
        JsonElement cleared = await DashboardAsync(client, cancellationToken);
        Card(cleared, LiveEmail).GetProperty("cliLoggedOut").ValueKind.ShouldBe(JsonValueKind.Null);
        Card(cleared, ParkedEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeTrue();
        AssertLoggedOnce(factory, "the CLI logged out of " + LiveEmail);

        factory.Clock.Advance(TimeSpan.FromHours(1));
        await StripTokensAsync(factory, cancellationToken);
        JsonElement again = await DashboardAsync(client, cancellationToken);
        Card(again, LiveEmail).GetProperty("cliLoggedOut").GetString().ShouldBe(AgainSentence);
        Card(again, ParkedEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
        string[] againLines = LogoutLines(factory);
        againLines.Length.ShouldBe(2);
        againLines.ShouldAllBe(static line => line.Contains("Error the CLI logged out of " + LiveEmail, StringComparison.Ordinal));
        AssertQuiet(factory);
    }

    [Fact]
    public async Task AHalfWrittenLiveCredentialFileDoesNotRecordALogout()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        await factory.ParkedProfileAsync(ParkedEmail, "refresh-b", cancellationToken);
        using HttpClient client = factory.CreateClient();
        _ = await DashboardAsync(client, cancellationToken);

        await File.WriteAllTextAsync(Path.Combine(factory.LiveDirectory, CredentialFiles.FileName), "{", cancellationToken);
        JsonElement dashboard = await DashboardAsync(client, cancellationToken);

        Card(dashboard, LiveEmail).GetProperty("cliLoggedOut").ValueKind.ShouldBe(JsonValueKind.Null);
        Card(dashboard, LiveEmail).GetProperty("hasCredentials").GetBoolean().ShouldBeTrue();
        Card(dashboard, ParkedEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeTrue();
        LogoutLines(factory).ShouldBeEmpty();
    }

    [Fact]
    public async Task AMissingLiveCredentialFileDoesNotRecordALogout()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        await factory.ParkedProfileAsync(ParkedEmail, "refresh-b", cancellationToken);
        using HttpClient client = factory.CreateClient();
        _ = await DashboardAsync(client, cancellationToken);

        File.Delete(Path.Combine(factory.LiveDirectory, CredentialFiles.FileName));
        JsonElement dashboard = await DashboardAsync(client, cancellationToken);

        Card(dashboard, LiveEmail).GetProperty("cliLoggedOut").ValueKind.ShouldBe(JsonValueKind.Null);
        Card(dashboard, ParkedEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeTrue();
        LogoutLines(factory).ShouldBeEmpty();
    }

    [Fact]
    public async Task ALiveFileThatNeverParsedAsAPairDoesNotRecordALogout()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AppFactory factory = new();
        await WriteTokenlessAsync(factory, cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        await factory.ParkedProfileAsync(ParkedEmail, "refresh-b", cancellationToken);
        using HttpClient client = factory.CreateClient();

        JsonElement dashboard = await DashboardAsync(client, cancellationToken);

        Card(dashboard, LiveEmail).GetProperty("cliLoggedOut").ValueKind.ShouldBe(JsonValueKind.Null);
        Card(dashboard, ParkedEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeTrue();
        LogoutLines(factory).ShouldBeEmpty();
        AssertQuiet(factory);
    }

    [Fact]
    public async Task ATokenlessFileThatWasNeverAPairDoesNotRefuseSwitchAsALogout()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AppFactory factory = new();
        await WriteTokenlessAsync(factory, cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        await factory.ParkedProfileAsync(ParkedEmail, "refresh-b", cancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage refused = await client.PostAsync(SwitchUri(ParkedEmail), content: null, cancellationToken);
        string body = await refused.Content.ReadAsStringAsync(cancellationToken);
        body.ShouldNotContain("CliLoggedOut");
        refused.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        LogoutLines(factory).ShouldBeEmpty();
    }

    [Fact]
    public async Task ALogoutNamesTheOwnerWhenTheStateFileHasNoAddress()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        using HttpClient client = factory.CreateClient();
        _ = await DashboardAsync(client, cancellationToken);

        await File.WriteAllTextAsync(factory.StateFilePath, "{}", cancellationToken);
        await WriteOwnerAsync(factory, "owner@example.com", cancellationToken);
        await StripTokensAsync(factory, cancellationToken);
        JsonElement dashboard = await DashboardAsync(client, cancellationToken);

        dashboard.GetProperty("warnings").EnumerateArray()
            .Select(static warning => warning.GetString())
            .ShouldContain(SameDaySentence);
        dashboard.GetProperty("accounts").GetArrayLength().ShouldBe(0);
        string[] lines = LogoutLines(factory);
        lines.Length.ShouldBe(1);
        lines[0].ShouldContain("Error the CLI logged out of owner@example.com");
        lines[0].ShouldNotContain(LiveEmail);
        AssertQuiet(factory);
    }

    [Fact]
    public async Task ALogoutWithoutAnAddressDoesNotInventOne()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", cancellationToken);
        await File.WriteAllTextAsync(factory.StateFilePath, "{}", cancellationToken);
        using HttpClient client = factory.CreateClient();
        _ = await DashboardAsync(client, cancellationToken);

        await StripTokensAsync(factory, cancellationToken);
        JsonElement dashboard = await DashboardAsync(client, cancellationToken);

        dashboard.GetProperty("warnings").EnumerateArray()
            .Select(static warning => warning.GetString())
            .ShouldContain(SameDaySentence);
        string[] lines = LogoutLines(factory);
        lines.Length.ShouldBe(1);
        lines[0].ShouldContain("Error the CLI logged out");
        lines[0].ShouldNotContain(" of ");
        lines[0].ShouldNotContain("@");
        AssertQuiet(factory);
    }

    [Fact]
    public async Task ALogoutStillRefusesSwitchAfterTheLiveFileIsRemoved()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        await factory.ParkedProfileAsync(ParkedEmail, "refresh-b", cancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        _ = await DashboardAsync(client, cancellationToken);

        await StripTokensAsync(factory, cancellationToken);
        _ = await DashboardAsync(client, cancellationToken);
        File.Delete(Path.Combine(factory.LiveDirectory, CredentialFiles.FileName));

        JsonElement dashboard = await DashboardAsync(client, cancellationToken);
        Card(dashboard, LiveEmail).GetProperty("cliLoggedOut").GetString().ShouldBe(SameDaySentence);
        dashboard.GetProperty("warnings").EnumerateArray()
            .Select(static warning => warning.GetString())
            .ShouldNotContain(SameDaySentence);

        using HttpResponseMessage refused = await client.PostAsync(SwitchUri(ParkedEmail), content: null, cancellationToken);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await refused.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe("CliLoggedOut");
        AssertLoggedOnce(factory, "the CLI logged out of " + LiveEmail);
    }

    [Fact]
    public async Task ASideSwitchIsRefusedWhileALogoutStands()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using AppFactory factory = new(sharedStore: true, peerStorePath: "/mnt/c/store");
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        await factory.ParkedProfileAsync(ParkedEmail, "refresh-b", cancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        _ = await DashboardAsync(client, cancellationToken);

        await StripTokensAsync(factory, cancellationToken);
        _ = await DashboardAsync(client, cancellationToken);

        using HttpResponseMessage refused = await client.PostAsync(SideSwitchUri(ParkedEmail), content: null, cancellationToken);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await refused.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe("CliLoggedOut");

        File.Delete(Path.Combine(factory.LiveDirectory, CredentialFiles.FileName));
        using HttpResponseMessage removed = await client.PostAsync(SideSwitchUri(ParkedEmail), content: null, cancellationToken);
        removed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject removedBody = (await removed.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
        removedBody["refusal"]!.GetValue<string>().ShouldBe("CliLoggedOut");
        AssertLoggedOnce(factory, "the CLI logged out of " + LiveEmail);
    }

    private static void AssertLoggedOnce(AppFactory factory, string message)
    {
        string[] lines = LogoutLines(factory);
        lines.Length.ShouldBe(1);
        lines[0].ShouldContain("Error " + message);
        lines[0].ShouldNotContain(factory.LiveDirectory);
        lines[0].ShouldNotContain("refresh-");
        lines[0].ShouldNotContain("access-");
        AssertQuiet(factory);
    }

    private static void AssertQuiet(AppFactory factory) =>
        factory.Logs.Lines.ShouldNotContain(static line => line.Contains(CredentialPair.LacksTokensReason, StringComparison.Ordinal));

    private static string[] LogoutLines(AppFactory factory) =>
        [.. factory.Logs.Lines.Where(static line => line.Contains("the CLI logged out", StringComparison.Ordinal))];

    private static async Task<JsonElement> DashboardAsync(HttpClient client, CancellationToken cancellationToken) =>
        (await client.GetFromJsonAsync<JsonElement>(new Uri("/api/dashboard", UriKind.Relative), cancellationToken))!;

    private static JsonElement Card(JsonElement dashboard, string email) =>
        dashboard.GetProperty("accounts").EnumerateArray().Single(card => card.GetProperty("email").GetString() == email);

    private static Uri SwitchUri(string email) =>
        new("/api/accounts/" + Uri.EscapeDataString(email) + "/switch", UriKind.Relative);

    private static Uri SideSwitchUri(string email) =>
        new("/api/sides/wsl/accounts/" + Uri.EscapeDataString(email) + "/switch", UriKind.Relative);

    private static async Task StripTokensAsync(AppFactory factory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(factory.LiveDirectory, CredentialFiles.FileName);
        JsonObject raw = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!.AsObject();
        JsonObject oauth = raw["claudeAiOauth"]!.AsObject();
        oauth.Remove("accessToken");
        oauth.Remove("refreshToken");
        await File.WriteAllTextAsync(path, raw.ToJsonString(), cancellationToken);
    }

    private static async Task WriteTokenlessAsync(AppFactory factory, CancellationToken cancellationToken)
    {
        JsonObject raw = new()
        {
            ["claudeAiOauth"] = new JsonObject { ["expiresAt"] = 1 },
        };
        await File.WriteAllTextAsync(Path.Combine(factory.LiveDirectory, CredentialFiles.FileName), raw.ToJsonString(), cancellationToken);
    }

    private static async Task WriteOwnerAsync(AppFactory factory, string email, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(factory.AppData, "state");
        Directory.CreateDirectory(directory);
        JsonObject record = new()
        {
            ["fingerprint"] = "abc",
            ["email"] = email,
            ["at"] = "2026-09-07T12:00:00.0000000+00:00",
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "live-owner.json"), record.ToJsonString(), cancellationToken);
    }
}
