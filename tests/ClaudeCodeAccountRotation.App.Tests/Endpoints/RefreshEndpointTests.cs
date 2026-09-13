using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Tests.Adapters;
using ClaudeCodeAccountRotation.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    private static Uri OneUri(string email) => new("/api/accounts/" + Uri.EscapeDataString(email) + "/refresh", UriKind.Relative);

    public static TheoryData<string> Routes => ["/api/refresh", "/api/accounts/" + ParkedEmail + "/refresh"];

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
        await using AppFactory factory = await LiveAndParkedAsync(TestContext.Current.CancellationToken);
        int port = factory.Services.GetRequiredService<ClaudeCodeAccountRotationConfiguration>().ListenPort;
        // The pass this starts really reads the parked account, so its answer is
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

    /// <summary>
    /// One live account whose pair is expired (so a pass over it sends nothing)
    /// and one parked account. Both credential files are written here rather than
    /// through the factory's helper because both expiries have to come from the
    /// frozen clock: the fixture's own defaults are wall-clock and mean nothing to
    /// a host four days behind them.
    /// </summary>
    private static async Task<AppFactory> LiveAndParkedAsync(CancellationToken cancellationToken, bool parkedExpired = false)
    {
        AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        await WritePairAsync(factory, factory.LiveDirectory, "refresh-live", factory.Clock.GetUtcNow().AddHours(-1), cancellationToken);
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

    private static async Task<JsonNode> CardAsync(HttpClient client, string email)
    {
        JsonNode dashboard = (await client.GetFromJsonAsync<JsonNode>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken))!;
        return dashboard["accounts"]!.AsArray().Single(card => card!["email"]!.GetValue<string>() == email)!;
    }

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
