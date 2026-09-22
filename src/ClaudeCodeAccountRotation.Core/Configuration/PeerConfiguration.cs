using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.Core.Configuration;

/// <summary>
/// How the leader starts the follower: <c>wsl.exe -d &lt;distribution&gt; -u
/// &lt;user&gt;</c>, then the exec flag, the wrapper, the follower flag,
/// <paramref name="ExecutablePath"/>, the port flag, <paramref name="Port"/>,
/// and optionally the config flag and <paramref name="ConfigPath"/>.
/// The wrapper is the sibling <c>claude-code-account-rotation-follower-log</c>
/// of <paramref name="ExecutablePath"/>. The exec flag runs the wrapper;
/// <paramref name="ExecutablePath"/> is passed through as the follower flag so
/// the process that starts is the binary this record names.
/// This record is absent when the operator runs the follower themselves.
/// A side with no launch can still be talked to at
/// <see cref="PeerConfiguration.BaseAddress"/>, and it cannot be started from
/// the page. That peer entry still names <see cref="PeerConfiguration.Distribution"/>
/// and <see cref="PeerConfiguration.User"/> so the leader can read
/// <c>instance.url</c> as that user.
/// <para>
/// <paramref name="ConfigPath"/> is optional and is the follower's own
/// configuration file, spelled in its namespace. A follower installed where
/// design section 13 puts it finds that file for itself under its platform
/// app data directory and needs none; one running over temp roots does, and
/// without this key the leader would start it with no configuration, which a
/// first run answers by writing a default one — a <i>leader</i> — into the
/// distro.
/// </para>
/// </summary>
public sealed record PeerLaunch(string Distribution, string User, string ExecutablePath, int Port, string? ConfigPath = null);

/// <summary>
/// One entry of <c>peers[]</c>: the other side of this machine.
/// <para>
/// <see cref="StorePathFromPeer"/> is how the leader spells its own store in
/// the peer's namespace, because the two paths in an <c>ImportRequest</c> have
/// to be openable by the process that receives them. Dotfiles derive it with
/// <c>wslpath</c> at apply time, so no path is ever typed.
/// </para>
/// <para>
/// <see cref="Launch"/> is what starts the follower from the page. When it is
/// absent the operator started the follower, and the side is still reachable
/// at <see cref="BaseAddress"/>. A manually started follower still needs
/// <see cref="Distribution"/> and <see cref="User"/> on this entry so the
/// leader can read <c>instance.url</c> as that user. <see cref="ConfigPath"/>
/// is that follower's configuration file, spelled in the follower's namespace,
/// the same role as <see cref="PeerLaunch.ConfigPath"/>. When
/// <see cref="Launch"/> is present, its distribution, user, and configuration
/// path are the ones that count.
/// </para>
/// </summary>
public sealed record PeerConfiguration(
    SideName Side,
    Uri BaseAddress,
    string StorePathFromPeer,
    PeerLaunch? Launch = null,
    string? Distribution = null,
    string? User = null,
    string? ConfigPath = null)
{
    /// <summary>
    /// Distribution, user, and configuration path used to read the follower's
    /// <c>instance.url</c>. <see cref="Launch"/> wins when it is present.
    /// Otherwise <see cref="Distribution"/> and <see cref="User"/>, with
    /// <see cref="ConfigPath"/>. Null when neither supplies both a distribution
    /// and a user: the leader then has no identity to read the token with.
    /// </summary>
    public PeerFollowerIdentity? FollowerIdentity
    {
        get
        {
            if (Launch is PeerLaunch launch)
            {
                return new PeerFollowerIdentity(launch.Distribution, launch.User, launch.ConfigPath);
            }

            if (string.IsNullOrWhiteSpace(Distribution) || string.IsNullOrWhiteSpace(User))
            {
                return null;
            }

            return new PeerFollowerIdentity(Distribution, User, ConfigPath);
        }
    }
}

/// <summary>
/// Distribution, user, and optional configuration path the leader uses to
/// read one follower's <c>instance.url</c>.
/// </summary>
public readonly record struct PeerFollowerIdentity(string Distribution, string User, string? ConfigPath);
