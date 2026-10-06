using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.App.Tests.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// The leader starts every side it has a launch for, so a reboot or a release
/// upgrade needs no <c>Start WSL side</c> click. A side that will not start
/// must not stop the leader: the page is how the operator finds out why.
/// </summary>
public sealed class SideAutoStartTests
{
    [Fact]
    public async Task ASideWithALaunchIsStartedWithTheLeader()
    {
        CountingHost host = new(Result<Unit, string>.Success(Unit.Value));
        using PeerRegistry peers = new([new Peer(new FakePeerRotationInstance("mailbox"), host, "/store")]);

        await new SideAutoStart(peers, new RecordingLogger<SideAutoStart>()).StartAsync(TestContext.Current.CancellationToken);

        host.Starts.ShouldBe(1);
    }

    [Fact]
    public async Task ASideWithNoLaunchIsLeftAlone()
    {
        using PeerRegistry peers = new([new Peer(new FakePeerRotationInstance("mailbox"), null, "/store")]);
        RecordingLogger<SideAutoStart> logger = new();

        await new SideAutoStart(peers, logger).StartAsync(TestContext.Current.CancellationToken);

        logger.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedStartIsLoggedAndDoesNotStopTheLeader()
    {
        CountingHost host = new(Result<Unit, string>.Failure("wsl.exe did not start"));
        using PeerRegistry peers = new([new Peer(new FakePeerRotationInstance("mailbox"), host, "/store")]);
        RecordingLogger<SideAutoStart> logger = new();

        await Should.NotThrowAsync(() => new SideAutoStart(peers, logger).StartAsync(TestContext.Current.CancellationToken));

        host.Starts.ShouldBe(1);
        logger.Lines.ShouldContain(line => line.Contains("wsl side was not started with the leader: wsl.exe did not start", StringComparison.Ordinal));
    }

    private sealed class CountingHost(Result<Unit, string> answer) : IPeerProcessHost
    {
        public int Starts { get; private set; }

        public SideName Side => SideName.Wsl;

        public bool IsRunning => false;

        public Task<Result<Unit, string>> StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            return Task.FromResult(answer);
        }
    }
}
