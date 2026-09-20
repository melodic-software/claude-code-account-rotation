using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>What a completed hand-off moved: the account now live on that side, and the one parked back.</summary>
internal sealed record WslSwitchOutcome(SideName Side, AccountEmail Now, AccountEmail? ParkedAs, DateTimeOffset At);

/// <summary>What the leader's own crash table found and did at start, or on a poll.</summary>
internal sealed record WslReconciliation(string Outcome, string? Banner);

/// <summary>One line of side state for the page: is it up, and what does it hold.</summary>
internal sealed record WslSideState(SideName Side, bool Online, AccountEmail? LiveAccount, string Detail);

/// <summary>
/// The leader's coordinator for a switch of the <b>other</b> side of this
/// machine: design 9.1's L1 to L4, with the leader half of design 9.3's crash
/// table.
/// <para>
/// The step that earns the class is <b>L3b, the export gate</b>. The follower
/// stops after F4 with the outgoing pair copied into the mailbox and its own
/// live pair still in place; before the leader may send the commit that
/// destroys that last local copy, it reads the exported file <b>natively</b>,
/// on the store's own volume, and requires the fingerprint the follower named.
/// A mismatch, a short read, or an absent file aborts the import, unclaims the
/// slot, and refuses the switch with nothing swapped on either side. This is
/// the only check in the design that does not go through the 9P layer whose
/// durability section 3 could not measure.
/// </para>
/// <para>
/// <c>Cancel</c> is never offered blind. Every path that unclaims does so only
/// after the follower has answered a definite "not imported", because a
/// follower that has already swapped holds the incoming pair live and the
/// outgoing pair only as the export: taking the claim back then would create a
/// second holder of one lineage and strand the other.
/// </para>
/// </summary>
internal sealed partial class WslSwitch : IDisposable
{
    /// <summary>How long the leader waits for the follower's F1 to F4. Design 9.1's L3a bound.</summary>
    public static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One hand-off at a time. The mutation gate is not enough on its own: it
    /// is released across L3, deliberately, so the rest of the tool keeps
    /// working while the other side does its copies — and a dashboard poll
    /// landing in that window would otherwise run the crash table against a
    /// journal whose own switch is still using it, and re-issue L3a beside it.
    /// </summary>
    private readonly SemaphoreSlim _handOff = new(1, 1);

    private readonly PeerRegistry _peers;
    private readonly FileSystemCredentialPairStore _pairs;
    private readonly ProfileFolderStore _profiles;
    private readonly ClaudeStateFile _stateFile;
    private readonly SharedStoreSlots _slots;
    private readonly WslSwitchJournal _journal;
    private readonly SwitchJournal _windowsJournal;
    private readonly ILoginSessionRunner _logins;
    private readonly ManagedLoginPolicyReader _policyReader;
    private readonly RecoveryFiles _recovery;
    private readonly QuotaState _quota;
    private readonly CredentialMutationGate _gate;
    private readonly SwitchOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WslSwitch> _logger;

    public WslSwitch(
        PeerRegistry peers,
        FileSystemCredentialPairStore pairs,
        ProfileFolderStore profiles,
        ClaudeStateFile stateFile,
        SharedStoreSlots slots,
        WslSwitchJournal journal,
        SwitchJournal windowsJournal,
        ILoginSessionRunner logins,
        ManagedLoginPolicyReader policyReader,
        RecoveryFiles recovery,
        QuotaState quota,
        CredentialMutationGate gate,
        SwitchOptions options,
        TimeProvider timeProvider,
        ILogger<WslSwitch> logger)
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(stateFile);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(windowsJournal);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _peers = peers;
        _pairs = pairs;
        _profiles = profiles;
        _stateFile = stateFile;
        _slots = slots;
        _journal = journal;
        _windowsJournal = windowsJournal;
        _logins = logins;
        _policyReader = policyReader;
        _recovery = recovery;
        _quota = quota;
        _gate = gate;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public void Dispose() => _handOff.Dispose();

    /// <summary>L1 to L4, with the gate taken for L1-L2, released across the hand-off, and taken again for L4.</summary>
    public async Task<Result<WslSwitchOutcome, SwitchRefusal>> SwitchToAsync(SideName side, AccountEmail target, CancellationToken cancellationToken)
    {
        if (!await _handOff.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            LogRefused(side.Value, target.Value, SwitchRefusal.MutationInProgress);
            return Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.MutationInProgress);
        }

        try
        {
            return await SwitchUnderHandOffAsync(side, target, cancellationToken);
        }
        finally
        {
            _handOff.Release();
        }
    }

    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> SwitchUnderHandOffAsync(SideName side, AccountEmail target, CancellationToken cancellationToken)
    {
        if (_peers.For(side) is not Peer peer)
        {
            LogRefused(side.Value, target.Value, SwitchRefusal.SideOffline);
            return Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.SideOffline);
        }

        // L1's first read, outside the gate on purpose: it is a network round
        // trip to another operating system, and holding the leader's one
        // mutation gate across it would stall every refresh and every Windows
        // switch for as long as the distro takes to answer.
        Result<PeerDashboard, string> dashboard = await peer.Instance.ReadDashboardAsync(cancellationToken);
        if (dashboard.IsFailure)
        {
            LogSideUnreachable(side.Value, dashboard.Error);
            return Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.SideOffline);
        }

        // Before anything moves. The two processes share a journal vocabulary,
        // a request shape and a crash table; a follower built from different
        // sources might answer every call and still reconcile by rules this
        // leader does not know, so a mismatch refuses the import rather than
        // discovering the disagreement after a rename.
        if (Incompatible(dashboard.Value) is string mismatch)
        {
            LogIncompatible(side.Value, mismatch);
            return Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.SideOffline);
        }

        Result<WslSwitchJournalEntry, SwitchRefusal> claimed = await ClaimAsync(peer, dashboard.Value, target, cancellationToken);
        if (claimed.IsFailure)
        {
            LogRefused(side.Value, target.Value, claimed.Error);
            return Result<WslSwitchOutcome, SwitchRefusal>.Failure(claimed.Error);
        }

        return await HandOffAsync(peer, claimed.Value, cancellationToken);
    }

    /// <summary>
    /// The leader's own crash table, design 9.3's second half. Run at start and
    /// on every dashboard read, so a hand-off whose follower died while this
    /// process stayed up is finished by the next poll rather than by a restart.
    /// </summary>
    public async Task<WslReconciliation> ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!await _handOff.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return new WslReconciliation("a hand-off is in flight; nothing to reconcile", null);
        }

        try
        {
            return await ReconcileUnderHandOffAsync(cancellationToken);
        }
        finally
        {
            _handOff.Release();
        }
    }

    private async Task<WslReconciliation> ReconcileUnderHandOffAsync(CancellationToken cancellationToken)
    {
        WslSwitchJournalEntry? entry = await _journal.ReadOpenAsync(cancellationToken);
        if (entry is null)
        {
            return new WslReconciliation("no hand-off in flight", null);
        }

        if (_peers.For(entry.Side) is not Peer peer)
        {
            return InTransitBanner(entry, "no peer is configured for " + entry.Side.Value);
        }

        // An offline side leaves both the claim and any export where they are.
        // The follower may have swapped, so an unclaim here would create a
        // second holder of the incoming lineage; the card says in transit and
        // the decision waits for the side to answer.
        if (entry.StepReached is WslSwitchStep.Claimed or WslSwitchStep.ExportVerified
            && (await peer.Instance.ReadDashboardAsync(cancellationToken)).IsFailure)
        {
            return InTransitBanner(entry, "the side is not answering");
        }

        Result<WslSwitchOutcome, SwitchRefusal> resumed = await HandOffAsync(peer, entry, cancellationToken);
        return resumed.Match(
            done => new WslReconciliation(
                "the hand-off of " + done.Now.Value + " to " + done.Side.Value + " was finished from " + entry.StepReached,
                null),
            refusal => refusal is SwitchRefusal.SideOffline
                ? InTransitBanner(entry, "the side stopped answering mid-hand-off")
                : new WslReconciliation("the hand-off of " + entry.Incoming.Value + " was unwound from " + entry.StepReached + ": " + refusal, null));
    }

    /// <summary>The one line of side state the page shows. A side that does not answer is offline, not an error.</summary>
    public async Task<WslSideState> ReadSideAsync(SideName side, CancellationToken cancellationToken)
    {
        if (_peers.For(side) is not Peer peer)
        {
            return new WslSideState(side, Online: false, null, "not configured");
        }

        Result<PeerDashboard, string> dashboard = await peer.Instance.ReadDashboardAsync(cancellationToken);
        return dashboard.Match(
            live => Incompatible(live) is string mismatch
                ? new WslSideState(side, Online: false, live.LiveAccount, "incompatible: " + mismatch)
                : new WslSideState(side, Online: true, live.LiveAccount, live.LiveAccount is null ? "online, holding nothing" : "online"),
            reason => new WslSideState(side, Online: false, null, "offline: " + reason));
    }

    /// <summary>Why this leader will not hand a pair to that follower, or null when it will.</summary>
    private static string? Incompatible(PeerDashboard remote) =>
        string.Equals(remote.Version, Hosting.AppComposition.Version, StringComparison.Ordinal)
            ? null
            : "the side reports version " + (remote.Version ?? "(none)") + " and this one is " + Hosting.AppComposition.Version;

    /// <summary>The page's <c>Start WSL side</c>: spawn the follower, which is the leader's child.</summary>
    public async Task<Result<Unit, string>> StartSideAsync(SideName side, CancellationToken cancellationToken)
    {
        if (_peers.For(side) is not Peer peer)
        {
            return Result<Unit, string>.Failure("no peer is configured for " + side.Value);
        }

        return peer.Host is null
            ? Result<Unit, string>.Failure("the " + side.Value + " side has no launch configured, so it cannot be started from here")
            : await peer.Host.StartAsync(cancellationToken);
    }

    /// <summary>L1's guards and L2's claim, both under the leader's mutation gate.</summary>
    private async Task<Result<WslSwitchJournalEntry, SwitchRefusal>> ClaimAsync(
        Peer peer,
        PeerDashboard remote,
        AccountEmail target,
        CancellationToken cancellationToken)
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
                return Refuse(SwitchRefusal.MutationInProgress);
            }

            if (_quota.InProgress)
            {
                return Refuse(SwitchRefusal.RefreshInProgress);
            }

            Result<WslSwitchJournalEntry, SwitchRefusal> planned = await PlanAsync(peer, remote, target, cancellationToken);
            if (planned.IsFailure)
            {
                return planned;
            }

            WslSwitchJournalEntry entry = planned.Value;

            // The record goes in before the pair comes out, for the reason
            // design 9.4 gives: the table only ever deletes a record, so every
            // crash point must leave a slot holding a pair AND a record, which
            // reconciliation heals, rather than a slot holding neither.
            await _slots.TakeAsync(entry.IncomingFolderPath, entry.IncomingFingerprint, entry.StartedAt, peer.Side, CancellationToken.None);
            await _pairs.ClaimToMailboxAsync(entry.IncomingFolderPath, peer.Side, CancellationToken.None);
            await JournalAsync(entry, CancellationToken.None);
            LogClaimed(peer.Side.Value, entry.Incoming.Value, entry.Outgoing?.Value ?? "none");
            return Result<WslSwitchJournalEntry, SwitchRefusal>.Success(entry);
        }
        finally
        {
            permit?.Dispose();
        }
    }

    /// <summary>
    /// L1. The Core planner is not reused: it decides a switch of <i>this</i>
    /// side's live directory, and every one of its live-pair comparisons would
    /// be against the wrong operating system. What is shared is the vocabulary
    /// — the same <see cref="SwitchRefusal"/> values mean the same things — so
    /// the page needs no second set of sentences.
    /// </summary>
    private async Task<Result<WslSwitchJournalEntry, SwitchRefusal>> PlanAsync(
        Peer peer,
        PeerDashboard remote,
        AccountEmail target,
        CancellationToken cancellationToken)
    {
        if (!_slots.Enabled)
        {
            // Without store.shared there are no holder records, so nothing can
            // say which side holds what and a hand-off would be a rename into a
            // mailbox nobody reconciles. The flag is the rollback for this lane too.
            return Refuse(SwitchRefusal.SideOffline);
        }

        if (remote.LiveAccount == target)
        {
            return Refuse(SwitchRefusal.AlreadyOnTarget);
        }

        if (await _journal.ReadOpenAsync(cancellationToken) is not null
            || await _windowsJournal.ReadOpenAsync(cancellationToken) is not null)
        {
            return Refuse(SwitchRefusal.LiveIdentityUnverified);
        }

        ManagedLoginPolicy policy = await _policyReader.ReadAsync(cancellationToken);
        if (policy.Unreadable)
        {
            return Refuse(SwitchRefusal.ManagedPolicyUnreadable);
        }

        if (policy.BlocksSwitching)
        {
            return Refuse(SwitchRefusal.SwitchingBlockedByManagedPolicy);
        }

        IReadOnlyList<ParkedProfile> profiles = await _profiles.ListAsync(cancellationToken);
        if (profiles.FirstOrDefault(profile => profile.Email == target) is not ParkedProfile incoming)
        {
            return Refuse(SwitchRefusal.TargetHasNoCredentials);
        }

        // The target's slot, read the one way the design allows. A slot the
        // Windows side holds reads HeldHere from here, which for a switch of
        // the other side is exactly "in use by the other side".
        SlotSnapshot? slot = await _slots.ReadAsync(
            target,
            incoming.FolderPath,
            incoming.HasCredentials,
            await WindowsHoldAsync(cancellationToken),
            cancellationToken);
        Result<Unit, SwitchRefusal> usable = slot?.State switch
        {
            SlotState.HeldHere => Refuse<Unit>(SwitchRefusal.HeldByOtherSide),
            SlotState.HeldElsewhere when remote.LiveAccount != target => Refuse<Unit>(SwitchRefusal.HeldByOtherSide),
            SlotState.InTransit => Refuse<Unit>(SwitchRefusal.SlotInTransit),
            _ => Result<Unit, SwitchRefusal>.Success(Unit.Value),
        };
        if (usable.IsFailure)
        {
            return Refuse(usable.Error);
        }

        if (!incoming.HasCredentials)
        {
            return Refuse(SwitchRefusal.TargetHasNoCredentials);
        }

        if (incoming.Account is not OAuthAccountBlock incomingAccount || incomingAccount.Email is null)
        {
            return Refuse(SwitchRefusal.TargetHasNoAccountBlock);
        }

        if (_recovery.HasRecoveryFor(incoming.FolderPath))
        {
            return Refuse(SwitchRefusal.TargetStrandedInRecovery);
        }

        if (_logins.IsRunningAgainst(incoming.FolderPath))
        {
            return Refuse(SwitchRefusal.LoginInProgress);
        }

        CredentialPair? pair = await _pairs.ReadParkedAsync(incoming.FolderPath, cancellationToken);
        if (pair is null)
        {
            return Refuse(SwitchRefusal.TargetHasNoCredentials);
        }

        if (pair.LoginExpiresAt is DateTimeOffset expiry && expiry <= _timeProvider.GetUtcNow())
        {
            return Refuse(SwitchRefusal.TargetLoginExpired);
        }

        // The outgoing account, which lives on the other side. Its slot here
        // must be empty: a slot still holding a pair for an account that is
        // live over there is the duplicate lineage the whole design refuses,
        // and parking on top of it at L4 would be the moment it became one.
        string? outgoingFolder = null;
        if (remote.LiveAccount is AccountEmail outgoing)
        {
            outgoingFolder = _profiles.FolderPathFor(outgoing);
            if (File.Exists(Path.Combine(outgoingFolder, FileSystemCredentialPairStore.FileName)))
            {
                return Refuse(SwitchRefusal.LiveIdentityUnverified);
            }

            if (_slots.HasTransitFile(outgoingFolder))
            {
                return Refuse(SwitchRefusal.SlotInTransit);
            }

            if (remote.LiveFingerprint is null)
            {
                // The side names an account but cannot name the pair under it,
                // so nothing could verify an export of it at L3b.
                return Refuse(SwitchRefusal.LiveIdentityUnverified);
            }
        }

        return Result<WslSwitchJournalEntry, SwitchRefusal>.Success(new WslSwitchJournalEntry(
            peer.Side,
            target,
            pair.Fingerprint,
            incoming.FolderPath,
            remote.LiveAccount,
            remote.LiveFingerprint,
            outgoingFolder,
            WslSwitchStep.Claimed,
            _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// L3a to L4, entered at whatever step the journal names. One method for
    /// the first run and for every resumption, so the crash table and the happy
    /// path cannot drift apart.
    /// </summary>
    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> HandOffAsync(Peer peer, WslSwitchJournalEntry entry, CancellationToken cancellationToken)
    {
        switch (entry.StepReached)
        {
            case WslSwitchStep.Claimed:
                return await ImportAsync(peer, entry, cancellationToken);
            case WslSwitchStep.ExportVerified:
                return await ResumeAfterVerificationAsync(peer, entry, cancellationToken);
            case WslSwitchStep.Imported:
                return await ParkAsync(peer, entry, cancellationToken);
            default:
                // Parked: the files are where they belong and only the journal
                // is left. Clearing it is the whole of design 9.3's last row.
                await _journal.ClearAsync(CancellationToken.None);
                return Result<WslSwitchOutcome, SwitchRefusal>.Success(Outcome(entry));
        }
    }

    /// <summary>L3a, then the gate of L3b, then L3c.</summary>
    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> ImportAsync(Peer peer, WslSwitchJournalEntry entry, CancellationToken cancellationToken)
    {
        string claimedPath = _pairs.ClaimedPathFor(entry.IncomingFolderPath, peer.Side);
        string exportPath = ExportPathFor(entry, peer.Side);
        ImportRequest request = new(
            entry.Incoming,
            peer.InPeerNamespace(claimedPath),
            entry.IncomingFingerprint,
            (await _profiles.ReadAccountAsync(entry.IncomingFolderPath, cancellationToken))?.Raw ?? [],
            peer.InPeerNamespace(exportPath));

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ImportTimeout);
        Result<ImportAnswer, string> answered;
        try
        {
            answered = await peer.Instance.ImportAsync(request, bounded.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            answered = Result<ImportAnswer, string>.Failure("the side did not answer within " + ImportTimeout.TotalSeconds + " s");
        }

        if (answered.IsFailure)
        {
            // The journal stays open at Claimed with nothing unclaimed: design
            // 9.3's Claimed row. The side may have staged and swapped already,
            // and only its own answer may decide.
            LogHandOffStalled(peer.Side.Value, entry.Incoming.Value, answered.Error);
            return Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.SideOffline);
        }

        ImportAnswer answer = answered.Value;
        if (answer.AlreadyImported && answer.Result is ImportResult done)
        {
            // F1 answered from its record: this import already ran, so there is
            // nothing left to gate and the park is all that is owed. The export
            // is still verified, by the fingerprint compare inside the promote.
            return await ParkAsync(peer, await ImportedAsync(entry, done, CancellationToken.None), CancellationToken.None);
        }

        // L3b. Skipped outright on Exported {none}: with no outgoing pair there
        // is no export, so there is nothing to verify and nothing to lose.
        if (answer.ExportedFingerprint is RefreshTokenFingerprint exported)
        {
            if (entry.OutgoingFolderPath is not string outgoingFolder)
            {
                // The side exported a pair for an account this hand-off never
                // planned to park. Nothing here can name a slot for it, so the
                // gate refuses rather than guessing one.
                LogGateRefused(peer.Side.Value, entry.Incoming.Value, "the side exported a pair but named no outgoing account at plan time");
                return await AbortAndUnclaimAsync(peer, entry, SwitchRefusal.ExportNotVerified, CancellationToken.None);
            }

            Result<RefreshTokenFingerprint, string> native = await _pairs.ReadExportedFingerprintAsync(outgoingFolder, peer.Side, CancellationToken.None);
            if (native.IsFailure || native.Value != exported)
            {
                LogGateRefused(
                    peer.Side.Value,
                    entry.Incoming.Value,
                    native.IsFailure ? native.Error : "the export reads " + native.Value.Sha256Hex[..12] + " natively but the side named " + exported.Sha256Hex[..12]);
                return await AbortAndUnclaimAsync(peer, entry, SwitchRefusal.ExportNotVerified, CancellationToken.None);
            }

            LogGatePassed(peer.Side.Value, exported.Sha256Hex[..12]);
        }

        await JournalAsync(entry with { StepReached = WslSwitchStep.ExportVerified }, CancellationToken.None);
        return await CommitAsync(peer, entry with { StepReached = WslSwitchStep.ExportVerified }, CancellationToken.None);
    }

    /// <summary>L3c: the commit the gate has earned, and the park that follows it.</summary>
    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> CommitAsync(Peer peer, WslSwitchJournalEntry entry, CancellationToken cancellationToken)
    {
        Result<ImportResult, string> committed = await peer.Instance.CommitImportAsync(entry.Incoming, cancellationToken);
        if (committed.IsFailure)
        {
            // Never an unclaim from here on a bare failure: the answer may have
            // been lost on the way back from a swap that did happen. The status
            // route, which reads the files rather than the journal, decides.
            LogCommitFailed(peer.Side.Value, entry.Incoming.Value, committed.Error);
            return await ResumeAfterVerificationAsync(peer, entry, cancellationToken);
        }

        return await ParkAsync(peer, await ImportedAsync(entry, committed.Value, CancellationToken.None), CancellationToken.None);
    }

    /// <summary>
    /// The <c>Imported</c> journal write, carrying what the commit's answer
    /// taught the leader: which pair left the other side and the account block
    /// that has to go into its <c>profile.json</c>. Written before the park so
    /// a crash in between leaves the park doable from the journal alone.
    /// </summary>
    private async Task<WslSwitchJournalEntry> ImportedAsync(WslSwitchJournalEntry entry, ImportResult result, CancellationToken cancellationToken)
    {
        WslSwitchJournalEntry imported = entry with
        {
            StepReached = WslSwitchStep.Imported,
            OutgoingFingerprint = result.OutgoingFingerprint ?? entry.OutgoingFingerprint,
            OutgoingAccount = result.OutgoingAccount ?? entry.OutgoingAccount,
        };
        await JournalAsync(imported, cancellationToken);
        return imported;
    }

    /// <summary>
    /// Design 9.3's three <c>ExportVerified</c> rows, collapsed into the one
    /// question that separates them: what does the follower say about <i>this
    /// account's</i> import, decided from its files?
    /// <para>
    /// A cleared follower journal is never read as "nothing happened". That is
    /// the leader-crashed-after-the-commit case, and unclaiming on it would
    /// take back a slot whose pair is already live on the other side and
    /// strand the outgoing account's export.
    /// </para>
    /// </summary>
    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> ResumeAfterVerificationAsync(Peer peer, WslSwitchJournalEntry entry, CancellationToken cancellationToken)
    {
        Result<ImportStatus, string> asked = await peer.Instance.ImportStatusAsync(entry.Incoming, cancellationToken);
        if (asked.IsFailure)
        {
            LogHandOffStalled(peer.Side.Value, entry.Incoming.Value, asked.Error);
            return Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.SideOffline);
        }

        ImportStatus status = asked.Value;
        if (status.Imported)
        {
            // The leader-crashed-after-the-commit case. The other side's files
            // say the swap ran, so the switch resolves forward: the journal
            // moves to Imported and L4 parks the export. It must not unclaim.
            WslSwitchJournalEntry imported = entry with { StepReached = WslSwitchStep.Imported };
            await JournalAsync(imported, CancellationToken.None);
            return await ParkAsync(peer, imported, CancellationToken.None);
        }

        if (status.JournalStep == ImportStep.Exported)
        {
            // The hold is still open on the other side and the export has
            // already passed the native gate, so it is not read again.
            return await CommitAsync(peer, entry, cancellationToken);
        }

        // A definite "not imported": no journal there and no record of this
        // account's import. Only now may the claim come back.
        LogNotImported(peer.Side.Value, entry.Incoming.Value, status.Detail);
        return await UnclaimAsync(peer, entry, SwitchRefusal.PeerDidNotImport, CancellationToken.None);
    }

    /// <summary>L4, under the gate: the export becomes the outgoing account's parked pair.</summary>
    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> ParkAsync(Peer peer, WslSwitchJournalEntry entry, CancellationToken cancellationToken)
    {
        using IDisposable permit = await _gate.AcquireAsync(_options.MutationGateTimeout, cancellationToken);

        // Nothing to park on Exported {none}: the other side held no pair, so
        // the hand-off moved one pair and not two.
        if (entry.Outgoing is AccountEmail outgoing && entry.OutgoingFolderPath is string folder && entry.OutgoingFingerprint is RefreshTokenFingerprint fingerprint)
        {
            await _profiles.EnsureFolderAsync(outgoing, cancellationToken);
            if (entry.OutgoingAccount is not null)
            {
                await _profiles.WriteProfileAsync(folder, OAuthAccountBlock.FromJson(entry.OutgoingAccount), cancellationToken);
            }

            Result<Unit, string> promoted = await _pairs.PromoteFromMailboxAsync(folder, peer.Side, fingerprint, cancellationToken);
            if (promoted.IsFailure)
            {
                // The export stays where it is. A pair stranded in the mailbox
                // is recoverable; one promoted without matching the fingerprint
                // the journal named would be a lineage nobody verified.
                LogParkRefused(outgoing.Value, promoted.Error);
                return Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.LiveIdentityUnverified);
            }

            // The slot holds its pair again, so its record goes: design 9.4 row 2.
            await _slots.ReleaseAsync(folder, cancellationToken);
        }

        await JournalAsync(entry with { StepReached = WslSwitchStep.Parked }, cancellationToken);
        await _journal.ClearAsync(cancellationToken);
        LogSwitched(peer.Side.Value, entry.Incoming.Value, entry.Outgoing?.Value ?? "none");
        return Result<WslSwitchOutcome, SwitchRefusal>.Success(Outcome(entry));
    }

    /// <summary>The gate's refusal: unwind the follower's import first, then take the claim back.</summary>
    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> AbortAndUnclaimAsync(
        Peer peer,
        WslSwitchJournalEntry entry,
        SwitchRefusal refusal,
        CancellationToken cancellationToken)
    {
        Result<Unit, string> aborted = await peer.Instance.AbortImportAsync(entry.Incoming, cancellationToken);
        if (aborted.IsFailure)
        {
            // The follower refuses an abort it has already swapped past, and
            // says so. Asking it again, by status, is the only safe reading:
            // unclaiming here would take back a slot whose pair is live there.
            LogAbortRefused(peer.Side.Value, entry.Incoming.Value, aborted.Error);
            WslSwitchJournalEntry verified = entry with { StepReached = WslSwitchStep.ExportVerified };
            await JournalAsync(verified, cancellationToken);
            return await ResumeAfterVerificationAsync(peer, verified, cancellationToken);
        }

        return await UnclaimAsync(peer, entry, refusal, cancellationToken);
    }

    /// <summary>L2 reversed: the pair goes back into its slot and the record goes with it.</summary>
    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> UnclaimAsync(
        Peer peer,
        WslSwitchJournalEntry entry,
        SwitchRefusal refusal,
        CancellationToken cancellationToken)
    {
        using IDisposable permit = await _gate.AcquireAsync(_options.MutationGateTimeout, cancellationToken);
        await _pairs.UnclaimFromMailboxAsync(entry.IncomingFolderPath, peer.Side, cancellationToken);
        await _slots.ReleaseAsync(entry.IncomingFolderPath, cancellationToken);
        await _journal.ClearAsync(cancellationToken);
        LogUnclaimed(peer.Side.Value, entry.Incoming.Value);
        return Result<WslSwitchOutcome, SwitchRefusal>.Failure(refusal);
    }

    private async Task<WindowsHold> WindowsHoldAsync(CancellationToken cancellationToken)
    {
        CredentialPair? live = await _pairs.ReadLiveAsync(cancellationToken);
        OAuthAccountBlock? account = await _stateFile.ReadAccountBlockAsync(cancellationToken);
        return new WindowsHold(account?.Email, live?.Fingerprint);
    }

    private string ExportPathFor(WslSwitchJournalEntry entry, SideName side) =>
        entry.OutgoingFolderPath is string folder
            ? _pairs.ExportPathFor(folder, side)
            // Nothing is ever written here: with no outgoing account the
            // follower's F4 exports nothing. The request still needs a path
            // under the mailbox, and the incoming account's own export name is
            // the one name that is certainly free, since its pair is in the
            // mailbox under the claim's name and not the export's.
            : _pairs.ExportPathFor(entry.IncomingFolderPath, side);

    /// <summary>
    /// One journal write with the crash hook on both sides of it. The
    /// before-journal timing is the one that matters: it leaves the step's disk
    /// effect done and the journal one step behind, which is the torn state
    /// every row of the crash table has to be decided by the files for.
    /// </summary>
    private async Task JournalAsync(WslSwitchJournalEntry entry, CancellationToken cancellationToken)
    {
        CrashInjection.KillIfConfigured(_options.FailAfterStep, entry.StepReached, beforeJournal: true);
        await _journal.WriteAsync(entry, cancellationToken);
        CrashInjection.KillIfConfigured(_options.FailAfterStep, entry.StepReached, beforeJournal: false);
    }

    private static WslReconciliation InTransitBanner(WslSwitchJournalEntry entry, string why) => new(
        "the hand-off of " + entry.Incoming.Value + " to " + entry.Side.Value + " is at " + entry.StepReached + " and " + why,
        entry.Incoming.Value + " is in transit to " + entry.Side.Value + " (" + why + "); it stays claimed until that side answers.");

    private WslSwitchOutcome Outcome(WslSwitchJournalEntry entry) =>
        new(entry.Side, entry.Incoming, entry.Outgoing, _timeProvider.GetUtcNow());

    private static Result<WslSwitchJournalEntry, SwitchRefusal> Refuse(SwitchRefusal refusal) =>
        Result<WslSwitchJournalEntry, SwitchRefusal>.Failure(refusal);

    private static Result<T, SwitchRefusal> Refuse<T>(SwitchRefusal refusal) =>
        Result<T, SwitchRefusal>.Failure(refusal);

    [LoggerMessage(Level = LogLevel.Information, Message = "claimed {Incoming} for {Side}; {Outgoing} is expected back")]
    private partial void LogClaimed(string side, string incoming, string outgoing);

    [LoggerMessage(Level = LogLevel.Information, Message = "the export gate passed: the exported pair reads {Fingerprint} natively on this volume ({Side})")]
    private partial void LogGatePassed(string side, string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the export gate refused the switch of {Incoming} to {Side}: {Reason}; nothing was swapped")]
    private partial void LogGateRefused(string side, string incoming, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "switched {Side} to {Incoming}; {Outgoing} parked back into the store")]
    private partial void LogSwitched(string side, string incoming, string outgoing);

    [LoggerMessage(Level = LogLevel.Information, Message = "the claim of {Incoming} for {Side} was taken back; the slot holds its pair again")]
    private partial void LogUnclaimed(string side, string incoming);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the hand-off of {Incoming} to {Side} is waiting: {Reason}; the claim stands until that side answers")]
    private partial void LogHandOffStalled(string side, string incoming, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the commit of {Incoming} to {Side} did not answer ({Reason}); asking what happened rather than unclaiming")]
    private partial void LogCommitFailed(string side, string incoming, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Side} reports {Incoming} not imported: {Detail}")]
    private partial void LogNotImported(string side, string incoming, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Side} refused the abort of {Incoming} ({Reason}); the swap may have run, so nothing is unclaimed")]
    private partial void LogAbortRefused(string side, string incoming, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the export for {Outgoing} could not be parked: {Reason}; it stays in the mailbox")]
    private partial void LogParkRefused(string outgoing, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the {Side} side is not answering: {Reason}")]
    private partial void LogSideUnreachable(string side, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the {Side} side is incompatible and was sent no import: {Reason}")]
    private partial void LogIncompatible(string side, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "switch of {Side} to {Target} refused: {Refusal}")]
    private partial void LogRefused(string side, string target, SwitchRefusal refusal);
}
