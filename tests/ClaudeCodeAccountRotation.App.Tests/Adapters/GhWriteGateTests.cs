using ClaudeCodeAccountRotation.App.Adapters.Process;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>The gate's spacing, on a clock whose timers fire early the way the system's can.</summary>
public sealed class GhWriteGateTests
{
    [Fact]
    public async Task AWriteStartsAFullSpacingAfterTheLastEndedEvenWhenTheTimerFiresEarly()
    {
        EarlyFiringClock clock = new();
        using GhWriteGate gate = new(clock);

        await gate.RunAsync(static () => Task.FromResult(0), TestContext.Current.CancellationToken);
        long ended = clock.GetTimestamp();
        long started = await gate.RunAsync(() => Task.FromResult(clock.GetTimestamp()), TestContext.Current.CancellationToken);

        clock.GetElapsedTime(ended, started).ShouldBeGreaterThanOrEqualTo(GhWriteGate.Spacing);
    }
}

/// <summary>
/// A clock that stands still until a timer is made, then moves to just short
/// of that timer's due time and fires it: a timer on the system clock can
/// return up to a millisecond before the span it was given.
/// </summary>
internal sealed class EarlyFiringClock : TimeProvider
{
    private static readonly TimeSpan _earlyBy = TimeSpan.FromMilliseconds(1);
    private long _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _now);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Add(ref _now, (dueTime > _earlyBy ? dueTime - _earlyBy : dueTime).Ticks);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new FiredTimer();
    }

    private sealed class FiredTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
