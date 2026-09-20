namespace ClaudeCodeAccountRotation.Core.Configuration;

/// <summary>
/// Which half of a machine's rotation this process is. A machine runs one
/// leader and, once the WSL side is in use, one follower; the roles differ in
/// what they own, not in what they are allowed to read.
/// </summary>
public enum RotationRole
{
    /// <summary>
    /// The Windows side: it owns the account store and is its only writer of
    /// slots, serves the page, runs logins and the refresh pass, and drives the
    /// follower's imports.
    /// </summary>
    Leader,

    /// <summary>
    /// The WSL side: no store, no roster, no logins, no refresh pass. It holds
    /// one live pair in its own live directory and takes another across the
    /// volume boundary when the leader hands it one.
    /// </summary>
    Follower,
}
