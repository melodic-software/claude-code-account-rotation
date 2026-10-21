namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// The one gate every <c>gh</c> write passes, whichever writer runs it: one
/// write at a time, each starting at least <see cref="Spacing"/> after the last
/// one ended. GitHub asks for requests to be serial and for writes to wait at
/// least a second between each. Basis:
/// https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api#pause-between-mutative-requests.
/// </summary>
internal sealed class GhWriteGate(TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _writes = new(1, 1);
    private long? _lastWriteEnded;

    /// <summary>Runs <paramref name="write"/> once the gate is free and the spacing has passed.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        await _writes.WaitAsync(cancellationToken);
        try
        {
            if (_lastWriteEnded is long ended && Spacing - clock.GetElapsedTime(ended) is { Ticks: > 0 } wait)
            {
                await Task.Delay(wait, clock, cancellationToken);
            }

            return await write();
        }
        finally
        {
            _lastWriteEnded = clock.GetTimestamp();
            _writes.Release();
        }
    }

    public void Dispose() => _writes.Dispose();
}
