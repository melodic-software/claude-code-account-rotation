using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// What the leader asks the follower to take. No token crosses the wire:
/// identity is the SHA-256 fingerprint and the pair itself is read from
/// <see cref="ClaimedPath"/>, a file on the store's own volume that the leader
/// renamed into its mailbox before asking.
/// </summary>
internal sealed record ImportRequest(
    AccountEmail Email,
    string ClaimedPath,
    RefreshTokenFingerprint Fingerprint,
    JsonObject Account,
    string ExportPath);

/// <summary>
/// The answer to <c>POST /api/import</c>: the import has run F1 to F4 and
/// stopped. <see cref="ExportedFingerprint"/> is the outgoing pair the leader
/// must now read natively and verify before it may commit, or null when this
/// side held nothing and there is nothing to verify.
/// </summary>
internal sealed record ImportAnswer(
    RefreshTokenFingerprint? ExportedFingerprint,
    AccountEmail? Outgoing,
    bool AlreadyImported,
    ImportResult? Result);

/// <summary>What the commit hands back: which account left this side, and its block for the leader's park.</summary>
internal sealed record ImportResult(
    AccountEmail? Outgoing,
    RefreshTokenFingerprint? OutgoingFingerprint,
    JsonObject? OutgoingAccount,
    bool AlreadyImported);

/// <summary>
/// <c>GET /api/import-status</c>: what the follower believes about the import
/// the leader is asking after, decided from the files and not only the journal.
/// </summary>
internal sealed record ImportStatus(
    bool Imported,
    ImportStep? JournalStep,
    RefreshTokenFingerprint? LiveFingerprint,
    string Detail);

/// <summary>
/// The follower's staged import, held across two calls.
/// <para>
/// <c>POST /api/import</c> runs F1 to F4 and <b>stops</b>: the incoming pair is
/// staged and the outgoing pair is exported, but the live file is untouched and
/// the answer is <c>Exported {fa}</c> (or <c>Exported {none}</c>). Only
/// <c>POST /api/import/commit</c> runs F5 to F8, and this class refuses it
/// unless its own journal reads <see cref="ImportStep.Exported"/>. That refusal
/// lives here, in the follower, rather than in the caller, so a leader that
/// forgets to gate cannot skip the one check that stands between a host power
/// loss and a lost credential lineage.
/// </para>
/// <para>
/// The follower's mutation gate and the WSL live directory's
/// <c>.oauth_refresh.lock</c> are both held from F2 until the import ends. That
/// hold outlives the lock's own 60 s stale threshold, so three rules keep it
/// honest: a commit later than <see cref="CommitBudget"/> is refused and the
/// import self-aborts; the live file is re-read immediately before F5 and a
/// rotation aborts instead of swapping; and the lock directory's mtime is
/// re-stamped every <see cref="HeartbeatInterval"/> while it is held.
/// </para>
/// </summary>
internal sealed class FollowerImport
{
    private readonly SwitchOptions _options;

    public FollowerImport(
        SwitchOptions options,
        StagedImportCredentialPairStore pairs,
        ImportJournal journal,
        ImportReconciler reconciler,
        ClaudeStateFile stateFile,
        CredentialMutationGate gate,
        TimeProvider timeProvider,
        ILogger<FollowerImport> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(stateFile);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
    }

    /// <summary>The window between the <c>Exported</c> answer and the commit. A later commit is refused.</summary>
    public TimeSpan CommitBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long the hold may sit idle before the follower unwinds it and releases the lock.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How often the lock directory's mtime is re-stamped while the hold lasts.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>F1 to F4: idempotency, the lock, the stage, the export, and stop.</summary>
    public Task<Result<ImportAnswer, string>> ImportAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<ImportAnswer, string>.Failure("not implemented: " + _options.LiveConfigDirectory));
    }

    /// <summary>F5 to F8, and only from <see cref="ImportStep.Exported"/>.</summary>
    public Task<Result<ImportResult, string>> CommitAsync(AccountEmail email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<ImportResult, string>.Failure("not implemented: " + email.Value + " " + _options.LiveConfigDirectory));
    }

    /// <summary>Unwind from <see cref="ImportStep.Exported"/>: delete the export and the staging file, leave the live pair.</summary>
    public Task<Result<Unit, string>> AbortAsync(AccountEmail email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<Unit, string>.Failure("not implemented: " + email.Value + " " + _options.LiveConfigDirectory));
    }

    public Task<ImportStatus> StatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ImportStatus(false, null, null, "not implemented: " + _options.LiveConfigDirectory));
    }
}
