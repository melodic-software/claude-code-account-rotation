using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Tests.Quota;

/// <summary>
/// The 70% and 90% thresholds come from the plan's acceptance criteria (#255):
/// an account backing a CI role is warned at 70% of its 7-day window and
/// critically at 90%. Every expected value below is one of those two literals or
/// a figure on either side of them.
/// </summary>
public sealed class UsageAlertsTests
{
    private static readonly AccountEmail _ci = new("ci@example.com");
    private static readonly AccountEmail _plain = new("plain@example.com");
    private static readonly CiRole _automations = new("AUTOMATIONS", CiSecretScope.Organization, "octo");

    private static Roster Roster() => new(
    [
        new RosterEntry(_ci).ClaimingToken(_automations, new DateOnly(2026, 10, 1), setAt: null),
        new RosterEntry(_plain),
    ]);

    private static IReadOnlyList<UsageAlert> SevenDay(decimal? ci, decimal? plain) =>
        UsageAlerts.Evaluate(Roster(), email => email == _ci ? ci : plain);

    [Fact]
    public void JustUnderSeventyRaisesNothing() => SevenDay(69.9m, null).ShouldBeEmpty();

    [Fact]
    public void SeventyIsAWarning()
    {
        UsageAlert alert = SevenDay(70m, null).Single();

        alert.ShouldBe(new UsageAlert(_ci, AlertLevel.Warning, 70m));
        alert.Threshold.ShouldBe(70m);
    }

    [Fact]
    public void NinetyIsOneCriticalAlertNotTwo()
    {
        UsageAlert alert = SevenDay(90m, null).Single();

        alert.ShouldBe(new UsageAlert(_ci, AlertLevel.Critical, 90m));
        alert.Threshold.ShouldBe(90m);
    }

    [Fact]
    public void JustUnderNinetyIsStillAWarning() =>
        SevenDay(89.9m, null).Single().Level.ShouldBe(AlertLevel.Warning);

    [Fact]
    public void AnAccountBackingNoCiRoleRaisesNothing() => SevenDay(null, 95m).ShouldBeEmpty();

    [Fact]
    public void AMissingFigureRaisesNothing() => SevenDay(null, null).ShouldBeEmpty();

    [Fact]
    public void AHandSetTokenDateWithNoRoleIsNotACiRole()
    {
        Roster roster = new([new RosterEntry(_plain, CiTokenGeneratedOn: new DateOnly(2026, 10, 1))]);

        UsageAlerts.Evaluate(roster, static _ => 95m).ShouldBeEmpty();
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

        IReadOnlyList<UsageAlert> alerts = UsageAlerts.Evaluate(roster, email => email == _ci ? 75m : 95m);

        alerts.Select(static alert => alert.Account.Value).ShouldBe(["z@example.com", "ci@example.com"]);
    }
}
