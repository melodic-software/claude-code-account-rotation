using System.Net;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.Peers;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// <c>POST /api/shutdown</c> on the leader and the follower. Exit code 0 is
/// <c>Program.cs</c> returning after <c>RunAsync</c> once
/// <see cref="IHostApplicationLifetime.StopApplication"/> finishes the host;
/// these tests stay in process and watch <see cref="IHostApplicationLifetime.ApplicationStopping"/>.
/// </summary>
public sealed class ShutdownEndpointTests
{
    private static readonly Uri _shutdown = new("/api/shutdown", UriKind.Relative);
    private static readonly Uri _stop = new("/api/stop", UriKind.Relative);
    private static readonly RefreshTokenFingerprint _outgoing = new("1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly RefreshTokenFingerprint _incoming = new("2222222222222222222222222222222222222222222222222222222222222222");
    private static readonly AccountEmail _outgoingAccount = new("a@example.com");
    private static readonly AccountEmail _incomingAccount = new("b@example.com");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnIdleLeaderStopsAndSignalsApplicationStopping()
    {
        await using AppFactory factory = new();
        using HttpClient client = factory.CreateMutatingClient();

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, Lifetime(factory.Services), Token);

        status.ShouldBe(HttpStatusCode.OK);
        stopping.ShouldBeTrue();
        JsonNode.Parse(body)!["stopped"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task AnOpenSwitchJournalRefusesShutdownAndLeavesTheJournal()
    {
        await using AppFactory factory = new();
        // Startup reconciliation has already run. The journal written now is the
        // open one shutdown must refuse, and must not finish, unwind, or rewrite.
        IHostApplicationLifetime lifetime = Lifetime(factory.Services);
        string path = Path.Combine(factory.AppData, "state", "switch-journal.json");
        await new SwitchJournal(factory.AppData).WriteAsync(
            new SwitchJournalEntry(
                _outgoingAccount,
                _outgoing,
                Path.Combine(factory.ProfilesRoot, "a@example.com"),
                _incomingAccount,
                _incoming,
                Path.Combine(factory.ProfilesRoot, "b@example.com"),
                SwitchStep.Parked,
                DateTimeOffset.UnixEpoch),
            Token);
        byte[] before = await File.ReadAllBytesAsync(path, Token);
        using HttpClient client = factory.CreateMutatingClient();

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, lifetime, Token);

        status.ShouldBe(HttpStatusCode.Conflict);
        stopping.ShouldBeFalse();
        JsonNode.Parse(body)!["error"]!.GetValue<string>().ShouldBe("a switch or import is in flight");
        File.Exists(path).ShouldBeTrue();
        (await File.ReadAllBytesAsync(path, Token)).ShouldBe(before);
    }

    [Fact]
    public async Task AnOpenWslSwitchJournalRefusesShutdownAndLeavesTheJournal()
    {
        await using AppFactory factory = new();
        IHostApplicationLifetime lifetime = Lifetime(factory.Services);
        string path = Path.Combine(factory.AppData, "state", "wsl-switch-journal.json");
        await new WslSwitchJournal(factory.AppData).WriteAsync(
            new WslSwitchJournalEntry(
                SideName.Wsl,
                _incomingAccount,
                _incoming,
                Path.Combine(factory.ProfilesRoot, "b@example.com"),
                _outgoingAccount,
                _outgoing,
                Path.Combine(factory.ProfilesRoot, "a@example.com"),
                WslSwitchStep.Claimed,
                DateTimeOffset.UnixEpoch),
            Token);
        byte[] before = await File.ReadAllBytesAsync(path, Token);
        using HttpClient client = factory.CreateMutatingClient();

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, lifetime, Token);

        status.ShouldBe(HttpStatusCode.Conflict);
        stopping.ShouldBeFalse();
        JsonNode.Parse(body)!["error"]!.GetValue<string>().ShouldBe("a switch or import is in flight");
        File.Exists(path).ShouldBeTrue();
        (await File.ReadAllBytesAsync(path, Token)).ShouldBe(before);
    }

    [Fact]
    public async Task AnIdleFollowerStopsAndSignalsApplicationStopping()
    {
        await using FollowerAppFactory factory = new();
        using HttpClient client = factory.CreateMutatingClient();

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, Lifetime(factory.Services), Token);

        status.ShouldBe(HttpStatusCode.OK);
        stopping.ShouldBeTrue();
        JsonNode.Parse(body)!["stopped"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task AnOpenImportJournalRefusesShutdownAndLeavesTheJournal()
    {
        await using FollowerAppFactory factory = new();
        IHostApplicationLifetime lifetime = Lifetime(factory.Services);
        string path = Path.Combine(factory.Roots.AppData, "state", "import-journal.json");
        await factory.Roots.Journal().WriteAsync(
            new ImportJournalEntry(
                _incomingAccount,
                _incoming,
                factory.Roots.ClaimedPath("b@example.com"),
                factory.Roots.ExportPath("a@example.com"),
                _outgoingAccount,
                _outgoing,
                IncomingAccount: null,
                OutgoingAccount: null,
                ImportStep.Exported,
                factory.Roots.Clock.GetUtcNow()),
            Token);
        byte[] before = await File.ReadAllBytesAsync(path, Token);
        using HttpClient client = factory.CreateMutatingClient();

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, lifetime, Token);

        status.ShouldBe(HttpStatusCode.Conflict);
        stopping.ShouldBeFalse();
        JsonNode.Parse(body)!["error"]!.GetValue<string>().ShouldBe("a switch or import is in flight");
        File.Exists(path).ShouldBeTrue();
        (await File.ReadAllBytesAsync(path, Token)).ShouldBe(before);
    }

    [Fact]
    public async Task AShutdownWithoutTheMutationHeaderIsForbiddenAndTheHostKeepsRunning()
    {
        await using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, Lifetime(factory.Services), Token);

        status.ShouldBe(HttpStatusCode.Forbidden);
        stopping.ShouldBeFalse();
        body.ShouldNotContain("stopped");
    }

    [Fact]
    public async Task ACrossOriginShutdownIsForbiddenAndTheHostKeepsRunning()
    {
        await using AppFactory factory = new();
        using HttpClient client = factory.CreateMutatingClient();
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        (HttpStatusCode status, _, bool stopping) = await PostAsync(client, Lifetime(factory.Services), Token);

        status.ShouldBe(HttpStatusCode.Forbidden);
        stopping.ShouldBeFalse();
    }

    [Fact]
    public async Task AShutdownWhileAnotherCredentialChangeHoldsTheGateIsRefused()
    {
        await using AppFactory factory = new();
        IHostApplicationLifetime lifetime = Lifetime(factory.Services);
        using HttpClient client = factory.CreateMutatingClient();
        using IDisposable permit = await factory.Services.GetRequiredService<CredentialMutationGate>().AcquireAsync(TimeSpan.Zero, Token);

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, lifetime, Token);

        status.ShouldBe(HttpStatusCode.Conflict);
        stopping.ShouldBeFalse();
        JsonNode.Parse(body)!["error"]!.GetValue<string>().ShouldBe("another credential change is in progress");
        permit.ShouldNotBeNull();
    }

    [Fact]
    public async Task StopAsksTheFollowerFirstThenStopsTheLeader()
    {
        await using FollowerAppFactory follower = new();
        SideEndpointTests.PeerLink link = new();
        await using AppFactory leader = SideEndpointTests.LeaderOver(follower, link);
        using HttpClient client = leader.CreateMutatingClient();
        IHostApplicationLifetime leaderLifetime = Lifetime(leader.Services);
        IHostApplicationLifetime followerLifetime = Lifetime(follower.Services);
        TaskCompletionSource<bool> followerAskedFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration onStopping = leaderLifetime.ApplicationStopping.Register(
            () => followerAskedFirst.TrySetResult(link.Sent.Any(sent => sent.Route == "/api/shutdown" && sent.Status == HttpStatusCode.OK)));

        using HttpResponseMessage response = await client.PostAsync(_stop, content: null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await followerAskedFirst.Task.WaitAsync(_applicationStoppingGrace, Token)).ShouldBeTrue();
        (await StoppingAsync(followerLifetime, Token)).ShouldBeTrue();
        JsonNode side = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["sides"]![0]!;
        side["side"]!.GetValue<string>().ShouldBe("wsl");
        side["detail"]!.GetValue<string>().ShouldBe("stopped");
    }

    [Fact]
    public async Task AFollowerRefusalStopsNothingAndReleasesTheLeadersGate()
    {
        await using FollowerAppFactory follower = new();
        SideEndpointTests.PeerLink link = new();
        await using AppFactory leader = SideEndpointTests.LeaderOver(follower, link);
        using HttpClient client = leader.CreateMutatingClient();
        IHostApplicationLifetime followerLifetime = Lifetime(follower.Services);
        await follower.Roots.Journal().WriteAsync(
            new ImportJournalEntry(
                _incomingAccount,
                _incoming,
                follower.Roots.ClaimedPath("b@example.com"),
                follower.Roots.ExportPath("a@example.com"),
                _outgoingAccount,
                _outgoing,
                IncomingAccount: null,
                OutgoingAccount: null,
                ImportStep.Exported,
                follower.Roots.Clock.GetUtcNow()),
            Token);

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, Lifetime(leader.Services), Token, _stop);

        status.ShouldBe(HttpStatusCode.Conflict);
        stopping.ShouldBeFalse();
        followerLifetime.ApplicationStopping.IsCancellationRequested.ShouldBeFalse();
        JsonNode.Parse(body)!["error"]!.GetValue<string>().ShouldBe("the wsl side refused to stop: a switch or import is in flight");
        using IDisposable permit = await leader.Services.GetRequiredService<CredentialMutationGate>().AcquireAsync(TimeSpan.Zero, Token);
    }

    [Fact]
    public async Task AFollowerThatDoesNotAnswerLeavesTheLeaderToStop()
    {
        await using FollowerAppFactory follower = new();
        SideEndpointTests.PeerLink link = new() { Offline = true };
        await using AppFactory leader = SideEndpointTests.LeaderOver(follower, link);
        using HttpClient client = leader.CreateMutatingClient();

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, Lifetime(leader.Services), Token, _stop);

        status.ShouldBe(HttpStatusCode.OK);
        stopping.ShouldBeTrue();
        JsonNode.Parse(body)!["sides"]![0]!["detail"]!.GetValue<string>().ShouldStartWith("not running: ");
    }

    [Fact]
    public async Task AnOpenLeaderJournalRefusesStopWithoutAskingTheFollower()
    {
        await using FollowerAppFactory follower = new();
        SideEndpointTests.PeerLink link = new();
        await using AppFactory leader = SideEndpointTests.LeaderOver(follower, link);
        using HttpClient client = leader.CreateMutatingClient();
        IHostApplicationLifetime lifetime = Lifetime(leader.Services);
        await new SwitchJournal(leader.AppData).WriteAsync(
            new SwitchJournalEntry(
                _outgoingAccount,
                _outgoing,
                Path.Combine(leader.ProfilesRoot, "a@example.com"),
                _incomingAccount,
                _incoming,
                Path.Combine(leader.ProfilesRoot, "b@example.com"),
                SwitchStep.Parked,
                DateTimeOffset.UnixEpoch),
            Token);

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, lifetime, Token, _stop);

        status.ShouldBe(HttpStatusCode.Conflict);
        stopping.ShouldBeFalse();
        JsonNode.Parse(body)!["error"]!.GetValue<string>().ShouldBe("a switch or import is in flight");
        link.Sent.ShouldNotContain(sent => sent.Route == "/api/shutdown");
        Lifetime(follower.Services).ApplicationStopping.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task ShutdownOnTheLeaderLeavesTheFollowerRunning()
    {
        await using FollowerAppFactory follower = new();
        SideEndpointTests.PeerLink link = new();
        await using AppFactory leader = SideEndpointTests.LeaderOver(follower, link);
        using HttpClient client = leader.CreateMutatingClient();

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, Lifetime(leader.Services), Token);

        status.ShouldBe(HttpStatusCode.OK);
        stopping.ShouldBeTrue();
        body.ShouldNotContain("sides");
        link.Sent.ShouldNotContain(sent => sent.Route == "/api/shutdown");
    }

    [Fact]
    public async Task StopWithMoreThanOneSideRefusesBeforeAskingAny()
    {
        await using FollowerAppFactory follower = new();
        SideEndpointTests.PeerLink link = new();
        await using AppFactory leader = SideEndpointTests.LeaderOver(follower, link);
        HttpClient toFollower = follower.CreateDefaultClient(link);
        leader.Overrides = services => services.Replace(ServiceDescriptor.Singleton(new PeerRegistry(
        [
            new Peer(new HttpPeerRotationInstance(SideName.Wsl, toFollower), null, follower.Roots.Store),
            new Peer(new HttpPeerRotationInstance(new SideName("other"), toFollower), null, follower.Roots.Store),
        ])));
        using HttpClient client = leader.CreateMutatingClient();

        (HttpStatusCode status, string body, bool stopping) = await PostAsync(client, Lifetime(leader.Services), Token, _stop);

        status.ShouldBe(HttpStatusCode.Conflict);
        stopping.ShouldBeFalse();
        JsonNode.Parse(body)!["error"]!.GetValue<string>().ShouldStartWith("more than one side is configured");
        link.Sent.ShouldNotContain(sent => sent.Route == "/api/shutdown");
        using IDisposable permit = await leader.Services.GetRequiredService<CredentialMutationGate>().AcquireAsync(TimeSpan.Zero, Token);
    }

    [Fact]
    public async Task TheFollowerHasNoStopRoute()
    {
        await using FollowerAppFactory factory = new();
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(_stop, content: null, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static IHostApplicationLifetime Lifetime(IServiceProvider services) =>
        services.GetRequiredService<IHostApplicationLifetime>();

    /// <summary>
    /// How long to wait for <see cref="IHostApplicationLifetime.ApplicationStopping"/>
    /// after a 200. <c>HttpResponse.OnCompleted</c> runs once the response has
    /// finished, and TestServer can hand the body to the client before that
    /// callback, so sampling the token at the end of <c>ReadAsStringAsync</c>
    /// races the callback.
    /// </summary>
    private static readonly TimeSpan _applicationStoppingGrace = TimeSpan.FromSeconds(5);

    private static async Task<(HttpStatusCode Status, string Body, bool Stopping)> PostAsync(
        HttpClient client,
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken,
        Uri? route = null)
    {
        using HttpResponseMessage response = await client.PostAsync(route ?? _shutdown, content: null, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        bool stopping = response.StatusCode == HttpStatusCode.OK
            ? await StoppingAsync(lifetime, cancellationToken)
            : lifetime.ApplicationStopping.IsCancellationRequested;
        return (response.StatusCode, body, stopping);
    }

    private static async Task<bool> StoppingAsync(IHostApplicationLifetime lifetime, CancellationToken cancellationToken)
    {
        if (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime.ApplicationStopping);
            try
            {
                await Task.Delay(_applicationStoppingGrace, wait.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return lifetime.ApplicationStopping.IsCancellationRequested;
    }
}
