using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>How close a figure is to running out: past its window's <see cref="AlertThresholds.WarnAt"/>, or past its <see cref="AlertThresholds.CriticalAt"/>.</summary>
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
/// <see cref="Kind"/> is the window's kind, which picks its thresholds.
/// </summary>
public sealed record UsageAlert(AccountEmail Account, AlertLevel Level, decimal Percent, string Window, DateTimeOffset? ResetsAt, LimitKind Kind)
{
    /// <summary>The threshold <see cref="Level"/> stands for on this window.</summary>
    public decimal Threshold => UsageAlerts.For(Kind).At(Level);
}

/// <summary>One window's warning and critical percentages.</summary>
public sealed record AlertThresholds(decimal WarnAt, decimal CriticalAt)
{
    /// <summary>The percentage <paramref name="level"/> stands for.</summary>
    public decimal At(AlertLevel level) => level == AlertLevel.Critical ? CriticalAt : WarnAt;

    /// <summary>Both, lowest first: where a meter marks them.</summary>
    public IReadOnlyList<decimal> Ascending => [WarnAt, CriticalAt];
}

/// <summary>
/// Which accounts are near the end of a window. Pure: it reads the figures it
/// is handed and decides only the level. An alert never acts.
/// </summary>
public static class UsageAlerts
{
    /// <summary>
    /// The 5-hour window's thresholds. Basis: lane-funding 24 h usage analysis,
    /// 2026-10-26: the steepest measured climb was about 22 points per
    /// 15-minute read on one account, so 70 to 90 left less than one read to act.
    /// </summary>
    public static AlertThresholds FiveHour { get; } = new(50m, 75m);

    /// <summary>The 7-day and every model's weekly window. Basis: the plan's acceptance criteria for #255, "warned at 70% and 90%".</summary>
    public static AlertThresholds Weekly { get; } = new(70m, 90m);

    /// <summary>The thresholds of a window of <paramref name="kind"/>.</summary>
    public static AlertThresholds For(LimitKind kind) => kind == LimitKind.Session ? FiveHour : Weekly;

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
                AlertThresholds thresholds = For(limit.Kind);
                if (percent >= thresholds.WarnAt)
                {
                    alerts.Add(new UsageAlert(entry.Email, percent >= thresholds.CriticalAt ? AlertLevel.Critical : AlertLevel.Warning, percent, limit.Label, limit.ResetsAt, limit.Kind));
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
