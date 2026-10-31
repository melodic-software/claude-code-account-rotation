using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Tests.Quota;

/// <summary>
/// Pins the rule that decides which figure a card shows for each window. Two
/// sources carry overlapping but unequal sets of buckets, so a merge that picked
/// a whole snapshot instead of merging per bucket would blank whatever the
/// newest writer happens not to know about; and a card whose rows came from one
/// rule while a decision about that account came from another could show the
/// operator a number nothing acted on.
/// </summary>
public sealed class UsageMergeTests
{
    private static readonly DateTimeOffset _noon = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly AccountEmail _account = new("dev.a@example.com");

    private static UsageLimit Session(double percent) =>
        new("session", LimitKind.Session, Group: null, percent, Severity: null, ResetsAt: null, ScopeDisplayName: null, IsActive: true);

    private static UsageLimit Weekly(double percent) =>
        new("weekly_all", LimitKind.WeeklyAll, Group: null, percent, Severity: null, ResetsAt: null, ScopeDisplayName: null, IsActive: true);

    private static UsageSnapshot Snapshot(DateTimeOffset capturedAt, QuotaSource source, params UsageLimit[] limits) =>
        new(_account, capturedAt, source, limits, ExtraUsage: null);

    private static UsageLimit Row(MergedUsage merged, LimitKind kind) =>
        merged.Rows.Single(row => row.Limit.Kind == kind).Limit;

    [Fact]
    public void TheNewestSourceWinsPerBucket()
    {
        MergedUsage? merged = UsageMerge.Merge(
        [
            Snapshot(_noon.AddMinutes(-30), QuotaSource.Cached, Session(10)),
            Snapshot(_noon, QuotaSource.StatuslineSnapshot, Session(80)),
        ]);

        Row(merged.ShouldNotBeNull(), LimitKind.Session).Percent.ShouldBe(80);
    }

    [Fact]
    public void ASourceThatCarriesFewerBucketsDoesNotBlankTheOthers()
    {
        // The tee carries two buckets and an on-demand read carries three or
        // more, so the newest writer is routinely the one that knows less.
        UsageSnapshot read = new(
            _account,
            _noon.AddMinutes(-30),
            QuotaSource.OnDemandRefresh,
            [Session(10), Weekly(42)],
            new ExtraUsageState(IsEnabled: true, DisabledReason: null, SpendLimitReached: false));

        MergedUsage merged = UsageMerge.Merge([read, Snapshot(_noon, QuotaSource.StatuslineSnapshot, Session(80))]).ShouldNotBeNull();

        Row(merged, LimitKind.WeeklyAll).Percent.ShouldBe(42);
        merged.Rows.Single(row => row.Limit.Kind == LimitKind.WeeklyAll).Source.ShouldBeSameAs(read);
        merged.Merged.ExtraUsage.ShouldBe(read.ExtraUsage);
    }

    [Fact]
    public void TheMergedSnapshotNamesTheNewestContributingSource()
    {
        // A tee observation that carried no percentages at all contributes no
        // row, so it cannot be what the card's "as of" line names.
        UsageSnapshot read = Snapshot(_noon.AddMinutes(-30), QuotaSource.OnDemandRefresh, Session(10));

        MergedUsage merged = UsageMerge.Merge([read, Snapshot(_noon, QuotaSource.StatuslineSnapshot)]).ShouldNotBeNull();

        merged.Card.ShouldBeSameAs(read);
        merged.Merged.CapturedAt.ShouldBe(read.CapturedAt);
        merged.Merged.Source.ShouldBe(QuotaSource.OnDemandRefresh);
    }

    [Fact]
    public void NoSourcesMergeToNothing()
    {
        UsageMerge.Merge([]).ShouldBeNull();
    }

    [Fact]
    public void ASourceWithTheSameCaptureInstantDoesNotStealTheRow()
    {
        // An equal capture instant says nothing about which writer is later, so
        // the same two sources must merge the same way whichever order they were
        // gathered in; letting the second one win would make that untrue.
        UsageSnapshot first = Snapshot(_noon, QuotaSource.OnDemandRefresh, Session(10));

        MergedUsage merged = UsageMerge.Merge([first, Snapshot(_noon, QuotaSource.StatuslineSnapshot, Session(80))]).ShouldNotBeNull();

        Row(merged, LimitKind.Session).Percent.ShouldBe(10);
        merged.Card.ShouldBeSameAs(first);
        merged.SessionlessRead.ShouldBeNull();
    }

    [Fact]
    public void AFullReadWithNoFiveHourWindowDropsAnOlderSessionRow()
    {
        // An account whose plan has no 5-hour window (Ultra): the endpoint lists
        // only the weekly rows. A session figure some other source took before
        // that read must not bring the window back, or the account would be
        // judged on a window it does not have.
        UsageSnapshot read = Snapshot(_noon, QuotaSource.OnDemandRefresh, Weekly(0));

        MergedUsage merged = UsageMerge.Merge([Snapshot(_noon.AddMinutes(-10), QuotaSource.StatuslineSnapshot, Session(100)), read]).ShouldNotBeNull();

        merged.Rows.ShouldNotContain(row => row.Limit.Kind == LimitKind.Session);
        merged.Merged.Limits.ShouldNotContain(limit => limit.Kind == LimitKind.Session);
        merged.SessionlessRead.ShouldBeSameAs(read);
    }

    [Fact]
    public void ACachedReadWithNoFiveHourWindowSaysNothingAboutThePlan()
    {
        // The cache file may hold a CLI read from an earlier release that left
        // out a 5-hour window it could not read, so a cached read without one is
        // not evidence the plan has none: the window stays unknown, and an older
        // spent figure stays, until a live read says otherwise.
        UsageSnapshot statusline = Snapshot(_noon.AddMinutes(-10), QuotaSource.StatuslineSnapshot, Session(100));

        MergedUsage merged = UsageMerge.Merge([statusline, Snapshot(_noon, QuotaSource.Cached, Weekly(0))]).ShouldNotBeNull();

        Row(merged, LimitKind.Session).Percent.ShouldBe(100);
        merged.Rows.Single(row => row.Limit.Kind == LimitKind.Session).Source.ShouldBeSameAs(statusline);
        merged.SessionlessRead.ShouldBeNull();
    }

    [Fact]
    public void ASessionRowTakenAfterTheSessionlessReadStands()
    {
        // A newer source that does carry the 5-hour window is newer evidence
        // that the window exists, the way every bucket goes to its newest source.
        UsageSnapshot read = Snapshot(_noon.AddMinutes(-10), QuotaSource.OnDemandRefresh, Weekly(0));

        MergedUsage merged = UsageMerge.Merge([Snapshot(_noon, QuotaSource.StatuslineSnapshot, Session(80)), read]).ShouldNotBeNull();

        Row(merged, LimitKind.Session).Percent.ShouldBe(80);
        merged.SessionlessRead.ShouldBeNull();
    }

    [Fact]
    public void AStatuslineSnapshotWithNoFiveHourWindowSaysNothingAboutThePlan()
    {
        // The tee omits a window it has no figure for, so its silence is not
        // the endpoint saying the window does not exist.
        MergedUsage merged = UsageMerge.Merge([Snapshot(_noon, QuotaSource.StatuslineSnapshot, Weekly(10))]).ShouldNotBeNull();

        merged.SessionlessRead.ShouldBeNull();
    }
}
