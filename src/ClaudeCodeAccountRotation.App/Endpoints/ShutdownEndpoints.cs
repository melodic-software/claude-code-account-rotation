using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// <c>POST /api/shutdown</c> stops this process after the response is written.
/// Both roles map it. The follower is started by the leader over <c>wsl.exe</c>
/// and has no other stop path.
/// <para>
/// A switch or import that is in flight, or any other credential change holding
/// the mutation gate, is refused with 409 and the process keeps running. The
/// journals are only read. Startup reconciliation finishes or unwinds an open
/// one; shutdown never rewrites it. A login does not hold the gate for the
/// session, and this route does not ask whether one is running.
/// </para>
/// <para>
/// <c>POST /api/stop</c> is the leader's alone and stops every configured side
/// as well. The leader takes its gate and checks its journals first, so no
/// switch can start between the two stops, then sends each side its own
/// <c>/api/shutdown</c>. A side's 409 refuses the whole stop and the leader
/// keeps running. A side that does not answer has nothing to drain, so the
/// leader stops anyway.
/// </para>
/// </summary>
internal static class ShutdownEndpoints
{
    private const string SwitchOrImportInFlight = "a switch or import is in flight";
    private const string CredentialChangeInProgress = "another credential change is in progress";

    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();
        mutations.MapPost("/shutdown", static (
            HttpContext http,
            CredentialMutationGate gate,
            ClaudeCodeAccountRotationConfiguration configuration,
            IHostApplicationLifetime lifetime,
            CancellationToken cancellationToken) => StopAsync(http, gate, configuration, lifetime, peers: null, cancellationToken));
    }

    public static void MapStop(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();
        mutations.MapPost("/stop", static (
            HttpContext http,
            CredentialMutationGate gate,
            ClaudeCodeAccountRotationConfiguration configuration,
            IHostApplicationLifetime lifetime,
            PeerRegistry peers,
            CancellationToken cancellationToken) => StopAsync(http, gate, configuration, lifetime, peers.All, cancellationToken));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The permit is held across the response: OnCompleted stops the host and then releases it, so a new mutation cannot acquire the gate first. A refusal disposes it in this handler.")]
    private static async Task<IResult> StopAsync(
        HttpContext http,
        CredentialMutationGate gate,
        ClaudeCodeAccountRotationConfiguration configuration,
        IHostApplicationLifetime lifetime,
        IReadOnlyList<Peer>? peers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(lifetime);

        IDisposable permit;
        try
        {
            permit = await gate.AcquireAsync(TimeSpan.Zero, cancellationToken);
        }
        catch (TimeoutException)
        {
            return Results.Json(new { error = CredentialChangeInProgress }, statusCode: StatusCodes.Status409Conflict);
        }

        bool releaseInHandler = true;
        try
        {
            if (await JournalOpenAsync(configuration, http.RequestServices, cancellationToken))
            {
                return Results.Json(new { error = SwitchOrImportInFlight }, statusCode: StatusCodes.Status409Conflict);
            }

            List<SideStopView> sides = [];
            foreach (Peer peer in peers ?? [])
            {
                // Not the request's token: a tab closed mid-stop must not turn a
                // side that would have answered into one that did not.
                Result<string, string> answer = await peer.Instance.ShutdownAsync(CancellationToken.None);
                if (answer.IsFailure)
                {
                    return Results.Json(
                        new { error = "the " + peer.Side.Value + " side refused to stop: " + answer.Error },
                        statusCode: StatusCodes.Status409Conflict);
                }

                sides.Add(new SideStopView(peer.Side.Value, answer.Value));
            }

            // StopApplication inside the handler cancels the request before the
            // 200 is written. OnCompleted runs after that write. The permit stays
            // held until then, so a new mutation cannot take the gate first.
            http.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                permit.Dispose();
                return Task.CompletedTask;
            });
            releaseInHandler = false;
            return peers is null ? Results.Ok(new { stopped = true }) : Results.Ok(new { stopped = true, sides });
        }
        finally
        {
            if (releaseInHandler)
            {
                permit.Dispose();
            }
        }
    }

    /// <summary>What one side said to the leader's stop: <c>stopped</c>, or why it did not.</summary>
    internal sealed record SideStopView(string Side, string Detail);

    /// <summary>
    /// The journals this role registered. The follower has no switch journal,
    /// and the leader has no import journal; resolving the other would throw.
    /// </summary>
    private static async Task<bool> JournalOpenAsync(
        ClaudeCodeAccountRotationConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (configuration.Role == RotationRole.Follower)
        {
            return await services.GetRequiredService<ImportJournal>().ReadOpenAsync(cancellationToken) is not null;
        }

        if (await services.GetRequiredService<SwitchJournal>().ReadOpenAsync(cancellationToken) is not null)
        {
            return true;
        }

        return await services.GetRequiredService<WslSwitchJournal>().ReadOpenAsync(cancellationToken) is not null;
    }
}
