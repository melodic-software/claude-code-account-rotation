using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Configuration;

/// <summary>
/// Refuses a configuration the credential-move contract cannot honor: a
/// profiles root on another volume than the live directory (a move would be a
/// copy), a profiles root that equals, contains, or sits inside the live
/// directory or the home root, or one under a sync folder (Files On-Demand
/// dehydrates a credential file into a placeholder and a synced folder uploads
/// refresh tokens, a second holder by another name).
/// </summary>
internal static class ConfigurationValidator
{
    private static readonly string[] _syncEnvironmentVariables = ["OneDrive", "OneDriveCommercial", "OneDriveConsumer"];

    // Matched as prefixes of the folder directly under the home directory, since the
    // clients decorate the name: "OneDrive - Contoso", "Dropbox (Personal)", "My Drive".
    private static readonly string[] _syncFolderPrefixes = ["OneDrive", "Dropbox", "Google Drive", "My Drive", "iCloudDrive", "iCloud Drive", "Box"];

    public static Result<Unit, string> Validate(
        ClaudeCodeAccountRotationConfiguration configuration,
        string homeDirectory,
        Func<string, string?> volumeOf,
        Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentNullException.ThrowIfNull(volumeOf);
        ArgumentNullException.ThrowIfNull(environment);

        if (configuration.ListenPort is < 1 or > 65535)
        {
            return Failure("listenPort must be between 1 and 65535");
        }

        if (configuration.Role == RotationRole.Follower)
        {
            return ValidateFollower(configuration, volumeOf);
        }

        string live = Normalize(configuration.LiveConfigDirectory);
        string profiles = Normalize(configuration.ProfilesRoot);
        string home = Normalize(homeDirectory);

        if (Same(profiles, live) || Contains(profiles, live) || Contains(live, profiles))
        {
            return Failure("the profiles root " + profiles + " must not equal, contain, or sit inside the live config directory " + live);
        }

        if (Same(profiles, home))
        {
            return Failure("the profiles root must be a folder of its own, not the home directory " + home);
        }

        // Both roots that ever hold a credential file: the profiles root (parked pairs) and
        // the app data directory (quarantined pairs). Each must share the live volume, since
        // every move is a rename, and neither may sit under a sync folder.
        string appData = Normalize(configuration.AppDataDirectory);
        string? liveVolume = volumeOf(live);
        foreach ((string label, string root) in new[] { ("profiles root", profiles), ("app data directory", appData) })
        {
            string? rootVolume = volumeOf(root);
            if (!string.Equals(liveVolume, rootVolume, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("the " + label + " " + root + " (volume " + (rootVolume ?? "?") + ") must sit on the same volume as the live config directory " + live + " (volume " + (liveVolume ?? "?") + "): credential pairs are moved by rename, never copied");
            }

            foreach (string variable in _syncEnvironmentVariables)
            {
                if (environment(variable) is string syncRoot && !string.IsNullOrWhiteSpace(syncRoot) && (Same(root, Normalize(syncRoot)) || Contains(Normalize(syncRoot), root)))
                {
                    return Failure("the " + label + " " + root + " sits under the " + variable + " sync folder " + syncRoot + "; credentials must never be synced");
                }
            }

            if (SyncFolderUnderHome(root, home) is string syncFolder)
            {
                return Failure("the " + label + " " + root + " sits under the " + syncFolder + " folder; credentials must never be synced");
            }
        }

        return Result<Unit, string>.Success(Unit.Value);
    }

    /// <summary>
    /// The follower's rules, which are not the leader's. It has no profiles root
    /// and never parks a pair, so the same-volume rule that protects park and
    /// unpark does not apply to one; what does apply is that the only path it
    /// may reach on the other volume is its mailbox.
    /// <list type="bullet">
    /// <item>The live directory must not sit under <c>/mnt/</c>. A live pair on
    /// DrvFs would put the CLI's own writes and its <c>.oauth_refresh.lock</c>
    /// on the mount whose durability section 3 could not measure, and the
    /// atomic replace F5 depends on would no longer be atomic.</item>
    /// <item>App data must share the live directory's volume: the journal is
    /// what decides, after a crash, whether a swap happened, and a journal on
    /// the other volume can disagree with the files it describes.</item>
    /// <item><c>mailbox</c> must be configured, exist, and be writable, since
    /// the export is the outgoing account's only copy between F4 and the
    /// leader's park.</item>
    /// </list>
    /// </summary>
    private static Result<Unit, string> ValidateFollower(
        ClaudeCodeAccountRotationConfiguration configuration,
        Func<string, string?> volumeOf)
    {
        // The configured spelling, not the normalized one: Path.GetFullPath turns
        // "/mnt/c/..." into a drive-rooted path on Windows, where this check would
        // then pass for exactly the configuration it exists to refuse. And the
        // resolved one too, so neither "/tmp/../mnt/c/..." nor a link under the
        // home directory that points onto the mount can spell its way past.
        string live = Resolved(configuration.LiveConfigDirectory);
        string appData = Resolved(configuration.AppDataDirectory);
        if (UnderWindowsMount(configuration.LiveConfigDirectory) || UnderWindowsMount(live))
        {
            return Failure("the follower's live config directory " + configuration.LiveConfigDirectory + " sits under /mnt/; a follower's live pair must be on its own file system, never on the Windows volume through DrvFs");
        }

        string? liveVolume = volumeOf(live);
        string? appDataVolume = volumeOf(appData);
        if (!string.Equals(liveVolume, appDataVolume, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("the follower's app data directory " + appData + " (volume " + (appDataVolume ?? "?") + ") must sit on the same volume as its live config directory " + live + " (volume " + (liveVolume ?? "?") + "): the import journal decides after a crash what the live file already holds");
        }

        if (string.IsNullOrWhiteSpace(configuration.Mailbox))
        {
            return Failure("a follower needs a mailbox: the directory on the store's volume it stages from and exports to");
        }

        string mailbox = Normalize(configuration.Mailbox);
        if (!Directory.Exists(mailbox))
        {
            return Failure("the follower's mailbox " + mailbox + " does not exist; the leader creates it in the store under .transit/");
        }

        return IsWritable(mailbox)
            ? Result<Unit, string>.Success(Unit.Value)
            : Failure("the follower's mailbox " + mailbox + " is not writable; the export is the outgoing account's only copy until the leader parks it");
    }

    /// <summary>Whether a path is spelled as a WSL view of a Windows volume, before any normalization.</summary>
    private static bool UnderWindowsMount(string path) =>
        path.Replace('\\', '/').TrimStart().StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writability asked of the file system rather than inferred from a mode or an ACL.</summary>
    private static bool IsWritable(string directory)
    {
        string probe = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".probe");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The volume that owns a path: the drive root on Windows, and elsewhere the
    /// longest mount point that contains the path. <see cref="DriveInfo"/> built
    /// from a path reports the path itself as its name on Unix, which would make
    /// every two directories look like two volumes.
    /// </summary>
    public static string? VolumeOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
            {
                return new DriveInfo(full).Name;
            }

            string? owner = null;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string mount = Path.TrimEndingDirectorySeparator(drive.Name);
                bool owns = mount == "/"
                    || string.Equals(full, mount, StringComparison.Ordinal)
                    || full.StartsWith(mount + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (owns && (owner is null || mount.Length > owner.Length))
                {
                    owner = mount;
                }
            }

            return owner;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The name of the sync folder directly under the home directory that owns <paramref name="path"/>, or null.</summary>
    private static string? SyncFolderUnderHome(string path, string home)
    {
        if (!Contains(home, path))
        {
            return null;
        }

        string firstSegment = path[(home.Length + 1)..].Split(Path.DirectorySeparatorChar, 2)[0];
        return _syncFolderPrefixes.Any(prefix => firstSegment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ? firstSegment
            : null;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// The normalized path with every symbolic link along it followed, so a
    /// directory is judged by where its files really land. A link is followed
    /// whether or not its target exists. Each link found restarts the walk from
    /// the root of the path it produced, so a link inside a link's target is
    /// followed too, and a cycle stops at 40 links in all.
    /// </summary>
    private static string Resolved(string path)
    {
        string pending = Normalize(path);
        for (int hop = 0; hop < 40; hop++)
        {
            string current = Path.GetPathRoot(pending)!;
            string[] parts = pending[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            int index = 0;
            for (; index < parts.Length; index++)
            {
                current = Path.Combine(current, parts[index]);
                if (new FileInfo(current).LinkTarget is string target)
                {
                    pending = Normalize(Path.Combine(Path.GetDirectoryName(current)!, target, string.Join(Path.DirectorySeparatorChar, parts[(index + 1)..])));
                    break;
                }
            }

            if (index == parts.Length)
            {
                return current;
            }
        }

        return pending;
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool Same(string first, string second) => string.Equals(first, second, Comparison);

    private static bool Contains(string parent, string child) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, Comparison);

    private static Result<Unit, string> Failure(string reason) => Result<Unit, string>.Failure(reason);
}
