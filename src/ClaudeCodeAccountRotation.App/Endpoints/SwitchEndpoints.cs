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
                static refusal => Results.Json(SwitchRefusalView.Of(refusal), statusCode: StatusCodes.Status409Conflict));
        });
    }
}
