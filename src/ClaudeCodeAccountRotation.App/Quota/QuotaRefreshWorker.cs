using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>
/// Runs refresh passes off the request thread. The route that starts a pass
/// answers 202 at once and the page's existing poll watches the pass land, which
/// is the only shape that works here: a pass outlives its request (a browser
/// navigating away would abort it mid-rotation and strand a pair), it outruns
/// the request pipeline's own timeouts, and the dashboard's poll takes the
/// mutation gate with a zero wait every ten seconds, so a pass holding a request
/// open would fight it.
/// <para>
/// One request at a time: the channel is bounded at one and
/// <see cref="TryStart"/> claims <see cref="QuotaState.InProgress"/> before
/// writing to it, so a second Refresh all arriving mid-pass is refused by the
/// route rather than queued behind ten accounts.
/// </para>
/// </summary>
internal sealed partial class QuotaRefreshWorker : BackgroundService
{
    private readonly Channel<RefreshRequest> _requests =
        Channel.CreateBounded<RefreshRequest>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly QuotaRefresh _refresh;
    private readonly QuotaState _state;
    private readonly ILogger<QuotaRefreshWorker> _logger;

    public QuotaRefreshWorker(QuotaRefresh refresh, QuotaState state, ILogger<QuotaRefreshWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(state);
        _refresh = refresh;
        _state = state;
        _logger = logger;
    }

    /// <summary>
    /// Starts one pass, or reports that one is already running. The run's
    /// completion source is created here, under the state's own lock, before the
    /// request reaches the channel: a caller that awaits
    /// <see cref="QuotaState.CurrentRun"/> the instant this returns true is then
    /// guaranteed to wait for this pass rather than to see the previous one's
    /// finished task.
    /// </summary>
    public bool TryStart(RefreshRequest request)
    {
        if (!_state.TryBeginRun())
        {
            return false;
        }

        if (_requests.Writer.TryWrite(request))
        {
            return true;
        }

        // The channel refused although nothing was in flight, which means the
        // service is stopping. Give the claim back rather than leave the state
        // saying a pass is running that nothing will ever run.
        _state.EndRun();
        return false;
    }

    /// <summary>
    /// Closes the channel to new work before the base class cancels the loop.
    /// Without it, a Refresh all that arrives during shutdown claims the run,
    /// writes into a channel nothing will read again, and is answered 202 for a
    /// pass that never runs; with it the write fails, the claim is given back,
    /// and the route says a refresh could not be started.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _requests.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            RefreshRequest request;
            try
            {
                request = await _requests.Reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                // Shutdown completed the writer and drained the channel. An
                // ordinary ending, and a different exception from the cancelled
                // one above: the reader throws this one when there is nothing
                // left to read and nothing left to come.
                return;
            }

            try
            {
                await _refresh.RunAsync(request, stoppingToken);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception exception)
#pragma warning restore CA1031
            {
                // The pass turns its own failures into outcomes, so reaching here is
                // a defect rather than a foreseen path. It must still not end the
                // service: the alternative is a tool whose Refresh all button
                // silently does nothing until the next restart.
                LogPassFailed(exception.GetType().Name);
                LogPassFailedDetail(exception);
            }
            finally
            {
                _state.EndRun();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "refresh pass ended in an unhandled failure ({Failure})")]
    private partial void LogPassFailed(string failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "refresh pass ended in an unhandled failure")]
    private partial void LogPassFailedDetail(Exception exception);
}
