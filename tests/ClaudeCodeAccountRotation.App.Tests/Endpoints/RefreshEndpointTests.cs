using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Endpoints;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Tests.Adapters;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Configuration;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// The two refresh routes through the whole host: the same-origin guards, the
/// two 409 refusals, and one real pass end to end, with the socket underneath
/// both outbound clients scripted so nothing leaves this machine.
/// <para>
/// Every instant here is derived from the factory's frozen clock. The host reads
/// that clock to decide whether an access token has expired and how long a
/// lockout still has to run, so a wall-clock expiry would mean nothing to it.
/// </para>
/// </summary>
public sealed class RefreshEndpointTests
{
    private const string LiveEmail = "a@example.com";
    private const string ParkedEmail = "b@example.com";

    private static Uri AllUri => new("/api/refresh", UriKind.Relative);

    private static Uri CiRolesUri => new("/api/refresh/ci-roles", UriKind.Relative);

    private static Uri CiRolesWaitUri => new("/api/refresh/ci-roles?wait=true", UriKind.Relative);

    private static Uri HookUri => new(HookRoute, UriKind.Relative);

    private static Uri OneUri(string email) => new("/api/accounts/" + Uri.EscapeDataString(email) + "/refresh", UriKind.Relative);

    private const string HookRoute = "/api/hooks/rate-limit";

    public static TheoryData<string> Routes => ["/api/refresh", "/api/accounts/" + ParkedEmail + "/refresh", HookRoute];

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ARefreshWithoutTheCustomHeaderIsForbiddenAndSendsNothing(string route)
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(new Uri(route, UriKind.Relative), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Outbound.Requests.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task ARefreshFromACrossSiteOriginIsForbiddenAndSendsNothing(string route)
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        using HttpResponseMessage response = await client.PostAsync(new Uri(route, UriKind.Relative), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Outbound.Requests.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task TheOriginIsMeasuredAgainstTheConfiguredListenAddress(string route)
    {
        // The filter reads the configured port rather than the request's own Host,
        // or a rebound name arriving in both would match itself. This client's Host
        // is the test server's and its Origin is this instance's real address.
        // The hook reads only the live account, so for it the live pair is valid
        // and that is the one read; the other routes read only the parked one.
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, liveExpired: route != HookRoute);
        int port = factory.Services.GetRequiredService<ClaudeCodeAccountRotationConfiguration>().ListenPort;
        // The pass this starts really reads one account, so its answer is
        // scripted: an unscripted call throws inside the handler and the engine
        // turns it into a read failure, which would leave this passing for the
        // wrong reason and spend the harness's own no-network assertion.
        Script(factory, UsageResponse);
        using HttpClient client = factory.CreateMutatingClient();
        client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.PostAsync(new Uri(route, UriKind.Relative), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.Services.GetRequiredService<QuotaState>().CurrentRun;
        factory.Outbound.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task APassOverAParkedAccountFillsItsCardFromTheEndpointsOwnAnswer()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        Script(factory, UsageResponse);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage started = await client.PostAsync(OneUri(ParkedEmail), content: null, TestContext.Current.CancellationToken);

        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await started.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!["started"]!.GetValue<bool>().ShouldBeTrue();
        // TryStart claims the run before it writes the channel, so the task this
        // awaits is this pass's and never the previous one's finished task.
        await factory.Services.GetRequiredService<QuotaState>().CurrentRun;

        JsonNode card = await CardAsync(client, ParkedEmail);
        card["usage"]!["source"]!.GetValue<string>().ShouldBe("refresh");
        card["refresh"]!["state"]!.GetValue<string>().ShouldBe("read");
        JsonArray limits = card["usage"]!["limits"]!.AsArray();
        limits.Count.ShouldBe(3);
        limits.Select(static limit => limit!["known"]!.GetValue<bool>()).ShouldAllBe(static known => known);
        // Every row came from the one source the card names, so none names its own.
        limits.Select(static limit => limit!["source"]).ShouldAllBe(static source => source == null);
        limits[0]!["label"]!.GetValue<string>().ShouldBe("5-hour");
        // That window's reset time is behind the host's clock, so the figure is
        // dropped rather than shown: the same rule a cached read is held to.
        limits[0]!["windowReset"]!.GetValue<bool>().ShouldBeTrue();
        limits[0]!["percent"].ShouldBeNull();
        limits[1]!["label"]!.GetValue<string>().ShouldBe("7-day");
        limits[1]!["percent"]!.GetValue<double>().ShouldBe(20);
        // The label is the endpoint's own display name; no model name is compiled in.
        limits[2]!["label"]!.GetValue<string>().ShouldBe("Fable");
        limits[2]!["percent"]!.GetValue<double>().ShouldBe(34);
        card["usage"]!["credits"]!["enabled"]!.GetValue<bool>().ShouldBeFalse();
        card["usage"]!["credits"]!["disabledReason"]!.GetValue<string>().ShouldBe("not_enabled");
    }

    [Fact]
    public async Task AnExpiredParkedAccessTokenIsRefreshedOnceBeforeTheOneUsageRead()
    {
        // The ordinary path for a parked account: its access token expired hours
        // ago, so the pass rotates the pair and writes it back before it reads.
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, parkedExpired: true);
        Script(factory, TokenResponse);
        Script(factory, UsageResponse);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage started = await client.PostAsync(AllUri, content: null, TestContext.Current.CancellationToken);

        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.Services.GetRequiredService<QuotaState>().CurrentRun;

        // One token POST and one usage GET, in that order and no more: the
        // endpoint sees exactly one read per account per pass.
        factory.Outbound.Requests.Select(static request => request.Method).ShouldBe([HttpMethod.Post, HttpMethod.Get]);
        (await CredentialFiles.FingerprintAsync(Path.Combine(factory.ProfilesRoot, ParkedEmail), TestContext.Current.CancellationToken))
            .ShouldBe(CredentialFiles.Pair("refresh-rotated").Fingerprint);
    }

    [Fact]
    public async Task ASecondRefreshWhileOneIsRunningIsRefused()
    {
        // The claim itself is taken directly rather than by racing two requests:
        // TryStart refuses on exactly this predicate, and a claim nothing will ever
        // complete is the only way to hold a pass open deterministically.
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        QuotaState state = factory.Services.GetRequiredService<QuotaState>();
        state.TryBeginRun().ShouldBeTrue();
        using HttpClient client = factory.CreateMutatingClient();

        try
        {
            using HttpResponseMessage response = await client.PostAsync(AllUri, content: null, TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            JsonNode refusal = (await response.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
            refusal["refusal"]!.GetValue<string>().ShouldBe("RefreshInProgress");
            refusal["message"]!.GetValue<string>().ShouldBe("A refresh is already running");
            factory.Outbound.Requests.ShouldBeEmpty();
        }
        finally
        {
            state.EndRun();
        }
    }

    [Fact]
    public async Task ARefreshUnderAUsageLockoutIsRefusedWithTheCountdown()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        factory.Services.GetRequiredService<QuotaState>().UsageLockedUntil = factory.Clock.GetUtcNow().AddMinutes(5);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(AllUri, content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonNode refusal = (await response.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
        refusal["refusal"]!.GetValue<string>().ShouldBe("RateLimited");
        refusal["message"]!.GetValue<string>().ShouldBe("rate limited, retry in 300 s");
        factory.Outbound.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefreshOfAnAccountThisMachineDoesNotKnowIsNotFound()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(OneUri("nobody@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        factory.Outbound.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefreshOfSomethingThatIsNotAnAddressIsABadRequest()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(OneUri("not an address"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.Outbound.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefreshOfTheLiveAccountIsAcceptedAndNeverRefreshesItsPair()
    {
        // The live pair's access token is expired, which on any parked account
        // would start a token POST. The running session owns that lineage.
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(OneUri(LiveEmail), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.Services.GetRequiredService<QuotaState>().CurrentRun;
        factory.Outbound.Requests.ShouldBeEmpty();
        JsonNode card = await CardAsync(client, LiveEmail);
        card["refresh"]!["state"]!.GetValue<string>().ShouldBe("session-will-refresh");
    }

    [Fact]
    public async Task NothingReachesEitherEndpointWithoutATrigger()
    {
        // A parked pair with an expired access token is the one a background
        // refresher would reach for first. Hours pass and the page polls, and
        // neither is a trigger.
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, parkedExpired: true);
        using HttpClient client = factory.CreateClient();

        for (int hour = 0; hour < 6; hour++)
        {
            factory.Clock.Advance(TimeSpan.FromHours(1));
            await CardAsync(client, ParkedEmail);
        }

        factory.Outbound.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefreshInsideTheMinuteKeepsTheCachedReadingAndItsTime()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        Script(factory, UsageResponse);
        Script(factory, UsageResponse);
        using HttpClient client = factory.CreateMutatingClient();
        QuotaState state = factory.Services.GetRequiredService<QuotaState>();
        DateTimeOffset firstRead = factory.Clock.GetUtcNow();

        (await client.PostAsync(OneUri(ParkedEmail), content: null, TestContext.Current.CancellationToken)).Dispose();
        await state.CurrentRun;
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        using HttpResponseMessage inside = await client.PostAsync(OneUri(ParkedEmail), content: null, TestContext.Current.CancellationToken);
        await state.CurrentRun;

        inside.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Outbound.Requests.Count.ShouldBe(1);
        JsonNode card = await CardAsync(client, ParkedEmail);
        card["usage"]!["capturedAt"]!.GetValue<DateTimeOffset>().ShouldBe(firstRead);
        card["refresh"]!["state"]!.GetValue<string>().ShouldBe("budget-refused");
        card["refresh"]!["message"]!.GetValue<string>().ShouldBe("read 30 s ago");

        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        (await client.PostAsync(OneUri(ParkedEmail), content: null, TestContext.Current.CancellationToken)).Dispose();
        await state.CurrentRun;

        factory.Outbound.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheRateLimitHookUnderTheLiveAccountsLockoutIsRefusedWithTheCountdown()
    {
        // The account's own 429 lockout, not the host-wide one Start checks. A
        // real 429 spent a reservation too, so the 60 s gap also stands, and the
        // countdown must name the longer of the two.
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, liveExpired: false);
        RefreshBudget budget = factory.Services.GetRequiredService<RefreshBudget>();
        budget.TryReserve(new AccountEmail(LiveEmail)).ShouldBeTrue();
        budget.RecordLockout(new AccountEmail(LiveEmail), TimeSpan.FromMinutes(5));
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(HookUri, content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonNode refusal = (await response.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
        refusal["refusal"]!.GetValue<string>().ShouldBe("RateLimited");
        refusal["message"]!.GetValue<string>().ShouldBe("rate limited, retry in 300 s");
        factory.Services.GetRequiredService<QuotaState>().InProgress.ShouldBeFalse();
        factory.Outbound.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheRateLimitHookReadsOnlyTheLiveAccountAtMostOnceAMinute()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, liveExpired: false);
        Script(factory, UsageResponse);
        Script(factory, UsageResponse);
        using HttpClient client = factory.CreateMutatingClient();
        QuotaState state = factory.Services.GetRequiredService<QuotaState>();

        using HttpResponseMessage first = await client.PostAsync(HookUri, content: null, TestContext.Current.CancellationToken);
        await state.CurrentRun;

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Outbound.Requests.Count.ShouldBe(1);
        (await CardAsync(client, LiveEmail))["refresh"]!["state"]!.GetValue<string>().ShouldBe("read");
        (await CardAsync(client, ParkedEmail))["refresh"]!["state"]!.GetValue<string>().ShouldBe("idle");

        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        using HttpResponseMessage inside = await client.PostAsync(HookUri, content: null, TestContext.Current.CancellationToken);

        inside.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonNode refusal = (await inside.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
        refusal["refusal"]!.GetValue<string>().ShouldBe("RateLimited");
        refusal["message"]!.GetValue<string>().ShouldBe("rate limited, retry in 30 s");
        state.InProgress.ShouldBeFalse();
        factory.Outbound.Requests.Count.ShouldBe(1);

        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        using HttpResponseMessage after = await client.PostAsync(HookUri, content: null, TestContext.Current.CancellationToken);
        await state.CurrentRun;

        after.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Outbound.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheRateLimitHookWithNoLiveAccountIsRefusedAndSendsNothing()
    {
        await using AppFactory factory = new();
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(HookUri, content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("NoLiveAccount");
        factory.Outbound.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFullPassReadsTheParkedAccount()
    {
        // The live pair is expired, so the session owns it and the pass sends
        // nothing for it: the one read is the parked account's.
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        Script(factory, UsageResponse);
        using HttpClient client = factory.CreateMutatingClient();

        (await client.PostAsync(AllUri, content: null, TestContext.Current.CancellationToken)).Dispose();
        await factory.Services.GetRequiredService<QuotaState>().CurrentRun;

        factory.Outbound.Requests.Select(static request => request.Method).ShouldBe([HttpMethod.Get]);
        JsonNode card = await CardAsync(client, ParkedEmail);
        card["refresh"]!["state"]!.GetValue<string>().ShouldBe("read");
        card["usage"]!["source"]!.GetValue<string>().ShouldBe("refresh");
    }

    [Fact]
    public async Task ACardRefreshOfAParkedAccountWithAnExpiredTokenRefreshesItThenReadsIt()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, parkedExpired: true);
        Script(factory, TokenResponse);
        Script(factory, UsageResponse);
        using HttpClient client = factory.CreateMutatingClient();

        (await client.PostAsync(OneUri(ParkedEmail), content: null, TestContext.Current.CancellationToken)).Dispose();
        await factory.Services.GetRequiredService<QuotaState>().CurrentRun;

        factory.Outbound.Requests.Select(static request => request.Method).ShouldBe([HttpMethod.Post, HttpMethod.Get]);
        (await CardAsync(client, ParkedEmail))["refresh"]!["state"]!.GetValue<string>().ShouldBe("read");
    }

    [Fact]
    public async Task AFullPassSetsTheAlertsForAParkedAccountBackingACiRole()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        await BackACiRoleAsync(factory);
        Script(factory, Usage(weekly: 75));
        using HttpClient client = factory.CreateMutatingClient();

        (await client.PostAsync(AllUri, content: null, TestContext.Current.CancellationToken)).Dispose();
        await factory.Services.GetRequiredService<QuotaState>().CurrentRun;

        JsonNode dashboard = await DashboardAsync(client);
        JsonNode alert = dashboard["alerts"]!.AsArray().Single()!;
        alert["account"]!.GetValue<string>().ShouldBe(ParkedEmail);
        alert["level"]!.GetValue<string>().ShouldBe("warning");
        alert["percent"]!.GetValue<decimal>().ShouldBe(75m);
        alert["threshold"]!.GetValue<decimal>().ShouldBe(70m);
        dashboard["alertThresholds"]!.AsArray().Select(static value => value!.GetValue<decimal>()).ShouldBe([70m, 90m]);
        (await CardAsync(client, ParkedEmail))["roster"]!["backsCiRole"]!.GetValue<bool>().ShouldBeTrue();
        dashboard["alertsComputedAt"]!.GetValue<DateTimeOffset>().ShouldBe(factory.Clock.GetUtcNow());
    }

    [Fact]
    public async Task TheCiRolePassReadsOnlyTheAccountsBackingARoleAndNotifiesOnce()
    {
        // The live pair is not expired here, so a pass over every account would
        // send a usage read for it too; this one sends only the parked account's.
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, liveExpired: false);
        await BackACiRoleAsync(factory);
        Script(factory, Usage(weekly: 92));
        Script(factory, Usage(weekly: 93));
        using HttpClient client = factory.CreateMutatingClient();
        QuotaState state = factory.Services.GetRequiredService<QuotaState>();

        using (HttpResponseMessage started = await client.PostAsync(CiRolesUri, content: null, TestContext.Current.CancellationToken))
        {
            started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await state.CurrentRun;
        factory.Clock.Advance(CiUsageSchedule.Interval);
        (await client.PostAsync(CiRolesUri, content: null, TestContext.Current.CancellationToken)).Dispose();
        await state.CurrentRun;

        factory.Outbound.Requests.Count.ShouldBe(2);
        factory.Outbound.Requests.ShouldAllBe(static request => request.Method == HttpMethod.Get);
        JsonNode alert = (await DashboardAsync(client))["alerts"]!.AsArray().Single()!;
        alert["window"]!.GetValue<string>().ShouldBe("7-day");
        alert["level"]!.GetValue<string>().ShouldBe("critical");
        (string title, string body) = factory.Desktop.Shown.Single();
        title.ShouldBe(ParkedEmail + ": 7-day usage at 92%");
        body.ShouldStartWith("Backs CI role AUTOMATIONS. Past the 90% critical threshold. ");
    }

    [Fact]
    public async Task WaitAnswersOnceThePassEndsWithOneRowPerCiRoleAccount()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, liveExpired: false);
        // The live account on the roster too, backing nothing: it sorts first,
        // so the parked account is the roster's second entry and the only row.
        await factory.Services.GetRequiredService<RosterFile>().UpdateAsync(
            static roster => roster.With(new RosterEntry(new AccountEmail(LiveEmail))), TestContext.Current.CancellationToken);
        await BackACiRoleAsync(factory);
        Script(factory, UsageResponse);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage answered = await client.PostAsync(CiRolesWaitUri, content: null, TestContext.Current.CancellationToken);

        answered.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Services.GetRequiredService<QuotaState>().InProgress.ShouldBeFalse();
        factory.Outbound.Requests.Count.ShouldBe(1);
        JsonNode report = (await answered.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
        report["finished"]!.GetValue<bool>().ShouldBeTrue();
        JsonNode row = report["accounts"]!.AsArray().Single()!;
        row["index"]!.GetValue<int>().ShouldBe(1);
        row["roles"]!.AsArray().Select(static role => role!.GetValue<string>()).ShouldBe(["AUTOMATIONS"]);
        row["email"]!.GetValue<string>().ShouldBe(ParkedEmail);
        row["refresh"]!["state"]!.GetValue<string>().ShouldBe("read");
        JsonNode weekly = row["usage"]!["limits"]!.AsArray()[1]!;
        weekly["label"]!.GetValue<string>().ShouldBe("7-day");
        weekly["percent"]!.GetValue<double>().ShouldBe(20);
        weekly["resetsAt"]!.GetValue<DateTimeOffset>().ShouldBe(new DateTimeOffset(2026, 9, 8, 23, 59, 59, TimeSpan.Zero));
    }

    [Fact]
    public async Task WaitNamesTheLegacyRoleAsThePageDoes()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, liveExpired: false);
        await factory.Services.GetRequiredService<RosterFile>().UpdateAsync(
            static roster => roster.With(new RosterEntry(new AccountEmail(ParkedEmail))
                .ClaimingToken(new CiRole(string.Empty, CiSecretScope.Organization, "octo"), new DateOnly(2026, 9, 1), setAt: null)),
            TestContext.Current.CancellationToken);
        Script(factory, UsageResponse);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage answered = await client.PostAsync(CiRolesWaitUri, content: null, TestContext.Current.CancellationToken);

        JsonNode report = (await answered.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
        report["accounts"]!.AsArray().Single()!["roles"]!.AsArray().Select(static role => role!.GetValue<string>())
            .ShouldBe([CiRole.LegacyName]);
    }

    [Fact]
    public async Task WaitAnswers202WhenTheCeilingPassesWithThePassStillRunning()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, liveExpired: false);
        factory.Overrides = static services => services.Replace(ServiceDescriptor.Singleton<CiRoleWaitDelay>(static (_, _) => Task.CompletedTask));
        await BackACiRoleAsync(factory);
        Script(factory, UsageResponse);
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Outbound.Hold = held.Task;
        using HttpClient client = factory.CreateMutatingClient();
        QuotaState state = factory.Services.GetRequiredService<QuotaState>();

        using HttpResponseMessage answered = await client.PostAsync(CiRolesWaitUri, content: null, TestContext.Current.CancellationToken);

        answered.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await answered.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!["finished"]!.GetValue<bool>().ShouldBeFalse();
        // The wait ended, not the pass: it finishes once the read is answered.
        state.InProgress.ShouldBeTrue();
        held.SetResult();
        await state.CurrentRun;
        state.OutcomeFor(new AccountEmail(ParkedEmail))!.Kind.ShouldBe(RefreshOutcomeKind.Read);
    }

    [Fact]
    public async Task WaitIsRefusedLikeTheRouteWhileAPassIsRunning()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, liveExpired: false);
        await BackACiRoleAsync(factory);
        Script(factory, UsageResponse);
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Outbound.Hold = held.Task;
        using HttpClient client = factory.CreateMutatingClient();
        (await client.PostAsync(CiRolesUri, content: null, TestContext.Current.CancellationToken)).Dispose();

        using HttpResponseMessage refused = await client.PostAsync(CiRolesWaitUri, content: null, TestContext.Current.CancellationToken);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonNode body = (await refused.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe("RefreshInProgress");
        body["message"]!.GetValue<string>().ShouldBe("A refresh is already running");
        held.SetResult();
        await factory.Services.GetRequiredService<QuotaState>().CurrentRun;
    }

    [Fact]
    public async Task EveryFifteenMinutesTheScheduleStartsTheCiRolePass()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken, liveExpired: false);
        TaskCompletionSource tick = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource ticked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<TimeSpan> intervals = [];
        factory.Overrides = services => services.Replace(ServiceDescriptor.Singleton<ScheduleDelay>(async (interval, cancellationToken) =>
        {
            intervals.Add(interval);
            if (intervals.Count == 1)
            {
                await tick.Task.WaitAsync(cancellationToken);
                return;
            }

            ticked.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }));
        await BackACiRoleAsync(factory);
        Script(factory, Usage(weekly: 75));
        QuotaState state = factory.Services.GetRequiredService<QuotaState>();

        tick.SetResult();
        await ticked.Task.WaitAsync(TestContext.Current.CancellationToken);
        await state.CurrentRun;

        intervals.ShouldBe([TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)]);
        factory.Outbound.Requests.Single().Method.ShouldBe(HttpMethod.Get);
        state.Alerts!.Alerts.Single().Account.Value.ShouldBe(ParkedEmail);
    }

    [Fact]
    public async Task ASevenDayWindowThatHasResetRaisesNoAlert()
    {
        // The answer's 7-day window resets 2026-09-08T23:59:59Z; the pass runs
        // after it, so the card blanks the figure and no alert may count it.
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        await BackACiRoleAsync(factory);
        factory.Clock.Advance(TimeSpan.FromDays(2));
        Script(factory, TokenResponse);
        Script(factory, Usage(weekly: 95));
        using HttpClient client = factory.CreateMutatingClient();

        (await client.PostAsync(AllUri, content: null, TestContext.Current.CancellationToken)).Dispose();
        await factory.Services.GetRequiredService<QuotaState>().CurrentRun;

        factory.Outbound.Requests.Count.ShouldBe(2);
        JsonNode dashboard = await DashboardAsync(client);
        dashboard["alerts"]!.AsArray().ShouldBeEmpty();
        dashboard["alertsComputedAt"]!.GetValue<DateTimeOffset>().ShouldBe(factory.Clock.GetUtcNow());
    }

    [Fact]
    public async Task ACardRefreshLeavesTheAlertsTheLastFullPassComputed()
    {
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        await BackACiRoleAsync(factory);
        Script(factory, Usage(weekly: 75));
        Script(factory, Usage(weekly: 95));
        using HttpClient client = factory.CreateMutatingClient();
        QuotaState state = factory.Services.GetRequiredService<QuotaState>();
        DateTimeOffset passAt = factory.Clock.GetUtcNow();

        (await client.PostAsync(AllUri, content: null, TestContext.Current.CancellationToken)).Dispose();
        await state.CurrentRun;
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        (await client.PostAsync(OneUri(ParkedEmail), content: null, TestContext.Current.CancellationToken)).Dispose();
        await state.CurrentRun;

        // The card read 95%, which a recompute would have made critical.
        factory.Outbound.Requests.Count.ShouldBe(2);
        JsonNode dashboard = await DashboardAsync(client);
        dashboard["alerts"]!.AsArray().Single()!["level"]!.GetValue<string>().ShouldBe("warning");
        dashboard["alertsComputedAt"]!.GetValue<DateTimeOffset>().ShouldBe(passAt);
    }

    [Fact]
    public async Task TheDashboardComputesNoAlertsOfItsOwn()
    {
        // A figure past 90% is in place through a card's refresh, and the page
        // polls for hours: no full pass has run, so there is nothing to show.
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        await BackACiRoleAsync(factory);
        Script(factory, Usage(weekly: 95));
        using HttpClient client = factory.CreateMutatingClient();

        (await client.PostAsync(OneUri(ParkedEmail), content: null, TestContext.Current.CancellationToken)).Dispose();
        await factory.Services.GetRequiredService<QuotaState>().CurrentRun;
        for (int poll = 0; poll < 3; poll++)
        {
            factory.Clock.Advance(TimeSpan.FromHours(1));
            JsonNode dashboard = await DashboardAsync(client);
            dashboard["alerts"]!.AsArray().ShouldBeEmpty();
            dashboard["alertsComputedAt"].ShouldBeNull();
        }
    }

    private static Task<Roster> BackACiRoleAsync(AppFactory factory) =>
        factory.Services.GetRequiredService<RosterFile>().UpdateAsync(
            roster => roster.With(new RosterEntry(new AccountEmail(ParkedEmail))
                .ClaimingToken(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 9, 1), setAt: null)),
            TestContext.Current.CancellationToken);

    /// <summary>The usage answer below with its 7-day figure replaced.</summary>
    private static string Usage(int weekly) =>
        UsageResponse.Replace("\"percent\": 20,", "\"percent\": " + weekly.ToString(CultureInfo.InvariantCulture) + ",", StringComparison.Ordinal);

    /// <summary>
    /// One live account whose pair is expired unless asked otherwise (so a pass
    /// over it sends nothing) and one parked account. Both credential files are written here rather than
    /// through the factory's helper because both expiries have to come from the
    /// frozen clock: the fixture's own defaults are wall-clock and mean nothing to
    /// a host four days behind them.
    /// </summary>
    private static async Task<AppFactory> LiveAndParkedAsync(CancellationToken cancellationToken, bool parkedExpired = false, bool liveExpired = true)
    {
        AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        await WritePairAsync(factory, factory.LiveDirectory, "refresh-live", factory.Clock.GetUtcNow().AddHours(liveExpired ? -1 : 1), cancellationToken);
        string folder = Path.Combine(factory.ProfilesRoot, ParkedEmail);
        Directory.CreateDirectory(folder);
        await WritePairAsync(
            factory,
            folder,
            "refresh-parked",
            parkedExpired ? factory.Clock.GetUtcNow().AddHours(-1) : factory.Clock.GetUtcNow().AddHours(1),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "profile.json"),
            AppFactory.AccountJson(ParkedEmail).ToJsonString(),
            cancellationToken);
        return factory;
    }

    /// <summary>
    /// One scripted answer for the socket under both outbound clients, in the
    /// order the pass will send. The response belongs to whoever dequeues it: the
    /// handler returns it and the adapter reads and disposes it. CA2000 cannot see
    /// that hand-off across the queue, and disposing it here would dispose a
    /// response the pass has not been given yet.
    /// </summary>
#pragma warning disable CA2000 // Dispose objects before losing scope
    private static void Script(AppFactory factory, string body) =>
        factory.Outbound.Enqueue(RecordingHandler.Json(HttpStatusCode.OK, body));
#pragma warning restore CA2000

    private static Task WritePairAsync(AppFactory factory, string directory, string refreshToken, DateTimeOffset accessTokenExpiresAt, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            Path.Combine(directory, CredentialFiles.FileName),
            CredentialFiles.Shape(refreshToken, accessTokenExpiresAt, factory.Clock.GetUtcNow().AddDays(28)).ToJsonString(),
            cancellationToken);

    private static async Task<JsonNode> CardAsync(HttpClient client, string email) =>
        (await DashboardAsync(client))["accounts"]!.AsArray().Single(card => card!["email"]!.GetValue<string>() == email)!;

    private static async Task<JsonNode> DashboardAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonNode>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken))!;

    /// <summary>
    /// The rotation the token host answers with. The tokens carry the
    /// <c>refresh-</c> and <c>access-</c> prefixes every secrets assertion in this
    /// repository looks for, so a leak fails a test rather than passing quietly.
    /// </summary>
    private const string TokenResponse = """
        {
          "access_token": "access-rotated",
          "refresh_token": "refresh-rotated",
          "expires_in": 28800,
          "refresh_token_expires_in": 2419200,
          "scope": "user:inference user:profile"
        }
        """;

    /// <summary>
    /// The usage endpoint's answer: the <c>limits</c> and <c>extra_usage</c>
    /// blocks of the spike-01 capture the Core parser is tested against
    /// (<c>Core.Tests/Fixtures/usage-response-spike01.json</c>), verbatim. Copied
    /// rather than linked because this project builds no fixture content, and
    /// trimmed to the two blocks a card is built from: the capture's other
    /// top-level keys are the endpoint's own per-bucket duplicates, which this
    /// build reads nothing from and names nowhere.
    /// </summary>
    private const string UsageResponse = """
        {
          "limits": [
            {
              "kind": "session",
              "group": "session",
              "percent": 43,
              "severity": "ok",
              "resets_at": "2026-09-04T05:29:59Z",
              "scope": null,
              "is_active": true
            },
            {
              "kind": "weekly_all",
              "group": "weekly",
              "percent": 20,
              "severity": "ok",
              "resets_at": "2026-09-08T23:59:59Z",
              "scope": null,
              "is_active": true
            },
            {
              "kind": "weekly_scoped",
              "group": "weekly",
              "percent": 34,
              "severity": "warning",
              "resets_at": "2026-09-08T23:59:59Z",
              "scope": { "model": { "display_name": "Fable" } },
              "is_active": true
            }
          ],
          "extra_usage": {
            "is_enabled": false,
            "disabled_reason": "not_enabled",
            "spend_limit_reached": false
          }
        }
        """;
}
