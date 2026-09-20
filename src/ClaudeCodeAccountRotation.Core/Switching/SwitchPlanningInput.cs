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
/// <paramref name="TargetSlot"/> is the target account's slot in the store the
/// two sides of a machine share, as <see cref="SlotStateRule.Resolve"/> reads
/// it. It trails the record and defaults to <see cref="SlotState.Parked"/>, the
/// one value neither of the side guards below refuses, so the construction
/// sites that know nothing about sides compile and behave unchanged.
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
