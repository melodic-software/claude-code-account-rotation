using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Tests.Quota;

public sealed class RefreshBudgetTests
{
    private static readonly AccountEmail _accountA = new("dev.a@example.com");
    private static readonly AccountEmail _accountB = new("dev.b@example.com");

    [Fact]
    public void TheSeventhReserveInsideTheWindowIsDenied()
    {
        TestClock clock = new(DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        // The gap is set aside here so the window rule is what the test measures;
        // under the shipped defaults the 60-second gap binds first.
        RefreshBudget budget = new(clock, minimumGapSinceLastRead: TimeSpan.Zero);

        for (int read = 0; read < 6; read++)
        {
            budget.TryReserve(_accountA).ShouldBeTrue("reserve " + read.ToString(System.Globalization.CultureInfo.InvariantCulture) + " should be allowed");
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        budget.TryReserve(_accountA).ShouldBeFalse();
    }

    [Fact]
    public void TheWindowSlidesSoTheOldestReadStopsCounting()
    {
        TestClock clock = new(DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        RefreshBudget budget = new(clock, minimumGapSinceLastRead: TimeSpan.Zero);
        for (int read = 0; read < 6; read++)
        {
            budget.TryReserve(_accountA);
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        budget.TryReserve(_accountA).ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(5));

        budget.TryReserve(_accountA).ShouldBeTrue();
    }

    [Fact]
    public void AReserveWithinSixtySecondsOfTheLastReadIsDenied()
    {
        TestClock clock = new(DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        RefreshBudget budget = new(clock);

        budget.TryReserve(_accountA).ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(59));
        budget.TryReserve(_accountA).ShouldBeFalse();

        clock.Advance(TimeSpan.FromSeconds(2));
        budget.TryReserve(_accountA).ShouldBeTrue();
    }

    [Fact]
    public void UnauthorizedResponseDoesNotStartTheGapClock()
    {
        TestClock clock = new(DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        RefreshBudget budget = new(clock);

        budget.TryReserve(_accountA).ShouldBeTrue();
        budget.RecordUnauthorized(_accountA);
        clock.Advance(TimeSpan.FromSeconds(2));

        // The retry after the credential refresh rides the original reservation.
        budget.TryReserve(_accountA).ShouldBeTrue();
    }

    [Fact]
    public void ALoopOfUnauthorizedResponsesStillRunsOutOfTheWindow()
    {
        // A refund costs no gap, but it is remembered: a token the endpoint keeps
        // rejecting must not buy an unbounded run of requests against it.
        TestClock clock = new(DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        RefreshBudget budget = new(clock, minimumGapSinceLastRead: TimeSpan.Zero);

        for (int attempt = 0; attempt < 6; attempt++)
        {
            budget.TryReserve(_accountA).ShouldBeTrue("attempt " + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture) + " should be allowed");
            budget.RecordUnauthorized(_accountA);
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        budget.TryReserve(_accountA).ShouldBeFalse();

        // And the window still slides: the refunds age out with the reads.
        clock.Advance(TimeSpan.FromMinutes(5));
        budget.TryReserve(_accountA).ShouldBeTrue();
    }

    [Fact]
    public void TheRetryAfterACredentialRefreshIsAllowedWithoutWaitingTheGap()
    {
        TestClock clock = new(DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        RefreshBudget budget = new(clock);

        // Six accounts' worth of the window is not spent by one 401 and its retry.
        budget.TryReserve(_accountA).ShouldBeTrue();
        budget.RecordUnauthorized(_accountA);
        clock.Advance(TimeSpan.FromSeconds(2));
        budget.TryReserve(_accountA).ShouldBeTrue();

        clock.Advance(TimeSpan.FromSeconds(61));
        budget.TryReserve(_accountA).ShouldBeTrue();
    }

    [Fact]
    public void ALockoutRefusesEveryReserveUntilRetryAfterHasPassed()
    {
        TestClock clock = new(DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        RefreshBudget budget = new(clock, minimumGapSinceLastRead: TimeSpan.Zero);

        budget.RecordLockout(_accountA, TimeSpan.FromSeconds(300));

        budget.LockedOutFor(_accountA)!.Value.ShouldBe(TimeSpan.FromSeconds(300));
        budget.TryReserve(_accountA).ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(299));
        budget.TryReserve(_accountA).ShouldBeFalse();

        clock.Advance(TimeSpan.FromSeconds(2));
        budget.LockedOutFor(_accountA).ShouldBeNull();
        budget.TryReserve(_accountA).ShouldBeTrue();
    }

    [Fact]
    public void EachAccountCarriesItsOwnBudget()
    {
        TestClock clock = new(DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        RefreshBudget budget = new(clock);

        budget.TryReserve(_accountA).ShouldBeTrue();
        budget.RecordLockout(_accountA, TimeSpan.FromSeconds(300));

        budget.TryReserve(_accountB).ShouldBeTrue();
        budget.LockedOutFor(_accountB).ShouldBeNull();
    }

    [Fact]
    public void TheGapRemainingCountsDownFromTheLastReadAndThenGoesAway()
    {
        TestClock clock = new(DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        RefreshBudget budget = new(clock);

        // Nothing to wait for before the account has ever been read, which is what
        // lets a card that was never read say "unknown" rather than "read 0 s ago".
        budget.GapRemaining(_accountA).ShouldBeNull();

        budget.TryReserve(_accountA).ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(10));

        budget.GapRemaining(_accountA)!.Value.ShouldBe(TimeSpan.FromSeconds(50));

        // Past the gap it agrees with TryReserve, so the card never counts down
        // against a read the budget would already allow.
        clock.Advance(TimeSpan.FromSeconds(51));
        budget.GapRemaining(_accountA).ShouldBeNull();
    }
}
