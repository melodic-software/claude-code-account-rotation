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
/// The other side of this machine, from the Windows page: its switch control,
/// its start control, and one line of side state per configured side.
/// <para>
/// The per-card chips and the figures for a held account are the dashboard's,
/// built from the same <see cref="WslSwitch.ReadSidesAsync"/> read these lines
/// come from, so a card and a side line never disagree about whether a side is
/// up.
/// </para>
/// </summary>
internal static class SideEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        // `quarantineForeignFamily` is the operator's second click after a
        // ForeignFamily refusal, and it is a query flag rather than a second
        // route because it is the same switch: the only thing it changes is
        // that the pair coming back is quarantined instead of refusing to move.
        mutations.MapPost("/sides/{side}/accounts/{email}/switch", static async (
            string side,
            string email,
            // Nullable for the reason the login's own flag is: a minimal-API
            // `bool` bound from the query string is required, and the ordinary
            // switch sends no query at all.
            bool? quarantineForeignFamily,
            WslSwitch coordinator,
            CancellationToken cancellationToken) =>
        {
            Result<AccountEmail, string> target = AccountEmail.Parse(email);
            if (target.IsFailure)
            {
                return Results.BadRequest(new { error = target.Error });
            }

            Result<WslSwitchOutcome, SwitchRefusal> outcome =
                await coordinator.SwitchToAsync(new SideName(side), target.Value, quarantineForeignFamily == true, cancellationToken);
            return View(outcome);
        });

        // The park-back. It names no account because it has no target: the side
        // ends holding nothing and the pair it held is in the store, where
        // either side may take it. A switch to a sentinel account would put a
        // non-account in the segment `AccountEmail.Parse` owns and would have to
        // suppress every refusal keyed on a target — `AlreadyOnTarget`,
        // `TargetHasNoCredentials`, `TargetLoginExpired`,
        // `TargetStrandedInRecovery`, `HeldByOtherSide` — none of which a
        // release can raise. The vocabulary already has side verbs with no
        // account in the path (`/start`, `/transit/cancel`); this is one more.
        mutations.MapPost("/sides/{side}/release", static async (
            string side,
            // The same second click as the switch's, and the same one refusal it
            // overrides: a family the store no longer owns comes back to the
            // quarantine rather than over the fresh one.
            bool? quarantineForeignFamily,
            WslSwitch coordinator,
            CancellationToken cancellationToken) =>
                View(await coordinator.ReleaseAsync(new SideName(side), quarantineForeignFamily == true, cancellationToken)));

        // The operator's Cancel on a hand-off that is standing. A refusal is a
        // 409 with the reason, because every reason it has is a state on the
        // other side rather than a bad request.
        mutations.MapPost("/sides/transit/cancel", static async (WslSwitch coordinator, CancellationToken cancellationToken) =>
        {
            Result<string, string> cancelled = await coordinator.CancelAsync(cancellationToken);
            return cancelled.Match(
                static said => Results.Ok(new { cancelled = true, detail = said }),
                static reason => Results.Json(new SwitchRefusalView("CancelUnavailable", reason), statusCode: StatusCodes.Status409Conflict));
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
        routes.MapGet("/api/sides", static async (WslSwitch coordinator, CancellationToken cancellationToken) =>
        {
            _ = await coordinator.ReconcileAsync(cancellationToken);
            return Results.Ok((await coordinator.ReadSidesAsync(cancellationToken))
                .Select(static state => new SideLineView(state.Side.Value, state.Online, state.LiveAccount?.Value, state.Detail, state.CanStart))
                .ToList());
        });

        routes.MapGet("/api/sides/{side}", static async (string side, WslSwitch coordinator, CancellationToken cancellationToken) =>
        {
            _ = await coordinator.ReconcileAsync(cancellationToken);
            WslSideState state = await coordinator.ReadSideAsync(new SideName(side), cancellationToken);
            return Results.Ok(new SideStateView(state.Side.Value, state.Online, state.LiveAccount?.Value, state.Detail));
        });
    }

    /// <summary>One answer for both directions: what moved, or the refusal's sentence.</summary>
    private static IResult View(Result<WslSwitchOutcome, SwitchRefusal> outcome) => outcome.Match(
        static done => Results.Ok(new SideSwitchView(done.Side.Value, done.Now?.Value, done.ParkedAs?.Value, done.At, done.QuarantinedAt)),
        static refusal => Results.Json(SwitchRefusalView.Of(refusal), statusCode: StatusCodes.Status409Conflict));

    /// <summary>
    /// What a hand-off moved. <c>Now</c> is null after a release, which leaves
    /// that side holding nothing. <c>QuarantinedAt</c> names a file instead of
    /// an account when the pair that came back was a superseded family the
    /// store may not hold, and <c>ParkedAs</c> is then null.
    /// </summary>
    internal sealed record SideSwitchView(string Side, string? Now, string? ParkedAs, DateTimeOffset At, string? QuarantinedAt = null);

    internal sealed record SideStateView(string Side, bool Online, string? LiveAccount, string Detail);

    internal sealed record SideLineView(string Side, bool Online, string? LiveAccount, string Detail, bool CanStart);
}
