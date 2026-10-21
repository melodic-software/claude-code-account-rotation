using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class ScheduledTaskRemoteControlTests
{
    private const string Running = "4\r\n";
    private const string Ready = "3\r\n";

    /// <summary>The action the provisioning task registers, as <c>/Query /XML</c> prints it.</summary>
    internal const string TaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Actions Context="Author">
            <Exec>
              <Command>%SystemRoot%\system32\conhost.exe</Command>
              <Arguments>--headless "%ProgramFiles%\PowerShell\7\pwsh.exe" -NoProfile -Command "claude remote-control --permission-mode auto"</Arguments>
              <WorkingDirectory>%USERPROFILE%\claude-remote-control</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// This test machine's environment: the variables the task XML names, with
    /// roots that are not drive letters so the machine-path gate stays clean.
    /// </summary>
    internal static string Expand(string text) => text
        .Replace("%SystemRoot%", "\\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles%", "\\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%USERPROFILE%", "\\Profiles\\someone", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The machine as observed: the task engine's conhost, the pwsh it runs, and
    /// the claude.exe server under that; beside them a terminal running its own
    /// <c>claude remote-control</c>, which no restart may touch. Command lines
    /// are expanded, as Win32_Process reports them, while the task XML is not.
    /// </summary>
    internal static IReadOnlyList<ProcessEntry> Machine { get; } =
    [
        new(4, 0, "svchost.exe", "\\Windows\\system32\\svchost.exe -k netsvcs"),
        new(10, 4, "conhost.exe", "\\Windows\\system32\\conhost.exe --headless \"\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoProfile -Command \"claude remote-control --permission-mode auto\""),
        new(20, 10, "pwsh.exe", "\"\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoProfile -Command \"claude remote-control --permission-mode auto\""),
        new(30, 20, "claude.exe", "\\Profiles\\someone\\.local\\bin\\claude.exe remote-control --permission-mode auto"),
        new(100, 4, "WindowsTerminal.exe", "wt.exe"),
        new(110, 100, "pwsh.exe", "\"\\Program Files\\PowerShell\\7\\pwsh.exe\""),
        new(120, 110, "claude.exe", "\\Profiles\\someone\\.local\\bin\\claude.exe remote-control"),
    ];

    private static ScheduledTaskRemoteControl RemoteControl(Script script, Killer? killer = null, TimeSpan? stopBound = null, IReadOnlyList<ProcessEntry>? table = null) =>
        new(
            "claude-remote-control",
            script.Run,
            script.Run,
            _ => Task.FromResult(Result<IReadOnlyList<ProcessEntry>, string>.Success(table ?? Machine)),
            (killer ?? new Killer()).Kill,
            TimeProvider.System,
            stopBound ?? TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            NullLogger<ScheduledTaskRemoteControl>.Instance,
            Expand);

    private static Script Restartable() => new Script()
        .Answer("/XML", 0, TaskXml)
        .Answer("/End", 0)
        .Answer("/Query", 0, Running).Answer("/Query", 0, Ready)
        .Answer("/Run", 0);

    [Fact]
    public async Task RunningIsReadFromTheStatusColumn()
    {
        (await RemoteControl(new Script().Answer("/Query", 0, Running)).IsRunningAsync(Token)).Value.ShouldBeTrue();
        (await RemoteControl(new Script().Answer("/Query", 0, Ready)).IsRunningAsync(Token)).Value.ShouldBeFalse();
    }

    [Fact]
    public async Task StateIsReadAsANumberWhateverLanguageSchtasksSpeaks()
    {
        // A German Windows: schtasks' own text would say "Wird ausgeführt"; the
        // state read never looks at it, only at the enum's number.
        Script running = new Script()
            .Answer("/Query", 0, "4\r\n")
            .Answer("/XML", 1, "FEHLER: Zugriff verweigert");
        Script ready = new Script().Answer("/Query", 0, "3\r\n");

        (await RemoteControl(running).IsRunningAsync(Token)).Value.ShouldBeTrue();
        (await RemoteControl(ready).IsRunningAsync(Token)).Value.ShouldBeFalse();
        running.Calls.ShouldBe(["State"]);
        running.LastStateScript.ShouldBe("$task = Get-ScheduledTask -TaskPath '\\' -TaskName 'claude-remote-control' -ErrorAction SilentlyContinue; foreach ($t in @($task)) { [int]$t.State }");
    }

    [Fact]
    public void TheStateReadLooksOnlyInTheRootFolder()
    {
        ScheduledTaskRemoteControl.StateScript("claude-remote-control").ShouldContain("-TaskPath '\\' -TaskName 'claude-remote-control'");
    }

    [Fact]
    public async Task MoreThanOneMatchingTaskIsAReadFailure()
    {
        Result<bool, string> state = await RemoteControl(new Script().Answer("/Query", 0, "4\r\n3\r\n")).IsRunningAsync(Token);

        state.IsFailure.ShouldBeTrue();
        state.Error.ShouldContain("more than one task");
    }

    [Fact]
    public void TheTaskNameIsAQuotedLiteralInTheStateScript()
    {
        ScheduledTaskRemoteControl.StateScript("it's").ShouldContain("-TaskName 'it''s'");
    }

    [Fact]
    public async Task AStateThatIsNotANumberIsAFailure()
    {
        Result<bool, string> state = await RemoteControl(new Script().Answer("/Query", 0, "Wird ausgeführt")).IsRunningAsync(Token);

        state.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnknownEarlierStateNeverStartsATaskFoundStopped()
    {
        Script script = new Script()
            .Answer("/XML", 0, TaskXml.Replace("--permission-mode auto", "--permission-mode plan", StringComparison.Ordinal))
            .Answer("/Query", 0, Ready)
            .Answer("/End", 0)
            .Answer("/Run", 0);

        Result<Unit, string> restart = await RemoteControl(script).RestartAsync(startIfStopped: false, Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("was not started");
        script.Calls.ShouldNotContain(static call => call.StartsWith("/Run", StringComparison.Ordinal) || call.StartsWith("/End", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMissingTaskIsNotRunning()
    {
        Result<bool, string> state = await RemoteControl(new Script().Answer("/Query", 0, "")).IsRunningAsync(Token);

        state.IsSuccess.ShouldBeTrue();
        state.Value.ShouldBeFalse();
    }

    [Fact]
    public async Task AStatusThatCannotBeReadIsAFailureNotStopped()
    {
        Result<bool, string> unstarted = await RemoteControl(new Script().Answer("/Query", Result<(int, string), string>.Failure("schtasks.exe could not be started"))).IsRunningAsync(Token);
        Result<bool, string> refused = await RemoteControl(new Script().Answer("/Query", 1, "ERROR: Access is denied.")).IsRunningAsync(Token);

        unstarted.IsFailure.ShouldBeTrue();
        unstarted.Error.ShouldContain("could not be started");
        refused.IsFailure.ShouldBeTrue();
        refused.Error.ShouldContain("Access is denied");
    }

    [Fact]
    public void TheActionIsReadFromTheTaskXml()
    {
        ScheduledTaskRemoteControl.Action(TaskXml).ShouldBe((
            "%SystemRoot%\\system32\\conhost.exe",
            "--headless \"%ProgramFiles%\\PowerShell\\7\\pwsh.exe\" -NoProfile -Command \"claude remote-control --permission-mode auto\""));
        ScheduledTaskRemoteControl.Action("<Task />").ShouldBeNull();
    }

    [Fact]
    public void TheTaskTreeIsTheActionsProcessAndItsDescendantsOnly()
    {
        (string command, string arguments) = ScheduledTaskRemoteControl.Action(TaskXml)!.Value;

        ScheduledTaskRemoteControl.TaskTree(Machine, command, arguments, Expand).Select(static process => process.Id).ShouldBe([10, 20, 30]);
    }

    [Fact]
    public void TheUnexpandedActionFindsTheExpandedProcessOnlyThroughTheExpansion()
    {
        (string command, string arguments) = ScheduledTaskRemoteControl.Action(TaskXml)!.Value;

        // The XML says %ProgramFiles%; the process table says \Program Files.
        arguments.ShouldContain("%ProgramFiles%");
        ScheduledTaskRemoteControl.TaskTree(Machine, command, arguments, static text => text).ShouldBeEmpty();
        ScheduledTaskRemoteControl.TaskTree(Machine, command, arguments, Expand).Select(static process => process.Id).ShouldBe([10, 20, 30]);
    }

    [Fact]
    public async Task ARestartEndsTheTaskThenTheOrphansItLeftDeepestFirstAndRunsItAgain()
    {
        Script script = Restartable();
        Killer killer = new();

        Result<Unit, string> restart = await RemoteControl(script, killer).RestartAsync(startIfStopped: true, Token);

        restart.IsSuccess.ShouldBeTrue(restart.IsFailure ? restart.Error : "");
        script.Calls.ShouldBe([
            "/Query /TN claude-remote-control /XML",
            "/End /TN claude-remote-control",
            "State",
            "State",
            "/Run /TN claude-remote-control"]);
        killer.Killed.ShouldBe([30, 20, 10]);
    }

    /// <summary>
    /// After a switch: the task's conhost and pwsh have exited, the task reads
    /// Ready, and the server they started is still running under the previous
    /// account with a parent id that no longer exists.
    /// </summary>
    private static IReadOnlyList<ProcessEntry> Orphaned { get; } = [.. Machine.Where(static process => process.Id is not (10 or 20))];

    [Fact]
    public async Task AServerWhoseTaskTreeExitedIsEndedBeforeTheTaskRunsAgain()
    {
        Script script = new Script().Answer("/XML", 0, TaskXml).Answer("/Query", 0, Ready).Answer("/End", 0).Answer("/Run", 0);
        Killer killer = new() { Log = script };

        Result<Unit, string> restart = await RemoteControl(script, killer, table: Orphaned).RestartAsync(startIfStopped: true, Token);

        restart.IsSuccess.ShouldBeTrue(restart.IsFailure ? restart.Error : "");
        script.Calls.ShouldBe([
            "/Query /TN claude-remote-control /XML",
            "State",
            "Kill 30",
            "/Run /TN claude-remote-control"]);
    }

    [Fact]
    public async Task AnOrphanedServerThatCannotBeEndedIsNeverJoinedByASecond()
    {
        Script script = new Script().Answer("/XML", 0, TaskXml).Answer("/Query", 0, Ready).Answer("/End", 0).Answer("/Run", 0);

        Result<Unit, string> restart = await RemoteControl(script, new Killer { Refuse = 30 }, table: Orphaned).RestartAsync(startIfStopped: true, Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("claude.exe (process 30): Access is denied.");
        script.Calls.ShouldNotContain(static call => call.StartsWith("/Run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOrphanLeftBesideARunningTreeIsEndedWithIt()
    {
        Killer killer = new();
        IReadOnlyList<ProcessEntry> table = [.. Machine, new(40, 25, "claude.exe", "\\Profiles\\someone\\.local\\bin\\claude.exe remote-control --permission-mode auto")];

        Result<Unit, string> restart = await RemoteControl(Restartable(), killer, table: table).RestartAsync(startIfStopped: true, Token);

        restart.IsSuccess.ShouldBeTrue(restart.IsFailure ? restart.Error : "");
        killer.Killed.ShouldBe([30, 20, 40, 10]);
    }

    [Fact]
    public async Task ATerminalServerWithTheTasksArgumentsAndALiveParentIsLeftAlone()
    {
        Script script = new Script().Answer("/XML", 0, TaskXml).Answer("/Query", 0, Ready).Answer("/End", 0).Answer("/Run", 0);
        Killer killer = new();
        IReadOnlyList<ProcessEntry> table =
        [
            .. Machine.Where(static process => process.Id is not (10 or 20 or 30 or 120)),
            new(120, 110, "claude.exe", "\\Profiles\\someone\\.local\\bin\\claude.exe remote-control --permission-mode auto"),
        ];

        Result<Unit, string> restart = await RemoteControl(script, killer, table: table).RestartAsync(startIfStopped: true, Token);

        restart.IsSuccess.ShouldBeTrue(restart.IsFailure ? restart.Error : "");
        killer.Killed.ShouldBeEmpty();
        script.Calls[^1].ShouldBe("/Run /TN claude-remote-control");
    }

    [Fact]
    public void AParentIdHeldByAProcessStartedAfterTheServerIsAReusedIdAndTheServerIsOrphaned()
    {
        DateTimeOffset logon = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        IReadOnlyList<ProcessEntry> table =
        [
            new(4, 0, "svchost.exe", null, logon.AddMinutes(-5)),
            new(20, 4, "notepad.exe", "notepad.exe", logon.AddHours(2)),
            new(30, 20, "claude.exe", "claude.exe remote-control --permission-mode auto", logon),
            new(110, 4, "pwsh.exe", "pwsh.exe", logon.AddMinutes(-1)),
            new(120, 110, "claude.exe", "claude.exe remote-control --permission-mode auto", logon),
        ];
        (string command, string arguments) = ScheduledTaskRemoteControl.Action(TaskXml)!.Value;

        ScheduledTaskRemoteControl.TaskTree(table, command, arguments, Expand).Select(static process => process.Id).ShouldBe([30]);
    }

    [Fact]
    public void OnlyTheServersOwnArgumentsMarkAnOrphan()
    {
        (string command, string arguments) = ScheduledTaskRemoteControl.Action(TaskXml)!.Value;
        IReadOnlyList<ProcessEntry> table =
        [
            new(30, 20, "claude.exe", "claude.exe remote-control"),
            new(31, 21, "claude.exe", "\"\\Program Files\\claude\\claude.exe\" remote-control --permission-mode auto --verbose"),
            new(32, 22, "node.exe", "node.exe remote-control --permission-mode auto"),
            new(33, 23, "claude.exe", "\"\\Program Files\\claude\\claude.exe\" remote-control --permission-mode auto"),
        ];

        ScheduledTaskRemoteControl.TaskTree(table, command, arguments, Expand).Select(static process => process.Id).ShouldBe([33]);
    }

    [Fact]
    public void AnIdNowHeldByAProcessStartedAtAnotherTimeIsNotTheRecordedProcess()
    {
        DateTimeOffset started = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        ProcessEntry recorded = new(30, 20, "claude.exe", "claude.exe remote-control", started);

        ScheduledTaskRemoteControl.IsRecorded(recorded, "claude", started.AddTicks(9)).ShouldBeTrue();
        ScheduledTaskRemoteControl.IsRecorded(recorded, "claude", started.ToOffset(TimeSpan.FromHours(-5))).ShouldBeTrue();
        ScheduledTaskRemoteControl.IsRecorded(recorded, "claude", started.AddMinutes(3)).ShouldBeFalse();
        ScheduledTaskRemoteControl.IsRecorded(recorded, "pwsh", started).ShouldBeFalse();
        ScheduledTaskRemoteControl.IsRecorded(recorded with { Started = null }, "claude", started.AddMinutes(3)).ShouldBeTrue();
    }

    [Fact]
    public async Task AnInteractiveRemoteControlInATerminalIsLeftAlone()
    {
        Killer killer = new();

        await RemoteControl(Restartable(), killer).RestartAsync(startIfStopped: true, Token);

        killer.Killed.ShouldNotContain(120);
        killer.Killed.ShouldNotContain(110);
    }

    [Fact]
    public async Task AnOrphanThatCannotBeEndedFailsTheRestartBeforeASecondServerStarts()
    {
        Script script = Restartable();
        Killer killer = new() { Refuse = 30 };

        Result<Unit, string> restart = await RemoteControl(script, killer).RestartAsync(startIfStopped: true, Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("claude.exe (process 30): Access is denied.");
        script.Calls.ShouldNotContain(static call => call.StartsWith("/Run", StringComparison.Ordinal));
    }

    [Fact]
    public void ACommandNamedWithoutExeStillFindsItsProcess()
    {
        ScheduledTaskRemoteControl.TaskTree(Machine, "pwsh", "-NoProfile -Command \"claude remote-control --permission-mode auto\"", Expand)
            .Select(static process => process.Id).ShouldBe([20, 30]);
    }

    [Fact]
    public async Task AStatusThatCannotBeReadAfterTheEndIsNeverTakenForStopped()
    {
        Script script = new Script()
            .Answer("/XML", 0, TaskXml)
            .Answer("/End", 0)
            .Answer("/Query", Result<(int, string), string>.Failure("schtasks.exe did not finish within 20 s"))
            .Answer("/Run", 0);

        Result<Unit, string> restart = await RemoteControl(script, stopBound: TimeSpan.FromMilliseconds(50)).RestartAsync(startIfStopped: true, Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("could not be read");
        script.Calls.ShouldNotContain(static call => call.StartsWith("/Run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStatusThatFailsOnceIsReadAgainBeforeTheRun()
    {
        Script script = new Script()
            .Answer("/XML", 0, TaskXml)
            .Answer("/End", 0)
            .Answer("/Query", 1, "ERROR: busy").Answer("/Query", 0, Ready)
            .Answer("/Run", 0);

        Result<Unit, string> restart = await RemoteControl(script).RestartAsync(startIfStopped: true, Token);

        restart.IsSuccess.ShouldBeTrue(restart.IsFailure ? restart.Error : "");
        script.Calls[^1].ShouldBe("/Run /TN claude-remote-control");
    }

    [Fact]
    public async Task ARunningTaskWhoseProcessesCannotBeIdentifiedIsNeitherEndedNorRun()
    {
        Script script = new Script()
            .Answer("/XML", 0, TaskXml.Replace("--permission-mode auto", "--permission-mode plan", StringComparison.Ordinal))
            .Answer("/Query", 0, Running)
            .Answer("/End", 0)
            .Answer("/Run", 0);
        Killer killer = new();

        Result<Unit, string> restart = await RemoteControl(script, killer).RestartAsync(startIfStopped: true, Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("could not be identified");
        killer.Killed.ShouldBeEmpty();
        script.Calls.ShouldNotContain(static call => call.StartsWith("/End", StringComparison.Ordinal) || call.StartsWith("/Run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATaskThatStoppedByItselfIsRunOnlyOnceNoServerOfItsOwnSurvives()
    {
        static Script Stopped() => new Script()
            .Answer("/XML", 0, TaskXml)
            .Answer("/Query", 0, Ready)
            .Answer("/End", 0)
            .Answer("/Run", 0);
        Script empty = Stopped();
        Script survivor = Stopped();
        Killer killer = new();

        Result<Unit, string> ran = await RemoteControl(empty, killer, table: [.. Machine.Where(static process => process.Id is not (10 or 20 or 30))])
            .RestartAsync(startIfStopped: true, Token);
        Result<Unit, string> blocked = await RemoteControl(survivor, new Killer { Refuse = 30 }, table: Orphaned)
            .RestartAsync(startIfStopped: true, Token);

        ran.IsSuccess.ShouldBeTrue(ran.IsFailure ? ran.Error : "");
        empty.Calls[^1].ShouldBe("/Run /TN claude-remote-control");
        empty.Calls.ShouldNotContain(static call => call.StartsWith("/End", StringComparison.Ordinal));
        killer.Killed.ShouldBeEmpty();
        blocked.IsFailure.ShouldBeTrue();
        survivor.Calls.ShouldNotContain(static call => call.StartsWith("/Run", StringComparison.Ordinal) || call.StartsWith("/End", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProcessTableThatCannotBeReadEndsNothing()
    {
        Script script = Restartable();
        ScheduledTaskRemoteControl remoteControl = new(
            "claude-remote-control",
            script.Run,
            script.Run,
            _ => Task.FromResult(Result<IReadOnlyList<ProcessEntry>, string>.Failure("powershell.exe exited with code 1")),
            new Killer().Kill,
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            NullLogger<ScheduledTaskRemoteControl>.Instance);

        Result<Unit, string> restart = await remoteControl.RestartAsync(startIfStopped: true, Token);

        restart.IsFailure.ShouldBeTrue();
        script.Calls.ShouldBe(["/Query /TN claude-remote-control /XML"]);
    }

    [Fact]
    public async Task ATaskThatNeverStopsIsNotRunAgain()
    {
        Script script = new Script().Answer("/XML", 0, TaskXml).Answer("/End", 0).Answer("/Query", 0, Running).Answer("/Run", 0);

        Result<Unit, string> restart = await RemoteControl(script, stopBound: TimeSpan.FromMilliseconds(50)).RestartAsync(startIfStopped: true, Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("still running");
        script.Calls.ShouldNotContain(static call => call.StartsWith("/Run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEndThatIsRefusedStopsTheRestartWithTheReasonAndKillsNothing()
    {
        Script script = new Script().Answer("/XML", 0, TaskXml).Answer("/End", 1, "ERROR: Access is denied.").Answer("/Run", 0);
        Killer killer = new();

        Result<Unit, string> restart = await RemoteControl(script, killer).RestartAsync(startIfStopped: true, Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("Access is denied");
        killer.Killed.ShouldBeEmpty();
        script.Calls.ShouldNotContain(static call => call.StartsWith("/Run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARunThatIsRefusedIsAFailure()
    {
        Script script = new Script().Answer("/XML", 0, TaskXml).Answer("/End", 0).Answer("/Query", 0, Ready).Answer("/Run", 1, "ERROR: Access is denied.");

        Result<Unit, string> restart = await RemoteControl(script).RestartAsync(startIfStopped: true, Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("/Run");
    }

    [Fact]
    public void TheProcessTableIsReadFromCimJson()
    {
        Result<IReadOnlyList<ProcessEntry>, string> table = ScheduledTaskRemoteControl.ParseProcessTable(
            """[{"ProcessId":4,"ParentProcessId":0,"Name":"System","CommandLine":null,"Started":null},{"ProcessId":30,"ParentProcessId":20,"Name":"claude.exe","CommandLine":"claude.exe remote-control","Started":134055072000000000}]""");

        table.IsSuccess.ShouldBeTrue();
        table.Value.ShouldBe([
            new ProcessEntry(4, 0, "System", null),
            new ProcessEntry(30, 20, "claude.exe", "claude.exe remote-control", new DateTimeOffset(2025, 10, 21, 8, 0, 0, TimeSpan.Zero))]);
        ScheduledTaskRemoteControl.ParseProcessTable("not json").IsFailure.ShouldBeTrue();
    }

    /// <summary>Records which ids it was asked to end, and refuses one when told to.</summary>
    internal sealed class Killer
    {
        public List<int> Killed { get; } = [];

        public int? Refuse { get; init; }

        /// <summary>A script to record each kill in, so its order against /Run shows.</summary>
        public Script? Log { get; init; }

        public Task<Result<Unit, string>> Kill(ProcessEntry process, CancellationToken cancellationToken)
        {
            Log?.Calls.Add("Kill " + process.Id);
            if (process.Id == Refuse)
            {
                return Task.FromResult(Result<Unit, string>.Failure("Access is denied."));
            }

            Killed.Add(process.Id);
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }
    }

    /// <summary>
    /// A scripted schtasks.exe and powershell.exe: each call takes the next
    /// answer for its verb (<c>/XML</c> for the definition query, <c>/Query</c>
    /// for the state read), the last one repeating, and is recorded.
    /// </summary>
    internal sealed class Script
    {
        private readonly Dictionary<string, Queue<Result<(int, string), string>>> _answers = [];

        public List<string> Calls { get; } = [];

        public string? LastStateScript { get; private set; }

        public Script Answer(string verb, int exitCode, string output = "") =>
            Answer(verb, Result<(int, string), string>.Success((exitCode, output)));

        public Script Answer(string verb, Result<(int, string), string> answer)
        {
            if (!_answers.TryGetValue(verb, out Queue<Result<(int, string), string>>? queue))
            {
                queue = new();
                _answers[verb] = queue;
            }

            queue.Enqueue(answer);
            return this;
        }

        public Task<Result<(int ExitCode, string Output), string>> Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            // The PowerShell state read answers to "/Query" and is recorded as "State".
            bool state = arguments.Contains("-Command");
            if (state)
            {
                LastStateScript = arguments[^1];
            }

            Calls.Add(state ? "State" : string.Join(' ', arguments));
            Queue<Result<(int, string), string>> queue = _answers[state ? "/Query" : arguments.Contains("/XML") ? "/XML" : arguments[0]];
            return Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        }
    }
}
