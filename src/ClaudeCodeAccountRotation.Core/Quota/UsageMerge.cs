namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>
/// One bucket's winning figure and the snapshot instance it came from. The
/// source is the input instance, never a copy: a caller decides whether a row
/// names its own source by comparing it with the card's source by reference, and
/// a copy would make every row claim to come from somewhere else.
/// </summary>
public sealed record MergedLimit(UsageLimit Limit, UsageSnapshot Source);

/// <summary>
/// What every reader of one account's numbers reads: <paramref name="Rows"/> for
/// per-bucket attribution, <paramref name="Card"/> for the source and capture
/// time a card states, and <paramref name="Merged"/> as the single snapshot a
/// decision about the account keys on. All three come out of one merge, so what
/// the operator reads and what anything decides cannot disagree.
/// <paramref name="SessionlessRead"/> is the full usage read that lists no
/// 5-hour window, when nothing newer carries one: the account's plan has no
/// such window, so it is not applicable rather than unknown.
/// </summary>
public sealed record MergedUsage(
    UsageSnapshot Merged,
    UsageSnapshot Card,
    IReadOnlyList<MergedLimit> Rows,
    UsageSnapshot? SessionlessRead = null);

/// <summary>
/// Merges every source that has numbers for one account into one view of that
/// account.
/// <para>
/// The merge is per bucket, not per snapshot. The statusline tee carries two of
/// the buckets and an on-demand read carries three or more, so whichever of them
/// happened to be written last would otherwise blank the rows it does not know
/// about. Each bucket therefore comes from whichever source carrying that bucket
/// captured it last.
/// </para>
/// </summary>
public static class UsageMerge
{
    /// <summary>
    /// Null when there is nothing to merge, so a caller can tell "no numbers at
    /// all" from numbers that happen to be zero.
    /// </summary>
    public static MergedUsage? Merge(IReadOnlyList<UsageSnapshot> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            return null;
        }

        List<MergedLimit> rows = [];
        foreach (UsageSnapshot source in sources)
        {
            foreach (UsageLimit limit in source.Limits)
            {
                int existing = rows.FindIndex(row => SameBucket(row.Limit, limit));
                if (existing < 0)
                {
                    rows.Add(new MergedLimit(limit, source));
                }
                else if (source.CapturedAt > rows[existing].Source.CapturedAt)
                {
                    // Strict, so two sources captured at the same instant leave the
                    // first one's row where it is. An equal instant says nothing
                    // about which writer is later, and a rule that let the second
                    // one win would make the same two sources render differently
                    // depending on the order they were gathered in.
                    rows[existing] = new MergedLimit(limit, source);
                }
            }
        }

        // A full read lists every window the account's plan has, so its newest one
        // listing no 5-hour window says the plan has none (an Ultra plan). A
        // session figure some source took before that read is from a window that
        // no longer exists and is dropped; one taken after it is newer evidence
        // that the window does exist, and stands. The tee is not a full read: it
        // omits a window it has no figure for.
        UsageSnapshot? read = sources
            .Where(static source => source.Source is QuotaSource.OnDemandRefresh or QuotaSource.Cached)
            .MaxBy(static source => source.CapturedAt);
        UsageSnapshot? sessionless = null;
        if (read is not null && !read.Limits.Any(static limit => limit.Kind == LimitKind.Session))
        {
            _ = rows.RemoveAll(row => row.Limit.Kind == LimitKind.Session && row.Source.CapturedAt < read.CapturedAt);
            sessionless = rows.Exists(static row => row.Limit.Kind == LimitKind.Session) ? null : read;
        }

        // The card's own line names the newest source that actually contributed a
        // row, falling back to the newest source there is when none did: a tee
        // observation that carried no percentages at all is still an "as of".
        UsageSnapshot card = rows.Select(row => row.Source).MaxBy(source => source.CapturedAt)
            ?? sources.MaxBy(source => source.CapturedAt)!;

        return new MergedUsage(
            new UsageSnapshot(
                card.Account,
                card.CapturedAt,
                card.Source,
                [.. rows.Select(row => row.Limit)],
                // The newest source that has a credits block rather than the newest
                // source outright, for the reason the rows are merged per bucket:
                // the tee never carries one, and a tee write must not blank the line.
                sources.Where(source => source.ExtraUsage is not null).MaxBy(source => source.CapturedAt)?.ExtraUsage),
            card,
            rows,
            sessionless);
    }

    /// <summary>
    /// Whether two limits measure the same window. The two named buckets are
    /// identified by kind alone; a scoped or unrecognized one needs its raw kind
    /// and its display name too, because an account can hold several of either
    /// and merging them would let one model's figure overwrite another's.
    /// </summary>
    private static bool SameBucket(UsageLimit left, UsageLimit right) =>
        left.Kind == right.Kind
        && (left.Kind is LimitKind.Session or LimitKind.WeeklyAll
            || (string.Equals(left.RawKind, right.RawKind, StringComparison.Ordinal)
                && string.Equals(left.ScopeDisplayName, right.ScopeDisplayName, StringComparison.Ordinal)));
}
