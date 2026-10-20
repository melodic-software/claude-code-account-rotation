using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Adapters.SecretStore;

/// <summary>
/// The freedesktop Secret Service through libsecret's
/// <c>secret_password_*v_sync</c> calls (the non-variadic forms), one item
/// per key under this tool's schema in the default collection.
/// <para>
/// It refuses rather than degrades: with no libsecret, no session bus, no
/// provider on it, or a locked collection, a key is not kept at all. libsecret
/// itself switches to its own file backend inside a flatpak or snap, or when
/// <c>SECRET_BACKEND</c> names another backend, so those are refused before it
/// is called. A probe stores, reads back and clears an item of its own, since
/// a provider that is present can still refuse to store.
/// </para>
/// <para>
/// Every buffer this class fills with a key is zeroed before it is freed, and
/// a key libsecret returns is released with <c>secret_password_free</c>, which
/// clears it. A call that does not answer within its bound is cancelled.
/// </para>
/// </summary>
internal sealed class LibsecretStore(string libraryName, Func<string, string?> environment, Func<string, bool> pathExists, TimeSpan callBound) : ISecretStore
{
    public const string DefaultLibrary = "libsecret-1.so.0";
    public const string NotInstalled = "libsecret is not installed";
    public const string NoProvider = "no Secret Service provider";
    public const string Locked = "the Secret Service collection is locked; unlock it and try again";

    private const string SchemaName = "com.melodic-software.ClaudeCodeAccountRotation";
    private const string ProbeKind = "probe";

    private readonly Lazy<Result<Api, string>> _api = new(() => Api.Load(libraryName), LazyThreadSafetyMode.ExecutionAndPublication);

    public static LibsecretStore ForCurrentProcess() =>
        new(DefaultLibrary, Environment.GetEnvironmentVariable, Path.Exists, TimeSpan.FromSeconds(15));

    public async Task<Result<Unit, string>> ProbeAsync(CancellationToken cancellationToken)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return await RunAsync(
            (api, cancellable) =>
            {
                Result<Unit, string> stored = api.Store(ProbeKind, ProbeKind, SecretNames.Product + ":probe", token, cancellable);
                if (stored.IsFailure)
                {
                    return stored;
                }

                Result<string?, string> read = api.Lookup(ProbeKind, ProbeKind, cancellable);
                Result<bool, string> cleared = api.Clear(ProbeKind, ProbeKind, cancellable);
                return read.IsFailure ? Result<Unit, string>.Failure(read.Error)
                    : read.Value != token ? Result<Unit, string>.Failure("the Secret Service did not return the item it was given")
                    : cleared.IsFailure ? Result<Unit, string>.Failure(cleared.Error)
                    : !cleared.Value ? Result<Unit, string>.Failure("the Secret Service did not remove the item it was given")
                    : Result<Unit, string>.Success(Unit.Value);
            },
            cancellationToken);
    }

    public Task<Result<Unit, string>> WriteAsync(SecretKind kind, AccountEmail email, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        return RunAsync(
            (api, cancellable) => api.Store(SecretNames.Kind(kind), email.Value, SecretNames.Target(kind, email), value, cancellable),
            cancellationToken);
    }

    public Task<Result<string?, string>> ReadAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken) =>
        RunAsync((api, cancellable) => api.Lookup(SecretNames.Kind(kind), email.Value, cancellable), cancellationToken);

    public async Task<Result<Unit, string>> DeleteAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken)
    {
        // Removed or nothing matched are both success; any failure, including a
        // store this process cannot reach, is reported: the key may still be there.
        Result<bool, string> cleared = await RunAsync(
            (api, cancellable) => api.Clear(SecretNames.Kind(kind), email.Value, cancellable),
            cancellationToken);
        return cleared.IsSuccess ? Result<Unit, string>.Success(Unit.Value) : Result<Unit, string>.Failure(cleared.Error);
    }

    /// <summary>
    /// Refuses what must not reach libsecret, then runs <paramref name="call"/>
    /// on its own thread with a cancellable the bound and the request both cancel.
    /// </summary>
    private async Task<Result<T, string>> RunAsync<T>(Func<Api, nint, Result<T, string>> call, CancellationToken cancellationToken)
    {
        if (FileBackend() is string fileBackend)
        {
            return Result<T, string>.Failure(fileBackend);
        }

        Result<Api, string> loaded = _api.Value;
        if (loaded.IsFailure)
        {
            return Result<T, string>.Failure(loaded.Error);
        }

        if (!HasSessionBus())
        {
            return Result<T, string>.Failure(NoProvider + " (this session has no D-Bus session bus)");
        }

        Api api = loaded.Value;
        nint cancellable = api.NewCancellable();
        Task<Result<T, string>> work = Task.Factory.StartNew(
            () => call(api, cancellable),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Result<T, string> result;
        try
        {
            result = await work.WaitAsync(callBound, cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // Cancelled before the release is queued, so the cancellable is
            // never touched after the call that holds it has freed it.
            api.Cancel(cancellable);
            _ = work.ContinueWith(_ => api.Unref(cancellable), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return Result<T, string>.Failure(
                "the Secret Service did not answer within " + callBound.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                + " s, so the change may still complete; its collection may be locked and waiting for an unlock prompt");
        }

        api.Unref(cancellable);
        return result;
    }

    // libsecret's own backend choice: a flatpak or snap with the secret portal,
    // or SECRET_BACKEND naming anything but "service", selects its file backend.
    private string? FileBackend()
    {
        if (pathExists("/.flatpak-info") || !string.IsNullOrEmpty(environment("SNAP_NAME")))
        {
            return "libsecret would keep the key in a file inside this flatpak or snap, not in a Secret Service provider";
        }

        string? backend = environment("SECRET_BACKEND");
        return string.IsNullOrEmpty(backend) || backend == "service"
            ? null
            : "SECRET_BACKEND=" + backend + " points libsecret away from the Secret Service";
    }

    // GLib's own lookup for the session bus, less X11 autolaunch: the
    // address variable, then the per-user socket.
    private bool HasSessionBus() =>
        !string.IsNullOrEmpty(environment("DBUS_SESSION_BUS_ADDRESS"))
        || (environment("XDG_RUNTIME_DIR") is string runtime && runtime.Length > 0 && pathExists(Path.Combine(runtime, "bus")));

    /// <summary>
    /// Names a GError for the page. Only a failure to reach a provider on the
    /// bus reads as "no Secret Service provider"; every other error, including
    /// G_IO_ERROR_DBUS_ERROR, is the provider refusing.
    /// </summary>
    internal static string Describe(string domain, int code, string message) => domain switch
    {
        // gio/gioenums.h GDBusError: SERVICE_UNKNOWN = 2, NAME_HAS_NO_OWNER = 3, NO_SERVER = 11.
        "g-dbus-error-quark" when code is 2 or 3 or 11 => NoProvider + " (" + message + ")",
        // gio/gioenums.h GIOErrorEnum: NOT_FOUND = 1 (no bus socket), CONNECTION_REFUSED = 39.
        "g-io-error-quark" when code is 1 or 39 => NoProvider + " (" + message + ")",
        // libsecret/secret-util.c registers its domain as "secret-error";
        // secret-types.h SECRET_ERROR_IS_LOCKED = 2.
        "secret-error" when code == 2 => Locked,
        _ => "the Secret Service refused (" + message + ")",
    };

    private unsafe delegate T NativeCall<T>(nint schema, nint attributes, nint* error);

    /// <summary>The libsecret, GLib and GIO entry points, resolved once.</summary>
    private sealed unsafe class Api
    {
        private const string GLib = "libglib-2.0.so.0";
        private const string Gio = "libgio-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";

        private delegate* unmanaged<byte*, int, nint, nint> _schemaNew;
        private delegate* unmanaged<nint, void> _schemaUnref;
        private delegate* unmanaged<nint, nint, byte*, byte*, byte*, nint, nint*, int> _store;
        private delegate* unmanaged<nint, nint, nint, nint*, byte*> _lookup;
        private delegate* unmanaged<nint, nint, nint, nint*, int> _clear;
        private delegate* unmanaged<byte*, void> _passwordFree;
        private delegate* unmanaged<nint, nint, nint> _hashTableNew;
        private delegate* unmanaged<nint, nint, nint, int> _hashTableInsert;
        private delegate* unmanaged<nint, void> _hashTableUnref;
        private nint _strHash;
        private nint _strEqual;
        private delegate* unmanaged<nint, void> _errorFree;
        private delegate* unmanaged<uint, byte*> _quarkToString;
        private delegate* unmanaged<nint> _cancellableNew;
        private delegate* unmanaged<nint, void> _cancellableCancel;
        private delegate* unmanaged<nint, void> _objectUnref;

        public static Result<Api, string> Load(string libraryName)
        {
            try
            {
                nint secret = NativeLibrary.Load(libraryName);
                nint glib = NativeLibrary.Load(GLib);
                nint gio = NativeLibrary.Load(Gio);
                nint gobject = NativeLibrary.Load(GObject);
                return Result<Api, string>.Success(new Api
                {
                    _schemaNew = (delegate* unmanaged<byte*, int, nint, nint>)NativeLibrary.GetExport(secret, "secret_schema_newv"),
                    _schemaUnref = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(secret, "secret_schema_unref"),
                    _store = (delegate* unmanaged<nint, nint, byte*, byte*, byte*, nint, nint*, int>)NativeLibrary.GetExport(secret, "secret_password_storev_sync"),
                    _lookup = (delegate* unmanaged<nint, nint, nint, nint*, byte*>)NativeLibrary.GetExport(secret, "secret_password_lookupv_sync"),
                    _clear = (delegate* unmanaged<nint, nint, nint, nint*, int>)NativeLibrary.GetExport(secret, "secret_password_clearv_sync"),
                    _passwordFree = (delegate* unmanaged<byte*, void>)NativeLibrary.GetExport(secret, "secret_password_free"),
                    _hashTableNew = (delegate* unmanaged<nint, nint, nint>)NativeLibrary.GetExport(glib, "g_hash_table_new"),
                    _hashTableInsert = (delegate* unmanaged<nint, nint, nint, int>)NativeLibrary.GetExport(glib, "g_hash_table_insert"),
                    _hashTableUnref = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(glib, "g_hash_table_unref"),
                    _strHash = NativeLibrary.GetExport(glib, "g_str_hash"),
                    _strEqual = NativeLibrary.GetExport(glib, "g_str_equal"),
                    _errorFree = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(glib, "g_error_free"),
                    _quarkToString = (delegate* unmanaged<uint, byte*>)NativeLibrary.GetExport(glib, "g_quark_to_string"),
                    _cancellableNew = (delegate* unmanaged<nint>)NativeLibrary.GetExport(gio, "g_cancellable_new"),
                    _cancellableCancel = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(gio, "g_cancellable_cancel"),
                    _objectUnref = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(gobject, "g_object_unref"),
                });
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
            {
                return Result<Api, string>.Failure(NotInstalled + " (" + libraryName + " could not be loaded)");
            }
            catch (EntryPointNotFoundException exception)
            {
                return Result<Api, string>.Failure(NotInstalled + " in a version this tool can use (" + exception.Message + ")");
            }
        }

        public nint NewCancellable() => _cancellableNew();

        public void Cancel(nint cancellable) => _cancellableCancel(cancellable);

        public void Unref(nint cancellable) => _objectUnref(cancellable);

        public Result<Unit, string> Store(string kind, string email, string label, string password, nint cancellable)
        {
            int size = Encoding.UTF8.GetByteCount(password);
            byte* secret = (byte*)NativeMemory.AllocZeroed((nuint)size + 1);
            try
            {
                Encoding.UTF8.GetBytes(password, new Span<byte>(secret, size));
                nint secretAddress = (nint)secret;
                Result<bool, string> stored = With(kind, email, (schema, attributes, error) =>
                {
                    using Utf8 labelText = new(label);
                    return _store(schema, attributes, null, labelText.Pointer, (byte*)secretAddress, cancellable, error) != 0;
                });
                // FALSE with no GError is still a store that did not happen.
                return stored.IsFailure ? Result<Unit, string>.Failure(stored.Error)
                    : stored.Value ? Result<Unit, string>.Success(Unit.Value)
                    : Result<Unit, string>.Failure("the Secret Service did not store the key and gave no reason");
            }
            finally
            {
                NativeMemory.Clear(secret, (nuint)size);
                NativeMemory.Free(secret);
            }
        }

        public Result<string?, string> Lookup(string kind, string email, nint cancellable) =>
            With<string?>(kind, email, (schema, attributes, error) =>
            {
                byte* found = _lookup(schema, attributes, cancellable, error);
                if (found == null)
                {
                    return null;
                }

                try
                {
                    return Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(found));
                }
                finally
                {
                    _passwordFree(found);
                }
            });

        /// <summary>
        /// True when an item was removed. libsecret documents the return as
        /// "whether any passwords were removed", so FALSE with no GError means
        /// nothing matched.
        /// </summary>
        public Result<bool, string> Clear(string kind, string email, nint cancellable) =>
            With(kind, email, (schema, attributes, error) => _clear(schema, attributes, cancellable, error) != 0);

        /// <summary>
        /// Builds the schema and the two attributes, runs <paramref name="call"/>,
        /// turns any GError it set into a refusal, and frees all of it.
        /// </summary>
        private Result<T, string> With<T>(string kind, string email, NativeCall<T> call)
        {
            using Utf8 schemaName = new(SchemaName);
            using Utf8 kindName = new("kind");
            using Utf8 emailName = new("email");
            using Utf8 kindValue = new(kind);
            using Utf8 emailValue = new(email);
            nint types = _hashTableNew(_strHash, _strEqual);
            nint attributes = _hashTableNew(_strHash, _strEqual);
            nint schema = 0;
            try
            {
                // SECRET_SCHEMA_ATTRIBUTE_STRING is 0, so each type is a null pointer.
                _hashTableInsert(types, (nint)kindName.Pointer, 0);
                _hashTableInsert(types, (nint)emailName.Pointer, 0);
                schema = _schemaNew(schemaName.Pointer, 0, types);
                _hashTableInsert(attributes, (nint)kindName.Pointer, (nint)kindValue.Pointer);
                _hashTableInsert(attributes, (nint)emailName.Pointer, (nint)emailValue.Pointer);
                nint error = 0;
                T value = call(schema, attributes, &error);
                if (error == 0)
                {
                    return Result<T, string>.Success(value);
                }

                // GError: GQuark domain, gint code, gchar *message.
                string described = Describe(
                    Marshal.PtrToStringUTF8((nint)_quarkToString(*(uint*)error)) ?? string.Empty,
                    *(int*)(error + 4),
                    Marshal.PtrToStringUTF8(*(nint*)(error + 8)) ?? string.Empty);
                _errorFree(error);
                return Result<T, string>.Failure(described);
            }
            finally
            {
                if (schema != 0)
                {
                    _schemaUnref(schema);
                }

                _hashTableUnref(attributes);
                _hashTableUnref(types);
            }
        }
    }

    /// <summary>A NUL-terminated UTF-8 copy of a string that is not a secret, freed on dispose.</summary>
    private readonly unsafe struct Utf8 : IDisposable
    {
        public Utf8(string text) => Pointer = (byte*)Marshal.StringToCoTaskMemUTF8(text);

        public byte* Pointer { get; }

        public void Dispose() => Marshal.FreeCoTaskMem((nint)Pointer);
    }
}
