using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
/// A missing id is not the same volume: the move is refused.
/// </remarks>
internal static partial class SameVolume
{
    // ENOENT. Any other errno from stat is a probe that cannot be trusted.
    private const int UnixErrorNoEntry = 2;

    /// <summary>
    /// Whether <paramref name="first"/> and <paramref name="second"/> are on
    /// one volume. On Windows the <paramref name="deviceId"/> function is not
    /// consulted. On Unix both ids must be present and equal.
    /// </summary>
    public static bool OnOneVolume(string first, string second, Func<string, long?> deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return OperatingSystem.IsWindows()
            ? WindowsPathsOnOneVolume(first, second)
            : UnixDevicesMatch(first, second, deviceId);
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

    private static bool WindowsPathsOnOneVolume(string first, string second) =>
        WindowsPathRootsMatch(Path.GetPathRoot(Path.GetFullPath(first)), Path.GetPathRoot(Path.GetFullPath(second)));

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

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [SupportedOSPlatform("linux")]
    private static partial int StatLinuxX64(string path, out LinuxX64Stat buffer);
}
