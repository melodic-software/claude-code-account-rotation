using System.ComponentModel;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Peers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Hosting;

/// <summary>
/// The follower's half of installing a release on the leader first. The leader
/// that started this follower named its own version in
/// <see cref="FollowerUpgrade.LeaderVersionVariable"/>. When the binary this
/// process runs from is replaced, the installed build is asked for its
/// <c>--version</c>. Only when that equals the leader's version does this
/// process stop, with <see cref="FollowerUpgrade.ExitCode"/>, so the leader
/// starts it again on the new build. Any other installed version is left for
/// the leader to act on when it, too, runs that build: stopping now would only
/// trade a follower that matches the running leader for one that does not.
/// <para>
/// It stops only the way <c>POST /api/shutdown</c> does: holding the mutation
/// gate, with no import journal open. A busy follower is asked again on the
/// next tick. The check is a file stat; it reads nothing over the network.
/// Without the variable, as for a follower the operator started by hand or one
/// started by a leader older than this, it does nothing.
/// </para>
/// </summary>
internal sealed partial class FollowerUpgradeWatcher(
    string? leaderVersion,
    string? executablePath,
    string ownVersion,
    Func<string, CancellationToken, Task<Result<string, string>>> installedVersion,
    Func<CancellationToken, Task<bool>> tryExit,
    TimeSpan interval,
    ILogger<FollowerUpgradeWatcher> logger) : BackgroundService
{
    private readonly ILogger<FollowerUpgradeWatcher> _logger = logger;

    // Held from an upgrade exit until the process ends, so no credential change
    // starts while the host drains.
    private static IDisposable? _heldForShutdown;

    // None until the first tick has asked the installed build for its version:
    // a build renamed in between this process starting and this watcher being
    // built is not the image this process runs, so it must not be the baseline.
    private BinaryStamp? _baseline;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(leaderVersion) || string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        using PeriodicTimer timer = new(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (await CheckOnceAsync(stoppingToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>One tick: true when this process has asked the host to stop for the installed build.</summary>
    internal async Task<bool> CheckOnceAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaderVersion) || executablePath is null)
        {
            return false;
        }

        // Missing is a replace between its unlink and its rename; the next tick sees the result.
        if (Stamp(executablePath) is not BinaryStamp current || current == _baseline)
        {
            return false;
        }

        Result<string, string> installed = await installedVersion(executablePath, cancellationToken);
        if (installed.IsFailure)
        {
            LogProbeFailed(installed.Error);
            return false;
        }

        if (string.Equals(installed.Value, ownVersion, StringComparison.Ordinal))
        {
            _baseline = current;
            return false;
        }

        if (!string.Equals(installed.Value, leaderVersion, StringComparison.Ordinal))
        {
            LogWaitingForLeader(installed.Value, leaderVersion, ownVersion);
            _baseline = current;
            return false;
        }

        if (!await tryExit(cancellationToken))
        {
            LogDeferred(installed.Value);
            return false;
        }

        LogExiting(ownVersion, installed.Value);
        return true;
    }

    /// <summary>
    /// The stop <c>POST /api/shutdown</c> makes, with the upgrade exit code:
    /// refused while another credential change holds the gate or an import
    /// journal is open. A stop keeps the gate for the rest of the process, so
    /// an import waiting on it times out instead of starting mid-shutdown.
    /// </summary>
    internal static async Task<bool> TryExitAsync(
        CredentialMutationGate gate,
        ImportJournal journal,
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken)
    {
        IDisposable? permit = null;
        try
        {
            try
            {
                permit = await gate.AcquireAsync(TimeSpan.Zero, cancellationToken);
            }
            catch (TimeoutException)
            {
                return false;
            }

            if (await journal.ReadOpenAsync(cancellationToken) is not null)
            {
                return false;
            }

            Environment.ExitCode = FollowerUpgrade.ExitCode;
            _heldForShutdown = permit;
            permit = null;
            lifetime.StopApplication();
            return true;
        }
        finally
        {
            permit?.Dispose();
        }
    }

    /// <summary>The installed binary's <c>--version</c>, bounded so a hung start cannot hold a tick.</summary>
    internal static async Task<Result<string, string>> ReadInstalledVersionAsync(string path, CancellationToken cancellationToken)
    {
        using System.Diagnostics.Process process = new();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.StartInfo.ArgumentList.Add("--version");
        try
        {
            if (!process.Start())
            {
                return Result<string, string>.Failure("the installed build did not start");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return Result<string, string>.Failure("the installed build did not start: " + exception.Message);
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(bounded.Token);
            await process.WaitForExitAsync(bounded.Token);
            string output = await stdout;
            _ = await stderr;
            string version = output.Split('\n')[0].Trim();
            return process.ExitCode == 0 && version.Length > 0
                ? Result<string, string>.Success(version)
                : Result<string, string>.Failure("the installed build did not print a version");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }

            return Result<string, string>.Failure("the installed build did not print a version within 10 s");
        }
    }

    private static BinaryStamp? Stamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            FileInfo file = new(path);
            return file.Exists ? new BinaryStamp(file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly record struct BinaryStamp(long Length, DateTime LastWriteUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the installed follower build could not be read: {Reason}")]
    private partial void LogProbeFailed(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Installed} is installed and the leader runs {Leader}; this follower stays on {Own} until the leader starts it again")]
    private partial void LogWaitingForLeader(string installed, string leader, string own);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Installed} is installed but a credential change is in progress; stopping for it once that finishes")]
    private partial void LogDeferred(string installed);

    [LoggerMessage(Level = LogLevel.Information, Message = "stopping {Own} so the leader starts the installed {Installed}")]
    private partial void LogExiting(string own, string installed);
}
