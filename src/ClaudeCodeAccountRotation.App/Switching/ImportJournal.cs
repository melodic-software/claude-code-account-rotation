using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// How far a follower's staged import got before the journal was last written.
/// <para>
/// The journal is a hint and the fingerprints on disk are the evidence. A
/// crash between a disk mutation and the journal write leaves the journal one
/// step behind what the files say, which is why
/// <see cref="ImportReconciler"/> decides every row by reading the live,
/// staging, claimed and export files rather than by trusting this value.
/// </para>
/// </summary>
internal enum ImportStep
{
    /// <summary>F2: the refresh lock is held and the outgoing pair has been read. Nothing has moved.</summary>
    Planned,

    /// <summary>F3: the incoming pair is staged beside the live file and verified by fingerprint.</summary>
    Staged,

    /// <summary>F4: the outgoing pair is copied to the mailbox and verified. The follower stops here until a commit arrives.</summary>
    Exported,

    /// <summary>F5: the staging file has replaced the live file. The outgoing pair's last local copy is gone.</summary>
    Swapped,

    /// <summary>F6: the claimed file in the mailbox has been deleted.</summary>
    Released,

    /// <summary>F7: the state file's account block and the live owner record name the incoming account.</summary>
    Patched,
}

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
internal sealed record ImportJournalEntry(
    AccountEmail Incoming,
    RefreshTokenFingerprint IncomingFingerprint,
    string ClaimedPath,
    string ExportPath,
    AccountEmail? Outgoing,
    RefreshTokenFingerprint? OutgoingFingerprint,
    JsonObject IncomingAccount,
    JsonObject? OutgoingAccount,
    ImportStep StepReached,
    DateTimeOffset StartedAt);

/// <summary>
/// What the last completed import handed back, kept so a re-issued request
/// (the leader's L3a is idempotent by F1) is answered from the record rather
/// than performed again.
/// </summary>
internal sealed record LastImportEntry(
    AccountEmail Incoming,
    RefreshTokenFingerprint IncomingFingerprint,
    AccountEmail? Outgoing,
    RefreshTokenFingerprint? OutgoingFingerprint,
    JsonObject? OutgoingAccount,
    DateTimeOffset CompletedAt);

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
        string Incoming,
        string IncomingFingerprint,
        string ClaimedPath,
        string ExportPath,
        string? Outgoing,
        string? OutgoingFingerprint,
        JsonObject? IncomingAccount,
        JsonObject? OutgoingAccount,
        ImportStep StepReached,
        DateTimeOffset StartedAt)
    {
        public static JournalDocument From(ImportJournalEntry entry) => new(
            entry.Incoming.Value,
            entry.IncomingFingerprint.Sha256Hex,
            entry.ClaimedPath,
            entry.ExportPath,
            entry.Outgoing?.Value,
            entry.OutgoingFingerprint?.Sha256Hex,
            entry.IncomingAccount.DeepClone().AsObject(),
            entry.OutgoingAccount?.DeepClone().AsObject(),
            entry.StepReached,
            entry.StartedAt);

        public ImportJournalEntry ToEntry() => new(
            new AccountEmail(Incoming),
            new RefreshTokenFingerprint(IncomingFingerprint),
            ClaimedPath,
            ExportPath,
            Outgoing is null ? null : new AccountEmail(Outgoing),
            OutgoingFingerprint is null ? null : new RefreshTokenFingerprint(OutgoingFingerprint),
            IncomingAccount ?? [],
            OutgoingAccount,
            StepReached,
            StartedAt);
    }

    private sealed record LastImportDocument(
        string Incoming,
        string IncomingFingerprint,
        string? Outgoing,
        string? OutgoingFingerprint,
        JsonObject? OutgoingAccount,
        DateTimeOffset CompletedAt)
    {
        public static LastImportDocument From(LastImportEntry entry) => new(
            entry.Incoming.Value,
            entry.IncomingFingerprint.Sha256Hex,
            entry.Outgoing?.Value,
            entry.OutgoingFingerprint?.Sha256Hex,
            entry.OutgoingAccount?.DeepClone().AsObject(),
            entry.CompletedAt);

        public LastImportEntry ToEntry() => new(
            new AccountEmail(Incoming),
            new RefreshTokenFingerprint(IncomingFingerprint),
            Outgoing is null ? null : new AccountEmail(Outgoing),
            OutgoingFingerprint is null ? null : new RefreshTokenFingerprint(OutgoingFingerprint),
            OutgoingAccount,
            CompletedAt);
    }
}
