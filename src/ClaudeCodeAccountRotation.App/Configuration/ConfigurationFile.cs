using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Configuration;

/// <summary>
/// <c>config.json</c> under app data. A missing file is created on first run
/// with the defaults resolved for this user, so what the user edits is what
/// the tool runs with; an existing file overrides the defaults key by key, and
/// a null or absent key keeps the default.
/// </summary>
internal static class ConfigurationFile
{
    public static async Task<Result<ClaudeCodeAccountRotationConfiguration, string>> LoadOrCreateAsync(
        string path,
        ClaudeCodeAccountRotationConfiguration defaults,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(defaults);
        string fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await AtomicJsonFile.WriteAsync(fullPath, ToJson(defaults), cancellationToken);
            return Result<ClaudeCodeAccountRotationConfiguration, string>.Success(defaults);
        }

        JsonObject raw;
        try
        {
            var node = JsonNode.Parse(await SharedFileReader.ReadAllBytesAsync(fullPath, cancellationToken), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (node is not JsonObject parsed)
            {
                return Result<ClaudeCodeAccountRotationConfiguration, string>.Failure("the configuration file " + fullPath + " is not a JSON object");
            }

            raw = parsed;
        }
        catch (JsonException exception)
        {
            return Result<ClaudeCodeAccountRotationConfiguration, string>.Failure("the configuration file " + fullPath + " could not be parsed: " + exception.Message);
        }

        return Result<ClaudeCodeAccountRotationConfiguration, string>.Success(Merge(defaults, raw));
    }

    private static JsonObject ToJson(ClaudeCodeAccountRotationConfiguration configuration) => new()
    {
        ["liveConfigDirectory"] = configuration.LiveConfigDirectory,
        ["stateFilePath"] = configuration.StateFilePath,
        ["profilesRoot"] = configuration.ProfilesRoot,
        ["appDataDirectory"] = configuration.AppDataDirectory,
        ["listenPort"] = configuration.ListenPort,
        ["refreshLockWaitSeconds"] = configuration.RefreshLockWaitBound.TotalSeconds,
        ["claudeExecutable"] = configuration.ClaudeExecutable,
        ["userAgentProductToken"] = configuration.UserAgentProductToken,
        ["browserExecutables"] = BrowserExecutablesToJson(configuration.BrowserExecutables),
        ["store"] = new JsonObject { ["shared"] = configuration.SharedStore },
        ["role"] = RoleName(configuration.Role),
        ["mailbox"] = configuration.Mailbox,
    };

    private static string RoleName(RotationRole role) => role == RotationRole.Follower ? "follower" : "leader";

    private static JsonObject BrowserExecutablesToJson(IReadOnlyDictionary<string, string> executables)
    {
        JsonObject json = [];
        foreach ((string browser, string path) in executables)
        {
            json[browser] = path;
        }

        return json;
    }

    private static ClaudeCodeAccountRotationConfiguration Merge(ClaudeCodeAccountRotationConfiguration defaults, JsonObject raw)
    {
        string liveConfigDirectory = Text(raw, "liveConfigDirectory") ?? defaults.LiveConfigDirectory;
        return new ClaudeCodeAccountRotationConfiguration(
            liveConfigDirectory,
            Text(raw, "stateFilePath") ?? defaults.StateFilePath,
            Text(raw, "profilesRoot") ?? defaults.ProfilesRoot,
            Text(raw, "appDataDirectory") ?? defaults.AppDataDirectory,
            // Always derived: the tee lives inside the live directory, so a file that
            // moves the live directory moves the tee with it. No knob until a layout
            // exists that needs one.
            ConfigurationDefaults.TeePathFor(liveConfigDirectory),
            Number(raw, "listenPort") is double port ? (int)port : defaults.ListenPort,
            Number(raw, "refreshLockWaitSeconds") is double seconds ? TimeSpan.FromSeconds(seconds) : defaults.RefreshLockWaitBound,
            Text(raw, "claudeExecutable") ?? defaults.ClaudeExecutable,
            Text(raw, "userAgentProductToken") ?? defaults.UserAgentProductToken,
            BrowserExecutables(raw) ?? defaults.BrowserExecutables,
            SharedStore(raw) ?? defaults.SharedStore,
            Role(raw) ?? defaults.Role,
            Text(raw, "mailbox") ?? defaults.Mailbox);
    }

    /// <summary>
    /// <c>store.shared</c>: whether the two sides of this machine share one
    /// account store. Nested under <c>store</c> because the follower's own keys
    /// join it there, and absent or malformed means the default, which is off.
    /// </summary>
    private static bool? SharedStore(JsonObject raw) =>
        raw["store"] is JsonObject store ? Flag(store, "shared") : null;

    /// <summary>
    /// <c>role</c>: <c>follower</c> puts this process on the WSL side, and
    /// anything else — including an absent or unrecognized value — leaves it the
    /// leader. A machine that mistypes its role gets the side that owns the
    /// store and refuses nothing it could do before, rather than a process with
    /// no roster and no page.
    /// </summary>
    private static RotationRole? Role(JsonObject raw) =>
        Text(raw, "role") is string role && role.Trim().Equals("follower", StringComparison.OrdinalIgnoreCase)
            ? RotationRole.Follower
            : null;

    /// <summary>
    /// <c>browserExecutables</c>: a browser name (<c>chrome</c>, <c>edge</c>,
    /// <c>brave</c>) to the full path of its executable. Matched
    /// case-insensitively, so the operator's capitalization never decides
    /// whether an override is found.
    /// </summary>
    private static Dictionary<string, string>? BrowserExecutables(JsonObject raw)
    {
        if (raw["browserExecutables"] is not JsonObject overrides)
        {
            return null;
        }

        Dictionary<string, string> executables = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string browser, JsonNode? path) in overrides)
        {
            if (path is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                executables[browser] = text;
            }
        }

        return executables;
    }

    private static string? Text(JsonObject raw, string key) =>
        raw[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static double? Number(JsonObject raw, string key) =>
        raw[key] is JsonValue value && value.TryGetValue(out double number) ? number : null;

    private static bool? Flag(JsonObject raw, string key) =>
        raw[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;
}
