using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Tests.Quota;

/// <summary>
/// Pins the one conversion that lets the tee's two windows travel the same road
/// as an on-demand read. The card model is driven by the generic limits array,
/// so a snapshot that stayed a shape of its own would need a second renderer and
/// a second set of rules for what "unknown" means.
/// </summary>
public sealed class UsageSnapshotTests
{
    private static readonly AccountEmail _account = new("dev.a@example.com");
    private static readonly DateTimeOffset _capturedAt = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ATeeSnapshotBecomesTwoLimitsSourcedFromTheStatusline()
    {
        StatuslineSnapshot tee = new(
            _capturedAt,
            SessionId: "session-1",
            Account: _account,
            FiveHourPercent: 69,
            FiveHourResetsAt: _capturedAt.AddHours(3),
            SevenDayPercent: 43,
            SevenDayResetsAt: _capturedAt.AddDays(4));

        var snapshot = UsageSnapshot.FromStatusline(tee, _account);

        snapshot.Account.ShouldBe(_account);
        snapshot.CapturedAt.ShouldBe(_capturedAt);
        snapshot.Source.ShouldBe(QuotaSource.StatuslineSnapshot);
        snapshot.ExtraUsage.ShouldBeNull("the tee carries no usage-credits block");
        snapshot.Limits.Count.ShouldBe(2);

        UsageLimit session = snapshot.Limits[0];
        session.RawKind.ShouldBe("session");
        session.Kind.ShouldBe(LimitKind.Session);
        session.Percent.ShouldBe(69);
        session.ResetsAt.ShouldBe(_capturedAt.AddHours(3));
        session.Group.ShouldBeNull();
        session.Severity.ShouldBeNull();
        session.ScopeDisplayName.ShouldBeNull();
        session.IsActive.ShouldBeTrue();

        UsageLimit weekly = snapshot.Limits[1];
        weekly.RawKind.ShouldBe("weekly_all");
        weekly.Kind.ShouldBe(LimitKind.WeeklyAll);
        weekly.Percent.ShouldBe(43);
        weekly.ResetsAt.ShouldBe(_capturedAt.AddDays(4));
    }

    [Fact]
    public void AWindowWithoutAPercentIsOmitted()
    {
        StatuslineSnapshot tee = new(
            _capturedAt,
            SessionId: null,
            Account: _account,
            FiveHourPercent: null,
            FiveHourResetsAt: null,
            SevenDayPercent: 43,
            SevenDayResetsAt: null);

        var snapshot = UsageSnapshot.FromStatusline(tee, _account);

        // The card renders a window no source carried as "unknown"; a zero here
        // would be a lie it could not tell apart from a genuinely idle window.
        snapshot.Limits.Count.ShouldBe(1);
        snapshot.Limits[0].Kind.ShouldBe(LimitKind.WeeklyAll);
        snapshot.Limits[0].ResetsAt.ShouldBeNull();
    }
}
