using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

public sealed class RosterTests
{
    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    [Fact]
    public void EntriesAreOrderedByEmail()
    {
        Roster roster = new([new RosterEntry(Email("c@example.com")), new RosterEntry(Email("a@example.com"))]);

        roster.Entries.Select(static entry => entry.Email.Value).ShouldBe(["a@example.com", "c@example.com"]);
    }

    [Fact]
    public void WithReplacesTheEntryForTheSameAccount()
    {
        Roster roster = Roster.Empty
            .With(new RosterEntry(Email("a@example.com")))
            .With(new RosterEntry(Email("a@example.com"), Alias: "work", Paused: true));

        roster.Entries.Count.ShouldBe(1);
        roster.Find(Email("a@example.com"))!.Paused.ShouldBeTrue();
        roster.Find(Email("a@example.com"))!.Alias.ShouldBe("work");
    }

    [Fact]
    public void MarkingOneAccountAsTheCiTokenHolderClearsAnyOtherHolder()
    {
        DateOnly first = new(2026, 1, 1);
        DateOnly second = new(2026, 6, 1);
        Roster roster = Roster.Empty
            .With(new RosterEntry(Email("a@example.com"), CiTokenGeneratedOn: first))
            .With(new RosterEntry(Email("b@example.com"), CiTokenGeneratedOn: second));

        roster.Find(Email("a@example.com"))!.CiTokenGeneratedOn.ShouldBeNull();
        roster.Find(Email("b@example.com"))!.CiTokenGeneratedOn.ShouldBe(second);
    }

    [Fact]
    public void EachSecretHasItsOwnHolder()
    {
        DateOnly day = new(2026, 10, 6);
        CiTokenSecret laneOne = CiTokenSecret.Parse("CLAUDE_CODE_OAUTH_TOKEN", "octo/lane-one", null).Value;
        CiTokenSecret laneTwo = CiTokenSecret.Parse("CLAUDE_CODE_OAUTH_TOKEN", "octo/lane-two", null).Value;
        Roster roster = Roster.Empty
            .With(new RosterEntry(Email("a@example.com"), CiTokenGeneratedOn: day, CiTokenSecret: laneOne))
            .With(new RosterEntry(Email("b@example.com"), CiTokenGeneratedOn: day, CiTokenSecret: laneTwo))
            .With(new RosterEntry(Email("c@example.com"), CiTokenGeneratedOn: day));

        roster.Entries.ShouldAllBe(entry => entry.CiTokenGeneratedOn == day);
    }

    [Fact]
    public void TheSameSecretMovesToItsNewHolderWhateverItsCase()
    {
        DateOnly day = new(2026, 10, 6);
        CiTokenSecret lower = CiTokenSecret.Parse("claude_code_oauth_token", "Octo/Lane-One", null).Value;
        CiTokenSecret upper = CiTokenSecret.Parse("CLAUDE_CODE_OAUTH_TOKEN", "octo/lane-one", null).Value;
        Roster roster = Roster.Empty
            .With(new RosterEntry(Email("a@example.com"), CiTokenGeneratedOn: day, CiTokenSecret: lower))
            .With(new RosterEntry(Email("b@example.com"), CiTokenGeneratedOn: day, CiTokenSecret: upper));

        roster.Find(Email("a@example.com"))!.CiTokenGeneratedOn.ShouldBeNull();
        roster.Find(Email("a@example.com"))!.CiTokenSecret.ShouldBeNull();
        roster.Find(Email("b@example.com"))!.CiTokenSecret.ShouldBe(upper);
    }

    [Theory]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", "octo/repo", null, true)]
    [InlineData("_UNDERSCORE_1", null, "my-org", true)]
    [InlineData("GITHUB_TOKEN", "octo/repo", null, false)]
    [InlineData("github_anything", "octo/repo", null, false)]
    [InlineData("9LIVES", "octo/repo", null, false)]
    [InlineData("WITH SPACE", "octo/repo", null, false)]
    [InlineData("OK", null, null, false)]
    [InlineData("OK", "octo/repo", "octo", false)]
    [InlineData("OK", "octo", null, false)]
    [InlineData("OK", "octo/repo --body x", null, false)]
    [InlineData("OK", null, "-org", false)]
    [InlineData("OK", null, "double--hyphen", false)]
    public void ASecretTargetFollowsGitHubsRules(string name, string? repository, string? organization, bool valid) =>
        CiTokenSecret.Parse(name, repository, organization).IsSuccess.ShouldBe(valid);

    [Fact]
    public void ClearingTheCiTokenMarkerLeavesEveryOtherEntryAlone()
    {
        DateOnly marked = new(2026, 1, 1);
        Roster roster = Roster.Empty
            .With(new RosterEntry(Email("a@example.com"), CiTokenGeneratedOn: marked))
            .With(new RosterEntry(Email("a@example.com"), CiTokenGeneratedOn: null));

        roster.Find(Email("a@example.com"))!.CiTokenGeneratedOn.ShouldBeNull();
    }

    [Fact]
    public void WithoutRemovesOnlyThatAccount()
    {
        Roster roster = Roster.Empty
            .With(new RosterEntry(Email("a@example.com")))
            .With(new RosterEntry(Email("b@example.com")))
            .Without(Email("a@example.com"));

        roster.Entries.Select(static entry => entry.Email.Value).ShouldBe(["b@example.com"]);
        roster.Find(Email("a@example.com")).ShouldBeNull();
    }
}
