using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// The byte-level half of <see cref="AtomicJsonFile"/>: temp file beside the
/// target, flushed to disk, renamed into place, with the same sharing-violation
/// retry. The state-file patch uses it because it splices bytes rather than
/// serializing a node.
/// </summary>
internal static class AtomicBytesFile
{
    private static readonly TimeSpan _retryBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _initialBackoff = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan _maximumBackoff = TimeSpan.FromMilliseconds(400);

    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        string temporaryPath = await WriteTemporaryAsync(fullPath, content, cancellationToken);
        try
        {
            await MoveIntoPlaceWithRetryAsync(temporaryPath, fullPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// The staging half of <see cref="WriteAsync"/>: writes and flushes the content
    /// into a temp file beside <paramref name="path"/> and returns its path, for a
    /// caller that must check something between the staging and the rename. The
    /// caller owns the temp file from here: it moves it into place or deletes it.
    /// </summary>
    internal static async Task<string> WriteTemporaryAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The path has no parent directory.", nameof(path));
        string temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await WriteTemporaryFileAsync(temporaryPath, content, cancellationToken);
            return temporaryPath;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static async Task WriteTemporaryFileAsync(string temporaryPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        FileStream stream = CreateOwnerOnly(temporaryPath);
        await using (stream)
        {
            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            // The durability step: FlushAsync has no flush-to-disk overload, and the
            // fsync must complete before the rename makes the file visible.
#pragma warning disable CA1849 // Call async methods when in an async method
            stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
        }
    }

    /// <summary>
    /// Creates the file readable and writable by this user alone. These files
    /// hold credential pairs and the recovery copy of a rotated one, and a
    /// temp inherits the parent directory's permissions unless it is told
    /// otherwise. A rename carries the permissions with the file, so a target
    /// this writer creates is owner-only too; Win32 <c>ReplaceFile</c>, which
    /// the Windows path uses when the target already exists, deliberately
    /// preserves the replaced file's own DACL, so a file another program
    /// created keeps the permissions that program gave it.
    /// </summary>
    internal static FileStream CreateOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateOwnerOnlyOnWindows(path);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnlyOnWindows(string path)
    {
        // Inheritance off and one allow rule for this user: no Users, no
        // Authenticated Users, whatever the parent directory grants.
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows identity has no user SID, so an owner-only file cannot be created.");
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.Write | FileSystemRights.Synchronize,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            security);
    }

    /// <summary>
    /// Deletes a file under the same sharing-violation retry as a replace: a scanner
    /// holding a just-written file for a moment must not turn a finished operation
    /// into a failure.
    /// </summary>
    internal static async Task DeleteWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        TimeSpan waited = TimeSpan.Zero;
        TimeSpan backoff = _initialBackoff;
        while (true)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException exception) when (IsTransientSharingFailure(exception) && waited < _retryBudget)
            {
                TimeSpan delay = backoff + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(25));
                await Task.Delay(delay, cancellationToken);
                waited += delay;
                backoff = backoff * 2 > _maximumBackoff ? _maximumBackoff : backoff * 2;
            }
        }
    }

    internal static async Task MoveIntoPlaceWithRetryAsync(string temporaryPath, string path, CancellationToken cancellationToken)
    {
        TimeSpan waited = TimeSpan.Zero;
        TimeSpan backoff = _initialBackoff;
        while (true)
        {
            try
            {
                MoveIntoPlace(temporaryPath, path);
                return;
            }
            catch (IOException exception) when (IsTransientSharingFailure(exception) && waited < _retryBudget)
            {
                TimeSpan delay = backoff + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(25));
                await Task.Delay(delay, cancellationToken);
                waited += delay;
                backoff = backoff * 2 > _maximumBackoff ? _maximumBackoff : backoff * 2;
            }
        }
    }

    private static void MoveIntoPlace(string temporaryPath, string path)
    {
        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.Replace(temporaryPath, path, destinationBackupFileName: null);
            return;
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    // Win32 ERROR_SHARING_VIOLATION (32), ERROR_LOCK_VIOLATION (33), and the two
    // ReplaceFile-specific codes for a destination it could not remove or a
    // replacement it could not move (1175, 1176).
    private static bool IsTransientSharingFailure(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33 or 1175 or 1176;
}
