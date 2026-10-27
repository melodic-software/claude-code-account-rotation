using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>Waits until the next look at the reset times. Its own type, so a test's hold on <see cref="ScheduleDelay"/> is never taken for it.</summary>
internal delegate Task UsageResetDelay(TimeSpan wait, CancellationToken cancellationToken);

/// <summary>Which roster accounts are due a read now, with the reset each is read for, and how long until the schedule looks again.</summary>
internal sealed record UsageResetPlan(IReadOnlyDictionary<AccountEmail, DateTimeOffset> Due, TimeSpan Wait);

/// <summary>
/// The leader's read of a roster account once a window its last read was
/// captured in has reset, whether or not the account backs a CI role: the
/// account a role is waiting to move to keeps showing a spent window until
/// something reads it again. One read per reset, timed from the reset instants
/// the reads already hold, so nothing is read on a timer. It starts the same
/// pass <see cref="CiUsageSchedule"/> does, through
/// <see cref="QuotaRefreshWorker.TryStart"/>, so the per-account budget, the
/// endpoint lockouts and the one-pass-at-a-time rule all still hold. A reset
/// whose pass started is not tried again, whatever its read came to; one that
/// found a pass running or a lockout standing is tried again after
/// <see cref="Grace"/>.
/// </summary>
internal sealed partial class UsageResetSchedule(
    QuotaRefreshWorker worker,
    QuotaState state,
    RosterFile rosterFile,
    TimeProvider timeProvider,
    UsageResetDelay delay,
    ILogger<UsageResetSchedule> logger) : BackgroundService
{
    /// <summary>Basis: judgment. Past the reset so a clock a little behind the endpoint's still reads the new window, and inside issue #373's "within a few minutes".</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Basis: issue #373's "within a few minutes". The longest the schedule goes
    /// without looking at memory again, which is how it sees a reset time a
    /// read brought in after it last looked. A look reads no account.
    /// </summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(5);

    // The reset each account was last read for. Memory only: after a restart a
    // reset the cached figure predates is read once more, which is the point.
    private readonly Dictionary<AccountEmail, DateTimeOffset> _attempted = [];

    /// <summary>
    /// A limit is due once its reset and the grace have passed, if the figure
    /// was captured before the reset and that reset has not been read for. A
    /// limit with no reset time, or one that reset before the read, is never due.
    /// </summary>
    internal static UsageResetPlan Plan(IEnumerable<UsageSnapshot> read, IReadOnlyDictionary<AccountEmail, DateTimeOffset> attempted, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(attempted);
        Dictionary<AccountEmail, DateTimeOffset> due = [];
        TimeSpan wait = LongestWait;
        foreach (UsageSnapshot snapshot in read)
        {
            foreach (UsageLimit limit in snapshot.Limits)
            {
                if (limit.ResetsAt is not DateTimeOffset resets
                    || resets <= snapshot.CapturedAt
                    || (attempted.TryGetValue(snapshot.Account, out DateTimeOffset done) && resets <= done))
                {
                    continue;
                }

                TimeSpan until = resets + Grace - now;
                if (until > TimeSpan.Zero)
                {
                    wait = until < wait ? until : wait;
                }
                else if (!due.TryGetValue(snapshot.Account, out DateTimeOffset latest) || resets > latest)
                {
                    due[snapshot.Account] = resets;
                }
            }
        }

        return new UsageResetPlan(due, wait);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first look waits too, so it follows the startup load of the cache.
        TimeSpan wait = Grace;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await delay(wait, stoppingToken);
                wait = await LookAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception exception)
#pragma warning restore CA1031
            {
                // The roster is a file other processes write. Anything unforeseen
                // costs this look, not the schedule.
                LogLookFailed(exception.GetType().Name);
                wait = Grace;
            }
        }
    }

    private async Task<TimeSpan> LookAsync(CancellationToken stoppingToken)
    {
        Roster roster = await rosterFile.ReadAsync(stoppingToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        UsageResetPlan plan = Plan(roster.Entries.Select(entry => state.LatestFor(entry.Email)).OfType<UsageSnapshot>(), _attempted, now);
        if (plan.Due.Count == 0)
        {
            return plan.Wait;
        }

        if (state.LockedUntil(now) is not null || !worker.TryStart(RefreshRequest.Named(plan.Due.Keys)))
        {
            return Grace < plan.Wait ? Grace : plan.Wait;
        }

        foreach ((AccountEmail account, DateTimeOffset resets) in plan.Due)
        {
            _attempted[account] = resets;
        }

        LogStarted(plan.Due.Count);
        return plan.Wait;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "reset read started for {Count} account(s) whose usage window has reset")]
    private partial void LogStarted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "reset read could not look at the roster ({Failure})")]
    private partial void LogLookFailed(string failure);
}
