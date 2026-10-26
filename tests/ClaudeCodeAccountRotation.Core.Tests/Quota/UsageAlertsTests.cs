using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Tests.Quota;

/// <summary>
/// The 70% and 90% thresholds come from the plan's acceptance criteria (#255),
/// and the three windows (5-hour, 7-day, each model's weekly) from the owner's
/// decision on the usage alerts: an account backing a CI role is warned at 70%
/// of any of them and critically at 90%. Every expected value below is one of
/// those two literals, a figure on either side of them, or a window label the
/// usage endpoint's own <c>kind</c> or display name stands for.
/// </summary>
public sealed class UsageAlertsTests
{
    private static readonly AccountEmail _ci = new("ci@example.com");
    private static readonly AccountEmail _plain = new("plain@example.com");
    private static readonly CiRole _automations = new("AUTOMATIONS", CiSecretScope.Organization, "octo");
    private static readonly DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _weekEnd = _now.AddDays(3);

    private static Roster Roster() => new(
    [
        new RosterEntry(_ci).ClaimingToken(_automations, new DateOnly(2026, 10, 1), setAt: null),
        new RosterEntry(_plain),
    ]);

    private static UsageLimit Weekly(double percent, DateTimeOffset? resets = null) =>
        new("weekly_all", LimitKind.WeeklyAll, "weekly", percent, null, resets ?? _weekEnd, null, IsActive: true);

    private static UsageLimit Session(double percent) =>
        new("session", LimitKind.Session, "session", percent, null, _now.AddHours(2), null, IsActive: true);

    private static UsageLimit Scoped(double percent, string model) =>
        new("weekly_scoped", LimitKind.WeeklyScoped, "weekly", percent, null, _weekEnd, model, IsActive: true);

    private static IReadOnlyList<UsageAlert> Evaluate(Roster roster, Func<AccountEmail, IReadOnlyList<UsageLimit>> limits) =>
        UsageAlerts.Evaluate(roster, limits, _now);

    private static IReadOnlyList<UsageAlert> SevenDay(double ci, double plain) =>
        Evaluate(Roster(), email => email == _ci ? [Weekly(ci)] : [Weekly(plain)]);

    [Fact]
    public void JustUnderSeventyRaisesNothing() => SevenDay(69.9, 0).ShouldBeEmpty();

    [Fact]
    public void SeventyIsAWarningOnTheSevenDayWindow()
    {
        UsageAlert alert = SevenDay(70, 0).Single();

        alert.ShouldBe(new UsageAlert(_ci, AlertLevel.Warning, 70m, "7-day", _weekEnd));
        alert.Threshold.ShouldBe(70m);
    }

    [Fact]
    public void NinetyIsOneCriticalAlertNotTwo()
    {
        UsageAlert alert = SevenDay(90, 0).Single();

        alert.Level.ShouldBe(AlertLevel.Critical);
        alert.Threshold.ShouldBe(90m);
    }

    [Fact]
    public void JustUnderNinetyIsStillAWarning() =>
        SevenDay(89.9, 0).Single().Level.ShouldBe(AlertLevel.Warning);

    [Fact]
    public void AnAccountBackingNoCiRoleRaisesNothing() => SevenDay(0, 95).ShouldBeEmpty();

    [Fact]
    public void TheFiveHourWindowAlertsToo() =>
        Evaluate(Roster(), email => email == _ci ? [Session(91)] : []).Single()
            .ShouldBe(new UsageAlert(_ci, AlertLevel.Critical, 91m, "5-hour", _now.AddHours(2)));

    [Fact]
    public void EachModelsWeeklyWindowAlertsUnderItsOwnName()
    {
        IReadOnlyList<UsageAlert> alerts = Evaluate(Roster(), email => email == _ci ? [Scoped(75, "Fable"), Scoped(10, "Other")] : []);

        alerts.Single().Window.ShouldBe("Fable");
    }

    [Fact]
    public void AWindowThatHasResetRaisesNothing() =>
        Evaluate(Roster(), email => email == _ci ? [Weekly(95, _now.AddMinutes(-1))] : []).ShouldBeEmpty();

    [Fact]
    public void AHandSetTokenDateWithNoRoleIsNotACiRole()
    {
        Roster roster = new([new RosterEntry(_plain, CiTokenGeneratedOn: new DateOnly(2026, 10, 1))]);

        Evaluate(roster, static _ => [Weekly(95)]).ShouldBeEmpty();
    }

    [Fact]
    public void CriticalAlertsComeBeforeWarnings()
    {
        Roster roster = new(
        [
            new RosterEntry(_ci).ClaimingToken(_automations, new DateOnly(2026, 10, 1), setAt: null),
            new RosterEntry(new AccountEmail("z@example.com")).ClaimingToken(
                new CiRole("REVIEWS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 10, 1), setAt: null),
        ]);

        IReadOnlyList<UsageAlert> alerts = Evaluate(roster, email => email == _ci ? [Weekly(75)] : [Weekly(95)]);

        alerts.Select(static alert => alert.Account.Value).ShouldBe(["z@example.com", "ci@example.com"]);
    }

    [Fact]
    public void TheBestSpareIsTheOtherAccountWithTheMostHeadroom()
    {
        AccountEmail low = new("low@example.com");
        AccountEmail high = new("high@example.com");
        Roster roster = new([new RosterEntry(_ci), new RosterEntry(low), new RosterEntry(high)]);
        Dictionary<AccountEmail, IReadOnlyList<UsageLimit>> limits = new()
        {
            [_ci] = [Weekly(5)],
            [low] = [Weekly(60), Session(10)],
            [high] = [Weekly(20), Session(30)],
        };

        // ci has the most headroom, but it is the account being warned about;
        // high's binding window is 30% used, low's 60%.
        UsageAlerts.BestSpare(roster, _ci, email => limits[email], _now).ShouldBe(high);
    }

    [Fact]
    public void ASpareIsNeverPausedSpentOrUnread()
    {
        AccountEmail paused = new("paused@example.com");
        AccountEmail fableSpent = new("fable@example.com");
        AccountEmail unread = new("unread@example.com");
        AccountEmail busy = new("busy@example.com");
        Roster roster = new(
        [
            new RosterEntry(_ci),
            new RosterEntry(paused, Paused: true),
            new RosterEntry(fableSpent),
            new RosterEntry(unread),
            new RosterEntry(busy),
        ]);
        Dictionary<AccountEmail, IReadOnlyList<UsageLimit>> limits = new()
        {
            [_ci] = [Weekly(95)],
            [paused] = [Weekly(0)],
            [fableSpent] = [Weekly(0), Scoped(100, "Fable")],
            [unread] = [],
            [busy] = [Weekly(80)],
        };

        UsageAlerts.BestSpare(roster, _ci, email => limits[email], _now).ShouldBe(busy);
    }

    [Fact]
    public void NoSpareWhenNoOtherAccountQualifies() =>
        UsageAlerts.BestSpare(Roster(), _ci, static _ => [], _now).ShouldBeNull();
}
