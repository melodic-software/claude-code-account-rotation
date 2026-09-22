using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// The file credential store Windows and Linux share: the live pair at
/// <c>&lt;live dir&gt;/.credentials.json</c> and one parked pair per profile
/// folder. Park and unpark are renames on one volume, so at no instant do two
/// files hold the same refresh token, and there is deliberately no copy path.
/// </summary>
internal sealed class FileSystemCredentialPairStore : ICredentialPairStore
{
    public const string FileName = ".credentials.json";

    /// <summary>
    /// The mailbox root inside the store, one directory per other side. It sits
    /// under the profiles root so a claim stays a single-volume rename, and it
    /// is named with a leading dot so the folder listing, which wants an
    /// identity file in every directory it reports, passes over it.
    /// </summary>
    public const string TransitDirectoryName = ".transit";

    /// <summary>What the other side calls a pair it has exported but this side has not promoted yet.</summary>
    public const string IncomingSuffix = ".incoming";

    private const string DaemonLockFileName = "daemon.lock";

    private readonly string _liveConfigDirectory;
    private readonly string _livePath;
    private readonly string _profilesRoot;
    private readonly TimeProvider _timeProvider;
    private readonly OAuthRefreshLock _refreshLock;
    private readonly Func<string, long?> _deviceId;
    private readonly Func<string, string, int>? _rename;

    public FileSystemCredentialPairStore(
        string liveConfigDirectory,
        string profilesRoot,
        TimeProvider timeProvider,
        Func<string, long?>? deviceId = null,
        Func<string, string, int>? rename = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveConfigDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _liveConfigDirectory = Path.GetFullPath(liveConfigDirectory);
        _livePath = Path.Combine(_liveConfigDirectory, FileName);
        _profilesRoot = Path.GetFullPath(profilesRoot);
        _timeProvider = timeProvider;
        _refreshLock = new OAuthRefreshLock(_liveConfigDirectory, timeProvider);
        _deviceId = deviceId ?? SameVolume.DeviceId;
        _rename = rename;
    }

    public Task<CredentialPair?> ReadLiveAsync(CancellationToken cancellationToken) =>
        ReadPairAsync(_livePath, cancellationToken);

    public Task<CredentialPair?> ReadParkedAsync(string folderPath, CancellationToken cancellationToken) =>
        ReadPairAsync(Path.Combine(ProfileFolder(folderPath), FileName), cancellationToken);

    public Task MoveLiveToParkedAsync(string folderPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string folder = ProfileFolder(folderPath);
        Directory.CreateDirectory(folder);
        Rename(_livePath, Path.Combine(folder, FileName));
        return Task.CompletedTask;
    }

    public Task MoveParkedToLiveAsync(string folderPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Rename(Path.Combine(ProfileFolder(folderPath), FileName), _livePath);
        // The CLI reloads credentials when the file's mtime differs from the one it
        // cached; a rename keeps the parked file's old mtime, so stamp it now.
        File.SetLastWriteTimeUtc(_livePath, _timeProvider.GetUtcNow().UtcDateTime);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The claim: renames a slot's parked pair into the named side's mailbox
    /// under the store, and answers where it went. One volume, because the
    /// mailbox is inside the store, so this is the same rename park and unpark
    /// already are and there is no copy path on this side, ever.
    /// </summary>
    public Task<string> ClaimToMailboxAsync(string folderPath, SideName side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string folder = ProfileFolder(folderPath);
        string mailbox = MailboxFor(side);
        Directory.CreateDirectory(mailbox);
        string destination = Path.Combine(mailbox, ClaimedName(folder));
        Rename(Path.Combine(folder, FileName), destination);
        return Task.FromResult(destination);
    }

    /// <summary>
    /// The mailbox exists before the other side is asked to write into it. A
    /// claim creates it on the way past, and a release makes no claim: without
    /// this, the first park-back after a fresh install would have the follower
    /// export into a directory that is not there.
    /// </summary>
    public void EnsureMailbox(SideName side) => Directory.CreateDirectory(MailboxFor(side));

    /// <summary>
    /// The claim reversed: renames a claimed file back out of the mailbox into
    /// the slot it came from. This is the unclaim of design 9.1, and it runs
    /// only once the other side has answered a definite "not imported": a
    /// claimed file the follower may already have staged from is never taken
    /// back blind.
    /// </summary>
    public Task UnclaimFromMailboxAsync(string folderPath, SideName side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string folder = ProfileFolder(folderPath);
        Directory.CreateDirectory(folder);
        Rename(ClaimedPathFor(folderPath, side), Path.Combine(folder, FileName));
        return Task.CompletedTask;
    }

    /// <summary>
    /// <b>L3b, the export gate.</b> Reads the file the other side exported,
    /// <b>natively</b>, on the store's own volume, through a fresh open, and
    /// answers its fingerprint. An absent file, a short or torn read, and an
    /// one that will not parse are all failures with a reason, never an exception: the
    /// gate exists so that a bad crossing costs a refused switch, and a
    /// refusal is how it says so.
    /// <para>
    /// This read is the only check in the design that does not go through the
    /// layer under suspicion. Section 3 measured <c>fsync</c> over DrvFs
    /// returning 0 without measuring durability, and the follower's own F4
    /// read-back is a 9P read that the mount cache can serve; reading here, on
    /// the volume that owns the bytes, is what turns that assumption into a
    /// per-switch verification.
    /// </para>
    /// </summary>
    public async Task<Result<RefreshTokenFingerprint, string>> ReadExportedFingerprintAsync(
        string folderPath,
        SideName side,
        CancellationToken cancellationToken)
    {
        string folder = ProfileFolder(folderPath);
        string export = ExportPathFor(folderPath, side);
        CredentialPair? exported;
        try
        {
            exported = await ReadPairAsync(export, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            // An export the other side truncated is a refusal, not a fault: the
            // whole point of the gate is that a bad crossing costs a switch.
            return Result<RefreshTokenFingerprint, string>.Failure("the exported pair for " + Path.GetFileName(folder) + " could not be read");
        }

        return exported is null
            ? Result<RefreshTokenFingerprint, string>.Failure("no exported pair for " + Path.GetFileName(folder))
            : Result<RefreshTokenFingerprint, string>.Success(exported.Fingerprint);
    }

    /// <summary>
    /// The promote: renames the pair the other side exported into this slot,
    /// but only once its fingerprint is the one the caller expected. A
    /// mismatch, a short read, or an absent export is a refusal and moves
    /// nothing, which is what keeps a lineage the leader has not verified out
    /// of the store.
    /// </summary>
    public async Task<Result<Unit, string>> PromoteFromMailboxAsync(
        string folderPath,
        SideName side,
        RefreshTokenFingerprint expected,
        CancellationToken cancellationToken)
    {
        string folder = ProfileFolder(folderPath);
        Result<RefreshTokenFingerprint, string> exported = await ReadExportedFingerprintAsync(folderPath, side, cancellationToken);
        if (exported.IsFailure)
        {
            return Result<Unit, string>.Failure(exported.Error);
        }

        if (exported.Value != expected)
        {
            return Result<Unit, string>.Failure("the exported pair for " + Path.GetFileName(folder) + " is not the one that was verified (fingerprint " + exported.Value.Sha256Hex[..12] + " vs expected " + expected.Sha256Hex[..12] + ")");
        }

        Directory.CreateDirectory(folder);
        try
        {
            Rename(ExportPathFor(folderPath, side), Path.Combine(folder, FileName));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // Every other way this method declines is a failure it returns, and
            // so is this one: a slot that already holds a pair, or an export
            // that went away between the read and the move, is a disagreement
            // for the caller to report and leave alone. Throwing would carry it
            // out of a dashboard poll as a 500 and strand the export anyway.
            return Result<Unit, string>.Failure("the exported pair for " + Path.GetFileName(folder) + " could not be moved into its slot: " + exception.Message);
        }

        return Result<Unit, string>.Success(Unit.Value);
    }

    /// <summary>
    /// The one thing that is done with an export nothing may park: it is moved,
    /// by the same guarded rename every other credential move uses, out of the
    /// mailbox and into quarantine, where it is neither promoted nor deleted.
    /// The destination is answered so the banner can name the file the operator
    /// has to decide about.
    /// </summary>
    public Task<Result<string, string>> MoveExportToQuarantineAsync(
        string folderPath,
        SideName side,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        string folder = ProfileFolder(folderPath);
        string destination = Path.Combine(Path.GetFullPath(destinationDirectory), FileName);
        try
        {
            Directory.CreateDirectory(Path.GetFullPath(destinationDirectory));
            Rename(ExportPathFor(folderPath, side), destination);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // A quarantine that cannot happen leaves the export in the mailbox,
            // which is recoverable, and says why. Throwing would carry it out of
            // a dashboard poll as a 500 and strand it just the same.
            return Task.FromResult(Result<string, string>.Failure(
                "the exported pair for " + Path.GetFileName(folder) + " could not be quarantined: " + exception.Message));
        }

        return Task.FromResult(Result<string, string>.Success(destination));
    }

    public Task MoveParkedToQuarantineAsync(string folderPath, string destinationDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        string destination = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destination);
        Rename(Path.Combine(ProfileFolder(folderPath), FileName), Path.Combine(destination, FileName));
        return Task.CompletedTask;
    }

    public async Task<Result<Unit, string>> WriteParkedAsync(
        string folderPath,
        CredentialPair pair,
        RefreshTokenFingerprint expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pair);
        string path = Path.Combine(ProfileFolder(folderPath), FileName);
        CredentialPair? current = await ReadPairAsync(path, cancellationToken);
        if (current is null)
        {
            return Result<Unit, string>.Failure("no parked pair at " + path + "; nothing to replace");
        }

        if (current.Fingerprint != expected)
        {
            return Result<Unit, string>.Failure("the parked pair at " + path + " is no longer the one being replaced (fingerprint " + current.Fingerprint.Sha256Hex[..12] + " vs expected " + expected.Sha256Hex[..12] + ")");
        }

        await AtomicJsonFile.WriteAsync(path, pair.Raw, cancellationToken);
        return Result<Unit, string>.Success(Unit.Value);
    }

    public Task<Result<IAsyncDisposable, string>> AcquireRefreshLockAsync(TimeSpan waitBound, CancellationToken cancellationToken) =>
        _refreshLock.AcquireAsync(waitBound, cancellationToken);

    public string? FreshLockFileName(TimeSpan maxAge)
    {
        if (!Directory.Exists(_liveConfigDirectory))
        {
            return null;
        }

        DateTime threshold = _timeProvider.GetUtcNow().UtcDateTime - maxAge;
        foreach (string path in Directory.EnumerateFiles(_liveConfigDirectory, "*.lock"))
        {
            string name = Path.GetFileName(path);
            if (string.Equals(name, DaemonLockFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.GetLastWriteTimeUtc(path) >= threshold)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Where a side's mailbox sits under a store: <c>&lt;store&gt;/.transit/&lt;side&gt;</c>.</summary>
    public static string MailboxPath(string profilesRoot, SideName side) =>
        Path.Combine(Path.GetFullPath(profilesRoot), TransitDirectoryName, side.Value);

    /// <summary>
    /// What a claimed pair is called in the mailbox: the slot's folder name and
    /// the credential file name it had, so one mailbox holds one file per
    /// account and the name says which account it belongs to.
    /// </summary>
    public static string ClaimedFileName(string folderName) => folderName + FileName;

    /// <summary>Where this slot's pair sits while the named side holds the claim.</summary>
    public string ClaimedPathFor(string folderPath, SideName side) =>
        Path.Combine(MailboxFor(side), ClaimedName(ProfileFolder(folderPath)));

    /// <summary>Where the named side puts this slot's pair when it exports it back.</summary>
    public string ExportPathFor(string folderPath, SideName side) =>
        ClaimedPathFor(folderPath, side) + IncomingSuffix;

    private string MailboxFor(SideName side) => MailboxPath(_profilesRoot, side);

    private static string ClaimedName(string folder) => ClaimedFileName(Path.GetFileName(folder));

    private static async Task<CredentialPair?> ReadPairAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes = await SharedFileReader.ReadAllBytesAsync(path, cancellationToken);
        var node = JsonNode.Parse(bytes);
        if (node is not JsonObject raw)
        {
            throw new InvalidDataException("The credential file at " + path + " is not a JSON object.");
        }

        return CredentialPair.FromJson(raw).Match(
            static pair => pair,
            reason => throw new InvalidDataException("The credential file at " + path + " is unreadable: " + reason));
    }

    /// <summary>
    /// A rename that fails loudly instead of ever leaving two holders: the
    /// destination must not exist, the device ids must agree when this
    /// platform can read them, and the move itself is a rename. On Linux
    /// <see cref="File.Move"/> is not used: across volumes, and across a bind
    /// mount whose device ids match, it copies the file and then deletes it.
    /// <c>renameat2</c> returns <c>EXDEV</c> instead, and that is a refusal.
    /// </summary>
    private void Rename(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("No credential pair to move.", sourcePath);
        }

        if (File.Exists(destinationPath))
        {
            throw new InvalidOperationException("Refusing to move " + sourcePath + ": " + destinationPath + " already holds a credential pair, and a second holder is never created.");
        }

        if (!SameVolume.OnOneVolume(sourcePath, destinationPath, _deviceId))
        {
            throw new InvalidOperationException("Refusing to move " + sourcePath + " to " + destinationPath + ": the paths are on different volumes and a move must be a rename, never a copy.");
        }

        int renamed = SameVolume.MoveByRename(sourcePath, destinationPath, _rename);
        if (renamed == SameVolume.CrossDeviceError)
        {
            throw new InvalidOperationException("Refusing to move " + sourcePath + " to " + destinationPath + ": the paths are on different volumes and a move must be a rename, never a copy.");
        }

        if (renamed != 0)
        {
            throw new IOException("Refusing to move " + sourcePath + " to " + destinationPath + ": rename returned " + renamed + ".");
        }
    }

    private string ProfileFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        // The same rule as ProfileFolderStore.UnderRoot: one level below the root, never
        // outside it and never deeper, and never through a junction or symbolic link.
        Result<string, string> child = DirectoryLinks.DirectChild(_profilesRoot, folderPath);
        if (child.IsFailure)
        {
            throw new ArgumentException(child.Error, nameof(folderPath));
        }

        return child.Value;
    }
}
