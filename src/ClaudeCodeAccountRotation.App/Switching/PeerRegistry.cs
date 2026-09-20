using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// One configured side of this machine: how to talk to it, how to start it,
/// and how it spells this store in its own namespace.
/// </summary>
internal sealed record Peer(IPeerRotationInstance Instance, IPeerProcessHost? Host, string StorePathFromPeer)
{
    public SideName Side => Instance.Side;

    /// <summary>
    /// A path under this store's mailbox, spelled the way the peer must open
    /// it. Joined with a forward slash on purpose: the peer's file system is
    /// not this one's, and <c>Path.Combine</c> on Windows would hand a Linux
    /// follower a path with backslashes in it that its own mailbox check would
    /// then refuse.
    /// </summary>
    public string InPeerNamespace(string nativeMailboxPath) =>
        StorePathFromPeer.TrimEnd('/', '\\')
        + "/" + Adapters.FileSystem.FileSystemCredentialPairStore.TransitDirectoryName
        + "/" + Side.Value
        + "/" + Path.GetFileName(nativeMailboxPath);
}

/// <summary>
/// The sides <c>peers[]</c> configured. Empty is the ordinary state and the
/// whole lane's rollback: with no peer there is no WSL side on the page and
/// nothing here is ever called.
/// </summary>
internal sealed class PeerRegistry(IReadOnlyList<Peer> peers)
{
    public IReadOnlyList<Peer> All { get; } = peers;

    public Peer? For(SideName side) => All.FirstOrDefault(peer => peer.Side == side);
}
