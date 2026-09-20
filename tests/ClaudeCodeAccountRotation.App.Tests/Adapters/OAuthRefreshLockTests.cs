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
}
