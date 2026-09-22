using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Configuration;

/// <summary>
/// <c>config.json</c> under app data. A missing file is created on first run
/// from the embedded template, with the defaults resolved for this user, so
/// what the user edits is what the tool runs with; an existing file overrides
/// the defaults key by key, and a null or absent key keeps the default.
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
            Result<JsonObject, string> template = ReadTemplate();
            if (template.IsFailure)
            {
                return Result<ClaudeCodeAccountRotationConfiguration, string>.Failure(template.Error);
            }

            // The template holds the keys and no paths. Nulls take the defaults
            // computed for this user, and that resolved object is what is written.
            ClaudeCodeAccountRotationConfiguration resolved = Merge(defaults, template.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await AtomicJsonFile.WriteAsync(fullPath, ToJson(resolved), cancellationToken);
            return Result<ClaudeCodeAccountRotationConfiguration, string>.Success(resolved);
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

    private static Result<JsonObject, string> ReadTemplate()
    {
        try
        {
            if (JsonNode.Parse(EmbeddedConfigTemplate.Json) is not JsonObject template)
            {
                return Result<JsonObject, string>.Failure("the embedded configuration template is not a JSON object");
            }

            return Result<JsonObject, string>.Success(template);
        }
        catch (JsonException exception)
        {
            return Result<JsonObject, string>.Failure("the embedded configuration template could not be parsed: " + exception.Message);
        }
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
        ["peers"] = PeersToJson(configuration.Peers),
    };

    private static JsonArray PeersToJson(IReadOnlyList<PeerConfiguration>? peers)
    {
        JsonArray json = [];
        foreach (PeerConfiguration peer in peers ?? [])
        {
            json.Add(new JsonObject
            {
                ["side"] = peer.Side.Value,
                ["baseAddress"] = peer.BaseAddress.ToString(),
                ["storePathFromPeer"] = peer.StorePathFromPeer,
                ["distribution"] = peer.Distribution,
                ["user"] = peer.User,
                ["configPath"] = peer.ConfigPath,
                ["launch"] = peer.Launch is null ? null : new JsonObject
                {
                    ["distribution"] = peer.Launch.Distribution,
                    ["user"] = peer.Launch.User,
                    ["executablePath"] = peer.Launch.ExecutablePath,
                    ["port"] = peer.Launch.Port,
                    ["configPath"] = peer.Launch.ConfigPath,
                },
            });
        }

        return json;
    }

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
        // A live directory named in this file moves the state file with it, the
        // same way CLAUDE_CONFIG_DIR does in the defaults: <dir>/.claude.json.
        // An explicit stateFilePath still wins. A file that leaves the directory
        // unset keeps the default path, which already follows the environment.
        string? configuredLive = Text(raw, "liveConfigDirectory");
        string liveConfigDirectory = configuredLive ?? defaults.LiveConfigDirectory;
        string stateFilePath = Text(raw, "stateFilePath")
            ?? (configuredLive is not null
                ? Path.Combine(liveConfigDirectory, ".claude.json")
                : defaults.StateFilePath);
        return new ClaudeCodeAccountRotationConfiguration(
            liveConfigDirectory,
            stateFilePath,
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
            Text(raw, "mailbox") ?? defaults.Mailbox,
            Peers(raw) ?? defaults.Peers);
    }

    /// <summary>
    /// <c>peers[]</c>: the other sides of this machine. An entry missing
    /// <c>side</c>, a usable <c>baseAddress</c>, or <c>storePathFromPeer</c> is
    /// dropped rather than failing the whole file, because the alternative is a
    /// tool that will not start over a key that only disables one lane. The
    /// dropped entry shows up as a side that is simply not on the page.
    /// <para>
    /// <c>distribution</c>, <c>user</c>, and <c>configPath</c> sit beside
    /// <c>launch</c>. They name a follower the operator started. A distribution
    /// without a user, or a user without a distribution, is stored as neither,
    /// and the entry stays. <c>configPath</c> is kept either way.
    /// </para>
    /// </summary>
    private static List<PeerConfiguration>? Peers(JsonObject raw)
    {
        if (raw["peers"] is not JsonArray entries)
        {
            return null;
        }

        List<PeerConfiguration> peers = [];
        foreach (JsonNode? node in entries)
        {
            if (node is not JsonObject peer
                || Text(peer, "side") is not string side
                || !IsUsableAsPathSegment(side)
                || Text(peer, "baseAddress") is not string address
                || !Uri.TryCreate(address, UriKind.Absolute, out Uri? baseAddress)
                || Text(peer, "storePathFromPeer") is not string storePath)
            {
                continue;
            }

            (string? distribution, string? user) = DistributionAndUser(peer);
            peers.Add(new PeerConfiguration(
                new SideName(side),
                baseAddress,
                storePath,
                Launch(peer),
                distribution,
                user,
                Text(peer, "configPath")));
        }

        return peers;
    }

    /// <summary>
    /// Peer-level <c>distribution</c> and <c>user</c>. Either one without the
    /// other is neither: the entry stays, and nothing is recorded as identity.
    /// </summary>
    private static (string? Distribution, string? User) DistributionAndUser(JsonObject peer)
    {
        string? distribution = Text(peer, "distribution");
        string? user = Text(peer, "user");
        return distribution is not null && user is not null ? (distribution, user) : (null, null);
    }

    /// <summary>
    /// A side names a directory under the store's <c>.transit/</c> and a
    /// directory in the peer's own namespace, so a hand-edited one has to be a
    /// single path segment before anything joins it to a root. A value with a
    /// separator, a rooted path, a traversal, or a character the file system
    /// refuses would otherwise reach <c>Path.Combine</c> — where <c>../</c>
    /// escapes the store and an invalid character throws — and both
    /// <c>MailboxPath</c> and <c>Peer.InPeerNamespace</c> would carry it.
    /// Dropped like every other malformed key here rather than failing the
    /// file: the side simply does not appear on the page.
    /// </summary>
    private static bool IsUsableAsPathSegment(string side) =>
        !string.IsNullOrWhiteSpace(side)
        && side == side.Trim()
        && side is not ("." or "..")
        && side.AsSpan().IndexOfAny('/', '\\', ':') < 0
        && side.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static PeerLaunch? Launch(JsonObject peer) =>
        peer["launch"] is JsonObject launch
        && Text(launch, "distribution") is string distribution
        && Text(launch, "user") is string user
        && Text(launch, "executablePath") is string executablePath
        && Number(launch, "port") is double port
            ? new PeerLaunch(distribution, user, executablePath, (int)port, Text(launch, "configPath"))
            : null;

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
