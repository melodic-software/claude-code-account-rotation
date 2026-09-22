using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// One running instance per app-data directory: an exclusively opened lock
/// file, plus a sibling file holding the running instance's URL so a second
/// launch refuses and prints where the first one is listening.
/// </summary>
internal sealed class InstanceLock : IDisposable
{
    public const string FileName = "instance.lock";
    public const string UrlFileName = "instance.url";

    private readonly FileStream _stream;
    private readonly string _urlFilePath;

    private InstanceLock(FileStream stream, string lockFilePath, string urlFilePath)
    {
        _stream = stream;
        LockFilePath = lockFilePath;
        _urlFilePath = urlFilePath;
    }

    public string LockFilePath { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership of the lock transfers to the caller through the result; disposing it releases the file.")]
    public static Result<InstanceLock, string> TryAcquire(string appDataDirectory, string listenUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(listenUrl);
        Directory.CreateDirectory(appDataDirectory);
        string lockFilePath = Path.Combine(appDataDirectory, FileName);
        string urlFilePath = Path.Combine(appDataDirectory, UrlFileName);

        // Exclusive on every platform: Windows refuses a second open outright (a sharing
        // violation), and .NET on Unix takes flock(LOCK_EX) for FileShare.None, which the
        // kernel releases with the handle even after a crash. Delete-on-close is Windows-only
        // because .NET on Unix unlinks the path at open, so a second opener would create a
        // fresh inode and lock that one instead; there the empty lock file simply persists.
        FileStreamOptions options = new()
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream? stream = null;
        try
        {
            try
            {
                stream = new FileStream(lockFilePath, options);
            }
            catch (IOException)
            {
                return Result<InstanceLock, string>.Failure("another instance is running at " + ReadRunningUrl(urlFilePath) + " (lock file " + lockFilePath + ")");
            }

            // The URL sits beside the lock, not inside it: the lock file is held without
            // sharing, so a refused second instance could not read it from there.
            File.WriteAllText(urlFilePath, listenUrl);

            InstanceLock acquired = new(stream, lockFilePath, urlFilePath);
            stream = null;
            return Result<InstanceLock, string>.Success(acquired);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>
    /// Rewrites the URL a second launch is told about. Port 0 is only a real
    /// port after the socket binds, so the address written at acquire time is
    /// replaced with the one the server actually took.
    /// </summary>
    public void PublishListenUrl(string listenUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenUrl);
        File.WriteAllText(_urlFilePath, listenUrl);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_urlFilePath);
        }
        catch (IOException)
        {
            // A stale URL file only ever names an address that stops answering; the lock decides.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the next instance overwrites it.
        }

        _stream.Dispose();
    }

    private static string ReadRunningUrl(string urlFilePath)
    {
        try
        {
            string url = File.ReadAllText(urlFilePath).Trim();
            return url.Length == 0 ? "an unknown address" : url;
        }
        catch (IOException)
        {
            return "an unknown address";
        }
        catch (UnauthorizedAccessException)
        {
            return "an unknown address";
        }
    }
}
