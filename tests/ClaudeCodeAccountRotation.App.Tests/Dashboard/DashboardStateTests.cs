using System.Diagnostics;
using System.Globalization;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Dashboard;

/// <summary>
/// A dashboard poll reads one publication. The reconciliation report and the
/// pre-switch windows in that publication were written together, so a reader
/// racing a writer must not see a new report beside the previous windows, or
/// the previous report beside new windows.
/// </summary>
public sealed class DashboardStateTests
{
    [Fact]
    public void AReportUpdateKeepsTheWindowsAndHandOffLineItReplacedFrom()
    {
        DashboardState state = new();
        DateTimeOffset fiveHour = DateTimeOffset.UnixEpoch.AddHours(1);
        DateTimeOffset sevenDay = DateTimeOffset.UnixEpoch.AddDays(1);
        var windows = new PreSwitchWindows(fiveHour, sevenDay);
        state.Publish(current => current with { PreSwitchWindows = windows, HandOffBanner = "in transit" });
        var report = new ReconciliationReport([], "no open journal", false, "repaired");

        state.Publish(current => current with { LastReconciliation = report });

        DashboardSnapshot seen = state.Read();
        seen.LastReconciliation.ShouldBe(report);
        seen.PreSwitchWindows.ShouldBe(windows);
        seen.HandOffBanner.ShouldBe("in transit");
    }

    [Fact]
    public void ClearingPreSwitchWindowsMatchesTheObservedInstance()
    {
        DateTimeOffset at = DateTimeOffset.UnixEpoch.AddHours(1);
        var observed = new PreSwitchWindows(at, at);
        var newer = new PreSwitchWindows(at, at);
        DashboardSnapshot holdingObserved = DashboardSnapshot.Empty with { PreSwitchWindows = observed };
        DashboardSnapshot holdingNewer = DashboardSnapshot.Empty with { PreSwitchWindows = newer };

        holdingObserved.WithoutObservedPreSwitchWindows(observed).PreSwitchWindows.ShouldBeNull();
        DashboardSnapshot kept = holdingNewer.WithoutObservedPreSwitchWindows(observed);
        ReferenceEquals(kept.PreSwitchWindows, newer).ShouldBeTrue();
    }

    [Fact]
    public async Task AReaderNeverMixesAReconciliationWithWindowsFromAnotherPublication()
    {
        DashboardState state = new();
        var race = new Race();
        const int generationsEach = 30_000;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Task[] readers =
        [
            Task.Run(() => ReadUntilFinished(state, race, cancellationToken), cancellationToken),
            Task.Run(() => ReadUntilFinished(state, race, cancellationToken), cancellationToken),
        ];
        WaitUntilReading(race, cancellationToken);
        int readsBeforeWrites = Volatile.Read(ref race.Reads);

        Task[] writers =
        [
            Task.Run(() => PublishPairs(state, race, generationsEach, cancellationToken), cancellationToken),
            Task.Run(() => PublishPairs(state, race, generationsEach, cancellationToken), cancellationToken),
            Task.Run(() => PublishHandOff(state, generationsEach, cancellationToken), cancellationToken),
        ];

        await Task.WhenAll(writers);
        Volatile.Read(ref race.Reads).ShouldBeGreaterThan(readsBeforeWrites);
        Volatile.Write(ref race.Finished, 1);
        await Task.WhenAll(readers);

        DashboardSnapshot published = state.Read();
        AssertOnePublication(published);
        ReferenceEquals(published, state.Read()).ShouldBeTrue();
    }

    private static void PublishPairs(DashboardState state, Race race, int generations, CancellationToken cancellationToken)
    {
        for (int i = 0; i < generations && !cancellationToken.IsCancellationRequested; i++)
        {
            int generation = Interlocked.Increment(ref race.Generation);
            state.Publish(current => Paired(current, generation));
        }
    }

    private static void PublishHandOff(DashboardState state, int generations, CancellationToken cancellationToken)
    {
        for (int i = 0; i < generations && !cancellationToken.IsCancellationRequested; i++)
        {
            state.Publish(current => current with { HandOffBanner = "in transit" });
        }
    }

    private static void ReadUntilFinished(DashboardState state, Race race, CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref race.Finished) == 0 && !cancellationToken.IsCancellationRequested)
        {
            AssertOnePublication(state.Read());
            _ = Interlocked.Increment(ref race.Reads);
        }

        AssertOnePublication(state.Read());
    }

    private static void WaitUntilReading(Race race, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (Volatile.Read(ref race.Reads) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (started.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("the dashboard reader never observed a publication");
            }

            Thread.Yield();
        }
    }

    private static DashboardSnapshot Paired(DashboardSnapshot current, int generation)
    {
        string token = generation.ToString(CultureInfo.InvariantCulture);
        DateTimeOffset at = DateTimeOffset.UnixEpoch.AddSeconds(generation);
        return current with
        {
            LastReconciliation = new ReconciliationReport([], token, false, token),
            PreSwitchWindows = new PreSwitchWindows(at, at),
        };
    }

    /// <summary>
    /// Report and windows carry the same generation, or both are absent. The
    /// hand-off line is either still absent or the one the other writer publishes,
    /// never a generation of its own.
    /// </summary>
    private static void AssertOnePublication(DashboardSnapshot seen)
    {
        ReconciliationReport? report = seen.LastReconciliation;
        string? token = report?.Banner;
        if (report is null || token is null)
        {
            report.ShouldBeNull();
            token.ShouldBeNull();
            seen.PreSwitchWindows.ShouldBeNull();
        }
        else
        {
            report.JournalOutcome.ShouldBe(token);
            int generation = int.Parse(token, CultureInfo.InvariantCulture);
            DateTimeOffset at = DateTimeOffset.UnixEpoch.AddSeconds(generation);
            seen.PreSwitchWindows.ShouldBe(new PreSwitchWindows(at, at));
        }

        if (seen.HandOffBanner is string handOff)
        {
            handOff.ShouldBe("in transit");
        }
    }

    private sealed class Race
    {
        internal int Finished;

        internal int Reads;

        internal int Generation;
    }
}
