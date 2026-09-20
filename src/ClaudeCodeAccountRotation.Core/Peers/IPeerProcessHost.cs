using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.Core.Peers;

/// <summary>
/// Starts and supervises the follower, which is the leader's child (design
/// decision 3). A side with no host configured can still be talked to when it
/// is already running; it simply cannot be started from the page.
/// </summary>
public interface IPeerProcessHost
{
    SideName Side { get; }

    /// <summary>True while this host's child is alive. False says nothing about whether the side is reachable.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the child if it is not already running. A start that fails answers why.</summary>
    Task<Result<Unit, string>> StartAsync(CancellationToken cancellationToken);
}
