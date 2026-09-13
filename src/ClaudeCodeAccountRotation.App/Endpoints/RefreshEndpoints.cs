using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// The two ways a refresh starts: every account, or one card's button.
/// <para>
/// Both answer at once and start nothing themselves. The pass is a credential
/// operation that outlives its request — a browser navigating away mid-rotation
/// would strand a pair — so the route hands the work to the hosted worker and
/// returns 202, and the page's existing ten-second poll watches each card land
/// through <c>GET /api/dashboard</c>.
/// </para>
/// <para>
/// Both refusals are 409 with a refusal token, the shape every refusal on this
/// page takes, and never 429: the page has no 429 handling and a lockout is this
/// tool declining to send, not the endpoint declining to answer.
/// </para>
/// </summary>
internal static class RefreshEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPost("/refresh", static (
            QuotaRefreshWorker worker,
            QuotaState state,
            TimeProvider timeProvider) => Start(worker, state, timeProvider, RefreshRequest.All));

        mutations.MapPost("/accounts/{email}/refresh", static async (
            string email,
            QuotaRefreshWorker worker,
            QuotaState state,
            ClaudeStateFile stateFile,
            ProfileFolderStore profiles,
            RosterFile rosterFile,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            Result<AccountEmail, string> target = AccountEmail.Parse(email);
            if (target.IsFailure)
            {
                return Results.BadRequest(new { error = target.Error });
            }

            return await KnownAsync(target.Value, stateFile, profiles, rosterFile, cancellationToken)
                ? Start(worker, state, timeProvider, RefreshRequest.One(target.Value))
                : Results.NotFound(new { error = "This machine has no account called " + target.Value.Value + "." });
        });
    }

    /// <summary>
    /// The lockout first, then the claim. A lockout means every read the pass
    /// would make is already refused, so starting one would burn the single run
    /// slot on a pass that sends nothing.
    /// </summary>
    private static IResult Start(QuotaRefreshWorker worker, QuotaState state, TimeProvider timeProvider, RefreshRequest request)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (state.LockedUntil(now) is DateTimeOffset until)
        {
            return Refused("RateLimited", RefreshMessages.RateLimited(until - now));
        }

        return worker.TryStart(request)
            ? Results.Json(new { started = true }, statusCode: StatusCodes.Status202Accepted)
            : Refused("RefreshInProgress", "A refresh is already running");
    }

    /// <summary>
    /// Whether this machine knows the account at all: it is the live one, it has
    /// a profile folder, or the roster names it. A roster entry with no folder
    /// counts, because its card exists and its Refresh button must answer with
    /// "no credentials" rather than with a 404 the page has no card to attach.
    /// </summary>
    private static async Task<bool> KnownAsync(
        AccountEmail account,
        ClaudeStateFile stateFile,
        ProfileFolderStore profiles,
        RosterFile rosterFile,
        CancellationToken cancellationToken)
    {
        if ((await stateFile.ReadAccountBlockAsync(cancellationToken))?.Email == account)
        {
            return true;
        }

        IReadOnlyList<ParkedProfile> parked = await profiles.ListAsync(cancellationToken);
        return parked.Any(profile => profile.Email == account)
            || (await rosterFile.ReadAsync(cancellationToken)).Find(account) is not null;
    }

    private static IResult Refused(string refusal, string message) =>
        Results.Json(new SwitchRefusalView(refusal, message), statusCode: StatusCodes.Status409Conflict);
}
