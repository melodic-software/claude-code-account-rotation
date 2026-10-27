using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Dashboard;

/// <summary>
/// What <c>POST /api/refresh/ci-roles?wait=true</c> answers once the pass it
/// started has ended, or once the wait has run out with the pass still going:
/// <see cref="Finished"/> says which. One row per roster entry backing a CI
/// role, the same filter the pass reads by.
/// </summary>
internal sealed record CiUsageReport(bool Finished, IReadOnlyList<CiUsageRow> Accounts)
{
    /// <summary>
    /// The rows, built from the roster and the figures the pass left behind.
    /// The usage is the card's own (<see cref="DashboardAssembler.Usage"/>), so
    /// the table and the page cannot show an account's figures two different
    /// ways. The read column is the last outcome alone: a stranded folder,
    /// which the card names instead, is the page's to show.
    /// </summary>
    public static CiUsageReport For(bool finished, Roster roster, QuotaState quota, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(quota);
        return new CiUsageReport(finished, [.. roster.Entries
            .Select(static (entry, index) => (entry, index))
            .Where(static row => row.entry.BacksCiRole)
            .Select(row => new CiUsageRow(
                row.index,
                [.. row.entry.CiRoles.Select(static assignment => assignment.Role.Name)],
                row.entry.Email.Value,
                DashboardAssembler.Refresh(quota.OutcomeFor(row.entry.Email)),
                DashboardAssembler.Usage(quota.MergedFor(row.entry.Email), capturedAt)))]);
    }
}

/// <summary>
/// One CI-role account: its position in <c>roster.json</c>'s <c>accounts</c>
/// array, the prefixes of the roles it backs, its e-mail, how its turn in the
/// last pass went, and its usage as its card shows it.
/// </summary>
internal sealed record CiUsageRow(int Index, IReadOnlyList<string> Roles, string Email, RefreshStateView Refresh, UsageView Usage);
