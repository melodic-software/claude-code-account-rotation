using System.Net;
using System.Net.Http.Json;
using ClaudeCodeAccountRotation.App.Adapters.Http;
using ClaudeCodeAccountRotation.App.Adapters.Peers;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.App.Tests.Quota;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// The follower's half of a Refresh for the account it holds (#185): it reads
/// usage with its own live access token when the leader asks, through the real
/// route and the leader's real adapter, and never refreshes a token for it.
/// </summary>
public sealed class FollowerUsageReadTests : IAsyncDisposable
{
    private const string Email = "a@example.com";

    private readonly FollowerAppFactory _factory = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpPeerRotationInstance Side() => new(SideName.Wsl, _factory.CreateClient());

    private static AccountEmail Account(string email) => AccountEmail.Parse(email).Value;

    [Fact]
    public async Task ItReadsWithItsOwnLiveAccessTokenWhenThePairIsTheOneExpected()
    {
        RefreshTokenFingerprint live = await _factory.Roots.WriteLiveAsync(Email, "refresh-a", Token);
        _factory.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        Result<PeerUsageRead, string> read = await Side().ReadUsageAsync(Account(Email), live, Token);

        read.IsSuccess.ShouldBeTrue(read.IsFailure ? read.Error : null);
        read.Value.Outcome.ShouldBe(PeerUsageOutcome.Read);
        read.Value.Body!["limits"]![0]!["percent"]!.GetValue<double>().ShouldBe(43);
        _factory.Usage.AccessTokens.ShouldBe(["access-refresh-a"]);
    }

    /// <summary>
    /// The CLI on that side rotates the pair on its first refresh after a
    /// hand-off, so the leader's record names an older fingerprint. That side's
    /// own record of the live pair's owner is what still names the account.
    /// </summary>
    [Fact]
    public async Task ItReadsARotatedPairItsOwnRecordStillNamesForThatAccount()
    {
        var handedOver = RefreshTokenFingerprint.FromRefreshToken("refresh-a");
        await new LiveOwnerRecord(_factory.Roots.AppData, _factory.Roots.Clock, NullLogger.Instance)
            .WriteAsync(handedOver, Account(Email), account: null, Token);
        _ = await _factory.Roots.WriteLiveAsync(Email, "refresh-a-rotated", Token);
        _factory.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        Result<PeerUsageRead, string> read = await Side().ReadUsageAsync(Account(Email), handedOver, Token);

        read.Value.Outcome.ShouldBe(PeerUsageOutcome.Read);
        _factory.Usage.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ItRefusesToReadAPairThatBelongsToAnotherAccount()
    {
        _ = await _factory.Roots.WriteLiveAsync("b@example.com", "refresh-b", Token);

        Result<PeerUsageRead, string> read = await Side().ReadUsageAsync(Account(Email), RefreshTokenFingerprint.FromRefreshToken("refresh-a"), Token);

        read.Value.Outcome.ShouldBe(PeerUsageOutcome.NotHeld);
        _factory.Usage.Calls.ShouldBe(0);
    }

    /// <summary>An expired access token is the session's to renew: nothing is sent and nothing is refreshed.</summary>
    [Fact]
    public async Task AnExpiredAccessTokenIsLeftToTheSessionAndNothingIsSent()
    {
        await File.WriteAllTextAsync(
            _factory.Roots.LivePath,
            CredentialFiles.Shape("refresh-a", accessTokenExpiresAt: _factory.Roots.Clock.GetUtcNow().AddHours(-1)).ToJsonString(),
            Token);
        await _factory.Roots.WriteStateFileAsync(Email, Token);

        Result<PeerUsageRead, string> read = await Side().ReadUsageAsync(Account(Email), RefreshTokenFingerprint.FromRefreshToken("refresh-a"), Token);

        read.Value.Outcome.ShouldBe(PeerUsageOutcome.SessionWillRefresh);
        _factory.Usage.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ARateLimitIsRelayedWithItsWait()
    {
        RefreshTokenFingerprint live = await _factory.Roots.WriteLiveAsync(Email, "refresh-a", Token);
        _factory.Usage.Answers.Enqueue(ScriptedUsage.Failed(UsageReadFailureKind.RateLimited, TimeSpan.FromSeconds(90)));

        Result<PeerUsageRead, string> read = await Side().ReadUsageAsync(Account(Email), live, Token);

        read.Value.Outcome.ShouldBe(PeerUsageOutcome.RateLimited);
        read.Value.RetryAfter.ShouldBe(TimeSpan.FromSeconds(90));
    }

    /// <summary>The route is a mutation: a caller without the custom header is refused before anything is read.</summary>
    [Fact]
    public async Task TheRouteRefusesACallerWithoutTheMutationHeader()
    {
        _ = await _factory.Roots.WriteLiveAsync(Email, "refresh-a", Token);
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(new Uri("/api/usage/read", UriKind.Relative), new { email = Email }, Token);

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        _factory.Usage.Calls.ShouldBe(0);
    }

    /// <summary>
    /// The follower reads usage and nothing more: it has no token client, so
    /// no code on that side can POST a refresh token and rotate a pair.
    /// </summary>
    [Fact]
    public void TheFollowerCanReadUsageButCannotRefreshATokenAtAll()
    {
        _factory.Services.GetService<ITokenRefreshClient>().ShouldBeNull();
        _factory.Services.GetService<Func<ITokenRefreshClient>>().ShouldBeNull();
        _factory.Services.GetService<IUsageEndpointClient>().ShouldNotBeNull();
    }

    /// <summary>The composition the follower actually runs, without the test's replacement of the usage client.</summary>
    [Fact]
    public void TheFollowersOwnRegistrationResolvesTheRealUsageClient()
    {
        ServiceCollection services = new();
        App.Hosting.AppComposition.AddUsageClient(services, "test");
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetService<IUsageEndpointClient>().ShouldBeOfType<AnthropicUsageEndpointClient>();
        provider.GetService<ITokenRefreshClient>().ShouldBeNull();
    }
}
