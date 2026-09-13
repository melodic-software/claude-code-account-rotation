using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Routing;

namespace ClaudeCodeAccountRotation.Core.Tests.Routing;

/// <summary>
/// Pins the order accounts are listed in: the ones usable now first, by the
/// window closest to turning over, so the subscription nearest its boundary is
/// spent before it resets unused. The rules below are each one assertion, and
/// the last fact is the judgement the rules cannot make for themselves — a real
/// roster shape whose sequence an operator approved, which a rule change can
/// break while every per-rule fact stays green.
/// </summary>
public sealed class AccountAvailabilityTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 13, 0, 30, 0, TimeSpan.Zero);

    private static UsageLimit Session(double percent, DateTimeOffset? resetsAt = null) =>
        new("session", LimitKind.Session, Group: null, percent, Severity: null, resetsAt, ScopeDisplayName: null, IsActive: true);

    private static UsageLimit Weekly(double percent, DateTimeOffset? resetsAt = null) =>
        new("weekly_all", LimitKind.WeeklyAll, Group: null, percent, Severity: null, resetsAt, ScopeDisplayName: null, IsActive: true);

    private static UsageLimit Scoped(string displayName, double percent, DateTimeOffset? resetsAt = null) =>
        new("weekly_scoped", LimitKind.WeeklyScoped, "weekly", percent, "warning", resetsAt, displayName, IsActive: true);

    private static AccountStanding Read(string email, params UsageLimit[] limits)
    {
        AccountEmail account = new(email);
        return new AccountStanding(
            account,
            IsLive: false,
            IsPaused: false,
            HasCredentials: true,
            new UsageSnapshot(account, _now.AddMinutes(-5), QuotaSource.Cached, limits, ExtraUsage: null));
    }

    private static AccountStanding NeverRead(string email) =>
        new(new AccountEmail(email), IsLive: false, IsPaused: false, HasCredentials: true);

    private static IReadOnlyList<string> Order(params AccountStanding[] standings) =>
        [.. AccountAvailability.Arrange(standings, _now).Select(arranged => arranged.Key.Email.Value)];

    [Fact]
    public void AUsableAccountComesBeforeAnExhaustedOne()
    {
        Order(
            Read("a@example.com", Session(95, _now.AddHours(3))),
            Read("b@example.com", Session(10, _now.AddHours(3))))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void APausedAccountSortsLast()
    {
        // After the never-read ones too: an account the operator took out of the
        // rotation is not a candidate whatever is or is not known about it.
        Order(
            Read("a@example.com", Weekly(50, _now.AddDays(2))) with { IsPaused = true },
            NeverRead("b@example.com"))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void AnAccountNeverReadIsGroupedAfterTheExhaustedOnes()
    {
        // An account nothing has read has no position, and a missing figure is
        // not a zero: treating it as headroom would send the operator to the one
        // account nobody can say anything about.
        Order(
            NeverRead("a@example.com"),
            Read("b@example.com", Session(95, _now.AddHours(3))))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void AnAccountWhoseOnlyFigureIsScopedIsUnread()
    {
        // A scoped window says nothing about the two windows that decide whether
        // the account can be worked at all, so an account carrying only one has
        // been read about something else, not about this.
        AvailabilityKey key = AccountAvailability.KeyFor(
            Read("a@example.com", Scoped("Fable", 100, _now.AddDays(2))),
            _now);

        key.Standing.ShouldBe(AvailabilityStanding.Unread);
        key.NextResetAt.ShouldBeNull();
    }

    [Fact]
    public void OrdersByEarliestWeeklyResetAmongUsableAccounts()
    {
        // The whole point of the order: spend the subscription closest to its
        // boundary first, because what is left in that window is lost when it
        // turns over and what is left in the others is not.
        Order(
            Read("a@example.com", Weekly(20, _now.AddDays(5))),
            Read("b@example.com", Weekly(20, _now.AddDays(1))))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void AUsableAccountWithNoResetTimeSortsAfterTheOnesThatHaveOne()
    {
        // The nullable comparer the refresh order relies on puts null first,
        // which here would read as "frees up soonest" about the one account
        // whose window nobody can date.
        Order(
            Read("a@example.com", Weekly(20)),
            Read("b@example.com", Weekly(20, _now.AddDays(5))))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void ExhaustedAccountsAreOrderedByTheResetThatActuallyFreesThem()
    {
        // Not by the earliest window either account happens to hold: b is out of
        // five-hour quota, so its weekly window turning over later changes
        // nothing, and a is out of weekly quota, so its session window turning
        // over sooner frees nothing.
        Order(
            Read("a@example.com", Session(10, _now.AddHours(2)), Weekly(100, _now.AddHours(12))),
            Read("b@example.com", Session(95, _now.AddHours(6)), Weekly(50, _now.AddHours(20))))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void AnAccountExhaustedOnBothBucketsKeysOnTheLaterReset()
    {
        // The five-hour window turning over leaves the account still out of
        // weekly quota, so keying on it would put the card ahead of accounts
        // that really do free up first and promise a wait that is not over.
        AvailabilityKey key = AccountAvailability.KeyFor(
            Read("a@example.com", Session(95, _now.AddHours(3)), Weekly(100, _now.AddHours(30))),
            _now);

        key.Standing.ShouldBe(AvailabilityStanding.Exhausted);
        key.NextResetAt.ShouldBe(_now.AddHours(30));
    }

    [Fact]
    public void AnExhaustedAccountWhoseResetCannotBeDatedSortsAfterTheOnesThatCan()
    {
        // Spent, and nothing says when it comes back: the account is still out of
        // the running, so it stays in the exhausted group, and it states no wait
        // at all rather than one the page would have to invent. Last of that
        // group, because an account whose return nobody can date must not be
        // offered ahead of one that really does free up at a known hour.
        AvailabilityKey key = AccountAvailability.KeyFor(Read("a@example.com", Weekly(100)), _now);

        key.Standing.ShouldBe(AvailabilityStanding.Exhausted);
        key.NextResetAt.ShouldBeNull();

        Order(
            Read("a@example.com", Weekly(100)),
            Read("b@example.com", Weekly(100, _now.AddDays(2))))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void AnAccountSpentOnBothWindowsWithOneUndatedCarriesNoInstant()
    {
        // The undated window can still be blocking when the dated one turns
        // over, so the reset that is known is not the hour the account comes
        // back: stating it would promise a return nobody can vouch for and put
        // the card ahead of accounts that really do free up later.
        AvailabilityKey key = AccountAvailability.KeyFor(
            Read("a@example.com", Session(95), Weekly(100, _now.AddDays(1))),
            _now);

        key.Standing.ShouldBe(AvailabilityStanding.Exhausted);
        key.NextResetAt.ShouldBeNull();

        Order(
            Read("a@example.com", Session(95), Weekly(100, _now.AddDays(1))),
            Read("b@example.com", Weekly(100, _now.AddDays(3))))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void AWindowThatHasResetSinceCaptureCountsAsUsable()
    {
        // A cached hundred per cent from the window before this one measures
        // nothing the operator can act on, and its instant is in the past: kept,
        // it would sort ahead of every account that really does free up next and
        // render as a wait that ended hours ago.
        AvailabilityKey key = AccountAvailability.KeyFor(
            Read("a@example.com", Weekly(100, _now.AddHours(-2))),
            _now);

        key.Standing.ShouldBe(AvailabilityStanding.Usable);
        key.NextResetAt.ShouldBeNull();
    }

    [Fact]
    public void APausedAccountSortsLastEvenWithHeadroom()
    {
        Order(
            Read("a@example.com", Session(0), Weekly(0, _now.AddDays(6))) with { IsPaused = true },
            Read("b@example.com", Session(95, _now.AddHours(3))))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void APausedAccountThatIsAlsoLiveStillSortsLast()
    {
        // The operator can pause the account they are working on, and the pause
        // is the instruction: being live is not a reason to offer it next.
        Order(
            Read("a@example.com", Weekly(20, _now.AddDays(1))) with { IsLive = true, IsPaused = true },
            Read("b@example.com", Weekly(20, _now.AddDays(6))))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void TheLiveAccountIsNotPinnedToTheTop()
    {
        // Where the live account sits is a fact about its own windows; the page
        // marks it wherever it lands.
        Order(
            Read("a@example.com", Weekly(20, _now.AddDays(6))) with { IsLive = true },
            Read("b@example.com", Weekly(20, _now.AddDays(1))))
            .ShouldBe(["b@example.com", "a@example.com"]);
    }

    [Fact]
    public void FiveHourAtThresholdIsExhaustedAndOneBelowItIsUsable()
    {
        AccountAvailability.KeyFor(Read("a@example.com", Session(90, _now.AddHours(3))), _now)
            .Standing.ShouldBe(AvailabilityStanding.Exhausted);
        AccountAvailability.KeyFor(Read("a@example.com", Session(89, _now.AddHours(3))), _now)
            .Standing.ShouldBe(AvailabilityStanding.Usable);
    }

    [Fact]
    public void SevenDayExhaustedAtOneHundredAndUsableAtNinetyNine()
    {
        // The weekly window is spent only when it is actually spent: at
        // ninety-nine there is still work the operator can put through it.
        AccountAvailability.KeyFor(Read("a@example.com", Weekly(100, _now.AddDays(2))), _now)
            .Standing.ShouldBe(AvailabilityStanding.Exhausted);
        AccountAvailability.KeyFor(Read("a@example.com", Weekly(99, _now.AddDays(2))), _now)
            .Standing.ShouldBe(AvailabilityStanding.Usable);
    }

    [Fact]
    public void AScopedBucketDoesNotChangeTheStandingOrTheKey()
    {
        // An account can hold several scoped windows at once, so there is no one
        // scoped reset to key on; the card keeps the row and the order ignores it.
        AvailabilityKey key = AccountAvailability.KeyFor(
            Read(
                "a@example.com",
                Session(10, _now.AddHours(3)),
                Weekly(40, _now.AddDays(5)),
                Scoped("Fable", 100, _now.AddDays(2))),
            _now);

        key.Standing.ShouldBe(AvailabilityStanding.Usable);
        key.NextResetAt.ShouldBe(_now.AddDays(5));
    }

    [Fact]
    public void AccountsWithTheSameKeyInstantAreOrderedByEmail()
    {
        // Ten accounts refreshed in one pass share a reset instant often enough
        // that an unspecified tie order would reshuffle the page between polls.
        Order(
            Read("c@example.com", Weekly(20, _now.AddDays(1))),
            Read("a@example.com", Weekly(20, _now.AddDays(1))),
            Read("b@example.com", Weekly(20, _now.AddDays(1))))
            .ShouldBe(["a@example.com", "b@example.com", "c@example.com"]);
    }

    [Fact]
    public void TheThresholdsAreParametersSoAPolicyCanMoveThem()
    {
        // A policy binds these two numbers later; moving them must not need a
        // signature change here, or card order and queue order could diverge.
        AccountStanding account = Read("a@example.com", Session(60, _now.AddHours(3)), Weekly(70, _now.AddDays(4)));

        AccountAvailability.KeyFor(account, _now).Standing.ShouldBe(AvailabilityStanding.Usable);
        AccountAvailability.KeyFor(account, _now, eligibleFiveHourMaxPercent: 50).Standing.ShouldBe(AvailabilityStanding.Exhausted);
        AccountAvailability.KeyFor(account, _now, eligibleSevenDayMaxPercent: 70).Standing.ShouldBe(AvailabilityStanding.Exhausted);
    }

    [Fact]
    public void TheOperatorsRosterShapeOrdersAsPresented()
    {
        // Ten accounts in the shape a real roster has, and one sequence the
        // operator judged and approved. Every fact above says a rule is obeyed;
        // this one says the rules together produce the list a person wanted, and
        // a rule change that keeps all of them green can still fail here.
        IReadOnlyList<AccountStanding> roster =
        [
            Read("a@example.com", Session(0), Weekly(42, At(15, 9, 0))),
            Read("b@example.com", Session(0), Weekly(84, At(16, 0, 0))),
            Read("c@example.com", Session(23, At(13, 4, 50)), Weekly(65, At(15, 22, 0))) with { IsLive = true },
            Read("d@example.com", Session(0), Weekly(3, At(16, 3, 0))),
            Read("e@example.com", Session(4, At(13, 5, 20)), Weekly(31, At(18, 8, 0))),
            Read("f@example.com", Session(3, At(13, 5, 20)), Weekly(58, At(16, 7, 0))),
            Read("g@example.com", Session(0), Weekly(0, At(18, 23, 0))),
            // The one scoped window in the fixture, at a hundred per cent: h is
            // out of Fable quota and still the account that frees up next.
            Read("h@example.com", Session(0, At(13, 5, 30)), Weekly(93, At(13, 16, 0)), Scoped("Fable", 100, At(13, 16, 0))),
            Read("i@example.com", Session(0), Weekly(30, At(16, 10, 0))),
            Read("j@example.com", Session(0), Weekly(38, At(17, 23, 59))),
        ];

        IReadOnlyList<ArrangedAccount> arranged = AccountAvailability.Arrange(roster, _now);

        arranged.Select(account => account.Key.Email.Value).ShouldBe(
        [
            "h@example.com",
            "a@example.com",
            "c@example.com",
            "b@example.com",
            "d@example.com",
            "f@example.com",
            "i@example.com",
            "j@example.com",
            "e@example.com",
            "g@example.com",
        ]);

        ArrangedAccount live = arranged.Single(account => account.Standing.IsLive);
        live.Key.Email.Value.ShouldBe("c@example.com");
        arranged[0].Standing.IsLive.ShouldBeFalse();
    }

    /// <summary>A reset instant in the fixture's own week, read as day, hour, minute.</summary>
    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 9, day, hour, minute, 0, TimeSpan.Zero);
}
