using System.Diagnostics;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>What one <c>schtasks.exe</c> run returned, or why it could not run.</summary>
internal delegate Task<Result<(int ExitCode, string Output), string>> SchtasksRunner(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>No task configured, or not Windows: never running, and nothing to restart.</summary>
internal sealed class NoRemoteControlTask : IRemoteControlTask
{
    public string? Name => null;

    public Task<bool> IsRunningAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<Result<Unit, string>> RestartAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result<Unit, string>.Failure("no Remote Control task is configured"));
}

/// <summary>
/// The Remote Control task through <c>schtasks.exe</c>: <c>/Query</c>,
/// <c>/End</c>, <c>/Run</c>. The task is registered with MultipleInstances
/// IgnoreNew, so a run sent while the old instance is still stopping is
/// dropped; the restart therefore polls until the task is no longer Running,
/// within <see cref="StopBound"/>, before it runs it again.
/// <para>
/// <c>/Query</c>'s status column is the English word on an English Windows;
/// another display language reads as not running, which only skips the
/// confirmation and the restart.
/// </para>
/// </summary>
internal sealed class ScheduledTaskRemoteControl(string name, SchtasksRunner run, TimeProvider timeProvider, TimeSpan stopBound, TimeSpan pollInterval) : IRemoteControlTask
{
    public static readonly TimeSpan StopBound = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(10);

    public string? Name => name;

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        Result<(int ExitCode, string Output), string> query = await run(["/Query", "/TN", name, "/FO", "CSV", "/NH"], cancellationToken);
        if (query.IsFailure || query.Value.ExitCode != 0)
        {
            return false;
        }

        // One row per trigger: "\name","next run","status". The status is the last field.
        return query.Value.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(static line => line.EndsWith("\"Running\"", StringComparison.Ordinal));
    }

    public async Task<Result<Unit, string>> RestartAsync(CancellationToken cancellationToken)
    {
        Result<Unit, string> ended = await StepAsync("/End", cancellationToken);
        if (ended.IsFailure)
        {
            return ended;
        }

        long started = timeProvider.GetTimestamp();
        while (await IsRunningAsync(cancellationToken))
        {
            if (timeProvider.GetElapsedTime(started) >= stopBound)
            {
                return Result<Unit, string>.Failure("the task was still running " + stopBound.TotalSeconds + " s after it was ended");
            }

            await Task.Delay(pollInterval, timeProvider, cancellationToken);
        }

        return await StepAsync("/Run", cancellationToken);
    }

    private async Task<Result<Unit, string>> StepAsync(string verb, CancellationToken cancellationToken)
    {
        Result<(int ExitCode, string Output), string> step = await run([verb, "/TN", name], cancellationToken);
        if (step.IsFailure)
        {
            return Result<Unit, string>.Failure(step.Error);
        }

        return step.Value.ExitCode == 0
            ? Result<Unit, string>.Success(Unit.Value)
            : Result<Unit, string>.Failure("schtasks " + verb + " exited with code " + step.Value.ExitCode + ": " + step.Value.Output.Trim());
    }

    /// <summary>
    /// <c>schtasks.exe</c> from the system directory, never from PATH, with its
    /// arguments as a list and a bounded wait. Its combined output is returned.
    /// </summary>
    public static SchtasksRunner WindowsRunner(string systemDirectory) => async (arguments, cancellationToken) =>
    {
        ProcessStartInfo startInfo = new(Path.Combine(systemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using System.Diagnostics.Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return Result<(int, string), string>.Failure("schtasks.exe could not be started: " + exception.Message);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_processTimeout);
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(budget.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(budget.Token);
            await process.WaitForExitAsync(budget.Token);
            return Result<(int, string), string>.Success((process.ExitCode, await output + await error));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ChildProcess.TryKill(process);
            return Result<(int, string), string>.Failure("schtasks.exe did not finish within " + _processTimeout.TotalSeconds + " s");
        }
    };
}
