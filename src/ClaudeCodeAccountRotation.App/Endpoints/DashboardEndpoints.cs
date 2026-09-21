using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClaudeCodeAccountRotation.App.Endpoints;

internal static class DashboardEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/api/dashboard", static async (
            DashboardAssembler assembler,
            WslSwitch coordinator,
            DashboardState state,
            CancellationToken cancellationToken) =>
        {
            // The leader's own crash table, on every poll. Startup is not enough:
            // the case the acceptance exercises most is a follower killed while
            // this process stays up, and nothing else would notice until a
            // restart. It costs one File.Exists when no hand-off is in flight,
            // and it steps aside when one is.
            // Its banner is what the page says about a claim the other side has
            // not answered for. Kept rather than discarded: this is the recovery
            // state the operator has least other evidence of, and a poll that
            // resolved it clears the line by writing null here.
            state.HandOffBanner = (await coordinator.ReconcileAsync(cancellationToken)).Banner;
            return Results.Ok(await assembler.AssembleAsync(cancellationToken));
        });

        // What the roster's profile picker is populated from. A read, so it takes
        // the read endpoints' shape: no mutation header and no same-origin filter,
        // both of which exist to stop a cross-site write, not a local read.
        routes.MapGet("/api/browser-profiles", static async (IBrowserProfileReader profiles, CancellationToken cancellationToken) =>
            Results.Ok((await profiles.ReadAsync(cancellationToken)).Select(View).ToList()));
    }

    private static BrowserProfileView View(BrowserProfile profile) => new(
        profile.Browser.ToString().ToLowerInvariant(),
        profile.Directory,
        profile.Name,
        profile.SignedInAs?.Value);
}
