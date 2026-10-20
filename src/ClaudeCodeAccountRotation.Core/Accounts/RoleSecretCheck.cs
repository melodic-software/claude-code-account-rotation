using System.Collections.Immutable;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>The two secrets of a role.</summary>
public enum RoleHalf
{
    Token,
    Key,
}

/// <summary>What GitHub's listing says about one half the roster records as set from an account.</summary>
public enum RoleSecretVerdict
{
    /// <summary>The secret has not changed since the tool set it.</summary>
    Ok,

    /// <summary>The secret changed after the tool set it, so someone else wrote it.</summary>
    ChangedOutsideTool,

    /// <summary>
    /// Cannot be told: set on the day the roster records with no instant to
    /// compare, listed as older than the tool's own write, or listed with no
    /// readable time.
    /// </summary>
    Unknown,

    /// <summary>The secret is gone.</summary>
    Missing,
}

/// <summary>One half a roster entry claims, and its verdict.</summary>
public sealed record ClaimedHalf(AccountEmail Holder, CiRole Role, RoleHalf Half, string SecretName, RoleSecretVerdict Verdict);

/// <summary>
/// A check of the roster against the listed secrets: a verdict per claimed half
/// whose organization was read, and the roles whose secrets exist that no entry claims.
/// </summary>
public sealed record RoleSecretReport(DateTimeOffset CheckedAt, ImmutableArray<ClaimedHalf> Claimed, ImmutableArray<RoleSecrets> Unclaimed);

/// <summary>
/// Whether each CI role secret the roster says the tool set is still the one it
/// set: the out-of-band change check. Pure: it compares the roster's dates with
/// the listing's <c>updated_at</c> and reads nothing itself.
/// </summary>
public static class RoleSecretCheck
{
    /// <summary>
    /// How much later than the tool's own write GitHub may stamp a secret before
    /// the change counts as someone else's: the gap between this machine's clock
    /// and GitHub's. Basis: judgment.
    /// </summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <param name="now">When the listing was read, carried on the report.</param>
    /// <param name="zone">
    /// The zone the roster's set-on dates were written in: the tool records
    /// them as this machine's local date, so <c>updated_at</c> is compared as a
    /// date in that zone.
    /// </param>
    public static RoleSecretReport Evaluate(Roster roster, RoleCatalog secrets, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(zone);
        ImmutableArray<ClaimedHalf>.Builder claimed = ImmutableArray.CreateBuilder<ClaimedHalf>();
        HashSet<CiRole> held = [];
        foreach (RosterEntry entry in roster.Entries)
        {
            foreach (CiRoleAssignment assignment in entry.CiRoles)
            {
                held.Add(assignment.Role);
                if (!secrets.Covers(assignment.Role))
                {
                    continue;
                }

                if (assignment.TokenSetOn is DateOnly tokenSetOn)
                {
                    claimed.Add(Judge(entry.Email, assignment.Role, RoleHalf.Token, assignment.Role.TokenSecretName, tokenSetOn, assignment.TokenSetAt, secrets, zone));
                }

                if (assignment.KeySetOn is DateOnly keySetOn && assignment.Role.KeySecretName is string keySecret)
                {
                    claimed.Add(Judge(entry.Email, assignment.Role, RoleHalf.Key, keySecret, keySetOn, assignment.KeySetAt, secrets, zone));
                }
            }
        }

        ImmutableArray<RoleSecrets> unclaimed = [.. secrets.Roles.Values
            .Where(role => !held.Contains(role.Role))
            .OrderBy(static role => role.Role.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static role => role.Role.Prefix, StringComparer.OrdinalIgnoreCase)];
        return new RoleSecretReport(now, claimed.ToImmutable(), unclaimed);
    }

    private static ClaimedHalf Judge(
        AccountEmail holder,
        CiRole role,
        RoleHalf half,
        string secretName,
        DateOnly setOn,
        DateTimeOffset? setAt,
        RoleCatalog secrets,
        TimeZoneInfo zone)
    {
        ListedSecret? listed = secrets.Find(role.Owner, secretName);
        RoleSecretVerdict verdict = listed switch
        {
            null => RoleSecretVerdict.Missing,
            { UpdatedAt: not DateTimeOffset } => RoleSecretVerdict.Unknown,
            { UpdatedAt: DateTimeOffset updated } when setAt is DateTimeOffset at =>
                updated > at + ClockSkew ? RoleSecretVerdict.ChangedOutsideTool
                    // Older than the tool's own write: GitHub never recorded it.
                    : updated < at - ClockSkew ? RoleSecretVerdict.Unknown
                    : RoleSecretVerdict.Ok,
            { UpdatedAt: DateTimeOffset updated } =>
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(updated, zone).DateTime) > setOn
                    ? RoleSecretVerdict.ChangedOutsideTool
                    : RoleSecretVerdict.Unknown,
        };
        return new ClaimedHalf(holder, role, half, secretName, verdict);
    }
}
