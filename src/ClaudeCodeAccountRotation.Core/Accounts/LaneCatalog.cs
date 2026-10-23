using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>One lane a catalog role funds, and the secrets it reads today.</summary>
public sealed record LaneCatalogLane(string Id, string Workflow, string Launcher, IReadOnlyList<string> CurrentSecrets);

/// <summary>One role a lane catalog names, by its secret prefix in the catalog's organization.</summary>
public sealed record LaneCatalogRole(string Id, string Prefix, bool Keyed, string Category, IReadOnlyList<LaneCatalogLane> Lanes);

/// <summary>
/// A lane-funding catalog (version 1): the organization whose secrets fund the
/// CI lanes, and each role with its lanes. A role's token and key secrets must
/// be <c>&lt;PREFIX&gt;_CLAUDE_CODE_OAUTH_TOKEN</c> and
/// <c>&lt;PREFIX&gt;_CLAUDE_API_KEY</c>, and every name is held to
/// <see cref="CiTokenSecret.Parse"/>'s rules. Any other shape is refused whole.
/// </summary>
public sealed record LaneCatalog(string Organization, IReadOnlyList<LaneCatalogRole> Roles)
{
    public const int Version = 1;

    /// <summary>
    /// Whether <paramref name="role"/> is keyed: the catalog marks it keyed, or
    /// any lane in the catalog still reads its key secret. Null when the
    /// catalog does not speak for the role: another organization, or a
    /// repository role.
    /// </summary>
    public bool? IsKeyed(CiRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (role.Scope != CiSecretScope.Organization || !string.Equals(role.Owner, Organization, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (role.KeySecretName is not string key)
        {
            return false;
        }

        return Roles.Any(named => named.Keyed && string.Equals(named.Prefix, role.Prefix, StringComparison.OrdinalIgnoreCase))
            || Roles.SelectMany(static named => named.Lanes).Any(lane => lane.CurrentSecrets.Contains(key, StringComparer.OrdinalIgnoreCase));
    }

    public static Result<LaneCatalog, string> Parse(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException exception)
        {
            return Failure("is not JSON: " + exception.Message);
        }

        if (root is null)
        {
            return Failure("is not a JSON object");
        }

        if (root["version"] is not JsonValue versionValue || !versionValue.TryGetValue(out int version))
        {
            return Failure("has no version");
        }

        if (version != Version)
        {
            return Failure("is version " + version + "; this release reads version " + Version);
        }

        string? organization = Text(root, "org");
        if (organization is null || CiTokenSecret.Parse(CiTokenSecret.DefaultName, null, organization).IsFailure)
        {
            return Failure("has an org that is not a GitHub login");
        }

        if (root["roles"] is not JsonArray items)
        {
            return Failure("has no roles list");
        }

        List<LaneCatalogRole> roles = [];
        foreach (JsonNode? item in items)
        {
            Result<LaneCatalogRole, string> role = ParseRole(item, organization);
            if (role.IsFailure)
            {
                return Failure(role.Error);
            }

            if (roles.Any(seen => string.Equals(seen.Prefix, role.Value.Prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return Failure("names the prefix " + role.Value.Prefix + " twice");
            }

            roles.Add(role.Value);
        }

        return Result<LaneCatalog, string>.Success(new LaneCatalog(organization, roles));
    }

    private static Result<LaneCatalogRole, string> ParseRole(JsonNode? node, string organization)
    {
        if (node is not JsonObject item)
        {
            return Result<LaneCatalogRole, string>.Failure("has a role that is not an object");
        }

        string? prefix = Text(item, "secretPrefix");
        if (prefix is null || prefix.Length == 0)
        {
            return Result<LaneCatalogRole, string>.Failure("has a role with no secretPrefix");
        }

        Result<CiRole, string> parsed = CiRole.Parse(prefix, null, null, organization);
        if (parsed.IsFailure)
        {
            return Result<LaneCatalogRole, string>.Failure("has the prefix " + prefix + ": " + parsed.Error);
        }

        CiRole role = parsed.Value;
        if (!string.Equals(Text(item, "tokenSecret"), role.TokenSecretName, StringComparison.OrdinalIgnoreCase))
        {
            return Result<LaneCatalogRole, string>.Failure("gives " + prefix + " a tokenSecret other than " + role.TokenSecretName);
        }

        if (!string.Equals(Text(item, "keySecret"), role.KeySecretName, StringComparison.OrdinalIgnoreCase))
        {
            return Result<LaneCatalogRole, string>.Failure("gives " + prefix + " a keySecret other than " + role.KeySecretName);
        }

        if (item["keyed"] is not JsonValue keyedValue || !keyedValue.TryGetValue(out bool keyed))
        {
            return Result<LaneCatalogRole, string>.Failure("gives " + prefix + " a keyed that is not true or false");
        }

        // A list of any other shape is refused, never read as empty: an empty
        // currentSecrets would quietly turn a keyed role token-only.
        if (item["lanes"] is JsonNode laneList && laneList is not JsonArray)
        {
            return Result<LaneCatalogRole, string>.Failure("gives " + prefix + " lanes that are not a list");
        }

        List<LaneCatalogLane> lanes = [];
        foreach (JsonNode? laneNode in item["lanes"] as JsonArray ?? [])
        {
            if (laneNode is not JsonObject lane)
            {
                return Result<LaneCatalogRole, string>.Failure("gives " + prefix + " a lane that is not an object");
            }

            if (lane["currentSecrets"] is JsonNode secretList && secretList is not JsonArray)
            {
                return Result<LaneCatalogRole, string>.Failure("gives a lane of " + prefix + " currentSecrets that are not a list");
            }

            List<string> secrets = [];
            foreach (JsonNode? secret in lane["currentSecrets"] as JsonArray ?? [])
            {
                string? name = secret is JsonValue value && value.TryGetValue(out string? text) ? text : null;
                if (name is null || CiTokenSecret.Parse(name, null, organization).IsFailure)
                {
                    return Result<LaneCatalogRole, string>.Failure("gives a lane of " + prefix + " a current secret that is not a secret name");
                }

                secrets.Add(name);
            }

            lanes.Add(new LaneCatalogLane(Text(lane, "id") ?? string.Empty, Text(lane, "workflow") ?? string.Empty, Text(lane, "launcher") ?? string.Empty, secrets));
        }

        return Result<LaneCatalogRole, string>.Success(new LaneCatalogRole(Text(item, "id") ?? prefix.ToLowerInvariant(), role.Prefix, keyed, Text(item, "category") ?? string.Empty, lanes));
    }

    private static string? Text(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue(out string? text) ? text.Trim() : null;

    private static Result<LaneCatalog, string> Failure(string why) => Result<LaneCatalog, string>.Failure("the lane catalog " + why);
}
