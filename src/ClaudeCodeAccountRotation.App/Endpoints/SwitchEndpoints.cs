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

/// <summary>
/// Holds a Windows switch from its Remote Control read through its restart, so
/// a second switch cannot read or change the account mid-restart.
/// </summary>
internal sealed class RemoteControlRestartLock : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool TryEnter() => _semaphore.Wait(TimeSpan.Zero);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}

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
            RemoteControlRestartLock restartLock,
            CancellationToken cancellationToken) =>
        {
            Result<AccountEmail, string> target = AccountEmail.Parse(email);
            if (target.IsFailure)
            {
                return Results.BadRequest(new { error = target.Error });
            }

            await worker.YieldAsync(cancellationToken);

            // From the running-state read through the restart, one switch at a
            // time: the switch's own gate is released before the restart, and a
            // second switch landing in between would read the task mid-restart
            // and change the account under the session the first one starts.
            // Refused rather than queued, as the switch's own gate refuses a
            // switch already in flight.
            if (!restartLock.TryEnter())
            {
                return Results.Json(SwitchRefusalView.Of(SwitchRefusal.MutationInProgress), statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                return await SwitchAndRestartAsync(target.Value, executor, tee, state, remoteControl, cancellationToken);
            }
            finally
            {
                restartLock.Release();
            }
        });
    }

    private static async Task<IResult> SwitchAndRestartAsync(
        AccountEmail target,
        LiveDirectorySwitch executor,
        RateLimitGuardTeeFileReader tee,
        DashboardState state,
        IRemoteControlTask remoteControl,
        CancellationToken cancellationToken)
    {
        // Read before the switch: Claude Code may end the session itself once
        // the account changes, and a task the operator had stopped is not
        // started on their behalf.
        bool remoteControlWasRunning = await remoteControl.IsRunningAsync(cancellationToken);

        // Remember the outgoing account's windows before the swap. A session
        // mid-turn at switch time writes them back under the incoming
        // account's name, and the dashboard must not read them as its own.
        StatuslineSnapshot? before = await tee.ReadAsync(cancellationToken);
        Result<SwitchOutcome, SwitchRefusal> outcome = await executor.SwitchToAsync(target, cancellationToken);
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
        // Stop-ScheduledTask ends only the task's root process, so the claude.exe
        // under it has to be ended by hand before the start, or two servers run.
        string manual = "; restart it by hand: Stop-ScheduledTask " + remoteControl.Name
            + ", end the claude.exe running remote-control that it leaves behind, then Start-ScheduledTask " + remoteControl.Name;
        bool reportsIncoming = done.CliVerification.IsSuccess
            && string.Equals(done.CliVerification.Value.Email?.Trim(), done.Now.Value, StringComparison.OrdinalIgnoreCase);
        if (!reportsIncoming)
        {
            return (false, "Remote Control was not restarted, because the CLI did not report " + done.Now.Value + manual);
        }

        Result<Unit, string> restart = await remoteControl.RestartAsync(CancellationToken.None);
        return restart.IsSuccess
            ? (true, null)
            : (false, "Remote Control was not restarted: " + restart.Error + manual);
    }
}
