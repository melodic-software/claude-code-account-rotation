using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Configuration;

/// <summary>
/// Refuses a configuration the credential-move contract cannot honor: a
/// profiles root on another volume than the live directory (a move would be a
/// copy), a profiles root that equals, contains, or sits inside the live
/// directory or the home root, or one under a sync folder (Files On-Demand
/// dehydrates a credential file into a placeholder and a synced folder uploads
/// refresh tokens, a second holder by another name). Junctions and symbolic
/// links are followed before those comparisons. A profiles root or app data
/// directory that is itself a link is refused even when its target would pass.
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

        Result<string, string> liveResult = ForComparison("live config directory", configuration.LiveConfigDirectory);
        Result<string, string> profilesResult = ForComparison("profiles root", configuration.ProfilesRoot);
        Result<string, string> homeResult = ForComparison("home directory", homeDirectory);
        Result<string, string> appDataResult = ForComparison("app data directory", configuration.AppDataDirectory);
        if (liveResult.IsFailure)
        {
            return Failure(liveResult.Error);
        }

        if (profilesResult.IsFailure)
        {
            return Failure(profilesResult.Error);
        }

        if (homeResult.IsFailure)
        {
            return Failure(homeResult.Error);
        }

        if (appDataResult.IsFailure)
        {
            return Failure(appDataResult.Error);
        }

        string live = liveResult.Value;
        string profiles = profilesResult.Value;
        string home = homeResult.Value;
        string appData = appDataResult.Value;

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
        // every move is a rename, and neither may sit under a sync folder. The paths here
        // are the resolved ones, so a link into a sync folder or onto another volume is
        // judged by where the files would land.
        string? liveVolume = volumeOf(live);
        foreach ((string label, string configured, string root) in new[]
        {
            ("profiles root", configuration.ProfilesRoot, profiles),
            ("app data directory", configuration.AppDataDirectory, appData),
        })
        {
            string? rootVolume = volumeOf(root);
            if (!string.Equals(liveVolume, rootVolume, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("the " + label + " " + root + " (volume " + (rootVolume ?? "?") + ") must sit on the same volume as the live config directory " + live + " (volume " + (liveVolume ?? "?") + "): credential pairs are moved by rename, never copied");
            }

            foreach (string variable in _syncEnvironmentVariables)
            {
                if (environment(variable) is string syncRoot && !string.IsNullOrWhiteSpace(syncRoot) && UnderSyncRoot(root, syncRoot))
                {
                    return Failure("the " + label + " " + root + " sits under the " + variable + " sync folder " + syncRoot + "; credentials must never be synced");
                }
            }

            if (SyncFolderUnderHome(root, home) is string syncFolder)
            {
                return Failure("the " + label + " " + root + " sits under the " + syncFolder + " folder; credentials must never be synced");
            }

            // After the target has been checked. A link whose target is a sync
            // folder or another volume was already refused above; a link whose
            // target would otherwise pass is still refused. The reason names no path.
            if (DirectoryLinks.ItselfALink(configured))
            {
                return Failure("the " + label + " is a junction or symbolic link; credentials are not stored through one");
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
        // The mount check runs before a missing-target refusal so a link onto
        // the mount is still named as the mount, including when the final
        // directory has not been created.
        Result<DirectoryLinks.CanonicalPath, string> liveCanonical = DirectoryLinks.Canonicalize(configuration.LiveConfigDirectory);
        if (liveCanonical.IsFailure)
        {
            return Failure("the follower's live config directory " + liveCanonical.Error);
        }

        string live = liveCanonical.Value.Path;
        if (UnderWindowsMount(configuration.LiveConfigDirectory) || UnderWindowsMount(live))
        {
            return Failure("the follower's live config directory " + configuration.LiveConfigDirectory + " sits under /mnt/; a follower's live pair must be on its own file system, never on the Windows volume through DrvFs");
        }

        if (liveCanonical.Value.TargetMissing)
        {
            return Failure("the follower's live config directory has a junction or symbolic link whose target is missing");
        }

        Result<string, string> appDataResult = ForComparison("follower's app data directory", configuration.AppDataDirectory);
        if (appDataResult.IsFailure)
        {
            return Failure(appDataResult.Error);
        }

        string appData = appDataResult.Value;
        string? liveVolume = volumeOf(live);
        string? appDataVolume = volumeOf(appData);
        if (!string.Equals(liveVolume, appDataVolume, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("the follower's app data directory " + appData + " (volume " + (appDataVolume ?? "?") + ") must sit on the same volume as its live config directory " + live + " (volume " + (liveVolume ?? "?") + "): the import journal decides after a crash what the live file already holds");
        }

        if (DirectoryLinks.ItselfALink(configuration.AppDataDirectory))
        {
            return Failure("the follower's app data directory is a junction or symbolic link; credentials are not stored through one");
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
            // The mount or drive of the final target. A junction's own drive
            // letter is not the volume a file created through it lands on.
            Result<DirectoryLinks.CanonicalPath, string> canonical = DirectoryLinks.Canonicalize(path);
            if (canonical.IsFailure || canonical.Value.TargetMissing)
            {
                return null;
            }

            string full = canonical.Value.Path;
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

    /// <summary>
    /// The path comparisons use, with links followed. A missing link target is a
    /// refusal that names no path: the unresolved spelling is not a usable root.
    /// </summary>
    private static Result<string, string> ForComparison(string label, string path)
    {
        Result<DirectoryLinks.CanonicalPath, string> canonical = DirectoryLinks.Canonicalize(path);
        if (canonical.IsFailure)
        {
            return Result<string, string>.Failure("the " + label + " " + canonical.Error);
        }

        return canonical.Value.TargetMissing
            ? Result<string, string>.Failure("the " + label + " has a junction or symbolic link whose target is missing")
            : Result<string, string>.Success(canonical.Value.Path);
    }

    /// <summary>Whether <paramref name="root"/> is the sync folder or sits inside it, in either spelling.</summary>
    private static bool UnderSyncRoot(string root, string syncRoot)
    {
        string lexical = Normalize(syncRoot);
        if (Same(root, lexical) || Contains(lexical, root))
        {
            return true;
        }

        Result<DirectoryLinks.CanonicalPath, string> canonical = DirectoryLinks.Canonicalize(syncRoot);
        return canonical.IsSuccess
            && !canonical.Value.TargetMissing
            && (Same(root, canonical.Value.Path) || Contains(canonical.Value.Path, root));
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool Same(string first, string second) => string.Equals(first, second, Comparison);

    private static bool Contains(string parent, string child) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, Comparison);

    private static Result<Unit, string> Failure(string reason) => Result<Unit, string>.Failure(reason);
}
