using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// Resolves junctions and symbolic links before a containment or volume
/// comparison. <see cref="Path.GetFullPath"/> only normalizes spelling, so a
/// profiles root that is a junction would otherwise match every string check
/// and a rename would land outside the root, or be copied across volumes.
/// </summary>
/// <remarks>
/// The walk uses <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> with
/// <c>returnFinalTarget: true</c>. On Windows a junction is a reparse point,
/// and that call follows it; a reparse point the runtime cannot read is
/// refused. A link whose final target does not exist is a failure, not a
/// reason to keep the unresolved path. A directory that has not been created
/// yet, and is not itself a link, is returned as spelled: first-run
/// configuration names folders that are not on disk yet.
/// </remarks>
internal static class DirectoryLinks
{
    // Unix refuses the walk at 40 links. Windows allows 63. The stricter limit
    // bounds every platform, including a cycle ResolveLinkTarget did not throw for.
    private const int MaxHops = 40;

    /// <summary>A path with every junction and symbolic link along it followed.</summary>
    /// <param name="Path">The normalized final spelling, whether or not that directory exists yet.</param>
    /// <param name="TargetMissing">True when a link was followed and its target is not on disk.</param>
    internal readonly record struct CanonicalPath(string Path, bool TargetMissing);

    /// <summary>
    /// Whether <paramref name="path"/>'s own final component is a junction,
    /// symbolic link, or other reparse point. An ancestor that is a link does
    /// not count: the directory itself is what a later rename would trust.
    /// A path that is not on disk is not a link.
    /// </summary>
    public static bool ItselfALink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Classify(Path.GetFullPath(path)) == ComponentKind.Link;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Follows every junction and symbolic link in <paramref name="path"/>.
    /// The failure text names no path. <see cref="CanonicalPath.TargetMissing"/>
    /// is set when the spelling was produced by a link whose target is absent,
    /// so the caller can refuse it instead of trusting the link path.
    /// </summary>
    public static Result<CanonicalPath, string> Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string pending;
        try
        {
            pending = Normalize(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return Result<CanonicalPath, string>.Failure("could not be resolved");
        }

        bool targetMissing = false;
        for (int hop = 0; hop < MaxHops; hop++)
        {
            string? root = Path.GetPathRoot(pending);
            if (string.IsNullOrEmpty(root))
            {
                return Result<CanonicalPath, string>.Failure("could not be resolved");
            }

            string[] parts = pending.Length > root.Length
                ? pending[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                : [];
            string current = root;
            int index = 0;
            for (; index < parts.Length; index++)
            {
                current = Path.Combine(current, parts[index]);
                ComponentKind kind = Classify(current);
                if (kind == ComponentKind.Unreadable)
                {
                    return Result<CanonicalPath, string>.Failure("could not be resolved");
                }

                if (kind != ComponentKind.Link)
                {
                    continue;
                }

                Result<string, string> target = ReadFinalTarget(current);
                if (target.IsFailure)
                {
                    return Result<CanonicalPath, string>.Failure(target.Error);
                }

                // returnFinalTarget still leaves a link in an ancestor of the
                // target (a link whose target path itself goes through a link).
                // Restarting the walk follows that one too. A target with no
                // remaining link is judged here: a file is not a directory we
                // can store in, and a missing target must not fall back to the
                // link's own path.
                if (!ContainsLink(target.Value))
                {
                    if (File.Exists(target.Value) && !Directory.Exists(target.Value))
                    {
                        return Result<CanonicalPath, string>.Failure("has a junction or symbolic link that does not name a directory");
                    }

                    if (!Directory.Exists(target.Value))
                    {
                        targetMissing = true;
                    }
                }

                string remainder = string.Join(Path.DirectorySeparatorChar, parts[(index + 1)..]);
                string next = string.IsNullOrEmpty(remainder) ? target.Value : Path.Combine(target.Value, remainder);
                try
                {
                    next = Normalize(next);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
                {
                    return Result<CanonicalPath, string>.Failure("could not be resolved");
                }

                if (string.Equals(next, pending, Comparison))
                {
                    return Result<CanonicalPath, string>.Failure("has a junction or symbolic link that could not be resolved");
                }

                pending = next;
                break;
            }

            if (index == parts.Length)
            {
                return Result<CanonicalPath, string>.Success(new CanonicalPath(Normalize(current), targetMissing));
            }
        }

        return Result<CanonicalPath, string>.Failure("has too many junctions or symbolic links");
    }

    /// <summary>
    /// The folder directly under <paramref name="profilesRoot"/>, after junctions
    /// and symbolic links on both paths are followed. A root or folder that is
    /// itself a link is refused even when its target sits on the same volume
    /// and is not a sync folder. The returned path keeps the caller's spelling
    /// when the check passes, so a discovered-folder set stays stable; the
    /// move's volume check resolves that spelling again.
    /// The failure text names no path.
    /// </summary>
    public static Result<string, string> DirectChild(string profilesRoot, string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (ItselfALink(profilesRoot))
        {
            return Result<string, string>.Failure("The profiles root is a junction or symbolic link; credentials are not stored through one.");
        }

        if (ItselfALink(folderPath))
        {
            return Result<string, string>.Failure("A profile folder is a junction or symbolic link; credentials are not stored through one.");
        }

        Result<CanonicalPath, string> root = Canonicalize(profilesRoot);
        if (root.IsFailure)
        {
            return Result<string, string>.Failure("The profiles root " + root.Error + ".");
        }

        if (root.Value.TargetMissing)
        {
            return Result<string, string>.Failure("The profiles root has a junction or symbolic link whose target is missing.");
        }

        Result<CanonicalPath, string> folder = Canonicalize(folderPath);
        if (folder.IsFailure)
        {
            return Result<string, string>.Failure("A profile folder " + folder.Error + ".");
        }

        if (folder.Value.TargetMissing)
        {
            return Result<string, string>.Failure("A profile folder has a junction or symbolic link whose target is missing.");
        }

        string relative = Path.GetRelativePath(root.Value.Path, folder.Value.Path);
        if (relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || relative == "."
            || relative.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Result<string, string>.Failure("A profile folder must sit directly under the profiles root.");
        }

        return Result<string, string>.Success(Path.GetFullPath(folderPath));
    }

    /// <summary>Whether any component of <paramref name="path"/> is itself a link. A missing component ends the scan.</summary>
    private static bool ContainsLink(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        string[] parts = path.Length > root.Length
            ? path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            : [];
        string current = root;
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            if (Classify(current) == ComponentKind.Link)
            {
                return true;
            }

            if (!Directory.Exists(current) && !File.Exists(current))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The final target of the link at <paramref name="linkPath"/>, normalized.
    /// Null from <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> means a
    /// reparse point this runtime will not follow; that is a refusal, because
    /// the directory's real location is then unknown.
    /// </summary>
    private static Result<string, string> ReadFinalTarget(string linkPath)
    {
        try
        {
            FileSystemInfo? target = new DirectoryInfo(linkPath).ResolveLinkTarget(returnFinalTarget: true);
            return target is null
                ? Result<string, string>.Failure("has a reparse point that could not be resolved")
                : Result<string, string>.Success(Normalize(target.FullName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The exception text can quote the path. The reason must not.
            return Result<string, string>.Failure("has a junction or symbolic link that could not be resolved");
        }
    }

    private static ComponentKind Classify(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                ? ComponentKind.Link
                : ComponentKind.Plain;
        }
        catch (UnauthorizedAccessException)
        {
            return ComponentKind.Unreadable;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            // Not on disk yet, which includes a not-yet-created profiles root.
            return ComponentKind.Plain;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private enum ComponentKind
    {
        Plain,
        Link,
        Unreadable,
    }
}
