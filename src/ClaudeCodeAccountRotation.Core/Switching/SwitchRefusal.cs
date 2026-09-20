namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// Why a switch is not planned or not executed. Ordered as the planner checks
/// them: the spike's guards first, then the three the plan review added and the
/// one the refresh pass needs for a folder it stranded; then the three the
/// shared store adds, for a slot this side does not hold; the last three are the
/// executor's own, for a second mutation arriving while one runs, for a usage
/// refresh reading this machine's accounts, and for a login that owns one of the
/// folders the switch would move.
/// </summary>
public enum SwitchRefusal
{
    TargetIsLiveDirectory,
    TargetHasNoCredentials,
    TargetHasNoAccountBlock,
    AlreadyOnTarget,
    SharesLiveRefreshToken,
    RefreshLockPresent,

    /// <summary>
    /// The target folder's rotated credential pair sits in the recovery directory
    /// after a write-back that failed: the file still in the folder holds the
    /// refresh token the token endpoint killed the moment it answered, so moving
    /// it to live would move a dead pair there, and the restore that could still
    /// put the rotated one back compares against the parked file this switch
    /// would have taken away. A restart or a per-card refresh clears it.
    /// </summary>
    TargetStrandedInRecovery,
    TargetLoginExpired,
    SwitchingBlockedByManagedPolicy,
    ManagedPolicyUnreadable,
    LiveIdentityUnverified,

    /// <summary>
    /// The other side of this machine holds the target account's pair: its slot
    /// carries a holder record naming that side and no credential file, because
    /// an account has one token family per machine and the pair is moved, never
    /// copied. Taking it needs the other side to park it first, which is the
    /// hand-off, not a switch. The page disables the button; this refusal is
    /// what answers when the button is bypassed.
    /// </summary>
    HeldByOtherSide,

    /// <summary>
    /// A hand-off for this account is in flight: a file naming it sits in a
    /// side's mailbox, so the pair is somewhere between two live directories and
    /// no reader can say which end will own it. Nothing auto-clears the state;
    /// it ends when the hand-off finishes or the operator cancels it.
    /// </summary>
    SlotInTransit,

    /// <summary>
    /// The switch is for the other side and that side is not answering, so
    /// nothing can be asked to import the pair. The store is untouched: an
    /// offline side keeps its pairs in its own live directory.
    /// </summary>
    SideOffline,

    MutationInProgress,

    /// <summary>
    /// A usage refresh is in flight. A pass fixes the live account's identity when
    /// it starts and reads the live pair between its gated units, so a switch
    /// landing mid-pass would have the outgoing account's turn read the incoming
    /// account's pair and put one account's figures on the other's card.
    /// </summary>
    RefreshInProgress,
    LoginInProgress,
}
