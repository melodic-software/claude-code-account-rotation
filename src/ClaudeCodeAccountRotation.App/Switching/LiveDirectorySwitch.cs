using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// Executes a switch: snapshot the live directory, plan, take Claude Code's own
/// refresh lock, journal the intent, park, unpark, patch the state file, clear
/// the journal, then verify with the CLI. Every step that moves a credential
/// runs under the one mutation gate and inside the refresh lock. Startup
/// reconciliation quarantines duplicate lineages and finishes or unwinds a
/// switch a crash left half done.
/// </summary>
internal sealed partial class LiveDirectorySwitch
{
    private const string QuarantineDirectoryName = "quarantine";
    private const string LiveOwnerFileName = "live-owner.json";
    private static readonly TimeSpan _secondaryLockGuardAge = TimeSpan.FromSeconds(60);

    // AttributesToSkip is cleared because the default hides Hidden and System
    // files, and every temporary this writer makes starts with a dot, which
    // some tools mark hidden. IgnoreInaccessible on the deep walk so one
    // unreadable nested directory costs itself rather than the whole leg; the
    // SearchOption overloads would abort the enumeration instead.
    private static readonly EnumerationOptions _shallowSweep = new() { AttributesToSkip = FileAttributes.None };
    private static readonly EnumerationOptions _deepSweep = new()
    {
        AttributesToSkip = FileAttributes.None,
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

    private readonly ICredentialPairStore _pairs;
    private readonly ClaudeStateFile _stateFile;
    private readonly ProfileFolderStore _profiles;
    private readonly SwitchJournal _journal;
    private readonly CredentialMutationGate _gate;
    private readonly ILoginSessionRunner _logins;
    private readonly IClaudeCliAuthStatus _authStatus;
    private readonly ManagedLoginPolicyReader _policyReader;
    private readonly RecoveryFiles _recovery;
    private readonly QuotaState _quota;
    private readonly SwitchOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LiveDirectorySwitch> _logger;
    private readonly string _quarantineDirectory;
    private readonly string _liveOwnerPath;

    public LiveDirectorySwitch(
        ICredentialPairStore pairs,
        ClaudeStateFile stateFile,
        ProfileFolderStore profiles,
        SwitchJournal journal,
        CredentialMutationGate gate,
        ILoginSessionRunner logins,
        IClaudeCliAuthStatus authStatus,
        ManagedLoginPolicyReader policyReader,
        RecoveryFiles recovery,
        QuotaState quota,
        SwitchOptions options,
        TimeProvider timeProvider,
        ILogger<LiveDirectorySwitch> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pairs = pairs;
        _stateFile = stateFile;
        _profiles = profiles;
        _journal = journal;
        _gate = gate;
        _logins = logins;
        _authStatus = authStatus;
        _policyReader = policyReader;
        _recovery = recovery;
        _quota = quota;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _quarantineDirectory = Path.Combine(options.AppDataDirectory, QuarantineDirectoryName);
        _liveOwnerPath = Path.Combine(options.AppDataDirectory, "state", LiveOwnerFileName);
    }

    public async Task<Result<SwitchOutcome, SwitchRefusal>> SwitchToAsync(AccountEmail target, CancellationToken cancellationToken)
    {
        IDisposable? permit = null;
        try
        {
            try
            {
                permit = await _gate.AcquireAsync(_options.MutationGateTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                LogRefused(target.Value, SwitchRefusal.MutationInProgress);
                return Result<SwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.MutationInProgress);
            }

            // Under the gate, not before it: checked before, a switch could still
            // slip in between the pass's TryBeginRun and the identity repair that
            // opens it, which takes the gate with a zero wait.
            if (_quota.InProgress)
            {
                LogRefused(target.Value, SwitchRefusal.RefreshInProgress);
                return Result<SwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.RefreshInProgress);
            }

            return await SwitchUnderGateAsync(target, cancellationToken);
        }
        finally
        {
            permit?.Dispose();
        }
    }

    /// <summary>
    /// Restores the state file's <c>oauthAccount</c> block when a running session has
    /// written its in-memory block back over the switch's patch (observed on the
    /// real machine within minutes of a switch). The owner record decides: a block
    /// naming another account and stamped before the record was written is stale,
    /// and the owner's block, kept in its profile folder since it was parked, is
    /// patched in again; a block stamped after the record is a real login and is
    /// adopted instead. Runs with a zero gate wait so it never delays a switch.
    /// </summary>
    public async Task<IdentityRepair> RepairStaleIdentityAsync(CancellationToken cancellationToken)
    {
        IDisposable? permit = null;
        try
        {
            try
            {
                permit = await _gate.AcquireAsync(TimeSpan.Zero, cancellationToken);
            }
            catch (TimeoutException)
            {
                return IdentityRepair.Busy;
            }

            LiveAccountState live = await SnapshotLiveAsync(cancellationToken);
            if (!live.HasCredentials)
            {
                return IdentityRepair.NotNeeded;
            }

            AccountEmail? owner = await ReadLiveOwnerAsync(live, cancellationToken);
            if (owner is not AccountEmail recorded || live.Account?.Email == recorded)
            {
                return IdentityRepair.NotNeeded;
            }

            string folder = Path.Combine(_options.ProfilesRoot, ProfileFolderName.FromEmail(recorded));
            OAuthAccountBlock? block = Directory.Exists(folder) ? await _profiles.ReadAccountAsync(folder, cancellationToken) : null;
            if (block is null)
            {
                LogRepairImpossible(recorded.Value, folder);
                return IdentityRepair.NoProfileBlock;
            }

            await _stateFile.PatchAccountBlockAsync(block, CancellationToken.None);
            LogRepatched(recorded.Value, live.Account?.Email?.Value ?? "(none)");
            return IdentityRepair.Repatched;
        }
        finally
        {
            permit?.Dispose();
        }
    }

    public async Task<ReconciliationReport> ReconcileAsync(CancellationToken cancellationToken)
    {
        using IDisposable permit = await _gate.AcquireAsync(_options.MutationGateTimeout, cancellationToken);
        (IReadOnlyList<string> sweptTemporaries, IReadOnlyList<string> stranded) = await SweepTemporariesAsync(cancellationToken);
        IReadOnlyList<string> quarantined = [.. sweptTemporaries, .. await QuarantineDuplicateLineagesAsync(cancellationToken)];
        (string journalOutcome, bool journalBlocks) = await ReconcileJournalAsync(cancellationToken);
        bool quarantineBlocks = QuarantineHoldsFiles();
        string? banner = stranded.Count > 0
            ? "A credential pair was left in a temporary file that could not be moved to safety: " + string.Join(", ", stranded) + "; move or delete it before switching."
            : quarantineBlocks
                ? "A duplicate credential lineage was quarantined under " + _quarantineDirectory + "; delete or restore those files before switching."
                : journalBlocks ? "An earlier switch could not be reconciled: " + journalOutcome : null;
        return new ReconciliationReport(quarantined, journalOutcome, quarantineBlocks || journalBlocks || stranded.Count > 0, banner);
    }

    private async Task<Result<SwitchOutcome, SwitchRefusal>> SwitchUnderGateAsync(AccountEmail target, CancellationToken cancellationToken)
    {
        LiveAccountState live = await SnapshotLiveAsync(cancellationToken);
        IReadOnlyList<ParkedProfile> profiles = await _profiles.ListAsync(cancellationToken);
        ParkedProfile? targetProfile = profiles.FirstOrDefault(profile => profile.Email == target);
        if (targetProfile is null)
        {
            LogRefused(target.Value, SwitchRefusal.TargetHasNoCredentials);
            return Result<SwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.TargetHasNoCredentials);
        }

        CredentialPair? liveCredentials = await _pairs.ReadLiveAsync(cancellationToken);
        CredentialPair? targetCredentials = targetProfile.HasCredentials
            ? await _pairs.ReadParkedAsync(targetProfile.FolderPath, cancellationToken)
            : null;
        ManagedLoginPolicy policy = await _policyReader.ReadAsync(cancellationToken);
        // A credential temporary left in place is a refresh token no quarantine and
        // no journal knows about, so neither of those guards sees it. Checked here
        // rather than read from the startup report, because the crash that strands
        // one can happen while the tool is running.
        string? strandedTemporary = StrandedCredentialTemporary();
        if (strandedTemporary is not null)
        {
            // Named in the log here, because the reconciliation banner is written
            // once at startup and a temporary stranded since then would otherwise
            // refuse every switch with nothing anywhere telling the operator which
            // file to deal with.
            LogStrandedTemporary(strandedTemporary);
        }

        bool journalOpen = await _journal.ReadOpenAsync(cancellationToken) is not null
            || QuarantineHoldsFiles()
            || strandedTemporary is not null;
        AccountEmail? liveOwner = await ReadLiveOwnerAsync(live, cancellationToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Read here, under the gate, with every other planning input: a refresh
        // that stranded this folder's rotated pair left the file in it dead, so
        // moving that file to live would both fail the account and take away the
        // parked pair the restore compares against.
        bool targetStranded = _recovery.HasRecoveryFor(targetProfile.FolderPath);

        Result<SwitchPlan, SwitchRefusal> planned = SwitchPlanner.Plan(new SwitchPlanningInput(
            live, targetProfile, liveCredentials, targetCredentials, policy, journalOpen, liveOwner, _options.ProfilesRoot, now, targetStranded));
        if (planned.IsFailure)
        {
            LogRefused(target.Value, planned.Error);
            return Result<SwitchOutcome, SwitchRefusal>.Failure(planned.Error);
        }

        SwitchPlan plan = planned.Value;
        // A login owns its folder from the moment it is admitted until its child has
        // ended, and it writes a credential pair into that folder at a moment nothing
        // here chooses. Renaming that folder's pair out from under it leaves the
        // account with two pairs, one live and one the login writes afterwards, which
        // is the second holder the whole design refuses. The login is admitted under
        // this same gate, so a login already in flight is visible here and one
        // starting now waits for this switch to finish.
        if (_logins.IsRunningAgainst(plan.IncomingFolderPath)
            || (plan.OutgoingFolderPath is string outgoingFolderPath && _logins.IsRunningAgainst(outgoingFolderPath)))
        {
            LogRefused(target.Value, SwitchRefusal.LoginInProgress);
            return Result<SwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.LoginInProgress);
        }

        Result<IAsyncDisposable, string> held = await _pairs.AcquireRefreshLockAsync(_options.RefreshLockWaitBound, cancellationToken);
        if (held.IsFailure)
        {
            LogLockRefused(target.Value, held.Error);
            return Result<SwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.RefreshLockPresent);
        }

        await using (held.Value)
        {
            // Read again under the lock: a session may have refreshed, and so rotated, the
            // live pair during the wait, and the journal must carry the fingerprint that is
            // on disk. A pair that appeared or vanished meanwhile invalidates the plan.
            CredentialPair? lockedLive = await _pairs.ReadLiveAsync(cancellationToken);
            if ((liveCredentials is null) != (lockedLive is null))
            {
                LogRefused(target.Value, SwitchRefusal.LiveIdentityUnverified);
                return Result<SwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.LiveIdentityUnverified);
            }

            SwitchJournalEntry entry = new(
                plan.Outgoing, lockedLive?.Fingerprint, plan.OutgoingFolderPath,
                plan.Incoming, targetCredentials!.Fingerprint, plan.IncomingFolderPath,
                SwitchStep.Planned, now);
            await _journal.WriteAsync(entry, cancellationToken);

            // From the first move on, the request's own token is not consulted: a browser
            // abort must not leave the live directory without a pair. Every step below runs
            // to completion or is journaled for startup reconciliation.
            CancellationToken committed = CancellationToken.None;
            if (plan.Outgoing is AccountEmail outgoing && plan.OutgoingFolderPath is string outgoingFolder)
            {
                await _profiles.EnsureFolderAsync(outgoing, committed);
                if (live.Account is not null)
                {
                    await _profiles.WriteProfileAsync(outgoingFolder, live.Account, committed);
                }

                await _pairs.MoveLiveToParkedAsync(outgoingFolder, committed);
            }

            // The unpark follows the park at once and the journal catches up afterwards, so
            // the window in which no live pair exists is two renames, not a flushed write.
            await _pairs.MoveParkedToLiveAsync(plan.IncomingFolderPath, committed);
            await _journal.WriteAsync(entry with { StepReached = SwitchStep.Unparked }, committed);

            // The CLI never re-stamps oauthAccount on an ordinary request (probe 1.5a), so
            // the patch is unconditional: without it every session keeps naming the
            // outgoing account and the tool cannot plan the next switch.
            await _stateFile.PatchAccountBlockAsync(plan.IncomingAccount, committed);
            await _journal.WriteAsync(entry with { StepReached = SwitchStep.Patched }, committed);

            await WriteLiveOwnerAsync(targetCredentials.Fingerprint, plan.Incoming, committed);
            await _journal.ClearAsync(committed);
        }

        // The verification belongs to the committed switch as well: an aborted request must
        // still get the outcome, and the CLI adapter bounds the read with its own timeout.
        Result<ClaudeAuthStatus, string> verification = await _authStatus.ReadAsync(null, CancellationToken.None);
        bool mismatch = verification.IsSuccess
            && verification.Value.Email is string reported
            && !string.Equals(reported.Trim(), plan.Incoming.Value, StringComparison.OrdinalIgnoreCase);
        LogSwitched(plan.Outgoing?.Value, plan.Incoming.Value, mismatch);
        return Result<SwitchOutcome, SwitchRefusal>.Success(new SwitchOutcome(plan.Incoming, plan.Outgoing, verification, mismatch, _timeProvider.GetUtcNow()));
    }

    private async Task<LiveAccountState> SnapshotLiveAsync(CancellationToken cancellationToken)
    {
        OAuthAccountBlock? account = await _stateFile.ReadAccountBlockAsync(cancellationToken);
        CredentialPair? pair = await _pairs.ReadLiveAsync(cancellationToken);
        return new LiveAccountState(
            _options.LiveConfigDirectory,
            _options.StateFilePath,
            account,
            pair is not null,
            pair?.Fingerprint,
            _pairs.FreshLockFileName(_secondaryLockGuardAge));
    }

    /// <summary>
    /// Clears this writer's own temporary files, which a crash between the temp
    /// write and the rename leaves behind. A temp that parses as a credential
    /// pair is the dangerous one: it holds a refresh token that neither the
    /// lineage scan nor the single-holder check would ever match, because both
    /// look only for <c>.credentials.json</c>, and after a write-back it may
    /// hold the only live token of its lineage. Those move to quarantine, where
    /// the operator can see them and the lineage scan does not; any other
    /// leftover temp is deleted. Nothing of this tool's can be in flight here:
    /// the sweep runs at startup, under the mutation gate, and the instance lock
    /// rules out a second copy of the tool.
    /// </summary>
    private async Task<(IReadOnlyList<string> Quarantined, IReadOnlyList<string> Stranded)> SweepTemporariesAsync(CancellationToken cancellationToken)
    {
        List<string> quarantined = [];
        List<string> stranded = [];
        // Each root is listed under its own guard inside TemporaryFiles, so one
        // directory that refuses to be listed costs that directory and no other.
        foreach (string path in TemporaryFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await SweepOneAsync(path, cancellationToken) is string destination)
                {
                    quarantined.Add(destination);
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Startup runs before anything else can work, so an unreadable or
                // held temp reports itself rather than taking the app down with it.
                LogStrandedTemporary(path);
                stranded.Add(path);
            }
        }

        return (quarantined, stranded);
    }

    /// <summary>
    /// Deals with one temporary file: returns its quarantine path when it held a
    /// credential pair, null when it was deleted as ordinary residue. Refuses,
    /// by throwing, to move a pair anywhere it would have to be copied.
    /// </summary>
    private async Task<string?> SweepOneAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsCredentialTemporary(path))
        {
            // The same sharing-violation retry every other file operation here uses:
            // a scanner holding a temp for a moment must not fail a startup.
            await AtomicBytesFile.DeleteWithRetryAsync(path, cancellationToken);
            return null;
        }

        string stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string destinationDirectory = Path.Combine(_quarantineDirectory, stamp + "-temp-" + Path.GetFileName(Path.GetDirectoryName(path)!));
        string destination = Path.Combine(destinationDirectory, Path.GetFileName(path));
        if (File.Exists(destination) || !OnOneVolume(path, destination))
        {
            // Never a copy: a second holder of a refresh token is the one thing this
            // tool refuses to create, so the file stays where it is and the banner
            // sends the operator to it.
            throw new IOException("Refusing to move " + path + " to " + destination + ": a credential pair moves by rename or not at all.");
        }

        Directory.CreateDirectory(destinationDirectory);
        await AtomicBytesFile.MoveIntoPlaceWithRetryAsync(path, destination, cancellationToken);
        LogQuarantined(path, destination, "credential temporary");
        return destination;
    }

    /// <summary>
    /// This writer's temp names only: <c>.&lt;name&gt;.&lt;32 hex&gt;.tmp</c>, as
    /// <see cref="AtomicBytesFile"/> forms them. The CLI's own in-flight temps do
    /// not match that shape and are never touched.
    /// </summary>
    private IEnumerable<string> TemporaryFiles()
    {
        List<string> roots = [_options.LiveConfigDirectory, .. ProfileFolders()];
        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string path in ListTemporaries(root, _shallowSweep))
            {
                yield return path;
            }
        }

        if (Directory.Exists(_options.AppDataDirectory))
        {
            // App data holds the journal, the owner record, the snapshot cache, and
            // the recovery files, in nested directories, so this leg goes deep. The
            // quarantine itself is skipped: its contents are already at rest.
            foreach (string path in ListTemporaries(_options.AppDataDirectory, _deepSweep)
                .Where(path => !path.StartsWith(_quarantineDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// One root's temporaries, materialized under its own guard. Listing is lazy,
    /// so an unlistable directory would otherwise throw from a <c>foreach</c>
    /// header far away. Per root rather than per sweep on purpose: a permission
    /// on one profile folder must not silently cost the live directory its sweep
    /// and let a switch proceed over a stranded credential temporary.
    /// </summary>
    private IReadOnlyList<string> ListTemporaries(string root, EnumerationOptions options)
    {
        try
        {
            return [.. Directory.EnumerateFiles(root, ".*.tmp", options).Where(IsOwnTemporary)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogSweepIncomplete(root, exception.Message);
            return [];
        }
    }

    private IReadOnlyList<string> ProfileFolders()
    {
        try
        {
            return Directory.Exists(_options.ProfilesRoot) ? [.. Directory.EnumerateDirectories(_options.ProfilesRoot)] : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogSweepIncomplete(_options.ProfilesRoot, exception.Message);
            return [];
        }
    }

    private static bool IsOwnTemporary(string path) => OwnTemporaryName().IsMatch(Path.GetFileName(path));

    [GeneratedRegex(@"^\..+\.[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnTemporaryName();

    /// <summary>
    /// Whether a temporary file was being written as a credential pair, decided
    /// by the name it was going to take, never by parsing what is in it. A crash
    /// can stop the write anywhere: a truncated temp holds bytes that are not
    /// valid JSON and can still be the only copy of a rotated refresh token, so
    /// parsing would delete exactly the file worth keeping.
    /// </summary>
    private static bool IsCredentialTemporary(string path)
    {
        // ".<intended name>.<32 hex>.tmp" as AtomicBytesFile forms it. The caller
        // filters on that shape already; the length check keeps this honest for
        // anyone who calls it without doing so.
        string name = Path.GetFileName(path);
        const int SuffixLength = 32 + 1 + 4;
        return name.Length > SuffixLength + 1
            && name[1..^SuffixLength].EndsWith(FileSystemCredentialPairStore.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool OnOneVolume(string first, string second) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(first)),
            Path.GetPathRoot(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task<IReadOnlyList<string>> QuarantineDuplicateLineagesAsync(CancellationToken cancellationToken)
    {
        List<(string Path, bool IsLive, RefreshTokenFingerprint Fingerprint)> holders = [];
        string livePath = Path.Combine(_options.LiveConfigDirectory, FileSystemCredentialPairStore.FileName);
        if (await _pairs.ReadLiveAsync(cancellationToken) is CredentialPair live)
        {
            holders.Add((livePath, true, live.Fingerprint));
        }

        if (Directory.Exists(_options.ProfilesRoot))
        {
            foreach (string folder in Directory.EnumerateDirectories(_options.ProfilesRoot))
            {
                if (await _pairs.ReadParkedAsync(folder, cancellationToken) is CredentialPair parked)
                {
                    holders.Add((Path.Combine(folder, FileSystemCredentialPairStore.FileName), false, parked.Fingerprint));
                }
            }
        }

        List<string> quarantined = [];
        foreach (IGrouping<RefreshTokenFingerprint, (string Path, bool IsLive, RefreshTokenFingerprint Fingerprint)> lineage in holders.GroupBy(static holder => holder.Fingerprint))
        {
            (string Path, bool IsLive, RefreshTokenFingerprint Fingerprint)[] copies = [.. lineage];
            if (copies.Length < 2)
            {
                continue;
            }

            // The live copy is what every open session uses, so it is the one that stays.
            (string Path, bool IsLive, RefreshTokenFingerprint Fingerprint) kept = copies.FirstOrDefault(static copy => copy.IsLive, copies[0]);
            foreach ((string path, _, _) in copies.Where(copy => !string.Equals(copy.Path, kept.Path, StringComparison.Ordinal)))
            {
                string stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                string folder = Path.GetDirectoryName(path)!;
                string destinationDirectory = Path.Combine(_quarantineDirectory, stamp + "-" + Path.GetFileName(folder));
                // The extra copy is always a parked one (the live copy is the one kept), and it
                // moves by the store's guarded rename: never a copy across volumes.
                await _pairs.MoveParkedToQuarantineAsync(folder, destinationDirectory, cancellationToken);
                string destination = Path.Combine(destinationDirectory, FileSystemCredentialPairStore.FileName);
                quarantined.Add(destination);
                LogQuarantined(path, destination, lineage.Key.Sha256Hex[..12]);
            }
        }

        return quarantined;
    }

    private async Task<(string Outcome, bool Blocks)> ReconcileJournalAsync(CancellationToken cancellationToken)
    {
        SwitchJournalEntry? entry = await _journal.ReadOpenAsync(cancellationToken);
        if (entry is null)
        {
            return ("no open journal", false);
        }

        RefreshTokenFingerprint? liveFingerprint = (await _pairs.ReadLiveAsync(cancellationToken))?.Fingerprint;
        string incomingFolder = entry.IncomingFolderPath;
        RefreshTokenFingerprint? incomingParkedFingerprint = (await _pairs.ReadParkedAsync(incomingFolder, cancellationToken))?.Fingerprint;
        RefreshTokenFingerprint? outgoingParkedFingerprint = entry.OutgoingFolderPath is string outgoingFolderPath
            ? (await _pairs.ReadParkedAsync(outgoingFolderPath, cancellationToken))?.Fingerprint
            : null;
        bool incomingStillParked = incomingParkedFingerprint == entry.IncomingFingerprint;
        bool outgoingParked = entry.OutgoingFolderPath is not null && outgoingParkedFingerprint == entry.OutgoingFingerprint;

        // A live token rotates on the CLI's next refresh, so after a crash the live
        // fingerprint may match neither side of the journal. The folder layout still
        // says what happened: a live pair with the incoming folder emptied (and the
        // outgoing pair parked, when there was one) is a finished unpark.
        bool unparkFinished = liveFingerprint == entry.IncomingFingerprint
            || (liveFingerprint is not null && incomingParkedFingerprint is null && (entry.OutgoingFolderPath is null || outgoingParked));

        if (unparkFinished && liveFingerprint is RefreshTokenFingerprint liveNow)
        {
            // The unpark happened; only the patch, the owner record, and the clear may be missing.
            OAuthAccountBlock? current = await _stateFile.ReadAccountBlockAsync(cancellationToken);
            if (current?.Email != entry.Incoming)
            {
                OAuthAccountBlock? incomingAccount = await _profiles.ReadAccountAsync(incomingFolder, cancellationToken);
                if (incomingAccount is null)
                {
                    return ("the unpark of " + entry.Incoming.Value + " completed but its account block is missing from " + incomingFolder + ", so the state file was not patched", true);
                }

                await _stateFile.PatchAccountBlockAsync(incomingAccount, cancellationToken);
            }

            await WriteLiveOwnerAsync(liveNow, entry.Incoming, cancellationToken);
            await _journal.ClearAsync(cancellationToken);
            LogReconciled("completed", entry.Incoming.Value);
            return ("completed the switch to " + entry.Incoming.Value, false);
        }

        if (liveFingerprint is null && outgoingParked && incomingStillParked && entry.OutgoingFolderPath is string parkedFolder)
        {
            // Parked but never unparked: put the outgoing pair back, which restores the pre-switch
            // state. The move runs inside Claude Code's refresh lock like every other credential
            // move, so a session refreshing its cached pair cannot replace the restored file.
            Result<IAsyncDisposable, string> held = await _pairs.AcquireRefreshLockAsync(_options.RefreshLockWaitBound, cancellationToken);
            if (held.IsFailure)
            {
                LogReconciled("deferred", entry.Outgoing?.Value ?? "(none)");
                return ("the switch to " + entry.Incoming.Value + " is parked but not unwound yet: " + held.Error + "; restart once the session's refresh has finished", true);
            }

            await using (held.Value)
            {
                await _pairs.MoveParkedToLiveAsync(parkedFolder, cancellationToken);
                await _journal.ClearAsync(cancellationToken);
            }

            LogReconciled("unwound", entry.Outgoing?.Value ?? "(none)");
            return ("unwound the switch; " + (entry.Outgoing?.Value ?? "the previous pair") + " is live again", false);
        }

        // Nothing moved when the incoming pair is still parked and the live directory holds
        // the outgoing pair (by fingerprint, or rotated: a live pair while the outgoing
        // folder holds none) or, for a switch that had nothing to park, no pair at all.
        bool liveIsOutgoing = liveFingerprint == entry.OutgoingFingerprint
            || (liveFingerprint is not null && entry.OutgoingFingerprint is not null && outgoingParkedFingerprint is null);
        bool nothingMoved = (liveIsOutgoing || (liveFingerprint is null && entry.OutgoingFingerprint is null)) && incomingStillParked;
        if (nothingMoved)
        {
            await _journal.ClearAsync(cancellationToken);
            LogReconciled("cleared", entry.Incoming.Value);
            return ("cleared a journal written before any move", false);
        }

        LogReconciled("unresolved", entry.Incoming.Value);
        return ("the live pair matches neither side of the journaled switch to " + entry.Incoming.Value + "; resolve by hand under " + _options.AppDataDirectory, true);
    }

    /// <summary>
    /// The first credential-bearing temporary still sitting where a crashed write
    /// left it, or null. The startup sweep moves these to quarantine; one that is
    /// still here could not be moved, and it holds a refresh token that no
    /// single-holder check matches, so no switch may run over it.
    /// </summary>
    private string? StrandedCredentialTemporary() =>
        TemporaryFiles().FirstOrDefault(IsCredentialTemporary);

    private bool QuarantineHoldsFiles() =>
        Directory.Exists(_quarantineDirectory) && Directory.EnumerateFiles(_quarantineDirectory, "*", SearchOption.AllDirectories).Any();

    /// <summary>
    /// The account the live pair was unparked for, from the owner record. The CLI
    /// rotates the refresh token on its first refresh after every unpark, so a
    /// record whose fingerprint no longer matches is the normal case within seconds
    /// of a switch: while the state file still names the recorded owner the record
    /// is re-bound to the new fingerprint. When the state file names another
    /// account, a block the CLI stamped after the record was written means a login
    /// replaced the pair and the record is released; an older block means a
    /// session wrote a stale identity back, and the recorded owner stands so the
    /// planner refuses. Every transition is logged; the guard never lapses silently.
    /// </summary>
    private async Task<AccountEmail?> ReadLiveOwnerAsync(LiveAccountState live, CancellationToken cancellationToken)
    {
        if (live.Fingerprint is not RefreshTokenFingerprint liveFingerprint || !File.Exists(_liveOwnerPath))
        {
            return null;
        }

        string? fingerprint;
        string? email;
        JsonObject? record;
        try
        {
            record = JsonNode.Parse(await SharedFileReader.ReadAllBytesAsync(_liveOwnerPath, cancellationToken)) as JsonObject;
            fingerprint = record?["fingerprint"] is JsonValue fingerprintValue && fingerprintValue.TryGetValue(out string? fingerprintText) ? fingerprintText : null;
            email = record?["email"] is JsonValue emailValue && emailValue.TryGetValue(out string? emailText) ? emailText : null;
        }
        catch (JsonException)
        {
            // A corrupt record is no recorded owner; the next switch writes a fresh one.
            return null;
        }

        if (fingerprint is null || email is null)
        {
            return null;
        }

        AccountEmail owner = new(email);
        if (new RefreshTokenFingerprint(fingerprint) == liveFingerprint)
        {
            return owner;
        }

        if (live.Account?.Email == owner)
        {
            await WriteLiveOwnerAsync(liveFingerprint, owner, cancellationToken);
            LogOwnerRebound(owner.Value, liveFingerprint.Sha256Hex[..12]);
            return owner;
        }

        DateTimeOffset? recordedAt = record?["at"] is JsonValue atValue
            && atValue.TryGetValue(out string? at)
            && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;
        if (live.Account?.ProfileFetchedAt is DateTimeOffset fetchedAt && recordedAt is DateTimeOffset writtenAt && fetchedAt > writtenAt)
        {
            LogOwnerReleased(owner.Value, live.Account.Email?.Value ?? "(none)");
            await AtomicBytesFile.DeleteWithRetryAsync(_liveOwnerPath, cancellationToken);
            return null;
        }

        LogOwnerStale(owner.Value, live.Account?.Email?.Value ?? "(none)");
        return owner;
    }

    private Task WriteLiveOwnerAsync(RefreshTokenFingerprint fingerprint, AccountEmail owner, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_liveOwnerPath)!);
        return AtomicJsonFile.WriteAsync(
            _liveOwnerPath,
            new JsonObject
            {
                ["fingerprint"] = fingerprint.Sha256Hex,
                ["email"] = owner.Value,
                ["at"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "live owner record re-bound to the rotated pair {Fingerprint} for {Owner}")]
    private partial void LogOwnerRebound(string owner, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "live owner record for {Owner} released: the CLI stamped a login as {Named} after it was written")]
    private partial void LogOwnerReleased(string owner, string named);

    [LoggerMessage(Level = LogLevel.Warning, Message = "live owner record for {Owner} disagrees with the state file, which names {Named} from before the record; switching refuses until they agree")]
    private partial void LogOwnerStale(string owner, string named);

    [LoggerMessage(Level = LogLevel.Information, Message = "state file re-patched to {Owner}: a session had written back its older block naming {Named}")]
    private partial void LogRepatched(string owner, string named);

    [LoggerMessage(Level = LogLevel.Warning, Message = "state file is stale but {Owner} has no profile block under {Folder} to restore it from")]
    private partial void LogRepairImpossible(string owner, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "switched {From} -> {To} (cli mismatch: {Mismatch})")]
    private partial void LogSwitched(string? from, string to, bool mismatch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "switch to {Target} refused: {Refusal}")]
    private partial void LogRefused(string target, SwitchRefusal refusal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "switch to {Target} refused: refresh lock not acquired ({Detail})")]
    private partial void LogLockRefused(string target, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "quarantined a duplicate credential lineage {Fingerprint}: {Source} -> {Destination}")]
    private partial void LogQuarantined(string source, string destination, string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "a temporary file at {Source} holds a credential pair and could not be moved to quarantine; it stays where it is and switching is blocked")]
    private partial void LogStrandedTemporary(string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the stale-temporary sweep could not list {Root}, so that directory was skipped this start: {Detail}")]
    private partial void LogSweepIncomplete(string root, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "journal reconciliation {Outcome} for {Incoming}")]
    private partial void LogReconciled(string outcome, string incoming);
}
