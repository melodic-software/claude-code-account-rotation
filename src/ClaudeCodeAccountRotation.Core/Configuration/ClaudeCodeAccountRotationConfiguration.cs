namespace ClaudeCodeAccountRotation.Core.Configuration;

/// <summary>
/// Everything the tool reads from its configuration file. Every path defaults
/// from the user profile at runtime; the shipped template carries no literal
/// path, drive letter, or user name.
/// <para>
/// <paramref name="SharedStore"/> is <c>store.shared</c>: whether the two
/// operating-system sides of this machine share one account store, with a
/// holder record in each slot whose pair a side has taken. It is false by
/// default and every behavior that depends on it is gated on it, so turning
/// it off is the whole rollback. It trails the record with a default so the
/// construction sites that predate it compile unchanged.
/// </para>
/// <para>
/// <paramref name="Role"/> and <paramref name="Mailbox"/> are the follower's
/// two keys. A follower owns no store and no profiles root; it imports into
/// its own live directory from files under <paramref name="Mailbox"/>, the
/// one path in the product allowed to sit on the other volume.
/// </para>
/// <para>
/// <paramref name="Peers"/> is <c>peers[]</c>, the other sides of this
/// machine. An absent or empty list is the rollback for the whole WSL lane:
/// no side appears on the page and the Windows side behaves exactly as it did
/// before the coordinator existed.
/// </para>
/// <para>
/// <paramref name="RemoteControlTask"/> is <c>remoteControlTask</c>: the
/// Windows scheduled task a switch clicked on this side restarts once the
/// switch verifies, or null for none.
/// </para>
/// <para>
/// <paramref name="LaneRepositories"/> is <c>laneRepositories</c>: the
/// repositories (<c>owner/name</c>) where CI lanes run, whose
/// <c>CLAUDE_LANES_REPO_DISABLED</c> variable Check roles reads. Null, as in
/// a file written before the key, means none.
/// </para>
/// <para>
/// <paramref name="LaneCatalog"/> is <c>laneCatalog</c>: where the lane-funding
/// catalog is read from, or null, as in a file written before the key, for
/// none: the roles are then typed by hand and keyed by
/// <see cref="Accounts.CiRole.Keyed"/>.
/// </para>
/// </summary>
public sealed record ClaudeCodeAccountRotationConfiguration(
    string LiveConfigDirectory,
    string StateFilePath,
    string ProfilesRoot,
    string AppDataDirectory,
    string StatuslineTeePath,
    int ListenPort,
    TimeSpan RefreshLockWaitBound,
    string? ClaudeExecutable,
    string UserAgentProductToken,
    IReadOnlyDictionary<string, string> BrowserExecutables,
    bool SharedStore = false,
    RotationRole Role = RotationRole.Leader,
    string? Mailbox = null,
    IReadOnlyList<PeerConfiguration>? Peers = null,
    string? RemoteControlTask = null,
    IReadOnlyList<string>? LaneRepositories = null,
    LaneCatalogSource? LaneCatalog = null);

/// <summary>
/// A lane-funding catalog's file: its repository (<c>owner/name</c>), its path
/// in that repository, and the branch, tag or commit to read, or null for the
/// repository's default branch.
/// </summary>
public sealed record LaneCatalogSource(string Repository, string Path, string? Ref);
