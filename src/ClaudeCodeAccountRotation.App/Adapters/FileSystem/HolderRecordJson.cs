using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// A <see cref="HolderRecord"/> stored as a named JSON file in a store slot:
/// the one reader and writer behind <see cref="HolderRecordFile"/> and
/// <see cref="SupersededFamilyFile"/>, which differ only in the file name.
/// Written through <see cref="AtomicJsonFile"/>, so it is owner-only on Windows
/// like every other file this tool creates, and a reader sees the old bytes or
/// the new bytes and never a torn file.
/// <para>
/// The record is an index, never a token, so every way of failing to read one
/// answers the same: no record.
/// </para>
/// </summary>
internal static class HolderRecordJson
{
    public static async Task<HolderRecord?> ReadAsync(string folderPath, string fileName, CancellationToken cancellationToken)
    {
        string path = Path.Combine(folderPath, fileName);
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

    public static Task WriteAsync(string folderPath, string fileName, HolderRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(folderPath);
        return AtomicJsonFile.WriteAsync(
            Path.Combine(folderPath, fileName),
            new JsonObject
            {
                ["side"] = record.Side.Value,
                ["fingerprint"] = record.Fingerprint.Sha256Hex,
                ["since"] = record.Since.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken);
    }

    /// <summary>Removes the record, if there is one. A slot without one is already in the wanted state.</summary>
    public static Task DeleteAsync(string folderPath, string fileName, CancellationToken cancellationToken)
    {
        string path = Path.Combine(folderPath, fileName);
        return File.Exists(path) ? AtomicBytesFile.DeleteWithRetryAsync(path, cancellationToken) : Task.CompletedTask;
    }

    private static string? Text(JsonObject? raw, string key) =>
        raw?[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
