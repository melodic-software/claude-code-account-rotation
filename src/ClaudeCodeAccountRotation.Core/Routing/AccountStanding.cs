using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Routing;

/// <summary>
/// Everything known about one account at the moment a decision is made about it,
/// gathered by the caller that owns those three sources and passed in whole, so
/// the rule that reads it stays pure and testable without the machine around it.
/// <para>
/// <paramref name="Latest"/> is the one merged snapshot behind the account's
/// numbers, not a second reading of them: a decision keyed on figures the
/// operator is not looking at is a decision nobody can check. It is null for an
/// account nothing has read yet, which is a third answer rather than a zero,
/// because an untouched window and an unknown one call for different treatment.
/// </para>
/// <para>
/// The last three carry defaults so a caller that knows none of them compiles
/// unchanged. <paramref name="HeldElsewhere"/> is an account another side of this
/// machine is using or is being handed to: in use, like <paramref name="IsLive"/>.
/// </para>
/// </summary>
public sealed record AccountStanding(
    AccountEmail Email,
    bool IsLive,
    bool IsPaused,
    bool HasCredentials,
    UsageSnapshot? Latest = null,
    DateTimeOffset? LoginExpiresAt = null,
    bool HeldElsewhere = false);
