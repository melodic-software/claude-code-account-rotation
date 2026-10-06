using ClaudeCodeAccountRotation.App.Hosting;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Hosting;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// A follower stops for a build installed over it only when that build is the
/// leader's version and nothing is in flight. Anything else keeps it running,
/// because a follower that matches the running leader is the one that works.
/// </summary>
public sealed class FollowerUpgradeWatcherTests : IDisposable
{
    private const string Own = "2.0.9+old";
    private const string Leader = "2.1.0+new";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ccar-upgrade-" + Guid.NewGuid().ToString("N"));
    private readonly string _binary;
    private readonly Queue<Result<string, string>> _probes = new();
    private int _probeCount;
    private int _exitCount;
    private bool _exitAllowed = true;

    public FollowerUpgradeWatcherTests()
    {
        Directory.CreateDirectory(_directory);
        _binary = Path.Combine(_directory, "claude-code-account-rotation-linux-x64");
        File.WriteAllText(_binary, "old build");
        File.SetLastWriteTimeUtc(_binary, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task AnUnchangedBinaryIsNotProbed()
    {
        using FollowerUpgradeWatcher watcher = Watcher(Leader);

        (await watcher.CheckOnceAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        _probeCount.ShouldBe(0);
        _exitCount.ShouldBe(0);
    }

    [Fact]
    public async Task TheLeadersBuildInstalledOverItStopsTheFollower()
    {
        using FollowerUpgradeWatcher watcher = Watcher(Leader);
        Replace("new build");
        _probes.Enqueue(Result<string, string>.Success(Leader));

        (await watcher.CheckOnceAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        _exitCount.ShouldBe(1);
    }

    [Fact]
    public async Task ABuildTheLeaderDoesNotRunKeepsTheFollowerAndIsNotProbedAgain()
    {
        // The side was updated first: the old follower still matches the old
        // leader, which restarts it once it runs the new build itself.
        using FollowerUpgradeWatcher watcher = Watcher(Own);
        Replace("new build");
        _probes.Enqueue(Result<string, string>.Success(Leader));

        (await watcher.CheckOnceAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await watcher.CheckOnceAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        _probeCount.ShouldBe(1);
        _exitCount.ShouldBe(0);
    }

    [Fact]
    public async Task ABusyFollowerIsAskedAgainOnTheNextTick()
    {
        using FollowerUpgradeWatcher watcher = Watcher(Leader);
        Replace("new build");
        _probes.Enqueue(Result<string, string>.Success(Leader));
        _probes.Enqueue(Result<string, string>.Success(Leader));
        _exitAllowed = false;

        (await watcher.CheckOnceAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        _exitAllowed = true;
        (await watcher.CheckOnceAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        _exitCount.ShouldBe(2);
    }

    [Fact]
    public async Task AProbeThatFailsIsRetried()
    {
        using FollowerUpgradeWatcher watcher = Watcher(Leader);
        Replace("new build");
        _probes.Enqueue(Result<string, string>.Failure("Text file busy"));
        _probes.Enqueue(Result<string, string>.Success(Leader));

        (await watcher.CheckOnceAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await watcher.CheckOnceAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task TheSameBuildReinstalledIsIgnored()
    {
        using FollowerUpgradeWatcher watcher = Watcher(Leader);
        Replace("same build, new timestamp");
        _probes.Enqueue(Result<string, string>.Success(Own));

        (await watcher.CheckOnceAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        _exitCount.ShouldBe(0);
    }

    [Fact]
    public async Task AFollowerNoLeaderStartedNeverStops()
    {
        using FollowerUpgradeWatcher watcher = Watcher(leaderVersion: null);
        Replace("new build");
        _probes.Enqueue(Result<string, string>.Success(Leader));

        (await watcher.CheckOnceAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        _probeCount.ShouldBe(0);
    }

    [Fact]
    public async Task TheExitIsRefusedWhileTheGateIsHeldOrAnImportIsOpen()
    {
        using CredentialMutationGate gate = new();
        ImportJournal journal = new(_directory);
        StoppingLifetime lifetime = new();
        try
        {
            using (await gate.AcquireAsync(TimeSpan.Zero, TestContext.Current.CancellationToken))
            {
                (await FollowerUpgradeWatcher.TryExitAsync(gate, journal, lifetime, TestContext.Current.CancellationToken)).ShouldBeFalse();
            }

            await journal.WriteAsync(
                new ImportJournalEntry(
                    new AccountEmail("a@example.com"),
                    RefreshTokenFingerprint.FromRefreshToken("refresh-a"),
                    Path.Combine(_directory, "claimed"),
                    Path.Combine(_directory, "export"),
                    null,
                    null,
                    null,
                    null,
                    ImportStep.Staged,
                    DateTimeOffset.UnixEpoch),
                TestContext.Current.CancellationToken);
            (await FollowerUpgradeWatcher.TryExitAsync(gate, journal, lifetime, TestContext.Current.CancellationToken)).ShouldBeFalse();
            lifetime.Stopped.ShouldBeFalse();
            Environment.ExitCode.ShouldBe(0);

            await journal.ClearAsync(TestContext.Current.CancellationToken);
            (await FollowerUpgradeWatcher.TryExitAsync(gate, journal, lifetime, TestContext.Current.CancellationToken)).ShouldBeTrue();
            lifetime.Stopped.ShouldBeTrue();
            Environment.ExitCode.ShouldBe(FollowerUpgrade.ExitCode);
        }
        finally
        {
            // The test host's own exit code.
            Environment.ExitCode = 0;
        }
    }

    private FollowerUpgradeWatcher Watcher(string? leaderVersion) => new(
        leaderVersion,
        _binary,
        Own,
        (_, _) =>
        {
            _probeCount++;
            return Task.FromResult(_probes.Dequeue());
        },
        _ =>
        {
            _exitCount++;
            return Task.FromResult(_exitAllowed);
        },
        TimeSpan.FromHours(1),
        new RecordingLogger<FollowerUpgradeWatcher>());

    /// <summary>A replace the way an installer makes one: a new file renamed over the old.</summary>
    private void Replace(string content)
    {
        string staged = _binary + ".new";
        File.WriteAllText(staged, content);
        File.SetLastWriteTimeUtc(staged, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        File.Move(staged, _binary, overwrite: true);
    }

    private sealed class StoppingLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }
}
