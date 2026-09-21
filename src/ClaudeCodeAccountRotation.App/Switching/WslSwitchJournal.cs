using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// The leader's intent for a switch of the other side, written before the
/// claim: which pair is arriving on that side, which is leaving it, and the
/// step reached. Fingerprints, never tokens.
/// <para>
/// The two mailbox paths are not stored, because they are derived from the
/// folder names and the side by the same code that created them; storing them
/// would be a second statement of the same fact that a rename could leave
/// stale.
/// </para>
/// <para>
/// <paramref name="OutgoingAccount"/> is written at the <b>claim</b>, from the
/// other side's dashboard, and refreshed from the commit's answer. It has to
/// be there from the first write for the same reason the follower journals
/// both blocks: a leader that dies between the commit and its own journal
/// write can never learn it again, because by then the other side's state file
/// names the incoming account. Without it the park would put the outgoing pair
/// into a folder with no identity — a slot <c>ProfileFolderStore</c> skips, so
/// no card and a later switch refused — which is the whole failure for an
/// account that has only ever lived on the other side.
/// </para>
/// </summary>
internal sealed record WslSwitchJournalEntry(
    SideName Side,
    AccountEmail Incoming,
    RefreshTokenFingerprint IncomingFingerprint,
    string IncomingFolderPath,
    AccountEmail? Outgoing,
    RefreshTokenFingerprint? OutgoingFingerprint,
    string? OutgoingFolderPath,
    WslSwitchStep StepReached,
    DateTimeOffset StartedAt,
    JsonObject? OutgoingAccount = null);

/// <summary>
/// <c>&lt;appdata&gt;/state/wsl-switch-journal.json</c>: present only while a
/// hand-off to the other side is in flight. The same shape as
/// <see cref="SwitchJournal"/>, and deliberately a second file rather than a
/// second step in that one: a Windows switch and a WSL switch never run at
/// once (both take the leader's mutation gate), but their crash tables are
/// different and reading one journal with two vocabularies in it would put
/// the two tables in one method.
/// </summary>
internal sealed class WslSwitchJournal
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public WslSwitchJournal(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        _path = Path.Combine(Path.GetFullPath(appDataDirectory), "state", "wsl-switch-journal.json");
    }

    public async Task WriteAsync(WslSwitchJournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(JournalDocument.From(entry), _serializerOptions);
        await AtomicBytesFile.WriteAsync(_path, bytes, cancellationToken);
    }

    public async Task<WslSwitchJournalEntry?> ReadOpenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[] bytes = await SharedFileReader.ReadAllBytesAsync(_path, cancellationToken);
        return JsonSerializer.Deserialize<JournalDocument>(bytes, _serializerOptions)?.ToEntry();
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
        {
            await AtomicBytesFile.DeleteWithRetryAsync(_path, cancellationToken);
        }
    }

    /// <summary>The on-disk shape: primitives only, so the value types need no converters.</summary>
    private sealed record JournalDocument(
        string Side,
        string Incoming,
        string IncomingFingerprint,
        string IncomingFolderPath,
        string? Outgoing,
        string? OutgoingFingerprint,
        string? OutgoingFolderPath,
        WslSwitchStep StepReached,
        DateTimeOffset StartedAt,
        JsonObject? OutgoingAccount)
    {
        public static JournalDocument From(WslSwitchJournalEntry entry) => new(
            entry.Side.Value,
            entry.Incoming.Value,
            entry.IncomingFingerprint.Sha256Hex,
            entry.IncomingFolderPath,
            entry.Outgoing?.Value,
            entry.OutgoingFingerprint?.Sha256Hex,
            entry.OutgoingFolderPath,
            entry.StepReached,
            entry.StartedAt,
            entry.OutgoingAccount?.DeepClone().AsObject());

        public WslSwitchJournalEntry ToEntry() => new(
            new SideName(Side),
            new AccountEmail(Incoming),
            new RefreshTokenFingerprint(IncomingFingerprint),
            IncomingFolderPath,
            Outgoing is null ? null : new AccountEmail(Outgoing),
            OutgoingFingerprint is null ? null : new RefreshTokenFingerprint(OutgoingFingerprint),
            OutgoingFolderPath,
            StepReached,
            StartedAt,
            OutgoingAccount);
    }
}
