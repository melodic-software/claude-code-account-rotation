using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Accounts;

/// <summary>What the store holds: the catalog last read, when, and the last read's failure.</summary>
internal sealed record LaneCatalogSnapshot(LaneCatalog? Catalog, DateTimeOffset? ReadAt, string? Error);

/// <summary>
/// The lane catalog <c>laneCatalog</c> names, read through <c>gh api</c> on
/// a refresh or the first write that needs it, and held in memory from then
/// on. It decides which roles are keyed: the catalog when one is configured,
/// <see cref="CiRole.Keyed"/> when none is. A write never guesses past a
/// catalog it could not read.
/// </summary>
internal sealed class LaneCatalogStore(ClaudeCodeAccountRotationConfiguration configuration, IGhApiReader reader, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _read = new(1, 1);
    private LaneCatalogSnapshot _snapshot = new(null, null, null);

    public LaneCatalogSource? Source => configuration.LaneCatalog;

    public LaneCatalogSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Whether a role is keyed by what is held now, for the page: never a read.
    /// A catalog not read yet leaves <see cref="CiRole.Keyed"/> in charge.
    /// </summary>
    public bool IsKeyedNow(CiRole role) => KeyedBy(Snapshot.Catalog)(role);

    /// <summary>
    /// The keyed test a write uses: the catalog, read first when it has not
    /// been, or <see cref="CiRole.Keyed"/> when none is configured. A catalog
    /// that cannot be read is the failure.
    /// </summary>
    public async Task<Result<Func<CiRole, bool>, string>> KeyedForWriteAsync(CancellationToken cancellationToken)
    {
        if (Source is null)
        {
            return Result<Func<CiRole, bool>, string>.Success(KeyedBy(null));
        }

        // A catalog held from an earlier read does not outlive a read that
        // failed since: it stays for the page, and writes wait for a good read.
        LaneCatalogSnapshot held = Snapshot;
        if (held.Error is string failed)
        {
            return Result<Func<CiRole, bool>, string>.Failure("the lane catalog says which roles are keyed, and its last read failed: " + failed + ". Load it again once that is fixed.");
        }

        if (held.Catalog is LaneCatalog catalog)
        {
            return Result<Func<CiRole, bool>, string>.Success(KeyedBy(catalog));
        }

        Result<LaneCatalog, string> read = await RefreshAsync(cancellationToken);
        return read.IsSuccess
            ? Result<Func<CiRole, bool>, string>.Success(KeyedBy(read.Value))
            : Result<Func<CiRole, bool>, string>.Failure("the lane catalog says which roles are keyed, and it could not be read: " + read.Error);
    }

    /// <summary>Reads the catalog again, one read at a time, and holds what it got.</summary>
    public async Task<Result<LaneCatalog, string>> RefreshAsync(CancellationToken cancellationToken)
    {
        if (Source is not LaneCatalogSource source)
        {
            return Result<LaneCatalog, string>.Failure("no laneCatalog is configured");
        }

        await _read.WaitAsync(cancellationToken);
        try
        {
            Result<LaneCatalog, GhApiFailure> read = await reader.ReadLaneCatalogAsync(source, cancellationToken);
            LaneCatalogSnapshot held = Snapshot;
            Volatile.Write(ref _snapshot, read.IsSuccess
                ? new LaneCatalogSnapshot(read.Value, clock.GetUtcNow(), null)
                : held with { Error = read.Error.Message });
            return read.IsSuccess
                ? Result<LaneCatalog, string>.Success(read.Value)
                : Result<LaneCatalog, string>.Failure(read.Error.Message);
        }
        finally
        {
            _read.Release();
        }
    }

    public void Dispose() => _read.Dispose();

    /// <summary>The catalog's answer for a role it speaks for, else <see cref="CiRole.Keyed"/>.</summary>
    private static Func<CiRole, bool> KeyedBy(LaneCatalog? catalog) => role => catalog?.IsKeyed(role) ?? role.Keyed;
}
