using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// <c>holder.json</c> in a store slot: which side took the slot's pair, which
/// pair it was, and since when. Written through <see cref="AtomicJsonFile"/>,
/// so it is owner-only on Windows like every other file this tool creates, and
/// a reader sees the old bytes or the new bytes and never a torn file.
/// <para>
/// The record is an index, never a token, so every way of failing to read one
/// answers the same: no record. A missing, torn, or unparsable file leaves the
/// slot to be judged by what it holds, which is the rule the design settles on
/// wherever the two disagree.
/// </para>
/// </summary>
internal static class HolderRecordFile
{
    public const string FileName = "holder.json";

    public static string PathIn(string folderPath) =>
        Path.Combine(folderPath, FileName);

    public static async Task<HolderRecord?> ReadAsync(string folderPath, CancellationToken cancellationToken)
    {
        string path = PathIn(folderPath);
        if (!File.Exists(path))
        {
            return null;
        }

        JsonObject? raw;
        try
        {
            raw = JsonNode.Parse(await SharedFileReader.ReadAllBytesAsync(path, cancellationToken)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (Text(raw, "side") is not string side
            || Text(raw, "fingerprint") is not string fingerprint
            || Text(raw, "since") is not string since
            || !DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at))
        {
            return null;
        }

        return new HolderRecord(new SideName(side), new RefreshTokenFingerprint(fingerprint), at);
    }

    public static Task WriteAsync(string folderPath, HolderRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(folderPath);
        return AtomicJsonFile.WriteAsync(
            PathIn(folderPath),
            new JsonObject
            {
                ["side"] = record.Side.Value,
                ["fingerprint"] = record.Fingerprint.Sha256Hex,
                ["since"] = record.Since.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken);
    }

    /// <summary>Removes the record, if there is one. A slot without one is already in the wanted state.</summary>
    public static Task DeleteAsync(string folderPath, CancellationToken cancellationToken)
    {
        string path = PathIn(folderPath);
        return File.Exists(path) ? AtomicBytesFile.DeleteWithRetryAsync(path, cancellationToken) : Task.CompletedTask;
    }

    private static string? Text(JsonObject? raw, string key) =>
        raw?[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
