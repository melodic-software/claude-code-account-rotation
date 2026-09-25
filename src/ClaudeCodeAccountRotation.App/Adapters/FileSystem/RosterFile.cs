using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// <c>roster.json</c> under app data: the accounts the operator put on this
/// machine. Written through <see cref="AtomicJsonFile"/>, so a reader sees the
/// old list or the new one and never a torn file. An absent file is an empty
/// roster, and an entry whose e-mail no longer parses is dropped rather than
/// failing every read.
/// </summary>
internal sealed class RosterFile : IDisposable
{
    public const string FileName = "roster.json";

    private const string CiTokenDateFormat = "yyyy-MM-dd";

    // Read-modify-write, so two edits arriving together must not lose one. One
    // process-wide gate is the whole story: the instance lock already makes this
    // the only process editing the file.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public RosterFile(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        _path = Path.Combine(Path.GetFullPath(appDataDirectory), FileName);
    }

    public async Task<Roster> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnguardedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies <paramref name="change"/> to the stored roster and writes the result back.</summary>
    public async Task<Roster> UpdateAsync(Func<Roster, Roster> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Roster updated = change(await ReadUnguardedAsync(cancellationToken));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await AtomicJsonFile.WriteAsync(_path, ToJson(updated), cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static JsonObject ToJson(Roster roster)
    {
        JsonArray accounts = [];
        foreach (RosterEntry entry in roster.Entries)
        {
            accounts.Add(new JsonObject
            {
                ["email"] = entry.Email.Value,
                ["alias"] = entry.Alias,
                ["browser"] = entry.Browser?.ToString().ToLowerInvariant(),
                ["browserProfileDirectory"] = entry.BrowserProfileDirectory,
                ["paused"] = entry.Paused,
                ["notes"] = entry.Notes,
                ["ciTokenGeneratedOn"] = entry.CiTokenGeneratedOn?.ToString(CiTokenDateFormat, CultureInfo.InvariantCulture),
            });
        }

        return new JsonObject { ["accounts"] = accounts };
    }

    private static RosterEntry? FromJson(JsonNode? node)
    {
        if (node is not JsonObject entry || Text(entry, "email") is not string address)
        {
            return null;
        }

        Result<AccountEmail, string> email = AccountEmail.Parse(address);
        return email.IsFailure
            ? null
            : new RosterEntry(
                email.Value,
                Text(entry, "alias"),
                ParseBrowser(Text(entry, "browser")),
                Text(entry, "browserProfileDirectory"),
                entry["paused"] is JsonValue paused && paused.TryGetValue(out bool value) && value,
                Text(entry, "notes"),
                ParseCiTokenDate(Text(entry, "ciTokenGeneratedOn")));
    }

    private static DateOnly? ParseCiTokenDate(string? value) =>
        value is not null && DateOnly.TryParseExact(value, CiTokenDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)
            ? parsed
            : null;

    private static BrowserFamily? ParseBrowser(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out BrowserFamily browser) ? browser : null;

    private static string? Text(JsonObject entry, string key) =>
        entry[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private async Task<Roster> ReadUnguardedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return Roster.Empty;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(await SharedFileReader.ReadAllBytesAsync(_path, cancellationToken));
        }
        catch (JsonException)
        {
            // A roster the operator hand-edited into invalid JSON must not take the
            // page down; it reads as empty and the next write replaces it.
            return Roster.Empty;
        }

        return parsed is not JsonObject root || root["accounts"] is not JsonArray accounts
            ? Roster.Empty
            : new Roster(accounts.Select(FromJson).OfType<RosterEntry>());
    }
}
