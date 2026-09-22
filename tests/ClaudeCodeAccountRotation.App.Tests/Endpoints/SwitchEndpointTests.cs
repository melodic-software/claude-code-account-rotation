using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        // request. Holding the permit states that outright. The overlap inside a
        // switch, with the second request issued only after the first has the gate,
        // is TwoConcurrentSwitchesYieldOneSuccessAndOneConflict. The moves themselves
        // stay covered by LiveDirectorySwitchTests.ConcurrentSwitchesSerializeAndLeaveOneHolderPerLineage.
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
    public async Task TwoConcurrentSwitchesYieldOneSuccessAndOneConflict()
    {
        // The block is after the park rename, which is after the switch has taken
        // the mutation gate and before it disposes the permit. The second POST
        // is issued only once that block is reached, then the block is released.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using AppFactory factory = await LiveOnAWithParkedBAsync(cancellationToken);
        TaskCompletionSource<bool> holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HoldTheGateAfterPark(factory, holding, release);
        using HttpClient client = factory.CreateMutatingClient();
        Task<HttpResponseMessage> first = client.PostAsync(SwitchUri("b@example.com"), content: null, cancellationToken);
        try
        {
            Task arrived = await Task.WhenAny(holding.Task, first);
            if (first.IsFaulted)
            {
                await first;
            }

            arrived.ShouldBeSameAs(holding.Task, "the first switch must reach the park while it still holds the gate");

            using HttpResponseMessage refused = await client.PostAsync(SwitchUri("b@example.com"), content: null, cancellationToken);
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await refused.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("MutationInProgress");

            release.TrySetResult(true);
            using HttpResponseMessage succeeded = await first;
            succeeded.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await CredentialFiles.FingerprintAsync(factory.LiveDirectory, cancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        }
        finally
        {
            release.TrySetResult(true);
            using HttpResponseMessage finished = await first;
        }
    }

    [Fact]
    public async Task ADashboardPollWhileASwitchHoldsTheGateLeavesDistinctFingerprintsAndNoJournal()
    {
        // Same window as the concurrent switches: after the park, before the
        // unpark, with the gate still held. The poll has to come back while
        // that is true, and releasing the block has to let the switch finish
        // without an open journal and with one pair per account.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using AppFactory factory = await LiveOnAWithParkedBAsync(cancellationToken);
        TaskCompletionSource<bool> holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HoldTheGateAfterPark(factory, holding, release);
        using HttpClient client = factory.CreateMutatingClient();
        string journal = Path.Combine(factory.AppData, "state", "switch-journal.json");
        Task<HttpResponseMessage> switching = client.PostAsync(SwitchUri("b@example.com"), content: null, cancellationToken);
        try
        {
            Task arrived = await Task.WhenAny(holding.Task, switching);
            if (switching.IsFaulted)
            {
                await switching;
            }

            arrived.ShouldBeSameAs(holding.Task, "the poll has to run while the switch is between the park and the unpark");
            File.Exists(journal).ShouldBeTrue();

            using HttpResponseMessage dashboard = await client.GetAsync(new Uri("/api/dashboard", UriKind.Relative), cancellationToken);
            dashboard.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonObject body = (await dashboard.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
            body.ContainsKey("accounts").ShouldBeTrue();

            release.TrySetResult(true);
            using HttpResponseMessage switched = await switching;
            switched.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await CredentialFiles.FingerprintAsync(factory.LiveDirectory, cancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
            (await CredentialFiles.FingerprintAsync(Path.Combine(factory.ProfilesRoot, "a@example.com"), cancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
            (await CredentialFiles.FingerprintAsync(Path.Combine(factory.ProfilesRoot, "b@example.com"), cancellationToken)).ShouldBeNull();
            File.Exists(journal).ShouldBeFalse();
        }
        finally
        {
            release.TrySetResult(true);
            using HttpResponseMessage finished = await switching;
        }
    }

    /// <summary>
    /// The 409 body's <c>refusal</c> field is the enum name, which is the
    /// operator's stable handle on a refusal; its <c>message</c> is the
    /// sentence beside it, and a member nobody wrote one for used to reach the
    /// page as that same bare name. One fact over every member, so the gap is
    /// found when the member is added rather than when an operator meets it.
    /// </summary>
    [Fact]
    public void EveryRefusalCarriesASentenceAndNotItsEnumName()
    {
        foreach (SwitchRefusal refusal in Enum.GetValues<SwitchRefusal>())
        {
            var view = SwitchRefusalView.Of(refusal);

            view.Refusal.ShouldBe(refusal.ToString());
            view.Message.ShouldNotContain(refusal.ToString());
            view.Message.ShouldEndWith(".");
        }
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

    /// <summary>
    /// Installs a store whose park awaits <paramref name="release"/> after
    /// signaling <paramref name="holding"/>. The park runs only once the switch
    /// holds the mutation gate, so the wait is inside the gate and not before it.
    /// </summary>
    private static void HoldTheGateAfterPark(AppFactory factory, TaskCompletionSource<bool> holding, TaskCompletionSource<bool> release)
    {
        factory.Overrides = services => services.Replace(ServiceDescriptor.Singleton<ICredentialPairStore>(provider =>
            new ParkingGate(provider.GetRequiredService<FileSystemCredentialPairStore>())
            {
                AfterParkAsync = async () =>
                {
                    holding.TrySetResult(true);
                    await release.Task;
                },
            }));
    }

    /// <summary>
    /// Forwards to the host's store. <see cref="AfterParkAsync"/> runs after the
    /// park rename and before the unpark, while the switch still holds the gate.
    /// </summary>
    private sealed class ParkingGate(ICredentialPairStore inner) : ICredentialPairStore
    {
        public Func<Task>? AfterParkAsync { get; init; }

        public Task<CredentialPair?> ReadLiveAsync(CancellationToken cancellationToken) => inner.ReadLiveAsync(cancellationToken);

        public Task<CredentialPair?> ReadParkedAsync(string folderPath, CancellationToken cancellationToken) => inner.ReadParkedAsync(folderPath, cancellationToken);

        public async Task MoveLiveToParkedAsync(string folderPath, CancellationToken cancellationToken)
        {
            await inner.MoveLiveToParkedAsync(folderPath, cancellationToken);
            if (AfterParkAsync is not null)
            {
                await AfterParkAsync();
            }
        }

        public Task MoveParkedToLiveAsync(string folderPath, CancellationToken cancellationToken) => inner.MoveParkedToLiveAsync(folderPath, cancellationToken);

        public Task MoveParkedToQuarantineAsync(string folderPath, string destinationDirectory, CancellationToken cancellationToken) => inner.MoveParkedToQuarantineAsync(folderPath, destinationDirectory, cancellationToken);

        public Task<Result<Unit, string>> WriteParkedAsync(string folderPath, CredentialPair pair, RefreshTokenFingerprint expected, CancellationToken cancellationToken) => inner.WriteParkedAsync(folderPath, pair, expected, cancellationToken);

        public Task<Result<IAsyncDisposable, string>> AcquireRefreshLockAsync(TimeSpan waitBound, CancellationToken cancellationToken) => inner.AcquireRefreshLockAsync(waitBound, cancellationToken);

        public string? FreshLockFileName(TimeSpan maxAge) => inner.FreshLockFileName(maxAge);
    }
}
