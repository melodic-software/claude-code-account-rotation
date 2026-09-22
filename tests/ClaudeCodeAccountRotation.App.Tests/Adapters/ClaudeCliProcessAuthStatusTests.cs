using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class ClaudeCliProcessAuthStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public ClaudeCliProcessAuthStatusTests() => Directory.CreateDirectory(_root);

    public static bool OnWindows => OperatingSystem.IsWindows();

    private ClaudeExecutable FakeCli(string body, string? directory = null)
    {
        string folder = directory ?? _root;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "claude.cmd");
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        return new ClaudeExecutable(Path.Combine(Environment.SystemDirectory, "cmd.exe"), [], Shim: path);
    }

    /// <summary>
    /// A stand-in CLI started the way production starts one. Windows goes through
    /// the batch shim; Unix runs a shebang script as the executable. Same
    /// <see cref="ClaudeExecutable"/> path either way, not a separate runner.
    /// </summary>
    private ClaudeExecutable ShellCli(string unixBody, string windowsBody)
    {
        if (OperatingSystem.IsWindows())
        {
            return FakeCli(windowsBody);
        }

        string path = Path.Combine(_root, "claude.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + unixBody + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new ClaudeExecutable(path, []);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "The fake CLI is a Windows batch file")]
    public async Task AShimPathHoldingACommandSeparatorIsRunAsOneOperand()
    {
        // "&" in a PATH entry or in the configured claudeExecutable must reach cmd.exe as
        // part of the script's path, never as a second command.
        ClaudeExecutable cli = FakeCli("echo {\"loggedIn\":true,\"email\":\"a@example.com\"}", Path.Combine(_root, "npm&tools"));
        ClaudeCliProcessAuthStatus reader = new(cli, TimeSpan.FromSeconds(30), new RecordingLogger<ClaudeCliProcessAuthStatus>());

        Result<ClaudeAuthStatus, string> result = await reader.ReadAsync(null, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error : "");
        result.Value.Email.ShouldBe("a@example.com");
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "The fake CLI is a Windows batch file")]
    public async Task AnArgumentHoldingACommandSeparatorRunsNothingUnderTheRealInterpreter()
    {
        // The string-level assertion lives with the locator. This one runs the command
        // line cmd.exe is actually given: "&" inside quotes must start no second
        // command, and the quoted argument must still reach the script's %* whole,
        // which is what the real npm claude.cmd forwards to node.
        ClaudeExecutable cli = FakeCli("echo ARGS=%*");
        using System.Diagnostics.Process process = new()
        {
            StartInfo = cli.StartInfo(["auth", "login", "--email", "a&whoami&b@x.com"], configDirectory: null),
        };
        process.Start();

        string output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        output.ShouldNotContain(Environment.UserName, Case.Insensitive, "whoami must never have run");
        output.ShouldContain("\"a&whoami&b@x.com\"");
        error.ShouldBeEmpty();
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "The fake CLI is a Windows batch file")]
    public async Task ParsesTheStatusJsonAndPassesTheConfigDirectory()
    {
        // The batch file forward-slashes the directory so the echoed JSON stays valid.
        ClaudeExecutable cli = FakeCli("set \"dir=%CLAUDE_CONFIG_DIR:\\=/%\"\r\necho {\"loggedIn\":true,\"email\":\"a@example.com\",\"authMethod\":\"claude.ai\",\"orgName\":\"Personal\",\"subscriptionType\":\"max\",\"projectsDirectory\":\"%dir%/projects\"}");
        ClaudeCliProcessAuthStatus reader = new(cli, TimeSpan.FromSeconds(30), new RecordingLogger<ClaudeCliProcessAuthStatus>());

        Result<ClaudeAuthStatus, string> result = await reader.ReadAsync(_root, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error : "");
        result.Value.Email.ShouldBe("a@example.com");
        result.Value.LoggedIn.ShouldBeTrue();
        result.Value.SubscriptionType.ShouldBe("max");
        result.Value.ProjectsDirectory.ShouldBe(_root.Replace('\\', '/') + "/projects");
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "The fake CLI is a Windows batch file")]
    public async Task ANonZeroExitReportsTheExitCodeAndLogsWhatTheChildPrintedRatherThanReturningIt()
    {
        // The failure string is embedded verbatim in the 409 body the page renders,
        // so whatever the child wrote must not travel in it. The operator still
        // needs to see it, so it goes to the log.
        ClaudeExecutable cli = FakeCli("echo not logged in 1>&2\r\nexit /b 3");
        RecordingLogger<ClaudeCliProcessAuthStatus> logger = new();
        ClaudeCliProcessAuthStatus reader = new(cli, TimeSpan.FromSeconds(30), logger);

        Result<ClaudeAuthStatus, string> result = await reader.ReadAsync(null, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain("exited with code 3");
        result.Error.ShouldNotContain("not logged in");
        logger.Lines.ShouldContain(line => line.Contains("not logged in", StringComparison.Ordinal));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "The fake CLI is a Windows batch file")]
    public async Task AFailedLogoutKeepsTheChildOutputOutOfTheRefusalTheCallerGets()
    {
        // The same construction serves auth logout, whose failure the removal endpoint
        // puts straight into its refusal message.
        ClaudeExecutable cli = FakeCli("echo sk-ant-secret-looking-text 1>&2\r\nexit /b 1");
        RecordingLogger<ClaudeCliProcessAuthStatus> logger = new();
        ClaudeCliProcessAuthStatus reader = new(cli, TimeSpan.FromSeconds(30), logger);

        Result<Unit, string> result = await reader.LogoutAsync(_root, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotContain("sk-ant-secret-looking-text");
        logger.Lines.ShouldContain(line => line.Contains("sk-ant-secret-looking-text", StringComparison.Ordinal));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "The fake CLI is a Windows batch file")]
    public async Task AHungProcessIsKilledAtTheTimeout()
    {
        ClaudeExecutable cli = FakeCli("ping -n 30 127.0.0.1 >nul");
        ClaudeCliProcessAuthStatus reader = new(cli, TimeSpan.FromMilliseconds(500), new RecordingLogger<ClaudeCliProcessAuthStatus>());

        Result<ClaudeAuthStatus, string> result = await reader.ReadAsync(null, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain("timed out");
    }

    [Fact]
    public async Task ANonZeroExitKeepsAStderrSentinelOutOfABoundedFailure()
    {
        // The head sits inside the logged excerpt; the tail sits past it. Neither
        // may reach the failure string the page renders, and the log has to say
        // it was cut.
        const string head = "HEAD_SENTINEL_9f2e";
        const string tail = "TAIL_SENTINEL_9f2e";
        string payloadPath = Path.Combine(_root, "payload.txt");
        await File.WriteAllTextAsync(payloadPath, head + new string('A', 500) + tail, TestContext.Current.CancellationToken);
        ClaudeExecutable cli = ShellCli(
            "cat '" + payloadPath + "' >&2\nexit 3",
            "type \"" + payloadPath + "\" 1>&2\r\nexit /b 3");
        RecordingLogger<ClaudeCliProcessAuthStatus> logger = new();
        ClaudeCliProcessAuthStatus reader = new(cli, TimeSpan.FromSeconds(30), logger);

        Result<ClaudeAuthStatus, string> result = await reader.ReadAsync(null, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain("exited with code 3");
        result.Error.ShouldNotContain(head);
        result.Error.ShouldNotContain(tail);
        result.Error.Length.ShouldBeLessThan(400);
        string logged = string.Join('\n', logger.Lines);
        logged.ShouldContain(head);
        logged.ShouldContain("(truncated)");
        logged.ShouldNotContain(tail);
    }

    [Fact]
    public async Task ATimedOutChildReturnsATimeoutAndThePipesAreDrainedIntoTheLog()
    {
        const string sentinel = "TIMEOUT_STDERR_SENTINEL_9f2e";
        RecordingLogger<ClaudeCliProcessAuthStatus> logger = new();
        ClaudeCliProcessAuthStatus reader = new(
            ShellCli("echo " + sentinel + " >&2\nsleep 30", "echo " + sentinel + " 1>&2\r\nping -n 30 127.0.0.1 >nul"),
            TimeSpan.FromSeconds(1),
            logger);
        int unobservedReads = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            args.SetObserved();
            string text = args.Exception.ToString();
            if (text.Contains("ReadToEnd", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref unobservedReads);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            Result<ClaudeAuthStatus, string> result = await reader.ReadAsync(null, TestContext.Current.CancellationToken);

            result.IsFailure.ShouldBeTrue();
            result.Error.ShouldContain("timed out");
            result.Error.ShouldNotContain(sentinel);
            result.Error.Length.ShouldBeLessThan(400);
            string.Join('\n', logger.Lines).ShouldContain(sentinel);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            unobservedReads.ShouldBe(0);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task ACallerCancellationStillThrowsAfterTheChildIsKilled()
    {
        ClaudeCliProcessAuthStatus reader = new(
            ShellCli("sleep 30", "ping -n 30 127.0.0.1 >nul"),
            TimeSpan.FromSeconds(30),
            new RecordingLogger<ClaudeCliProcessAuthStatus>());
        using CancellationTokenSource caller = new(TimeSpan.FromMilliseconds(300));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<OperationCanceledException>(() => reader.ReadAsync(null, linked.Token));
    }

    [Fact]
    public async Task StatusJsonThatSaysLoggedOutIsANormalStatus()
    {
        // loggedIn false is the CLI's ordinary answer for a logged-out folder.
        // Callers already branch on ClaudeAuthStatus.LoggedIn, so this is not
        // reclassified as an error.
        ClaudeExecutable cli = ShellCli(
            "printf '%s\\n' '{\"loggedIn\":false}'",
            "echo {\"loggedIn\":false}");
        ClaudeCliProcessAuthStatus reader = new(cli, TimeSpan.FromSeconds(30), new RecordingLogger<ClaudeCliProcessAuthStatus>());

        Result<ClaudeAuthStatus, string> result = await reader.ReadAsync(null, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error : "");
        result.Value.LoggedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task UnparsableStatusJsonDoesNotReturnTheBody()
    {
        // An invalid JSON literal is quoted whole by JsonException.Message, so the
        // failure string must not carry this sentinel even though the log may.
        const string sentinel = "sk-ant-NOJSON-9f2e";
        RecordingLogger<ClaudeCliProcessAuthStatus> logger = new();
        ClaudeExecutable cli = ShellCli(
            "printf '%s\\n' '" + sentinel + " is not json'",
            "echo " + sentinel + " is not json");
        ClaudeCliProcessAuthStatus reader = new(cli, TimeSpan.FromSeconds(30), logger);

        Result<ClaudeAuthStatus, string> result = await reader.ReadAsync(null, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe("claude auth status printed no JSON object");
        result.Error.ShouldNotContain(sentinel);
        string.Join('\n', logger.Lines).ShouldContain(sentinel);
        string.Join('\n', logger.Lines).ShouldNotContain("(truncated)");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
