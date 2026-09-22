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
            "--follower", "/opt/rotation/claude-code-account-rotation",
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
        // wrapper. --exec is the wrapper; --follower is the configured binary.
        IReadOnlyList<string> arguments = WslDistributionPeerHost.Arguments(
            new PeerLaunch("Some-Distribution", "someone", "/opt/rotation/claude-code-account-rotation", 48212, "/mnt/c/tmp/follower.json"));

        arguments.ShouldBe(
        [
            "-d", "Some-Distribution",
            "-u", "someone",
            "--exec", "/opt/rotation/claude-code-account-rotation-follower-log",
            "--follower", "/opt/rotation/claude-code-account-rotation",
            "--port", "48212",
            "--config", "/mnt/c/tmp/follower.json",
        ]);
        arguments.TakeLast(2).ShouldBe(["--config", "/mnt/c/tmp/follower.json"]);
    }

    public static bool OnLinux => OperatingSystem.IsLinux();

    /// <summary>
    /// The wrapper is what a discarded <c>wsl.exe</c> pty cannot be: a place the
    /// follower's own line survives. A second start keeps that line in
    /// <c>follower.log.1</c> and writes the new line to <c>follower.log</c>. A
    /// symlink at the log path is not a place that line may go.
    /// </summary>
    [Fact(SkipUnless = nameof(OnLinux), Skip = "The wrapper runs under /bin/sh")]
    public async Task TheWrapperKeepsThePreviousFollowerLogAndRefusesASymlink()
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
            (await File.ReadAllTextAsync(logPath + ".1", TestContext.Current.CancellationToken))
                .ShouldBe("first line\n");

            File.Delete(logPath);
            string secret = Path.Combine(root, "secret");
            await File.WriteAllTextAsync(secret, "untouched\n", TestContext.Current.CancellationToken);
            File.CreateSymbolicLink(logPath, secret);
            int runsBefore = WitnessRuns(witnessPath);
            await WriteSiblingBinaryAsync(binary, "third line", argvPath, witnessPath, TestContext.Current.CancellationToken);
            WrapperRun refused = await RunWrapperAsync(wrapper, configPath, TestContext.Current.CancellationToken);

            refused.ExitCode.ShouldNotBe(0);
            refused.Output.ShouldBe("follower-log: refusing to follow a symlink at follower.log\n");
            (await File.ReadAllTextAsync(secret, TestContext.Current.CancellationToken)).ShouldBe("untouched\n");
            new FileInfo(logPath).LinkTarget.ShouldNotBeNull();
            WitnessRuns(witnessPath).ShouldBe(runsBefore);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <c>peers[].launch.executablePath</c> is whatever the operator installed.
    /// A sibling named <c>claude-code-account-rotation-linux-x64</c> must not
    /// win over that path.
    /// </summary>
    [Fact(SkipUnless = nameof(OnLinux), Skip = "The wrapper runs under /bin/sh")]
    public async Task TheWrapperRunsTheNamedFollowerRatherThanASibling()
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
            string decoy = Path.Combine(install, "claude-code-account-rotation-linux-x64");
            string named = Path.Combine(install, "custom follower");
            string argvPath = Path.Combine(root, "argv");
            string witnessPath = Path.Combine(root, "witness");
            string configPath = Path.Combine(root, "follower.json");
            await File.WriteAllTextAsync(
                configPath,
                "{\"appDataDirectory\":\"" + appData + "\"}",
                TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(decoy, "wrong binary", argvPath, witnessPath, TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(named, "named binary", argvPath, witnessPath, TestContext.Current.CancellationToken);

            WrapperRun run = await RunWrapperAsync(wrapper, configPath, TestContext.Current.CancellationToken, named);

            run.ExitCode.ShouldBe(0);
            (await File.ReadAllTextAsync(Path.Combine(appData, "follower.log"), TestContext.Current.CancellationToken))
                .ShouldBe("named binary\n");
            (await File.ReadAllTextAsync(argvPath, TestContext.Current.CancellationToken))
                .ShouldBe("--port\n48212\n--config\n" + configPath + "\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <c>ConfigurationFile.Merge</c>
    /// keeps the platform default when <c>appDataDirectory</c> is omitted, null,
    /// blank, or the file does not exist yet. The log has to land in that same
    /// directory or the first start exits before the application can create it.
    /// </summary>
    [Fact(SkipUnless = nameof(OnLinux), Skip = "The wrapper runs under /bin/sh")]
    public async Task AnOmittedOrNullAppDataDirectoryUsesThePlatformDefault()
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
            string xdg = Path.Combine(root, "xdg");
            string home = Path.Combine(root, "home");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(xdg);
            Directory.CreateDirectory(home);
            string wrapper = Path.Combine(install, "claude-code-account-rotation-follower-log");
            File.Copy(WrapperSource(), wrapper);
            string binary = Path.Combine(install, "claude-code-account-rotation");
            string argvPath = Path.Combine(root, "argv");
            string witnessPath = Path.Combine(root, "witness");
            await WriteSiblingBinaryAsync(binary, "started", argvPath, witnessPath, TestContext.Current.CancellationToken);
            string logPath = Path.Combine(xdg, "claude-code-account-rotation", "follower.log");

            string?[] documents = ["{\"role\":\"follower\"}", "{\"appDataDirectory\":null}", "{\"appDataDirectory\":\"  \"}", null];
            foreach (string? document in documents)
            {
                string configPath = Path.Combine(root, "follower.json");
                if (document is null)
                {
                    File.Delete(configPath);
                }
                else
                {
                    await File.WriteAllTextAsync(configPath, document, TestContext.Current.CancellationToken);
                }

                WrapperRun run = await RunWrapperAsync(
                    wrapper,
                    configPath,
                    TestContext.Current.CancellationToken,
                    home: home,
                    xdgDataHome: xdg);

                run.ExitCode.ShouldBe(0);
                (await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken)).ShouldBe("started\n");
                Directory.Exists(Path.Combine(home, ".local")).ShouldBeFalse();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The default serializer writes <c>&amp;</c> as <c>\u0026</c>. A text scan
    /// that keeps the escape would log beside a directory the application never
    /// opens.
    /// </summary>
    [Fact(SkipUnless = nameof(OnLinux), Skip = "The wrapper runs under /bin/sh")]
    public async Task AnEscapedAppDataDirectoryIsDecoded()
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
            Directory.CreateDirectory(install);
            string appData = Path.Combine(root, "app&data");
            string wrapper = Path.Combine(install, "claude-code-account-rotation-follower-log");
            File.Copy(WrapperSource(), wrapper);
            string binary = Path.Combine(install, "claude-code-account-rotation");
            string argvPath = Path.Combine(root, "argv");
            string witnessPath = Path.Combine(root, "witness");
            string configPath = Path.Combine(root, "follower.json");
            await File.WriteAllTextAsync(
                configPath,
                "{\"appDataDirectory\":\"" + appData.Replace("&", "\\u0026", StringComparison.Ordinal) + "\"}",
                TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(binary, "decoded", argvPath, witnessPath, TestContext.Current.CancellationToken);

            WrapperRun run = await RunWrapperAsync(wrapper, configPath, TestContext.Current.CancellationToken);

            run.ExitCode.ShouldBe(0);
            (await File.ReadAllTextAsync(Path.Combine(appData, "follower.log"), TestContext.Current.CancellationToken))
                .ShouldBe("decoded\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A second start leaves the first start's bytes in <c>follower.log.1</c>
    /// and a new <c>follower.log</c>. The new file is not an append.
    /// </summary>
    [Fact(SkipUnless = nameof(OnLinux), Skip = "The wrapper runs under /bin/sh")]
    public async Task ASecondStartLeavesTheFirstLogsBytesInThePreviousFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        PreparedWrapper prepared = await PrepareWrapperAsync(TestContext.Current.CancellationToken);
        try
        {
            string logPath = Path.Combine(prepared.AppData, "follower.log");
            await WriteSiblingBinaryAsync(prepared.Binary, "first line", prepared.ArgvPath, prepared.WitnessPath, TestContext.Current.CancellationToken);
            WrapperRun first = await RunWrapperAsync(prepared.Wrapper, prepared.ConfigPath, TestContext.Current.CancellationToken);

            first.ExitCode.ShouldBe(0);
            (await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken)).ShouldBe("first line\n");

            await WriteSiblingBinaryAsync(prepared.Binary, "second line", prepared.ArgvPath, prepared.WitnessPath, TestContext.Current.CancellationToken);
            WrapperRun second = await RunWrapperAsync(prepared.Wrapper, prepared.ConfigPath, TestContext.Current.CancellationToken);

            second.ExitCode.ShouldBe(0);
            second.Output.ShouldBe(string.Empty);
            (await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken)).ShouldBe("second line\n");
            (await File.ReadAllTextAsync(logPath + ".1", TestContext.Current.CancellationToken)).ShouldBe("first line\n");
            File.Exists(logPath + ".2").ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(prepared.Root, recursive: true);
        }
    }

    /// <summary>
    /// Shifting drops the oldest once <c>follower.log.3</c> would be pushed off.
    /// A generation that does not exist is skipped.
    /// </summary>
    [Fact(SkipUnless = nameof(OnLinux), Skip = "The wrapper runs under /bin/sh")]
    public async Task ShiftingDropsTheOldestOnceThePreviousChainIsFull()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        PreparedWrapper prepared = await PrepareWrapperAsync(TestContext.Current.CancellationToken);
        try
        {
            string logPath = Path.Combine(prepared.AppData, "follower.log");
            await File.WriteAllTextAsync(logPath, "gen0\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(logPath + ".1", "gen1\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(logPath + ".2", "gen2\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(logPath + ".3", "gen3\n", TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(prepared.Binary, "live", prepared.ArgvPath, prepared.WitnessPath, TestContext.Current.CancellationToken);

            WrapperRun full = await RunWrapperAsync(prepared.Wrapper, prepared.ConfigPath, TestContext.Current.CancellationToken);

            full.ExitCode.ShouldBe(0);
            (await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken)).ShouldBe("live\n");
            (await File.ReadAllTextAsync(logPath + ".1", TestContext.Current.CancellationToken)).ShouldBe("gen0\n");
            (await File.ReadAllTextAsync(logPath + ".2", TestContext.Current.CancellationToken)).ShouldBe("gen1\n");
            (await File.ReadAllTextAsync(logPath + ".3", TestContext.Current.CancellationToken)).ShouldBe("gen2\n");
            foreach (string path in Directory.GetFiles(prepared.AppData))
            {
                (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldNotContain("gen3");
            }

            foreach (string name in _logNames)
            {
                File.Delete(Path.Combine(prepared.AppData, name));
            }

            await File.WriteAllTextAsync(logPath, "current\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(logPath + ".1", "one\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(logPath + ".3", "three\n", TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(prepared.Binary, "fresh", prepared.ArgvPath, prepared.WitnessPath, TestContext.Current.CancellationToken);

            WrapperRun hole = await RunWrapperAsync(prepared.Wrapper, prepared.ConfigPath, TestContext.Current.CancellationToken);

            hole.ExitCode.ShouldBe(0);
            (await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken)).ShouldBe("fresh\n");
            (await File.ReadAllTextAsync(logPath + ".1", TestContext.Current.CancellationToken)).ShouldBe("current\n");
            (await File.ReadAllTextAsync(logPath + ".2", TestContext.Current.CancellationToken)).ShouldBe("one\n");
            File.Exists(logPath + ".3").ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(prepared.Root, recursive: true);
        }
    }

    /// <summary>
    /// A previous file that is already <c>follower.log.2</c> and larger than 1 MiB
    /// is removed. The log just rotated to <c>follower.log.1</c> remains, including
    /// when that file alone is larger than 1 MiB. A younger previous file stays
    /// when dropping the oldest brings the sum back within the cap.
    /// </summary>
    [Fact(SkipUnless = nameof(OnLinux), Skip = "The wrapper runs under /bin/sh")]
    public async Task AnOversizedPreviousFileIsRemovedWhileTheLogJustRotatedRemains()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const int OneMebibyte = 1048576;
        PreparedWrapper prepared = await PrepareWrapperAsync(TestContext.Current.CancellationToken);
        try
        {
            string logPath = Path.Combine(prepared.AppData, "follower.log");
            var justEnded = new byte[OneMebibyte + 1];
            Array.Fill(justEnded, (byte)'J');
            var oversized = new byte[OneMebibyte + 1];
            Array.Fill(oversized, (byte)'Q');
            await File.WriteAllBytesAsync(logPath, justEnded, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(logPath + ".2", oversized, TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(prepared.Binary, "live", prepared.ArgvPath, prepared.WitnessPath, TestContext.Current.CancellationToken);

            WrapperRun kept = await RunWrapperAsync(prepared.Wrapper, prepared.ConfigPath, TestContext.Current.CancellationToken);

            kept.ExitCode.ShouldBe(0);
            (await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken)).ShouldBe("live\n");
            byte[] rotated = await File.ReadAllBytesAsync(logPath + ".1", TestContext.Current.CancellationToken);
            rotated.AsSpan().SequenceEqual(justEnded).ShouldBeTrue();
            File.Exists(logPath + ".2").ShouldBeFalse();
            File.Exists(logPath + ".3").ShouldBeFalse();

            foreach (string name in _logNames)
            {
                File.Delete(Path.Combine(prepared.AppData, name));
            }

            await File.WriteAllTextAsync(logPath, "just-ended\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(logPath + ".1", "middle\n", TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(logPath + ".2", oversized, TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(prepared.Binary, "again", prepared.ArgvPath, prepared.WitnessPath, TestContext.Current.CancellationToken);

            WrapperRun partial = await RunWrapperAsync(prepared.Wrapper, prepared.ConfigPath, TestContext.Current.CancellationToken);

            partial.ExitCode.ShouldBe(0);
            (await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken)).ShouldBe("again\n");
            (await File.ReadAllTextAsync(logPath + ".1", TestContext.Current.CancellationToken)).ShouldBe("just-ended\n");
            (await File.ReadAllTextAsync(logPath + ".2", TestContext.Current.CancellationToken)).ShouldBe("middle\n");
            File.Exists(logPath + ".3").ShouldBeFalse();

            foreach (string name in _logNames)
            {
                File.Delete(Path.Combine(prepared.AppData, name));
            }

            byte[] oneByte = [(byte)'Z'];
            var underTheCap = new byte[OneMebibyte - 1];
            Array.Fill(underTheCap, (byte)'A');
            await File.WriteAllBytesAsync(logPath + ".1", oneByte, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(logPath + ".2", underTheCap, TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(prepared.Binary, "at-cap", prepared.ArgvPath, prepared.WitnessPath, TestContext.Current.CancellationToken);

            WrapperRun atCap = await RunWrapperAsync(prepared.Wrapper, prepared.ConfigPath, TestContext.Current.CancellationToken);

            atCap.ExitCode.ShouldBe(0);
            (await File.ReadAllBytesAsync(logPath + ".1", TestContext.Current.CancellationToken)).AsSpan().SequenceEqual(oneByte).ShouldBeTrue();
            (await File.ReadAllBytesAsync(logPath + ".2", TestContext.Current.CancellationToken)).AsSpan().SequenceEqual(underTheCap).ShouldBeTrue();

            foreach (string name in _logNames)
            {
                File.Delete(Path.Combine(prepared.AppData, name));
            }

            var oneByteOver = new byte[OneMebibyte];
            Array.Fill(oneByteOver, (byte)'B');
            await File.WriteAllBytesAsync(logPath + ".1", oneByte, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(logPath + ".2", oneByteOver, TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(prepared.Binary, "over-cap", prepared.ArgvPath, prepared.WitnessPath, TestContext.Current.CancellationToken);

            WrapperRun overCap = await RunWrapperAsync(prepared.Wrapper, prepared.ConfigPath, TestContext.Current.CancellationToken);

            overCap.ExitCode.ShouldBe(0);
            (await File.ReadAllBytesAsync(logPath + ".1", TestContext.Current.CancellationToken)).AsSpan().SequenceEqual(oneByte).ShouldBeTrue();
            File.Exists(logPath + ".2").ShouldBeFalse();
            File.Exists(logPath + ".3").ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(prepared.Root, recursive: true);
        }
    }

    /// <summary>
    /// A symlink at <c>follower.log</c> is still refused. A symlink at
    /// <c>follower.log.1</c>, <c>follower.log.2</c>, or <c>follower.log.3</c> is
    /// refused the same way, and the target is not written.
    /// </summary>
    [Fact(SkipUnless = nameof(OnLinux), Skip = "The wrapper runs under /bin/sh")]
    public async Task ASymlinkAtFollowerLogOrAPreviousGenerationIsRefused()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        PreparedWrapper prepared = await PrepareWrapperAsync(TestContext.Current.CancellationToken);
        try
        {
            await File.WriteAllTextAsync(prepared.WitnessPath, string.Empty, TestContext.Current.CancellationToken);
            await WriteSiblingBinaryAsync(prepared.Binary, "should-not-run", prepared.ArgvPath, prepared.WitnessPath, TestContext.Current.CancellationToken);
            foreach (string name in _logNames)
            {
                foreach (string existing in _logNames)
                {
                    File.Delete(Path.Combine(prepared.AppData, existing));
                }

                string secret = Path.Combine(prepared.Root, "secret");
                await File.WriteAllTextAsync(secret, "untouched\n", TestContext.Current.CancellationToken);
                string linkPath = Path.Combine(prepared.AppData, name);
                if (name != "follower.log")
                {
                    await File.WriteAllTextAsync(Path.Combine(prepared.AppData, "follower.log"), "would-rotate\n", TestContext.Current.CancellationToken);
                }

                File.CreateSymbolicLink(linkPath, secret);
                int runsBefore = WitnessRuns(prepared.WitnessPath);
                WrapperRun refused = await RunWrapperAsync(prepared.Wrapper, prepared.ConfigPath, TestContext.Current.CancellationToken);

                refused.ExitCode.ShouldNotBe(0);
                refused.Output.ShouldBe("follower-log: refusing to follow a symlink at " + name + "\n");
                refused.Output.ShouldNotContain(prepared.Root);
                (await File.ReadAllTextAsync(secret, TestContext.Current.CancellationToken)).ShouldBe("untouched\n");
                new FileInfo(linkPath).LinkTarget.ShouldNotBeNull();
                WitnessRuns(prepared.WitnessPath).ShouldBe(runsBefore);
                if (name != "follower.log")
                {
                    (await File.ReadAllTextAsync(Path.Combine(prepared.AppData, "follower.log"), TestContext.Current.CancellationToken))
                        .ShouldBe("would-rotate\n");
                }
            }
        }
        finally
        {
            Directory.Delete(prepared.Root, recursive: true);
        }
    }

    private static readonly string[] _logNames = ["follower.log", "follower.log.1", "follower.log.2", "follower.log.3"];

    private readonly record struct PreparedWrapper(
        string Root,
        string AppData,
        string Wrapper,
        string Binary,
        string ArgvPath,
        string WitnessPath,
        string ConfigPath);

    private static async Task<PreparedWrapper> PrepareWrapperAsync(CancellationToken cancellationToken)
    {
        string root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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
        await File.WriteAllTextAsync(
            configPath,
            "{\"appDataDirectory\":\"" + appData + "\"}",
            cancellationToken);
        return new PreparedWrapper(root, appData, wrapper, binary, argvPath, witnessPath, configPath);
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

    private static async Task<WrapperRun> RunWrapperAsync(
        string wrapper,
        string configPath,
        CancellationToken cancellationToken,
        string? follower = null,
        string? home = null,
        string? xdgDataHome = null)
    {
        ProcessStartInfo start = new("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(wrapper)!,
        };
        if (home is not null)
        {
            start.Environment["HOME"] = home;
        }

        if (xdgDataHome is not null)
        {
            start.Environment["XDG_DATA_HOME"] = xdgDataHome;
        }

        start.ArgumentList.Add(wrapper);
        if (follower is not null)
        {
            start.ArgumentList.Add("--follower");
            start.ArgumentList.Add(follower);
        }

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
