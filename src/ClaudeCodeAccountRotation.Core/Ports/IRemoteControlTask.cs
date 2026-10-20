namespace ClaudeCodeAccountRotation.Core.Ports;

/// <summary>
/// The scheduled task that runs <c>claude remote-control</c> on this machine. A
/// Remote Control session belongs to the account signed in when it started, and
/// Claude Code stops it once that account changes, so a clicked switch restarts
/// the task under the incoming account. Out of process, so a port.
/// </summary>
public interface IRemoteControlTask
{
    /// <summary>The task's name, or null when no task is configured.</summary>
    string? Name { get; }

    /// <summary>Whether the task is running now. False when it cannot be read.</summary>
    Task<bool> IsRunningAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ends the task, waits until it is no longer running, and runs it again.
    /// A failure carries the reason; the caller decides what to tell the operator.
    /// </summary>
    Task<Result<Unit, string>> RestartAsync(CancellationToken cancellationToken);
}
