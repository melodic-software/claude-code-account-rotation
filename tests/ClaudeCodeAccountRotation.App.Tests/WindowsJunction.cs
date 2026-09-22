using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCodeAccountRotation.App.Tests;

/// <summary>
/// Creates a directory junction, which is a mount-point reparse point.
/// <see cref="Directory.CreateSymbolicLink(string, string)"/> creates a symbolic
/// link, and the Windows test has to plant the reparse point the containment
/// check exists to refuse. Returns false when the operating system refuses,
/// so the test can skip.
/// </summary>
internal static class WindowsJunction
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint SetReparsePoint = 0x000900A4;
    private const uint MountPointTag = 0xA0000003;

    public static bool TryCreate(string junction, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            Create(junction, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/> and everything under it. A junction or
    /// symbolic link is removed as the reparse point itself.
    /// <see cref="Directory.Delete(string, bool)"/> with recursion walks a
    /// Windows junction and then fails with "the parameter is incorrect",
    /// which is Win32 <c>ERROR_INVALID_PARAMETER</c> from the recursive
    /// remove. The non-recursive remove deletes only the mount point.
    /// </summary>
    public static void DeleteTree(string path)
    {
        if (IsReparsePoint(path))
        {
            RemoveReparsePoint(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (IsReparsePoint(entry))
            {
                RemoveReparsePoint(entry);
                continue;
            }

            if (Directory.Exists(entry))
            {
                DeleteTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RemoveReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.Delete(path, recursive: false);
            return;
        }

        File.Delete(path);
    }

    [SupportedOSPlatform("windows")]
    private static void Create(string junction, string target)
    {
        Directory.CreateDirectory(junction);
        string substitute = NtPrefix() + Path.GetFullPath(target);
        if (substitute[^1] != Path.DirectorySeparatorChar)
        {
            substitute += Path.DirectorySeparatorChar;
        }

        byte[] name = Encoding.Unicode.GetBytes(substitute);
        // Header, the four name fields, the substitute name, and the two nulls
        // the mount-point buffer keeps after it. The print name is empty.
        byte[] buffer = new byte[name.Length + 20];
        WriteUInt32(buffer, 0, MountPointTag);
        WriteUInt16(buffer, 4, (ushort)(name.Length + 12));
        WriteUInt16(buffer, 10, (ushort)name.Length);
        WriteUInt16(buffer, 12, (ushort)(name.Length + 2));
        name.CopyTo(buffer, 16);

        using SafeFileHandle handle = OpenReparseDirectory(junction);
        if (handle.IsInvalid)
        {
            throw new IOException("The junction directory could not be opened.");
        }

        if (!SetMountPoint(handle, SetReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>The NT object prefix a mount-point substitute name requires, built without a drive root in source.</summary>
    private static string NtPrefix() => new(['\\', '?', '?', '\\']);

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

#pragma warning disable SYSLIB1054 // LibraryImport wants unsafe code this test project does not enable.
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenReparseDirectory(
        string fileName,
        uint desiredAccess = GenericWrite,
        uint shareMode = ShareReadWrite,
        IntPtr securityAttributes = default,
        uint creationDisposition = OpenExisting,
        uint flagsAndAttributes = BackupSemantics | OpenReparsePoint,
        IntPtr templateFile = default);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMountPoint(
        SafeFileHandle device,
        uint controlCode,
        byte[] inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
#pragma warning restore SYSLIB1054
}
