using ClaudeCodeAccountRotation.App.Quota;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>
/// The hosted worker that runs passes off the request thread, and the one thing
/// it owes a caller besides running them: an honest answer about whether a pass
/// will actually run.
/// </summary>
public sealed class QuotaRefreshWorkerTests
{
    [Fact]
    public async Task ARefreshRequestedAfterShutdownIsRefused()
    {
        // The channel is the only thing that would notice: the run claim succeeds
        // whatever the host is doing. Without the writer being completed at
        // shutdown, a Refresh all arriving during it took the claim, wrote into a
        // channel nothing would read again, and was answered 202 for a pass that
        // never ran — leaving the state saying a pass was in progress for as long
        // as the process lived.
        using RefreshHarness harness = new();
        using QuotaRefreshWorker worker = new(harness.Engine, harness.State, NullLogger<QuotaRefreshWorker>.Instance);

        await worker.StopAsync(TestContext.Current.CancellationToken);

        worker.TryStart(RefreshRequest.All).ShouldBeFalse();
        harness.State.InProgress.ShouldBeFalse();
    }
}
