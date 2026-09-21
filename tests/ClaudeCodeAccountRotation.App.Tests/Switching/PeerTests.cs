using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Peers;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Configuration;
using ClaudeCodeAccountRotation.Core.Switching;
using Shouldly;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The two places a path or a command line crosses the boundary between the
/// two operating systems, which is the one place this leader's own conventions
/// are the wrong ones to apply.
/// </summary>
public sealed class PeerTests
{
    [Fact]
    public void APathForThePeerIsSpelledTheWayThePeerWillOpenIt()
    {
        // Forward slashes whatever this side runs on. Path.Combine would hand a
        // Linux follower backslashes on Windows, and the follower's own mailbox
        // check compares the parent directory literally - so the request would
        // be refused for a path that names exactly the right file.
        Peer peer = new(new StubInstance(), null, "/mnt/c/store");
        string native = Path.Combine("C:", "store", ".transit", "wsl", "b@example.com.credentials.json");

        string spelled = peer.InPeerNamespace(native);

        spelled.ShouldBe("/mnt/c/store/.transit/wsl/b@example.com.credentials.json");
        spelled.ShouldNotContain("\\");
    }

    [Fact]
    public void ATrailingSeparatorOnTheStorePathDoesNotDoubleUp()
    {
        Peer peer = new(new StubInstance(), null, "/mnt/c/store/");

        peer.InPeerNamespace("x" + FileSystemCredentialPairStore.FileName)
            .ShouldBe("/mnt/c/store/.transit/wsl/x" + FileSystemCredentialPairStore.FileName);
    }

    [Fact]
    public void TheLaunchCommandNamesTheDistributionTheUserTheBinaryAndThePort()
    {
        IReadOnlyList<string> arguments = WslDistributionPeerHost.Arguments(
            new PeerLaunch("Some-Distribution", "someone", "/opt/rotation/claude-code-account-rotation", 48212));

        arguments.ShouldBe(["-d", "Some-Distribution", "-u", "someone", "--exec", "/opt/rotation/claude-code-account-rotation", "--port", "48212"]);
    }

    [Fact]
    public void ALaunchWithAConfigPathPassesItSoTheFollowerDoesNotWriteItselfALeadersDefault()
    {
        // A follower started with no --config creates one from the defaults,
        // and the default role is leader. Over temp roots that is how a leader
        // ends up squatting the follower's port.
        IReadOnlyList<string> arguments = WslDistributionPeerHost.Arguments(
            new PeerLaunch("Some-Distribution", "someone", "/opt/rotation/claude-code-account-rotation", 48212, "/mnt/c/tmp/follower.json"));

        arguments.TakeLast(2).ShouldBe(["--config", "/mnt/c/tmp/follower.json"]);
    }

    /// <summary>A peer that is never called: these facts are about paths and argument lists, not traffic.</summary>
    private sealed class StubInstance : Core.Peers.IPeerRotationInstance
    {
        public SideName Side => SideName.Wsl;

        public Task<Core.Result<Core.Peers.PeerDashboard, string>> ReadDashboardAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Core.Result<Core.Peers.ImportAnswer, string>> ImportAsync(Core.Peers.ImportRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Core.Result<Core.Peers.ImportResult, string>> CommitImportAsync(Core.Identity.AccountEmail email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Core.Result<Core.Unit, string>> AbortImportAsync(Core.Identity.AccountEmail email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Core.Result<Core.Peers.ImportStatus, string>> ImportStatusAsync(Core.Identity.AccountEmail email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
