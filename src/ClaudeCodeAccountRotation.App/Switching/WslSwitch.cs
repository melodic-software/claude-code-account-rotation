using System.Globalization;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// What a completed hand-off moved: the account now live on that side, and the
/// one parked back. <c>QuarantinedAt</c> names the file instead when the pair
/// that came back was a superseded family the store may not hold, which is the
/// escape hatch's one sanctioned outcome; <c>ParkedAs</c> is then null, because
/// nothing was parked.
/// </summary>
internal sealed record WslSwitchOutcome(SideName Side, AccountEmail Now, AccountEmail? ParkedAs, DateTimeOffset At, string? QuarantinedAt = null);

/// <summary>
/// What the leader's own crash table found and did at start, or on a poll.
/// <see cref="Decided"/> is
/// false when the pass did not run at all because a hand-off held the gate, so
/// its null <see cref="Banner"/> means "no answer", not "nothing standing".
/// </summary>
internal sealed record WslReconciliation(string Outcome, string? Banner, bool Decided = true);

/// <summary>
/// One line of side state for the page: is it up, what does it hold, whether
/// this leader can start it, and the usage its own sessions last observed.
/// <para>
/// <see cref="Usage"/> is that side's rate-limit-guard tee, which is the only
/// source of figures for an account it holds: the leader reads no usage for a
/// pair it does not have (design 12). It is null when that side is offline or
/// has no snapshot to give.
/// </para>
/// <para>
/// <see cref="LoginExpiresAt"/> is when the login behind that side's live pair
/// runs out, and is the only source for the one number the operator plans the
/// month around: the 28-day window is fixed, a refresh does not move it, and an
/// account this side holds has no local file left to read it from. Null on the
/// same terms as the tee.
/// </para>
/// </summary>
internal sealed record WslSideState(
    SideName Side,
    bool Online,
    AccountEmail? LiveAccount,
    string Detail,
    bool CanStart = false,
    StatuslineSnapshot? Usage = null,
    DateTimeOffset? LoginExpiresAt = null);

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
    /// When an in-transit hand-off stops being a state that is still settling
    /// and starts being one the operator is asked to decide about. Design 9.4's
    /// last row. It changes what the banner says and nothing else: no path
    /// clears, retries differently, or gives up on a hand-off because of it.
    /// </summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromHours(24);

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

    /// <summary>
    /// L1 to L4, with the gate taken for L1-L2, released across the hand-off,
    /// and taken again for L4.
    /// <para>
    /// <paramref name="quarantineForeignFamily"/> is the operator's second
    /// click, and it overrides exactly one refusal: the
    /// <see cref="SwitchRefusal.ForeignFamily"/> this side raises when the
    /// account the other side is live on has a superseded family. It grants no
    /// other permission, and it is deliberately not written into the journal —
    /// L4 decides what to do with an export from the files, so a hand-off that
    /// was started before the escape hatch ran still quarantines what it must.
    /// </para>
    /// </summary>
    public async Task<Result<WslSwitchOutcome, SwitchRefusal>> SwitchToAsync(
        SideName side,
        AccountEmail target,
        bool quarantineForeignFamily,
        CancellationToken cancellationToken)
    {
        if (!await _handOff.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            LogRefused(side.Value, target.Value, SwitchRefusal.MutationInProgress);
            return Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.MutationInProgress);
        }

        try
        {
            return await SwitchUnderHandOffAsync(side, target, quarantineForeignFamily, cancellationToken);
        }
        finally
        {
            _handOff.Release();
        }
    }

    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> SwitchUnderHandOffAsync(
        SideName side,
        AccountEmail target,
        bool quarantineForeignFamily,
        CancellationToken cancellationToken)
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

        Result<WslSwitchJournalEntry, SwitchRefusal> claimed = await ClaimAsync(peer, dashboard.Value, target, quarantineForeignFamily, cancellationToken);
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
            // A hand-off holds the gate, so this pass has decided nothing. It
            // says so rather than answering "resolved": a caller that wrote a
            // null banner from here would clear, from a second tab or an
            // overlapping poll, the line a still-running pass had put up for a
            // claim that is still stranded.
            return new WslReconciliation("a hand-off is in flight; nothing to reconcile", null, Decided: false);
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
            return await ReconcileOrphansAsync(cancellationToken);
        }

        if (_peers.For(entry.Side) is not Peer peer)
        {
            return InTransitBanner(entry, "no peer is configured for " + entry.Side.Value);
        }

        // An offline side leaves both the claim and any export where they are.
        // The follower may have swapped, so an unclaim here would create a
        // second holder of the incoming lineage; the card says in transit and
        // the decision waits for the side to answer.
        //
        // A side that answers with a version this leader does not know is the
        // same case, not a lesser one: the switch path refuses to send it an
        // import at all, and resuming one into it would drive the very
        // protocol the refusal exists to protect through a process that may
        // reconcile by other rules. An upgrade with a hand-off open is exactly
        // when that happens.
        if (entry.StepReached is WslSwitchStep.Claimed or WslSwitchStep.ExportVerified)
        {
            Result<PeerDashboard, string> reachable = await peer.Instance.ReadDashboardAsync(cancellationToken);
            if (reachable.IsFailure)
            {
                return InTransitBanner(entry, "the side is not answering");
            }

            if (Incompatible(reachable.Value) is string mismatch)
            {
                LogIncompatible(entry.Side.Value, mismatch);
                return InTransitBanner(entry, "the side is incompatible (" + mismatch + ")");
            }
        }

        Result<WslSwitchOutcome, SwitchRefusal> resumed = await HandOffAsync(peer, entry, cancellationToken);
        if (resumed.IsSuccess)
        {
            return new WslReconciliation(
                "the hand-off of " + resumed.Value.Now.Value + " to " + resumed.Value.Side.Value + " was finished from " + entry.StepReached
                    + (resumed.Value.QuarantinedAt is string file ? "; a superseded family was quarantined at " + file : string.Empty),
                null);
        }

        // Whether this refusal unwound anything is the journal's answer, never
        // the refusal's. Three of them leave the journal open with a pair still
        // in a mailbox — an unreachable side, an export the gate could not
        // verify for an account that side had already swapped past, and a park
        // that could not run — and a reading that called any of those "unwound"
        // would clear the one line telling the operator a credential is
        // standing between two operating systems.
        WslSwitchJournalEntry? standing = await _journal.ReadOpenAsync(cancellationToken);
        return standing is null
            ? new WslReconciliation("the hand-off of " + entry.Incoming.Value + " was unwound from " + entry.StepReached + ": " + resumed.Error, null)
            : InTransitBanner(standing, Stalled(resumed.Error, standing));
    }

    /// <summary>Why a refusal left the hand-off standing, in the words the banner needs.</summary>
    private static string Stalled(SwitchRefusal refusal, WslSwitchJournalEntry entry) => refusal switch
    {
        SwitchRefusal.SideOffline => "the side stopped answering mid-hand-off",
        SwitchRefusal.ExportNotVerified =>
            "that side has already imported it and exported a pair for an account other than " + (entry.Outgoing?.Value ?? "none")
                + ", so nothing here can name a slot for it; the exported pair is in the mailbox and no code path will park it",
        SwitchRefusal.LiveIdentityUnverified => "the pair that came back could not be put anywhere; it is in the mailbox",
        _ => "the hand-off was refused with " + refusal,
    };

    /// <summary>
    /// Design 9.3's last leader row: <b>no journal, but a file under the
    /// mailbox</b>. It is reached when the process died between the claim's
    /// rename and its own journal write, and it is the one crash point where a
    /// pair sits in a mailbox with nothing at all pointing at it — so the rule
    /// is read off the file's own name, which is the slot it came from.
    /// <para>
    /// A <i>claim</i> is treated as the <c>Claimed</c> row: the side is asked,
    /// and only a definite "not imported" puts it back. A journal write is
    /// what precedes L3a, so a claim with no journal has almost certainly
    /// never been offered to anyone — but "almost certainly" is not what a
    /// credential is put back on, and the side answers for itself.
    /// </para>
    /// <para>
    /// An <i>export</i> is treated as the <c>Imported</c> row: the other side
    /// has already swapped, so this pair exists here and nowhere else, and L4
    /// parks it by its own fingerprint. There is no journal fingerprint left
    /// to compare it against, which is why the rename's refusal to overwrite
    /// an occupied slot is the check that matters here.
    /// </para>
    /// </summary>
    private async Task<WslReconciliation> ReconcileOrphansAsync(CancellationToken cancellationToken)
    {
        List<string> acted = [];
        List<string> standing = [];
        foreach (Peer peer in _peers.All)
        {
            string mailbox = FileSystemCredentialPairStore.MailboxPath(_options.ProfilesRoot, peer.Side);
            if (!Directory.Exists(mailbox))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(mailbox))
            {
                string name = Path.GetFileName(path);
                bool isExport = name.EndsWith(FileSystemCredentialPairStore.IncomingSuffix, StringComparison.Ordinal);
                string claimName = isExport ? name[..^FileSystemCredentialPairStore.IncomingSuffix.Length] : name;
                if (!claimName.EndsWith(FileSystemCredentialPairStore.FileName, StringComparison.Ordinal))
                {
                    continue;
                }

                string folderName = claimName[..^FileSystemCredentialPairStore.FileName.Length];
                if (folderName.Length == 0 || AccountEmail.Parse(folderName).IsFailure)
                {
                    continue;
                }

                string folder = Path.Combine(_options.ProfilesRoot, folderName);
                (string said, bool resolved) = isExport
                    ? await ParkOrphanExportAsync(peer, folder, folderName, cancellationToken)
                    : await UnclaimOrphanAsync(peer, folder, folderName, cancellationToken);
                acted.Add(said);
                if (!resolved)
                {
                    standing.Add(folderName);
                }
            }
        }

        if (acted.Count == 0)
        {
            return new WslReconciliation("no hand-off in flight", null);
        }

        // A mailbox file this pass could not resolve is a credential sitting
        // between two operating systems with no journal pointing at it, which is
        // the state the operator has least other evidence of. It gets a line for
        // the same reason the journaled one does.
        return new WslReconciliation(
            string.Join("; ", acted),
            standing.Count == 0
                ? null
                : "A pair for " + string.Join(", ", standing) + " is in a mailbox under " + _options.ProfilesRoot
                    + " with no hand-off recorded for it; nothing clears that by itself.");
    }

    private async Task<(string Said, bool Resolved)> ParkOrphanExportAsync(Peer peer, string folder, string folderName, CancellationToken cancellationToken)
    {
        Result<RefreshTokenFingerprint, string> exported = await _pairs.ReadExportedFingerprintAsync(folder, peer.Side, cancellationToken);
        if (exported.IsFailure)
        {
            LogParkRefused(folderName, exported.Error);
            return ("an export for " + folderName + " is in the mailbox and could not be read: " + exported.Error, false);
        }

        using IDisposable permit = await _gate.AcquireAsync(_options.MutationGateTimeout, cancellationToken);
        Result<Unit, string> promoted = await _pairs.PromoteFromMailboxAsync(folder, peer.Side, exported.Value, cancellationToken);
        if (promoted.IsFailure)
        {
            LogParkRefused(folderName, promoted.Error);
            return ("an export for " + folderName + " could not be parked: " + promoted.Error, false);
        }

        await _slots.ReleaseAsync(folder, cancellationToken);
        LogOrphanParked(folderName);
        return ("an export for " + folderName + " with no journal was parked back into its slot", true);
    }

    private async Task<(string Said, bool Resolved)> UnclaimOrphanAsync(Peer peer, string folder, string folderName, CancellationToken cancellationToken)
    {
        Result<AccountEmail, string> email = AccountEmail.Parse(folderName);
        Result<ImportStatus, string> asked = email.IsSuccess
            ? await peer.Instance.ImportStatusAsync(email.Value, cancellationToken)
            : Result<ImportStatus, string>.Failure("the mailbox file does not name an account");
        if (asked.IsFailure || asked.Value.Imported)
        {
            return ("a claim of " + folderName + " with no journal was left alone: "
                + (asked.IsFailure ? asked.Error : "that side says it is imported, so its export is what settles this"), false);
        }

        using IDisposable permit = await _gate.AcquireAsync(_options.MutationGateTimeout, cancellationToken);
        await _pairs.UnclaimFromMailboxAsync(folder, peer.Side, cancellationToken);
        await _slots.ReleaseAsync(folder, cancellationToken);
        LogOrphanUnclaimed(folderName);
        return ("a claim of " + folderName + " with no journal was put back: " + asked.Value.Detail, true);
    }

    /// <summary>
    /// The operator's <c>Cancel</c> on a hand-off that is standing: takes the
    /// claim back so the account is usable again.
    /// <para>
    /// It is never blind, and this is the whole of its safety. A claim is taken
    /// back only after that side has answered a definite "not imported" about
    /// this very account, decided from its files rather than from its journal
    /// being clear. Every other answer refuses and changes nothing: a side that
    /// does not answer at all, a side that says it has imported the pair, a side
    /// still holding the two-call import open, and a hand-off already past the
    /// commit, which finishes rather than cancels. A cancel granted on silence
    /// would take back a slot whose pair is live on the other side and strand
    /// the outgoing account's export.
    /// </para>
    /// </summary>
    public async Task<Result<string, string>> CancelAsync(CancellationToken cancellationToken)
    {
        if (!await _handOff.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return Result<string, string>.Failure("a hand-off is running right now; try again in a moment");
        }

        try
        {
            if (await _journal.ReadOpenAsync(cancellationToken) is not WslSwitchJournalEntry entry)
            {
                return Result<string, string>.Failure("no hand-off is in transit, so there is nothing to cancel");
            }

            if (_peers.For(entry.Side) is not Peer peer)
            {
                return Result<string, string>.Failure(
                    "no peer is configured for " + entry.Side.Value + ", so it cannot be asked whether it imported " + entry.Incoming.Value);
            }

            if (entry.StepReached is not (WslSwitchStep.Claimed or WslSwitchStep.ExportVerified))
            {
                return Result<string, string>.Failure(
                    "the " + entry.Side.Value + " side has already imported " + entry.Incoming.Value + "; the hand-off finishes rather than cancels");
            }

            Result<ImportStatus, string> asked = await peer.Instance.ImportStatusAsync(entry.Incoming, cancellationToken);
            if (asked.IsFailure)
            {
                LogCancelRefused(entry.Side.Value, entry.Incoming.Value, asked.Error);
                return Result<string, string>.Failure(
                    "the " + entry.Side.Value + " side is not answering (" + asked.Error
                        + "), so it has not said whether it imported " + entry.Incoming.Value + "; the claim stands");
            }

            if (asked.Value.Imported)
            {
                return Result<string, string>.Failure(
                    "the " + entry.Side.Value + " side says " + entry.Incoming.Value + " is imported there; the hand-off finishes rather than cancels");
            }

            if (asked.Value.JournalStep == ImportStep.Exported)
            {
                return Result<string, string>.Failure(
                    "the " + entry.Side.Value + " side still has the import of " + entry.Incoming.Value + " open; it has not answered whether it imported the pair");
            }

            // A definite "not imported", which is the only thing an unclaim is
            // ever done on. That side holds no import for this account, so
            // there is nothing to abort first.
            _ = await UnclaimAsync(peer, entry, SwitchRefusal.PeerDidNotImport, CancellationToken.None);
            LogCancelled(entry.Side.Value, entry.Incoming.Value, asked.Value.Detail);
            return Result<string, string>.Success(
                "the hand-off of " + entry.Incoming.Value + " to " + entry.Side.Value + " was cancelled and its pair is back in its slot");
        }
        finally
        {
            _handOff.Release();
        }
    }

    /// <summary>The one line of side state the page shows. A side that does not answer is offline, not an error.</summary>
    public async Task<WslSideState> ReadSideAsync(SideName side, CancellationToken cancellationToken) =>
        _peers.For(side) is Peer peer
            ? await ReadSideAsync(peer, cancellationToken)
            : new WslSideState(side, Online: false, null, "not configured");

    /// <summary>
    /// Every configured side, in one pass: what the page's side lines and its
    /// per-card chips are both built from. Empty when <c>peers[]</c> is, which
    /// is the lane's rollback.
    /// </summary>
    public async Task<IReadOnlyList<WslSideState>> ReadSidesAsync(CancellationToken cancellationToken)
    {
        List<WslSideState> sides = [];
        foreach (Peer peer in _peers.All)
        {
            sides.Add(await ReadSideAsync(peer, cancellationToken));
        }

        return sides;
    }

    private static async Task<WslSideState> ReadSideAsync(Peer peer, CancellationToken cancellationToken)
    {
        bool canStart = peer.Host is not null;
        Result<PeerDashboard, string> dashboard = await peer.Instance.ReadDashboardAsync(cancellationToken);
        return dashboard.Match(
            live => Incompatible(live) is string mismatch
                ? new WslSideState(peer.Side, Online: false, live.LiveAccount, "incompatible: " + mismatch, canStart)
                : new WslSideState(
                    peer.Side,
                    Online: true,
                    live.LiveAccount,
                    live.LiveAccount is null ? "online, holding nothing" : "online",
                    canStart,
                    live.Tee,
                    live.LoginExpiresAt),
            reason => new WslSideState(peer.Side, Online: false, null, "offline: " + reason, canStart));
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
        bool quarantineForeignFamily,
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

            Result<WslSwitchJournalEntry, SwitchRefusal> planned = await PlanAsync(peer, remote, target, quarantineForeignFamily, cancellationToken);
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
        bool quarantineForeignFamily,
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

        // One reading of what this side holds, shared by every slot verdict in
        // this plan: the target's, and the outgoing account's below.
        WindowsHold hold = await WindowsHoldAsync(cancellationToken);

        // The target's slot, read the one way the design allows. A slot the
        // Windows side holds reads HeldHere from here, which for a switch of
        // the other side is exactly "in use by the other side".
        SlotSnapshot? slot = await _slots.ReadAsync(
            target,
            incoming.FolderPath,
            incoming.HasCredentials,
            hold,
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
            bool outgoingSlotHoldsPair = File.Exists(Path.Combine(outgoingFolder, FileSystemCredentialPairStore.FileName));

            // The foreign family. The pair that side is live on is not the one
            // this store parked, because the escape hatch logged this account in
            // again here while that side was unreachable; parking it back would
            // put two families of one account in the store. Asked before the
            // occupied-slot refusal below, because it is the explained case and
            // the operator needs its sentence rather than "unverified".
            bool foreign = await ForeignFamilyAsync(outgoing, outgoingFolder, outgoingSlotHoldsPair, hold, peer.Side, cancellationToken);
            if (foreign && !quarantineForeignFamily)
            {
                return Refuse(SwitchRefusal.ForeignFamily);
            }

            // An occupied slot with nothing explaining it is the duplicate the
            // whole design refuses, and no click overrides it.
            if (outgoingSlotHoldsPair && !foreign)
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
            _timeProvider.GetUtcNow(),
            remote.LiveAccountBlock));
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
                return await ResumeAfterVerificationAsync(peer, entry, mayCommit: true, cancellationToken);
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

        // The identity the gate cannot see. The export is written to the path
        // this leader named, so its name is always the account L1 planned; the
        // bytes are whatever was live over there at F2. A refresh or a switch
        // on that side between the two makes those different accounts, and the
        // fingerprint gate would pass every check and park one account's pair
        // into another account's slot under that account's block. Only the
        // answer's own `Outgoing` says which it really is.
        AccountEmail? answered_outgoing = answer.AlreadyImported ? answer.Result?.Outgoing : answer.Outgoing;
        if (answered_outgoing != entry.Outgoing)
        {
            LogOutgoingChanged(peer.Side.Value, entry.Outgoing?.Value ?? "none", answered_outgoing?.Value ?? "none");
            return answer.AlreadyImported
                // The swap has already run, so there is nothing to abort and
                // nothing safe to park. The journal stays open and the card
                // says in transit: a pair in a mailbox is recoverable, and one
                // in the wrong slot is a second family waiting to happen.
                ? Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.ExportNotVerified)
                : await AbortAndUnclaimAsync(peer, entry, SwitchRefusal.ExportNotVerified, CancellationToken.None);
        }

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

            CrashInjection.CorruptIfConfigured(_options.CorruptExportBeforeGate, _pairs.ExportPathFor(outgoingFolder, peer.Side));
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

        // The fingerprint that goes in the journal is the one the gate just
        // verified, not the one L1 read off the dashboard. A rotation between
        // those two moments makes them different, and this value is what L4
        // promotes against — including on the resume after a crash between the
        // commit and the Imported write, where it is the only copy left of
        // what the export is supposed to be.
        WslSwitchJournalEntry verified = entry with
        {
            StepReached = WslSwitchStep.ExportVerified,
            OutgoingFingerprint = answer.ExportedFingerprint ?? entry.OutgoingFingerprint,
        };
        await JournalAsync(verified, CancellationToken.None);
        return await CommitAsync(peer, verified, CancellationToken.None);
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
            // Not a second commit from here, whatever the status says. A side
            // that keeps answering "still at Exported" to a commit that keeps
            // failing would otherwise bounce between these two methods until
            // the stack ran out, and a leader that dies mid-hand-off is the
            // one outcome this whole class exists to avoid. One attempt per
            // pass; the retry is the next reconciliation poll, which is the
            // backoff design 11 asks for rather than a spin.
            return await ResumeAfterVerificationAsync(peer, entry, mayCommit: false, cancellationToken);
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
    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> ResumeAfterVerificationAsync(Peer peer, WslSwitchJournalEntry entry, bool mayCommit, CancellationToken cancellationToken)
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
            return mayCommit
                ? await CommitAsync(peer, entry, cancellationToken)
                // Already tried once this pass. The journal stays at
                // ExportVerified and the card says in transit; the next poll
                // tries again, and nothing is unclaimed against a side that
                // is still holding the import open.
                : Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.SideOffline);
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

            // Decided from the files, never from what the request asked for: a
            // hand-off claimed before the escape hatch ran, and resumed after
            // it, carries no flag and must still quarantine rather than fail to
            // park for ever. The superseded record is the consent, and it is on
            // disk.
            bool slotHoldsPair = File.Exists(Path.Combine(folder, FileSystemCredentialPairStore.FileName));

            // The quarantine, replayed. A crash between its rename and the
            // journal write leaves the export in quarantine and nothing else
            // done, and neither branch below could finish from there: the
            // quarantine would find no export to move and the park no export to
            // promote, so the journal would stay open and refuse every switch
            // after it. Asked before either, and only when the mailbox is
            // empty, so a hand-off whose export is still there cannot mistake
            // an older quarantine of the same lineage for its own.
            string quarantined = QuarantinePathFor(folder, fingerprint);
            if (!File.Exists(_pairs.ExportPathFor(folder, peer.Side)) && File.Exists(quarantined))
            {
                await _slots.ClearSupersededAsync(folder, cancellationToken);
                await JournalAsync(entry with { StepReached = WslSwitchStep.Parked }, cancellationToken);
                await _journal.ClearAsync(cancellationToken);
                LogQuarantineAlreadyDone(outgoing.Value, quarantined);
                return Result<WslSwitchOutcome, SwitchRefusal>.Success(Outcome(entry) with { ParkedAs = null, QuarantinedAt = quarantined });
            }

            if (await ForeignFamilyAsync(outgoing, folder, slotHoldsPair, await WindowsHoldAsync(cancellationToken), peer.Side, cancellationToken))
            {
                return await QuarantineForeignFamilyAsync(peer, entry, outgoing, folder, quarantined, cancellationToken);
            }

            if (entry.OutgoingAccount is not null)
            {
                await _profiles.WriteProfileAsync(folder, OAuthAccountBlock.FromJson(entry.OutgoingAccount), cancellationToken);
            }

            // "L4 by fingerprint" means decided from the files, and the files
            // may already say done: a crash between the rename and the journal
            // write leaves the slot holding exactly this pair and no export to
            // promote. Re-running the rename there would fail for want of a
            // source and leave the journal open for good, which turns one
            // crash into a leader that refuses every switch afterwards.
            if (await _pairs.ReadParkedAsync(folder, cancellationToken) is CredentialPair parked && parked.Fingerprint == fingerprint)
            {
                LogParkAlreadyDone(outgoing.Value);
            }
            else
            {
                Result<Unit, string> promoted = await _pairs.PromoteFromMailboxAsync(folder, peer.Side, fingerprint, cancellationToken);
                if (promoted.IsFailure)
                {
                    // The export stays where it is. A pair stranded in the mailbox
                    // is recoverable; one promoted without matching the fingerprint
                    // the journal named would be a lineage nobody verified.
                    LogParkRefused(outgoing.Value, promoted.Error);
                    return Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.LiveIdentityUnverified);
                }
            }

            // The slot holds its pair again, so its record goes: design 9.4 row 2.
            await _slots.ReleaseAsync(folder, cancellationToken);
        }

        await JournalAsync(entry with { StepReached = WslSwitchStep.Parked }, cancellationToken);
        await _journal.ClearAsync(cancellationToken);
        LogSwitched(peer.Side.Value, entry.Incoming.Value, entry.Outgoing?.Value ?? "none");
        return Result<WslSwitchOutcome, SwitchRefusal>.Success(Outcome(entry));
    }

    /// <summary>
    /// L4 for a family the store may not take back: the export is moved out of
    /// the mailbox into <c>quarantine/superseded/</c>, where it is neither
    /// promoted nor deleted, and the superseded record that sanctioned it goes.
    /// The switch itself completes — the incoming pair is live on that side and
    /// nothing is served by refusing it afterwards — and the banner names the
    /// file the operator has to decide about.
    /// <para>
    /// Three things this deliberately does not do. It does not release the
    /// slot's holder record, which may be this side's own statement that it
    /// holds the fresh family live. It does not write the outgoing account's
    /// block over <c>profile.json</c>, which the fresh login wrote and which
    /// describes the family that is actually in the slot. And it does not leave
    /// the export in the mailbox, which would leave the account reading
    /// <c>in-transit</c> for ever.
    /// </para>
    /// </summary>
    private async Task<Result<WslSwitchOutcome, SwitchRefusal>> QuarantineForeignFamilyAsync(
        Peer peer,
        WslSwitchJournalEntry entry,
        AccountEmail outgoing,
        string folder,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Result<string, string> quarantined = await _pairs.MoveExportToQuarantineAsync(
            folder,
            peer.Side,
            Path.GetDirectoryName(destinationPath)!,
            cancellationToken);
        if (quarantined.IsFailure)
        {
            // The export stays in the mailbox, which is recoverable, and the
            // journal stays open so the next poll tries again.
            LogParkRefused(outgoing.Value, quarantined.Error);
            return Result<WslSwitchOutcome, SwitchRefusal>.Failure(SwitchRefusal.LiveIdentityUnverified);
        }

        await _slots.ClearSupersededAsync(folder, cancellationToken);
        await JournalAsync(entry with { StepReached = WslSwitchStep.Parked }, cancellationToken);
        await _journal.ClearAsync(cancellationToken);
        LogFamilyQuarantined(peer.Side.Value, outgoing.Value, quarantined.Value);
        return Result<WslSwitchOutcome, SwitchRefusal>.Success(Outcome(entry) with { ParkedAs = null, QuarantinedAt = quarantined.Value });
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
            return await ResumeAfterVerificationAsync(peer, verified, mayCommit: true, cancellationToken);
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

    /// <summary>
    /// Where a superseded family of this account ends up, named after the slot
    /// and the lineage rather than the moment. Deterministic on purpose: it is
    /// what lets a crash between the move and the journal write be seen
    /// on the next pass instead of leaving the hand-off open for ever, and it
    /// says which family it is rather than only when it arrived.
    /// <para>
    /// Two different families of one account get two directories. The same
    /// family arriving twice gets one, and the second move is refused by the
    /// store's own rename — which is right: an export and a quarantined file
    /// holding one refresh token is the duplicate nothing here may create.
    /// </para>
    /// </summary>
    private string QuarantinePathFor(string folder, RefreshTokenFingerprint fingerprint) => Path.Combine(
        _options.SupersededQuarantineDirectory,
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) + "-" + fingerprint.Sha256Hex[..12],
        FileSystemCredentialPairStore.FileName);

    /// <summary>
    /// Whether <paramref name="side"/> holds a second token family for this
    /// account: the slot carries a superseded record naming that side, and the
    /// files still bear it out. The single derivation, used by L1 to refuse and
    /// by L4 to decide where the export goes, so the refusal and the quarantine
    /// can never disagree about what is foreign.
    /// </summary>
    private async Task<bool> ForeignFamilyAsync(
        AccountEmail account,
        string folderPath,
        bool slotHoldsPair,
        WindowsHold hold,
        SideName side,
        CancellationToken cancellationToken) =>
        await _slots.ReadSupersededAsync(account, folderPath, slotHoldsPair, hold, cancellationToken)
            is { Stale: false } superseded && superseded.Record.Side == side;

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

    /// <summary>
    /// The line the page carries for a hand-off nothing could finish, and
    /// design 9.4's last row: past <see cref="StuckAfter"/> it says how long,
    /// because a state that has stood for a day is one the operator is being
    /// asked to decide about rather than one that is still settling. Nothing
    /// clears it on a timer; a later poll that resolves the hand-off writes
    /// null over it, and until then it stands.
    /// </summary>
    private WslReconciliation InTransitBanner(WslSwitchJournalEntry entry, string why)
    {
        TimeSpan age = _timeProvider.GetUtcNow() - entry.StartedAt;
        string stuck = age < StuckAfter
            ? string.Empty
            : " It has been in transit for " + ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture)
                + " hours: nothing clears an in-transit state by itself, so it stands until that side answers or you cancel it.";
        return new WslReconciliation(
            "the hand-off of " + entry.Incoming.Value + " to " + entry.Side.Value + " is at " + entry.StepReached + " and " + why,
            entry.Incoming.Value + " is in transit to " + entry.Side.Value + " (" + why + "); it stays claimed until that side answers." + stuck);
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "the slot for {Outgoing} already holds the pair this hand-off was parking; only the journal was left to finish")]
    private partial void LogParkAlreadyDone(string outgoing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the export for {Outgoing} could not be parked: {Reason}; it stays in the mailbox")]
    private partial void LogParkRefused(string outgoing, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the {Side} side is not answering: {Reason}")]
    private partial void LogSideUnreachable(string side, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the {Side} side is incompatible and was sent no import: {Reason}")]
    private partial void LogIncompatible(string side, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the {Side} side exported {Answered} where this hand-off planned to park {Planned}; the switch is refused rather than parking one account's pair in another's slot")]
    private partial void LogOutgoingChanged(string side, string planned, string answered);

    [LoggerMessage(Level = LogLevel.Information, Message = "an export for {Account} was in the mailbox with no journal; it is parked back in its slot")]
    private partial void LogOrphanParked(string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "a claim of {Account} was in the mailbox with no journal and that side says it never imported it; it is back in its slot")]
    private partial void LogOrphanUnclaimed(string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "switch of {Side} to {Target} refused: {Refusal}")]
    private partial void LogRefused(string side, string target, SwitchRefusal refusal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "a superseded family of {Outgoing} came back from {Side} and was quarantined at {Destination}; it is never promoted and never deleted")]
    private partial void LogFamilyQuarantined(string side, string outgoing, string destination);

    [LoggerMessage(Level = LogLevel.Information, Message = "the superseded family of {Outgoing} is already quarantined at {Destination}; only the journal was left to finish")]
    private partial void LogQuarantineAlreadyDone(string outgoing, string destination);

    [LoggerMessage(Level = LogLevel.Information, Message = "the hand-off of {Incoming} to {Side} was cancelled by the operator: {Detail}")]
    private partial void LogCancelled(string side, string incoming, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "cancel refused for {Incoming}: the {Side} side did not answer ({Reason}); the claim stands")]
    private partial void LogCancelRefused(string side, string incoming, string reason);
}
