using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

/// <summary>
/// The limit-stop warning's matching, held to the plan: a rate-limit
/// annotation naming a half the account backed when the run started is a hit,
/// labelled by half, and the scan reads at most 30 runs of the last 7 days.
/// </summary>
public sealed class LimitStopScanTests
{
    private const string A = "a@example.com";
    private const string B = "b@example.com";

    private static readonly DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly CiRole _automations = new("AUTOMATIONS", CiSecretScope.Organization, "octo");
    private static readonly LaneRun _run = new("octo/lanes", 101, new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero), null);

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    /// <summary>The lane record's shape from ci-workflows report-lane-outcome: key=value tokens in the message.</summary>
    private static LaneAnnotation RateLimit(string credential, LaneRun? run = null) =>
        new(run ?? _run, "lane=intake class=rate-limit credential=" + credential + " resets=2026-10-18T14:00:00Z");

    private static RoleHalfChange Change(DateTimeOffset at, RoleHalf half, string? account, string? previous, CiRole? role = null) =>
        new(at, role ?? _automations, half, account, previous);

    private static IReadOnlyList<LimitStopHit> HitsFor(string account, LaneAnnotation annotation, IReadOnlyList<RoleHalfChange> history, Roster? roster = null) =>
        LimitStopScan.Hits(Email(account), [annotation], history, roster ?? Roster.Empty);

    private static readonly IReadOnlyList<RoleHalfChange> _aBackedBothHalves =
    [
        Change(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), RoleHalf.Token, A, null),
        Change(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), RoleHalf.Key, A, null),
    ];

    [Fact]
    public void ARateLimitOnTheTokenHalfABackedThenIsASubscriptionLimitForA()
    {
        LimitStopHit hit = HitsFor(A, RateLimit("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"), _aBackedBothHalves).ShouldHaveSingleItem();

        hit.Label.ShouldBe("subscription limit");
        hit.Half.ShouldBe(RoleHalf.Token);
        hit.Run.ShouldBe(_run);
    }

    [Fact]
    public void TheSameRunIsNoHitForAWhenBBackedTheHalfThen()
    {
        IReadOnlyList<RoleHalfChange> history = [Change(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), RoleHalf.Token, B, null)];

        HitsFor(A, RateLimit("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"), history).ShouldBeEmpty();
        HitsFor(B, RateLimit("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"), history).ShouldHaveSingleItem();
    }

    [Fact]
    public void ARateLimitOnTheKeyHalfIsACreditStop()
    {
        HitsFor(A, RateLimit("AUTOMATIONS_CLAUDE_API_KEY"), _aBackedBothHalves).ShouldHaveSingleItem().Label.ShouldBe("credit stop");
    }

    [Fact]
    public void AMoveAfterTheRunStartedStillChargesTheRunToWhoBackedItThen()
    {
        // A held the token half from the 1st; B took it on the 19th, after the run on the 18th.
        IReadOnlyList<RoleHalfChange> history =
        [
            Change(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), RoleHalf.Token, A, null),
            Change(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), RoleHalf.Token, B, A),
        ];
        Roster now = new([new RosterEntry(Email(B)).WithCiRoles([new CiRoleAssignment(_automations, new DateOnly(2026, 10, 19))])]);

        HitsFor(A, RateLimit("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"), history, now).ShouldHaveSingleItem();
        HitsFor(B, RateLimit("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"), history, now).ShouldBeEmpty();
    }

    [Fact]
    public void WhenEveryChangeCameAfterTheRunItsPreviousAccountBackedTheHalf()
    {
        IReadOnlyList<RoleHalfChange> history = [Change(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), RoleHalf.Token, B, A)];

        HitsFor(A, RateLimit("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"), history).ShouldHaveSingleItem();
    }

    [Fact]
    public void WithNoHistoryForTheHalfTheCurrentRosterSaysWhoBackedIt()
    {
        Roster roster = new([new RosterEntry(Email(A)).WithCiRoles([new CiRoleAssignment(_automations, new DateOnly(2026, 9, 1))])]);

        HitsFor(A, RateLimit("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"), [], roster).ShouldHaveSingleItem();
        HitsFor(A, RateLimit("AUTOMATIONS_CLAUDE_API_KEY"), [], roster).ShouldBeEmpty();
    }

    [Fact]
    public void ACredentialOfTheSameNameInAnotherOrganizationIsNoHit()
    {
        LaneRun elsewhere = _run with { Repository = "other/lanes" };

        HitsFor(A, RateLimit("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", elsewhere), _aBackedBothHalves).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("class=rate-limited credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("class=failure credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("class=rate-limit credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN2")]
    [InlineData("class=rate-limit")]
    public void OnlyWholeTokensMatch(string message)
    {
        HitsFor(A, new LaneAnnotation(_run, message), _aBackedBothHalves).ShouldBeEmpty();
    }

    [Fact]
    public void AtMostThirtyRunsOfTheLastSevenDaysAreReadNewestFirst()
    {
        // 40 runs an hour apart, newest first from an hour before now; plus one from 8 days back.
        List<LaneRun> runs = [.. Enumerable.Range(1, 40).Select(static hour => new LaneRun("octo/lanes", hour, _now.AddHours(-hour), null))];
        runs.Add(new LaneRun("octo/other", 999, _now.AddDays(-8), null));

        IReadOnlyList<LaneRun> newest = LimitStopScan.Newest(runs, _now);

        newest.Count.ShouldBe(30);
        newest[0].Id.ShouldBe(1);
        newest[29].Id.ShouldBe(30);
    }

    [Fact]
    public void ARunOlderThanSevenDaysIsNotRead()
    {
        LaneRun old = new("octo/lanes", 7, _now.AddDays(-7).AddSeconds(-1), null);
        LaneRun edge = new("octo/lanes", 8, _now.AddDays(-7), null);

        LimitStopScan.Newest([old, edge], _now).ShouldBe([edge]);
    }
}
