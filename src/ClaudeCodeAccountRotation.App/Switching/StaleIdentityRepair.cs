using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>What the state file watcher runs on every change to the state file.</summary>
internal interface IStaleIdentityRepair
{
    Task<IdentityRepair> RepairStaleIdentityAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Restores the state file's <c>oauthAccount</c> block when a running session has
/// written its in-memory block back over the one a switch or an import put there
/// (observed on the real machine within minutes of a switch, on both sides). The
/// owner record decides: a block naming another account and stamped before the
/// record was written is stale, and the owner's block is patched in again; a
/// block stamped after the record is a real login and is adopted instead.
/// <para>
/// One implementation for both sides. They differ only in where the live pair is
/// read from, where the owner's block can be found when the record carries none,
/// and whether an unfinished transaction makes the live pair's owner unknowable.
/// </para>
/// </summary>
internal sealed partial class StaleIdentityRepair
{
    private readonly CredentialMutationGate _gate;
    private readonly ClaudeStateFile _stateFile;
    private readonly LiveOwnerRecord _owner;
    private readonly Func<CancellationToken, Task<RefreshTokenFingerprint?>> _liveFingerprint;
    private readonly Func<AccountEmail, CancellationToken, Task<OAuthAccountBlock?>> _storedBlock;
    private readonly Func<CancellationToken, Task<bool>> _transactionOpen;
    private readonly ILogger _logger;

    /// <param name="liveFingerprint">The live pair's fingerprint, or null when no pair is live.</param>
    /// <param name="storedBlock">The owner's block from wherever this side keeps one, for a record written without it.</param>
    /// <param name="transactionOpen">True while a journal says a switch or import is unfinished; the repair then leaves the files to its reconciliation.</param>
    public StaleIdentityRepair(
        CredentialMutationGate gate,
        ClaudeStateFile stateFile,
        LiveOwnerRecord owner,
        Func<CancellationToken, Task<RefreshTokenFingerprint?>> liveFingerprint,
        Func<AccountEmail, CancellationToken, Task<OAuthAccountBlock?>> storedBlock,
        Func<CancellationToken, Task<bool>>? transactionOpen,
        ILogger logger)
    {
        _gate = gate;
        _stateFile = stateFile;
        _owner = owner;
        _liveFingerprint = liveFingerprint;
        _storedBlock = storedBlock;
        _transactionOpen = transactionOpen ?? (static _ => Task.FromResult(false));
        _logger = logger;
    }

    /// <summary>
    /// Takes the mutation gate for at most <paramref name="gateWait"/>; a gate held
    /// longer is <see cref="IdentityRepair.Busy"/>, because whatever holds it writes
    /// the state file itself and that write wakes the watcher again.
    /// </summary>
    public async Task<IdentityRepair> RepairAsync(TimeSpan gateWait, CancellationToken cancellationToken)
    {
        IDisposable? permit = null;
        try
        {
            try
            {
                permit = await _gate.AcquireAsync(gateWait, cancellationToken);
            }
            catch (TimeoutException)
            {
                return IdentityRepair.Busy;
            }

            return await RepairUnderGateAsync(cancellationToken);
        }
        finally
        {
            permit?.Dispose();
        }
    }

    /// <summary>The repair for a caller that already holds the mutation gate.</summary>
    public async Task<IdentityRepair> RepairUnderGateAsync(CancellationToken cancellationToken)
    {
        if (await _transactionOpen(cancellationToken))
        {
            return IdentityRepair.NotNeeded;
        }

        if (await _liveFingerprint(cancellationToken) is not RefreshTokenFingerprint fingerprint)
        {
            return IdentityRepair.NotNeeded;
        }

        OAuthAccountBlock? named = await _stateFile.ReadAccountBlockAsync(cancellationToken);
        if (await _owner.ResolveAsync(fingerprint, named, cancellationToken) is not LiveOwner owner || named?.Email == owner.Email)
        {
            return IdentityRepair.NotNeeded;
        }

        OAuthAccountBlock? block = owner.Account ?? await _storedBlock(owner.Email, cancellationToken);
        if (block?.Email != owner.Email)
        {
            LogRepairImpossible(owner.Email.Value, named?.Email?.Value ?? "(none)");
            return IdentityRepair.NoProfileBlock;
        }

        await _stateFile.PatchAccountBlockAsync(block!, CancellationToken.None);
        LogRepatched(owner.Email.Value, named?.Email?.Value ?? "(none)");
        return IdentityRepair.Repatched;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "state file re-patched to {Owner}: a session had written back its older block naming {Named}")]
    private partial void LogRepatched(string owner, string named);

    [LoggerMessage(Level = LogLevel.Warning, Message = "state file names {Named} but the live pair belongs to {Owner}, and no account block for {Owner} is recorded or stored to restore it from")]
    private partial void LogRepairImpossible(string owner, string named);
}
