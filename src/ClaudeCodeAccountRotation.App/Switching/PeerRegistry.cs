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
/// <para>
/// It owns its hosts and disposes them, which is what makes design 11's "the
/// follower is the leader's child, so it goes down too" true of a graceful
/// stop as well as a crash. The hosts are built inside this registry's own
/// factory, so the container has no other handle on them; without this a
/// stopped leader would leave its <c>wsl.exe</c> follower running, holding an
/// import and the configured port.
/// </para>
/// </summary>
internal sealed class PeerRegistry(IReadOnlyList<Peer> peers) : IDisposable
{
    public IReadOnlyList<Peer> All { get; } = peers;

    public Peer? For(SideName side) => All.FirstOrDefault(peer => peer.Side == side);

    public void Dispose()
    {
        foreach (Peer peer in All)
        {
            (peer.Host as IDisposable)?.Dispose();
        }
    }
}
