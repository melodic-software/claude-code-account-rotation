using System.Diagnostics;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// How to start the CLI: the file to execute and the arguments that precede
/// the CLI's own. A native binary runs directly with an argument list. An npm
/// <c>.cmd</c> shim can only run through the command interpreter, and
/// <c>cmd.exe</c> does not parse its command line by the argument-list rules,
/// so <see cref="Shim"/> names the script and the process adapter builds the
/// one quoted command line the interpreter needs; the interpreter itself is
/// the system directory's, never one found on PATH.
/// </summary>
internal sealed record ClaudeExecutable(string FileName, IReadOnlyList<string> ArgumentPrefix, string? Shim = null)
{
    /// <summary>
    /// How every invocation of the CLI is started: never a shell, never a
    /// joined command line except the one the interpreter forces for an npm
    /// shim, and under <paramref name="configDirectory"/> when one is given so
    /// a command can only ever see one account's config root. Standard output
    /// and error are redirected; a caller that also writes to the child
    /// redirects standard input itself.
    /// </summary>
    public ProcessStartInfo StartInfo(IReadOnlyList<string> arguments, string? configDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ProcessStartInfo startInfo = new(FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (Shim is string shim)
        {
            // cmd.exe does not parse its command line by the argument-list rules, and an
            // argument-list entry is quoted only when it holds a space or a quote, so a
            // shim path carrying "&" would be read as a command separator. With /s the
            // interpreter strips the outer quotes and runs the rest; the inner quotes keep
            // the path one operand. A Windows path can never contain a quote itself. Every
            // argument is quoted the same way, so none of them can separate commands
            // either, whoever built it and whatever it came from.
            startInfo.Arguments = "/d /s /c \"\"" + shim + "\"" + string.Concat(arguments.Select(OneInterpreterOperand)) + "\"";
        }
        else
        {
            foreach (string argument in ArgumentPrefix.Concat(arguments))
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        if (configDirectory is not null)
        {
            startInfo.Environment["CLAUDE_CONFIG_DIR"] = configDirectory;
        }

        // A login's OAuth flow opens its callback URL in the machine's default
        // browser, which is not the profile the roster maps to the account: the
        // operator sees two browsers on two URLs. "true" is the CLI's own
        // no-browser sentinel. Every command is started through here, and no
        // command this tool runs wants a browser of its own.
        startInfo.Environment["BROWSER"] = "true";

        return startInfo;
    }

    /// <summary>
    /// One argument as a single operand of the interpreter's command line, with
    /// the leading space that separates it from the one before.
    /// <para>
    /// Quotes neutralize every separator <c>cmd.exe</c> reads: <c>&amp;</c>,
    /// <c>|</c>, <c>&lt;</c>, <c>&gt;</c>, <c>^</c>, and the parentheses. The two
    /// characters quoting cannot neutralize are refused instead. A quote would
    /// end the operand, and <c>%name%</c> is expanded before quoting is
    /// considered, with no escape for it on a command line (<c>%%</c> is a batch
    /// file's escape, not the interpreter's). Nothing this tool passes holds
    /// either: the arguments are compile-time constants plus an account e-mail,
    /// whose allowlist admits neither. The refusal is the backstop for the day
    /// that stops being true.
    /// </para>
    /// </summary>
    private static string OneInterpreterOperand(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Any(static character => character is '"' or '%' || char.IsControl(character)))
        {
            // Deliberately without the argument itself: it is the one thing here that
            // may be external, and this message reaches a log.
            throw new ArgumentException(
                "An argument passed through the command interpreter holds a quote, a percent sign, or a control character, none of which can be quoted safely.",
                nameof(argument));
        }

        return " \"" + argument + "\"";
    }
}

/// <summary>
/// Resolves the <c>claude</c> executable: the <c>claudeExecutable</c> configuration
/// key when set (an absent file there is refused, not skipped), else the first
/// PATH entry holding a native binary, else the first holding an npm shim.
/// </summary>
internal static class ClaudeExecutableLocator
{
    /// <param name="systemDirectory">Where the command interpreter lives (<see cref="Environment.SystemDirectory"/> on a real Windows machine); only consulted for an npm shim.</param>
    public static Result<ClaudeExecutable, string> Locate(string? configuredExecutable, string? pathVariable, bool isWindows, string? systemDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            string configured = Path.GetFullPath(configuredExecutable);
            return File.Exists(configured)
                ? Result<ClaudeExecutable, string>.Success(Describe(configured, isWindows, systemDirectory))
                : Result<ClaudeExecutable, string>.Failure("the configured claudeExecutable does not exist: " + configured);
        }

        string[] directories = (pathVariable ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] nativeNames = isWindows ? ["claude.exe"] : ["claude"];
        string[] shimNames = isWindows ? ["claude.cmd"] : [];

        foreach (string[] candidates in new[] { nativeNames, shimNames })
        {
            foreach (string directory in directories)
            {
                foreach (string candidate in candidates)
                {
                    string path = Path.Combine(directory, candidate);
                    if (File.Exists(path))
                    {
                        return Result<ClaudeExecutable, string>.Success(Describe(path, isWindows, systemDirectory));
                    }
                }
            }
        }

        return Result<ClaudeExecutable, string>.Failure(
            "no claude executable was found on PATH; set the claudeExecutable configuration key to its full path");
    }

    private static ClaudeExecutable Describe(string path, bool isWindows, string? systemDirectory)
    {
        bool isShim = isWindows && Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        return isShim
            ? new ClaudeExecutable(SystemCommandInterpreter(systemDirectory), [], Shim: path)
            : new ClaudeExecutable(path, []);
    }

    /// <summary>The interpreter from the system directory, by full path, never a bare name resolved through PATH or the current directory.</summary>
    private static string SystemCommandInterpreter(string? systemDirectory)
    {
        string directory = string.IsNullOrWhiteSpace(systemDirectory) ? Environment.SystemDirectory : systemDirectory;
        return Path.Combine(directory, "cmd.exe");
    }
}
