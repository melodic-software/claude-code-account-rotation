using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeCodeAccountRotation.Core;

namespace ClaudeCodeAccountRotation.App.Adapters.FileSystem;

/// <summary>
/// Whether two paths are on one volume, so a credential rename stays a rename.
/// <see cref="File.Move"/> across volumes copies the file and then deletes it,
/// and that window is a second holder of the refresh token.
/// </summary>
/// <remarks>
/// Windows compares drive roots, ordinal-ignore-case, which is what
/// <see cref="Path.GetPathRoot"/> can actually tell apart there. On Unix a
/// path root is <c>/</c> for every absolute path, including two different
/// mounts, so the check reads device ids instead. The id comes from an
/// injected function in tests and from <see cref="DeviceId"/> in production.
/// A missing id is not the same volume: the move is refused. Equal device
/// ids are not proof that <c>rename</c> can do it: a bind mount of one
/// filesystem, or two btrfs subvolumes, can share <c>st_dev</c> while
/// <c>rename</c> still returns <c>EXDEV</c> (Linux <c>rename(2)</c>). The
/// move itself is <c>renameat2</c> with <c>RENAME_NOREPLACE</c>, and
/// <c>EXDEV</c> is a refusal. <see cref="File.Move"/> is not used on Linux,
/// because across volumes it copies the file and then deletes the source.
/// Both paths are resolved through <see cref="DirectoryLinks"/> first:
/// <see cref="Path.GetFullPath"/> does not follow a junction, so a Windows
/// drive-root comparison of the unresolved path would call two volumes one.
/// </remarks>
internal static partial class SameVolume
{
    // ENOENT. Any other errno from stat is a probe that cannot be trusted.
    private const int UnixErrorNoEntry = 2;

    /// <summary><c>EXDEV</c> from <c>rename(2)</c>: the paths are not one mounted filesystem.</summary>
    public const int CrossDeviceError = 18;

    // renameat2(AT_FDCWD, ..., AT_FDCWD, ..., RENAME_NOREPLACE). AT_FDCWD is -100.
    // RENAME_NOREPLACE is 1 and fails with EEXIST instead of overwriting.
    private const int AtCurrentDirectory = -100;
    private const uint RenameNoReplace = 1;

    /// <summary>
    /// Whether <paramref name="first"/> and <paramref name="second"/> are on
    /// one volume. On Windows the <paramref name="deviceId"/> function is not
    /// consulted. On Unix both ids must be present and equal.
    /// </summary>
    public static bool OnOneVolume(string first, string second, Func<string, long?> deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        // A missing link target is not one volume: the move is refused. The
        // resolved spelling is what the drive root and the device id both see,
        // so a junction onto another volume cannot match the link's own root.
        if (!TryCanonical(first, out string left) || !TryCanonical(second, out string right))
        {
            return false;
        }

        return OperatingSystem.IsWindows()
            ? WindowsPathRootsMatch(Path.GetPathRoot(left), Path.GetPathRoot(right))
            : UnixDevicesMatch(left, right, deviceId);
    }

    /// <summary>
    /// The Windows comparison, separated from path parsing so a test can state
    /// the ordinal-ignore-case rule without a Windows path parser. Drive
    /// letters are what <see cref="Path.GetPathRoot"/> returns on Windows.
    /// </summary>
    public static bool WindowsPathRootsMatch(string? leftRoot, string? rightRoot) =>
        string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The device id of <paramref name="path"/> on linux-x64, or of the nearest
    /// existing ancestor when the path is not there yet (a rename's destination).
    /// Null when the id cannot be read, including on every Unix this layout
    /// does not describe, so the caller refuses the move.
    /// </summary>
    public static long? DeviceId(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            // struct stat is architecture-specific. This layout was measured for
            // glibc's stat on linux-x64 (Ubuntu 24.04, the CI image): 144 bytes,
            // st_dev at offset 0. A wrong offset would report one device for two
            // mounts, which is the bug. Other Unix, including other Linux
            // architectures, get no id and the move is refused.
            return null;
        }

        return DeviceIdLinuxX64(path);
    }

    /// <summary>
    /// Renames <paramref name="source"/> onto <paramref name="destination"/>
    /// without copying. On Linux this is <c>renameat2</c> with
    /// <c>RENAME_NOREPLACE</c>, or <paramref name="rename"/> in a test. The
    /// return is 0 or an errno; <see cref="CrossDeviceError"/> means the
    /// caller must leave the source where it is. On Windows
    /// <paramref name="rename"/> is not consulted and <see cref="File.Move"/>
    /// runs after the drive-root check the caller already made.
    /// </summary>
    public static int MoveByRename(string source, string destination, Func<string, string, int>? rename)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(source, destination);
            return 0;
        }

        if (!OperatingSystem.IsLinux())
        {
            // No measured rename for this Unix. Refusing is the same answer as
            // a missing device id: never fall through to a copying move.
            return CrossDeviceError;
        }

        return rename is null ? RenameNoReplaceLinux(source, destination) : rename(source, destination);
    }

    private static bool TryCanonical(string path, out string canonical)
    {
        Result<DirectoryLinks.CanonicalPath, string> resolved = DirectoryLinks.Canonicalize(path);
        if (resolved.IsFailure || resolved.Value.TargetMissing)
        {
            canonical = string.Empty;
            return false;
        }

        canonical = resolved.Value.Path;
        return true;
    }

    private static bool UnixDevicesMatch(string first, string second, Func<string, long?> deviceId)
    {
        long? left = deviceId(first);
        long? right = deviceId(second);
        return left is long leftId && right is long rightId && leftId == rightId;
    }

    [SupportedOSPlatform("linux")]
    private static long? DeviceIdLinuxX64(string path)
    {
        try
        {
            string? current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (StatLinuxX64(current, out LinuxX64Stat stat) == 0)
                {
                    return unchecked((long)stat.DeviceId);
                }

                // A destination that does not exist yet will be created on its
                // ancestor's device. Any other failure (permissions, a bad path)
                // fails closed rather than guessing.
                if (Marshal.GetLastPInvokeError() != UnixErrorNoEntry)
                {
                    return null;
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                {
                    return null;
                }

                current = parent;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// glibc <c>struct stat</c> on linux-x64, only as far as <c>st_dev</c>.
    /// Measured on glibc 2.39: <c>sizeof(struct stat)</c> is 144 and
    /// <c>st_dev</c> is the first field. The buffer is that size so <c>stat</c>
    /// does not write past it. <c>stat</c> follows a symlink, which is the
    /// device a directory names and the device a file created through it lands on.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct LinuxX64Stat
    {
        [FieldOffset(0)]
        public ulong DeviceId;
    }

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [SupportedOSPlatform("linux")]
    private static partial int RenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

    [SupportedOSPlatform("linux")]
    private static int RenameNoReplaceLinux(string source, string destination)
    {
        if (RenameAt2(AtCurrentDirectory, source, AtCurrentDirectory, destination, RenameNoReplace) == 0)
        {
            return 0;
        }

        return Marshal.GetLastPInvokeError();
    }

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [SupportedOSPlatform("linux")]
    private static partial int StatLinuxX64(string path, out LinuxX64Stat buffer);
}
