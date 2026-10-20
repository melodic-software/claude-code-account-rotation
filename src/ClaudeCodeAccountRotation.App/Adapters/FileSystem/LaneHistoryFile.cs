using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// One change of which account backs one half of a CI role. <see cref="Account"/>
/// is null when the half now has no account, <see cref="Previous"/> when it had
/// none. Names and dates only: no secret value reaches this type.
/// </summary>
internal sealed record LaneHistoryEntry(
    DateTimeOffset At,
    CiRole Role,
    string Half,
    string? Account,
    string? Previous,
    string Via)
{
    public const string TokenHalf = "token";

    public const string KeyHalf = "key";

    public JsonObject ToJson()
    {
        JsonObject role = new()
        {
            ["prefix"] = Role.Prefix,
            [Role.Scope == CiSecretScope.Repository ? "repository" : "organization"] = Role.Owner,
        };
        if (Role.TokenSecretOverride is not null)
        {
            role["tokenSecret"] = Role.TokenSecretOverride;
        }

        return new JsonObject
        {
            ["at"] = At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["role"] = role,
            ["half"] = Half,
            ["account"] = Account,
            ["previous"] = Previous,
            ["via"] = Via,
        };
    }
}

/// <summary>
/// <c>lane-history.jsonl</c> under app data: an append-only record of which
/// account backed which half of which role, and when. One JSON object per line.
/// A missing file reads as empty and a line that does not parse is skipped, so
/// a write cut short costs that line and no other.
/// </summary>
internal sealed class LaneHistoryFile
{
    public const string FileName = "lane-history.jsonl";

    private readonly string _path;

    public LaneHistoryFile(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        _path = Path.Combine(Path.GetFullPath(appDataDirectory), FileName);
    }

    /// <summary>
    /// One entry per role half whose account differs between the two rosters.
    /// The account of a half is the entry that has it set; a role the new
    /// roster lacks has none.
    /// </summary>
    public static IReadOnlyList<LaneHistoryEntry> Diff(Roster before, Roster after, DateTimeOffset at, string via)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentException.ThrowIfNullOrWhiteSpace(via);
        List<LaneHistoryEntry> changes = [];
        foreach (CiRole role in Roles(after).Concat(Roles(before)).Distinct())
        {
            Compare(role, LaneHistoryEntry.TokenHalf, static assignment => assignment.HasToken);
            if (role.HasKeyHalf)
            {
                Compare(role, LaneHistoryEntry.KeyHalf, static assignment => assignment.HasKey);
            }
        }

        return changes;

        void Compare(CiRole role, string half, Func<CiRoleAssignment, bool> isSet)
        {
            string? previous = Holder(before, role, isSet);
            string? account = Holder(after, role, isSet);
            if (!string.Equals(previous, account, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new LaneHistoryEntry(at, role, half, account, previous, via));
            }
        }
    }

    public async Task AppendAsync(IReadOnlyList<LaneHistoryEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        StringBuilder text = new();
        foreach (LaneHistoryEntry entry in entries)
        {
            text.Append(entry.ToJson().ToJsonString()).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        FileStream stream = new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, useAsync: true);
        await using (stream)
        {
            // A last line cut short has no newline; without one the next entry
            // would join it and be lost with it.
            byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n')
                {
                    bytes = [(byte)'\n', .. bytes];
                }
            }

            stream.Seek(0, SeekOrigin.End);
            await stream.WriteAsync(bytes, cancellationToken);
        }
    }

    /// <summary>The entries at or after <paramref name="since"/> (all when null), oldest first.</summary>
    public async Task<IReadOnlyList<LaneHistoryEntry>> ReadAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        byte[] bytes;
        try
        {
            bytes = await SharedFileReader.ReadAllBytesAsync(_path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return [];
        }

        List<LaneHistoryEntry> entries = [];
        foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            if (Parse(line) is LaneHistoryEntry entry && (since is null || entry.At >= since))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static IEnumerable<CiRole> Roles(Roster roster) =>
        roster.Entries.SelectMany(static entry => entry.CiRoles).Select(static assignment => assignment.Role);

    private static string? Holder(Roster roster, CiRole role, Func<CiRoleAssignment, bool> isSet) =>
        roster.Entries
            .FirstOrDefault(entry => entry.CiRoles.Any(assignment => assignment.Role.Equals(role) && isSet(assignment)))
            ?.Email.Value;

    private static LaneHistoryEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(line) is not JsonObject entry || entry["role"] is not JsonObject role)
            {
                return null;
            }

            string? half = Text(entry, "half");
            string? via = Text(entry, "via");
            if (half is not (LaneHistoryEntry.TokenHalf or LaneHistoryEntry.KeyHalf)
                || via is null
                || !DateTimeOffset.TryParse(Text(entry, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at))
            {
                return null;
            }

            Result<CiRole, string> parsed = CiRole.Parse(Text(role, "prefix") ?? string.Empty, Text(role, "tokenSecret"), Text(role, "repository"), Text(role, "organization"));
            return parsed.IsFailure
                ? null
                : new LaneHistoryEntry(at, parsed.Value, half, Text(entry, "account"), Text(entry, "previous"), via);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
