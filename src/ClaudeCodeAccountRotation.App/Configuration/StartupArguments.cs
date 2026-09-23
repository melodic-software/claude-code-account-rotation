using System.Globalization;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Configuration;

/// <summary>The command line: <c>--config &lt;path&gt;</c>, <c>--port &lt;n&gt;</c>, <c>--open</c>, <c>--version</c>, <c>--help</c> (and <c>-h</c>, <c>/?</c>).</summary>
internal sealed record StartupArguments(string? ConfigPath, int? Port, bool Open, bool ShowVersion, bool ShowHelp)
{
    public const string Usage = """
        claude-code-account-rotation [--config <path>] [--port <n>] [--open] [--version] [--help]

          --config <path>   configuration file (default: <app data>/claude-code-account-rotation/config.json)
          --port <n>        listen port for this launch (overrides the configured port; 0 lets the operating system choose a free port)
          --open            open the dashboard in the default browser, signed in; if an instance is already running, open that one and exit;
                            on Windows with none running, start one in the background with no window, open it, and exit
          --version         print the version and exit
          --help, -h, /?    print this text and exit
        """;

    public static Result<StartupArguments, string> Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? configPath = null;
        int? port = null;
        bool open = false;
        bool showVersion = false;
        bool showHelp = false;

        for (int index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--config" when index + 1 < arguments.Count:
                    configPath = arguments[++index];
                    break;
                case "--port" when index + 1 < arguments.Count:
                    if (!int.TryParse(arguments[++index], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed is < 0 or > 65535)
                    {
                        return Result<StartupArguments, string>.Failure("--port expects a number between 0 and 65535");
                    }

                    port = parsed;
                    break;
                case "--open":
                    open = true;
                    break;
                case "--version":
                    showVersion = true;
                    break;
                case "--help" or "-h" or "/?":
                    showHelp = true;
                    break;
                default:
                    // Anything else is the host's: WebApplication.CreateBuilder reads --key value
                    // pairs into configuration, and a test host passes its own runner arguments.
                    break;
            }
        }

        return Result<StartupArguments, string>.Success(new StartupArguments(configPath, port, open, showVersion, showHelp));
    }
}
