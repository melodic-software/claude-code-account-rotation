using System.Net;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.DependencyInjection;
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
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.PostAsync(_shutdown, content: null, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        bool stopping = lifetime.ApplicationStopping.IsCancellationRequested;
        if (response.StatusCode == HttpStatusCode.OK && !stopping)
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

            stopping = lifetime.ApplicationStopping.IsCancellationRequested;
        }

        return (response.StatusCode, body, stopping);
    }
}
