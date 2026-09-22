using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Peers;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Configuration;
using ClaudeCodeAccountRotation.Core.Switching;
using Shouldly;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The two places a path or a command line crosses the boundary between the
/// two operating systems, which is the one place this leader's own conventions
/// are the wrong ones to apply.
/// </summary>
public sealed class PeerTests
{
    [Fact]
    public void APathForThePeerIsSpelledTheWayThePeerWillOpenIt()
    {
        // Forward slashes whatever this side runs on. Path.Combine would hand a
        // Linux follower backslashes on Windows, and the follower's own mailbox
        // check compares the parent directory literally - so the request would
        // be refused for a path that names exactly the right file.
        Peer peer = new(new StubInstance(), null, "/mnt/c/store");
        string native = Path.Combine("C:", "store", ".transit", "wsl", "b@example.com.credentials.json");

        string spelled = peer.InPeerNamespace(native);

        spelled.ShouldBe("/mnt/c/store/.transit/wsl/b@example.com.credentials.json");
        spelled.ShouldNotContain("\\");
    }

    [Fact]
    public void ATrailingSeparatorOnTheStorePathDoesNotDoubleUp()
    {
        Peer peer = new(new StubInstance(), null, "/mnt/c/store/");

        peer.InPeerNamespace("x" + FileSystemCredentialPairStore.FileName)
            .ShouldBe("/mnt/c/store/.transit/wsl/x" + FileSystemCredentialPairStore.FileName);
    }

    [Fact]
    public void TheLaunchCommandNamesTheDistributionTheUserTheWrapperAndThePort()
    {
        // Forward slashes whatever this side runs on. The wrapper is the sibling
        // of the configured Linux path, and Path.GetDirectoryName would hand
        // that sibling backslashes on Windows. Same class of bug as the store
        // path above.
        IReadOnlyList<string> arguments = WslDistributionPeerHost.Arguments(
            new PeerLaunch("Some-Distribution", "someone", "/opt/rotation/claude-code-account-rotation", 48212));

        arguments.ShouldBe(
        [
            "-d", "Some-Distribution",
            "-u", "someone",
            "--exec", "/opt/rotation/claude-code-account-rotation-follower-log",
            "--port", "48212",
        ]);
        string.Join(' ', arguments).ShouldNotContain("\\");
    }

    [Fact]
    public void ALaunchWithAConfigPathPassesItSoTheFollowerDoesNotWriteItselfALeadersDefault()
    {
        // A follower started with no --config creates one from the defaults,
        // and the default role is leader. Over temp roots that is how a leader
        // ends up squatting the follower's port. --config still follows the
        // wrapper; the binary path itself is not on this command line.
        IReadOnlyList<string> arguments = WslDistributionPeerHost.Arguments(
            new PeerLaunch("Some-Distribution", "someone", "/opt/rotation/claude-code-account-rotation", 48212, "/mnt/c/tmp/follower.json"));

        arguments.ShouldBe(
        [
            "-d", "Some-Distribution",
            "-u", "someone",
            "--exec", "/opt/rotation/claude-code-account-rotation-follower-log",
            "--port", "48212",
            "--config", "/mnt/c/tmp/follower.json",
        ]);
        arguments.TakeLast(2).ShouldBe(["--config", "/mnt/c/tmp/follower.json"]);
    }

    public static bool OnLinux => OperatingSystem.IsLinux();

    /// <summary>
    /// The wrapper is what a discarded <c>wsl.exe</c> pty cannot be: a place the
    /// follower's own line survives. A second start truncates, and a symlink at
    /// the log path is not a place that line may go.
    /// </summary>
    [Fact(SkipUnless = nameof(OnLinux), Skip = "The wrapper runs under /bin/sh")]
    public async Task TheWrapperTruncatesFollowerLogAndRefusesASymlink()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string install = Path.Combine(root, "install");
            string appData = Path.Combine(root, "appdata");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(appData);
            string wrapper = Path.Combine(install, "claude-code-account-rotation-follower-log");
            File.Copy(WrapperSource(), wrapper);
            string binary = Path.Combine(install, "claude-code-account-rotation-linux-x64");
            string argvPath = Path.Combine(root, "argv");
            string witnessPath = Path.Combine(root, "witness");
            string configPath = Path.Combine(root, "follower.json");
            const string Token = "should-not-be-logged";
            const string OneTimeCode = "one-time-code";
            JsonObject config = new()
            {
                ["appDataDirectory"] = appData,
                ["refreshToken"] = Token,
                ["oneTimeCode"] = OneTimeCode,
            };
            await File.WriteAllTextAsync(configPath, config.ToJsonString(), TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(binary, "first line", argvPath, witnessPath, TestContext.Current.CancellationToken);

            WrapperRun first = await RunWrapperAsync(wrapper, configPath, TestContext.Current.CancellationToken);
            string logPath = Path.Combine(appData, "follower.log");
            string firstLog = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);

            first.ExitCode.ShouldBe(0);
            firstLog.ShouldBe("first line\n");
            firstLog.ShouldNotContain(Token);
            firstLog.ShouldNotContain(OneTimeCode);
            first.Output.ShouldNotContain(Token);
            first.Output.ShouldNotContain(OneTimeCode);
            (await File.ReadAllTextAsync(argvPath, TestContext.Current.CancellationToken))
                .ShouldBe("--port\n48212\n--config\n" + configPath + "\n");

            await WriteSiblingBinaryAsync(binary, "second line", argvPath, witnessPath, TestContext.Current.CancellationToken);
            WrapperRun second = await RunWrapperAsync(wrapper, configPath, TestContext.Current.CancellationToken);
            string secondLog = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);

            second.ExitCode.ShouldBe(0);
            secondLog.ShouldBe("second line\n");
            secondLog.ShouldNotContain("first line");

            File.Delete(logPath);
            string secret = Path.Combine(root, "secret");
            await File.WriteAllTextAsync(secret, "untouched\n", TestContext.Current.CancellationToken);
            File.CreateSymbolicLink(logPath, secret);
            int runsBefore = WitnessRuns(witnessPath);
            await WriteSiblingBinaryAsync(binary, "third line", argvPath, witnessPath, TestContext.Current.CancellationToken);
            WrapperRun refused = await RunWrapperAsync(wrapper, configPath, TestContext.Current.CancellationToken);

            refused.ExitCode.ShouldNotBe(0);
            (await File.ReadAllTextAsync(secret, TestContext.Current.CancellationToken)).ShouldBe("untouched\n");
            new FileInfo(logPath).LinkTarget.ShouldNotBeNull();
            WitnessRuns(witnessPath).ShouldBe(runsBefore);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int WitnessRuns(string witnessPath) =>
        File.ReadAllText(witnessPath).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    private static async Task WriteSiblingBinaryAsync(string path, string line, string argvPath, string witnessPath, CancellationToken cancellationToken)
    {
        string script = string.Join(
            '\n',
            [
                "#!/bin/sh",
                "printf '%s\\n' \"$@\" > " + QuoteForSh(argvPath),
                "printf '%s\\n' ran >> " + QuoteForSh(witnessPath),
                "printf '%s\\n' " + QuoteForSh(line),
                "",
            ]);
        await File.WriteAllTextAsync(path, script, cancellationToken);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task<WrapperRun> RunWrapperAsync(string wrapper, string configPath, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(wrapper)!,
        };
        start.ArgumentList.Add(wrapper);
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add("48212");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configPath);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("/bin/sh did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new WrapperRun(process.ExitCode, await stdout + await stderr);
    }

    private static string WrapperSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "ClaudeCodeAccountRotation.App", "follower-log");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("follower-log is not in the source tree above the test assembly");
    }

    private static string QuoteForSh(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private readonly record struct WrapperRun(int ExitCode, string Output);

    /// <summary>A peer that is never called: these facts are about paths and argument lists, not traffic.</summary>
    private sealed class StubInstance : Core.Peers.IPeerRotationInstance
    {
        public SideName Side => SideName.Wsl;

        public Task<Core.Result<Core.Peers.PeerDashboard, string>> ReadDashboardAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Core.Result<Core.Peers.ImportAnswer, string>> ImportAsync(Core.Peers.ImportRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Core.Result<Core.Peers.ImportResult, string>> CommitImportAsync(Core.Identity.AccountEmail email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Core.Result<Core.Unit, string>> AbortImportAsync(Core.Identity.AccountEmail email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Core.Result<Core.Peers.ImportStatus, string>> ImportStatusAsync(Core.Identity.AccountEmail email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
