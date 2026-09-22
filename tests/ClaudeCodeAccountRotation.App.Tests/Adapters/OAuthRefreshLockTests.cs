using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class OAuthRefreshLockTests : IDisposable
{
    private readonly string _liveDirectory = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _lockDirectory;

    public OAuthRefreshLockTests()
    {
        Directory.CreateDirectory(_liveDirectory);
        _lockDirectory = Path.Combine(_liveDirectory, OAuthRefreshLock.DirectoryName);
    }

    [Fact]
    public async Task AcquireCreatesTheLockDirectoryAndDisposeRemovesIt()
    {
        OAuthRefreshLock refreshLock = new(_liveDirectory, TimeProvider.System);

        Result<IAsyncDisposable, string> held = await refreshLock.AcquireAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        held.IsSuccess.ShouldBeTrue();
        Directory.Exists(_lockDirectory).ShouldBeTrue();
        await held.Value.DisposeAsync();
        Directory.Exists(_lockDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task AFreshlyTakenLockIsStampedOnTheClockItsStalenessIsJudgedBy()
    {
        // Staleness is the directory's mtime against the provider's now, but a
        // create leaves that mtime on the file system's clock. Where the two
        // are not the same clock the lock was born stale or born in the future
        // — and it showed up as a heartbeat test that passed only before
        // 09:00 UTC, because that is when the fixed test clock happened to sit.
        TestClock clock = new(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        OAuthRefreshLock refreshLock = new(_liveDirectory, clock);

        Result<IAsyncDisposable, string> held = await refreshLock.AcquireAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        held.IsSuccess.ShouldBeTrue();
        Directory.GetLastWriteTimeUtc(_lockDirectory).ShouldBe(clock.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(1));
        await held.Value.DisposeAsync();
    }

    [Fact]
    public async Task WaitsForTheBoundThenRefusesWhileAFreshLockIsHeld()
    {
        Directory.CreateDirectory(_lockDirectory);
        OAuthRefreshLock refreshLock = new(_liveDirectory, TimeProvider.System);
        var stopwatch = Stopwatch.StartNew();

        Result<IAsyncDisposable, string> held = await refreshLock.AcquireAsync(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

        held.IsFailure.ShouldBeTrue();
        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(350));
        Directory.Exists(_lockDirectory).ShouldBeTrue();
    }

    [Fact]
    public async Task ALongHoldRestampsTheDirectorySoItIsNotStolen()
    {
        // The steal window is 60 s of the lock's own clock. A holder that does
        // not refresh the directory mtime is stolen once that clock moves past
        // it. The beat is what keeps the directory fresh, so the test moves the
        // clock and waits for the mtime to follow, instead of sleeping out the
        // window.
        TestClock clock = new(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        OAuthRefreshLock refreshLock = new(_liveDirectory, clock, TimeSpan.FromMilliseconds(30));
        Result<IAsyncDisposable, string> held = await refreshLock.AcquireAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        held.IsSuccess.ShouldBeTrue();
        await using IAsyncDisposable hold = held.Value;

        clock.Advance(OAuthRefreshLock.StaleAfter + TimeSpan.FromSeconds(30));
        DateTime target = clock.GetUtcNow().UtcDateTime;
        DateTime observed = DateTime.MinValue;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            observed = Directory.GetLastWriteTimeUtc(_lockDirectory);
            if (Math.Abs((observed - target).TotalSeconds) < 2)
            {
                break;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        observed.ShouldBe(target, TimeSpan.FromSeconds(2));

        MovingClock contenderClock = new(clock.GetUtcNow());
        OAuthRefreshLock contender = new(_liveDirectory, contenderClock);
        Result<IAsyncDisposable, string> stolen = await contender.AcquireAsync(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

        stolen.IsFailure.ShouldBeTrue();
        Directory.Exists(_lockDirectory).ShouldBeTrue();
    }

    [Fact]
    public async Task StealsALockDirectoryOlderThanTheStaleThreshold()
    {
        Directory.CreateDirectory(_lockDirectory);
        Directory.SetLastWriteTimeUtc(_lockDirectory, DateTime.UtcNow.AddSeconds(-61));
        OAuthRefreshLock refreshLock = new(_liveDirectory, TimeProvider.System);

        Result<IAsyncDisposable, string> held = await refreshLock.AcquireAsync(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

        held.IsSuccess.ShouldBeTrue();
        await held.Value.DisposeAsync();
        Directory.Exists(_lockDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task ADirectoryReplacedBetweenTheTwoStaleChecksIsLeftInPlace()
    {
        // Removal stats, stats again, then deletes. The hook sits between those
        // two reads and replaces the stale directory with a fresh one. That
        // fresh directory is not the one the first stat saw, so it stays, and
        // the acquirer does not treat it as stolen.
        Directory.CreateDirectory(_lockDirectory);
        Directory.SetLastWriteTimeUtc(_lockDirectory, DateTime.UtcNow.AddSeconds(-61));
        DateTime stamped = DateTime.MinValue;
        int hooks = 0;
        OAuthRefreshLock refreshLock = new(
            _liveDirectory,
            TimeProvider.System,
            OAuthRefreshLock.HeartbeatInterval,
            () =>
            {
                hooks++;
                Directory.Delete(_lockDirectory);
                Directory.CreateDirectory(_lockDirectory);
                stamped = DateTime.UtcNow;
                Directory.SetLastWriteTimeUtc(_lockDirectory, stamped);
            });

        Result<IAsyncDisposable, string> held = await refreshLock.AcquireAsync(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

        if (held.IsSuccess)
        {
            await held.Value.DisposeAsync();
        }

        held.IsFailure.ShouldBeTrue();
        hooks.ShouldBe(1);
        Directory.Exists(_lockDirectory).ShouldBeTrue();
        Directory.GetLastWriteTimeUtc(_lockDirectory).ShouldBe(stamped, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TwoAcquisitionsSerialize()
    {
        OAuthRefreshLock refreshLock = new(_liveDirectory, TimeProvider.System);
        Result<IAsyncDisposable, string> first = await refreshLock.AcquireAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        first.IsSuccess.ShouldBeTrue();

        Task<Result<IAsyncDisposable, string>> second = refreshLock.AcquireAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        second.IsCompleted.ShouldBeFalse();
        await first.Value.DisposeAsync();

        Result<IAsyncDisposable, string> held = await second;
        held.IsSuccess.ShouldBeTrue();
        await held.Value.DisposeAsync();
        Directory.Exists(_lockDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task RefusesAfterTheBoundWhenAStaleLockCannotBeRemoved()
    {
        // A stray file inside the directory makes the non-recursive delete fail, the
        // way an open handle or a read-only bit does on a real machine.
        Directory.CreateDirectory(_lockDirectory);
        await File.WriteAllTextAsync(Path.Combine(_lockDirectory, "stray"), "x", TestContext.Current.CancellationToken);
        Directory.SetLastWriteTimeUtc(_lockDirectory, DateTime.UtcNow.AddSeconds(-61));
        OAuthRefreshLock refreshLock = new(_liveDirectory, TimeProvider.System);

        Task<Result<IAsyncDisposable, string>> acquire = refreshLock.AcquireAsync(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);
        Task finished = await Task.WhenAny(acquire, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.ShouldBeSameAs(acquire, "the acquisition must honor the wait bound instead of spinning");
        (await acquire).IsFailure.ShouldBeTrue();
        Directory.Exists(_lockDirectory).ShouldBeTrue();
    }

    public static bool OnWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// A create that cannot make the directory is a refusal, never a throw.
    /// <para>
    /// The case this exists for is not a misconfigured machine: it is the
    /// ordinary release. A directory whose delete has been issued but whose
    /// last reference is not gone yet is delete-pending, and Windows answers a
    /// create against that name with <c>ACCESS_DENIED</c> rather than
    /// <c>ALREADY_EXISTS</c>. A contending acquirer polling every 250 ms lands
    /// in that window on a loaded machine, and treating it as a fault turned an
    /// ordinary hand-off into an <c>IOException</c> out of
    /// <c>AcquireAsync</c>. A denied parent is the deterministic way to
    /// reach the same branch.
    /// </para>
    /// </summary>
    [Fact(SkipUnless = nameof(OnWindows), Skip = "Access control lists are a Windows behavior")]
    public async Task RefusesWithinTheBoundWhenTheLockDirectoryCannotBeCreated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
        FileSystemAccessRule deny = new(user, FileSystemRights.CreateDirectories, AccessControlType.Deny);
        DirectoryInfo directory = new(_liveDirectory);
        DirectorySecurity security = directory.GetAccessControl();
        security.AddAccessRule(deny);
        directory.SetAccessControl(security);
        try
        {
            OAuthRefreshLock refreshLock = new(_liveDirectory, TimeProvider.System);

            Task<Result<IAsyncDisposable, string>> acquire = refreshLock.AcquireAsync(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);
            Task finished = await Task.WhenAny(acquire, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

            finished.ShouldBeSameAs(acquire, "a denied create must honor the wait bound instead of throwing or spinning");
            (await acquire).IsFailure.ShouldBeTrue();
        }
        finally
        {
            DirectorySecurity restored = directory.GetAccessControl();
            restored.RemoveAccessRule(deny);
            directory.SetAccessControl(restored);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_liveDirectory))
        {
            Directory.Delete(_liveDirectory, recursive: true);
        }
    }

    /// <summary>A clock that moves with wall time from a chosen instant, so a wait bound can expire.</summary>
    private sealed class MovingClock(DateTimeOffset start) : TimeProvider
    {
        private readonly long _timestamp = Stopwatch.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => start + Stopwatch.GetElapsedTime(_timestamp);
    }
}
