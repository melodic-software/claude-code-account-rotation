using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

/// <summary>
/// The out-of-band change check, held to the plan's rules: more than two
/// minutes after the tool's write is someone else's change, a half with no
/// instant is judged by date, and a gone secret is missing.
/// </summary>
public sealed class RoleSecretCheckTests
{
    private static readonly DateTimeOffset _setAt = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly _setOn = new(2026, 10, 1);
    private static readonly DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly CiRole _automations = new("AUTOMATIONS", CiSecretScope.Organization, "octo");

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    private static Roster Holding(CiRoleAssignment assignment) =>
        new([new RosterEntry(Email("a@example.com")).WithCiRoles([assignment])]);

    /// <summary>A fixed zone seven hours west of UTC, as the Pacific coast in summer, with no daylight rules.</summary>
    private static readonly TimeZoneInfo _west = TimeZoneInfo.CreateCustomTimeZone("west", TimeSpan.FromHours(-7), "west", "west");

    private static RoleCatalog Listing(params (string Name, DateTimeOffset? UpdatedAt)[] secrets) =>
        RoleCatalog.From([new OrganizationSecrets("octo", [.. secrets.Select(static secret => new ListedSecret(secret.Name, secret.UpdatedAt))])]);

    private static ClaimedHalf TokenHalf(CiRoleAssignment assignment, RoleCatalog listing, TimeZoneInfo? zone = null) =>
        RoleSecretCheck.Evaluate(Holding(assignment), listing, _now, zone ?? TimeZoneInfo.Utc).Claimed.Single(static half => half.Half == RoleHalf.Token);

    private static RoleSecretVerdict TokenVerdict(CiRoleAssignment assignment, RoleCatalog listing, TimeZoneInfo? zone = null) =>
        TokenHalf(assignment, listing, zone).Verdict;

    private static (RoleSecretVerdict, UnknownReason?) TokenOutcome(CiRoleAssignment assignment, RoleCatalog listing, TimeZoneInfo? zone = null)
    {
        ClaimedHalf half = TokenHalf(assignment, listing, zone);
        return (half.Verdict, half.Reason);
    }

    [Fact]
    public void TenMinutesAfterTheToolsWriteIsAChangeOutsideTheTool() =>
        TokenVerdict(
            new CiRoleAssignment(_automations, _setOn, _setAt),
            Listing(("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", _setAt.AddMinutes(10)))).ShouldBe(RoleSecretVerdict.ChangedOutsideTool);

    [Fact]
    public void ThirtySecondsAfterTheToolsWriteIsTheToolsOwn() =>
        TokenOutcome(
            new CiRoleAssignment(_automations, _setOn, _setAt),
            Listing(("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", _setAt.AddSeconds(30)))).ShouldBe((RoleSecretVerdict.Ok, null));

    [Fact]
    public void ExactlyTwoMinutesAfterTheToolsWriteIsStillTheToolsOwn() =>
        TokenVerdict(
            new CiRoleAssignment(_automations, _setOn, _setAt),
            Listing(("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", _setAt.AddMinutes(2)))).ShouldBe(RoleSecretVerdict.Ok);

    [Fact]
    public void ASecretOlderThanTheToolsWriteIsUnknownAsNeverRecorded() =>
        TokenOutcome(
            new CiRoleAssignment(_automations, _setOn, _setAt),
            Listing(("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", _setAt.AddMinutes(-3)))).ShouldBe((RoleSecretVerdict.Unknown, UnknownReason.OlderThanWrite));

    [Fact]
    public void ASecretListedWithNoReadableTimeIsUnknownForWantOfATime() =>
        TokenOutcome(
            new CiRoleAssignment(_automations, _setOn, _setAt),
            Listing(("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", null))).ShouldBe((RoleSecretVerdict.Unknown, UnknownReason.NoListedTime));

    [Fact]
    public void AHalfWithNoInstantUpdatedTheSameDayIsUnknownForWantOfAnInstant() =>
        TokenOutcome(
            new CiRoleAssignment(_automations, _setOn),
            Listing(("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", new DateTimeOffset(2026, 10, 1, 23, 0, 0, TimeSpan.Zero)))).ShouldBe((RoleSecretVerdict.Unknown, UnknownReason.NoInstant));

    [Fact]
    public void AHalfWithNoInstantUpdatedOnALaterLocalDayChangedOutsideTheTool() =>
        TokenVerdict(
            new CiRoleAssignment(_automations, _setOn),
            Listing(("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero))),
            _west).ShouldBe(RoleSecretVerdict.ChangedOutsideTool);

    // 2026-10-01 20:00 seven hours west of UTC is 2026-10-02 03:00 UTC: the
    // same local day the roster recorded, though a later UTC day.
    [Fact]
    public void AnEveningWriteWestOfUtcIsTheSameLocalDay() =>
        TokenVerdict(
            new CiRoleAssignment(_automations, _setOn),
            Listing(("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", new DateTimeOffset(2026, 10, 2, 3, 0, 0, TimeSpan.Zero))),
            _west).ShouldBe(RoleSecretVerdict.Unknown);

    [Fact]
    public void AnAbsentSecretIsMissing() =>
        TokenVerdict(new CiRoleAssignment(_automations, _setOn, _setAt), Listing(("APP_PRIVATE_KEY", _setAt))).ShouldBe(RoleSecretVerdict.Missing);

    [Fact]
    public void TheKeyHalfIsJudgedByItsOwnSecret()
    {
        RoleSecretReport report = RoleSecretCheck.Evaluate(
            Holding(new CiRoleAssignment(_automations, _setOn, _setAt, _setOn, _setAt)),
            Listing(("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", _setAt), ("AUTOMATIONS_CLAUDE_API_KEY", _setAt.AddHours(1))),
            _now,
            TimeZoneInfo.Utc);

        report.Claimed.Select(static half => (half.Half, half.SecretName, half.Verdict)).ShouldBe([
            (RoleHalf.Token, "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", RoleSecretVerdict.Ok),
            (RoleHalf.Key, "AUTOMATIONS_CLAUDE_API_KEY", RoleSecretVerdict.ChangedOutsideTool),
        ]);
    }

    [Fact]
    public void ARoleWhoseSecretsExistButNobodyClaimsIsUnclaimed()
    {
        RoleSecretReport report = RoleSecretCheck.Evaluate(
            Holding(new CiRoleAssignment(_automations, _setOn, _setAt)),
            Listing(
                ("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", _setAt),
                ("ISSUE_LANE_CLAUDE_CODE_OAUTH_TOKEN", _setAt),
                ("ISSUE_LANE_CLAUDE_API_KEY", _setAt)),
            _now,
            TimeZoneInfo.Utc);

        report.Unclaimed.Select(static role => role.Role.Name).ShouldBe(["ISSUE_LANE"]);
        report.CheckedAt.ShouldBe(_now);
    }

    [Fact]
    public void AnOrganizationThatWasNotReadIsNotJudged()
    {
        CiRole elsewhere = new("AUTOMATIONS", CiSecretScope.Organization, "elsewhere");

        RoleSecretCheck.Evaluate(Holding(new CiRoleAssignment(elsewhere, _setOn, _setAt)), Listing(), _now, TimeZoneInfo.Utc).Claimed.ShouldBeEmpty();
    }

    [Fact]
    public void ARoleUnderALiteralNameIsJudgedByThatName()
    {
        var literal = CiRole.FromTokenSecret(new CiTokenSecret("MY_TOKEN", CiSecretScope.Organization, "octo"));

        TokenVerdict(new CiRoleAssignment(literal, _setOn, _setAt), Listing(("MY_TOKEN", _setAt))).ShouldBe(RoleSecretVerdict.Ok);
    }
}
