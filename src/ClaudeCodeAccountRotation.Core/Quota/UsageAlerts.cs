using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>How close a figure is to running out: past <see cref="UsageAlerts.WarnAt"/>, or past <see cref="UsageAlerts.CriticalAt"/>.</summary>
public enum AlertLevel
{
    Warning,
    Critical,
}

/// <summary>One account's 7-day figure past a threshold.</summary>
public sealed record UsageAlert(AccountEmail Account, AlertLevel Level, decimal Percent)
{
    /// <summary>The threshold <see cref="Level"/> stands for.</summary>
    public decimal Threshold => Level == AlertLevel.Critical ? UsageAlerts.CriticalAt : UsageAlerts.WarnAt;
}

/// <summary>
/// Which accounts are near the end of their 7-day window. Pure: it reads the
/// figures it is handed and decides only the level. An alert never acts.
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
    /// One alert per account that <see cref="RosterEntry.BacksCiRole"/>, at the
    /// highest level its figure reached, critical first, then in roster order.
    /// CI spends such an account's window while nobody is watching its card. A
    /// null figure raises nothing.
    /// </summary>
    public static IReadOnlyList<UsageAlert> Evaluate(Roster roster, Func<AccountEmail, decimal?> sevenDayPercent)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(sevenDayPercent);
        List<UsageAlert> alerts = [];
        foreach (RosterEntry entry in roster.Entries.Where(static entry => entry.BacksCiRole))
        {
            if (sevenDayPercent(entry.Email) is decimal percent && percent >= WarnAt)
            {
                alerts.Add(new UsageAlert(entry.Email, percent >= CriticalAt ? AlertLevel.Critical : AlertLevel.Warning, percent));
            }
        }

        return [.. alerts.OrderByDescending(static alert => alert.Level)];
    }
}
