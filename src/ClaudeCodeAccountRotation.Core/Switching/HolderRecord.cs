using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// What <c>holder.json</c> says: which side took this account's pair out of its
/// slot, which pair it was, and since when.
/// <para>
/// The record is an index into the store, not the store's truth. Possession is
/// the truth for tokens; the record exists because possession is unobservable
/// from Windows exactly when the page needs it, with the distro off. When the
/// two disagree, the file wins: a slot holding a pair and a record naming a
/// side means the record is stale, and reconciliation drops it.
/// </para>
/// </summary>
public sealed record HolderRecord(SideName Side, RefreshTokenFingerprint Fingerprint, DateTimeOffset Since);
