using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class ScheduledTaskRemoteControlTests
{
    private const string Running = "\"\\claude-remote-control\",\"N/A\",\"Running\"\r\n";
    private const string Ready = "\"\\claude-remote-control\",\"N/A\",\"Ready\"\r\n";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ScheduledTaskRemoteControl RemoteControl(Script script, TimeSpan? stopBound = null) =>
        new("claude-remote-control", script.Run, TimeProvider.System, stopBound ?? TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task RunningIsReadFromTheStatusColumn()
    {
        (await RemoteControl(new Script().Answer("/Query", 0, Running)).IsRunningAsync(Token)).ShouldBeTrue();
        (await RemoteControl(new Script().Answer("/Query", 0, Ready)).IsRunningAsync(Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task AMissingTaskOrAQueryThatCannotRunIsNotRunning()
    {
        (await RemoteControl(new Script().Answer("/Query", 1, "ERROR: The system cannot find the file specified.")).IsRunningAsync(Token)).ShouldBeFalse();
        (await RemoteControl(new Script().Answer("/Query", Result<(int, string), string>.Failure("schtasks.exe could not be started"))).IsRunningAsync(Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task ARestartEndsWaitsUntilTheTaskStopsAndRunsItAgain()
    {
        Script script = new Script()
            .Answer("/End", 0)
            .Answer("/Query", 0, Running).Answer("/Query", 0, Running).Answer("/Query", 0, Ready)
            .Answer("/Run", 0);

        Result<Unit, string> restart = await RemoteControl(script).RestartAsync(Token);

        restart.IsSuccess.ShouldBeTrue(restart.IsFailure ? restart.Error : "");
        script.Calls.Select(static call => call.Split(' ')[0]).ShouldBe(["/End", "/Query", "/Query", "/Query", "/Run"]);
        script.Calls[0].ShouldBe("/End /TN claude-remote-control");
        script.Calls[^1].ShouldBe("/Run /TN claude-remote-control");
    }

    [Fact]
    public async Task ATaskThatNeverStopsIsNotRunAgain()
    {
        Script script = new Script().Answer("/End", 0).Answer("/Query", 0, Running).Answer("/Run", 0);

        Result<Unit, string> restart = await RemoteControl(script, TimeSpan.FromMilliseconds(50)).RestartAsync(Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("still running");
        script.Calls.ShouldNotContain(static call => call.StartsWith("/Run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEndThatIsRefusedStopsTheRestartWithTheReason()
    {
        Script script = new Script().Answer("/End", 1, "ERROR: Access is denied.").Answer("/Run", 0);

        Result<Unit, string> restart = await RemoteControl(script).RestartAsync(Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("Access is denied");
        script.Calls.ShouldBe(["/End /TN claude-remote-control"]);
    }

    [Fact]
    public async Task ARunThatIsRefusedIsAFailure()
    {
        Script script = new Script().Answer("/End", 0).Answer("/Query", 0, Ready).Answer("/Run", 1, "ERROR: Access is denied.");

        Result<Unit, string> restart = await RemoteControl(script).RestartAsync(Token);

        restart.IsFailure.ShouldBeTrue();
        restart.Error.ShouldContain("/Run");
    }

    /// <summary>A scripted schtasks.exe: each call takes the next answer for its verb, the last one repeating, and is recorded.</summary>
    private sealed class Script
    {
        private readonly Dictionary<string, Queue<Result<(int, string), string>>> _answers = [];

        public List<string> Calls { get; } = [];

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
            Calls.Add(string.Join(' ', arguments));
            Queue<Result<(int, string), string>> queue = _answers[arguments[0]];
            return Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        }
    }
}
