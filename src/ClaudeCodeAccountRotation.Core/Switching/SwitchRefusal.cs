namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// Why a switch is not planned or not executed. Ordered as the planner checks
/// them: the spike's guards first, then the three the plan review added and the
/// one the refresh pass needs for a folder it stranded; the last three are the
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
