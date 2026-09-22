using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// One running instance per app-data directory: an exclusively opened lock
/// file, plus a sibling file whose first line is the listen URL and whose
/// second line is this process's loopback token. A second launch refuses and
/// prints the URL only.
/// </summary>
internal sealed class InstanceLock : IDisposable
{
    public const string FileName = "instance.lock";
    public const string UrlFileName = "instance.url";

    private readonly FileStream _stream;
    private readonly string _urlFilePath;
    private readonly byte[] _tokenBytes;

    private InstanceLock(FileStream stream, string lockFilePath, string urlFilePath, byte[] tokenBytes, string token)
    {
        _stream = stream;
        _urlFilePath = urlFilePath;
        _tokenBytes = tokenBytes;
        LockFilePath = lockFilePath;
        UrlFilePath = urlFilePath;
        Token = token;
    }

    public string LockFilePath { get; }

    public string UrlFilePath { get; }

    /// <summary>The token on line 2 of <see cref="UrlFileName"/>, Base64url without padding.</summary>
    public string Token { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership of the lock transfers to the caller through the result; disposing it releases the file.")]
    public static Result<InstanceLock, string> TryAcquire(string appDataDirectory, string listenUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(listenUrl);
        Directory.CreateDirectory(appDataDirectory);
        string lockFilePath = Path.Combine(appDataDirectory, FileName);
        string urlFilePath = Path.Combine(appDataDirectory, UrlFileName);
        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
        string token = Base64Url.EncodeToString(tokenBytes);

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
            WriteInstanceFile(urlFilePath, listenUrl, token);

            InstanceLock acquired = new(stream, lockFilePath, urlFilePath, tokenBytes, token);
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
    /// replaced with the one the server actually took. The token stays.
    /// </summary>
    public void PublishListenUrl(string listenUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenUrl);
        WriteInstanceFile(_urlFilePath, listenUrl, Token);
    }

    /// <summary>
    /// Whether <paramref name="presented"/> decodes to this instance's raw token.
    /// A value that is not 32 bytes of Base64url still takes the same comparison,
    /// against 32 zero bytes, so a bad decode is not a different code path.
    /// </summary>
    public bool MatchesPresented(string presented)
    {
        ArgumentNullException.ThrowIfNull(presented);
        Span<byte> decoded = stackalloc byte[32];
        byte[] candidate = TryDecodeToken(presented, decoded) ? decoded.ToArray() : new byte[32];
        return CryptographicOperations.FixedTimeEquals(candidate, _tokenBytes);
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

    private static void WriteInstanceFile(string path, string listenUrl, string token)
    {
        string document = listenUrl + "\n" + token + "\n";
        AtomicBytesFile.WriteOwnerOnly(path, Encoding.UTF8.GetBytes(document));
    }

    private static bool TryDecodeToken(string presented, Span<byte> destination)
    {
        OperationStatus status = Base64Url.DecodeFromChars(presented, destination, out int charsConsumed, out int bytesWritten);
        return status == OperationStatus.Done && bytesWritten == destination.Length && charsConsumed == presented.Length;
    }

    private static string ReadRunningUrl(string urlFilePath)
    {
        try
        {
            string line = FirstLine(File.ReadAllText(urlFilePath));
            if (Uri.TryCreate(line, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttp)
            {
                return line;
            }

            return "an unknown address";
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

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        string line = newline < 0 ? text : text[..newline];
        return line.Trim('\r', ' ', '\t');
    }
}
