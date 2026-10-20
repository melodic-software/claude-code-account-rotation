using System.Collections.Immutable;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>The Chromium-family browsers the launcher knows how to open a profile in.</summary>
public enum BrowserFamily
{
    Chrome,
    Edge,
    Brave,
}

/// <summary>
/// One account the operator put on this machine: its e-mail, what to call it,
/// which browser profile signs it in, whether it is out of the rotation, and
/// whatever the operator wrote about it. No token field exists here; the
/// credential pair lives in the profile folder, never in the roster.
/// <para>
/// <see cref="CiRoles"/> are the CI funding roles this account backs, and
/// <see cref="CiTokenGeneratedOn"/> the date of its newest
/// <c>claude setup-token</c> token still in a role: the newest token half's
/// date, kept so by <see cref="WithCiRoles"/>. A date with no token half is a marker the
/// operator set by hand, for the secret the README names. None of it is read
/// back from GitHub. At most one entry on a <see cref="Roster"/> backs each
/// role; see <see cref="Roster.With"/>.
/// </para>
/// </summary>
public sealed record RosterEntry(
    AccountEmail Email,
    string? Alias = null,
    BrowserFamily? Browser = null,
    string? BrowserProfileDirectory = null,
    bool Paused = false,
    string? Notes = null,
    DateOnly? CiTokenGeneratedOn = null)
{
    public ImmutableArray<CiRoleAssignment> CiRoles { get; init; } = [];

    /// <summary>The date this account's Console API key was stored, or null when none is.</summary>
    public DateOnly? ConsoleApiKeySetOn { get; init; }

    /// <summary>The token secret of the role whose token half was set last, or null when no role has one.</summary>
    public CiTokenSecret? CiTokenSecret => CiRoles
        .Where(static assignment => assignment.HasToken)
        .OrderBy(static assignment => assignment.TokenSetOn)
        .ThenBy(static assignment => assignment.TokenSetAt)
        .LastOrDefault()?.Role.TokenSecret;

    public bool HasTokenHalf => CiRoles.Any(static assignment => assignment.HasToken);

    /// <summary>
    /// Records a new token for <paramref name="role"/>, keeping that role's key
    /// half and every other role as they are; see <see cref="WithCiRoles"/> for the date.
    /// </summary>
    public RosterEntry ClaimingToken(CiRole role, DateOnly setOn, DateTimeOffset? setAt)
    {
        ArgumentNullException.ThrowIfNull(role);
        CiRoleAssignment held = CiRoles.FirstOrDefault(assignment => assignment.Role.Equals(role)) ?? new CiRoleAssignment(role);
        CiRoleAssignment claimed = held with { Role = role, TokenSetOn = setOn, TokenSetAt = setAt };
        return WithCiRoles(CiRoles.Any(assignment => assignment.Role.Equals(role))
            ? CiRoles.Select(assignment => assignment.Role.Equals(role) ? claimed : assignment)
            : CiRoles.Add(claimed));
    }

    /// <summary>
    /// Replaces the roles and settles <see cref="CiTokenGeneratedOn"/>: on an
    /// account left with a token half it is the newest token half's date, and
    /// on one that just lost its last token half it is cleared, since nothing
    /// in CI runs on that token. An account that held no token half keeps its
    /// date, a marker set by hand.
    /// </summary>
    public RosterEntry WithCiRoles(IEnumerable<CiRoleAssignment> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        RosterEntry updated = this with { CiRoles = [.. roles] };
        return updated with
        {
            CiTokenGeneratedOn = updated.CiRoles.Max(static assignment => assignment.TokenSetOn)
                ?? (HasTokenHalf ? null : CiTokenGeneratedOn),
        };
    }

    public bool Equals(RosterEntry? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Email == other.Email
            && Alias == other.Alias
            && Browser == other.Browser
            && BrowserProfileDirectory == other.BrowserProfileDirectory
            && Paused == other.Paused
            && Notes == other.Notes
            && CiTokenGeneratedOn == other.CiTokenGeneratedOn
            && ConsoleApiKeySetOn == other.ConsoleApiKeySetOn
            && CiRoles.SequenceEqual(other.CiRoles));

    public override int GetHashCode() =>
        HashCode.Combine(Email, Alias, Browser, BrowserProfileDirectory, Paused, Notes, CiTokenGeneratedOn, ConsoleApiKeySetOn);
}
