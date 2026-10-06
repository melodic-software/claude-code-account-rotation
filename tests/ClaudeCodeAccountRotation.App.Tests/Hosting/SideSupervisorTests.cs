using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.App.Tests.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// The leader brings its side up on its own version whichever side a release
/// was installed on first, and never stops a side that refuses to stop.
/// </summary>
public sealed class SideSupervisorTests
{
    private const string Leader = "2.1.0+new";
    private const string Old = "2.0.9+old";

    [Fact]
    public async Task ASideThatIsNotRunningIsStartedAtStartup()
    {
        using var side = Side.Offline(installed: Leader);
        using SideSupervisor supervisor = side.Supervisor();

        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        await Eventually(() => side.Host.Starts == 1);
        side.Instance.Calls.ShouldNotContain("Shutdown");
    }

    [Fact]
    public async Task ASideOnThisVersionIsLeftRunning()
    {
        using var side = Side.Running(Leader, installed: Leader);
        using SideSupervisor supervisor = side.Supervisor();

        Result<string, string> outcome = await supervisor.BringUpAsync(SideName.Wsl, TestContext.Current.CancellationToken);

        outcome.Value.ShouldBe("already running " + Leader);
        side.Host.Starts.ShouldBe(0);
        side.Instance.Calls.ShouldNotContain("Shutdown");
    }

    [Fact]
    public async Task ASideInstalledFirstIsStoppedAndStartedOnTheNewBuildWhenTheLeaderCatchesUp()
    {
        // WSL was updated first: the follower kept running the old build, and
        // the leader now starting on the new one finds it.
        using var side = Side.Running(Old, installed: Leader);
        using SideSupervisor supervisor = side.Supervisor();

        Result<string, string> outcome = await supervisor.BringUpAsync(SideName.Wsl, TestContext.Current.CancellationToken);

        outcome.Value.ShouldBe("started");
        side.Instance.Calls.ShouldContain("Shutdown");
        side.Host.Starts.ShouldBe(1);
        side.Instance.Version.ShouldBe(Leader);
    }

    [Fact]
    public async Task AStaleSideWithTheOldBuildStillInstalledIsRestartedSoItLearnsTheLeadersVersion()
    {
        // The leader was updated first. The follower is restarted from the old
        // binary anyway, so the leader's version reaches it and it can stop
        // itself for the new build when that lands.
        using var side = Side.Running("2.0.8+older", installed: Old);
        using SideSupervisor supervisor = side.Supervisor();

        Result<string, string> outcome = await supervisor.BringUpAsync(SideName.Wsl, TestContext.Current.CancellationToken);

        outcome.Value.ShouldBe("started");
        side.Host.Starts.ShouldBe(1);
        side.Instance.Version.ShouldBe(Old);
    }

    [Fact]
    public async Task ASideThatRefusesToStopIsLeftRunningAndNotStarted()
    {
        using var side = Side.Running(Old, installed: Leader);
        side.Instance.OnShutdown = static () => Result<string, string>.Failure("a switch or import is in flight");
        using SideSupervisor supervisor = side.Supervisor();

        Result<string, string> outcome = await supervisor.BringUpAsync(SideName.Wsl, TestContext.Current.CancellationToken);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error.ShouldContain("was left running: a switch or import is in flight");
        side.Host.Starts.ShouldBe(0);
        side.Instance.DashboardError.ShouldBeNull();
    }

    [Fact]
    public async Task ASideThatStillAnswersAfterTheBudgetIsNotStartedASecondTime()
    {
        using var side = Side.Running(Old, installed: Leader);
        side.Instance.OnShutdown = static () => Result<string, string>.Success("stopped");
        using SideSupervisor supervisor = side.Supervisor();

        Result<string, string> outcome = await supervisor.BringUpAsync(SideName.Wsl, TestContext.Current.CancellationToken);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error.ShouldContain("still answers");
        side.Host.Starts.ShouldBe(0);
    }

    [Fact]
    public async Task OnlyTheUpgradeExitStartsTheSideAgain()
    {
        using var side = Side.Offline(installed: Leader);
        using SideSupervisor supervisor = side.Supervisor();
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await Eventually(() => side.Host.Starts == 1);

        // A crash, an operator's stop, the wrapper finding another follower: left alone.
        side.Instance.DashboardError = "connection refused";
        side.Host.Exit(0);
        side.Host.Exit(1);
        side.Host.Exit(137);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        side.Host.Starts.ShouldBe(1);

        side.Host.Exit(FollowerUpgrade.ExitCode);
        await Eventually(() => side.Host.Starts == 2);
    }

    [Fact]
    public async Task OverlappingPassesStopAndStartTheSideOnce()
    {
        using var side = Side.Running(Old, installed: Leader);
        using SideSupervisor supervisor = side.Supervisor();

        Result<string, string>[] outcomes = await Task.WhenAll(
            supervisor.BringUpAsync(SideName.Wsl, TestContext.Current.CancellationToken),
            supervisor.BringUpAsync(SideName.Wsl, TestContext.Current.CancellationToken),
            supervisor.BringUpAsync(SideName.Wsl, TestContext.Current.CancellationToken));

        side.Instance.Calls.Count(static call => call == "Shutdown").ShouldBe(1);
        side.Host.Starts.ShouldBe(1);
        outcomes.Count(static outcome => outcome.IsSuccess && outcome.Value == "already running " + Leader).ShouldBe(2);
    }

    [Fact]
    public async Task ASideWithNoLaunchIsNeverStartedOrStopped()
    {
        FakePeerRotationInstance instance = new("mailbox") { Version = Old };
        using PeerRegistry peers = new([new Peer(instance, null, "/store")]);
        using SideSupervisor supervisor = new(peers, Leader, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(100), new RecordingLogger<SideSupervisor>(), CancellationToken.None);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        Result<string, string> outcome = await supervisor.BringUpAsync(SideName.Wsl, TestContext.Current.CancellationToken);

        outcome.Error.ShouldContain("has no launch configured");
        instance.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task NothingStartsOnceTheLeaderIsStopping()
    {
        using var side = Side.Offline(installed: Leader);
        using SideSupervisor supervisor = side.Supervisor();
        await supervisor.StopAsync(TestContext.Current.CancellationToken);

        side.Host.Exit(FollowerUpgrade.ExitCode);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        side.Host.Starts.ShouldBe(0);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the condition did not hold in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A side and the binary installed on it. A stop the side accepts takes it
    /// offline, and a start brings it up on whatever is installed.
    /// </summary>
    private sealed class Side : IDisposable
    {
        private readonly PeerRegistry _registry;

        private Side(FakePeerRotationInstance instance, string installed)
        {
            Instance = instance;
            Host = new FakeHost(instance, installed);
            _registry = new PeerRegistry([new Peer(Instance, Host, "/store")]);
        }

        public FakePeerRotationInstance Instance { get; }

        public FakeHost Host { get; }

        public static Side Offline(string installed) =>
            new(new FakePeerRotationInstance("mailbox") { DashboardError = "connection refused" }, installed);

        public static Side Running(string version, string installed)
        {
            var side = new Side(new FakePeerRotationInstance("mailbox") { Version = version }, installed);
            side.Instance.OnShutdown = () =>
            {
                side.Instance.DashboardError = "connection refused";
                return Result<string, string>.Success("stopped");
            };
            return side;
        }

        public SideSupervisor Supervisor() => new(
            _registry,
            Leader,
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(100),
            new RecordingLogger<SideSupervisor>(),
            CancellationToken.None);

        public void Dispose() => _registry.Dispose();
    }

    private sealed class FakeHost(FakePeerRotationInstance instance, string installed) : IPeerProcessHost
    {
        private int _starts;

        public int Starts => Volatile.Read(ref _starts);

        public SideName Side => SideName.Wsl;

        public bool IsRunning => false;

        public event EventHandler<PeerExitedEventArgs>? Exited;

        public Task<Result<Unit, string>> StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            instance.Version = installed;
            instance.DashboardError = null;
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }

        public void Exit(int code) => Exited?.Invoke(this, new PeerExitedEventArgs(code));
    }
}
