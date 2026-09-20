namespace ClaudeCodeAccountRotation.Core.Configuration;

/// <summary>
/// Everything the tool reads from its configuration file. Every path defaults
/// from the user profile at runtime; the shipped template carries no literal
/// path, drive letter, or user name.
/// </summary>
public sealed record ClaudeCodeAccountRotationConfiguration(
    string LiveConfigDirectory,
    string StateFilePath,
    string ProfilesRoot,
    string AppDataDirectory,
    string StatuslineTeePath,
    int ListenPort,
    TimeSpan RefreshLockWaitBound,
    string? ClaudeExecutable,
    string UserAgentProductToken,
    IReadOnlyDictionary<string, string> BrowserExecutables,
    RotationRole Role = RotationRole.Leader,
    string? Mailbox = null);
