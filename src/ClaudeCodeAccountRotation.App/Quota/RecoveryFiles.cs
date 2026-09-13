using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>
/// The last resort of a token refresh: an owner-only file under
/// <c>&lt;appdata&gt;/recovery/</c> holding a rotated credential pair the
/// write-back could not land.
/// <para>
/// The token endpoint kills the old refresh token the moment it answers 200, so
/// between that answer and a successful write-back the only copy of a working
/// lineage is in this process's memory. A write-back that exhausts its retries
/// with nothing on disk would end the account's login for good. Writing the pair
/// here instead costs the invariant that at most one file holds a lineage (this
/// file and the folder's dead one both exist for as long as the strand lasts,
/// which is why the acceptance script lists this directory), and buys the
/// account back on the next start or the next per-card refresh.
/// </para>
/// <para>
/// Every restore goes through the same compare-and-swap the write-back used: the
/// folder must still hold the pair the rotation replaced. A folder rewritten
/// since (a login, a restore that already ran) is not overwritten; its file is
/// moved aside and named in a warning instead.
/// </para>
/// </summary>
internal sealed partial class RecoveryFiles
{
    public const string DirectoryName = "recovery";
    public const string StaleDirectoryName = "stale";
    private const string FileSuffix = ".credentials.json";

    private static readonly StringComparison _folderComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ICredentialPairStore _pairs;
    private readonly ProfileFolderStore _profiles;
    private readonly QuotaState _state;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecoveryFiles> _logger;
    private readonly string _directory;
    private readonly string _staleDirectory;

    public RecoveryFiles(
        SwitchOptions options,
        ICredentialPairStore pairs,
        ProfileFolderStore profiles,
        QuotaState state,
        ILogger<RecoveryFiles> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pairs = pairs;
        _profiles = profiles;
        _state = state;
        _logger = logger;
        // Only the stale-file stamp reads it, so a caller with no clock of its own
        // may leave it out; the container hands in the one clock everything else
        // here is measured against.
        _timeProvider = timeProvider ?? TimeProvider.System;
        _directory = Path.Combine(Path.GetFullPath(options.AppDataDirectory), DirectoryName);
        _staleDirectory = Path.Combine(_directory, StaleDirectoryName);
    }

    /// <summary>
    /// Whether this folder's pair is stranded. Read on every dashboard poll and
    /// under the mutation gate before a switch is planned, so it stays a single
    /// <c>File.Exists</c> and never parses anything.
    /// </summary>
    public bool HasRecoveryFor(string folderPath) => File.Exists(PathFor(folderPath));

    /// <summary>
    /// Parks a rotated pair for <paramref name="folderPath"/>, recording the
    /// fingerprint the folder must still hold for the restore to apply.
    /// </summary>
    /// <returns>
    /// False when even this could not be written, which is the one path that
    /// loses a lineage; the caller reports it as such.
    /// </returns>
    public async Task<bool> WriteAsync(
        string folderPath,
        RefreshTokenFingerprint expected,
        CredentialPair pair,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pair);
        try
        {
            Directory.CreateDirectory(_directory);
            JsonObject envelope = new()
            {
                ["folder"] = folderPath,
                ["expectedFingerprint"] = expected.Sha256Hex,
                // DeepClone because the pair's own object already has a parent, and
                // a node may belong to one document at a time.
                ["pair"] = pair.Raw.DeepClone(),
            };
            await AtomicJsonFile.WriteAsync(PathFor(folderPath), envelope, cancellationToken);
            LogStranded(FolderName(folderPath), expected.Sha256Hex[..12], pair.Fingerprint.Sha256Hex[..12]);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The type, not the message: a file-system message quotes the path it
            // failed on, and this line is read at Error on a page the operator has
            // open. The exception itself is there at Debug for whoever is debugging.
            string folder = FolderName(folderPath);
            LogRecoveryWriteFailed(folder, exception.GetType().Name);
            LogRecoveryWriteFailedDetail(folder, exception);
            return false;
        }
    }

    /// <summary>Tries to apply one folder's recovery file, if it has one.</summary>
    /// <returns>
    /// True when the folder no longer holds a strand, which covers two different
    /// endings: the rotated pair went back into the folder, or the file was moved
    /// aside because the folder had moved on without it. In both the folder's own
    /// pair is the live lineage again and the account can be read. False only
    /// while the file is still there waiting.
    /// </returns>
    public async Task<bool> RestoreAsync(string folderPath, CancellationToken cancellationToken)
    {
        string path = PathFor(folderPath);
        if (!File.Exists(path))
        {
            return true;
        }

        _ = await RestoreFileAsync(path, cancellationToken);
        return !File.Exists(path);
    }

    /// <summary>
    /// Tries every recovery file, at startup and after a pass. Nothing it does
    /// can fail the caller: each file is attempted inside its own catch, so one
    /// unreadable envelope costs itself and not the other nine accounts.
    /// </summary>
    public async Task RestoreAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(_directory, "*" + FileSuffix))
        {
            _ = await RestoreFileAsync(path, cancellationToken);
        }
    }

    private static string FolderName(string folderPath) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath)));

    private string PathFor(string folderPath) =>
        Path.Combine(_directory, FolderName(folderPath) + FileSuffix);

    /// <summary>
    /// Which folder a recovery file belongs to, read off the file's own name.
    /// The inverse of <see cref="PathFor"/>, so the key a warning is filed under
    /// is the same whether it is derived from the file or from the envelope.
    /// </summary>
    private static string FolderKeyFor(string recoveryFilePath) =>
        Path.GetFileName(recoveryFilePath)[..^FileSuffix.Length];

    private async Task<bool> RestoreFileAsync(string path, CancellationToken cancellationToken)
    {
        // Every warning this file raises is filed under the folder it belongs to,
        // which is exactly what its own name carries. A warning filed under the
        // file's path instead could never be cleared, because a restore that
        // succeeds knows only the folder, and the page would keep showing a
        // sentence about a file that is long gone.
        string folder = FolderKeyFor(path);
        try
        {
            if (await ReadEnvelopeAsync(path, cancellationToken) is not Envelope envelope)
            {
                MoveAside(path);
                _state.WarnRecovery(folder, RefreshMessages.RestoreUnreadable());
                return false;
            }

            if (!string.Equals(FolderName(envelope.Folder), folder, _folderComparison))
            {
                // The file name is what says which folder an envelope belongs to,
                // and the sweep trusts that name when it picks the files to apply.
                // An envelope naming a different folder was renamed by hand or was
                // never written here at all; applying it would rewrite a folder
                // nobody filed it under.
                MoveAside(path);
                _state.WarnRecovery(folder, RefreshMessages.RestoreMisfiled());
                return false;
            }

            AccountEmail? account = await AccountInAsync(envelope.Folder, cancellationToken);
            CredentialPair? current = await _pairs.ReadParkedAsync(envelope.Folder, cancellationToken);
            if (current is null)
            {
                // The folder has no pair to compare against, so the compare-and-swap
                // cannot run and the rotated pair stays here. Deleting it would throw
                // away the one working lineage; a fresh login is the way out.
                _state.WarnRecovery(folder, Warning(account, RefreshMessages.RestoreNeedsLogin, RefreshMessages.RestoreKept));
                return false;
            }

            if (current.Fingerprint != envelope.Expected)
            {
                // The folder was legitimately rewritten since (a login, or a restore
                // that already applied). The pair here is an orphan lineage: moved
                // aside owner-only rather than deleted, and named so the operator can
                // decide whether to revoke it.
                MoveAside(path);
                _state.WarnRecovery(folder, Warning(account, RefreshMessages.RestoreStale, RefreshMessages.RestoreUnreadable));
                return false;
            }

            Result<Unit, string> written = await _pairs.WriteParkedAsync(envelope.Folder, envelope.Pair, envelope.Expected, cancellationToken);
            if (written.IsFailure)
            {
                LogRestoreRefused(FolderName(envelope.Folder), written.Error);
                _state.WarnRecovery(folder, Warning(account, RefreshMessages.RestoreRefused, RefreshMessages.RestoreKept));
                return false;
            }

            File.Delete(path);
            _state.ClearRecoveryWarning(folder);
            // Guarded because the two fingerprints are sliced to build the line:
            // the audit trail is worth the slice only when something reads it.
            if (_logger.IsEnabled(LogLevel.Information))
            {
                // CA1873 does not see through the guard to a source-generated log
                // method; the arguments are two hash slices and one path segment.
#pragma warning disable CA1873 // Evaluation of this argument may be expensive
                LogRestored(FolderName(envelope.Folder), envelope.Expected.Sha256Hex[..12], envelope.Pair.Fingerprint.Sha256Hex[..12]);
#pragma warning restore CA1873
            }

            return true;
        }
        catch (IOException exception)
        {
            return KeptForLater(folder, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return KeptForLater(folder, exception);
        }
        catch (ArgumentException exception)
        {
            // The envelope named a folder that is not one of ours. The store and the
            // profile reader both refuse such a path rather than following it.
            return KeptForLater(folder, exception);
        }
        catch (InvalidDataException exception)
        {
            // The folder's own credential file no longer parses as a pair, so there
            // is nothing to compare the envelope against yet.
            return KeptForLater(folder, exception);
        }
        catch (JsonException exception)
        {
            // The folder's own credential file is torn rather than merely wrong, so
            // the read of it throws before the pair is built. One folder caught
            // mid-write must not end the sweep for the other nine.
            return KeptForLater(folder, exception);
        }
    }

    /// <summary>A warning naming the account when the folder still says who it is, and an account-free sentence when it does not.</summary>
    private static string Warning(AccountEmail? account, Func<AccountEmail, string> named, Func<string> anonymous) =>
        account is AccountEmail known ? named(known) : anonymous();

    private bool KeptForLater(string folder, Exception exception)
    {
        LogRestoreFailed(folder, exception.GetType().Name);
        LogRestoreFailedDetail(folder, exception);
        _state.WarnRecovery(folder, RefreshMessages.RestoreKept());
        return false;
    }

    private async Task<AccountEmail?> AccountInAsync(string folderPath, CancellationToken cancellationToken)
    {
        try
        {
            return (await _profiles.ReadAccountAsync(folderPath, cancellationToken))?.Email;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves one recovery file into <c>stale/</c>, stamped with the instant it
    /// was moved and never over an earlier one. Two orphans for the same folder
    /// are two lineages, and overwriting the first to make room for the second
    /// would destroy exactly what this directory exists to keep. The stamp sits
    /// before the suffix so the acceptance script's <c>*.credentials.json</c>
    /// sweep still counts these files.
    /// </summary>
    private void MoveAside(string path)
    {
        Directory.CreateDirectory(_staleDirectory);
        string stamped = FolderKeyFor(path)
            + "."
            + _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
            + FileSuffix;
        // A rename, never a copy: a credential-bearing file moves or it stays.
        File.Move(path, Path.Combine(_staleDirectory, stamped), overwrite: false);
    }

    private static async Task<Envelope?> ReadEnvelopeAsync(string path, CancellationToken cancellationToken)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(await SharedFileReader.ReadAllBytesAsync(path, cancellationToken));
        }
        catch (JsonException)
        {
            return null;
        }

        // Every field read through TryGetValue, the reader convention for a file
        // this process wrote but cannot assume: a number where a string belongs
        // must read as absent, not throw out of a startup sweep.
        if (parsed is not JsonObject envelope
            || Text(envelope, "folder") is not string folder
            || Text(envelope, "expectedFingerprint") is not string fingerprint
            || envelope["pair"] is not JsonObject raw)
        {
            return null;
        }

        Result<CredentialPair, string> pair = CredentialPair.FromJson(raw);
        return pair.IsFailure ? null : new Envelope(folder, new RefreshTokenFingerprint(fingerprint), pair.Value);
    }

    private static string? Text(JsonObject envelope, string key) =>
        envelope[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>The file's three fields: whose folder, what it must still hold, and the pair to put there.</summary>
    private sealed record Envelope(string Folder, RefreshTokenFingerprint Expected, CredentialPair Pair);

    [LoggerMessage(Level = LogLevel.Warning, Message = "credential pair for {Folder} stranded in recovery (replacing {Expected}, rotated to {Rotated})")]
    private partial void LogStranded(string folder, string expected, string rotated);

    [LoggerMessage(Level = LogLevel.Error, Message = "the rotated credential pair for {Folder} could not be written to recovery and is lost ({Failure})")]
    private partial void LogRecoveryWriteFailed(string folder, string failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "the recovery write for {Folder} failed")]
    private partial void LogRecoveryWriteFailedDetail(string folder, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "recovery restored the credential pair for {Folder} ({Expected} to {Rotated})")]
    private partial void LogRestored(string folder, string expected, string rotated);

    [LoggerMessage(Level = LogLevel.Warning, Message = "recovery for {Folder} was refused by the store: {Reason}")]
    private partial void LogRestoreRefused(string folder, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the recovery file for {Folder} could not be applied and was kept ({Failure})")]
    private partial void LogRestoreFailed(string folder, string failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "the recovery file for {Folder} could not be applied")]
    private partial void LogRestoreFailedDetail(string folder, Exception exception);
}
