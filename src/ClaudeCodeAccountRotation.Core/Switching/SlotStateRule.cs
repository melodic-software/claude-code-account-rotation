namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// The single derivation of a slot's state from what the file system holds. No
/// caller open-codes it.
/// </summary>
public static class SlotStateRule
{
    /// <summary>
    /// Reconciles a slot's files against its record, from <paramref name="thisSide"/>'s
    /// point of view, in the order the design's reconciliation table reads.
    /// <para>
    /// A file in a side's mailbox outranks everything: the hand-off is in
    /// flight and neither end may be trusted to hold the pair. Then a slot
    /// holding a pair means <see cref="SlotState.Parked"/> whatever the record
    /// says, because possession is the truth for tokens and a record naming a
    /// side is then stale. Then the record's side decides. Then nothing has
    /// ever been parked here.
    /// </para>
    /// <para>
    /// Dropping the stale record and logging it are the caller's, as is
    /// checking that a <see cref="SlotState.HeldHere"/> record's fingerprint is
    /// this side's live pair or its rotation. Nothing here touches a file.
    /// </para>
    /// </summary>
    public static SlotState Resolve(bool slotFileExists, HolderRecord? record, SideName thisSide, bool transitFileExists)
    {
        if (transitFileExists)
        {
            return SlotState.InTransit;
        }

        if (slotFileExists)
        {
            return SlotState.Parked;
        }

        if (record is null)
        {
            return SlotState.NeverLoggedIn;
        }

        return record.Side == thisSide ? SlotState.HeldHere : SlotState.HeldElsewhere;
    }
}
