using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
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
        mutations.MapPost("/accounts/{email}/switch", static async (
            string email,
            LiveDirectorySwitch executor,
            RateLimitGuardTeeFileReader tee,
            DashboardState state,
            CancellationToken cancellationToken) =>
        {
            Result<AccountEmail, string> target = AccountEmail.Parse(email);
            if (target.IsFailure)
            {
                return Results.BadRequest(new { error = target.Error });
            }

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
                state.PreSwitchWindows = before is null
                    ? null
                    : new PreSwitchWindows(before.FiveHourResetsAt, before.SevenDayResetsAt);
            }

            return outcome.Match(
                static done => Results.Ok(new SwitchOutcomeView(
                    done.Now.Value,
                    done.ParkedAs?.Value,
                    done.CliVerification.IsSuccess ? done.CliVerification.Value.Email : null,
                    done.CliVerification.IsFailure ? done.CliVerification.Error : null,
                    done.IdentityMismatchWarning,
                    done.At)),
                static refusal => Results.Json(new SwitchRefusalView(refusal.ToString(), Describe(refusal)), statusCode: StatusCodes.Status409Conflict));
        });
    }

    private static string Describe(SwitchRefusal refusal) => refusal switch
    {
        SwitchRefusal.TargetIsLiveDirectory => "The target folder is the live config directory.",
        SwitchRefusal.TargetHasNoCredentials => "That account has no parked credentials; log in first.",
        SwitchRefusal.TargetHasNoAccountBlock => "That profile folder carries no account identity.",
        SwitchRefusal.AlreadyOnTarget => "That account is already live.",
        SwitchRefusal.SharesLiveRefreshToken => "That parked pair is the live pair's own lineage; a second holder is never created.",
        SwitchRefusal.RefreshLockPresent => "A session is refreshing its token right now; try again in a moment.",
        SwitchRefusal.TargetStrandedInRecovery => "This account's credentials are stranded in recovery after a failed refresh; a restart or a per-card refresh restores them.",
        SwitchRefusal.TargetLoginExpired => "That account's login has expired; log in again.",
        SwitchRefusal.SwitchingBlockedByManagedPolicy => "A device-managed login policy pins this machine to one organization.",
        SwitchRefusal.ManagedPolicyUnreadable => "A device-managed login policy exists but could not be read; switching stays off until it can be.",
        SwitchRefusal.LiveIdentityUnverified => "The live identity could not be verified; see the banner.",
        SwitchRefusal.MutationInProgress => "Another credential change is in progress.",
        SwitchRefusal.RefreshInProgress => "A usage refresh is reading this machine's accounts right now; switch again when it finishes.",
        SwitchRefusal.LoginInProgress => "A login is running against one of those folders; finish it or let it expire first.",
        _ => refusal.ToString(),
    };
}
