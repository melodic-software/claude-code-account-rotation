using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>
/// Where a successful read's numbers go so they survive a restart and the card
/// can say "via cached" with the original capture time instead of falling back
/// to "unknown".
/// <para>
/// The operator restarts the tool to install a build, and a pass costs the rate
/// window; without this file every card goes back to "unknown" and the window
/// pays for the same ten reads again. A cached figure stays honest without any
/// staleness rule of its own, because the card blanks a row whose reset time has
/// passed since it was captured and names the source on every "as of" line.
/// </para>
/// <para>
/// What is in the file is the security property: percentages, instants, bucket
/// names, and e-mail addresses. No token of any kind, and nothing derived from
/// one. It sits under the same app data as the credential-bearing recovery
/// files, and a reader looking for a second holder of a lineage must be able to
/// rule this out by its shape.
/// </para>
/// </summary>
internal sealed class UsageSnapshotCache : IDisposable
{
    public const string FileName = "usage-cache.json";

    // A semaphore rather than a Lock: the merge reads and writes a file, so the
    // section it protects awaits. Two accounts finishing their reads in one pass
    // must not interleave a read-modify-write, or the second would save the file
    // it read before the first one landed and drop that account's entry.
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly string _path;

    public UsageSnapshotCache(SwitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = Path.Combine(Path.GetFullPath(options.AppDataDirectory), "state", FileName);
    }

    /// <summary>
    /// Merges one account's newest numbers into the file. Failure is silent by
    /// design: the snapshot is already in <see cref="QuotaState"/> and is what the
    /// page renders either way, so a file this tool could not write costs a
    /// restart's memory and must not turn a successful read into a failed one on
    /// the card.
    /// <para>
    /// It takes no cancellation token, which is the point rather than an
    /// oversight: the same reasoning the gated write-back runs under. By the time
    /// this is called the read has already been paid for out of the rate window,
    /// and a shutdown arriving between the read and this write would throw that
    /// read away and leave the pass's own outcome unrecorded. Having no parameter
    /// at all is what makes that unarguable at every call site. The section is one
    /// small file written beside its temp, well inside the host's drain.
    /// </para>
    /// </summary>
    public async Task SaveAsync(AccountEmail account, UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _mutex.WaitAsync(CancellationToken.None);
        try
        {
            // A missing or unreadable file starts from empty rather than refusing
            // to save: the alternative is an account whose numbers can never be
            // cached again because something once wrote a torn byte.
            JsonObject entries = await ReadForMergeAsync(CancellationToken.None) ?? [];
            entries[account.Value] = Entry(snapshot);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await AtomicJsonFile.WriteAsync(_path, entries, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Every entry the file still holds, as snapshots attributed to
    /// <see cref="QuotaSource.Cached"/> and keeping the instant they were captured
    /// at. Read once at startup.
    /// <para>
    /// Tolerant throughout, the tee reader's posture: a missing file, a torn one,
    /// one that parses as something else, and an entry naming an unparsable
    /// e-mail all load nothing rather than failing. An entry is loaded whole or
    /// not at all, so a card never shows a half-read set of buckets.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<UsageSnapshot>> LoadAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await SharedFileReader.ReadAllBytesAsync(_path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            List<UsageSnapshot> loaded = [];
            foreach (JsonProperty entry in document.RootElement.EnumerateObject())
            {
                if (Snapshot(entry.Name, entry.Value) is UsageSnapshot snapshot)
                {
                    loaded.Add(snapshot);
                }
            }

            return loaded;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Dispose() => _mutex.Dispose();

    private static JsonObject Entry(UsageSnapshot snapshot)
    {
        JsonArray limits = [];
        foreach (UsageLimit limit in snapshot.Limits)
        {
            limits.Add(new JsonObject
            {
                ["rawKind"] = limit.RawKind,
                ["kind"] = limit.Kind.ToString(),
                ["group"] = limit.Group,
                ["percent"] = limit.Percent,
                ["severity"] = limit.Severity,
                ["resetsAt"] = Instant(limit.ResetsAt),
                ["scopeDisplayName"] = limit.ScopeDisplayName,
                ["isActive"] = limit.IsActive,
            });
        }

        return new JsonObject
        {
            ["capturedAt"] = Instant(snapshot.CapturedAt),
            // The source that captured the numbers, kept for the operator reading
            // the file by hand; every entry loads as Cached regardless, because
            // that is what it is once it comes back off disk.
            ["source"] = snapshot.Source.ToString(),
            ["limits"] = limits,
            ["extraUsage"] = snapshot.ExtraUsage is ExtraUsageState credits
                ? new JsonObject
                {
                    ["isEnabled"] = credits.IsEnabled,
                    ["disabledReason"] = credits.DisabledReason,
                    ["spendLimitReached"] = credits.SpendLimitReached,
                }
                : null,
        };
    }

    /// <summary>Round-trip form, so the instant that comes back is the instant that went in.</summary>
    private static string? Instant(DateTimeOffset? value) =>
        value?.ToString("o", CultureInfo.InvariantCulture);

    private static UsageSnapshot? Snapshot(string email, JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || AccountEmail.Parse(email).Match(static parsed => (AccountEmail?)parsed, static _ => null) is not AccountEmail account
            || JsonInstant.Read(entry, "capturedAt") is not DateTimeOffset capturedAt
            || !entry.TryGetProperty("limits", out JsonElement rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<UsageLimit> limits = [];
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (Limit(row) is not UsageLimit limit)
            {
                return null;
            }

            limits.Add(limit);
        }

        return new UsageSnapshot(account, capturedAt, QuotaSource.Cached, limits, Credits(entry));
    }

    private static UsageLimit? Limit(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object
            || Text(row, "rawKind") is not string rawKind
            || Text(row, "kind") is not string kindName
            || !Enum.TryParse(kindName, out LimitKind kind)
            || !row.TryGetProperty("percent", out JsonElement percent)
            || percent.ValueKind != JsonValueKind.Number
            || !percent.TryGetDouble(out double used)
            // Range-checked like every other number read off a file: a percentage
            // outside the scale would draw a bar past the end of the card.
            || used is < 0 or > 100
            || !row.TryGetProperty("isActive", out JsonElement active)
            || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return new UsageLimit(
            rawKind,
            kind,
            Text(row, "group"),
            used,
            Text(row, "severity"),
            JsonInstant.Read(row, "resetsAt"),
            Text(row, "scopeDisplayName"),
            active.GetBoolean());
    }

    private static ExtraUsageState? Credits(JsonElement entry) =>
        entry.TryGetProperty("extraUsage", out JsonElement credits) && credits.ValueKind == JsonValueKind.Object
            ? new ExtraUsageState(
                Flag(credits, "isEnabled"),
                Text(credits, "disabledReason"),
                Flag(credits, "spendLimitReached"))
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<JsonObject?> ReadForMergeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return JsonNode.Parse(await SharedFileReader.ReadAllBytesAsync(_path, cancellationToken)) as JsonObject;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
