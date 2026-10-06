using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Hosting;

/// <summary>
/// Brings every side that has a launch configured up on this leader's own
/// version, so a release can be installed on either side first and both end up
/// running it with no click.
/// <list type="bullet">
/// <item>Leader first: the leader starts, finds the side on the old build or not
/// running, and starts it from whatever binary is installed. That follower was
/// told this leader's version, so when the new Linux build lands over it, it
/// stops itself with <see cref="FollowerUpgrade.ExitCode"/> and is started
/// again here.</item>
/// <item>Side first: the running follower keeps the old build, which still
/// matches the old leader, so hand-offs keep working. When the leader then
/// starts on the new build it finds the side on another version, stops it,
/// and starts it from the new binary.</item>
/// </list>
/// <para>
/// It starts and stops a process and moves no account. A side is only ever
/// stopped through its own <c>/api/shutdown</c>, which refuses while a switch
/// or import is in flight; a refusal leaves it running and is reported. A side
/// on another version cannot be in a hand-off with this leader, which refuses
/// that version. One pass at a time runs per side, so startup, the page's
/// button and an upgrade exit cannot interleave a stop with a start.
/// </para>
/// <para>
/// The startup pass runs after <see cref="StartupReconciliation"/>, because
/// hosted services start in registration order, and in the background, so the
/// page binds while a stale side drains.
/// </para>
/// </summary>
internal sealed partial class SideSupervisor : IHostedService, IDisposable
{
    private readonly PeerRegistry _peers;
    private readonly string _version;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _stopBudget;
    private readonly ILogger<SideSupervisor> _logger;
    private readonly Dictionary<SideName, SemaphoreSlim> _gates;
    private readonly Lock _pendingMutex = new();
    private readonly List<Task> _pending = [];

    // The host's ApplicationStopping, which it owns and cancels before any
    // hosted service's StopAsync; _stopped covers a stop with no host behind it.
    private readonly CancellationToken _stopping;
    private volatile bool _stopped;

    public SideSupervisor(PeerRegistry peers, string version, ILogger<SideSupervisor> logger, CancellationToken stopping)
        : this(peers, version, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), logger, stopping)
    {
    }

    internal SideSupervisor(PeerRegistry peers, string version, TimeSpan pollInterval, TimeSpan stopBudget, ILogger<SideSupervisor> logger, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(logger);
        _peers = peers;
        _version = version;
        _stopping = stopping;
        _pollInterval = pollInterval;
        _stopBudget = stopBudget;
        _logger = logger;
        _gates = peers.All.ToDictionary(static peer => peer.Side, static _ => new SemaphoreSlim(1, 1));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (Peer peer in _peers.All)
        {
            if (peer.Host is null)
            {
                continue;
            }

            peer.Host.Exited += (_, exited) => OnSideExited(peer, exited.ExitCode);
            Track(peer, "startup");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopped = true;
        Task[] pending;
        lock (_pendingMutex)
        {
            pending = [.. _pending];
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping: an unfinished pass is abandoned, and the side is left as it is.
        }
    }

    /// <summary>
    /// One pass for <paramref name="side"/>, as the page's <c>Start</c> asks
    /// for it: on this version already, started, or why neither.
    /// </summary>
    public async Task<Result<string, string>> BringUpAsync(SideName side, CancellationToken cancellationToken)
    {
        if (_peers.For(side) is not Peer peer)
        {
            return Result<string, string>.Failure("no peer is configured for " + side.Value);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
        return await BringUpAsync(peer, linked.Token);
    }

    private async Task<Result<string, string>> BringUpAsync(Peer peer, CancellationToken cancellationToken)
    {
        if (peer.Host is not IPeerProcessHost host)
        {
            return Result<string, string>.Failure("the " + peer.Side.Value + " side has no launch configured, so it cannot be started from here");
        }

        SemaphoreSlim gate = _gates[peer.Side];
        await gate.WaitAsync(cancellationToken);
        try
        {
            Result<PeerDashboard, string> read = await peer.Instance.ReadDashboardAsync(cancellationToken);
            if (read.IsSuccess)
            {
                if (string.Equals(read.Value.Version, _version, StringComparison.Ordinal))
                {
                    return Result<string, string>.Success("already running " + _version);
                }

                string running = read.Value.Version ?? "(none)";
                LogStale(peer.Side.Value, running, _version);
                Result<string, string> stop = await peer.Instance.ShutdownAsync(cancellationToken);
                if (stop.IsFailure)
                {
                    return Result<string, string>.Failure("the " + peer.Side.Value + " side runs " + running + " and was left running: " + stop.Error);
                }

                if (stop.Value != "stopped" && !stop.Value.StartsWith("not running", StringComparison.Ordinal))
                {
                    return Result<string, string>.Failure("the " + peer.Side.Value + " side runs " + running + " and " + stop.Value);
                }

                if (!await WaitUntilGoneAsync(peer, host, cancellationToken))
                {
                    return Result<string, string>.Failure(
                        "the " + peer.Side.Value + " side was asked to stop and still answers after "
                        + _stopBudget.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s");
                }
            }

            Result<Unit, string> started = await host.StartAsync(cancellationToken);
            return started.IsSuccess
                ? Result<string, string>.Success("started")
                : Result<string, string>.Failure(started.Error);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Until the side stops answering and this host's own child, if any, has
    /// exited. The wrapper refuses a start while the old follower still holds
    /// its lock, so starting before then would start nothing.
    /// </summary>
    private async Task<bool> WaitUntilGoneAsync(Peer peer, IPeerProcessHost host, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + _stopBudget;
        while (true)
        {
            await Task.Delay(_pollInterval, cancellationToken);
            if (!host.IsRunning && (await peer.Instance.ReadDashboardAsync(cancellationToken)).IsFailure)
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Only the upgrade exit starts the side again. A crash, a stop the
    /// operator asked for, or the wrapper finding another follower running is
    /// left alone, so nothing here can loop.
    /// </summary>
    private void OnSideExited(Peer peer, int exitCode)
    {
        if (exitCode != FollowerUpgrade.ExitCode || Stopping)
        {
            return;
        }

        LogUpgradeExit(peer.Side.Value);
        Track(peer, "upgrade");
    }

    private void Track(Peer peer, string reason)
    {
        lock (_pendingMutex)
        {
            if (Stopping)
            {
                return;
            }

            _pending.RemoveAll(static task => task.IsCompleted);
            _pending.Add(Task.Run(() => RunAsync(peer, reason)));
        }
    }

    private async Task RunAsync(Peer peer, string reason)
    {
        try
        {
            Result<string, string> outcome = await BringUpAsync(peer, _stopping);
            if (outcome.IsSuccess)
            {
                LogBroughtUp(peer.Side.Value, reason, outcome.Value);
            }
            else
            {
                LogNotBroughtUp(peer.Side.Value, reason, outcome.Error);
            }
        }
        catch (OperationCanceledException) when (Stopping)
        {
            // The leader is stopping.
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // A background pass must not take the leader down; the side reads offline and the page's button is still there.
            LogNotBroughtUp(peer.Side.Value, reason, exception.GetType().Name);
        }
    }

    private bool Stopping => _stopped || _stopping.IsCancellationRequested;

    public void Dispose()
    {
        _stopped = true;
        foreach (SemaphoreSlim gate in _gates.Values)
        {
            gate.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "the {Side} side runs {Running} and this leader runs {Version}: stopping it to start it again")]
    private partial void LogStale(string side, string running, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "the {Side} side stopped to take an installed build; starting it again")]
    private partial void LogUpgradeExit(string side);

    [LoggerMessage(Level = LogLevel.Information, Message = "the {Side} side ({Reason}): {Outcome}")]
    private partial void LogBroughtUp(string side, string reason, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the {Side} side was not brought up ({Reason}): {Why}")]
    private partial void LogNotBroughtUp(string side, string reason, string why);
}
