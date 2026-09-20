namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// How far the leader's coordinator got before its journal was last written,
/// for a switch of the other side. Design 9.1's L1 to L4.
/// <para>
/// Like the follower's journal, this one is a hint and the files are the
/// evidence: the leader's crash table decides every row by asking the follower
/// what it did and by reading the mailbox, and the step only says where to
/// look first. In particular a follower journal that has been cleared is never
/// read as "nothing happened" — that is the leader-crashed-after-the-commit
/// case, and it resolves forward.
/// </para>
/// </summary>
public enum WslSwitchStep
{
    /// <summary>L2: the target's pair is renamed into the other side's mailbox and the slot carries its record.</summary>
    Claimed,

    /// <summary>
    /// L3b: the follower has answered <c>Exported</c> and the leader has read
    /// that export <b>natively</b>, on the store's own volume, and found the
    /// fingerprint the follower named. Only now may a commit be sent, because
    /// the commit is what destroys the outgoing pair's last local copy.
    /// </summary>
    ExportVerified,

    /// <summary>L3c: the follower has performed the swap and answered the result.</summary>
    Imported,

    /// <summary>L4: the export has been renamed into the outgoing account's slot and its record cleared.</summary>
    Parked,
}
