using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// The follower's half of the hand-off: the four file steps that carry a
/// credential pair across the volume boundary, F3 to F6 of the design's
/// section 9.2.
/// <para>
/// A rename cannot cross the boundary — <c>rename(2)</c> from <c>/mnt/c</c> to
/// ext4 is <c>EXDEV</c> — so this is the one place in the product that copies a
/// credential file. Every copy here is followed by a flush to disk, a read-back
/// <b>through a fresh open</b>, and a fingerprint comparison; a mismatch fails
/// and the caller unwinds, so a copy is never trusted on the strength of having
/// returned. "Moved, never copied" holds as "at most one reachable copy at
/// every instant": the second copy always lives under a name only this follower
/// and the leader's coordinator open.
/// </para>
/// </summary>
internal sealed class StagedImportCredentialPairStore
{
    /// <summary>The staging name: beside the live file, on the live volume, so F5 is an atomic replace.</summary>
    public const string StagingFileName = ".credentials.json.incoming";

    private readonly TimeProvider _timeProvider;

    public StagedImportCredentialPairStore(string liveConfigDirectory, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveConfigDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        LiveConfigDirectory = Path.GetFullPath(liveConfigDirectory);
        LivePath = Path.Combine(LiveConfigDirectory, FileSystemCredentialPairStore.FileName);
        StagingPath = Path.Combine(LiveConfigDirectory, StagingFileName);
        _timeProvider = timeProvider;
    }

    public string LiveConfigDirectory { get; }

    public string LivePath { get; }

    public string StagingPath { get; }

    /// <summary>The live pair as it is on disk right now, read through a fresh open every time.</summary>
    public Task<CredentialPair?> ReadLiveAsync(CancellationToken cancellationToken) =>
        ReadFreshAsync(LivePath, cancellationToken);

    /// <summary>The staged pair, or null when nothing is staged.</summary>
    public Task<CredentialPair?> ReadStagedAsync(CancellationToken cancellationToken) =>
        ReadFreshAsync(StagingPath, cancellationToken);

    /// <summary>
    /// F3, stage: copy the leader's claimed file into the live volume beside the
    /// live pair and prove the bytes landed as <paramref name="expected"/>.
    /// </summary>
    public Task<Result<Unit, string>> StageAsync(string claimedPath, RefreshTokenFingerprint expected, CancellationToken cancellationToken) =>
        CopyVerifiedAsync(claimedPath, StagingPath, expected, "stage", cancellationToken);

    /// <summary>
    /// F4, export: copy the outgoing live pair to the mailbox and prove the
    /// bytes landed as <paramref name="expected"/>. The read-back is through a
    /// fresh open, but it is still a read over the mount under suspicion, which
    /// is why the leader reads the same file natively before any commit.
    /// </summary>
    public Task<Result<Unit, string>> ExportAsync(string exportPath, RefreshTokenFingerprint expected, CancellationToken cancellationToken) =>
        CopyVerifiedAsync(LivePath, exportPath, expected, "export", cancellationToken);

    /// <summary>
    /// F5, swap: replace the live pair with the staged one. The caller has
    /// already re-read the live file and found the pair it exported; this is the
    /// step that destroys the outgoing account's last local copy, so it is
    /// reached only from a commit.
    /// <para>
    /// The replace is a same-volume rename, atomic on ext4 and on NTFS: no
    /// instant exists in which the live name is absent or half written.
    /// </para>
    /// </summary>
    public Result<Unit, string> Swap()
    {
        if (!File.Exists(StagingPath))
        {
            return Result<Unit, string>.Failure("nothing is staged at " + StagingPath + "; the swap has nothing to promote");
        }

        File.Move(StagingPath, LivePath, overwrite: true);
        // The CLI reloads credentials when the file's mtime differs from the one it
        // cached, and a rename carries the staging file's mtime over.
        File.SetLastWriteTimeUtc(LivePath, _timeProvider.GetUtcNow().UtcDateTime);
        return Result<Unit, string>.Success(Unit.Value);
    }

    /// <summary>F6, release: drop the leader's claimed file, now that the same lineage is live here.</summary>
    public static void Release(string claimedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedPath);
        DeleteIfPresent(claimedPath);
    }

    /// <summary>Unwind: the staging file and the export are the only two files an abort ever removes.</summary>
    public void DeleteStaging() => DeleteIfPresent(StagingPath);

    public static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A credential file read through an open of its own: no cached handle, no
    /// reused stream. Every fingerprint this class compares comes from here, so
    /// no verification is ever answered out of a buffer the write left behind.
    /// </summary>
    public static async Task<CredentialPair?> ReadFreshAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes;
        await using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            using MemoryStream buffer = new();
            await stream.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        // A short or torn file is "no credential pair", not an exception: reading
        // one back is precisely what this class does to find out whether a copy
        // landed, and a truncated export is the failure the export gate exists
        // to catch rather than a fault to propagate.
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(bytes);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        return node is JsonObject raw
            ? CredentialPair.FromJson(raw).Match(static pair => pair, static _ => (CredentialPair?)null)
            : null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "FlushAsync does not reach the device; Flush(flushToDisk: true) is the fsync this step exists for and has no asynchronous form.")]
    private static async Task<Result<Unit, string>> CopyVerifiedAsync(
        string sourcePath,
        string destinationPath,
        RefreshTokenFingerprint expected,
        string what,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!File.Exists(sourcePath))
        {
            return Result<Unit, string>.Failure("cannot " + what + ": no credential file at " + sourcePath);
        }

        byte[] bytes;
        await using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            using MemoryStream buffer = new();
            await source.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using (FileStream destination = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await destination.WriteAsync(bytes, cancellationToken);
            // flushToDisk is the fsync: over DrvFs it returns 0 without proving the
            // bytes are durable, which is the measured fact the export gate exists for.
            destination.Flush(flushToDisk: true);
        }

        CredentialPair? readBack = await ReadFreshAsync(destinationPath, cancellationToken);
        if (readBack is null)
        {
            DeleteIfPresent(destinationPath);
            return Result<Unit, string>.Failure("the " + what + " at " + destinationPath + " read back as no credential pair; nothing was promoted");
        }

        if (readBack.Fingerprint != expected)
        {
            DeleteIfPresent(destinationPath);
            return Result<Unit, string>.Failure(
                "the " + what + " at " + destinationPath + " read back as " + readBack.Fingerprint.Sha256Hex[..12]
                + ", not the expected " + expected.Sha256Hex[..12] + "; nothing was promoted");
        }

        return Result<Unit, string>.Success(Unit.Value);
    }
}
