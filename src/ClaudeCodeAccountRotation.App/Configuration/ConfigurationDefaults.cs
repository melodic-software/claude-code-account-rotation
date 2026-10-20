using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Configuration;

/// <summary>
/// Defaults computed from the user profile: the live directory follows
/// <c>CLAUDE_CONFIG_DIR</c> exactly as the CLI does (the state file sits at the
/// home root when it is unset and inside the directory when it is set), and app
/// data goes under the platform's per-user local data directory.
/// </summary>
internal static class ConfigurationDefaults
{
    public const int DefaultListenPort = 48211;
    public const string ProductToken = "claude-code-account-rotation";
    public const string RemoteControlTask = "claude-remote-control";

    private const string TeeDirectoryName = "rate-limit-guard";
    private const string TeeFileName = "rate-limits.json";

    /// <summary>
    /// The rate-limit-guard tee: written by live sessions inside the live config
    /// directory, so it follows that directory wherever it is set, whether by
    /// <c>CLAUDE_CONFIG_DIR</c> or by the configuration file's own
    /// <c>liveConfigDirectory</c>.
    /// </summary>
    public static string TeePathFor(string liveConfigDirectory) =>
        Path.Combine(liveConfigDirectory, TeeDirectoryName, TeeFileName);

    public static readonly TimeSpan DefaultRefreshLockWaitBound = TimeSpan.FromSeconds(10);

    public static ClaudeCodeAccountRotationConfiguration ForCurrentUser()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? claudeConfigDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return ForUser(home, localAppData, claudeConfigDirectory);
    }

    public static ClaudeCodeAccountRotationConfiguration ForUser(string homeDirectory, string localAppDataDirectory, string? claudeConfigDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataDirectory);
        bool configured = !string.IsNullOrWhiteSpace(claudeConfigDirectory);
        string liveConfigDirectory = configured ? Path.GetFullPath(claudeConfigDirectory!) : Path.Combine(homeDirectory, ".claude");
        string stateFilePath = configured ? Path.Combine(liveConfigDirectory, ".claude.json") : Path.Combine(homeDirectory, ".claude.json");
        return new ClaudeCodeAccountRotationConfiguration(
            liveConfigDirectory,
            stateFilePath,
            Path.Combine(homeDirectory, ".claude-profiles"),
            Path.Combine(localAppDataDirectory, ProductToken),
            TeePathFor(liveConfigDirectory),
            DefaultListenPort,
            DefaultRefreshLockWaitBound,
            ClaudeExecutable: null,
            ProductToken,
            // No override until the operator writes one: the launcher looks in the
            // platform's known install locations first.
            System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty,
            RemoteControlTask: RemoteControlTask);
    }
}
