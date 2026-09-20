using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>The paths and bounds a switch runs under; bound from the configuration.</summary>
internal sealed record SwitchOptions(
    string LiveConfigDirectory,
    string StateFilePath,
    string ProfilesRoot,
    string AppDataDirectory,
    TimeSpan RefreshLockWaitBound,
    TimeSpan MutationGateTimeout,
    string? Mailbox = null,
    string? FailAfterStep = null);

/// <summary>
/// The crash-injection hook, bound from <c>CCAR_FAIL_AFTER_STEP</c> and never
/// from the configuration file, so it is not a knob the product ships.
/// <para>
/// The value names one <see cref="ImportStep"/> and when to die at it:
/// <c>Swapped</c> kills the process immediately after that step's journal
/// write, and <c>Swapped:before-journal</c> kills it between the disk mutation
/// and the journal write. The second is the torn case that matters — the live
/// file already holds the incoming pair while the journal still reads
/// <c>Exported</c> — and it is exercised rather than merely written down.
/// </para>
/// <para>
/// The kill is <see cref="System.Diagnostics.Process.Kill()"/> of this process:
/// no unwinding, no <c>finally</c>, no flush. A simulated abort would leave the
/// `finally` blocks that release the lock and delete the staging file running,
/// which is exactly the code a real crash skips.
/// </para>
/// </summary>
internal static class CrashInjection
{
    public const string EnvironmentVariableName = "CCAR_FAIL_AFTER_STEP";

    /// <summary>Kills this process when <paramref name="configured"/> names this step and timing.</summary>
    public static void KillIfConfigured(string? configured, ImportStep step, bool beforeJournal)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        string wanted = step.ToString() + (beforeJournal ? ":before-journal" : string.Empty);
        if (!string.Equals(configured.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        self.Kill();
        // Kill is asynchronous on Unix; block so no further step ever runs here.
        self.WaitForExit();
    }
}

/// <summary>What a stale-identity repair pass found.</summary>
internal enum IdentityRepair
{
    /// <summary>The state file names the live pair's recorded owner, or there is no record to judge by.</summary>
    NotNeeded,

    /// <summary>A session had written an older block back; the owner's block was patched in again.</summary>
    Repatched,

    /// <summary>A credential mutation was in progress; nothing was read or written.</summary>
    Busy,

    /// <summary>The state file is stale but the owner's block is not on disk to restore it from.</summary>
    NoProfileBlock,
}

/// <summary>What startup or pre-plan reconciliation found and did.</summary>
internal sealed record ReconciliationReport(
    IReadOnlyList<string> Quarantined,
    string JournalOutcome,
    bool SwitchingBlocked,
    string? Banner);
