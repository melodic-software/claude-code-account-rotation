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
/// <b>The journal is a hint and the fingerprints are the evidence.</b> Every
/// row is decided by reading the live, staging, claimed and export files; the
/// journal only says where to look first. The row that makes this matter is a
/// journal reading <see cref="ImportStep.Exported"/> over a live file that
/// already holds the incoming pair: F5 landed and the process died before the
/// journal write. Unwinding there would delete an export whose lineage is no
/// longer live anywhere else, so that torn swap is continued, not reversed.
/// </para>
/// <para>
/// The CLI may have rotated the live pair while the follower was down — the
/// stale lock is stolen after 60 s — so every comparison allows for a
/// fingerprint having moved on: what is asserted is which <i>lineage</i> is
/// live, judged by the account the live pair's owner record names, not by
/// fingerprint equality alone.
/// </para>
/// </summary>
internal sealed class ImportReconciler
{
    private readonly SwitchOptions _options;

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
    }

    public Task<ImportReconciliation> ReconcileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ImportReconciliation(false, null, "not implemented: " + _options.AppDataDirectory));
    }
}
