using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Adapters.SecretStore;

/// <summary>
/// Windows Credential Manager: one generic credential per key, persisted for
/// this user on this computer only. <c>CRED_PERSIST_ENTERPRISE</c> would roam
/// the key to every machine the user signs in to, so it is never used. The
/// blob is the key's UTF-8 bytes, and every buffer this class fills with
/// them is zeroed once the call returns.
/// </summary>
internal sealed partial class WindowsCredentialStore : ISecretStore
{
    // wincred.h.
    internal const uint CredTypeGeneric = 1;
    internal const uint CredPersistLocalMachine = 2;
    internal const int ErrorNotFound = 1168;
    internal const int ErrorNoSuchLogonSession = 1312;

    // CRED_MAX_CREDENTIAL_BLOB_SIZE: 5 * 512 bytes.
    private const int MaximumBlobBytes = 5 * 512;

    /// <summary>What a write hands <c>CredWriteW</c> besides the blob.</summary>
    internal readonly record struct CredentialArguments(string TargetName, uint Type, uint Persist, string UserName);

    /// <summary>The fixed half of every write, built without touching the store so it is checked on every OS.</summary>
    internal static CredentialArguments ArgumentsFor(SecretKind kind, AccountEmail email) =>
        new(SecretNames.Target(kind, email), CredTypeGeneric, CredPersistLocalMachine, email.Value);

    // Credential Manager exists on every Windows this runs on. A session with
    // no credential set (SSH, a service) is found by the write, which says so.
    public Task<Result<Unit, string>> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result<Unit, string>.Success(Unit.Value));

    [SupportedOSPlatform("windows")]
    public Task<Result<Unit, string>> WriteAsync(SecretKind kind, AccountEmail email, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        int size = Encoding.UTF8.GetByteCount(value);
        if (size > MaximumBlobBytes)
        {
            return Task.FromResult(Result<Unit, string>.Failure("the key is longer than Credential Manager can hold"));
        }

        byte[] blob = new byte[size];
        try
        {
            Encoding.UTF8.GetBytes(value, blob);
            int error = WriteCredential(ArgumentsFor(kind, email), blob);
            return Task.FromResult(error == 0 ? Result<Unit, string>.Success(Unit.Value) : Result<Unit, string>.Failure(Cause(error)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    [SupportedOSPlatform("windows")]
    public Task<Result<string?, string>> ReadAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken)
    {
        int error = ReadCredential(SecretNames.Target(kind, email), out byte[]? blob, out _);
        if (error == ErrorNotFound)
        {
            return Task.FromResult(Result<string?, string>.Success(null));
        }

        if (error != 0)
        {
            return Task.FromResult(Result<string?, string>.Failure(Cause(error)));
        }

        try
        {
            return Task.FromResult(Result<string?, string>.Success(Encoding.UTF8.GetString(blob!)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    [SupportedOSPlatform("windows")]
    public Task<Result<Unit, string>> DeleteAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken)
    {
        int error = DeleteCredential(SecretNames.Target(kind, email));
        // A session with no credential set (1312) cannot see the user's store,
        // so the key may still be there: a failure, not "nothing stored".
        return Task.FromResult(error is 0 or ErrorNotFound
            ? Result<Unit, string>.Success(Unit.Value)
            : Result<Unit, string>.Failure(Cause(error)));
    }

    private static string Cause(int error) => error == ErrorNoSuchLogonSession
        ? "this Windows sign-in has no credential store (ERROR_NO_SUCH_LOGON_SESSION, 1312), as in an SSH or service session; run the tool from your desktop sign-in"
        : "Credential Manager refused (Win32 error " + error.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary><c>CredWriteW</c>; 0, or the Win32 error it set.</summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe int WriteCredential(CredentialArguments arguments, ReadOnlySpan<byte> blob)
    {
        fixed (char* target = arguments.TargetName)
        fixed (char* user = arguments.UserName)
        fixed (byte* bytes = blob)
        {
            NativeCredential credential = new()
            {
                Type = arguments.Type,
                TargetName = (nint)target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = (nint)bytes,
                Persist = arguments.Persist,
                UserName = (nint)user,
            };
            return CredWrite(&credential, 0) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    /// <summary>
    /// <c>CredReadW</c>; 0, or the Win32 error it set. On success the blob is
    /// copied out, the copy Windows allocated is zeroed before it is freed,
    /// and the caller owns (and zeroes) <paramref name="blob"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe int ReadCredential(string target, out byte[]? blob, out uint persist)
    {
        blob = null;
        persist = 0;
        if (!CredRead(target, CredTypeGeneric, 0, out nint handle))
        {
            return Marshal.GetLastPInvokeError();
        }

        try
        {
            var credential = (NativeCredential*)handle;
            persist = credential->Persist;
            Span<byte> native = new((void*)credential->CredentialBlob, checked((int)credential->CredentialBlobSize));
            blob = native.ToArray();
            native.Clear();
            return 0;
        }
        finally
        {
            CredFree(handle);
        }
    }

    /// <summary><c>CredDeleteW</c>; 0, or the Win32 error it set.</summary>
    [SupportedOSPlatform("windows")]
    internal static int DeleteCredential(string target) =>
        CredDelete(target, CredTypeGeneric, 0) ? 0 : Marshal.GetLastPInvokeError();

    // CREDENTIALW, with every pointer as nint so the struct stays blittable.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CredWrite(NativeCredential* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SupportedOSPlatform("windows")]
    private static partial void CredFree(nint buffer);
}
