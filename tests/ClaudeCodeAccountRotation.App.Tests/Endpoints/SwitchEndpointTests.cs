using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

public sealed class SwitchEndpointTests
{
    private static Uri SwitchUri(string email) => new("/api/accounts/" + Uri.EscapeDataString(email) + "/switch", UriKind.Relative);

    private static async Task<AppFactory> LiveOnAWithParkedBAsync(CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", cancellationToken);
        await factory.WriteStateFileAsync("a@example.com", cancellationToken);
        await factory.ParkedProfileAsync("b@example.com", "refresh-b", cancellationToken);
        factory.Cli.Email = "b@example.com";
        return factory;
    }

    [Fact]
    public async Task ASwitchMovesThePairsAndReportsTheOutcome()
    {
        using AppFactory factory = await LiveOnAWithParkedBAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["now"]!.GetValue<string>().ShouldBe("b@example.com");
        body["parkedAs"]!.GetValue<string>().ShouldBe("a@example.com");
        body["identityMismatchWarning"]!.GetValue<bool>().ShouldBeFalse();
        (await CredentialFiles.FingerprintAsync(factory.LiveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    [Fact]
    public async Task TheDashboardShowsTheLiveAccountAndEveryParkedProfile()
    {
        using AppFactory factory = await LiveOnAWithParkedBAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken))!;

        dashboard["liveAccount"]!["email"]!.GetValue<string>().ShouldBe("a@example.com");
        dashboard["liveAccount"]!["hasCredentials"]!.GetValue<bool>().ShouldBeTrue();
        JsonArray cards = dashboard["accounts"]!.AsArray();
        cards.Count.ShouldBe(2);
        cards.Select(static card => card!["email"]!.GetValue<string>()).ShouldBe(["a@example.com", "b@example.com"]);
        cards.Single(static card => card!["email"]!.GetValue<string>() == "a@example.com")!["isLive"]!.GetValue<bool>().ShouldBeTrue();
        cards.Single(static card => card!["email"]!.GetValue<string>() == "b@example.com")!["hasCredentials"]!.GetValue<bool>().ShouldBeTrue();
        dashboard.ToJsonString().ShouldNotContain("refresh-");
        dashboard.ToJsonString().ShouldNotContain("access-");
    }

    [Fact]
    public async Task AMutationWithoutTheCustomHeaderIsForbidden()
    {
        using AppFactory factory = await LiveOnAWithParkedBAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CredentialFiles.FingerprintAsync(factory.LiveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
    }

    [Fact]
    public async Task ACrossSiteOriginIsForbidden()
    {
        using AppFactory factory = await LiveOnAWithParkedBAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TheOriginIsMeasuredAgainstTheConfiguredListenAddressAndNotAgainstTheRequestsOwnHost()
    {
        // Deriving the expected origin from the request compares two values the same
        // request supplies, so a rebound name arriving in both would match itself.
        // This client's Host is the test server's, and its Origin is this instance's
        // real address: only a filter that reads the configuration lets it through.
        using AppFactory factory = await LiveOnAWithParkedBAsync(TestContext.Current.CancellationToken);
        int port = factory.Services.GetRequiredService<ClaudeCodeAccountRotationConfiguration>().ListenPort;
        using HttpClient client = factory.CreateMutatingClient();
        client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ALoopbackOriginOnAnotherPortIsForbidden()
    {
        // Another local page is another origin; only this instance's own port is it.
        using AppFactory factory = await LiveOnAWithParkedBAsync(TestContext.Current.CancellationToken);
        int port = factory.Services.GetRequiredService<ClaudeCodeAccountRotationConfiguration>().ListenPort;
        using HttpClient client = factory.CreateMutatingClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:" + (port + 1).ToString(CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ANonLoopbackHostIsRejected()
    {
        using AppFactory factory = await LiveOnAWithParkedBAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        client.DefaultRequestHeaders.Host = "rotation.evil.example";

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AFreshRefreshLockYieldsAConflict()
    {
        using AppFactory factory = await LiveOnAWithParkedBAsync(TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(factory.LiveDirectory, OAuthRefreshLock.DirectoryName));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("RefreshLockPresent");
    }

    [Fact]
    public async Task AnExpiredParkedLoginYieldsAConflict()
    {
        using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync("b@example.com", "refresh-b", TestContext.Current.CancellationToken, loginExpiresAt: factory.Clock.GetUtcNow().AddDays(-1));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("TargetLoginExpired");
    }

    [Fact]
    public async Task ASwitchToAStrandedFolderIsRefused()
    {
        // The folder's own pair is the dead half of a rotation the write-back could
        // not land; the working half is in the recovery directory, keyed to the
        // fingerprint that folder still holds. Making that pair live would move it
        // out from under the restore's compare-and-swap and no restore could ever
        // apply again, so the refusal is server-side and not a disabled button.
        using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync("b@example.com", "refresh-b", TestContext.Current.CancellationToken);
        (await factory.Services.GetRequiredService<RecoveryFiles>().WriteAsync(
            folder,
            CredentialFiles.Pair("refresh-b").Fingerprint,
            CredentialFiles.Pair("refresh-rotated"),
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("TargetStrandedInRecovery");
        (await CredentialFiles.FingerprintAsync(factory.LiveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
    }

    [Fact]
    public async Task ASwitchIsRefusedWhileARefreshPassIsInFlight()
    {
        // A pass fixes the live account's identity when it starts and reads the live
        // pair between its gated units. A switch landing between two turns would have
        // the outgoing account's turn read the incoming account's pair, putting one
        // account's usage figures on the other's card, so the refusal is server-side
        // and not a disabled button.
        using AppFactory factory = await LiveOnAWithParkedBAsync(TestContext.Current.CancellationToken);
        QuotaState quota = factory.Services.GetRequiredService<QuotaState>();
        quota.TryBeginRun().ShouldBeTrue();
        using HttpClient client = factory.CreateMutatingClient();

        using (HttpResponseMessage refused = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await refused.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("RefreshInProgress");
            (await CredentialFiles.FingerprintAsync(factory.LiveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        }

        quota.EndRun();

        using HttpResponseMessage allowed = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CredentialFiles.FingerprintAsync(factory.LiveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    [Fact]
    public async Task ASwitchIsRefusedWhileAnotherCredentialChangeHoldsTheGate()
    {
        // The endpoint's wait for the mutation gate is zero, so a switch arriving
        // while another credential change holds it is a 409 rather than a queued
        // request. Holding the permit states that outright; racing two requests
        // through the test server states it only when the thread pool happens to
        // schedule the second before the first is done. The serialization itself is
        // covered by LiveDirectorySwitchTests.ConcurrentSwitchesSerializeAndLeaveOneHolderPerLineage.
        using AppFactory factory = await LiveOnAWithParkedBAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using (IDisposable permit = await factory.Services
            .GetRequiredService<CredentialMutationGate>()
            .AcquireAsync(TimeSpan.Zero, TestContext.Current.CancellationToken))
        {
            using HttpResponseMessage refused = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await refused.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("MutationInProgress");
            (await CredentialFiles.FingerprintAsync(factory.LiveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        }

        using HttpResponseMessage allowed = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CredentialFiles.FingerprintAsync(factory.LiveDirectory, TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    [Fact]
    public async Task ThePageIsServedFromTheExecutable()
    {
        using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        string html = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        string script = await client.GetStringAsync(new Uri("/app.js", UriKind.Relative), TestContext.Current.CancellationToken);

        html.ShouldContain("claude-code-account-rotation");
        script.ShouldContain("/api/dashboard");
    }
}
