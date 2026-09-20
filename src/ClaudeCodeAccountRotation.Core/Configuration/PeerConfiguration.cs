using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.Core.Configuration;

/// <summary>
/// How the leader starts the follower: <c>wsl.exe -d &lt;distribution&gt;
/// -u &lt;user&gt; --exec &lt;executablePath&gt; --config ...</c>. Absent when the
/// operator runs the follower themselves, which is what the acceptance does;
/// a side with no launch can still be talked to, it just cannot be started
/// from the page.
/// </summary>
public sealed record PeerLaunch(string Distribution, string User, string ExecutablePath, int Port);

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
