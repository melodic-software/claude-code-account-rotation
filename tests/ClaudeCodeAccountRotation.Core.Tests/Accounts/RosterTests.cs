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
