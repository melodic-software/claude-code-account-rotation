using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.Core.Configuration;

/// <summary>
/// How the leader starts the follower: <c>wsl.exe -d &lt;distribution&gt; -u
/// &lt;user&gt; --exec &lt;executablePath&gt; --port &lt;port&gt;</c>. Absent when
/// the operator runs the follower themselves; a side with no launch can still
/// be talked to, it just cannot be started from the page.
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
/// </summary>
public sealed record PeerConfiguration(
    SideName Side,
    Uri BaseAddress,
    string StorePathFromPeer,
    PeerLaunch? Launch = null);
