namespace ClaudeCodeAccountRotation.App.Switching;

/// <summary>
/// The one in-process gate every credential-touching operation (switch,
/// refresh write-back, login admission, login completion, remove) acquires
/// with a timeout and releases in <c>finally</c>. A second concurrent mutation
/// is refused, not queued behind a moved file.
/// <para>
/// A login is the one operation the gate cannot cover end to end: its child
/// writes into the folder minutes later, and holding the gate for a ten-minute
/// window would refuse every switch for its duration. So a login takes the
/// gate twice, at admission and at completion, and registers itself between
/// them; everything that would move, delete, or log out what is in a folder
/// asks <see cref="ClaudeCodeAccountRotation.Core.Ports.ILoginSessionRunner.IsRunningAgainst"/>
/// while holding this gate and refuses rather than racing the child.
/// </para>
/// </summary>
internal sealed class CredentialMutationGate : IDisposable
{
    private readonly SemaphoreSlim _permit = new(1, 1);

    public async Task<IDisposable> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await _permit.WaitAsync(timeout, cancellationToken))
        {
            throw new TimeoutException("another credential mutation is in progress; try again in a moment");
        }

        return new Permit(_permit);
    }

    public void Dispose() => _permit.Dispose();

    private sealed class Permit(SemaphoreSlim permit) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            try
            {
                permit.Release();
            }
            catch (ObjectDisposedException)
            {
                // The container disposed the gate at shutdown before this request's finally
                // block ran; there is nothing left to release into.
            }
        }
    }
}
