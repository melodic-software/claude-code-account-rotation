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
    public void ClaimingAHalfTakesThatHalfOffItsHolderWhateverTheDatesAndLeavesItsOtherRoles()
    {
        // The claimant's key is older than the holder's and it carries no token
        // date at all: the key half moves anyway, and only the key half.
        RosterEntry holder = new RosterEntry(Email("a@example.com"))
            .ClaimingToken(Org("AUTOMATIONS"), _january, null)
            .ClaimingKey(Org("AUTOMATIONS"), _june, null)
            .ClaimingToken(Org("REVIEWS"), _june, null);
        var keyClaimant = new RosterEntry(Email("b@example.com")) { CiRoles = [new CiRoleAssignment(Org("AUTOMATIONS"), KeySetOn: _january)] };

        Roster keyMoved = Roster.Empty.With(holder).With(keyClaimant);

        RosterEntry a = keyMoved.Find(Email("a@example.com"))!;
        a.CiRoles.Select(static held => (held.Role.Prefix, held.HasToken, held.HasKey)).ShouldBe([("AUTOMATIONS", true, false), ("REVIEWS", true, false)]);
        a.CiTokenGeneratedOn.ShouldBe(_june);
        keyMoved.Find(Email("b@example.com"))!.CiRoles.Single().ShouldBe(new CiRoleAssignment(Org("AUTOMATIONS"), KeySetOn: _january));

        // A claim of both halves takes both, and the holder keeps its other role.
        var bothClaimant = new RosterEntry(Email("c@example.com")) { CiRoles = [new CiRoleAssignment(Org("AUTOMATIONS"), _january, KeySetOn: _january)] };

        Roster bothMoved = Roster.Empty.With(holder).With(bothClaimant);

        bothMoved.Find(Email("a@example.com"))!.CiRoles.Select(static held => held.Role.Prefix).ShouldBe(["REVIEWS"]);
        bothMoved.Find(Email("a@example.com"))!.CiTokenGeneratedOn.ShouldBe(_june);
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
    public void ClaimingTheRoleTheDateCameFromLowersTheHoldersDateToItsNewestRemainingToken()
    {
        RosterEntry holder = new RosterEntry(Email("a@example.com"))
            .ClaimingToken(Org("REVIEWS"), _january, null)
            .ClaimingToken(Org("AUTOMATIONS"), _june, null);

        Roster roster = Roster.Empty
            .With(holder)
            .With(new RosterEntry(Email("b@example.com")).ClaimingToken(Org("AUTOMATIONS"), new DateOnly(2026, 10, 6), null));

        roster.Find(Email("a@example.com"))!.CiTokenGeneratedOn.ShouldBe(_january);
    }

    [Fact]
    public void ARoleAnEntryAlreadyHeldUnchangedTakesNothingFromTheHolderOfItsOtherHalf()
    {
        // A split: b set the token, a still holds the key. Editing either
        // account's other fields leaves both halves where they are.
        RosterEntry tokenHolder = new RosterEntry(Email("b@example.com")).ClaimingToken(Org("AUTOMATIONS"), _june, null);
        var keyHolder = new RosterEntry(Email("a@example.com")) { CiRoles = [new CiRoleAssignment(Org("AUTOMATIONS"), KeySetOn: _january)] };
        Roster split = new([tokenHolder, keyHolder]);

        Roster edited = split.With(keyHolder with { Alias = "renamed" }).With(tokenHolder with { Notes = "edited" });

        edited.Find(Email("a@example.com"))!.CiRoles.Single().HasKey.ShouldBeTrue();
        edited.Find(Email("b@example.com"))!.CiRoles.Single().HasToken.ShouldBeTrue();

        // Setting the key from b changes b's claim, which takes the role whole.
        Roster repaired = edited.With(edited.Find(Email("b@example.com"))!.ClaimingKey(Org("AUTOMATIONS"), _june, null));

        repaired.Find(Email("a@example.com"))!.CiRoles.ShouldBeEmpty();
    }

    [Fact]
    public void ReSendingOneHalfOfASplitRoleTakesOnlyThatHalf()
    {
        // b set the token, a still holds the key.
        RosterEntry tokenHolder = new RosterEntry(Email("b@example.com")).ClaimingToken(Org("AUTOMATIONS"), _june, null);
        var keyHolder = new RosterEntry(Email("a@example.com")) { CiRoles = [new CiRoleAssignment(Org("AUTOMATIONS"), KeySetOn: _january)] };
        Roster split = new([tokenHolder, keyHolder]);

        Roster keyResent = split.With(keyHolder.ClaimingKey(Org("AUTOMATIONS"), _june, null));
        Roster tokenResent = split.With(tokenHolder.ClaimingToken(Org("AUTOMATIONS"), new DateOnly(2026, 10, 6), null));

        keyResent.Find(Email("b@example.com"))!.CiRoles.ShouldBe([new CiRoleAssignment(Org("AUTOMATIONS"), _june)]);
        keyResent.Find(Email("a@example.com"))!.CiRoles.ShouldBe([new CiRoleAssignment(Org("AUTOMATIONS"), KeySetOn: _june)]);
        tokenResent.Find(Email("a@example.com"))!.CiRoles.ShouldBe([new CiRoleAssignment(Org("AUTOMATIONS"), KeySetOn: _january)]);
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
