using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>What one child process run returned, or why it could not run.</summary>
internal delegate Task<Result<(int ExitCode, string Output), string>> CommandRunner(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>Every process on the machine, or why they could not be listed.</summary>
internal delegate Task<Result<IReadOnlyList<ProcessEntry>, string>> ProcessLister(CancellationToken cancellationToken);

/// <summary>Ends one recorded process and waits, bounded, for it to exit. Success when it is already gone.</summary>
internal delegate Task<Result<Unit, string>> ProcessKiller(ProcessEntry process, CancellationToken cancellationToken);

/// <summary>One row of the process table: <c>Win32_Process</c>'s id, parent id, image name and command line.</summary>
internal sealed record ProcessEntry(int Id, int ParentId, string Name, string? CommandLine);

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
/// <c>/End</c>, <c>/Run</c>.
/// <para>
/// <c>/End</c> ends only the task's root process. Under it the provisioning
/// task runs <c>conhost.exe --headless</c>, then <c>pwsh.exe</c>, then the
/// <c>claude.exe</c> that is the server, and those two keep running, so a
/// <c>/Run</c> after <c>/End</c> alone starts a second server. The restart
/// therefore records the task's process tree first: the process whose command
/// line carries the task action's own arguments, read from <c>/Query /XML</c>,
/// and its descendants. A <c>claude remote-control</c> started in a terminal
/// has no such ancestor and is never touched. After <c>/End</c> the recorded
/// descendants still running are ended, deepest first; only those ids. A
/// running task with no such tree is neither ended nor run, and the restart
/// fails so the operator is told.
/// </para>
/// <para>
/// The task is registered with MultipleInstances IgnoreNew, so a run sent
/// while the old instance is still stopping is dropped; the restart polls
/// until the task is no longer Running, within <see cref="StopBound"/>, before
/// it runs it again.
/// </para>
/// <para>
/// <c>/Query</c>'s status column is the English word on an English Windows;
/// another display language reads as not running, which only skips the
/// confirmation and the restart.
/// </para>
/// </summary>
internal sealed partial class ScheduledTaskRemoteControl(
    string name,
    CommandRunner schtasks,
    ProcessLister listProcesses,
    ProcessKiller kill,
    TimeProvider timeProvider,
    TimeSpan stopBound,
    TimeSpan pollInterval) : IRemoteControlTask
{
    public static readonly TimeSpan StopBound = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _killWait = TimeSpan.FromSeconds(5);

    public string? Name => name;

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        Result<(int ExitCode, string Output), string> query = await schtasks(["/Query", "/TN", name, "/FO", "CSV", "/NH"], cancellationToken);
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
        Result<(int ExitCode, string Output), string> definition = await schtasks(["/Query", "/TN", name, "/XML"], cancellationToken);
        if (definition.IsFailure)
        {
            return Result<Unit, string>.Failure(definition.Error);
        }

        (string Command, string Arguments)? action = definition.Value.ExitCode == 0 ? Action(definition.Value.Output) : null;
        if (action is null)
        {
            return Result<Unit, string>.Failure("the task's action could not be read: " + definition.Value.Output.Trim());
        }

        Result<IReadOnlyList<ProcessEntry>, string> table = await listProcesses(cancellationToken);
        if (table.IsFailure)
        {
            return Result<Unit, string>.Failure("the task's processes could not be listed: " + table.Error);
        }

        IReadOnlyList<ProcessEntry> tree = TaskTree(table.Value, action.Value.Command, action.Value.Arguments);
        if (tree.Count == 0)
        {
            // A running task whose processes cannot be told apart is left as it
            // is: /End alone would leave its server running, and /Run would then
            // start a second one. A task that has stopped by itself is just run.
            if (await IsRunningAsync(cancellationToken))
            {
                return Result<Unit, string>.Failure("the task is running but its processes could not be identified");
            }

            return await StepAsync("/Run", cancellationToken);
        }

        Result<Unit, string> ended = await StepAsync("/End", cancellationToken);
        if (ended.IsFailure)
        {
            return ended;
        }

        // Deepest first: the server before the shell that started it.
        List<string> survivors = [];
        foreach (ProcessEntry process in tree.Reverse())
        {
            Result<Unit, string> killed = await kill(process, cancellationToken);
            if (killed.IsFailure)
            {
                survivors.Add(process.Name + " (process " + process.Id + "): " + killed.Error);
            }
        }

        if (survivors.Count > 0)
        {
            return Result<Unit, string>.Failure("the task was ended but could not end " + string.Join("; ", survivors));
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

    /// <summary>The first <c>Exec</c> action's command and arguments from <c>/Query /XML</c>, or null when it has none.</summary>
    internal static (string Command, string Arguments)? Action(string xml)
    {
        Match command = CommandElement().Match(xml);
        if (!command.Success)
        {
            return null;
        }

        Match arguments = ArgumentsElement().Match(xml);
        return (WebUtility.HtmlDecode(command.Groups[1].Value.Trim()), arguments.Success ? WebUtility.HtmlDecode(arguments.Groups[1].Value.Trim()) : string.Empty);
    }

    /// <summary>
    /// The task's running tree in breadth-first order, root first: every process
    /// whose image is the action's command and whose command line carries the
    /// action's arguments, and every descendant of those. Empty when the
    /// action has no arguments to tell its process from any other.
    /// </summary>
    internal static IReadOnlyList<ProcessEntry> TaskTree(IReadOnlyList<ProcessEntry> table, string command, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        string image = Path.GetFileName(command.Replace('\\', '/'));
        List<ProcessEntry> tree = [.. table.Where(process =>
            string.Equals(process.Name, image, StringComparison.OrdinalIgnoreCase)
            && process.CommandLine is string line
            && line.Contains(arguments, StringComparison.OrdinalIgnoreCase))];
        HashSet<int> seen = [.. tree.Select(static process => process.Id)];
        for (int index = 0; index < tree.Count; index++)
        {
            int parent = tree[index].Id;
            foreach (ProcessEntry child in table.Where(process => process.ParentId == parent && seen.Add(process.Id)))
            {
                tree.Add(child);
            }
        }

        return tree;
    }

    private async Task<Result<Unit, string>> StepAsync(string verb, CancellationToken cancellationToken)
    {
        Result<(int ExitCode, string Output), string> step = await schtasks([verb, "/TN", name], cancellationToken);
        if (step.IsFailure)
        {
            return Result<Unit, string>.Failure(step.Error);
        }

        return step.Value.ExitCode == 0
            ? Result<Unit, string>.Success(Unit.Value)
            : Result<Unit, string>.Failure("schtasks " + verb + " exited with code " + step.Value.ExitCode + ": " + step.Value.Output.Trim());
    }

    /// <summary>
    /// An executable from the system directory, never from PATH, with its
    /// arguments as a list and a bounded wait. Its combined output is returned.
    /// </summary>
    public static CommandRunner WindowsRunner(string executable) => async (arguments, cancellationToken) =>
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
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
            return Result<(int, string), string>.Failure(Path.GetFileName(executable) + " could not be started: " + exception.Message);
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
            return Result<(int, string), string>.Failure(Path.GetFileName(executable) + " did not finish within " + _processTimeout.TotalSeconds + " s");
        }
    };

    /// <summary>
    /// <c>Win32_Process</c> through Windows PowerShell's <c>Get-CimInstance</c>:
    /// .NET's own process API has neither the parent id nor the command line.
    /// One run per restart, never per poll.
    /// </summary>
    public static ProcessLister WindowsProcessLister(string systemDirectory)
    {
        CommandRunner powershell = WindowsRunner(Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        return async cancellationToken =>
        {
            Result<(int ExitCode, string Output), string> listed = await powershell(
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::OutputEncoding = [Text.Encoding]::UTF8; "
                    + "ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CommandLine)",
                ],
                cancellationToken);
            if (listed.IsFailure)
            {
                return Result<IReadOnlyList<ProcessEntry>, string>.Failure(listed.Error);
            }

            if (listed.Value.ExitCode != 0)
            {
                return Result<IReadOnlyList<ProcessEntry>, string>.Failure("powershell.exe exited with code " + listed.Value.ExitCode);
            }

            return ParseProcessTable(listed.Value.Output);
        };
    }

    internal static Result<IReadOnlyList<ProcessEntry>, string> ParseProcessTable(string json)
    {
        try
        {
            List<CimProcess?>? rows = JsonSerializer.Deserialize<List<CimProcess?>>(json);
            if (rows is null)
            {
                return Result<IReadOnlyList<ProcessEntry>, string>.Failure("the process table was empty");
            }

            return Result<IReadOnlyList<ProcessEntry>, string>.Success([.. rows
                .Where(static row => row?.Name is not null)
                .Select(static row => new ProcessEntry(row!.ProcessId, row.ParentProcessId, row.Name!, row.CommandLine))]);
        }
        catch (JsonException exception)
        {
            return Result<IReadOnlyList<ProcessEntry>, string>.Failure("the process table could not be read: " + exception.Message);
        }
    }

    /// <summary>
    /// Ends one recorded process. An id now held by a process of another name
    /// was reused after the recorded one exited, and is left alone.
    /// </summary>
    public static async Task<Result<Unit, string>> KillRecordedAsync(ProcessEntry recorded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(recorded.Id);
        }
        catch (ArgumentException)
        {
            return Result<Unit, string>.Success(Unit.Value);
        }

        using (process)
        {
            try
            {
                if (!string.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(recorded.Name), StringComparison.OrdinalIgnoreCase))
                {
                    return Result<Unit, string>.Success(Unit.Value);
                }

                process.Kill();
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(_killWait);
                await process.WaitForExitAsync(budget.Token);
                return Result<Unit, string>.Success(Unit.Value);
            }
            catch (InvalidOperationException)
            {
                // Exited between the lookup and the kill.
                return Result<Unit, string>.Success(Unit.Value);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                return Result<Unit, string>.Failure(exception.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Result<Unit, string>.Failure("did not exit within " + _killWait.TotalSeconds + " s");
            }
        }
    }

    /// <summary>One <c>Win32_Process</c> row as <c>ConvertTo-Json</c> writes it.</summary>
    private sealed record CimProcess(int ProcessId, int ParentProcessId, string? Name, string? CommandLine);

    [GeneratedRegex("<Command>(.*?)</Command>", RegexOptions.Singleline)]
    private static partial Regex CommandElement();

    [GeneratedRegex("<Arguments>(.*?)</Arguments>", RegexOptions.Singleline)]
    private static partial Regex ArgumentsElement();
}
