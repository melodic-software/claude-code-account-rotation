using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClaudeCodeAccountRotation.App.Endpoints;

internal static class SwitchEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        // Read by the page when Switch is clicked, not on every poll: one
        // schtasks.exe per click rather than one every ten seconds. A running
        // task is what makes the page ask before switching.
        routes.MapGet("/api/remote-control", static async (IRemoteControlTask remoteControl, CancellationToken cancellationToken) =>
            Results.Ok(new RemoteControlView(remoteControl.Name, await remoteControl.IsRunningAsync(cancellationToken))));

        mutations.MapPost("/accounts/{email}/switch", static async (
            string email,
            LiveDirectorySwitch executor,
            RateLimitGuardTeeFileReader tee,
            DashboardState state,
            QuotaRefreshWorker worker,
            IRemoteControlTask remoteControl,
            CancellationToken cancellationToken) =>
        {
            Result<AccountEmail, string> target = AccountEmail.Parse(email);
            if (target.IsFailure)
            {
                return Results.BadRequest(new { error = target.Error });
            }

            await worker.YieldAsync(cancellationToken);

            // Read before the switch: Claude Code may end the session itself once
            // the account changes, and a task the operator had stopped is not
            // started on their behalf.
            bool remoteControlWasRunning = await remoteControl.IsRunningAsync(cancellationToken);

            // Remember the outgoing account's windows before the swap. A session
            // mid-turn at switch time writes them back under the incoming
            // account's name, and the dashboard must not read them as its own.
            StatuslineSnapshot? before = await tee.ReadAsync(cancellationToken);
            Result<SwitchOutcome, SwitchRefusal> outcome = await executor.SwitchToAsync(target.Value, cancellationToken);
            if (outcome.IsSuccess)
            {
                // Assigned on every success, including when the tee could not be
                // read: leaving an earlier switch's values in place would let them
                // disown a later account's own snapshot whose reset times happened
                // to match.
                PreSwitchWindows? windows = before is null
                    ? null
                    : new PreSwitchWindows(before.FiveHourResetsAt, before.SevenDayResetsAt);
                state.Publish(current => current with { PreSwitchWindows = windows });
            }

            if (outcome.IsFailure)
            {
                return Results.Json(SwitchRefusalView.Of(outcome.Error), statusCode: StatusCodes.Status409Conflict);
            }

            SwitchOutcome done = outcome.Value;
            (bool restarted, string? warning) = remoteControlWasRunning
                ? await RestartRemoteControlAsync(remoteControl, done)
                : (false, null);
            return Results.Ok(new SwitchOutcomeView(
                done.Now.Value,
                done.ParkedAs?.Value,
                done.CliVerification.IsSuccess ? done.CliVerification.Value.Email : null,
                done.CliVerification.IsFailure ? done.CliVerification.Error : null,
                done.IdentityMismatchWarning,
                done.At,
                restarted,
                warning));
        });
    }

    /// <summary>
    /// Restarts Remote Control under the incoming account, after the switch has
    /// released its gate and only once the CLI reports that account: a restart
    /// on any other answer would bind Remote Control to the wrong login. Never
    /// fails the switch, which is already committed; a restart that cannot run
    /// comes back as a warning with the commands that do it by hand. The
    /// request's token is not consulted, for the switch's own reason: an aborted
    /// request must not leave the task ended and not started again.
    /// </summary>
    private static async Task<(bool Restarted, string? Warning)> RestartRemoteControlAsync(IRemoteControlTask remoteControl, SwitchOutcome done)
    {
        string manual = "; restart it by hand: Stop-ScheduledTask " + remoteControl.Name + "; Start-ScheduledTask " + remoteControl.Name;
        if (done.CliVerification.IsFailure || done.IdentityMismatchWarning)
        {
            return (false, "Remote Control was not restarted, because the CLI did not report " + done.Now.Value + manual);
        }

        Result<Unit, string> restart = await remoteControl.RestartAsync(CancellationToken.None);
        return restart.IsSuccess
            ? (true, null)
            : (false, "Remote Control was not restarted: " + restart.Error + manual);
    }
}
