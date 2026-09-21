using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// The other side of this machine, from the Windows page: one switch control
/// and one line of side state.
/// <para>
/// Deliberately thin. The panel, the per-card chips and the tee figures for a
/// held account are phase 6's, and building any of them here would mean
/// building them twice.
/// </para>
/// </summary>
internal static class SideEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPost("/sides/{side}/accounts/{email}/switch", static async (
            string side,
            string email,
            WslSwitch coordinator,
            CancellationToken cancellationToken) =>
        {
            Result<AccountEmail, string> target = AccountEmail.Parse(email);
            if (target.IsFailure)
            {
                return Results.BadRequest(new { error = target.Error });
            }

            Result<WslSwitchOutcome, SwitchRefusal> outcome = await coordinator.SwitchToAsync(new SideName(side), target.Value, cancellationToken);
            return outcome.Match(
                static done => Results.Ok(new SideSwitchView(done.Side.Value, done.Now.Value, done.ParkedAs?.Value, done.At)),
                static refusal => Results.Json(new SwitchRefusalView(refusal.ToString(), Describe(refusal)), statusCode: StatusCodes.Status409Conflict));
        });

        mutations.MapPost("/sides/{side}/start", static async (string side, WslSwitch coordinator, CancellationToken cancellationToken) =>
        {
            Result<Unit, string> started = await coordinator.StartSideAsync(new SideName(side), cancellationToken);
            return started.Match(
                static _ => Results.Ok(new { started = true }),
                static reason => Results.Json(new { error = reason }, statusCode: StatusCodes.Status409Conflict));
        });

        // What the page draws its one line per side from. Empty when peers[] is,
        // which is the lane's rollback: no side, no line, no control.
        routes.MapGet("/api/sides", static async (PeerRegistry peers, WslSwitch coordinator, CancellationToken cancellationToken) =>
        {
            _ = await coordinator.ReconcileAsync(cancellationToken);
            List<SideLineView> lines = [];
            foreach (Peer peer in peers.All)
            {
                WslSideState state = await coordinator.ReadSideAsync(peer.Side, cancellationToken);
                lines.Add(new SideLineView(state.Side.Value, state.Online, state.LiveAccount?.Value, state.Detail, CanStart: peer.Host is not null));
            }

            return Results.Ok(lines);
        });

        routes.MapGet("/api/sides/{side}", static async (string side, WslSwitch coordinator, CancellationToken cancellationToken) =>
        {
            _ = await coordinator.ReconcileAsync(cancellationToken);
            WslSideState state = await coordinator.ReadSideAsync(new SideName(side), cancellationToken);
            return Results.Ok(new SideStateView(state.Side.Value, state.Online, state.LiveAccount?.Value, state.Detail));
        });
    }

    /// <summary>
    /// The two refusals this phase adds sentences for. The rest fall through
    /// to <see cref="SwitchEndpoints"/>'s vocabulary, which #71 owns; the 409
    /// body's <c>refusal</c> field is the enum name either way.
    /// </summary>
    private static string Describe(SwitchRefusal refusal) => refusal switch
    {
        SwitchRefusal.SideOffline => "That side is not answering, so nothing can be asked to take the pair. Start it and try again.",
        SwitchRefusal.ExportNotVerified => "The pair that side exported did not read back on this volume as the pair it named, so the switch was refused and nothing was swapped.",
        SwitchRefusal.PeerDidNotImport => "That side did not complete the import, so the account has been put back in its slot.",
        _ => refusal.ToString(),
    };

    internal sealed record SideSwitchView(string Side, string Now, string? ParkedAs, DateTimeOffset At);

    internal sealed record SideStateView(string Side, bool Online, string? LiveAccount, string Detail);

    internal sealed record SideLineView(string Side, bool Online, string? LiveAccount, string Detail, bool CanStart);
}
