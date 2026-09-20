using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Peers;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.App.Tests.Switching;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// A real hand-off between two hosted processes: the Windows leader and the
/// WSL follower, both in this test host, over one store on one temp volume.
/// Nothing here scripts an answer — the coordinator talks to the follower's own
/// routes through the adapter the operator's build uses.
/// </summary>
public sealed class SideEndpointTests
{
    private const string Incoming = "b@example.com";
    private const string Outgoing = "a@example.com";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Uri _dashboard = new("/api/dashboard", UriKind.Relative);

    private static readonly Uri _wslSide = new("/api/sides/wsl", UriKind.Relative);

    private static Uri SwitchUri(string email) =>
        new("/api/sides/wsl/accounts/" + Uri.EscapeDataString(email) + "/switch", UriKind.Relative);

    /// <summary>
    /// The link between the two hosts, standing in for the loopback socket: it
    /// records what crossed it, and can refuse to carry anything, which is what
    /// a stopped distribution looks like from the leader's side.
    /// </summary>
    private sealed class PeerLink : DelegatingHandler
    {
        public bool Offline { get; set; }

        public List<(string Route, HttpStatusCode Status, bool SentOrigin, bool SentHeader)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (Offline)
            {
                throw new HttpRequestException("the distribution is not running");
            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            Sent.Add((
                request.RequestUri!.AbsolutePath,
                response.StatusCode,
                request.Headers.Contains("Origin"),
                request.Headers.Contains(SameOriginMutationFilter.HeaderName)));
            return response;
        }
    }

    /// <summary>
    /// The leader over the follower's own store, with its peer registry built
    /// over the follower's client. Both sides are the same assembly, so the
    /// version check the coordinator runs before anything moves passes.
    /// </summary>
    private static AppFactory LeaderOver(FollowerAppFactory follower, PeerLink link)
    {
        AppFactory leader = new(sharedStore: true, profilesRoot: follower.Roots.Store, peerStorePath: follower.Roots.Store);
        HttpClient toFollower = follower.CreateDefaultClient(link);
        leader.Overrides = services => services.Replace(ServiceDescriptor.Singleton(
            new PeerRegistry([new Peer(new HttpPeerRotationInstance(SideName.Wsl, toFollower), null, follower.Roots.Store)])));
        return leader;
    }

    private static JsonObject Card(JsonObject dashboard, string email) =>
        dashboard["accounts"]!.AsArray().Single(card => card!["email"]!.GetValue<string>() == email)!.AsObject();

    [Fact]
    public async Task ASwitchOfTheWslSideThroughTheRouteMovesOnePairEachWay()
    {
        await using FollowerAppFactory follower = new();
        PeerLink link = new();
        await using AppFactory leader = LeaderOver(follower, link);
        RefreshTokenFingerprint outgoing = await follower.Roots.WriteLiveAsync(Outgoing, "refresh-a", Token);
        await CredentialFiles.WriteAsync(leader.LiveDirectory, "refresh-w", Token);
        await leader.WriteStateFileAsync("w@example.com", Token);
        await leader.ParkedProfileAsync(Incoming, "refresh-b", Token);
        using HttpClient client = leader.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(SwitchUri(Incoming), content: null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!;
        body["now"]!.GetValue<string>().ShouldBe(Incoming);
        body["parkedAs"]!.GetValue<string>().ShouldBe(Outgoing);

        // The WSL side: the claimed pair is its live pair, its state file names
        // the account that came with it, and the staging copy the swap went
        // through is gone.
        (await FollowerRoots.FingerprintOfAsync(follower.Roots.LivePath, Token))
            .ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        JsonNode state = JsonNode.Parse(await File.ReadAllTextAsync(follower.Roots.StateFilePath, Token))!;
        state["oauthAccount"]!["emailAddress"]!.GetValue<string>().ShouldBe(Incoming);
        File.Exists(follower.Roots.StagingPath).ShouldBeFalse();
        // The store: the incoming slot names the side that holds its pair and
        // holds none itself, the outgoing slot has its pair back, and the
        // mailbox both sides used is empty again.
        string store = follower.Roots.Store;
        (await HolderRecordFile.ReadAsync(Path.Combine(store, Incoming), Token))!.Side.ShouldBe(SideName.Wsl);
        File.Exists(Path.Combine(store, Incoming, CredentialFiles.FileName)).ShouldBeFalse();
        (await CredentialFiles.FingerprintAsync(Path.Combine(store, Outgoing), Token)).ShouldBe(outgoing);
        Directory.GetFiles(follower.Roots.Mailbox).ShouldBeEmpty();
        // And the leader's own live pair never took part in the hand-off.
        (await CredentialFiles.FingerprintAsync(leader.LiveDirectory, Token)).ShouldBe(CredentialFiles.Pair("refresh-w").Fingerprint);
    }

    [Fact]
    public async Task AfterTheSwitchTheDashboardReportsTheAccountAsHeldByTheOtherSide()
    {
        await using FollowerAppFactory follower = new();
        PeerLink link = new();
        await using AppFactory leader = LeaderOver(follower, link);
        await follower.Roots.WriteLiveAsync(Outgoing, "refresh-a", Token);
        await CredentialFiles.WriteAsync(leader.LiveDirectory, "refresh-w", Token);
        await leader.WriteStateFileAsync("w@example.com", Token);
        await leader.ParkedProfileAsync(Incoming, "refresh-b", Token);
        using HttpClient client = leader.CreateMutatingClient();
        (await client.PostAsync(SwitchUri(Incoming), content: null, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The criterion is "within one poll interval of completion", and the
        // interval is the page's own: the next read is what the page would have
        // made, so nothing here waits for one.
        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(_dashboard, Token))!;

        Card(dashboard, Incoming)["slot"]!.GetValue<string>().ShouldBe("held-elsewhere");
        Card(dashboard, Outgoing)["slot"]!.GetValue<string>().ShouldBe("parked");
        dashboard.ToJsonString().ShouldNotContain("refresh-");
    }

    [Fact]
    public async Task AMutationCarryingTheCustomHeaderAndNoOriginIsNeitherRefusedNorRejected()
    {
        // What the adapter sends the follower's mutation group: the header a
        // cross-site form post cannot add, and the absent Origin a
        // process-to-process request honestly has. The filter must take it.
        await using FollowerAppFactory follower = new();
        PeerLink link = new();
        await using AppFactory leader = LeaderOver(follower, link);
        await follower.Roots.WriteLiveAsync(Outgoing, "refresh-a", Token);
        await CredentialFiles.WriteAsync(leader.LiveDirectory, "refresh-w", Token);
        await leader.WriteStateFileAsync("w@example.com", Token);
        await leader.ParkedProfileAsync(Incoming, "refresh-b", Token);
        using HttpClient client = leader.CreateMutatingClient();

        (await client.PostAsync(SwitchUri(Incoming), content: null, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (string Route, HttpStatusCode Status, bool SentOrigin, bool SentHeader) import =
            link.Sent.First(sent => sent.Route == "/api/import");
        import.SentHeader.ShouldBeTrue();
        import.SentOrigin.ShouldBeFalse();
        import.Status.ShouldNotBe(HttpStatusCode.BadRequest);
        import.Status.ShouldNotBe(HttpStatusCode.Forbidden);
        import.Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StoppingTheOtherSideFlipsItsLineToOffline()
    {
        await using FollowerAppFactory follower = new();
        PeerLink link = new();
        await using AppFactory leader = LeaderOver(follower, link);
        await follower.Roots.WriteLiveAsync(Outgoing, "refresh-a", Token);
        await CredentialFiles.WriteAsync(leader.LiveDirectory, "refresh-w", Token);
        await leader.WriteStateFileAsync("w@example.com", Token);
        using HttpClient client = leader.CreateClient();

        JsonObject running = (await client.GetFromJsonAsync<JsonObject>(_wslSide, Token))!;
        running["online"]!.GetValue<bool>().ShouldBeTrue();
        running["liveAccount"]!.GetValue<string>().ShouldBe(Outgoing);

        // Stopped is modelled as the socket refusing, which is exactly what the
        // leader sees when the distribution is not running: disposing the host
        // would take the shared store's directory with it.
        link.Offline = true;
        JsonObject stopped = (await client.GetFromJsonAsync<JsonObject>(_wslSide, Token))!;

        stopped["online"]!.GetValue<bool>().ShouldBeFalse();
        stopped["detail"]!.GetValue<string>().ShouldContain("offline");
    }

    [Fact]
    public async Task NoLineTheOtherSideLoggedNamesTheCliLoginCommand()
    {
        // R1's rule, asserted where it can actually be broken: the follower runs
        // a whole hand-off and never spawns, names, or logs a login.
        await using FollowerAppFactory follower = new();
        PeerLink link = new();
        await using AppFactory leader = LeaderOver(follower, link);
        await follower.Roots.WriteLiveAsync(Outgoing, "refresh-a", Token);
        await CredentialFiles.WriteAsync(leader.LiveDirectory, "refresh-w", Token);
        await leader.WriteStateFileAsync("w@example.com", Token);
        await leader.ParkedProfileAsync(Incoming, "refresh-b", Token);
        using HttpClient client = leader.CreateMutatingClient();

        (await client.PostAsync(SwitchUri(Incoming), content: null, Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        follower.Logs.Lines.Count(line => line.Contains("auth login", StringComparison.OrdinalIgnoreCase)).ShouldBe(0);
        follower.Logs.Lines.ShouldNotBeEmpty("a log with nothing in it would pass this for the wrong reason");
    }
}
