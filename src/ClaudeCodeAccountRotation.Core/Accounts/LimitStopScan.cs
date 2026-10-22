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

/// <summary>
/// Which page of a repository's run listing to read: runs created at or after
/// <paramref name="Since"/> and, once narrowed, at or before
/// <paramref name="Before"/>.
/// </summary>
public sealed record ListingRange(DateTimeOffset Since, DateTimeOffset? Before, int Page)
{
    /// <summary>The first page of the whole window.</summary>
    public static ListingRange First(DateTimeOffset since) => new(since, null, 1);

    /// <summary>
    /// The page after this one, or null when the repository is read to the end:
    /// a page short of <see cref="LimitStopScan.RunsPerPage"/>, or one reaching
    /// back past <see cref="Since"/>. After the last page GitHub returns for one
    /// date range, the range narrows to runs no newer than the oldest on it,
    /// inclusive, and starts again at page 1; the caller drops runs it has seen.
    /// </summary>
    /// <param name="runs">The runs the page listed, newest first.</param>
    public ListingRange? After(IReadOnlyList<LaneRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count < LimitStopScan.RunsPerPage || runs.Any(run => run.CreatedAt < Since))
        {
            return null;
        }

        return Page == LimitStopScan.PagesPerRange
            ? new ListingRange(Since, runs.Min(static run => run.CreatedAt), 1)
            : this with { Page = Page + 1 };
    }
}

/// <summary>
/// How far back one repository has been read: newest first, the window ends at
/// the first run read only in part, and <see cref="Back"/> is the start time of
/// the oldest run before it (null until one is read).
/// </summary>
public sealed class RunCoverage
{
    public DateTimeOffset? Back { get; private set; }

    /// <summary>False once a run was read only in part.</summary>
    public bool Contiguous { get; private set; } = true;

    /// <summary>Notes <paramref name="run"/>, the next older one, as read in full when <paramref name="whole"/>.</summary>
    public void Read(LaneRun run, bool whole)
    {
        ArgumentNullException.ThrowIfNull(run);
        Contiguous &= whole;
        if (Contiguous)
        {
            Back = run.CreatedAt;
        }
    }
}

/// <summary>One lane-outcome record read from an annotation of <paramref name="Run"/>.</summary>
public sealed record LaneRecord(LaneRun Run, LaneOutcome Outcome);

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
/// caller reads the runs and their annotations; this finds the hits in the
/// records read.
/// <para>
/// A lane reports its outcome in an annotation holding a lane-outcome record,
/// which <see cref="LaneOutcomes.Parse"/> reads: the JSON line after the
/// <c>lane-outcome </c> marker, else the legacy <c>class=</c> and
/// <c>credential=&lt;secret name&gt;</c> tokens. The limit classes are
/// <c>rate-limit</c> and <c>credit-stop</c>. Basis: the lane-outcome record
/// contract in ci-workflows, and its <c>report-lane-outcome</c> action. Review
/// lanes on ci-workflows before the contract release name no credential, so
/// their stops are not seen.
/// </para>
/// </summary>
public static class LimitStopScan
{
    /// <summary>The most GitHub calls one scan makes. Basis: the issue's "about 30 API calls per click".</summary>
    public const int MaxCalls = 30;

    /// <summary>
    /// The GraphQL points a scan leaves unspent: it stops when a batch reports
    /// fewer remaining. Basis: the owner's answer, 20% of the 5,000-point
    /// hourly budget of a user.
    /// </summary>
    public const int GraphQlPointFloor = 1000;

    /// <summary>The runs one page of the listing holds, GitHub's most. Basis: https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-repository.</summary>
    public const int RunsPerPage = 100;

    /// <summary>
    /// The most runs the listing returns for one date range. Basis:
    /// https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-repository
    /// ("will return up to 1,000 results for each search" when using created, status and other filters).
    /// </summary>
    public const int ListingCap = 1000;

    /// <summary>The pages one date range of the listing yields.</summary>
    public const int PagesPerRange = ListingCap / RunsPerPage;

    /// <summary>
    /// Whether a scan read everything: no problem, and every repository read to
    /// the end of the window.
    /// </summary>
    public static bool Whole(int problems, IEnumerable<bool> complete) =>
        problems == 0 && complete.All(static done => done);

    /// <summary>
    /// How far back a scan looks. Basis: the owner's goal, every lane limit
    /// stop of the last 7 days seen before a role moves.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>
    /// The hits for <paramref name="account"/>: each record of a limit class
    /// whose credential is a half of a role that the run's repository draws on
    /// (the repository's organization for an organization role, the repository
    /// itself for a repository role) and that <paramref name="account"/> backed
    /// when the run started. One hit per run and half.
    /// </summary>
    /// <param name="history">The lane history, any order.</param>
    /// <param name="roster">Who backs each half now: the answer for a half the history never mentions.</param>
    public static IReadOnlyList<LimitStopHit> Hits(
        AccountEmail account,
        IEnumerable<LaneRecord> records,
        IReadOnlyList<RoleHalfChange> history,
        Roster roster)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(roster);
        List<RoleHalfChange> ordered = [.. history.OrderBy(static change => change.At)];
        List<CiRole> roles = [.. history.Select(static change => change.Role)
            .Concat(roster.Entries.SelectMany(static entry => entry.CiRoles).Select(static assignment => assignment.Role))
            .Distinct()];

        List<LimitStopHit> hits = [];
        foreach (LaneRecord record in records)
        {
            if (record.Outcome.Class is not string kind || !LaneOutcomes.LimitClasses.Contains(kind))
            {
                continue;
            }

            string credential = record.Outcome.Credential;
            foreach (CiRole role in roles.Where(role => DrawnOnBy(role, record.Run.Repository)))
            {
                RoleHalf? half = string.Equals(role.TokenSecretName, credential, StringComparison.OrdinalIgnoreCase) ? RoleHalf.Token
                    : string.Equals(role.KeySecretName, credential, StringComparison.OrdinalIgnoreCase) ? RoleHalf.Key
                    : null;
                if (half is RoleHalf found
                    && string.Equals(Backer(role, found, record.Run.CreatedAt, ordered, roster), account.Value, StringComparison.OrdinalIgnoreCase)
                    && !hits.Exists(hit => hit.Run == record.Run && hit.Role.Equals(role) && hit.Half == found))
                {
                    hits.Add(new LimitStopHit(record.Run, role, found, credential));
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
