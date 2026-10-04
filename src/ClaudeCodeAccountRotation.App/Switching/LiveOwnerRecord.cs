using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// The account a live pair was put in place for, bound to that pair's
/// fingerprint, and the account block that went with it. Written by the
/// leader's switch and by the follower's import, read by both sides' stale
/// identity repair and by the leader's planner. The one reader and writer of
/// <c>state/live-owner.json</c>, so the two sides cannot drift apart on what the
/// record means.
/// </summary>
internal sealed partial class LiveOwnerRecord
{
    public const string FileName = "live-owner.json";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public LiveOwnerRecord(string appDataDirectory, TimeProvider timeProvider, ILogger logger)
        : this(timeProvider, logger, PathUnder(appDataDirectory))
    {
    }

    private LiveOwnerRecord(TimeProvider timeProvider, ILogger logger, string path)
    {
        Path = path;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The record at a path already resolved by the caller.</summary>
    public static LiveOwnerRecord At(string path, TimeProvider timeProvider, ILogger logger) => new(timeProvider, logger, path);

    public string Path { get; }

    public static string PathUnder(string appDataDirectory) =>
        System.IO.Path.Combine(System.IO.Path.GetFullPath(appDataDirectory), "state", FileName);

    /// <summary>
    /// Records <paramref name="owner"/> as the owner of the pair with
    /// <paramref name="fingerprint"/>. The account block is kept beside it so a
    /// side with no profile folder of its own (the follower) can restore the
    /// state file from the record alone. A null block keeps the one already
    /// recorded for the same owner, which is what a re-bind after a rotation wants.
    /// </summary>
    public async Task WriteAsync(RefreshTokenFingerprint fingerprint, AccountEmail owner, OAuthAccountBlock? account, CancellationToken cancellationToken)
    {
        JsonObject? kept = account?.Raw;
        if (kept is null && await ReadAsync(cancellationToken) is { } existing && existing.Email == owner)
        {
            kept = existing.Account?.Raw;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        JsonObject record = new()
        {
            ["fingerprint"] = fingerprint.Sha256Hex,
            ["email"] = owner.Value,
            ["at"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
        };
        if (kept is not null)
        {
            record["account"] = kept.DeepClone();
        }

        await AtomicJsonFile.WriteAsync(Path, record, cancellationToken);
    }

    public Task DeleteAsync(CancellationToken cancellationToken) =>
        AtomicBytesFile.DeleteWithRetryAsync(Path, cancellationToken);

    /// <summary>
    /// The live pair's recorded owner. The CLI rotates the refresh token on its
    /// first refresh after every unpark, so a record whose fingerprint no longer
    /// matches is the normal case within seconds of a switch: while the state
    /// file still names the recorded owner the record is re-bound to the new
    /// fingerprint. When the state file names another account, a block the CLI
    /// stamped after the record was written means a login replaced the pair and
    /// the record is released; an older block means a session wrote a stale
    /// identity back, and the recorded owner stands. Every transition is logged;
    /// the guard never lapses silently.
    /// </summary>
    public async Task<LiveOwner?> ResolveAsync(RefreshTokenFingerprint? liveFingerprint, OAuthAccountBlock? named, CancellationToken cancellationToken)
    {
        if (liveFingerprint is not RefreshTokenFingerprint live || await ReadAsync(cancellationToken) is not { } record)
        {
            return null;
        }

        if (record.Fingerprint == live)
        {
            return new LiveOwner(record.Email, record.Account, FingerprintMatched: true);
        }

        if (named?.Email == record.Email)
        {
            await WriteAsync(live, record.Email, named, cancellationToken);
            LogOwnerRebound(record.Email.Value, live.Sha256Hex[..12]);
            return new LiveOwner(record.Email, named, FingerprintMatched: true);
        }

        if (named?.ProfileFetchedAt is DateTimeOffset fetchedAt && record.At is DateTimeOffset writtenAt && fetchedAt > writtenAt)
        {
            LogOwnerReleased(record.Email.Value, named.Email?.Value ?? "(none)");
            await DeleteAsync(cancellationToken);
            return null;
        }

        LogOwnerStale(record.Email.Value, named?.Email?.Value ?? "(none)");
        return new LiveOwner(record.Email, record.Account, FingerprintMatched: false);
    }

    /// <summary>
    /// Whether the record names <paramref name="owner"/> for the live pair, by
    /// the rule <see cref="ResolveAsync"/> applies and without re-binding or
    /// releasing anything: the record's fingerprint is the live one, or the
    /// state file still names the recorded owner after the CLI rotated it.
    /// </summary>
    public async Task<bool> NamesOwnerAsync(AccountEmail owner, RefreshTokenFingerprint liveFingerprint, OAuthAccountBlock? named, CancellationToken cancellationToken) =>
        await ReadAsync(cancellationToken) is { } record
        && record.Email == owner
        && (record.Fingerprint == liveFingerprint || named?.Email == owner);

    private async Task<Recorded?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        JsonObject? record;
        try
        {
            record = JsonNode.Parse(await SharedFileReader.ReadAllBytesAsync(Path, cancellationToken)) as JsonObject;
        }
        catch (JsonException)
        {
            // A corrupt record is no recorded owner; the next switch writes a fresh one.
            return null;
        }

        string? fingerprint = Text(record, "fingerprint");
        string? email = Text(record, "email");
        if (fingerprint is null || email is null)
        {
            return null;
        }

        DateTimeOffset? at = Text(record, "at") is string stamp
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;
        OAuthAccountBlock? account = record?["account"] is JsonObject block ? OAuthAccountBlock.FromJson((JsonObject)block.DeepClone()) : null;
        return new Recorded(new RefreshTokenFingerprint(fingerprint), new AccountEmail(email), at, account?.Email == new AccountEmail(email) ? account : null);
    }

    private static string? Text(JsonObject? record, string name) =>
        record?[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private sealed record Recorded(RefreshTokenFingerprint Fingerprint, AccountEmail Email, DateTimeOffset? At, OAuthAccountBlock? Account);

    [LoggerMessage(Level = LogLevel.Information, Message = "live owner record re-bound to the rotated pair {Fingerprint} for {Owner}")]
    private partial void LogOwnerRebound(string owner, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "live owner record for {Owner} released: the CLI stamped a login as {Named} after it was written")]
    private partial void LogOwnerReleased(string owner, string named);

    [LoggerMessage(Level = LogLevel.Warning, Message = "live owner record for {Owner} disagrees with the state file, which names {Named} from before the record")]
    private partial void LogOwnerStale(string owner, string named);
}

/// <summary>
/// The recorded owner of the live pair. <paramref name="Account"/> is the block
/// recorded with it, when one was. <paramref name="FingerprintMatched"/> is
/// false when the record stands only because the state file's block is older
/// than it: the live pair has rotated since and the name beside it is stale.
/// </summary>
internal sealed record LiveOwner(AccountEmail Email, OAuthAccountBlock? Account, bool FingerprintMatched);
