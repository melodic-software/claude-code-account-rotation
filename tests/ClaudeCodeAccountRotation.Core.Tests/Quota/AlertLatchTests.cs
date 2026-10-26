using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Tests.Quota;

/// <summary>
/// The owner's rule: each threshold fires once per window reset, not on every
/// fifteen-minute read. A window is told apart from the next one by its reset
/// instant having passed.
/// </summary>
public sealed class AlertLatchTests
{
    private static readonly AccountEmail _ci = new("ci@example.com");
    private static readonly DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _reset = _now.AddDays(3);

    private static UsageAlert Alert(AlertLevel level, string window = "7-day", DateTimeOffset? resets = null) =>
        new(_ci, level, level == AlertLevel.Critical ? 92m : 72m, window, resets ?? _reset, window == "5-hour" ? LimitKind.Session : LimitKind.WeeklyAll);

    [Fact]
    public void AThresholdFiresOnceAndNotAgainOnTheNextRead()
    {
        AlertLatch latch = new();

        latch.Admit([Alert(AlertLevel.Warning)], _now).Count.ShouldBe(1);
        latch.Admit([Alert(AlertLevel.Warning)], _now.AddMinutes(15)).ShouldBeEmpty();
    }

    [Fact]
    public void CrossingNinetyAfterSeventyFiresTheCriticalOnce()
    {
        AlertLatch latch = new();
        latch.Admit([Alert(AlertLevel.Warning)], _now);

        latch.Admit([Alert(AlertLevel.Critical)], _now.AddMinutes(15)).Single().Level.ShouldBe(AlertLevel.Critical);
        latch.Admit([Alert(AlertLevel.Critical)], _now.AddMinutes(30)).ShouldBeEmpty();
    }

    [Fact]
    public void AJumpStraightToNinetyCoversSeventyToo()
    {
        AlertLatch latch = new();
        latch.Admit([Alert(AlertLevel.Critical)], _now);

        latch.Admit([Alert(AlertLevel.Warning)], _now.AddMinutes(15)).ShouldBeEmpty();
    }

    [Fact]
    public void TheNextWindowFiresAgainOnceTheLatchedResetHasPassed()
    {
        AlertLatch latch = new();
        latch.Admit([Alert(AlertLevel.Warning)], _now);

        DateTimeOffset later = _reset.AddHours(1);
        latch.Admit([Alert(AlertLevel.Warning, resets: _reset.AddDays(7))], later).Count.ShouldBe(1);
    }

    [Fact]
    public void EachWindowLatchesOnItsOwn()
    {
        AlertLatch latch = new();
        latch.Admit([Alert(AlertLevel.Warning)], _now);

        latch.Admit([Alert(AlertLevel.Warning), Alert(AlertLevel.Warning, "5-hour", _now.AddHours(2))], _now.AddMinutes(15))
            .Single().Window.ShouldBe("5-hour");
    }

    [Fact]
    public void TheLatchSurvivesARoundTripThroughItsEntries()
    {
        AlertLatch latch = new();
        latch.Admit([Alert(AlertLevel.Warning)], _now);

        AlertLatch restored = new(latch.Entries);

        restored.Admit([Alert(AlertLevel.Warning)], _now.AddMinutes(15)).ShouldBeEmpty();
        restored.Admit([Alert(AlertLevel.Critical)], _now.AddMinutes(15)).Count.ShouldBe(1);
    }
}
