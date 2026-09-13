using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>Where a card's numbers came from, rendered as "via snapshot", "via refresh", "cached".</summary>
public enum QuotaSource
{
    StatuslineSnapshot,
    OnDemandRefresh,
    Cached,
}

/// <summary>
/// One account's quota as of one moment, from whichever source produced it.
/// Every card states its source and its capture time (AC 4).
/// </summary>
public sealed record UsageSnapshot(
    AccountEmail Account,
    DateTimeOffset CapturedAt,
    QuotaSource Source,
    IReadOnlyList<UsageLimit> Limits,
    ExtraUsageState? ExtraUsage)
{
    /// <summary>
    /// Turns one tee observation into the same shape an on-demand read produces,
    /// so the card renders both through the generic <c>limits[]</c> array instead
    /// of carrying a second view for the free tier. The raw kinds match the usage
    /// endpoint's own words (<c>session</c>, <c>weekly_all</c>) because the merge
    /// that picks the newest source per bucket compares kinds, not origins.
    /// <para>
    /// A window the tee carries no percentage for is omitted rather than emitted
    /// as zero: the card must be able to say "unknown", and a zero is
    /// indistinguishable from a genuinely untouched window. <paramref name="account"/>
    /// is passed in rather than taken from the snapshot because the caller is
    /// what decides whose card this observation belongs to, and the tee's own
    /// attribution is absent on an older writer.
    /// </para>
    /// </summary>
    public static UsageSnapshot FromStatusline(StatuslineSnapshot snapshot, AccountEmail account)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<UsageLimit> limits = [];
        if (snapshot.FiveHourPercent is double fiveHour)
        {
            limits.Add(new UsageLimit("session", LimitKind.Session, Group: null, fiveHour, Severity: null, snapshot.FiveHourResetsAt, ScopeDisplayName: null, IsActive: true));
        }

        if (snapshot.SevenDayPercent is double sevenDay)
        {
            limits.Add(new UsageLimit("weekly_all", LimitKind.WeeklyAll, Group: null, sevenDay, Severity: null, snapshot.SevenDayResetsAt, ScopeDisplayName: null, IsActive: true));
        }

        return new UsageSnapshot(account, snapshot.CapturedAt, QuotaSource.StatuslineSnapshot, limits, ExtraUsage: null);
    }
}
