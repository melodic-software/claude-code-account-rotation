using System.Globalization;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// What the leader asks the follower to take. No token crosses the wire:
/// identity is the SHA-256 fingerprint and the pair itself is read from
/// <see cref="ClaimedPath"/>, a file on the store's own volume that the leader
/// renamed into its mailbox before asking.
/// </summary>
internal sealed record ImportRequest(
    AccountEmail Email,
    string ClaimedPath,
    RefreshTokenFingerprint Fingerprint,
    JsonObject Account,
    string ExportPath);

/// <summary>
/// The answer to <c>POST /api/import</c>: the import has run F1 to F4 and
/// stopped. <see cref="ExportedFingerprint"/> is the outgoing pair the leader
/// must now read natively and verify before it may commit, or null when this
/// side held nothing and there is nothing to verify.
/// </summary>
internal sealed record ImportAnswer(
    RefreshTokenFingerprint? ExportedFingerprint,
    AccountEmail? Outgoing,
    bool AlreadyImported,
    ImportResult? Result);

/// <summary>What the commit hands back: which account left this side, and its block for the leader's park.</summary>
internal sealed record ImportResult(
    AccountEmail? Outgoing,
    RefreshTokenFingerprint? OutgoingFingerprint,
    JsonObject? OutgoingAccount,
    bool AlreadyImported);

/// <summary>
/// <c>GET /api/import-status</c>: what the follower believes about the import
/// the leader is asking after, decided from the files and not only the journal.
/// </summary>
internal sealed record ImportStatus(
    bool Imported,
    ImportStep? JournalStep,
    RefreshTokenFingerprint? LiveFingerprint,
    string Detail);

/// <summary>
/// The follower's staged import, held across two calls.
/// <para>
/// <c>POST /api/import</c> runs F1 to F4 and <b>stops</b>: the incoming pair is
/// staged and the outgoing pair is exported, but the live file is untouched and
/// the answer is <c>Exported {fa}</c> (or <c>Exported {none}</c>). Only
/// <c>POST /api/import/commit</c> runs F5 to F8, and this class refuses it
/// unless its own journal reads <see cref="ImportStep.Exported"/>. That refusal
/// lives here, in the follower, rather than in the caller, so a leader that
/// forgets to gate cannot skip the one check that stands between a host power
/// loss and a lost credential lineage.
/// </para>
/// <para>
/// The follower's mutation gate and the WSL live directory's
/// <c>.oauth_refresh.lock</c> are both held from F2 until the import ends. That
/// hold outlives the lock's own 60 s stale threshold, so three rules keep it
/// honest: a commit later than <see cref="CommitBudget"/> is refused and the
/// import self-aborts; the live file is re-read immediately before F5 and a
/// rotation aborts instead of swapping; and the lock directory's mtime is
/// re-stamped every <see cref="HeartbeatInterval"/> while it is held.
/// </para>
/// </summary>
internal sealed partial class FollowerImport : IDisposable
{
    private readonly SwitchOptions _options;
    private readonly StagedImportCredentialPairStore _pairs;
    private readonly ImportJournal _journal;
    private readonly ImportReconciler _reconciler;
    private readonly ClaudeStateFile _stateFile;
    private readonly CredentialMutationGate _gate;
    private readonly OAuthRefreshLock _refreshLock;
    private readonly string _refreshLockDirectory;
    private readonly string _liveOwnerPath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FollowerImport> _logger;

    /// <summary>Guards the hold: two requests never see it half built or half unwound.</summary>
    private readonly SemaphoreSlim _sync = new(1, 1);

    private Hold? _hold;
    private bool _reconciled;
    private ImportReconciliation? _atStart;

    public FollowerImport(
        SwitchOptions options,
        StagedImportCredentialPairStore pairs,
        ImportJournal journal,
        ImportReconciler reconciler,
        ClaudeStateFile stateFile,
        CredentialMutationGate gate,
        TimeProvider timeProvider,
        ILogger<FollowerImport> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(stateFile);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _pairs = pairs;
        _journal = journal;
        _reconciler = reconciler;
        _stateFile = stateFile;
        _gate = gate;
        _refreshLock = new OAuthRefreshLock(options.LiveConfigDirectory, timeProvider);
        _refreshLockDirectory = Path.Combine(Path.GetFullPath(options.LiveConfigDirectory), OAuthRefreshLock.DirectoryName);
        _liveOwnerPath = Path.Combine(Path.GetFullPath(options.AppDataDirectory), "state", "live-owner.json");
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The window between the <c>Exported</c> answer and the commit. A later commit is refused.</summary>
    public TimeSpan CommitBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long the hold may sit idle before the follower unwinds it and releases the lock.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How often the lock directory's mtime is re-stamped while the hold lasts.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Shutdown while a hold is open: the lock directory and the gate permit go
    /// back, so a follower that is stopped between the two calls does not leave
    /// a lock behind for the CLI to wait out. The journal stays, which is what
    /// the next process's reconciliation reads.
    /// </summary>
    public void Dispose()
    {
        _hold?.Dispose();
        _hold = null;
        _sync.Dispose();
    }

    /// <summary>F1 to F4: idempotency, the lock, the stage, the export, and stop.</summary>
    public async Task<Result<ImportAnswer, string>> ImportAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ReconcileOnceAsync(cancellationToken);
        await _sync.WaitAsync(cancellationToken);
        try
        {
            // F1. A re-issued L3a while the hold is still open is answered from the
            // hold: the leader restarted, not the request changed.
            if (_hold is Hold open)
            {
                return open.Request.Email == request.Email && open.Request.Fingerprint == request.Fingerprint
                    ? Result<ImportAnswer, string>.Success(new ImportAnswer(open.OutgoingFingerprint, open.Outgoing, false, null))
                    : Result<ImportAnswer, string>.Failure("an import of " + open.Request.Email.Value + " is already in flight");
            }

            if (await AlreadyImportedAsync(request, cancellationToken) is ImportResult done)
            {
                return Result<ImportAnswer, string>.Success(new ImportAnswer(done.OutgoingFingerprint, done.Outgoing, true, done));
            }

            Result<Unit, string> reachable = InTheMailbox(request);
            return reachable.IsFailure
                ? Result<ImportAnswer, string>.Failure(reachable.Error)
                : await StageAndExportAsync(request, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>F5 to F8, and only from <see cref="ImportStep.Exported"/>.</summary>
    public async Task<Result<ImportResult, string>> CommitAsync(AccountEmail email, CancellationToken cancellationToken)
    {
        await ReconcileOnceAsync(cancellationToken);
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_hold is not Hold hold)
            {
                // No hold: either the import finished (answer from the record) or it
                // never reached the export, or the idle window unwound it. All three
                // are "not imported" to a leader that has not been told otherwise.
                LastImportEntry? last = await _journal.ReadLastImportAsync(cancellationToken);
                return last is not null && last.Incoming == email
                    ? Result<ImportResult, string>.Success(new ImportResult(last.Outgoing, last.OutgoingFingerprint, last.OutgoingAccount, AlreadyImported: true))
                    : Result<ImportResult, string>.Failure("not imported: no export of " + email.Value + " is waiting for a commit");
            }

            if (hold.Request.Email != email)
            {
                return Result<ImportResult, string>.Failure("not imported: the open import is for " + hold.Request.Email.Value + ", not " + email.Value);
            }

            ImportJournalEntry? journal = await _journal.ReadOpenAsync(cancellationToken);
            if (journal?.StepReached != ImportStep.Exported)
            {
                return Result<ImportResult, string>.Failure(
                    "not imported: the journal reads " + (journal?.StepReached.ToString() ?? "nothing") + ", and only an export may be committed");
            }

            TimeSpan waited = _timeProvider.GetUtcNow() - hold.ExportedAt;
            if (waited > CommitBudget)
            {
                await UnwindAsync(hold, cancellationToken);
                return Result<ImportResult, string>.Failure(
                    "not imported: the commit arrived " + waited.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)
                    + " s after the export, past the " + CommitBudget.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s budget; the import was unwound");
            }

            return await SwapAndFinishAsync(hold, journal, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>Unwind from <see cref="ImportStep.Exported"/>: delete the export and the staging file, leave the live pair.</summary>
    public async Task<Result<Unit, string>> AbortAsync(AccountEmail email, CancellationToken cancellationToken)
    {
        await ReconcileOnceAsync(cancellationToken);
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_hold is not Hold hold)
            {
                return Result<Unit, string>.Failure("nothing to abort: no import of " + email.Value + " is in flight");
            }

            if (hold.Request.Email != email)
            {
                return Result<Unit, string>.Failure("the open import is for " + hold.Request.Email.Value + ", not " + email.Value);
            }

            await UnwindAsync(hold, cancellationToken);
            return Result<Unit, string>.Success(Unit.Value);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// What the leader's reconciliation asks after a crash on either side. When
    /// it names an account, the answer is about that account: the leader's
    /// <c>ExportVerified</c> row turns on a definite "not imported", and a
    /// completed import of some other account must not read as one.
    /// </summary>
    public async Task<ImportStatus> StatusAsync(CancellationToken cancellationToken, AccountEmail? about = null)
    {
        await ReconcileOnceAsync(cancellationToken);
        ImportJournalEntry? journal = await _journal.ReadOpenAsync(cancellationToken);
        CredentialPair? live = await _pairs.ReadLiveAsync(cancellationToken);
        if (journal is not null)
        {
            return new ImportStatus(false, journal.StepReached, live?.Fingerprint, "an import is in flight at " + journal.StepReached);
        }

        LastImportEntry? last = await _journal.ReadLastImportAsync(cancellationToken);
        if (last is null)
        {
            return new ImportStatus(false, null, live?.Fingerprint, _atStart?.Outcome ?? "no import in flight and none recorded");
        }

        return about is AccountEmail asked && last.Incoming != asked
            ? new ImportStatus(false, null, live?.Fingerprint, "not imported: the last import here was of " + last.Incoming.Value)
            : new ImportStatus(true, null, live?.Fingerprint, "the last import of " + last.Incoming.Value + " completed");
    }

    /// <summary>
    /// The crash table, run once before this process answers anything. It is a
    /// once-guard rather than a hosted service because a hosted service's order
    /// against the web host is not this class's to guarantee, and every entry
    /// point here passes through it.
    /// </summary>
    private async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        if (_reconciled)
        {
            return;
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_reconciled)
            {
                return;
            }

            using IDisposable permit = await _gate.AcquireAsync(_options.MutationGateTimeout, cancellationToken);
            _atStart = await _reconciler.ReconcileAsync(cancellationToken);
            _reconciled = true;
            LogReconciled(_atStart.Outcome);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// F1's second half: the import is already done when the record says so, or
    /// when the live pair is the incoming one and the owner record agrees. The
    /// live check is what answers a leader whose own journal was lost.
    /// </summary>
    /// <summary>
    /// The two paths a request names are the caller's, and this is a loopback
    /// route a local process can reach. The mailbox is the one directory the
    /// design allows a follower to touch on the other volume, so both paths must
    /// sit directly under it: without this, a request could have the follower
    /// copy any file it can read to any path it can write.
    /// </summary>
    private Result<Unit, string> InTheMailbox(ImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(_options.Mailbox))
        {
            return Result<Unit, string>.Failure("this process has no mailbox configured and cannot import");
        }

        string mailbox = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.Mailbox));
        foreach ((string label, string path) in new[] { ("claimedPath", request.ClaimedPath), ("exportPath", request.ExportPath) })
        {
            string full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is not string parent
                || !string.Equals(Path.TrimEndingDirectorySeparator(parent), mailbox, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return Result<Unit, string>.Failure("the " + label + " " + full + " is not directly under the mailbox " + mailbox + "; a follower copies nothing else");
            }
        }

        return Result<Unit, string>.Success(Unit.Value);
    }

    private async Task<ImportResult?> AlreadyImportedAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        LastImportEntry? last = await _journal.ReadLastImportAsync(cancellationToken);
        if (last is null || last.Incoming != request.Email || last.IncomingFingerprint != request.Fingerprint)
        {
            return null;
        }

        return new ImportResult(last.Outgoing, last.OutgoingFingerprint, last.OutgoingAccount, AlreadyImported: true);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The gate permit and the refresh lock are the hold: ownership transfers to the Hold, which releases both however the import ends, and to Dispose for a shutdown between the two calls.")]
    private async Task<Result<ImportAnswer, string>> StageAndExportAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        IDisposable permit = await _gate.AcquireAsync(_options.MutationGateTimeout, cancellationToken);
        Result<IAsyncDisposable, string> locked = await _refreshLock.AcquireAsync(_options.RefreshLockWaitBound, cancellationToken);
        if (locked.IsFailure)
        {
            permit.Dispose();
            return Result<ImportAnswer, string>.Failure(locked.Error);
        }

        Hold hold = new(request, permit, locked.Value, _timeProvider.GetUtcNow());
        try
        {
            // F2: what is leaving, if anything. Both may be absent, which is the
            // first real hand-off's own starting state and not an error.
            CredentialPair? live = await _pairs.ReadLiveAsync(cancellationToken);
            OAuthAccountBlock? outgoingAccount = live is null ? null : await _stateFile.ReadAccountBlockAsync(cancellationToken);
            if (live is not null && outgoingAccount?.Email is null)
            {
                // A pair this side cannot name is a pair the leader cannot park, so
                // exporting it would strand it in the mailbox under no account's
                // name. A refused switch is the cheaper answer.
                hold.Dispose();
                return Result<ImportAnswer, string>.Failure(
                    "not imported: the live pair is here but the state file names no account for it, so nothing can say what would be leaving");
            }

            hold.Outgoing = outgoingAccount?.Email;
            hold.OutgoingFingerprint = live?.Fingerprint;
            hold.OutgoingAccount = outgoingAccount?.Raw;

            ImportJournalEntry entry = new(
                request.Email,
                request.Fingerprint,
                request.ClaimedPath,
                request.ExportPath,
                hold.Outgoing,
                hold.OutgoingFingerprint,
                request.Account,
                hold.OutgoingAccount,
                ImportStep.Planned,
                hold.StartedAt);
            CrashInjection.KillIfConfigured(_options.FailAfterStep, ImportStep.Planned, beforeJournal: true);
            await _journal.WriteAsync(entry, cancellationToken);
            CrashInjection.KillIfConfigured(_options.FailAfterStep, ImportStep.Planned, beforeJournal: false);

            // F3: stage the incoming pair on the live volume and prove it landed.
            Result<Unit, string> staged = await _pairs.StageAsync(request.ClaimedPath, request.Fingerprint, cancellationToken);
            if (staged.IsFailure)
            {
                await UnwindAsync(hold, cancellationToken);
                return Result<ImportAnswer, string>.Failure(staged.Error);
            }

            CrashInjection.KillIfConfigured(_options.FailAfterStep, ImportStep.Staged, beforeJournal: true);
            await _journal.WriteAsync(entry with { StepReached = ImportStep.Staged }, cancellationToken);
            CrashInjection.KillIfConfigured(_options.FailAfterStep, ImportStep.Staged, beforeJournal: false);

            // F4: export the outgoing pair, or nothing when this side held nothing.
            if (hold.OutgoingFingerprint is RefreshTokenFingerprint outgoing)
            {
                Result<Unit, string> exported = await _pairs.ExportAsync(request.ExportPath, outgoing, cancellationToken);
                if (exported.IsFailure)
                {
                    await UnwindAsync(hold, cancellationToken);
                    return Result<ImportAnswer, string>.Failure(exported.Error);
                }
            }

            CrashInjection.KillIfConfigured(_options.FailAfterStep, ImportStep.Exported, beforeJournal: true);
            await _journal.WriteAsync(entry with { StepReached = ImportStep.Exported }, cancellationToken);
            CrashInjection.KillIfConfigured(_options.FailAfterStep, ImportStep.Exported, beforeJournal: false);

            hold.ExportedAt = _timeProvider.GetUtcNow();
            hold.StartHeartbeat(_timeProvider, HeartbeatInterval, Beat);
            _hold = hold;
            LogExported(request.Email.Value, hold.OutgoingFingerprint?.Sha256Hex[..12] ?? "none");
            return Result<ImportAnswer, string>.Success(new ImportAnswer(hold.OutgoingFingerprint, hold.Outgoing, false, null));
        }
        catch (IOException exception)
        {
            await UnwindAsync(hold, cancellationToken);
            return Result<ImportAnswer, string>.Failure("the import could not be staged: " + exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            await UnwindAsync(hold, cancellationToken);
            return Result<ImportAnswer, string>.Failure("the import could not be staged: " + exception.Message);
        }
    }

    private async Task<Result<ImportResult, string>> SwapAndFinishAsync(Hold hold, ImportJournalEntry entry, CancellationToken cancellationToken)
    {
        // F5's first half, and the rule that makes the 20 s budget a latency bound
        // rather than a safety claim: the pair on disk must still be the pair that
        // was exported. A rotation found here costs a refused switch, never a pair.
        CredentialPair? live = await _pairs.ReadLiveAsync(cancellationToken);
        if (live?.Fingerprint != hold.OutgoingFingerprint)
        {
            await UnwindAsync(hold, cancellationToken);
            return Result<ImportResult, string>.Failure(
                "not imported: the live pair is no longer the one that was exported ("
                + (live?.Fingerprint.Sha256Hex[..12] ?? "none") + " against " + (hold.OutgoingFingerprint?.Sha256Hex[..12] ?? "none")
                + "); a session rotated it, so nothing was swapped");
        }

        Result<Unit, string> swapped = _pairs.Swap();
        if (swapped.IsFailure)
        {
            await UnwindAsync(hold, cancellationToken);
            return Result<ImportResult, string>.Failure(swapped.Error);
        }

        CrashInjection.KillIfConfigured(_options.FailAfterStep, ImportStep.Swapped, beforeJournal: true);
        await _journal.WriteAsync(entry with { StepReached = ImportStep.Swapped }, cancellationToken);
        CrashInjection.KillIfConfigured(_options.FailAfterStep, ImportStep.Swapped, beforeJournal: false);

        ImportResult result = await FinishAsync(entry, _journal, _stateFile, _pairs, _liveOwnerPath, _options.FailAfterStep, _timeProvider, ImportStep.Swapped, cancellationToken);
        Release(hold);
        LogImported(entry.Incoming.Value, entry.Outgoing?.Value ?? "none");
        return Result<ImportResult, string>.Success(result);
    }

    /// <summary>
    /// F6 to F8, shared with the reconciler: whoever gets here has established
    /// that the swap has happened, and the rest is bookkeeping that is safe to
    /// repeat. Every step is idempotent, which is what lets a crash between any
    /// two of them be answered by running from the one the journal names.
    /// </summary>
    internal static async Task<ImportResult> FinishAsync(
        ImportJournalEntry entry,
        ImportJournal journal,
        ClaudeStateFile stateFile,
        StagedImportCredentialPairStore pairs,
        string liveOwnerPath,
        string? failAfterStep,
        TimeProvider timeProvider,
        ImportStep from,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(stateFile);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (from <= ImportStep.Swapped)
        {
            // F6: the claimed file goes only now, after the same lineage is live here.
            StagedImportCredentialPairStore.Release(entry.ClaimedPath);
            CrashInjection.KillIfConfigured(failAfterStep, ImportStep.Released, beforeJournal: true);
            await journal.WriteAsync(entry with { StepReached = ImportStep.Released }, cancellationToken);
            CrashInjection.KillIfConfigured(failAfterStep, ImportStep.Released, beforeJournal: false);
        }

        if (from <= ImportStep.Released)
        {
            // F7: the state file and the owner record now name the incoming account.
            await stateFile.PatchAccountBlockAsync(OAuthAccountBlock.FromJson(entry.IncomingAccount), cancellationToken);
            await WriteLiveOwnerAsync(liveOwnerPath, entry.IncomingFingerprint, entry.Incoming, timeProvider, cancellationToken);
            CrashInjection.KillIfConfigured(failAfterStep, ImportStep.Patched, beforeJournal: true);
            await journal.WriteAsync(entry with { StepReached = ImportStep.Patched }, cancellationToken);
            CrashInjection.KillIfConfigured(failAfterStep, ImportStep.Patched, beforeJournal: false);
        }

        // F8: the record that answers a re-issued request, then the journal goes.
        await journal.WriteLastImportAsync(
            new LastImportEntry(
                entry.Incoming,
                entry.IncomingFingerprint,
                entry.Outgoing,
                entry.OutgoingFingerprint,
                entry.OutgoingAccount,
                timeProvider.GetUtcNow()),
            cancellationToken);
        await journal.ClearAsync(cancellationToken);
        pairs.DeleteStaging();
        return new ImportResult(entry.Outgoing, entry.OutgoingFingerprint, entry.OutgoingAccount, AlreadyImported: false);
    }

    /// <summary>
    /// The owner record <see cref="LiveDirectorySwitch"/> writes on the leader,
    /// in the same shape and the same place, so the follower's live pair carries
    /// the same statement of who it belongs to.
    /// </summary>
    internal static Task WriteLiveOwnerAsync(
        string liveOwnerPath,
        RefreshTokenFingerprint fingerprint,
        AccountEmail owner,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(liveOwnerPath))!);
        return AtomicJsonFile.WriteAsync(
            liveOwnerPath,
            new JsonObject
            {
                ["fingerprint"] = fingerprint.Sha256Hex,
                ["email"] = owner.Value,
                ["at"] = timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken);
    }

    /// <summary>
    /// Everything an abort, a refusal and a self-abort share: the two files this
    /// import created go, the journal goes, and the hold is released. The live
    /// pair is never touched here, which is what makes "the outgoing pair is
    /// recoverable" true of every path through this method.
    /// </summary>
    private async Task UnwindAsync(Hold hold, CancellationToken cancellationToken)
    {
        _pairs.DeleteStaging();
        StagedImportCredentialPairStore.DeleteIfPresent(hold.Request.ExportPath);
        await _journal.ClearAsync(cancellationToken);
        Release(hold);
        LogUnwound(hold.Request.Email.Value);
    }

    private void Release(Hold hold)
    {
        _hold = null;
        hold.Dispose();
    }

    /// <summary>
    /// One tick: re-stamp the lock so the 60 s stale threshold never catches a
    /// hold that is still alive, and unwind a hold nobody has come back for.
    /// </summary>
    private void Beat()
    {
        try
        {
            if (Directory.Exists(_refreshLockDirectory))
            {
                Directory.SetLastWriteTimeUtc(_refreshLockDirectory, _timeProvider.GetUtcNow().UtcDateTime);
            }
        }
        catch (IOException)
        {
            // The lock was stolen or removed under us; the pre-swap re-read is what
            // catches that, and re-stamping is only ever best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        try
        {
            if (!_sync.Wait(0))
            {
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutdown disposed the gate between this tick being scheduled and it
            // running. Disposing a timer does not wait for a callback already in
            // flight, so this arrives after the hold has been given back.
            return;
        }

        try
        {
            if (_hold is Hold hold && _timeProvider.GetUtcNow() - hold.ExportedAt > IdleTimeout)
            {
                UnwindAsync(hold, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        finally
        {
            try
            {
                _sync.Release();
            }
            catch (ObjectDisposedException)
            {
                // Same race, one step later.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "import reconciliation at start: {Outcome}")]
    private partial void LogReconciled(string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "exported {Fingerprint} for the import of {Incoming}; waiting for the leader's commit")]
    private partial void LogExported(string incoming, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "imported {Incoming}; {Outgoing} left this side")]
    private partial void LogImported(string incoming, string outgoing);

    [LoggerMessage(Level = LogLevel.Information, Message = "the import of {Incoming} was unwound; the live pair was not touched")]
    private partial void LogUnwound(string incoming);

    /// <summary>
    /// The state that spans the two calls: what was asked, what was exported,
    /// and the two things that must be given back however the import ends.
    /// </summary>
    private sealed class Hold(ImportRequest request, IDisposable permit, IAsyncDisposable refreshLock, DateTimeOffset startedAt) : IDisposable
    {
        private ITimer? _heartbeat;

        public ImportRequest Request { get; } = request;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset ExportedAt { get; set; } = startedAt;

        public AccountEmail? Outgoing { get; set; }

        public RefreshTokenFingerprint? OutgoingFingerprint { get; set; }

        public JsonObject? OutgoingAccount { get; set; }

        public void StartHeartbeat(TimeProvider timeProvider, TimeSpan interval, Action beat) =>
            _heartbeat = timeProvider.CreateTimer(_ => beat(), null, interval, interval);

        public void Dispose()
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
            refreshLock.DisposeAsync().AsTask().GetAwaiter().GetResult();
            permit.Dispose();
        }
    }
}
