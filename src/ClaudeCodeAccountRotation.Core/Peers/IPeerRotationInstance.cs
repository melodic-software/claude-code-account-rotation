using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.Core.Peers;

/// <summary>
/// What the leader asks the follower to take. No token crosses the wire:
/// identity is the SHA-256 fingerprint and the pair itself is read from
/// <see cref="ClaimedPath"/>, a file on the store's own volume that the leader
/// renamed into the mailbox before asking. Both paths are spelled in the
/// <b>peer's</b> namespace, which is what <c>storePathFromPeer</c> is for.
/// </summary>
public sealed record ImportRequest(
    AccountEmail Email,
    string ClaimedPath,
    RefreshTokenFingerprint Fingerprint,
    JsonObject Account,
    string ExportPath);

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
public sealed record ImportStatus(
    bool Imported,
    ImportStep? JournalStep,
    RefreshTokenFingerprint? LiveFingerprint,
    AccountEmail? LiveAccount,
    string Detail);

/// <summary>
/// What the follower's dashboard tells the leader's L1 about that side.
/// <see cref="Version"/> is what the version check refuses on: the two
/// processes share a journal vocabulary and a request shape, so a follower
/// built from different sources is refused the import before anything moves,
/// rather than after a rename it cannot finish.
/// </summary>
public sealed record PeerDashboard(
    SideName Side,
    AccountEmail? LiveAccount,
    RefreshTokenFingerprint? LiveFingerprint,
    ImportStep? ImportJournalStep,
    string? Version);

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
