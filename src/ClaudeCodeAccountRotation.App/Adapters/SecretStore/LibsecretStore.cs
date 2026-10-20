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

    private const string SchemaName = "com.melodic-software.ClaudeCodeAccountRotation";
    private const string ProbeKind = "probe";

    private readonly Lazy<Result<Api, Refusal>> _api = new(() => Api.Load(libraryName), LazyThreadSafetyMode.ExecutionAndPublication);

    public static LibsecretStore ForCurrentProcess() =>
        new(DefaultLibrary, Environment.GetEnvironmentVariable, Path.Exists, TimeSpan.FromSeconds(15));

    public async Task<Result<Unit, string>> ProbeAsync(CancellationToken cancellationToken)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Result<Unit, Refusal> probed = await RunAsync(
            (api, cancellable) =>
            {
                Result<Unit, Refusal> stored = api.Store(ProbeKind, ProbeKind, SecretNames.Product + ":probe", token, cancellable);
                if (stored.IsFailure)
                {
                    return stored;
                }

                Result<string?, Refusal> read = api.Lookup(ProbeKind, ProbeKind, cancellable);
                Result<Unit, Refusal> cleared = api.Clear(ProbeKind, ProbeKind, cancellable);
                return read.IsFailure ? Result<Unit, Refusal>.Failure(read.Error)
                    : read.Value != token ? Result<Unit, Refusal>.Failure(new Refusal("the Secret Service did not return the item it was given", Unreachable: false))
                    : cleared;
            },
            cancellationToken);
        return probed.IsSuccess ? Result<Unit, string>.Success(Unit.Value) : Result<Unit, string>.Failure(probed.Error.Cause);
    }

    public async Task<Result<Unit, string>> WriteAsync(SecretKind kind, AccountEmail email, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        Result<Unit, Refusal> stored = await RunAsync(
            (api, cancellable) => api.Store(SecretNames.Kind(kind), email.Value, SecretNames.Target(kind, email), value, cancellable),
            cancellationToken);
        return stored.IsSuccess ? Result<Unit, string>.Success(Unit.Value) : Result<Unit, string>.Failure(stored.Error.Cause);
    }

    public async Task<Result<string?, string>> ReadAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken)
    {
        Result<string?, Refusal> read = await RunAsync(
            (api, cancellable) => api.Lookup(SecretNames.Kind(kind), email.Value, cancellable),
            cancellationToken);
        return read.IsSuccess ? Result<string?, string>.Success(read.Value) : Result<string?, string>.Failure(read.Error.Cause);
    }

    public async Task<Result<Unit, string>> DeleteAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken)
    {
        Result<Unit, Refusal> cleared = await RunAsync(
            (api, cancellable) => api.Clear(SecretNames.Kind(kind), email.Value, cancellable),
            cancellationToken);
        // A store this process cannot reach holds nothing it wrote.
        return cleared.IsSuccess || cleared.Error.Unreachable
            ? Result<Unit, string>.Success(Unit.Value)
            : Result<Unit, string>.Failure(cleared.Error.Cause);
    }

    /// <summary>
    /// Refuses what must not reach libsecret, then runs <paramref name="call"/>
    /// on its own thread with a cancellable the bound and the request both cancel.
    /// </summary>
    private async Task<Result<T, Refusal>> RunAsync<T>(Func<Api, nint, Result<T, Refusal>> call, CancellationToken cancellationToken)
    {
        if (FileBackend() is string fileBackend)
        {
            return Result<T, Refusal>.Failure(new Refusal(fileBackend, Unreachable: true));
        }

        Result<Api, Refusal> loaded = _api.Value;
        if (loaded.IsFailure)
        {
            return Result<T, Refusal>.Failure(loaded.Error);
        }

        if (!HasSessionBus())
        {
            return Result<T, Refusal>.Failure(new Refusal(NoProvider + " (this session has no D-Bus session bus)", Unreachable: true));
        }

        Api api = loaded.Value;
        nint cancellable = api.NewCancellable();
        Task<Result<T, Refusal>> work = Task.Factory.StartNew(
            () => call(api, cancellable),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Result<T, Refusal> result;
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
            return Result<T, Refusal>.Failure(new Refusal(
                "the Secret Service did not answer within " + callBound.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " s; its collection may be locked and waiting for an unlock prompt",
                Unreachable: false));
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

    /// <summary>Why a call did not happen, and whether that is because there is no store to reach.</summary>
    private sealed record Refusal(string Cause, bool Unreachable);

    private unsafe delegate T NativeCall<T>(nint schema, nint attributes, nint* error);

    /// <summary>The libsecret, GLib and GIO entry points, resolved once.</summary>
    private sealed unsafe class Api
    {
        private const string GLib = "libglib-2.0.so.0";
        private const string Gio = "libgio-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";

        // GDBusError: SERVICE_UNKNOWN, NAME_HAS_NO_OWNER, NO_SERVER.
        private static readonly int[] _noProviderCodes = [2, 3, 11];
        private const int SecretErrorIsLocked = 2;
        private const int GioErrorCancelled = 19;

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

        public static Result<Api, Refusal> Load(string libraryName)
        {
            try
            {
                nint secret = NativeLibrary.Load(libraryName);
                nint glib = NativeLibrary.Load(GLib);
                nint gio = NativeLibrary.Load(Gio);
                nint gobject = NativeLibrary.Load(GObject);
                return Result<Api, Refusal>.Success(new Api
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
                return Result<Api, Refusal>.Failure(new Refusal(NotInstalled + " (" + libraryName + " could not be loaded)", Unreachable: true));
            }
            catch (EntryPointNotFoundException exception)
            {
                return Result<Api, Refusal>.Failure(new Refusal(NotInstalled + " in a version this tool can use (" + exception.Message + ")", Unreachable: true));
            }
        }

        public nint NewCancellable() => _cancellableNew();

        public void Cancel(nint cancellable) => _cancellableCancel(cancellable);

        public void Unref(nint cancellable) => _objectUnref(cancellable);

        public Result<Unit, Refusal> Store(string kind, string email, string label, string password, nint cancellable)
        {
            int size = Encoding.UTF8.GetByteCount(password);
            byte* secret = (byte*)NativeMemory.AllocZeroed((nuint)size + 1);
            try
            {
                Encoding.UTF8.GetBytes(password, new Span<byte>(secret, size));
                nint secretAddress = (nint)secret;
                return With(kind, email, (schema, attributes, error) =>
                {
                    using Utf8 labelText = new(label);
                    _store(schema, attributes, null, labelText.Pointer, (byte*)secretAddress, cancellable, error);
                    return Unit.Value;
                });
            }
            finally
            {
                NativeMemory.Clear(secret, (nuint)size);
                NativeMemory.Free(secret);
            }
        }

        public Result<string?, Refusal> Lookup(string kind, string email, nint cancellable) =>
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

        public Result<Unit, Refusal> Clear(string kind, string email, nint cancellable) =>
            With(kind, email, (schema, attributes, error) =>
            {
                // False with no error is "nothing matched", which is what a delete wants.
                _clear(schema, attributes, cancellable, error);
                return Unit.Value;
            });

        /// <summary>
        /// Builds the schema and the two attributes, runs <paramref name="call"/>,
        /// turns any GError it set into a refusal, and frees all of it.
        /// </summary>
        private Result<T, Refusal> With<T>(string kind, string email, NativeCall<T> call)
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
                    return Result<T, Refusal>.Success(value);
                }

                Refusal refusal = Classify(error);
                _errorFree(error);
                return Result<T, Refusal>.Failure(refusal);
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

        // GError: GQuark domain, gint code, gchar *message.
        private Refusal Classify(nint error)
        {
            uint domain = *(uint*)error;
            int code = *(int*)(error + 4);
            string message = Marshal.PtrToStringUTF8(*(nint*)(error + 8)) ?? string.Empty;
            string domainName = Marshal.PtrToStringUTF8((nint)_quarkToString(domain)) ?? string.Empty;
            return domainName switch
            {
                "g-dbus-error-quark" when _noProviderCodes.Contains(code) => new Refusal(NoProvider + " (" + message + ")", Unreachable: true),
                "g-io-error-quark" when code != GioErrorCancelled => new Refusal(NoProvider + " (" + message + ")", Unreachable: true),
                "secret-error-quark" when code == SecretErrorIsLocked => new Refusal("the Secret Service collection is locked; unlock it and try again", Unreachable: false),
                _ => new Refusal("the Secret Service refused (" + message + ")", Unreachable: false),
            };
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
