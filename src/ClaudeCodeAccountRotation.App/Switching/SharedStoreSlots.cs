using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// What the Windows side holds a store slot to be, and the only place that
/// writes, drops, or reads a <c>holder.json</c>.
/// <para>
/// Everything here is off unless <c>store.shared</c> is set, which is what
/// makes the flag the rollback: with it false <see cref="ReadAsync"/> answers
/// null, no record is ever written, and the tool behaves exactly as it did
/// before this file existed.
/// </para>
/// </summary>
internal sealed partial class SharedStoreSlots
{
    private readonly string _profilesRoot;
    private readonly CredentialMutationGate _gate;
    private readonly ILoginSessionRunner _logins;
    private readonly ILogger<SharedStoreSlots> _logger;

    public SharedStoreSlots(
        string profilesRoot,
        bool enabled,
        CredentialMutationGate gate,
        ILoginSessionRunner logins,
        ILogger<SharedStoreSlots> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesRoot);
        _profilesRoot = Path.GetFullPath(profilesRoot);
        Enabled = enabled;
        _gate = gate;
        _logins = logins;
        _logger = logger;
    }

    public bool Enabled { get; }

    /// <summary>
    /// This slot's state and whether its record is one reconciliation should
    /// drop, or null when the shared store is off. Reads files and writes
    /// none: <see cref="DropStaleRecordAsync"/> is the side effect, so a caller
    /// that only wants the verdict never takes the gate.
    /// <para>
    /// Two verdicts the design's table leaves to the caller are applied here,
    /// on top of <see cref="SlotStateRule.Resolve"/> rather than instead of it.
    /// Its <c>HeldHere</c> row wants the record's fingerprint to be this side's
    /// live pair or its rotation, which <c>Resolve</c> carries no fingerprint
    /// to check; a record that fails it is stale, and the slot reads as one
    /// nothing has been parked in. And a slot whose account is live holds
    /// <c>HeldHere</c> whether or not a record says so, because the pair in the
    /// live directory is the possession the whole design treats as the truth,
    /// which is what keeps a store that predates the flag from reading as
    /// never logged in until its next switch.
    /// </para>
    /// </summary>
    public async Task<SlotSnapshot?> ReadAsync(
        AccountEmail account,
        string folderPath,
        bool slotHoldsPair,
        WindowsHold live,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        HolderRecord? record = await HolderRecordFile.ReadAsync(folderPath, cancellationToken);
        SlotState state = SlotStateRule.Resolve(slotHoldsPair, record, SideName.Windows, HasTransitFile(folderPath));
        bool windowsHoldsIt = live.Account == account
            || (record is not null && live.Fingerprint is RefreshTokenFingerprint fingerprint && record.Fingerprint == fingerprint);

        return state switch
        {
            SlotState.Parked when record is not null => new SlotSnapshot(SlotState.Parked, StaleRecord: true, record),
            SlotState.HeldHere when !windowsHoldsIt => new SlotSnapshot(SlotState.NeverLoggedIn, StaleRecord: true, record),
            SlotState.NeverLoggedIn when windowsHoldsIt => new SlotSnapshot(SlotState.HeldHere, StaleRecord: false, record),
            _ => new SlotSnapshot(state, StaleRecord: false, record),
        };
    }

    /// <summary>
    /// Drops the record <paramref name="observed"/> was judged stale on, under
    /// the mutation gate with a zero wait. A busy gate leaves the record for
    /// the next read.
    /// <para>
    /// The whole verdict is taken again under the gate, from the files as they
    /// are then, and the record must still be the same one: a switch that
    /// finished between the read and this acquisition has written a record of
    /// its own, and deleting that would throw away the fresh statement of who
    /// holds the pair. Only a slot that is still stale, and stale about the
    /// same record, loses it.
    /// </para>
    /// </summary>
    public async Task DropStaleRecordAsync(
        AccountEmail account,
        string folderPath,
        SlotSnapshot observed,
        WindowsHold live,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (!Enabled)
        {
            return;
        }

        IDisposable? permit = null;
        try
        {
            try
            {
                permit = await _gate.AcquireAsync(TimeSpan.Zero, cancellationToken);
            }
            catch (TimeoutException)
            {
                return;
            }

            bool holdsPair = File.Exists(Path.Combine(folderPath, FileSystemCredentialPairStore.FileName));
            if (await ReadAsync(account, folderPath, holdsPair, live, cancellationToken) is not { StaleRecord: true } current
                || current.Record != observed.Record)
            {
                return;
            }

            await HolderRecordFile.DeleteAsync(folderPath, cancellationToken);
            if (holdsPair)
            {
                LogRecordDroppedForPair(account.Value);
            }
            else
            {
                LogRecordDroppedForFingerprint(account.Value);
            }
        }
        finally
        {
            permit?.Dispose();
        }
    }

    /// <summary>
    /// Records that a side has taken this slot's pair. Called by the switch,
    /// under the gate it already holds, so it takes none of its own. The side
    /// is a parameter because the leader writes this record for both of them:
    /// its own on a Windows switch, and the other side's on the claim that
    /// starts a hand-off.
    /// </summary>
    public Task TakeAsync(string folderPath, RefreshTokenFingerprint fingerprint, DateTimeOffset since, CancellationToken cancellationToken) =>
        TakeAsync(folderPath, fingerprint, since, SideName.Windows, cancellationToken);

    /// <inheritdoc cref="TakeAsync(string, RefreshTokenFingerprint, DateTimeOffset, CancellationToken)"/>
    public Task TakeAsync(string folderPath, RefreshTokenFingerprint fingerprint, DateTimeOffset since, SideName side, CancellationToken cancellationToken) =>
        Enabled
            ? HolderRecordFile.WriteAsync(folderPath, new HolderRecord(side, fingerprint, since), cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// The second token family another side still holds for this account, and
    /// whether the files have since contradicted it. Null when the store is not
    /// shared or no control ever wrote one.
    /// <para>
    /// The record is read the way <c>holder.json</c> is: the files decide. It
    /// claims that a family was left on another side when this slot was logged
    /// in again, so it is only true while this side has something to show for
    /// that login — a pair in the slot, or the account live here. A record with
    /// neither is one whose login never finished, and believing it would refuse
    /// a legitimate hand-off and then quarantine the account's only family: the
    /// family the other side holds is still the store's until a replacement for
    /// it exists here.
    /// </para>
    /// <para>
    /// The one moment neither has happened yet and the record is still true is
    /// while the login it was written for is running, which is why the record
    /// goes in before that login starts and why a login in flight counts as
    /// evidence for it.
    /// </para>
    /// </summary>
    public async Task<SupersededFamily?> ReadSupersededAsync(
        AccountEmail account,
        string folderPath,
        bool slotHoldsPair,
        WindowsHold live,
        CancellationToken cancellationToken)
    {
        if (!Enabled || await SupersededFamilyFile.ReadAsync(folderPath, cancellationToken) is not HolderRecord record)
        {
            return null;
        }

        return new SupersededFamily(
            record,
            Stale: !slotHoldsPair && live.Account != account && !_logins.IsRunningAgainst(folderPath));
    }

    /// <summary>
    /// Records that a family of this account was left on <paramref name="record"/>'s
    /// side when the slot was logged in again. Written before the login runs, so
    /// a login that dies half way still leaves the fact behind.
    /// </summary>
    public Task SupersedeAsync(string folderPath, HolderRecord record, CancellationToken cancellationToken) =>
        Enabled ? SupersededFamilyFile.WriteAsync(folderPath, record, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// The superseded family is accounted for: quarantined, or contradicted by
    /// the files. Called under the gate by the quarantine, and with a zero wait
    /// by the dashboard's reconciliation.
    /// </summary>
    public Task ClearSupersededAsync(string folderPath, CancellationToken cancellationToken) =>
        Enabled ? SupersededFamilyFile.DeleteAsync(folderPath, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Drops a superseded record the files contradict, under the mutation gate
    /// with a zero wait and only while the verdict still stands. The same shape
    /// as <see cref="DropStaleRecordAsync"/>, and for the same reason: a switch
    /// that finished between the read and this acquisition has changed what the
    /// files say.
    /// </summary>
    public async Task DropStaleSupersededAsync(
        AccountEmail account,
        string folderPath,
        SupersededFamily observed,
        WindowsHold live,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (!Enabled)
        {
            return;
        }

        IDisposable? permit = null;
        try
        {
            try
            {
                permit = await _gate.AcquireAsync(TimeSpan.Zero, cancellationToken);
            }
            catch (TimeoutException)
            {
                return;
            }

            bool holdsPair = File.Exists(Path.Combine(folderPath, FileSystemCredentialPairStore.FileName));
            if (await ReadSupersededAsync(account, folderPath, holdsPair, live, cancellationToken) is not { Stale: true } current
                || current.Record != observed.Record)
            {
                return;
            }

            await SupersededFamilyFile.DeleteAsync(folderPath, cancellationToken);
            LogSupersededDropped(account.Value);
        }
        finally
        {
            permit?.Dispose();
        }
    }

    /// <summary>Records that the slot holds its pair again: the record goes.</summary>
    public Task ReleaseAsync(string folderPath, CancellationToken cancellationToken) =>
        Enabled ? HolderRecordFile.DeleteAsync(folderPath, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// True while a file for this slot sits in any side's mailbox, whether the
    /// leader claimed it or the follower exported one back. Both names begin
    /// with the claim's, so one prefix covers the pair of them.
    /// </summary>
    public bool HasTransitFile(string folderPath)
    {
        string transitRoot = Path.Combine(_profilesRoot, FileSystemCredentialPairStore.TransitDirectoryName);
        if (!Enabled || !Directory.Exists(transitRoot))
        {
            return false;
        }

        string claimed = FileSystemCredentialPairStore.ClaimedFileName(Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath)));
        // One level of mailboxes under the transit root, one file per account in
        // each. Both the claim and the export answer to the claim's own name as a
        // prefix, so a search pattern covers the two without naming the suffix.
        return Directory.EnumerateFiles(transitRoot, claimed + "*", SearchOption.AllDirectories).Any();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "holder record dropped: slot holds a pair ({Account})")]
    private partial void LogRecordDroppedForPair(string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "holder record dropped: this side does not hold that pair ({Account})")]
    private partial void LogRecordDroppedForFingerprint(string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "superseded record dropped: this side holds no family for that account ({Account})")]
    private partial void LogSupersededDropped(string account);
}

/// <summary>
/// A second token family another side holds for one account, as this side reads
/// it. <paramref name="Stale"/> is the files disagreeing with the record, which
/// the next dashboard read drops; <paramref name="Record"/> names the side that
/// holds the family, the fingerprint it had when the slot was superseded, and
/// when that happened.
/// </summary>
internal sealed record SupersededFamily(HolderRecord Record, bool Stale);

/// <summary>
/// What the Windows side's live directory holds right now: the account its
/// state file names and the fingerprint of the pair in it. Together they are
/// the design's "matches the Windows live pair or its rotation": the
/// fingerprint settles it outright, and the named account settles it after the
/// CLI has rotated the token out from under the record.
/// </summary>
internal readonly record struct WindowsHold(AccountEmail? Account, RefreshTokenFingerprint? Fingerprint);

/// <summary>
/// One slot as this side reads it. <paramref name="StaleRecord"/> is the
/// design's reconciliation: a record the files contradict, which the next
/// dashboard read drops. <paramref name="Record"/> is the record that verdict
/// was formed on, so the drop can tell it from one written since.
/// </summary>
internal sealed record SlotSnapshot(SlotState State, bool StaleRecord, HolderRecord? Record);
