using System.Threading.Channels;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>
/// The read of a roster account once a window its cached figure was captured
/// in has reset. Every instant is derived from the harness's frozen clock, and
/// the schedule's waits are handed to the test rather than slept, so a test
/// moves the clock past a reset and lets the schedule look again at once.
/// </summary>
public sealed class UsageResetScheduleTests
{
    private const string Unassigned = "b@example.com";
    private const string Other = "c@example.com";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnAccountIsDueOnceItsWindowHasResetAndTheGraceHasRunAndTheNextResetSetsTheWait()
    {
        UsageSnapshot reset = Snapshot(Unassigned, _now.AddDays(-2), Weekly(_now.AddMinutes(-10)));
        UsageSnapshot waiting = Snapshot(Other, _now.AddHours(-1), Weekly(_now.AddMinutes(3)));

        UsageResetPlan plan = UsageResetSchedule.Plan([reset, waiting], new Dictionary<AccountEmail, DateTimeOffset>(), _now);

        plan.Due.ShouldBe(new Dictionary<AccountEmail, DateTimeOffset> { [Email(Unassigned)] = _now.AddMinutes(-10) });
        // Three minutes to the reset, then the one-minute grace.
        plan.Wait.ShouldBe(TimeSpan.FromMinutes(4));
    }

    [Fact]
    public void AResetStillInsideTheGraceIsNotYetDue()
    {
        UsageResetPlan plan = UsageResetSchedule.Plan(
            [Snapshot(Unassigned, _now.AddDays(-2), Weekly(_now.AddSeconds(-30)))], new Dictionary<AccountEmail, DateTimeOffset>(), _now);

        plan.Due.ShouldBeEmpty();
        plan.Wait.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ALimitWithNoResetTimeOrOneThatResetBeforeTheReadIsNeverDue()
    {
        // The second reset passed before the figure was captured, so the figure
        // already counts the new window.
        UsageSnapshot noReset = Snapshot(Unassigned, _now.AddDays(-2), Weekly(resetsAt: null));
        UsageSnapshot readAfter = Snapshot(Other, _now.AddMinutes(-5), Weekly(_now.AddHours(-1)));

        UsageResetPlan plan = UsageResetSchedule.Plan([noReset, readAfter], new Dictionary<AccountEmail, DateTimeOffset>(), _now);

        plan.Due.ShouldBeEmpty();
        plan.Wait.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void AResetAlreadyAttemptedIsNotDueAgainButALaterOneIs()
    {
        UsageSnapshot snapshot = Snapshot(
            Unassigned,
            _now.AddDays(-2),
            Weekly(_now.AddHours(-3)),
            Session(_now.AddMinutes(-2)));

        UsageResetPlan once = UsageResetSchedule.Plan(
            [snapshot], new Dictionary<AccountEmail, DateTimeOffset> { [Email(Unassigned)] = _now.AddMinutes(-2) }, _now);
        UsageResetPlan again = UsageResetSchedule.Plan(
            [snapshot], new Dictionary<AccountEmail, DateTimeOffset> { [Email(Unassigned)] = _now.AddHours(-3) }, _now);

        once.Due.ShouldBeEmpty();
        again.Due.ShouldBe(new Dictionary<AccountEmail, DateTimeOffset> { [Email(Unassigned)] = _now.AddMinutes(-2) });
    }

    [Fact]
    public void AResetDaysAwayWaitsNoLongerThanFiveMinutes()
    {
        UsageResetPlan plan = UsageResetSchedule.Plan(
            [Snapshot(Unassigned, _now, Weekly(_now.AddDays(6)))], new Dictionary<AccountEmail, DateTimeOffset>(), _now);

        plan.Due.ShouldBeEmpty();
        plan.Wait.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task AnAccountWithNoCiRoleIsReadOnceItsWeeklyWindowHasReset()
    {
        using RefreshHarness harness = new();
        await OnRosterAsync(harness, Unassigned);
        // Cached at 100% two days ago, in a window that reset ten minutes ago.
        harness.State.RecordSnapshot(Snapshot(Unassigned, harness.Clock.GetUtcNow().AddDays(-2), Weekly(harness.Clock.GetUtcNow().AddMinutes(-10), 100)));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());
        await using Running running = await Running.StartAsync(harness);

        (await running.Ticks.AskedAsync()).ShouldBe(TimeSpan.FromMinutes(1));
        running.Ticks.Release();
        await running.Ticks.AskedAsync();
        await harness.State.CurrentRun;

        harness.Usage.Calls.ShouldBe(1);
        harness.OutcomeFor(Unassigned)!.Kind.ShouldBe(RefreshOutcomeKind.Read);
        harness.State.LatestFor(Email(Unassigned))!.Source.ShouldBe(QuotaSource.OnDemandRefresh);
    }

    [Fact]
    public async Task NothingIsReadBeforeTheResetAndTheAccountIsReadOnceItPasses()
    {
        using RefreshHarness harness = new();
        await OnRosterAsync(harness, Unassigned);
        harness.State.RecordSnapshot(Snapshot(Unassigned, harness.Clock.GetUtcNow().AddDays(-2), Weekly(harness.Clock.GetUtcNow().AddMinutes(3), 100)));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());
        await using Running running = await Running.StartAsync(harness);

        await running.Ticks.AskedAsync();
        running.Ticks.Release();
        TimeSpan untilReset = await running.Ticks.AskedAsync();

        harness.State.InProgress.ShouldBeFalse();
        harness.Usage.Calls.ShouldBe(0);
        untilReset.ShouldBe(TimeSpan.FromMinutes(4));

        harness.Clock.Advance(untilReset);
        running.Ticks.Release();
        await running.Ticks.AskedAsync();
        await harness.State.CurrentRun;

        harness.Usage.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task OnlyTheAccountWhoseWindowResetIsReadNotOneWithNoEntryOrNoResetTime()
    {
        using RefreshHarness harness = new();
        await OnRosterAsync(harness, Unassigned);
        await OnRosterAsync(harness, Other);
        await OnRosterAsync(harness, "d@example.com");
        harness.State.RecordSnapshot(Snapshot(Unassigned, harness.Clock.GetUtcNow().AddDays(-2), Weekly(harness.Clock.GetUtcNow().AddMinutes(-10))));
        harness.State.RecordSnapshot(Snapshot(Other, harness.Clock.GetUtcNow().AddDays(-2), Weekly(resetsAt: null)));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());
        await using Running running = await Running.StartAsync(harness);

        await running.Ticks.AskedAsync();
        running.Ticks.Release();
        await running.Ticks.AskedAsync();
        await harness.State.CurrentRun;

        harness.Usage.Calls.ShouldBe(1);
        harness.OutcomeFor(Unassigned)!.Kind.ShouldBe(RefreshOutcomeKind.Read);
        harness.OutcomeFor(Other).ShouldBeNull();
        harness.OutcomeFor("d@example.com").ShouldBeNull();
    }

    [Fact]
    public async Task ALockoutStandingStartsNoPassAndTheScheduleLooksAgainAfterTheGrace()
    {
        using RefreshHarness harness = new();
        await OnRosterAsync(harness, Unassigned);
        harness.State.RecordSnapshot(Snapshot(Unassigned, harness.Clock.GetUtcNow().AddDays(-2), Weekly(harness.Clock.GetUtcNow().AddMinutes(-10))));
        harness.State.UsageLockedUntil = harness.Clock.GetUtcNow().AddMinutes(5);
        await using Running running = await Running.StartAsync(harness);

        await running.Ticks.AskedAsync();
        running.Ticks.Release();
        TimeSpan next = await running.Ticks.AskedAsync();

        harness.State.InProgress.ShouldBeFalse();
        harness.Usage.Calls.ShouldBe(0);
        harness.OutcomeFor(Unassigned).ShouldBeNull();
        next.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task TheBudgetStillRefusesAndTheResetIsNotTriedAgain()
    {
        using RefreshHarness harness = new();
        await OnRosterAsync(harness, Unassigned);
        harness.State.RecordSnapshot(Snapshot(Unassigned, harness.Clock.GetUtcNow().AddDays(-2), Weekly(harness.Clock.GetUtcNow().AddMinutes(-10))));
        // A read a moment ago, so the one-minute gap stands.
        harness.Budget.TryReserve(Email(Unassigned)).ShouldBeTrue();
        await using Running running = await Running.StartAsync(harness);

        await running.Ticks.AskedAsync();
        running.Ticks.Release();
        await running.Ticks.AskedAsync();
        await harness.State.CurrentRun;

        harness.Usage.Calls.ShouldBe(0);
        harness.OutcomeFor(Unassigned)!.Kind.ShouldBe(RefreshOutcomeKind.BudgetRefused);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        running.Ticks.Release();
        await running.Ticks.AskedAsync();

        harness.State.InProgress.ShouldBeFalse();
        harness.Usage.Calls.ShouldBe(0);
    }

    private static async Task OnRosterAsync(RefreshHarness harness, string email)
    {
        await harness.ParkAsync(email, "refresh-" + email, harness.Valid, Token);
        await harness.AddToRosterAsync(email, Token);
    }

    private static AccountEmail Email(string value) => RefreshHarness.Email(value);

    private static UsageSnapshot Snapshot(string email, DateTimeOffset capturedAt, params UsageLimit[] limits) =>
        new(Email(email), capturedAt, QuotaSource.Cached, limits, null);

    private static UsageLimit Weekly(DateTimeOffset? resetsAt, double percent = 40) =>
        new("weekly_all", LimitKind.WeeklyAll, "weekly", percent, null, resetsAt, null, IsActive: true);

    private static UsageLimit Session(DateTimeOffset resetsAt) =>
        new("session", LimitKind.Session, "session", 40, null, resetsAt, null, IsActive: true);

    /// <summary>
    /// Each wait the schedule asks for, handed to the test, and held until the
    /// test releases it: the schedule has finished looking once it asks again.
    /// </summary>
    private sealed class Ticks : IDisposable
    {
        private readonly Channel<TimeSpan> _asked = Channel.CreateUnbounded<TimeSpan>();
        private readonly SemaphoreSlim _released = new(0);

        public async Task Delay(TimeSpan wait, CancellationToken cancellationToken)
        {
            _asked.Writer.TryWrite(wait);
            await _released.WaitAsync(cancellationToken);
        }

        public ValueTask<TimeSpan> AskedAsync() => _asked.Reader.ReadAsync(Token);

        public void Release() => _released.Release();

        public void Dispose() => _released.Dispose();
    }

    /// <summary>The worker and the schedule over one harness, stopped in reverse order.</summary>
    private sealed class Running : IAsyncDisposable
    {
        private readonly QuotaRefreshWorker _worker;
        private readonly UsageResetSchedule _schedule;

        private Running(RefreshHarness harness)
        {
            _worker = new QuotaRefreshWorker(harness.Engine, harness.State, NullLogger<QuotaRefreshWorker>.Instance);
            _schedule = new UsageResetSchedule(_worker, harness.State, harness.Roster, harness.Clock, Ticks.Delay, NullLogger<UsageResetSchedule>.Instance);
        }

        public Ticks Ticks { get; } = new();

        public static async Task<Running> StartAsync(RefreshHarness harness)
        {
            Running running = new(harness);
            await running._worker.StartAsync(Token);
            await running._schedule.StartAsync(Token);
            return running;
        }

        public async ValueTask DisposeAsync()
        {
            await _schedule.StopAsync(CancellationToken.None);
            await _worker.StopAsync(CancellationToken.None);
            _schedule.Dispose();
            _worker.Dispose();
            Ticks.Dispose();
        }
    }
}
