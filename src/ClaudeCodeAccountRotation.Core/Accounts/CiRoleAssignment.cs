namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>
/// One role an account backs, and when each of its two secrets was set from
/// that account. A half is set when its date is; the instant beside it is null
/// for a half set by hand or recorded by an earlier release. Nothing here is
/// read back from GitHub.
/// </summary>
public sealed record CiRoleAssignment(
    CiRole Role,
    DateOnly? TokenSetOn = null,
    DateTimeOffset? TokenSetAt = null,
    DateOnly? KeySetOn = null,
    DateTimeOffset? KeySetAt = null)
{
    public bool HasToken => TokenSetOn is not null;

    public bool HasKey => KeySetOn is not null;
}
