using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.Core.Peers;

/// <summary>
/// What the leader asks the follower to take, and what it asks it to give up.
/// No token crosses the wire: identity is the SHA-256 fingerprint and the pair
/// itself is read from <see cref="ClaimedPath"/>, a file on the store's own
/// volume that the leader renamed into the mailbox before asking. Both paths
/// are spelled in the <b>peer's</b> namespace, which is what
/// <c>storePathFromPeer</c> is for.
/// <para>
/// A <b>release</b> is this same request with its incoming half empty, and it
/// is deliberately not a second contract: the follower's journal, its crash
/// table, its two-call export gate, its commit budget and its refresh-lock
/// heartbeat are the same machinery whichever way the pair is moving.
/// </para>
/// </summary>
/// <param name="Email">
/// The account this transaction is about, and the key <c>commit</c>,
/// <c>abort</c> and <c>import-status</c> answer on. For an import it is the
/// account arriving; for a release it is the one leaving, because that is the
/// only account a release names.
/// </param>
/// <param name="Fingerprint">
/// The pair this transaction is about, as the leader last read it. For an
/// import it is what the stage must read back as. For a release it is the
/// pair the leader last saw live on that side. A side that has since
/// switched to another account refuses before exporting. A side whose live
/// pair has rotated exports the pair it holds, and the answer names that
/// fingerprint so the leader parks it.
/// </param>
public sealed record ImportRequest(
    AccountEmail Email,
    string? ClaimedPath,
    RefreshTokenFingerprint Fingerprint,
    JsonObject? Account,
    string ExportPath)
{
    /// <summary>
    /// Whether nothing arrives on that side: the park-back of its live pair
    /// into the store, with no incoming pair to put in its place. Read off the
    /// absent <see cref="ClaimedPath"/>, which is the one thing an import
    /// cannot be without — a release has no claimed file because the leader
    /// claimed nothing.
    /// </summary>
    public bool IsRelease => ClaimedPath is null;
}

/// <summary>
/// The answer to the first call: the import has run F1 to F4 and stopped.
/// <see cref="ExportedFingerprint"/> is the outgoing pair the leader must now
/// read natively and verify before it may commit, or null when that side held
/// nothing and there is nothing to verify.
/// </summary>
public sealed record ImportAnswer(
    RefreshTokenFingerprint? ExportedFingerprint,
    AccountEmail? Outgoing,
    bool AlreadyImported,
    ImportResult? Result);

/// <summary>What the commit hands back: which account left that side, and its block for the leader's park.</summary>
public sealed record ImportResult(
    AccountEmail? Outgoing,
    RefreshTokenFingerprint? OutgoingFingerprint,
    JsonObject? OutgoingAccount,
    bool AlreadyImported);

/// <summary>
/// What the follower believes about the import the leader is asking after,
/// decided from the files and not only from its journal. A cleared journal is
/// never "nothing happened". <see cref="LiveAccount"/> is read in the same
/// snapshot as <see cref="LiveFingerprint"/>, so the two never name different
/// accounts.
/// </summary>
/// <param name="LoginExpiresAt">
/// When the login behind that side's live pair runs out, read in the same
/// snapshot as the fingerprint beside it. One family per account means one
/// expiry per card, and for an account this side holds there is no local file
/// to read it from, so this is the only source (design 12). Null when that side
/// holds nothing or its pair carries no such instant.
/// </param>
public sealed record ImportStatus(
    bool Imported,
    ImportStep? JournalStep,
    RefreshTokenFingerprint? LiveFingerprint,
    OAuthAccountBlock? LiveAccount,
    string Detail,
    DateTimeOffset? LoginExpiresAt = null);

/// <summary>
/// What the follower's dashboard tells the leader's L1 about that side.
/// <see cref="Version"/> is what the version check refuses on: the two
/// processes share a journal vocabulary and a request shape, so a follower
/// built from different sources is refused the import before anything moves,
/// rather than after a rename it cannot finish.
/// </summary>
/// <remarks>
/// <see cref="LiveAccountBlock"/> is that side's <c>oauthAccount</c> block, the
/// same identity its slot's <c>profile.json</c> would carry. It rides along
/// because the leader has to journal it at the claim: after the swap the other
/// side's state file names the <i>incoming</i> account, so a leader that died
/// between the commit and its own journal write could never learn it again,
/// and would park the outgoing pair into a folder with no identity — a slot
/// the roster skips and a later switch refuses. No token is in it.
/// </remarks>
/// <param name="Tee">
/// That side's own rate-limit-guard observation, the free tier of the refresh
/// contract as its live sessions wrote it. The leader reads no usage for a pair
/// it does not hold — design 12 — so this is where a held account's figures on
/// the Windows page come from, and it is null when that side has no snapshot or
/// none it could attribute.
/// </param>
public sealed record PeerDashboard(
    SideName Side,
    AccountEmail? LiveAccount,
    RefreshTokenFingerprint? LiveFingerprint,
    ImportStep? ImportJournalStep,
    string? Version,
    JsonObject? LiveAccountBlock = null,
    StatuslineSnapshot? Tee = null,
    DateTimeOffset? LoginExpiresAt = null);

/// <summary>
/// The other side of this machine, as the leader's coordinator talks to it:
/// one dashboard read and the two-call export gate of design 9.2. Every method
/// answers a failure rather than throwing, because an unreachable side is an
/// ordinary state — the distro may be off — and never a fault.
/// </summary>
public interface IPeerRotationInstance
{
    /// <summary>Which side this is, and the base address a failure message names.</summary>
    SideName Side { get; }

    /// <summary>L1: is that side online, and what does it hold?</summary>
    Task<Result<PeerDashboard, string>> ReadDashboardAsync(CancellationToken cancellationToken);

    /// <summary>L3a: F1 to F4, stopping at the export.</summary>
    Task<Result<ImportAnswer, string>> ImportAsync(ImportRequest request, CancellationToken cancellationToken);

    /// <summary>L3c: F5 to F8, and only after the leader's native read has passed.</summary>
    Task<Result<ImportResult, string>> CommitImportAsync(AccountEmail email, CancellationToken cancellationToken);

    /// <summary>The gate's refusal path: unwind the import and leave that side's live pair where it is.</summary>
    Task<Result<Unit, string>> AbortImportAsync(AccountEmail email, CancellationToken cancellationToken);

    /// <summary>The leader's crash table: what happened to this account's import, from the files.</summary>
    Task<Result<ImportStatus, string>> ImportStatusAsync(AccountEmail email, CancellationToken cancellationToken);
}
