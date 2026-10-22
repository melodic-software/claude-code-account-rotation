using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>How close a figure is to running out: past <see cref="UsageAlerts.WarnAt"/>, or past <see cref="UsageAlerts.CriticalAt"/>.</summary>
public enum AlertLevel
{
    Warning70,
    Critical90,
}

/// <summary>One account's figure past a threshold. <paramref name="Figure"/> is <see cref="UsageAlerts.SevenDay"/> or <see cref="UsageAlerts.OrgCredit"/>.</summary>
public sealed record UsageAlert(AccountEmail Account, string Figure, AlertLevel Level, decimal Percent);

/// <summary>
/// Which accounts are near running out. Pure: it reads figures it is handed and
/// decides nothing beyond the level. An alert never acts; the page shows it and
/// the operator decides whether to move a role.
/// </summary>
public static class UsageAlerts
{
    /// <summary>Basis: the plan's acceptance criteria for #255, "warned at 70% and 90%".</summary>
    public const decimal WarnAt = 70m;

    /// <summary>Basis: as <see cref="WarnAt"/>.</summary>
    public const decimal CriticalAt = 90m;

    /// <summary>The 7-day all-models window.</summary>
    public const string SevenDay = "sevenDay";

    /// <summary>The organization's usage against its plan's included credit.</summary>
    public const string OrgCredit = "orgCredit";

    /// <summary>
    /// One alert per account and figure at the highest level reached, critical
    /// first, then in roster order. The 7-day figure is judged only for an
    /// account that backs a CI role: CI spends that account's window while
    /// nobody is watching it. The org figure is judged for every account the
    /// function answers for, so the caller decides who has one. A null figure
    /// raises nothing.
    /// </summary>
    public static IReadOnlyList<UsageAlert> Evaluate(
        Roster roster,
        Func<AccountEmail, decimal?> sevenDayPercent,
        Func<AccountEmail, decimal?> orgCreditPercent)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(sevenDayPercent);
        ArgumentNullException.ThrowIfNull(orgCreditPercent);
        List<UsageAlert> alerts = [];
        foreach (RosterEntry entry in roster.Entries)
        {
            if (!entry.CiRoles.IsEmpty)
            {
                Add(alerts, entry.Email, SevenDay, sevenDayPercent(entry.Email));
            }

            Add(alerts, entry.Email, OrgCredit, orgCreditPercent(entry.Email));
        }

        return [.. alerts.OrderByDescending(static alert => alert.Level)];
    }

    private static void Add(List<UsageAlert> alerts, AccountEmail account, string figure, decimal? percent)
    {
        if (percent is decimal value && value >= WarnAt)
        {
            alerts.Add(new UsageAlert(account, figure, value >= CriticalAt ? AlertLevel.Critical90 : AlertLevel.Warning70, value));
        }
    }
}
