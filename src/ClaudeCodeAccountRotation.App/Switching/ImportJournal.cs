using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// The intent written before the first move: which pair is arriving, which is
/// leaving, where each file sits, and the step reached. Fingerprints, never
/// tokens.
/// <para>
/// Both account blocks ride along because reconciliation has to be able to
/// finish F7 and F8 after a crash, and neither block can be recovered then:
/// the incoming one arrived in a request that is over, and the outgoing one
/// has already been overwritten in the state file by the time the crash table
/// needs it.
/// </para>
/// </summary>
/// <param name="Incoming">
/// The account arriving here, or null for a release, which takes nothing in.
/// The incoming fingerprint, claimed path and account block are absent with it:
/// one half of the entry describes a pair that does not exist.
/// </param>
internal sealed record ImportJournalEntry(
    AccountEmail? Incoming,
    RefreshTokenFingerprint? IncomingFingerprint,
    string? ClaimedPath,
    string ExportPath,
    AccountEmail? Outgoing,
    RefreshTokenFingerprint? OutgoingFingerprint,
    JsonObject? IncomingAccount,
    JsonObject? OutgoingAccount,
    ImportStep StepReached,
    DateTimeOffset StartedAt)
{
    /// <summary>Whether nothing arrives here: the park-back of this side's live pair.</summary>
    public bool IsRelease => Incoming is null;

    /// <summary>
    /// The account this transaction is keyed on, which is what a commit, an
    /// abort and a status read name. An import names the one arriving; a
    /// release has none and names the one leaving, which is the only account
    /// it moves.
    /// </summary>
    public AccountEmail Subject => (Incoming ?? Outgoing)!.Value;
}

/// <summary>
/// What the last completed transaction handed back, kept so a re-issued request
/// (the leader's L3a is idempotent by F1) is answered from the record rather
/// than performed again.
/// <para>
/// It records the <b>direction</b> as well as the account, and that is not
/// decoration: a release of X and a later switch of X back to this side name
/// the same account, and a record that could not tell them apart would answer
/// the switch "already imported" from the release's own row, handing the leader
/// an outgoing account its journal never planned to park.
/// </para>
/// </summary>
internal sealed record LastImportEntry(
    AccountEmail? Incoming,
    RefreshTokenFingerprint? IncomingFingerprint,
    AccountEmail? Outgoing,
    RefreshTokenFingerprint? OutgoingFingerprint,
    JsonObject? OutgoingAccount,
    DateTimeOffset CompletedAt)
{
    /// <summary>Whether the transaction this records was a release.</summary>
    public bool IsRelease => Incoming is null;

    /// <summary>The account it was keyed on, the same key the request carries.</summary>
    public AccountEmail Subject => (Incoming ?? Outgoing)!.Value;
}

/// <summary>
/// <c>&lt;appdata&gt;/state/import-journal.json</c>, present only while an
/// import is in flight, and <c>&lt;appdata&gt;/state/last-import.json</c>,
/// which outlives it. The same shape as <see cref="SwitchJournal"/>: a JSON
/// document of primitives written through <see cref="AtomicBytesFile"/>, read
/// at start by the reconciler.
/// </summary>
internal sealed class ImportJournal
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _journalPath;
    private readonly string _lastImportPath;

    public ImportJournal(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        string state = Path.Combine(Path.GetFullPath(appDataDirectory), "state");
        _journalPath = Path.Combine(state, "import-journal.json");
        _lastImportPath = Path.Combine(state, "last-import.json");
    }

    public async Task WriteAsync(ImportJournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(JournalDocument.From(entry), _serializerOptions);
        await AtomicBytesFile.WriteAsync(_journalPath, bytes, cancellationToken);
    }

    public async Task<ImportJournalEntry?> ReadOpenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
        {
            return null;
        }

        byte[] bytes = await SharedFileReader.ReadAllBytesAsync(_journalPath, cancellationToken);
        return JsonSerializer.Deserialize<JournalDocument>(bytes, _serializerOptions)?.ToEntry();
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_journalPath))
        {
            await AtomicBytesFile.DeleteWithRetryAsync(_journalPath, cancellationToken);
        }
    }

    public async Task WriteLastImportAsync(LastImportEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(_lastImportPath)!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(LastImportDocument.From(entry), _serializerOptions);
        await AtomicBytesFile.WriteAsync(_lastImportPath, bytes, cancellationToken);
    }

    /// <summary>
    /// Drops the completed-transaction record. It is called when a <i>new</i>
    /// transaction for the same account begins, because from that moment the
    /// old one can no longer answer for it: a commit or a status read naming
    /// that account means the new transaction, and answering either from the
    /// old row reports work that was never done.
    /// </summary>
    public async Task ClearLastImportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_lastImportPath))
        {
            await AtomicBytesFile.DeleteWithRetryAsync(_lastImportPath, cancellationToken);
        }
    }

    public async Task<LastImportEntry?> ReadLastImportAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_lastImportPath))
        {
            return null;
        }

        byte[] bytes = await SharedFileReader.ReadAllBytesAsync(_lastImportPath, cancellationToken);
        return JsonSerializer.Deserialize<LastImportDocument>(bytes, _serializerOptions)?.ToEntry();
    }

    /// <summary>The on-disk shape: primitives only, so the value types need no converters.</summary>
    private sealed record JournalDocument(
        string? Incoming,
        string? IncomingFingerprint,
        string? ClaimedPath,
        string ExportPath,
        string? Outgoing,
        string? OutgoingFingerprint,
        JsonObject? IncomingAccount,
        JsonObject? OutgoingAccount,
        ImportStep StepReached,
        DateTimeOffset StartedAt)
    {
        public static JournalDocument From(ImportJournalEntry entry) => new(
            entry.Incoming?.Value,
            entry.IncomingFingerprint?.Sha256Hex,
            entry.ClaimedPath,
            entry.ExportPath,
            entry.Outgoing?.Value,
            entry.OutgoingFingerprint?.Sha256Hex,
            entry.IncomingAccount?.DeepClone().AsObject(),
            entry.OutgoingAccount?.DeepClone().AsObject(),
            entry.StepReached,
            entry.StartedAt);

        public ImportJournalEntry ToEntry() => new(
            Incoming is null ? null : new AccountEmail(Incoming),
            IncomingFingerprint is null ? null : new RefreshTokenFingerprint(IncomingFingerprint),
            ClaimedPath,
            ExportPath,
            Outgoing is null ? null : new AccountEmail(Outgoing),
            OutgoingFingerprint is null ? null : new RefreshTokenFingerprint(OutgoingFingerprint),
            IncomingAccount,
            OutgoingAccount,
            StepReached,
            StartedAt);
    }

    private sealed record LastImportDocument(
        string? Incoming,
        string? IncomingFingerprint,
        string? Outgoing,
        string? OutgoingFingerprint,
        JsonObject? OutgoingAccount,
        DateTimeOffset CompletedAt)
    {
        public static LastImportDocument From(LastImportEntry entry) => new(
            entry.Incoming?.Value,
            entry.IncomingFingerprint?.Sha256Hex,
            entry.Outgoing?.Value,
            entry.OutgoingFingerprint?.Sha256Hex,
            entry.OutgoingAccount?.DeepClone().AsObject(),
            entry.CompletedAt);

        public LastImportEntry ToEntry() => new(
            Incoming is null ? null : new AccountEmail(Incoming),
            IncomingFingerprint is null ? null : new RefreshTokenFingerprint(IncomingFingerprint),
            Outgoing is null ? null : new AccountEmail(Outgoing),
            OutgoingFingerprint is null ? null : new RefreshTokenFingerprint(OutgoingFingerprint),
            OutgoingAccount,
            CompletedAt);
    }
}
