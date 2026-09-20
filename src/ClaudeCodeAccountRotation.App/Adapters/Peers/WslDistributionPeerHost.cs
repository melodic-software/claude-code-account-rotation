using System.Diagnostics;
using System.Globalization;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Adapters.Peers;

/// <summary>
/// The follower as the leader's child: <c>wsl.exe -d &lt;distribution&gt; -u
/// &lt;user&gt; --exec &lt;executable&gt; --port &lt;port&gt;</c>.
/// <para>
/// Design decision 3 makes the follower leader-spawned, so nothing is
/// installed in the distro that a running leader does not start. This class is
/// deliberately thin: it starts the child and reports whether it is alive.
/// Whether the side is <i>usable</i> is a different question, answered by
/// <see cref="IPeerRotationInstance.ReadDashboardAsync"/>, because a
/// <c>wsl.exe</c> that exited says nothing about a follower the operator
/// started themselves.
/// </para>
/// </summary>
internal sealed partial class WslDistributionPeerHost : IPeerProcessHost, IDisposable
{
    private readonly PeerLaunch _launch;
    private readonly ILogger<WslDistributionPeerHost> _logger;
    private readonly Lock _mutex = new();
    private System.Diagnostics.Process? _child;

    public WslDistributionPeerHost(SideName side, PeerLaunch launch, ILogger<WslDistributionPeerHost> logger)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(logger);
        Side = side;
        _launch = launch;
        _logger = logger;
    }

    public SideName Side { get; }

    public bool IsRunning
    {
        get
        {
            lock (_mutex)
            {
                return _child is { HasExited: false };
            }
        }
    }

    public Task<Result<Unit, string>> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_mutex)
        {
            if (_child is { HasExited: false })
            {
                return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
            }

            _child?.Dispose();
            _child = null;

            ProcessStartInfo start = new("wsl.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in Arguments(_launch))
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                _child = System.Diagnostics.Process.Start(start);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                LogStartFailed(Side.Value, exception.Message);
                return Task.FromResult(Result<Unit, string>.Failure("the " + Side.Value + " side could not be started: " + exception.Message));
            }

            if (_child is null)
            {
                return Task.FromResult(Result<Unit, string>.Failure("the " + Side.Value + " side could not be started: wsl.exe did not start"));
            }

            LogStarted(Side.Value, _launch.Distribution);
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }
    }

    /// <summary>The command line, separated so a test can assert it without spawning anything.</summary>
    public static IReadOnlyList<string> Arguments(PeerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return
        [
            "-d", launch.Distribution,
            "-u", launch.User,
            "--exec", launch.ExecutablePath,
            "--port", launch.Port.ToString(CultureInfo.InvariantCulture),
        ];
    }

    /// <summary>
    /// The leader going away takes the side with it, which is what design 11's
    /// "the follower is its child, so down too" says. A <c>wsl.exe</c> child is
    /// not in a job object, so this is a request rather than a guarantee: the
    /// in-distro process can outlive a killed leader, and the crash table is
    /// written for exactly that.
    /// </summary>
    public void Dispose()
    {
        lock (_mutex)
        {
            try
            {
                if (_child is { HasExited: false })
                {
                    _child.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // The child is already gone, which is the outcome wanted.
            }

            _child?.Dispose();
            _child = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "started the {Side} side in {Distribution}")]
    private partial void LogStarted(string side, string distribution);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the {Side} side could not be started: {Reason}")]
    private partial void LogStartFailed(string side, string reason);
}
