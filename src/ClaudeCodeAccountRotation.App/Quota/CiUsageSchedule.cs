using Microsoft.Extensions.Hosting;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>Waits one interval between scheduled reads. Its own type, so a test's instant pacing delay is never taken for it.</summary>
internal delegate Task ScheduleDelay(TimeSpan interval, CancellationToken cancellationToken);

/// <summary>
/// The leader's fifteen-minute usage read of every account that backs a CI
/// role. It starts the same pass the refresh routes start, through
/// <see cref="QuotaRefreshWorker.TryStart"/>, so the per-account budget, the
/// endpoint lockouts and the one-pass-at-a-time rule all still hold. A tick
/// that finds a pass running or a lockout standing is skipped, never queued.
/// An account the WSL side holds is read there by the same pass, so one
/// schedule on the leader covers both sides.
/// </summary>
internal sealed class CiUsageSchedule(QuotaRefreshWorker worker, QuotaState state, TimeProvider timeProvider, ScheduleDelay delay) : BackgroundService
{
    /// <summary>Basis: the owner's decision on the usage alerts, "a background usage read every 15 minutes".</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (state.LockedUntil(timeProvider.GetUtcNow()) is null)
            {
                worker.TryStart(RefreshRequest.CiRoleAccounts);
            }
        }
    }
}
