using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// The audit lines for a login, a server-side logout, and a credential-folder
/// deletion. Each names the account and the outcome. A planted access token,
/// refresh token, client secret, and one-time code are in the material those
/// operations touch, and none of them may appear in the log text.
/// </summary>
public sealed class CredentialAuditLogTests
{
    private const string LiveEmail = "live@example.com";
    private const string ParkedEmail = "parked@example.com";
    private const string RefreshToken = "planted-refresh-token-9f2e";
    private const string AccessToken = "access-planted-refresh-token-9f2e";
    private const string ClientSecret = "planted-client-secret-9f2e";
    private const string OneTimeCode = "planted-one-time-code-9f2e";

    private static readonly Uri _accounts = new("/api/accounts", UriKind.Relative);

    private static readonly string[] _secrets = [RefreshToken, AccessToken, ClientSecret, OneTimeCode];

    private static Uri Account(string email, string suffix = "") =>
        new("/api/accounts/" + Uri.EscapeDataString(email) + suffix, UriKind.Relative);

    private static Uri CodePath(string id) => new("/api/login-sessions/" + id + "/code", UriKind.Relative);

    private static Uri SessionPath(string id) => new("/api/login-sessions/" + id, UriKind.Relative);

    private static async Task<AppFactory> RosteredAsync(CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        factory.Cli.Email = LiveEmail;
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage added = await client.PostAsJsonAsync(
            _accounts,
            new { email = ParkedEmail, browser = "brave", browserProfileDirectory = "Profile 3" },
            cancellationToken);
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        return factory;
    }

    private static async Task<JsonObject> StartLoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.PostAsync(Account(ParkedEmail, "/login"), content: null, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
    }

    /// <summary>
    /// A credential file in the CLI's shape plus a client secret, which the
    /// shape itself does not carry. The login child writes this and exits.
    /// </summary>
    private static async Task CompleteWithPlantedPairAsync(ScriptedLoginChild child, string code)
    {
        ArgumentNullException.ThrowIfNull(child);
        _ = code;
        Directory.CreateDirectory(child.ConfigDirectory);
        JsonObject pair = CredentialFiles.Shape(RefreshToken);
        pair["claudeAiOauth"]!.AsObject()["clientSecret"] = ClientSecret;
        await File.WriteAllTextAsync(Path.Combine(child.ConfigDirectory, CredentialFiles.FileName), pair.ToJsonString(), CancellationToken.None);
        JsonObject state = new() { ["numStartups"] = 1, ["oauthAccount"] = AppFactory.AccountJson(child.Email) };
        await File.WriteAllTextAsync(Path.Combine(child.ConfigDirectory, ".claude.json"), state.ToJsonString(), CancellationToken.None);
        child.Exit();
    }

    private static void ShouldRecord(AppFactory factory, string text) =>
        factory.Logs.Lines.ShouldContain(line => line.Contains(text, StringComparison.Ordinal));

    private static void ShouldOmitSecrets(AppFactory factory)
    {
        foreach (string secret in _secrets)
        {
            factory.Logs.Lines.ShouldNotContain(line => line.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ACompletedLoginRecordsStartAndSuccessWithoutTheCodeOrThePair()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        factory.LoginChild.OnCode = CompleteWithPlantedPairAsync;
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            CodePath(started["id"]!.GetValue<string>()),
            new { code = OneTimeCode },
            TestContext.Current.CancellationToken);
        JsonObject completed = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        completed["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Completed));
        ShouldRecord(factory, "login for " + ParkedEmail + " started");
        ShouldRecord(factory, "login for " + ParkedEmail + " completed");
        ShouldOmitSecrets(factory);
    }

    [Fact]
    public async Task ALoginThatEndsWithoutCredentialsRecordsTheFailureAndNotTheCode()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        factory.LoginChild.OnCode = (child, code) =>
        {
            _ = code;
            child.Exit();
            return Task.CompletedTask;
        };
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);
        string id = started["id"]!.GetValue<string>();

        using HttpResponseMessage response = await client.PostAsJsonAsync(CodePath(id), new { code = OneTimeCode }, TestContext.Current.CancellationToken);
        JsonObject failed = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        await ((ClaudeCliLoginSessionRunner)factory.Services.GetRequiredService<ILoginSessionRunner>())
            .FinishedAsync(new LoginSessionId(id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        failed["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        ShouldRecord(factory, "login for " + ParkedEmail + " started");
        ShouldRecord(factory, "login for " + ParkedEmail + " failed");
        ShouldOmitSecrets(factory);
    }

    [Fact]
    public async Task AnExpiredLoginRecordsTheAccountAndThatItExpired()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        using HttpResponseMessage response = await client.GetAsync(
            SessionPath(started["id"]!.GetValue<string>()),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldRecord(factory, "login for " + ParkedEmail + " started");
        ShouldRecord(factory, "login for " + ParkedEmail + " expired");
    }

    [Fact]
    public async Task ALoginThatExpiresAndThenAdmitsRecordsBothOutcomes()
    {
        // The child writes a new pair and stays alive. The status read notices
        // the expiry and kills it; the pump then admits the pair. The page ends
        // on the admission, and the log has to say both that the login expired
        // and that it completed.
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);
        string id = started["id"]!.GetValue<string>();
        ScriptedLoginChild child = factory.LoginChild.Last;
        Directory.CreateDirectory(child.ConfigDirectory);
        JsonObject pair = CredentialFiles.Shape(RefreshToken);
        pair["claudeAiOauth"]!.AsObject()["clientSecret"] = ClientSecret;
        await File.WriteAllTextAsync(Path.Combine(child.ConfigDirectory, CredentialFiles.FileName), pair.ToJsonString(), TestContext.Current.CancellationToken);
        JsonObject state = new() { ["numStartups"] = 1, ["oauthAccount"] = AppFactory.AccountJson(child.Email) };
        await File.WriteAllTextAsync(Path.Combine(child.ConfigDirectory, ".claude.json"), state.ToJsonString(), TestContext.Current.CancellationToken);

        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        using HttpResponseMessage response = await client.GetAsync(SessionPath(id), TestContext.Current.CancellationToken);
        var runner = (ClaudeCliLoginSessionRunner)factory.Services.GetRequiredService<ILoginSessionRunner>();
        await runner.FinishedAsync(new LoginSessionId(id)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        JsonObject after = (await client.GetFromJsonAsync<JsonObject>(SessionPath(id), TestContext.Current.CancellationToken))!;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        child.Killed.ShouldBeTrue();
        after["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Completed));
        ShouldRecord(factory, "login for " + ParkedEmail + " started");
        ShouldRecord(factory, "login for " + ParkedEmail + " expired");
        ShouldRecord(factory, "login for " + ParkedEmail + " completed");
        ShouldOmitSecrets(factory);
    }

    [Fact]
    public async Task ARefusedLoginRecordsTheRevocationAndTheFolderDeletionWithoutThePair()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "enterprise";
        factory.LoginChild.OnCode = CompleteWithPlantedPairAsync;
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            CodePath(started["id"]!.GetValue<string>()),
            new { code = OneTimeCode },
            TestContext.Current.CancellationToken);
        JsonObject failed = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        failed["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        ShouldRecord(factory, "login for " + ParkedEmail + " started");
        ShouldRecord(factory, "login for " + ParkedEmail + " failed");
        ShouldRecord(factory, "logout for " + ParkedEmail + " revoked");
        ShouldRecord(factory, "credential folder for " + ParkedEmail + " deleted");
        ShouldOmitSecrets(factory);
    }

    [Fact]
    public async Task RemovingAnAccountRecordsTheRevocationAndTheFolderDeletionWithoutThePair()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(ParkedEmail, RefreshToken, TestContext.Current.CancellationToken);
        JsonObject pair = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), TestContext.Current.CancellationToken))!.AsObject();
        pair["claudeAiOauth"]!.AsObject()["clientSecret"] = ClientSecret;
        await File.WriteAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), pair.ToJsonString(), TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.DeleteAsync(Account(ParkedEmail), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.Exists(folder).ShouldBeFalse();
        ShouldRecord(factory, "logout for " + ParkedEmail + " revoked");
        ShouldRecord(factory, "credential folder for " + ParkedEmail + " deleted");
        ShouldOmitSecrets(factory);
    }

    [Fact]
    public async Task AFailedLogoutRecordsTheFailureAndNotTheDiagnostic()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(ParkedEmail, RefreshToken, TestContext.Current.CancellationToken);
        JsonObject pair = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), TestContext.Current.CancellationToken))!.AsObject();
        pair["claudeAiOauth"]!.AsObject()["clientSecret"] = ClientSecret;
        await File.WriteAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), pair.ToJsonString(), TestContext.Current.CancellationToken);
        factory.Cli.LogoutError = "printed " + RefreshToken + " " + AccessToken + " " + ClientSecret + " " + OneTimeCode;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.DeleteAsync(Account(ParkedEmail), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Directory.Exists(folder).ShouldBeTrue();
        ShouldRecord(factory, "logout for " + ParkedEmail + " failed");
        factory.Logs.Lines.ShouldNotContain(line => line.Contains("credential folder for " + ParkedEmail + " deleted", StringComparison.Ordinal));
        ShouldOmitSecrets(factory);
    }

    [Fact]
    public async Task TheConsoleFormatterStampsEveryLineInUtc()
    {
        await using AppFactory factory = new();
        IOptionsMonitor<ConsoleLoggerOptions> console = factory.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>();
        console.CurrentValue.FormatterName.ShouldBe(ConsoleFormatterNames.Simple);
        IOptionsMonitor<SimpleConsoleFormatterOptions> format = factory.Services.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>();
        format.CurrentValue.TimestampFormat.ShouldBe(AppComposition.ConsoleTimestampFormat);
        format.CurrentValue.UseUtcTimestamp.ShouldBeTrue();

        ConsoleFormatter formatter = factory.Services.GetServices<ConsoleFormatter>()
            .Single(item => item.Name == ConsoleFormatterNames.Simple);
        using StringWriter writer = new();
        string message = "login for " + ParkedEmail + " started";
        var entry = new LogEntry<string>(LogLevel.Information, "audit", default, message, null, static (state, _) => state);
        formatter.Write(in entry, scopeProvider: null, writer);

        string line = writer.ToString();
        line.ShouldContain(message);
        Match stamp = Regex.Match(line, @"(?<stamp>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z )");
        stamp.Success.ShouldBeTrue();
        var parsed = DateTimeOffset.ParseExact(
            stamp.Groups["stamp"].Value.TrimEnd(),
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        (DateTimeOffset.UtcNow - parsed).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }
}
