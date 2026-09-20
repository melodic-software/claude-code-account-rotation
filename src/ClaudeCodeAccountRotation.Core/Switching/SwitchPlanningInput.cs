using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Switching;

/// <summary>
/// Everything the planner consults, gathered by the App immediately before
/// planning. <paramref name="LiveFingerprintOwner"/> is the account the tool
/// last recorded as holding the live pair's lineage (null when unknown);
/// <paramref name="JournalOpen"/> is true while any credential is unaccounted
/// for: an earlier switch still unreconciled, a quarantined duplicate lineage,
/// or a credential-bearing temporary a crashed write left where it lies.
/// <para>
/// <paramref name="TargetHasRecoveryFile"/> says a refresh already rotated the
/// target's pair and could not write it back, so the rotated pair is parked in
/// the recovery directory and the file still in the folder is dead. It is read
/// under the mutation gate, like every other input here, and it trails the
/// record with a default so the construction sites that know nothing about
/// refreshes compile unchanged.
/// </para>
/// <para>
/// <paramref name="TargetSlot"/> is the target account's slot as
/// <see cref="SlotStateRule.Resolve"/> reads it, for the shared store the two
/// sides of a machine share. It defaults to <see cref="SlotState.Parked"/>,
/// which is what every slot is on a machine with one side, so the sites that
/// know nothing about sides compile unchanged.
/// </para>
/// </summary>
public sealed record SwitchPlanningInput(
    LiveAccountState Live,
    ParkedProfile Target,
    CredentialPair? LiveCredentials,
    CredentialPair? TargetCredentials,
    ManagedLoginPolicy Policy,
    bool JournalOpen,
    AccountEmail? LiveFingerprintOwner,
    string ProfilesRoot,
    DateTimeOffset Now,
    bool TargetHasRecoveryFile = false,
    SlotState TargetSlot = SlotState.Parked);
