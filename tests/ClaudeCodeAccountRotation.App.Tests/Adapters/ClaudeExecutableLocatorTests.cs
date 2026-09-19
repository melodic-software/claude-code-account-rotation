using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class ClaudeExecutableLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    public ClaudeExecutableLocatorTests() => Directory.CreateDirectory(_root);

    private string Touch(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void AConfiguredExecutableWinsWhenItExists()
    {
        string configured = Touch(Path.Combine("custom", "claude.exe"));

        ClaudeExecutable located = ClaudeExecutableLocator.Locate(configured, pathVariable: null, isWindows: true).Value;

        located.FileName.ShouldBe(configured);
        located.ArgumentPrefix.ShouldBeEmpty();
    }

    [Fact]
    public void AConfiguredExecutableThatDoesNotExistIsRefusedNotSkipped()
    {
        Result<ClaudeExecutable, string> result = ClaudeExecutableLocator.Locate(Path.Combine(_root, "missing.exe"), pathVariable: null, isWindows: true);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain("missing.exe");
    }

    [Fact]
    public void ANativeBinaryOnPathIsPreferredOverAnNpmShim()
    {
        string shimDirectory = Path.GetDirectoryName(Touch(Path.Combine("npm", "claude.cmd")))!;
        string nativeDirectory = Path.GetDirectoryName(Touch(Path.Combine("local", "claude.exe")))!;
        string pathVariable = shimDirectory + Path.PathSeparator + nativeDirectory;

        ClaudeExecutable located = ClaudeExecutableLocator.Locate(null, pathVariable, isWindows: true).Value;

        located.FileName.ShouldBe(Path.Combine(nativeDirectory, "claude.exe"));
        located.ArgumentPrefix.ShouldBeEmpty();
    }

    [Fact]
    public void AnNpmShimRunsThroughTheSystemCommandInterpreterAsAShim()
    {
        string shimPath = Touch(Path.Combine("npm", "claude.cmd"));
        string systemDirectory = Path.Combine(_root, "system32");

        ClaudeExecutable located = ClaudeExecutableLocator.Locate(null, Path.GetDirectoryName(shimPath), isWindows: true, systemDirectory).Value;

        located.FileName.ShouldBe(Path.Combine(systemDirectory, "cmd.exe"), "the interpreter is named by full path, never resolved through PATH");
        located.ArgumentPrefix.ShouldBeEmpty();
        located.Shim.ShouldBe(shimPath);
    }

    [Fact]
    public void OnUnixTheBareNameIsResolvedOnPath()
    {
        string binary = Touch(Path.Combine("bin", "claude"));

        ClaudeExecutable located = ClaudeExecutableLocator.Locate(null, Path.GetDirectoryName(binary), isWindows: false).Value;

        located.FileName.ShouldBe(binary);
    }

    [Fact]
    public void AnArgumentHoldingACommandSeparatorIsOneQuotedOperandNotASecondCommand()
    {
        // The e-mail allowlist refuses this address at the boundary. This asserts what
        // happens if one ever reaches the interpreter anyway: "&" inside quotes is a
        // character of the operand, and cmd.exe runs one command, not three.
        string shimPath = Path.Combine(_root, "npm", "claude.cmd");
        ClaudeExecutable shim = new(Path.Combine(_root, "cmd.exe"), [], Shim: shimPath);

        string commandLine = shim.StartInfo(["auth", "login", "--email", "a&whoami&b@x.com"], configDirectory: null).Arguments;

        commandLine.ShouldBe("/d /s /c \"\"" + shimPath + "\" \"auth\" \"login\" \"--email\" \"a&whoami&b@x.com\"\"");
        // The address sits between one quote and the next, so neither "&" is ever
        // the interpreter's separator.
        commandLine.ShouldContain("\"a&whoami&b@x.com\"");
    }

    [Theory]
    [InlineData("%PATH%")]
    [InlineData("a\"b@x.com")]
    public void AnArgumentThatQuotingCannotNeutralizeIsRefusedRatherThanPassed(string argument)
    {
        // Quotes stop the separators; they stop neither environment expansion nor a
        // quote of the argument's own, so those never reach the command line at all.
        ClaudeExecutable shim = new(Path.Combine(_root, "cmd.exe"), [], Shim: Path.Combine(_root, "npm", "claude.cmd"));

        ArgumentException thrown = Should.Throw<ArgumentException>(() => shim.StartInfo(["auth", "login", "--email", argument], configDirectory: null));

        thrown.Message.ShouldNotContain(argument);
    }

    [Fact]
    public void EveryCommandRunsWithTheCliSOwnBrowserOpenSuppressed()
    {
        // Without this the CLI's login opens its callback URL in the default
        // browser beside the mapped profile this tool opens the sign-in URL in.
        ClaudeExecutable executable = new(Path.Combine(_root, "claude.exe"), []);

        executable.StartInfo(["auth", "login"], configDirectory: null).Environment["BROWSER"].ShouldBe("true");
    }

    [Fact]
    public void NothingResolvableIsRefusedWithGuidance()
    {
        Result<ClaudeExecutable, string> result = ClaudeExecutableLocator.Locate(null, Path.Combine(_root, "empty"), isWindows: true);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldContain("claudeExecutable");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
