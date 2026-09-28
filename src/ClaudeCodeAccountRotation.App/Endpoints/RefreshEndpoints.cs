using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// The three ways a refresh starts: every account, one card's button, or the
/// Claude Code <c>StopFailure</c> hook with matcher <c>rate_limit</c>, whose
/// payload names no account and so reads this side's live one.
/// <para>
/// All answer at once and start nothing themselves. The pass is a credential
/// operation that outlives its request — a browser navigating away mid-rotation
/// would strand a pair — so the route hands the work to the hosted worker and
/// returns 202, and the page's existing ten-second poll watches each card land
/// through <c>GET /api/dashboard</c>.
/// </para>
/// <para>
/// Every refusal is 409 with a refusal token, the shape every refusal on this
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

        // A session stopped by a rate limit fires this on every stopped turn, so
        // the account's one-minute gap refuses here rather than starting a pass
        // the budget would only refuse: that pass would still take the run slot
        // and overwrite the card's last outcome.
        mutations.MapPost("/hooks/rate-limit", static async (
            QuotaRefreshWorker worker,
            QuotaState state,
            RefreshBudget budget,
            ClaudeStateFile stateFile,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if ((await stateFile.ReadAccountBlockAsync(cancellationToken))?.Email is not AccountEmail live)
            {
                return Refused("NoLiveAccount", "No account is logged in on this side");
            }

            // The longest of the waits that stand, so the countdown is the one after
            // which the hook is next accepted: a real 429 leaves both the gap and
            // the longer lockout. Max skips the ones that are null.
            DateTimeOffset now = timeProvider.GetUtcNow();
            return new[] { budget.GapRemaining(live), budget.LockedOutFor(live), state.LockedUntil(now) - now }.Max() is TimeSpan wait
                ? Refused("RateLimited", RefreshMessages.RateLimited(wait))
                : Start(worker, state, timeProvider, RefreshRequest.One(live));
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
