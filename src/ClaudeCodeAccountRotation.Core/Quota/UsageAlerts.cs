using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>How close a figure is to running out: past <see cref="UsageAlerts.WarnAt"/>, or past <see cref="UsageAlerts.CriticalAt"/>.</summary>
public enum AlertLevel
{
    Warning,
    Critical,
}

/// <summary>
/// One account's window past a threshold. <see cref="Window"/> is the window's
/// label (<see cref="UsageLimit.Label"/>): "5-hour", "7-day", or a model's
/// weekly window by the endpoint's own display name. <see cref="ResetsAt"/> is
/// when that window turns over, which is what tells one window from the next.
/// </summary>
public sealed record UsageAlert(AccountEmail Account, AlertLevel Level, decimal Percent, string Window, DateTimeOffset? ResetsAt)
{
    /// <summary>The threshold <see cref="Level"/> stands for.</summary>
    public decimal Threshold => Level == AlertLevel.Critical ? UsageAlerts.CriticalAt : UsageAlerts.WarnAt;
}

/// <summary>
/// Which accounts are near the end of a window. Pure: it reads the figures it
/// is handed and decides only the level. An alert never acts.
/// </summary>
public static class UsageAlerts
{
    /// <summary>Basis: the plan's acceptance criteria for #255, "warned at 70% and 90%".</summary>
    public const decimal WarnAt = 70m;

    /// <summary>Basis: as <see cref="WarnAt"/>.</summary>
    public const decimal CriticalAt = 90m;

    /// <summary>Every threshold, lowest first: where a meter marks them.</summary>
    public static IReadOnlyList<decimal> Thresholds { get; } = [WarnAt, CriticalAt];

    /// <summary>
    /// One alert per window of each account that <see cref="RosterEntry.BacksCiRole"/>,
    /// at the highest level its figure reached, critical first, then in roster
    /// order. The 5-hour, 7-day and every model's weekly window count; a window
    /// that has reset by <paramref name="now"/> counts as no figure, because the
    /// card blanks it. CI spends such an account's windows while nobody is
    /// watching its card.
    /// </summary>
    public static IReadOnlyList<UsageAlert> Evaluate(Roster roster, Func<AccountEmail, IReadOnlyList<UsageLimit>> limits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(limits);
        List<UsageAlert> alerts = [];
        foreach (RosterEntry entry in roster.Entries.Where(static entry => entry.BacksCiRole))
        {
            foreach (UsageLimit limit in Open(limits(entry.Email), now))
            {
                decimal percent = (decimal)limit.Percent;
                if (percent >= WarnAt)
                {
                    alerts.Add(new UsageAlert(entry.Email, percent >= CriticalAt ? AlertLevel.Critical : AlertLevel.Warning, percent, limit.Label, limit.ResetsAt));
                }
            }
        }

        return [.. alerts.OrderByDescending(static alert => alert.Level)];
    }

    /// <summary>
    /// The roster account, other than <paramref name="except"/>, with the most
    /// headroom: the lowest figure across its open windows. A paused account, one
    /// with no 5-hour or 7-day figure, and one with any window spent are never
    /// named, the same accounts the switch-now prompt never names. Ties go to
    /// roster order. Null when no account qualifies.
    /// </summary>
    public static AccountEmail? BestSpare(Roster roster, AccountEmail except, Func<AccountEmail, IReadOnlyList<UsageLimit>> limits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(limits);
        AccountEmail? best = null;
        double bestUsed = double.MaxValue;
        foreach (RosterEntry entry in roster.Entries.Where(entry => !entry.Paused && entry.Email != except))
        {
            List<UsageLimit> open = [.. Open(limits(entry.Email), now)];
            if (!open.Any(static limit => limit.Kind is LimitKind.Session or LimitKind.WeeklyAll)
                || open.Any(static limit => limit.Percent >= 100))
            {
                continue;
            }

            double used = open.Max(static limit => limit.Percent);
            if (used < bestUsed)
            {
                best = entry.Email;
                bestUsed = used;
            }
        }

        return best;
    }

    private static IEnumerable<UsageLimit> Open(IReadOnlyList<UsageLimit> limits, DateTimeOffset now) =>
        limits.Where(limit => (limit.Kind is LimitKind.Session or LimitKind.WeeklyAll or LimitKind.WeeklyScoped) && !limit.HasResetBy(now));
}
