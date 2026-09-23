namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// Why a switch is not planned or not executed. Ordered as the planner checks
/// them: the spike's guards first, then the three the plan review added and the
/// one the refresh pass needs for a folder it stranded; then the three the
/// shared store adds, for a slot this side does not hold; the last are the
/// executor's own, for a second mutation arriving while one runs, for a usage
/// refresh reading this machine's accounts, for a login that owns one of the
/// folders the switch would move, and for a live file whose tokens disappeared.
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

    /// <summary>
    /// <b>The export gate refused it.</b> The other side answered that it had
    /// exported the pair it holds, and the leader's native read of that file,
    /// on the store's own volume, did not find the fingerprint it named — a
    /// mismatch, a short read, or no file at all. The import was aborted and
    /// the claim taken back before the commit that would have destroyed the
    /// outgoing pair's last local copy, so nothing was swapped and both sides'
    /// live pairs are exactly where they were.
    /// </summary>
    ExportNotVerified,

    /// <summary>
    /// <b>The other side holds a second token family for the account it is live
    /// on.</b> That account's slot carries a <c>superseded.json</c> naming that
    /// side, which only one control writes: <c>Log in again on Windows</c> while
    /// that side was unreachable. The family over there is no longer the store's,
    /// so parking it back would put two families of one account in the store, and
    /// every hand-off that would move it is refused until the operator says what
    /// to do with it. Nothing else is blocked: a Windows switch, a refresh, and a
    /// hand-off of any other account all run as before.
    /// </summary>
    ForeignFamily,

    /// <summary>
    /// The other side answered a definite "not imported" for this account: its
    /// own crash table unwound the import, or it never reached one. The claim
    /// has been taken back and the slot holds its pair again. This is never
    /// inferred from a cleared journal; it is only ever that side's own answer.
    /// </summary>
    PeerDidNotImport,

    /// <summary>
    /// <b>A release with nothing to release.</b> The other side is answering and
    /// holds no live pair, so there is no hand-off to make: the store already
    /// has every family it can have and that side is already where a release
    /// would leave it. The page hides the control in this state; this refusal is
    /// what answers when it is bypassed, and it is not
    /// <see cref="AlreadyOnTarget"/> because a release names no target.
    /// </summary>
    NothingToRelease,

    MutationInProgress,

    /// <summary>
    /// A usage refresh is in flight. A pass fixes the live account's identity when
    /// it starts and reads the live pair between its gated units, so a switch
    /// landing mid-pass would have the outgoing account's turn read the incoming
    /// account's pair and put one account's figures on the other's card.
    /// </summary>
    RefreshInProgress,
    LoginInProgress,

    /// <summary>
    /// The live credential file had a parsed pair and now lacks its tokens. The
    /// live card says when. A switch would park that file, and it is not a pair,
    /// so the page does not offer Switch while this stands. This refusal is what
    /// answers when the button is bypassed.
    /// </summary>
    CliLoggedOut,
}
