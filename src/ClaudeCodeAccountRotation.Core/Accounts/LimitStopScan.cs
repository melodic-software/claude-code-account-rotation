using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>
/// One change of which account backs one half of a role, as the lane history
/// records it: <paramref name="Account"/> is null when the half was left with
/// none, <paramref name="Previous"/> when it had none.
/// </summary>
public sealed record RoleHalfChange(DateTimeOffset At, CiRole Role, RoleHalf Half, string? Account, string? Previous);

/// <summary>A failed workflow run in a lane repository (<c>owner/name</c>), and its page on GitHub.</summary>
public sealed record LaneRun(string Repository, long Id, DateTimeOffset CreatedAt, Uri? Url);

/// <summary>One annotation message from a failed job of <paramref name="Run"/>.</summary>
public sealed record LaneAnnotation(LaneRun Run, string Message);

/// <summary>
/// A run that stopped on a limit of a half the account backed when the run
/// started: a subscription limit on a token half, a credit stop on a key half.
/// </summary>
public sealed record LimitStopHit(LaneRun Run, CiRole Role, RoleHalf Half, string Credential)
{
    public string Label => Half == RoleHalf.Token ? "subscription limit" : "credit stop";
}

/// <summary>
/// The limit-stop warning before a role is moved off an account. Pure: the
/// caller reads the runs, jobs and annotations; this picks the runs to read
/// and finds the hits in what was read.
/// <para>
/// A lane that stops on a limit reports a <c>core.error</c> annotation whose
/// message holds <c>class=rate-limit</c> and <c>credential=&lt;secret name&gt;</c>
/// among other <c>key=value</c> tokens; a credit stop is reported under the
/// same class. Basis: ci-workflows <c>report-lane-outcome</c> and
/// <c>intake-triage.yml</c>. The review lanes print no credential, so their
/// stops are not seen.
/// </para>
/// </summary>
public static class LimitStopScan
{
    /// <summary>The most runs one scan reads, across every repository. Basis: the plan's cap per click.</summary>
    public const int MaxRuns = 30;

    /// <summary>How far back a scan looks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>The newest <see cref="MaxRuns"/> runs started within <see cref="Window"/> of <paramref name="now"/>, newest first.</summary>
    public static IReadOnlyList<LaneRun> Newest(IEnumerable<LaneRun> runs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(runs);
        DateTimeOffset since = now - Window;
        return [.. runs
            .Where(run => run.CreatedAt >= since)
            .OrderByDescending(static run => run.CreatedAt)
            .ThenByDescending(static run => run.Id)
            .Take(MaxRuns)];
    }

    /// <summary>
    /// The credential an annotation message names when it reports a rate
    /// limit, else null. Whole tokens only: <c>class=rate-limited</c> or a
    /// <c>credential=</c> inside another token is no match.
    /// </summary>
    public static string? RateLimitedCredential(string? message)
    {
        if (message is null)
        {
            return null;
        }

        bool rateLimit = false;
        string? credential = null;
        foreach (string token in message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            rateLimit |= string.Equals(token, "class=rate-limit", StringComparison.Ordinal);
            if (token.StartsWith("credential=", StringComparison.Ordinal) && token.Length > "credential=".Length)
            {
                credential = token["credential=".Length..];
            }
        }

        return rateLimit ? credential : null;
    }

    /// <summary>
    /// The hits for <paramref name="account"/>: each rate-limit annotation
    /// whose credential is a half of a role that the run's repository draws on
    /// (the repository's organization for an organization role, the repository
    /// itself for a repository role) and that <paramref name="account"/> backed
    /// when the run started. One hit per run and half.
    /// </summary>
    /// <param name="history">The lane history, any order.</param>
    /// <param name="roster">Who backs each half now: the answer for a half the history never mentions.</param>
    public static IReadOnlyList<LimitStopHit> Hits(
        AccountEmail account,
        IEnumerable<LaneAnnotation> annotations,
        IReadOnlyList<RoleHalfChange> history,
        Roster roster)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(roster);
        List<RoleHalfChange> ordered = [.. history.OrderBy(static change => change.At)];
        List<CiRole> roles = [.. history.Select(static change => change.Role)
            .Concat(roster.Entries.SelectMany(static entry => entry.CiRoles).Select(static assignment => assignment.Role))
            .Distinct()];

        List<LimitStopHit> hits = [];
        foreach (LaneAnnotation annotation in annotations)
        {
            if (RateLimitedCredential(annotation.Message) is not string credential)
            {
                continue;
            }

            foreach (CiRole role in roles.Where(role => DrawnOnBy(role, annotation.Run.Repository)))
            {
                RoleHalf? half = string.Equals(role.TokenSecretName, credential, StringComparison.OrdinalIgnoreCase) ? RoleHalf.Token
                    : string.Equals(role.KeySecretName, credential, StringComparison.OrdinalIgnoreCase) ? RoleHalf.Key
                    : null;
                if (half is RoleHalf found
                    && string.Equals(Backer(role, found, annotation.Run.CreatedAt, ordered, roster), account.Value, StringComparison.OrdinalIgnoreCase)
                    && !hits.Exists(hit => hit.Run == annotation.Run && hit.Role.Equals(role) && hit.Half == found))
                {
                    hits.Add(new LimitStopHit(annotation.Run, role, found, credential));
                }
            }
        }

        return hits;
    }

    private static bool DrawnOnBy(CiRole role, string repository) => role.Scope == CiSecretScope.Repository
        ? string.Equals(role.Owner, repository, StringComparison.OrdinalIgnoreCase)
        : string.Equals(role.Owner, repository.Split('/')[0], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Who backed the half at <paramref name="at"/>: the account of the last
    /// change at or before it; else, when every change came later, the first
    /// one's previous account; else, with no change on record, the roster's.
    /// </summary>
    private static string? Backer(CiRole role, RoleHalf half, DateTimeOffset at, List<RoleHalfChange> ordered, Roster roster)
    {
        List<RoleHalfChange> changes = ordered.FindAll(change => change.Half == half && change.Role.Equals(role));
        if (changes.Count > 0)
        {
            return changes.LastOrDefault(change => change.At <= at) is RoleHalfChange before ? before.Account : changes[0].Previous;
        }

        return roster.Entries
            .FirstOrDefault(entry => entry.CiRoles.Any(assignment => assignment.Role.Equals(role) && (half == RoleHalf.Token ? assignment.HasToken : assignment.HasKey)))
            ?.Email.Value;
    }
}
