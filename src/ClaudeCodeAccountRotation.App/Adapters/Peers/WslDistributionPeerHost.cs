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
/// &lt;user&gt; --exec &lt;wrapper&gt; --follower &lt;executablePath&gt; --port &lt;port&gt; [--config &lt;path&gt;]</c>.
/// The wrapper is <c>claude-code-account-rotation-follower-log</c> beside the
/// configured binary. <c>--exec</c> is that wrapper; <c>--follower</c> is the
/// binary <see cref="PeerLaunch.ExecutablePath"/> names, so a renamed build is
/// the process that actually starts. The wrapper keeps the follower's stdout.
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
    private readonly string _leaderVersion;
    private readonly ILogger<WslDistributionPeerHost> _logger;
    private readonly Lock _mutex = new();
    private System.Diagnostics.Process? _child;
    private bool _disposed;

    public WslDistributionPeerHost(SideName side, PeerLaunch launch, string leaderVersion, ILogger<WslDistributionPeerHost> logger)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaderVersion);
        ArgumentNullException.ThrowIfNull(logger);
        Side = side;
        _launch = launch;
        _leaderVersion = leaderVersion;
        _logger = logger;
    }

    public SideName Side { get; }

    public event EventHandler<PeerExitedEventArgs>? Exited;

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

            if (_disposed)
            {
                return Task.FromResult(Result<Unit, string>.Failure("the leader is stopping"));
            }

            _child?.Dispose();
            _child = null;

            System.Diagnostics.Process child = new()
            {
                StartInfo = StartInfo(_launch, _leaderVersion, Environment.GetEnvironmentVariable("WSLENV")),
                EnableRaisingEvents = true,
            };
            child.Exited += OnChildExited;
            try
            {
                if (!child.Start())
                {
                    child.Dispose();
                    return Task.FromResult(Result<Unit, string>.Failure("the " + Side.Value + " side could not be started: wsl.exe did not start"));
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                child.Dispose();
                LogStartFailed(Side.Value, exception.Message);
                return Task.FromResult(Result<Unit, string>.Failure("the " + Side.Value + " side could not be started: " + exception.Message));
            }

            _child = child;
            LogStarted(Side.Value, _launch.Distribution);
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }
    }

    /// <summary>
    /// The <c>wsl.exe</c> start, separated so a test can assert it without
    /// spawning anything. The leader's version crosses into the distribution in
    /// <see cref="FollowerUpgrade.LeaderVersionVariable"/>, which
    /// <c>WSLENV</c> must list for <c>wsl.exe</c> to pass it. Any list the
    /// leader inherited is kept. A follower that predates the variable ignores it.
    /// </summary>
    public static ProcessStartInfo StartInfo(PeerLaunch launch, string leaderVersion, string? inheritedWslEnv)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ProcessStartInfo start = new("wsl.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in Arguments(launch))
        {
            start.ArgumentList.Add(argument);
        }

        const string shared = FollowerUpgrade.LeaderVersionVariable + "/u";
        start.Environment[FollowerUpgrade.LeaderVersionVariable] = leaderVersion;
        start.Environment["WSLENV"] = string.IsNullOrEmpty(inheritedWslEnv) ? shared : inheritedWslEnv + ":" + shared;
        return start;
    }

    /// <summary>
    /// The command line, separated so a test can assert it without spawning anything.
    /// <para>
    /// <c>--exec</c> is the wrapper beside <see cref="PeerLaunch.ExecutablePath"/>.
    /// <c>--follower</c> is that path, so the wrapper runs the configured binary
    /// rather than a hard-coded sibling name. The configured path is a Linux path
    /// spelled on Windows, so the sibling wrapper is built by splitting on
    /// <c>/</c> and rejoining with <c>/</c>.
    /// <see cref="Path.GetDirectoryName(string)"/> would emit backslashes.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Arguments(PeerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        List<string> arguments =
        [
            "-d", launch.Distribution,
            "-u", launch.User,
            "--exec", WrapperPath(launch.ExecutablePath),
            "--follower", launch.ExecutablePath,
            "--port", launch.Port.ToString(CultureInfo.InvariantCulture),
        ];
        if (!string.IsNullOrWhiteSpace(launch.ConfigPath))
        {
            arguments.Add("--config");
            arguments.Add(launch.ConfigPath);
        }

        return arguments;
    }

    /// <summary>The follower-log wrapper in the same directory as <paramref name="executablePath"/>.</summary>
    private static string WrapperPath(string executablePath)
    {
        string[] segments = executablePath.Split('/');
        segments[^1] = "claude-code-account-rotation-follower-log";
        return string.Join('/', segments);
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
            // Before the kill, so the exit it causes is not reported as one to act on.
            _disposed = true;
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

    /// <summary>
    /// Reports the exit of the child this host still tracks. An exit after
    /// <see cref="Dispose"/>, or of a child a later start has replaced, is not reported.
    /// </summary>
    private void OnChildExited(object? sender, EventArgs e)
    {
        int exitCode;
        lock (_mutex)
        {
            if (_disposed || sender is not System.Diagnostics.Process exited || !ReferenceEquals(exited, _child))
            {
                return;
            }

            try
            {
                exitCode = exited.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        LogExited(Side.Value, exitCode);
        Exited?.Invoke(this, new PeerExitedEventArgs(exitCode));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "the {Side} side exited with code {ExitCode}")]
    private partial void LogExited(string side, int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "started the {Side} side in {Distribution}")]
    private partial void LogStarted(string side, string distribution);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the {Side} side could not be started: {Reason}")]
    private partial void LogStartFailed(string side, string reason);
}
