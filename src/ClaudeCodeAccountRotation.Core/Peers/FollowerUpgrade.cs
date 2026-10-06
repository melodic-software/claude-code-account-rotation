namespace ClaudeCodeAccountRotation.Core.Peers;

/// <summary>
/// The two values the leader and the follower agree on so a release can land on
/// either side first. The leader passes its own version to the follower it
/// starts in <see cref="LeaderVersionVariable"/>. A follower that sees a build
/// of exactly that version installed over its own binary stops, when nothing is
/// in flight, with <see cref="ExitCode"/>, and the leader starts it again from
/// the new binary. Any other exit is left alone.
/// </summary>
public static class FollowerUpgrade
{
    /// <summary>EX_TEMPFAIL: stopped to be started again on the installed build.</summary>
    public const int ExitCode = 75;

    /// <summary>The leader's version, as <c>--version</c> prints it.</summary>
    public const string LeaderVersionVariable = "CLAUDE_CODE_ACCOUNT_ROTATION_LEADER_VERSION";
}

/// <summary>The exit code of a side's process, raised when the child the host started ends.</summary>
public sealed class PeerExitedEventArgs(int exitCode) : EventArgs
{
    public int ExitCode { get; } = exitCode;
}
