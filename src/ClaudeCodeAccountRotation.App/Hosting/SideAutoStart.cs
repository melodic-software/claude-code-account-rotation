using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Hosting;

/// <summary>
/// Starts every side that has a launch configured, once, when the leader
/// starts. That is the page's <c>Start WSL side</c> run for the operator, so a
/// reboot, the logon task or a release upgrade brings both sides up with no
/// click. It starts a process and nothing else: no account moves, so every
/// switch is still a human click.
/// <para>
/// It is registered after <see cref="StartupReconciliation"/>, and hosted
/// services start in order, so a hand-off the last leader died in is settled
/// by its own rules before a follower can answer. A start that fails is
/// logged and the host comes up anyway: the side reads offline and the page's
/// button is still there. A follower that is already running is left alone,
/// because its <c>follower.lock</c> refuses a second one.
/// </para>
/// </summary>
internal sealed partial class SideAutoStart(PeerRegistry peers, ILogger<SideAutoStart> logger) : IHostedService
{
    private readonly ILogger<SideAutoStart> _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (Peer peer in peers.All)
        {
            if (peer.Host is null)
            {
                continue;
            }

            Result<Unit, string> started = await peer.Host.StartAsync(cancellationToken);
            if (started.IsFailure)
            {
                LogAutoStartFailed(peer.Side.Value, started.Error);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "the {Side} side was not started with the leader: {Reason}")]
    private partial void LogAutoStartFailed(string side, string reason);
}
