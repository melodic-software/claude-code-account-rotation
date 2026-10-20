using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

public sealed class CiRoleTests
{
    private static readonly DateOnly _january = new(2026, 1, 1);
    private static readonly DateOnly _june = new(2026, 6, 1);

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    private static CiRole Org(string prefix, string organization = "octo") => CiRole.Parse(prefix, null, null, organization).Value;

    [Theory]
    [InlineData("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", "AUTOMATIONS", "AUTOMATIONS", "AUTOMATIONS_CLAUDE_API_KEY")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", "", "legacy (review lanes)", null)]
    [InlineData("MY_TOKEN", "", "MY_TOKEN", null)]
    [InlineData("XCLAUDE_CODE_OAUTH_TOKEN", "", "XCLAUDE_CODE_OAUTH_TOKEN", null)]
    public void ATokenSecretNamesItsRole(string secretName, string prefix, string name, string? keySecret)
    {
        var role = CiRole.FromTokenSecret(CiTokenSecret.Parse(secretName, null, "octo").Value);

        role.Prefix.ShouldBe(prefix);
        role.Name.ShouldBe(name);
        role.TokenSecretName.ShouldBe(secretName);
        role.KeySecretName.ShouldBe(keySecret);
    }

    [Fact]
    public void ATokenOnlyRoleIsNotThePrefixRoleOfTheSameName()
    {
        var literal = CiRole.FromTokenSecret(CiTokenSecret.Parse("MY_TOKEN", null, "octo").Value);

        literal.ShouldNotBe(Org("MY_TOKEN"));
    }

    [Theory]
    [InlineData("9LIVES")]
    [InlineData("WITH SPACE")]
    [InlineData("GITHUB")]
    public void APrefixGitHubWouldRefuseIsRefused(string prefix) =>
        CiRole.Parse(prefix, null, null, "octo").IsSuccess.ShouldBeFalse();

    [Fact]
    public void ClaimingARoleTakesItOffItsHolderWhateverTheDatesAndLeavesItsOtherRoles()
    {
        // The new holder sets only the key half and carries no token date at all,
        // while the old holder's token is newer: the move happens anyway.
        RosterEntry holder = new RosterEntry(Email("a@example.com"))
            .ClaimingToken(Org("AUTOMATIONS"), _january, null)
            .ClaimingToken(Org("REVIEWS"), _june, null);
        var claimant = new RosterEntry(Email("b@example.com")) { CiRoles = [new CiRoleAssignment(Org("AUTOMATIONS"), KeySetOn: _january)] };

        Roster roster = Roster.Empty.With(holder).With(claimant);

        RosterEntry a = roster.Find(Email("a@example.com"))!;
        a.CiRoles.Select(static held => held.Role.Prefix).ShouldBe(["REVIEWS"]);
        a.CiTokenGeneratedOn.ShouldBe(_june);
        roster.Find(Email("b@example.com"))!.CiRoles.Single().Role.ShouldBe(Org("AUTOMATIONS"));
    }

    [Fact]
    public void TheSamePrefixInAnotherOrganizationIsAnotherRole()
    {
        RosterEntry holder = new RosterEntry(Email("a@example.com")).ClaimingToken(Org("AUTOMATIONS", "octo"), _january, null);
        RosterEntry other = new RosterEntry(Email("b@example.com")).ClaimingToken(Org("AUTOMATIONS", "acme"), _june, null);

        Roster roster = Roster.Empty.With(holder).With(other);

        roster.Find(Email("a@example.com"))!.CiRoles.Single().Role.Owner.ShouldBe("octo");
        roster.Find(Email("a@example.com"))!.CiTokenGeneratedOn.ShouldBe(_january);
    }

    [Fact]
    public void AnAccountLosesItsTokenDateOnlyWithItsLastTokenHalf()
    {
        var holder = new RosterEntry(Email("a@example.com"), CiTokenGeneratedOn: _january)
        {
            CiRoles =
            [
                new CiRoleAssignment(Org("AUTOMATIONS"), TokenSetOn: _january),
                new CiRoleAssignment(Org("REVIEWS"), KeySetOn: _january),
            ],
        };

        Roster roster = Roster.Empty
            .With(holder)
            .With(new RosterEntry(Email("b@example.com")).ClaimingToken(Org("AUTOMATIONS"), _june, null));

        RosterEntry a = roster.Find(Email("a@example.com"))!;
        a.CiRoles.Single().Role.Prefix.ShouldBe("REVIEWS");
        a.CiTokenGeneratedOn.ShouldBeNull();
    }

    [Fact]
    public void ARoleClaimLeavesAHandMarkerAlone()
    {
        Roster roster = Roster.Empty
            .With(new RosterEntry(Email("a@example.com"), CiTokenGeneratedOn: _january))
            .With(new RosterEntry(Email("b@example.com")).ClaimingToken(Org("AUTOMATIONS"), _june, null));

        roster.Find(Email("a@example.com"))!.CiTokenGeneratedOn.ShouldBe(_january);
    }

    [Fact]
    public void TheCiSecretOfAnEntryIsItsNewestTokenHalf()
    {
        RosterEntry entry = new RosterEntry(Email("a@example.com"))
            .ClaimingToken(Org("REVIEWS"), _june, null)
            .ClaimingToken(Org("AUTOMATIONS"), _january, null);

        entry.CiTokenSecret.ShouldBe(new CiTokenSecret("REVIEWS_CLAUDE_CODE_OAUTH_TOKEN", CiSecretScope.Organization, "octo"));
    }
}
