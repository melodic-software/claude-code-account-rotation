using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// The profile folders under the profiles root. Identity comes from
/// <c>profile.json</c> (written at park time) or, for a fresh login's residue,
/// the <c>oauthAccount</c> block of the folder's own <c>.claude.json</c>; the
/// folder name is a label and is never trusted. Deletion is allowed only for a
/// folder this store discovered itself, never for a path built from a request.
/// </summary>
internal sealed class ProfileFolderStore
{
    public const string ProfileFileName = "profile.json";
    private const string StateFileName = ".claude.json";

    // The holder record joins the two because a login into a held slot must not
    // silently take away the one thing that says the other side has that
    // account's pair. A record the slot's own file contradicts is dropped by
    // reconciliation, with a log line, rather than by a sweep nobody reads.
    private static readonly string[] _keptOnPrune = [FileSystemCredentialPairStore.FileName, ProfileFileName, HolderRecordFile.FileName];

    private readonly string _profilesRoot;

    // A singleton mutated by every dashboard poll and every switch at once, so the
    // set must be safe for concurrent adds and removes.
    private readonly ConcurrentDictionary<string, byte> _discovered;

    public ProfileFolderStore(string profilesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesRoot);
        _profilesRoot = Path.GetFullPath(profilesRoot);
        _discovered = new ConcurrentDictionary<string, byte>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<ParkedProfile>> ListAsync(CancellationToken cancellationToken)
    {
        List<ParkedProfile> profiles = [];
        if (!Directory.Exists(_profilesRoot))
        {
            return profiles;
        }

        foreach (string folder in Directory.EnumerateDirectories(_profilesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            OAuthAccountBlock? account = await ReadAccountInFolderAsync(folder, cancellationToken);
            if (account?.Email is not AccountEmail email)
            {
                continue;
            }

            _discovered[folder] = 0;
            profiles.Add(new ParkedProfile(email, folder, HasCredentials(folder), account));
        }

        profiles.Sort(static (left, right) => string.CompareOrdinal(left.Email.Value, right.Email.Value));
        return profiles;
    }

    /// <summary>Where an account's folder sits, whether or not it exists yet.</summary>
    public string FolderPathFor(AccountEmail email) => Path.Combine(_profilesRoot, ProfileFolderName.FromEmail(email));

    public async Task<ParkedProfile> EnsureFolderAsync(AccountEmail email, CancellationToken cancellationToken)
    {
        string folder = FolderPathFor(email);
        Directory.CreateDirectory(folder);
        _discovered[folder] = 0;
        OAuthAccountBlock? account = await ReadAccountInFolderAsync(folder, cancellationToken);
        return new ParkedProfile(email, folder, HasCredentials(folder), account);
    }

    public Task WriteProfileAsync(string folderPath, OAuthAccountBlock account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        return AtomicJsonFile.WriteAsync(Path.Combine(UnderRoot(folderPath), ProfileFileName), account.Raw, cancellationToken);
    }

    public Task DeleteFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string folder = UnderRoot(folderPath);
        if (!_discovered.ContainsKey(folder))
        {
            throw new InvalidOperationException("Refusing to delete " + folder + ": only a folder discovered by listing the profiles root can be deleted.");
        }

        Directory.Delete(folder, recursive: true);
        _discovered.TryRemove(folder, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes everything a login under <c>CLAUDE_CONFIG_DIR</c> left behind
    /// except the pair and the profile, writing the profile first from the
    /// residue's state file when it does not exist yet.
    /// </summary>
    public async Task PruneLoginResidueAsync(string folderPath, CancellationToken cancellationToken)
    {
        string folder = UnderRoot(folderPath);
        string profilePath = Path.Combine(folder, ProfileFileName);
        if (!File.Exists(profilePath))
        {
            OAuthAccountBlock? account = await ReadAccountInFolderAsync(folder, cancellationToken);
            if (account is not null)
            {
                await AtomicJsonFile.WriteAsync(profilePath, account.Raw, cancellationToken);
            }
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_keptOnPrune.Contains(Path.GetFileName(entry), StringComparer.Ordinal))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    /// <summary>The identity recorded in a folder, from its profile or a fresh login's state file.</summary>
    public Task<OAuthAccountBlock?> ReadAccountAsync(string folderPath, CancellationToken cancellationToken) =>
        ReadAccountInFolderAsync(UnderRoot(folderPath), cancellationToken);

    /// <summary>
    /// Takes a completed login's word for who a folder holds: rewrites
    /// <c>profile.json</c> from the state file the login just wrote, and only
    /// then prunes the residue. The order is the point. A folder logged in a
    /// second time as another account still carries the first login's
    /// <c>profile.json</c>, and pruning first would delete the only fresh copy
    /// of the identity and leave the stale one standing.
    /// </summary>
    /// <returns>
    /// False when the state file names no account, in which case nothing is
    /// written and nothing is pruned: a folder whose only identity is the one
    /// already on disk is left exactly as the login left it.
    /// </returns>
    public async Task<bool> AdoptFreshLoginAsync(string folderPath, CancellationToken cancellationToken)
    {
        string folder = UnderRoot(folderPath);
        _discovered[folder] = 0;
        if (await ReadStateFileAccountAsync(folder, cancellationToken) is not OAuthAccountBlock fresh)
        {
            return false;
        }

        await AtomicJsonFile.WriteAsync(Path.Combine(folder, ProfileFileName), fresh.Raw, cancellationToken);
        await PruneLoginResidueAsync(folder, cancellationToken);
        return true;
    }

    private static bool HasCredentials(string folder) =>
        File.Exists(Path.Combine(folder, FileSystemCredentialPairStore.FileName));

    private static async Task<OAuthAccountBlock?> ReadAccountInFolderAsync(string folder, CancellationToken cancellationToken)
    {
        string profilePath = Path.Combine(folder, ProfileFileName);
        if (File.Exists(profilePath))
        {
            return await ReadObjectAsync(profilePath, cancellationToken) is JsonObject profile ? OAuthAccountBlock.FromJson(profile) : null;
        }

        return await ReadStateFileAccountAsync(folder, cancellationToken);
    }

    /// <summary>The <c>oauthAccount</c> block of the folder's own state file, ignoring any profile beside it.</summary>
    private static async Task<OAuthAccountBlock?> ReadStateFileAccountAsync(string folder, CancellationToken cancellationToken)
    {
        string statePath = Path.Combine(folder, StateFileName);
        if (!File.Exists(statePath))
        {
            return null;
        }

        return await ReadObjectAsync(statePath, cancellationToken) is JsonObject state && state["oauthAccount"] is JsonObject block
            ? OAuthAccountBlock.FromJson(block)
            : null;
    }

    private static async Task<JsonObject?> ReadObjectAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await SharedFileReader.ReadAllBytesAsync(path, cancellationToken);
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            // An unreadable identity file leaves the folder unidentified; the roster
            // reports it as needing a login rather than trusting the folder name.
            return null;
        }
    }

    private string UnderRoot(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        string full = Path.GetFullPath(folderPath);
        string relative = Path.GetRelativePath(_profilesRoot, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) || relative == "." || relative.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("A profile folder must sit directly under the profiles root " + _profilesRoot + "; got " + full, nameof(folderPath));
        }

        return full;
    }
}
