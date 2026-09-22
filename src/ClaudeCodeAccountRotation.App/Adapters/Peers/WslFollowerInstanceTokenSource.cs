using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Configuration;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Adapters.Peers;

/// <summary>
/// Line 2 of the follower's <c>instance.url</c>, read as the follower user.
/// The user is the launch record when the page starts the follower, and the
/// peer entry's own distribution and user when the operator started it.
/// The path is an argument, never a shell string, and a Linux path stays
/// slash-separated. <see cref="Path.Combine(string, string)"/> is the wrong
/// tool for that path: on Windows it would insert backslashes.
/// </summary>
internal sealed class WslFollowerInstanceTokenSource(PeerConfiguration peer) : IFollowerInstanceTokenReader
{
    internal const string UnavailableReason = "the follower instance token is unavailable";

    internal const string UnreadableReason = "the follower instance token could not be read";

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(8);

    public async Task<Result<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (peer.FollowerIdentity is not PeerFollowerIdentity identity)
        {
            return Result<string, string>.Failure(UnavailableReason);
        }

        Result<string, string> directory = await AppDataDirectoryAsync(identity, cancellationToken);
        if (directory.IsFailure)
        {
            return directory;
        }

        Result<CommandOutput, string> file = await RunAsync(
            CatArguments(identity.Distribution, identity.User, InstanceUrlPath(directory.Value)),
            cancellationToken);
        if (file.IsFailure || file.Value.ExitCode != 0)
        {
            return Result<string, string>.Failure(UnreadableReason);
        }

        string? token = SecondLine(file.Value.Stdout);
        return string.IsNullOrWhiteSpace(token)
            ? Result<string, string>.Failure(UnreadableReason)
            : Result<string, string>.Success(token);
    }

    /// <summary>
    /// The wsl.exe argument list that cats one Linux path: distribution, user,
    /// the exec flag, cat, the end-of-options marker, then the path.
    /// </summary>
    internal static IReadOnlyList<string> CatArguments(string distribution, string user, string linuxPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distribution);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(linuxPath);
        return
        [
            "-d", distribution,
            "-u", user,
            "--exec", "cat",
            "--", linuxPath,
        ];
    }

    /// <summary>The wsl.exe argument list that prints one environment variable.</summary>
    internal static IReadOnlyList<string> PrintEnvArguments(string distribution, string user, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distribution);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return
        [
            "-d", distribution,
            "-u", user,
            "--exec", "printenv",
            "--", name,
        ];
    }

    /// <summary>The instance file under a Linux app-data directory.</summary>
    internal static string InstanceUrlPath(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        return TrimTrailingSlash(appDataDirectory) + "/" + InstanceLock.UrlFileName;
    }

    /// <summary>
    /// <c>config.json</c> inside a Linux app-data directory. The follower loads
    /// this file when no configuration path was named, and the file may point
    /// <c>appDataDirectory</c> somewhere else.
    /// </summary>
    internal static string DefaultConfigPath(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        return TrimTrailingSlash(appDataDirectory) + "/config.json";
    }

    /// <summary>
    /// The directory the follower uses when its configuration does not name one:
    /// <c>XDG_DATA_HOME</c> when that value is absolute, otherwise the home
    /// directory's local share. Same rule as the follower's own default.
    /// </summary>
    internal static string DefaultAppDataDirectory(string? xdgDataHome, string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        if (!string.IsNullOrEmpty(xdgDataHome) && xdgDataHome[0] == '/')
        {
            return TrimTrailingSlash(xdgDataHome) + "/" + ConfigurationDefaults.ProductToken;
        }

        return TrimTrailingSlash(home) + "/.local/share/" + ConfigurationDefaults.ProductToken;
    }

    /// <summary>
    /// The directory the follower's <c>instance.url</c> lives in. An explicit
    /// configuration path is read as named: its <c>appDataDirectory</c> wins,
    /// and a file that names none falls back to the default directory. With
    /// no path, that default directory is resolved first and its
    /// <c>config.json</c> is read, because the file may name a different
    /// directory. A missing, unreadable, or non-overriding default file keeps
    /// the default directory.
    /// </summary>
    private static async Task<Result<string, string>> AppDataDirectoryAsync(PeerFollowerIdentity identity, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(identity.ConfigPath))
        {
            Result<string, string> configured = await ReadNamedDirectoryAsync(identity, identity.ConfigPath, cancellationToken);
            if (configured.IsFailure)
            {
                return configured;
            }

            return configured.Value.Length == 0
                ? await DefaultDirectoryAsync(identity, cancellationToken)
                : configured;
        }

        Result<string, string> fallback = await DefaultDirectoryAsync(identity, cancellationToken);
        if (fallback.IsFailure)
        {
            return fallback;
        }

        Result<string, string> fromDefaultFile = await ReadNamedDirectoryAsync(identity, DefaultConfigPath(fallback.Value), cancellationToken);
        return fromDefaultFile.IsSuccess && fromDefaultFile.Value.Length > 0
            ? fromDefaultFile
            : fallback;
    }

    /// <summary>
    /// The root <c>appDataDirectory</c> named in one configuration file.
    /// Success with an empty string means the file parsed and did not name
    /// one. Failure means the file could not be read or would not parse.
    /// </summary>
    private static async Task<Result<string, string>> ReadNamedDirectoryAsync(PeerFollowerIdentity identity, string configPath, CancellationToken cancellationToken)
    {
        Result<CommandOutput, string> config = await RunAsync(
            CatArguments(identity.Distribution, identity.User, configPath),
            cancellationToken);
        if (config.IsFailure || config.Value.ExitCode != 0)
        {
            return Result<string, string>.Failure(UnreadableReason);
        }

        if (!TryReadAppDataDirectory(config.Value.Stdout, out string? configured))
        {
            return Result<string, string>.Failure(UnreadableReason);
        }

        return Result<string, string>.Success(configured ?? string.Empty);
    }

    private static async Task<Result<string, string>> DefaultDirectoryAsync(PeerFollowerIdentity identity, CancellationToken cancellationToken)
    {
        Result<string, string> home = await PrintEnvAsync(identity, "HOME", required: true, cancellationToken);
        if (home.IsFailure)
        {
            return home;
        }

        Result<string, string> xdg = await PrintEnvAsync(identity, "XDG_DATA_HOME", required: false, cancellationToken);
        if (xdg.IsFailure)
        {
            return xdg;
        }

        return Result<string, string>.Success(DefaultAppDataDirectory(xdg.Value, home.Value));
    }

    private static async Task<Result<string, string>> PrintEnvAsync(PeerFollowerIdentity identity, string name, bool required, CancellationToken cancellationToken)
    {
        Result<CommandOutput, string> printed = await RunAsync(PrintEnvArguments(identity.Distribution, identity.User, name), cancellationToken);
        if (printed.IsFailure)
        {
            return Result<string, string>.Failure(UnreadableReason);
        }

        string value = FirstLine(printed.Value.Stdout);
        if (printed.Value.ExitCode != 0 || value.Length == 0)
        {
            return required
                ? Result<string, string>.Failure(UnreadableReason)
                : Result<string, string>.Success(string.Empty);
        }

        return Result<string, string>.Success(value);
    }

    /// <summary>
    /// The root <c>appDataDirectory</c> string, or null when the key is absent,
    /// null, or blank and the follower would keep its default. False when the
    /// document will not parse; the text is not part of any failure.
    /// </summary>
    internal static bool TryReadAppDataDirectory(string json, out string? directory)
    {
        ArgumentNullException.ThrowIfNull(json);
        directory = null;
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (node is not JsonObject raw)
            {
                return false;
            }

            if (raw["appDataDirectory"] is JsonValue value
                && value.TryGetValue(out string? text)
                && !string.IsNullOrWhiteSpace(text))
            {
                directory = text;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string? SecondLine(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string[] lines = document.Split('\n');
        if (lines.Length < 2)
        {
            return null;
        }

        string token = lines[1].Trim('\r', ' ', '\t');
        return token.Length == 0 ? null : token;
    }

    private static async Task<Result<CommandOutput, string>> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using System.Diagnostics.Process process = new();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return Result<CommandOutput, string>.Failure(UnreadableReason);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return Result<CommandOutput, string>.Failure(UnreadableReason);
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(_bound);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(bounded.Token);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
            string output = await stdout;
            _ = await stderr;
            return Result<CommandOutput, string>.Success(new CommandOutput(process.ExitCode, output));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await DrainAsync(stdout, stderr);
            return Result<CommandOutput, string>.Failure(UnreadableReason);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainAsync(stdout, stderr);
            throw;
        }
        catch (IOException)
        {
            TryKill(process);
            await DrainAsync(stdout, stderr);
            return Result<CommandOutput, string>.Failure(UnreadableReason);
        }
    }

    private static async Task DrainAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await stdout;
        }
        catch (OperationCanceledException)
        {
            // The read was abandoned with the process.
        }
        catch (IOException)
        {
            // The pipe closed when the process was killed.
        }

        try
        {
            await stderr;
        }
        catch (OperationCanceledException)
        {
            // The read was abandoned with the process.
        }
        catch (IOException)
        {
            // The pipe closed when the process was killed.
        }
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The child is already gone.
        }
    }

    private static string TrimTrailingSlash(string path) => path.TrimEnd('/');

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        string line = newline < 0 ? text : text[..newline];
        return line.Trim('\r', ' ', '\t');
    }

    private readonly record struct CommandOutput(int ExitCode, string Stdout);
}
