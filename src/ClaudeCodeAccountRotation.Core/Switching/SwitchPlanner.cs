using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// Decides whether a switch may proceed and what it moves. Pure: every guard
/// from the swap spike, in the spike's order, then the three the plan review
/// added. Nothing here touches the machine.
/// </summary>
public static class SwitchPlanner
{
    public static Result<SwitchPlan, SwitchRefusal> Plan(SwitchPlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // An unreconciled switch or a live pair whose recorded owner is not the account
        // the state file names means nothing below can be trusted, so this comes first.
        AccountEmail? liveEmail = input.Live.Account?.Email;
        bool ownerMismatch = input.LiveFingerprintOwner is AccountEmail owner && liveEmail is AccountEmail named && owner != named;
        if (input.JournalOpen || ownerMismatch)
        {
            return Refuse(SwitchRefusal.LiveIdentityUnverified);
        }

        // A live pair whose account the state file does not name has no folder to be parked
        // under, and an unpark onto it would fail half-way with the journal left open.
        if (input.Live.HasCredentials && liveEmail is null)
        {
            return Refuse(SwitchRefusal.LiveIdentityUnverified);
        }

        if (SameDirectory(input.Target.FolderPath, input.Live.LiveConfigDirectory))
        {
            return Refuse(SwitchRefusal.TargetIsLiveDirectory);
        }

        // The live account's own folder never holds a pair, so "already live" is decided
        // before "no parked credentials" or the refusal names the wrong reason.
        if (liveEmail is not null && input.Target.Account?.Email == liveEmail)
        {
            return Refuse(SwitchRefusal.AlreadyOnTarget);
        }

        if (!input.Target.HasCredentials || input.TargetCredentials is null)
        {
            return Refuse(SwitchRefusal.TargetHasNoCredentials);
        }

        if (input.Target.Account?.Email is not AccountEmail incoming)
        {
            return Refuse(SwitchRefusal.TargetHasNoAccountBlock);
        }

        if (input.LiveCredentials is not null && input.LiveCredentials.Fingerprint == input.TargetCredentials.Fingerprint)
        {
            return Refuse(SwitchRefusal.SharesLiveRefreshToken);
        }

        if (input.Live.FreshLockFileName is not null)
        {
            return Refuse(SwitchRefusal.RefreshLockPresent);
        }

        // Before the expiry check, because the pair still in the folder is the one
        // a failed write-back left behind: its refresh token died when the token
        // endpoint answered, so its recorded expiry says nothing useful and an
        // unpark would move a dead pair to live, after which no restore can apply.
        if (input.TargetHasRecoveryFile)
        {
            return Refuse(SwitchRefusal.TargetStrandedInRecovery);
        }

        if (input.TargetCredentials.LoginExpiresAt is DateTimeOffset loginExpiresAt && loginExpiresAt <= input.Now)
        {
            return Refuse(SwitchRefusal.TargetLoginExpired);
        }

        if (input.Policy.Unreadable)
        {
            return Refuse(SwitchRefusal.ManagedPolicyUnreadable);
        }

        if (input.Policy.BlocksSwitching)
        {
            return Refuse(SwitchRefusal.SwitchingBlockedByManagedPolicy);
        }

        AccountEmail? outgoing = input.Live.HasCredentials ? liveEmail : null;
        string? outgoingFolderPath = outgoing is AccountEmail parkedAs
            ? Path.Combine(input.ProfilesRoot, ProfileFolderName.FromEmail(parkedAs))
            : null;

        return Result<SwitchPlan, SwitchRefusal>.Success(new SwitchPlan(
            outgoing,
            outgoingFolderPath,
            incoming,
            input.Target.FolderPath,
            input.Target.Account));
    }

    private static bool SameDirectory(string first, string second)
    {
        string left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        string right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }

    private static Result<SwitchPlan, SwitchRefusal> Refuse(SwitchRefusal refusal) =>
        Result<SwitchPlan, SwitchRefusal>.Failure(refusal);
}
