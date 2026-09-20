using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>What a reconciliation pass found and did, in one line the next caller can report.</summary>
internal sealed record ImportReconciliation(bool Imported, AccountEmail? Outgoing, string Outcome);

/// <summary>
/// The follower's crash table (design section 9.3), run at start under the
/// gate before any request is served.
/// <para>
/// <b>The journal is a hint and the fingerprints are the evidence.</b> The row
/// that makes this matter is a journal reading <see cref="ImportStep.Exported"/>
/// over a live directory whose staging file is gone: F5 renamed it over the
/// live pair and the process died before the journal write. Unwinding there
/// would delete an export whose lineage is no longer live anywhere else, which
/// is the one way this design could lose a pair rather than strand one.
/// </para>
/// <para>
/// The <b>absence of the staging file</b> is what decides that row, not a
/// fingerprint comparison. F5 is a rename, so the staging file exists exactly
/// when the swap has not happened; a fingerprint test would have to assume the
/// live pair still reads as the one that was staged, and the CLI may have
/// rotated it while the follower was down — the stale lock is stolen after
/// 60 s. The rename is the fact; the fingerprints confirm it.
/// </para>
/// </summary>
internal sealed partial class ImportReconciler
{
    private readonly SwitchOptions _options;
    private readonly StagedImportCredentialPairStore _pairs;
    private readonly ImportJournal _journal;
    private readonly ClaudeStateFile _stateFile;
    private readonly string _liveOwnerPath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImportReconciler> _logger;

    public ImportReconciler(
        SwitchOptions options,
        StagedImportCredentialPairStore pairs,
        ImportJournal journal,
        ClaudeStateFile stateFile,
        TimeProvider timeProvider,
        ILogger<ImportReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(stateFile);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _pairs = pairs;
        _journal = journal;
        _stateFile = stateFile;
        _liveOwnerPath = Path.Combine(Path.GetFullPath(options.AppDataDirectory), "state", "live-owner.json");
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Whether F5 has already run, for a journal entry and the files beside it.
    /// The single derivation: the reconciler, the status route and every unwind
    /// path ask this one question, so no caller can decide it differently and
    /// delete an export whose lineage is no longer live.
    /// </summary>
    internal static async Task<bool> SwapHasHappenedAsync(
        ImportJournalEntry entry,
        StagedImportCredentialPairStore pairs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(pairs);
        if (entry.StepReached is ImportStep.Planned or ImportStep.Staged)
        {
            return false;
        }

        if (entry.StepReached != ImportStep.Exported)
        {
            return true;
        }

        // The torn F5, and the only row this file has to infer rather than read.
        // Two facts together, because neither alone is enough. The staging file is
        // gone, since F5 is a rename and nothing else moves it — but an unwind
        // deletes it too, so its absence by itself cannot tell a swap from a
        // cleanup. And the live pair is no longer the one this entry recorded as
        // outgoing, which a cleanup never changes and a swap always does. A
        // rotation after the swap moves the live fingerprint again and still
        // satisfies the second fact, which is the case the fingerprint of the
        // incoming pair alone would miss.
        if (File.Exists(pairs.StagingPath))
        {
            return false;
        }

        CredentialPair? live = await pairs.ReadLiveAsync(cancellationToken);
        return live is not null && live.Fingerprint != entry.OutgoingFingerprint;
    }

    public async Task<ImportReconciliation> ReconcileAsync(CancellationToken cancellationToken)
    {
        ImportJournalEntry? entry = await _journal.ReadOpenAsync(cancellationToken);
        if (entry is null)
        {
            return new ImportReconciliation(false, null, "no import journal; nothing was in flight");
        }

        if (!await SwapHasHappenedAsync(entry, _pairs, cancellationToken))
        {
            return await UnwindAsync(entry, cancellationToken);
        }

        ImportStep from = entry.StepReached == ImportStep.Exported ? ImportStep.Swapped : entry.StepReached;
        await FollowerImport.FinishAsync(
            entry,
            _journal,
            _stateFile,
            _pairs,
            _liveOwnerPath,
            // A reconciliation never injects a crash: the hook belongs to the pass
            // that is being crashed, and a second kill here would make the table
            // untestable.
            failAfterStep: null,
            _timeProvider,
            from,
            cancellationToken);
        LogContinued(entry.Incoming.Value, entry.StepReached);
        return new ImportReconciliation(true, entry.Outgoing, "the swap had already happened at " + entry.StepReached + "; F6 to F8 were finished");
    }

    /// <summary>
    /// Before the swap, nothing has changed for a session, so nothing is
    /// completed on its behalf: the two files this import created go and the
    /// leader unclaims. The live pair is never touched.
    /// </summary>
    private async Task<ImportReconciliation> UnwindAsync(ImportJournalEntry entry, CancellationToken cancellationToken)
    {
        _pairs.DeleteStaging();
        StagedImportCredentialPairStore.DeleteIfPresent(entry.ExportPath);
        await _journal.ClearAsync(cancellationToken);
        LogUnwound(entry.Incoming.Value, entry.StepReached);
        return new ImportReconciliation(false, null, "the import of " + entry.Incoming.Value + " stopped at " + entry.StepReached + " before the swap and was unwound");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "the import of {Incoming} had passed the swap at {Step}; finishing it")]
    private partial void LogContinued(string incoming, ImportStep step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the import of {Incoming} was unwound from {Step}; the live pair was not touched and the leader unclaims")]
    private partial void LogUnwound(string incoming, ImportStep step);
}
