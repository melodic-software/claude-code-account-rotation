using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// Claude Code's own token-refresh mutex, joined rather than sampled. The CLI
/// takes <c>&lt;live dir&gt;/.oauth_refresh.lock</c> through an exclusive
/// directory create (proper-lockfile: stale after 60 s, mtime refreshed every
/// 5 s), and a contending process gets a retryable error. The tool acquires
/// the same directory the same way, so a session's own refresh can never run
/// between the tool's park and unpark. The hold covers the journal, the
/// profile and owner writes, and the state-file patch retries, which can run
/// past <see cref="StaleAfter"/>. The directory's mtime is refreshed every
/// <see cref="HeartbeatInterval"/> while the lock is held, the same cadence
/// proper-lockfile uses. A deadline that abandoned the hold would leave the
/// journal open.
/// </summary>
internal sealed partial class OAuthRefreshLock
{
    public const string DirectoryName = ".oauth_refresh.lock";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    /// <summary>How often a holder refreshes the directory mtime. proper-lockfile uses <c>update: 5000</c>.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private const int WindowsErrorAlreadyExists = 183;
    private const int WindowsErrorAccessDenied = 5;
    private const int UnixErrorExists = 17;
    private const int MaxStaleRemovals = 3;
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(250);

    private readonly string _lockDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _heartbeatInterval;

    public OAuthRefreshLock(string liveConfigDirectory, TimeProvider timeProvider)
        : this(liveConfigDirectory, timeProvider, HeartbeatInterval)
    {
    }

    internal OAuthRefreshLock(string liveConfigDirectory, TimeProvider timeProvider, TimeSpan heartbeatInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveConfigDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        _lockDirectory = Path.Combine(Path.GetFullPath(liveConfigDirectory), DirectoryName);
        _timeProvider = timeProvider;
        _heartbeatInterval = heartbeatInterval;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership of the held lock transfers to the caller through the result; disposing it releases the lock.")]
    public async Task<Result<IAsyncDisposable, string>> AcquireAsync(TimeSpan waitBound, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _timeProvider.GetUtcNow() + waitBound;
        int staleRemovals = 0;
        while (true)
        {
            if (TryCreateExclusively(_lockDirectory))
            {
                Stamp();
                return Result<IAsyncDisposable, string>.Success(new Held(_lockDirectory, _timeProvider, _heartbeatInterval));
            }

            // A stale directory is removed and the create retried at once, but only a
            // bounded number of times, and a removal that fails (an open handle, a
            // read-only bit, a stray file inside) falls through to the deadline and the
            // poll delay rather than spinning on the calling thread.
            if (staleRemovals < MaxStaleRemovals && TryRemoveIfStale())
            {
                staleRemovals++;
                continue;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                return Result<IAsyncDisposable, string>.Failure(
                    "another process holds " + DirectoryName + " (a session is refreshing its token); waited " + waitBound.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s");
            }

            await Task.Delay(_pollInterval, _timeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// Puts the directory's mtime on this lock's own clock, right after the
    /// create. Staleness is decided by comparing that mtime against
    /// <see cref="TimeProvider.GetUtcNow"/>, and the create leaves it on the
    /// file system's clock instead — so without this the one field is written
    /// by one clock and read against another. On a real machine the two agree
    /// and nothing changes; anywhere the clock is injected they need not, and
    /// a holder that stamps itself into the past reads as stale the moment it
    /// is taken.
    /// </summary>
    private void Stamp()
    {
        try
        {
            Directory.SetLastWriteTimeUtc(_lockDirectory, _timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort, as the heartbeat's own re-stamp is: a lock whose
            // mtime could not be set is still held, and the create is what
            // made it exclusive.
        }
    }

    /// <summary>
    /// Removes the directory only if it is still stale at the moment of the delete:
    /// the mtime is re-read right before the call, so a directory another process
    /// re-created between the two reads is left alone. Returns whether it removed.
    /// </summary>
    private bool TryRemoveIfStale()
    {
        if (!IsStale())
        {
            return false;
        }

        try
        {
            if (!IsStale())
            {
                return false;
            }

            Directory.Delete(_lockDirectory);
            return true;
        }
        catch (IOException)
        {
            // Another process removed or re-created it first, or the directory cannot be
            // deleted (a stray file, an open handle); the caller waits and re-evaluates.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsStale()
    {
        if (!Directory.Exists(_lockDirectory))
        {
            return false;
        }

        DateTime lastWrite = Directory.GetLastWriteTimeUtc(_lockDirectory);
        return _timeProvider.GetUtcNow() - lastWrite > StaleAfter;
    }

    private static void TryRemove(string lockDirectory)
    {
        try
        {
            Directory.Delete(lockDirectory);
        }
        catch (IOException)
        {
            // Another process removed it first, or it holds a stray entry; nothing to do.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: a held lock that cannot be released here is reclaimed as stale later.
        }
    }

    private static bool TryCreateExclusively(string lockDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (CreateDirectoryWindows(lockDirectory, nint.Zero))
            {
                return true;
            }

            int error = Marshal.GetLastPInvokeError();
            return error switch
            {
                WindowsErrorAlreadyExists => false,
                // A name whose delete has been issued but whose last reference is
                // not gone yet is delete-pending, and a create against it answers
                // ACCESS_DENIED rather than ALREADY_EXISTS. That is what the holder
                // releasing the lock a moment ago looks like to the next acquirer,
                // so it is "not mine yet" and the caller polls inside its wait
                // bound. A permission fault that really is one produces the same
                // answer every time and ends as the bound's own refusal.
                WindowsErrorAccessDenied => false,
                _ => throw new IOException("CreateDirectoryW failed for " + lockDirectory + " (Win32 error " + error.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"),
            };
        }

        if (MakeDirectoryUnix(lockDirectory, 0x1C0) == 0)
        {
            return true;
        }

        int errno = Marshal.GetLastPInvokeError();
        return errno == UnixErrorExists
            ? false
            : throw new IOException("mkdir failed for " + lockDirectory + " (errno " + errno.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryWindows(string path, nint securityAttributes);

    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int MakeDirectoryUnix(string path, uint mode);

    private sealed class Held : IAsyncDisposable
    {
        private readonly string _lockDirectory;
        private readonly TimeProvider _timeProvider;
        private readonly ITimer _heartbeat;
        private int _released;

        public Held(string lockDirectory, TimeProvider timeProvider, TimeSpan heartbeatInterval)
        {
            _lockDirectory = lockDirectory;
            _timeProvider = timeProvider;
            // The acquire already stamped. The first beat waits one interval, then
            // repeats, so a hold that outlives the steal window still looks fresh.
            _heartbeat = timeProvider.CreateTimer(Beat, state: null, heartbeatInterval, heartbeatInterval);
        }

        private void Beat(object? state)
        {
            _ = state;
            if (Volatile.Read(ref _released) != 0 || !Directory.Exists(_lockDirectory))
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref _released) != 0)
                {
                    return;
                }

                Directory.SetLastWriteTimeUtc(_lockDirectory, _timeProvider.GetUtcNow().UtcDateTime);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort, as the stamp at acquire is: a lock whose mtime
                // could not be refreshed is still held. The next beat tries again.
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _heartbeat.Dispose();
                TryRemove(_lockDirectory);
            }

            return ValueTask.CompletedTask;
        }
    }
}
