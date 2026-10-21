using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>What one child process run returned, or why it could not run.</summary>
internal delegate Task<Result<(int ExitCode, string Output), string>> CommandRunner(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>Every process on the machine, or why they could not be listed.</summary>
internal delegate Task<Result<IReadOnlyList<ProcessEntry>, string>> ProcessLister(CancellationToken cancellationToken);

/// <summary>Ends one recorded process and waits, bounded, for it to exit. Success when it is already gone.</summary>
internal delegate Task<Result<Unit, string>> ProcessKiller(ProcessEntry process, CancellationToken cancellationToken);

/// <summary>
/// One row of the process table: <c>Win32_Process</c>'s id, parent id, image
/// name, command line and creation time. No time is a row that did not report one.
/// </summary>
internal sealed record ProcessEntry(int Id, int ParentId, string Name, string? CommandLine, DateTimeOffset? Started = null);

/// <summary>No task configured, or not Windows: never running, and nothing to restart.</summary>
internal sealed class NoRemoteControlTask : IRemoteControlTask
{
    public string? Name => null;

    public Task<Result<bool, string>> IsRunningAsync(CancellationToken cancellationToken) => Task.FromResult(Result<bool, string>.Success(false));

    public Task<Result<Unit, string>> RestartAsync(bool startIfStopped, CancellationToken cancellationToken) =>
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
/// has no such ancestor and is not touched while its terminal runs. After <c>/End</c> the recorded
/// descendants still running are ended, deepest first; only those ids. A
/// running task with no such tree is neither ended nor run, and the restart
/// fails so the operator is told.
/// </para>
/// <para>
/// The task's server can outlive the shell that started it: then its parent
/// id names no process, and no walk down from the task reaches it. A
/// <c>claude.exe</c> whose arguments are exactly the server's, as the action's
/// command names them, and whose parent is gone, or is a later process holding
/// a reused id, is the task's too. It is ended with the tree, and the task is
/// never run while one is left. A server with a live parent outside the tree
/// is someone else's.
/// </para>
/// <para>
/// The task is registered with MultipleInstances IgnoreNew, so a run sent
/// while the old instance is still stopping is dropped; the restart polls
/// until the task is no longer Running, within <see cref="StopBound"/>, before
/// it runs it again.
/// </para>
/// <para>
/// The task's state is read through Windows PowerShell's ScheduledTasks
/// module, whose <c>State</c> is an enum, never from schtasks' text, which
/// follows the display language. <c>/End</c> and <c>/Run</c> are judged by
/// exit code alone.
/// </para>
/// </summary>
internal sealed partial class ScheduledTaskRemoteControl(
    string name,
    CommandRunner schtasks,
    CommandRunner powershell,
    ProcessLister listProcesses,
    ProcessKiller kill,
    TimeProvider timeProvider,
    TimeSpan stopBound,
    TimeSpan pollInterval,
    ILogger<ScheduledTaskRemoteControl> logger,
    Func<string, string>? expandEnvironment = null) : IRemoteControlTask
{
    private readonly Func<string, string> _expand = expandEnvironment ?? Environment.ExpandEnvironmentVariables;

    public static readonly TimeSpan StopBound = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan _processTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _killWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far a live process's start time may sit from the recorded one and
    /// still be that process: CIM reports microseconds where the process API
    /// reports 100 ns. An id reused by a later process starts far outside it.
    /// </summary>
    private static readonly TimeSpan _startTolerance = TimeSpan.FromSeconds(1);

    public string? Name => name;

    /// <summary>
    /// Whether the task is Running, or has stopped while a server of its own,
    /// orphaned when its shell exited, still runs: either needs the restart,
    /// which ends that server before it runs the task. The process table is
    /// read only for a task found stopped, once per call.
    /// </summary>
    public async Task<Result<bool, string>> IsRunningAsync(CancellationToken cancellationToken)
    {
        Result<bool, string> status = await StatusAsync(cancellationToken);
        if (status.IsFailure || status.Value)
        {
            return status;
        }

        Result<(int ExitCode, string Output), string> definition = await schtasks(["/Query", "/TN", name, "/XML"], cancellationToken);
        if (definition.IsFailure)
        {
            return Result<bool, string>.Failure("the task is not running, but its action could not be read: " + definition.Error);
        }

        (string Command, string Arguments)? action = definition.Value.ExitCode == 0 ? Action(definition.Value.Output) : null;
        if (action is null)
        {
            // Not registered, or no action to match a server by: nothing of its own can be running.
            return status;
        }

        Result<IReadOnlyList<ProcessEntry>, string> table = await listProcesses(cancellationToken);
        if (table.IsFailure)
        {
            return Result<bool, string>.Failure("the task is not running, but its processes could not be listed: " + table.Error);
        }

        return Result<bool, string>.Success(Orphans(table.Value, action.Value.Arguments, _expand).Count > 0);
    }

    /// <summary>
    /// Whether the task is Running, or why that could not be read, from the
    /// ScheduledTasks module's <c>State</c>: an enum, printed as its number, so
    /// no display language changes the answer. No output is a task that is not
    /// registered, which is not running; a run that fails or exits non-zero is
    /// a failure, and a restart never takes it for stopped.
    /// </summary>
    private async Task<Result<bool, string>> StatusAsync(CancellationToken cancellationToken)
    {
        Result<(int ExitCode, string Output), string> query = await powershell(
            ["-NoProfile", "-NonInteractive", "-Command", StateScript(name)],
            cancellationToken);
        if (query.IsFailure)
        {
            return Result<bool, string>.Failure(query.Error);
        }

        if (query.Value.ExitCode != 0)
        {
            return Result<bool, string>.Failure("Get-ScheduledTask exited with code " + query.Value.ExitCode + ": " + query.Value.Output.Trim());
        }

        string state = query.Value.Output.Trim();
        if (state.Length == 0)
        {
            return Result<bool, string>.Success(false);
        }

        // One task in the root folder by its exact name prints one line; more
        // than one is not the task /End and /Run will name.
        if (state.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 1)
        {
            return Result<bool, string>.Failure("Get-ScheduledTask matched more than one task: " + state.ReplaceLineEndings(", "));
        }

        return int.TryParse(state, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
            ? Result<bool, string>.Success(number == RunningState)
            : Result<bool, string>.Failure("Get-ScheduledTask printed an unexpected state: " + state);
    }

    /// <summary><c>Microsoft.Management.Infrastructure.CimInstance</c>'s <c>StateEnum</c>: Running is 4.</summary>
    internal const int RunningState = 4;

    /// <summary>The state query, with the task name as a single-quoted PowerShell literal.</summary>
    internal static string StateScript(string taskName) =>
        "$task = Get-ScheduledTask -TaskPath '\\' -TaskName '" + taskName.Replace("'", "''", StringComparison.Ordinal)
        + "' -ErrorAction SilentlyContinue; foreach ($t in @($task)) { [int]$t.State }";

    public async Task<Result<Unit, string>> RestartAsync(bool startIfStopped, CancellationToken cancellationToken)
    {
        Result<Unit, string> restart = await RestartCoreAsync(startIfStopped, cancellationToken);
        if (restart.IsSuccess)
        {
            LogRestarted(name);
        }
        else
        {
            LogRestartFailed(name, restart.Error);
        }

        return restart;
    }

    private async Task<Result<Unit, string>> RestartCoreAsync(bool startIfStopped, CancellationToken cancellationToken)
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

        IReadOnlyList<ProcessEntry> roots = Roots(table.Value, action.Value.Command, action.Value.Arguments, _expand);
        IReadOnlyList<ProcessEntry> orphans = Orphans(table.Value, action.Value.Arguments, _expand);
        foreach (ProcessEntry orphan in orphans)
        {
            LogOrphan(name, orphan.Name, orphan.Id, orphan.ParentId);
        }

        IReadOnlyList<ProcessEntry> tree = Walk(table.Value, [.. roots, .. orphans]);
        if (roots.Count == 0)
        {
            // A running task whose processes cannot be told apart is left as it
            // is: /End alone would leave its server running, and /Run would then
            // start a second one. A task that has stopped by itself is run only
            // when it was known to be running before the switch: a task whose
            // earlier state was unknown may be one the operator stopped. Either
            // way it is run only once no server of its own is left.
            Result<bool, string> status = await StatusAsync(cancellationToken);
            if (status.IsFailure)
            {
                return Result<Unit, string>.Failure("the task's state could not be read: " + status.Error);
            }

            if (status.Value)
            {
                return Result<Unit, string>.Failure("the task is running but its processes could not be identified");
            }

            if (!startIfStopped)
            {
                return Result<Unit, string>.Failure("the task is not running and its state before the switch could not be read, so it was not started");
            }

            Result<Unit, string> cleared = await EndAllAsync(tree, "the task had stopped but could not end ", cancellationToken);
            return cleared.IsFailure ? cleared : await StepAsync("/Run", cancellationToken);
        }

        Result<Unit, string> ended = await StepAsync("/End", cancellationToken);
        if (ended.IsFailure)
        {
            return ended;
        }

        Result<Unit, string> endedAll = await EndAllAsync(tree, "the task was ended but could not end ", cancellationToken);
        if (endedAll.IsFailure)
        {
            return endedAll;
        }

        // Only a status read as not Running lets the run go: a run sent while the
        // old instance is still stopping is dropped by IgnoreNew yet exits 0, so a
        // query that failed is retried, never taken for stopped.
        long started = timeProvider.GetTimestamp();
        while (true)
        {
            Result<bool, string> status = await StatusAsync(cancellationToken);
            if (status.IsSuccess && !status.Value)
            {
                return await StepAsync("/Run", cancellationToken);
            }

            if (timeProvider.GetElapsedTime(started) >= stopBound)
            {
                return Result<Unit, string>.Failure(status.IsFailure
                    ? "the task's state could not be read within " + stopBound.TotalSeconds + " s of ending it: " + status.Error
                    : "the task was still running " + stopBound.TotalSeconds + " s after it was ended");
            }

            await Task.Delay(pollInterval, timeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// Ends each recorded process, deepest first: the server before the shell
    /// that started it. Any that could not be ended fail the whole, so nothing
    /// runs the task beside it.
    /// </summary>
    private async Task<Result<Unit, string>> EndAllAsync(IReadOnlyList<ProcessEntry> tree, string failure, CancellationToken cancellationToken)
    {
        List<string> survivors = [];
        foreach (ProcessEntry process in tree.Reverse())
        {
            Result<Unit, string> killed = await kill(process, cancellationToken);
            if (killed.IsFailure)
            {
                survivors.Add(process.Name + " (process " + process.Id + "): " + killed.Error);
            }
        }

        return survivors.Count > 0
            ? Result<Unit, string>.Failure(failure + string.Join("; ", survivors))
            : Result<Unit, string>.Success(Unit.Value);
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
    /// The task's running tree in breadth-first order, roots first: every
    /// process whose image is the action's command and whose command line
    /// carries the action's arguments, every orphaned server of the task, and
    /// every descendant of those. Empty when the action has no arguments to
    /// tell its process from any other.
    /// </summary>
    internal static IReadOnlyList<ProcessEntry> TaskTree(IReadOnlyList<ProcessEntry> table, string command, string arguments, Func<string, string> expand) =>
        Walk(table, [.. Roots(table, command, arguments, expand), .. Orphans(table, arguments, expand)]);

    /// <summary>
    /// The processes the task itself started: image the action's command,
    /// command line carrying the action's arguments.
    /// <para>
    /// Names are compared without <c>.exe</c>, because a task may name its
    /// command <c>pwsh</c> while the process table says <c>pwsh.exe</c>; and
    /// the arguments match as written or with environment variables expanded,
    /// as Task Scheduler starts them.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ProcessEntry> Roots(IReadOnlyList<ProcessEntry> table, string command, string arguments, Func<string, string> expand)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        string image = ImageName(expand(command));
        string expanded = expand(arguments);
        return [.. table.Where(process =>
            string.Equals(ImageName(process.Name), image, StringComparison.OrdinalIgnoreCase)
            && process.CommandLine is string line
            && (line.Contains(arguments, StringComparison.OrdinalIgnoreCase) || line.Contains(expanded, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>
    /// The task's servers no walk from its roots reaches: a <c>claude.exe</c>
    /// whose arguments are exactly those the action passes to <c>claude</c>,
    /// and whose parent is not in the table or started after it, which is an
    /// id reused once the real parent exited. A server with a live parent is
    /// either in the tree or someone else's, and is not one.
    /// </summary>
    private static IReadOnlyList<ProcessEntry> Orphans(IReadOnlyList<ProcessEntry> table, string arguments, Func<string, string> expand)
    {
        Match server = ServerArguments().Match(expand(arguments));
        if (!server.Success)
        {
            return [];
        }

        string expected = Spaced(server.Groups[1].Value);
        var byId = table.GroupBy(static process => process.Id).ToDictionary(static group => group.Key, static group => group.First());
        return [.. table.Where(process =>
            string.Equals(ImageName(process.Name), "claude", StringComparison.OrdinalIgnoreCase)
            && process.CommandLine is string line
            && LeadingImage().Match(line) is { Success: true } image
            && string.Equals(Spaced(line[image.Length..]), expected, StringComparison.OrdinalIgnoreCase)
            && (!byId.TryGetValue(process.ParentId, out ProcessEntry? parent)
                || parent.Started > process.Started))];
    }

    /// <summary>The given roots and every descendant of them, breadth first, each once.</summary>
    private static List<ProcessEntry> Walk(IReadOnlyList<ProcessEntry> table, IReadOnlyList<ProcessEntry> roots)
    {
        HashSet<int> seen = [];
        List<ProcessEntry> tree = [.. roots.Where(process => seen.Add(process.Id))];
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

    /// <summary>Arguments with their runs of white space as one space, ends trimmed.</summary>
    private static string Spaced(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A path's file name without directory, quotes or a trailing <c>.exe</c>.</summary>
    private static string ImageName(string path)
    {
        string file = Path.GetFileName(path.Trim().Trim('"').Replace('\\', '/'));
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
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
    public static ProcessLister WindowsProcessLister(CommandRunner powershell) =>
        async cancellationToken =>
        {
            Result<(int ExitCode, string Output), string> listed = await powershell(
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::OutputEncoding = [Text.Encoding]::UTF8; "
                    + "ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CommandLine,"
                    + "@{ Name = 'Started'; Expression = { if ($_.CreationDate) { $_.CreationDate.ToFileTime() } } })",
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

    /// <summary>Windows PowerShell from the system directory, never from PATH: present on every Windows, with the ScheduledTasks and CIM cmdlets.</summary>
    public static CommandRunner WindowsPowerShell(string systemDirectory) =>
        WindowsRunner(Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));

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
                .Select(static row => new ProcessEntry(
                    row!.ProcessId,
                    row.ParentProcessId,
                    row.Name!,
                    row.CommandLine,
                    row.Started is long started ? DateTimeOffset.FromFileTime(started).ToUniversalTime() : null))]);
        }
        catch (JsonException exception)
        {
            return Result<IReadOnlyList<ProcessEntry>, string>.Failure("the process table could not be read: " + exception.Message);
        }
    }

    /// <summary>
    /// Ends one recorded process. An id now held by a process of another name
    /// was reused after the recorded one exited, and is left alone. One of the
    /// same name started at another time is left alone too, but as a failure:
    /// a start time read wrongly must never let the task run beside its server.
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

                if (!IsRecorded(recorded, process.ProcessName, recorded.Started is null ? null : new DateTimeOffset(process.StartTime)))
                {
                    return Result<Unit, string>.Failure("its id is held by a process started at another time, which was left running");
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

    /// <summary>
    /// Whether a live process is the recorded one: the same image name and,
    /// when a start time was recorded, a start within <see cref="_startTolerance"/> of it.
    /// </summary>
    internal static bool IsRecorded(ProcessEntry recorded, string liveName, DateTimeOffset? liveStarted) =>
        string.Equals(liveName, Path.GetFileNameWithoutExtension(recorded.Name), StringComparison.OrdinalIgnoreCase)
        && (recorded.Started is not DateTimeOffset started
            || (liveStarted is DateTimeOffset live && (live - started).Duration() <= _startTolerance));

    /// <summary>One <c>Win32_Process</c> row as <c>ConvertTo-Json</c> writes it, its creation time as a UTC file time.</summary>
    private sealed record CimProcess(int ProcessId, int ParentProcessId, string? Name, string? CommandLine, long? Started);

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote Control task {Task} restarted")]
    private partial void LogRestarted(string task);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote Control task {Task} was not restarted: {Reason}")]
    private partial void LogRestartFailed(string task, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote Control task {Task} left its server {Image} (process {Process}) running after its parent {Parent} exited")]
    private partial void LogOrphan(string task, string image, int process, int parent);

    [GeneratedRegex("<Command>(.*?)</Command>", RegexOptions.Singleline)]
    private static partial Regex CommandElement();

    [GeneratedRegex("<Arguments>(.*?)</Arguments>", RegexOptions.Singleline)]
    private static partial Regex ArgumentsElement();

    /// <summary>What the action passes to <c>claude</c>: from <c>remote-control</c> to the closing quote or the end.</summary>
    [GeneratedRegex("""(?:^|[\s"'\\/])claude(?:\.exe)?"?\s+(remote-control(?:\s[^"']*)?)""", RegexOptions.IgnoreCase)]
    private static partial Regex ServerArguments();

    /// <summary>A command line's executable: quoted, or up to the first <c>claude</c> or <c>claude.exe</c> that ends a word.</summary>
    [GeneratedRegex("""^\s*(?:"[^"]*"|.*?claude(?:\.exe)?(?=\s|$))""", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingImage();
}
