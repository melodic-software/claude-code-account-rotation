using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Hosting;

/// <summary>
/// Runs the switch executor's reconciliation once at startup, before the
/// first request, and keeps its report for the page's banner. It also puts the
/// last pass's numbers back on the cards, so a restart to install a build does
/// not cost the rate window a second set of reads.
/// </summary>
internal sealed partial class StartupReconciliation(
    LiveDirectorySwitch executor,
    RecoveryFiles recovery,
    UsageSnapshotCache cache,
    QuotaState quota,
    DashboardState state,
    ILogger<StartupReconciliation> logger) : IHostedService
{
    private readonly ILogger<StartupReconciliation> _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ReconciliationReport report = await executor.ReconcileAsync(cancellationToken);
        state.LastReconciliation = report;
        LogReconciled(report.JournalOutcome, report.Quarantined.Count, report.SwitchingBlocked);
        await RestoreStrandedPairsAsync(cancellationToken);
        await LoadCachedUsageAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Puts back any credential pair a refresh rotated and could not write, now
    /// that nothing else is running. Every file is already attempted inside its
    /// own catch; this outer guard covers everything else, because a tool that
    /// refuses to start over a recovery file would leave the operator with no way
    /// to reach the very page that explains it.
    /// </summary>
    private async Task RestoreStrandedPairsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await recovery.RestoreAllAsync(cancellationToken);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogRestoreSweepFailed(exception.GetType().Name);
            LogRestoreSweepFailedDetail(exception);
        }
    }

    /// <summary>
    /// Seeds the cards with the numbers the last run read. Nothing here is
    /// allowed to matter: the cache's own read is tolerant of every file it might
    /// find, and this guard covers the rest, because a tool that will not start
    /// over a cache of percentages would be worse than one showing "unknown".
    /// </summary>
    private async Task LoadCachedUsageAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<UsageSnapshot> cached = await cache.LoadAsync(cancellationToken);
            foreach (UsageSnapshot snapshot in cached)
            {
                // Nothing has read an account yet: the refresh worker acts only on
                // a request, and this runs before the first one is served.
                quota.RecordSnapshot(snapshot);
            }

            LogCacheLoaded(cached.Count);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogCacheLoadFailed(exception.GetType().Name);
            LogCacheLoadFailedDetail(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "startup reconciliation: {JournalOutcome}; quarantined {QuarantinedCount}; switching blocked: {Blocked}")]
    private partial void LogReconciled(string journalOutcome, int quarantinedCount, bool blocked);

    // The type and the curated reason where the operator will see them, the
    // exception itself at Debug: a stack trace from a startup sweep over the
    // profiles root quotes paths, and this console is read beside ten real
    // accounts.
    [LoggerMessage(Level = LogLevel.Warning, Message = "the recovery sweep could not run ({Failure})")]
    private partial void LogRestoreSweepFailed(string failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "the recovery sweep could not run")]
    private partial void LogRestoreSweepFailedDetail(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "loaded cached usage for {AccountCount} account(s)")]
    private partial void LogCacheLoaded(int accountCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the cached usage could not be loaded ({Failure})")]
    private partial void LogCacheLoadFailed(string failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "the cached usage could not be loaded")]
    private partial void LogCacheLoadFailedDetail(Exception exception);
}
