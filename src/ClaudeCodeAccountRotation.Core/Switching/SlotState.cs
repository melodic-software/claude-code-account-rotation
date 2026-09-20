namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// What a store slot holds, from the point of view of one side. Derived in one
/// place, by <see cref="SlotStateRule.Resolve"/>.
/// </summary>
public enum SlotState
{
    /// <summary>The slot holds a credential pair: free for either side to take.</summary>
    Parked,

    /// <summary>The pair is out of the slot and in this side's live directory.</summary>
    HeldHere,

    /// <summary>The pair is out of the slot and in another side's live directory.</summary>
    HeldElsewhere,

    /// <summary>The slot has never held a pair, or the account was removed.</summary>
    NeverLoggedIn,

    /// <summary>A hand-off for this account is in flight: a file for it sits in a side's mailbox.</summary>
    InTransit,
}
