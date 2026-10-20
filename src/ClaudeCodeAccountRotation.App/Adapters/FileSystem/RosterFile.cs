using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// <c>roster.json</c> under app data: the accounts the operator put on this
/// machine. Written through <see cref="AtomicJsonFile"/>, so a reader sees the
/// old list or the new one and never a torn file. An absent file is an empty
/// roster, and an entry whose e-mail no longer parses is dropped rather than
/// failing every read.
/// </summary>
internal sealed partial class RosterFile : IDisposable
{
    public const string FileName = "roster.json";

    private const string CiTokenDateFormat = "yyyy-MM-dd";

    // Read-modify-write, so two edits arriving together must not lose one. One
    // process-wide gate is the whole story: the instance lock already makes this
    // the only process editing the file.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    public RosterFile(string appDataDirectory, TimeProvider? clock = null, ILogger<RosterFile>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        _path = Path.Combine(Path.GetFullPath(appDataDirectory), FileName);
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RosterFile>.Instance;
        History = new LaneHistoryFile(appDataDirectory);
    }

    /// <summary>The record of which account backed which role half, appended to by every update.</summary>
    public LaneHistoryFile History { get; }

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

    /// <summary>
    /// Applies <paramref name="change"/> to the stored roster and writes the result back,
    /// then appends to the lane history one line per role half whose account changed,
    /// tagged <paramref name="via"/>.
    /// </summary>
    public async Task<Roster> UpdateAsync(Func<Roster, Roster> change, CancellationToken cancellationToken, string via = "edit")
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Roster before = await ReadUnguardedAsync(cancellationToken);
            Roster updated = change(before);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await AtomicJsonFile.WriteAsync(_path, ToJson(updated), cancellationToken);
            try
            {
                // The roster write has committed, so the append must outlive a
                // cancelled request.
                await History.AppendAsync(LaneHistoryFile.Diff(before, updated, _clock.GetUtcNow(), via), CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The edit is already stored; a history line that cannot be
                // appended must not report it as failed, but it is not silent.
                LogHistoryAppendFailed(LaneHistoryFile.FileName, exception.GetType().Name);
            }

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not append to {File}: {ExceptionType}")]
    private partial void LogHistoryAppendFailed(string file, string exceptionType);

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
                ["ciTokenGeneratedOn"] = DateText(entry.CiTokenGeneratedOn),
                ["consoleApiKeySetOn"] = DateText(entry.ConsoleApiKeySetOn),
                ["ciRoles"] = new JsonArray([.. entry.CiRoles.Select(RoleToJson)]),
            });
        }

        return new JsonObject { ["accounts"] = accounts };
    }

    private static JsonNode RoleToJson(CiRoleAssignment assignment)
    {
        CiRole role = assignment.Role;
        JsonObject node = new()
        {
            ["prefix"] = role.Prefix,
            [role.Scope == CiSecretScope.Repository ? "repository" : "organization"] = role.Owner,
        };
        if (role.TokenSecretOverride is not null)
        {
            node["tokenSecret"] = role.TokenSecretOverride;
        }

        node["tokenSetOn"] = DateText(assignment.TokenSetOn);
        node["tokenSetAt"] = InstantText(assignment.TokenSetAt);
        node["keySetOn"] = DateText(assignment.KeySetOn);
        node["keySetAt"] = InstantText(assignment.KeySetAt);
        return node;
    }

    private static string? DateText(DateOnly? value) => value?.ToString(CiTokenDateFormat, CultureInfo.InvariantCulture);

    private static string? InstantText(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture);

    private static RosterEntry? FromJson(JsonNode? node)
    {
        if (node is not JsonObject entry || Text(entry, "email") is not string address)
        {
            return null;
        }

        Result<AccountEmail, string> email = AccountEmail.Parse(address);
        if (email.IsFailure)
        {
            return null;
        }

        DateOnly? ciTokenGeneratedOn = ParseCiTokenDate(Text(entry, "ciTokenGeneratedOn"));
        return new RosterEntry(
            email.Value,
            Text(entry, "alias"),
            ParseBrowser(Text(entry, "browser")),
            Text(entry, "browserProfileDirectory"),
            entry["paused"] is JsonValue paused && paused.TryGetValue(out bool value) && value,
            Text(entry, "notes"),
            ciTokenGeneratedOn)
        {
            CiRoles = entry["ciRoles"] is JsonArray roles
                ? [.. roles.Select(RoleFromJson).OfType<CiRoleAssignment>().DistinctBy(static assignment => assignment.Role)]
                : [.. LegacyRole(entry["ciTokenSecret"], ciTokenGeneratedOn)],
            ConsoleApiKeySetOn = ParseCiTokenDate(Text(entry, "consoleApiKeySetOn")),
        };
    }

    /// <summary>
    /// The one role an earlier release's <c>ciTokenSecret</c> names, with its
    /// token half set on <c>ciTokenGeneratedOn</c>. None for an entry with no
    /// such key or no date, or one hand-edited past the rules
    /// <see cref="CiTokenSecret.Parse"/> holds a new one to.
    /// </summary>
    private static IEnumerable<CiRoleAssignment> LegacyRole(JsonNode? node, DateOnly? tokenSetOn)
    {
        if (node is JsonObject secret && tokenSetOn is not null)
        {
            Result<CiTokenSecret, string> parsed = CiTokenSecret.Parse(Text(secret, "name"), Text(secret, "repository"), Text(secret, "organization"));
            if (parsed.IsSuccess)
            {
                yield return new CiRoleAssignment(CiRole.FromTokenSecret(parsed.Value), tokenSetOn);
            }
        }
    }

    /// <summary>
    /// One recorded role, or null for one that fails <see cref="CiRole.Parse"/>
    /// or has neither half set. A key half on a token-only role is not kept.
    /// </summary>
    private static CiRoleAssignment? RoleFromJson(JsonNode? node)
    {
        if (node is not JsonObject role)
        {
            return null;
        }

        string? prefix = role["prefix"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        Result<CiRole, string> parsed = CiRole.Parse(prefix, Text(role, "tokenSecret"), Text(role, "repository"), Text(role, "organization"));
        if (parsed.IsFailure)
        {
            return null;
        }

        DateOnly? tokenSetOn = ParseCiTokenDate(Text(role, "tokenSetOn"));
        DateOnly? keySetOn = parsed.Value.HasKeyHalf ? ParseCiTokenDate(Text(role, "keySetOn")) : null;
        return tokenSetOn is null && keySetOn is null
            ? null
            : new CiRoleAssignment(
                parsed.Value,
                tokenSetOn,
                tokenSetOn is null ? null : ParseInstant(Text(role, "tokenSetAt")),
                keySetOn,
                keySetOn is null ? null : ParseInstant(Text(role, "keySetAt")));
    }

    private static DateOnly? ParseCiTokenDate(string? value) =>
        value is not null && DateOnly.TryParseExact(value, CiTokenDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ParseInstant(string? value) =>
        value is not null && DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
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
