using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// Browser-assisted login: the three calls that get an account the machine has
/// never seen onto the machine without a terminal.
/// <para>
/// Start runs the CLI under the account's own folder, captures the sign-in URL
/// it prints, and opens that URL in the browser profile the roster maps to the
/// account, so ten accounts do not fight over one signed-in browser profile. A
/// browser that could not be opened is reported beside the URL rather than
/// failing the login: the operator can still paste the URL.
/// </para>
/// <para>
/// The code call carries the one-time code in the body and hands it to the
/// child's standard input. It is never echoed back, never logged, and never an
/// argument.
/// </para>
/// </summary>
internal static class LoginEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPost("/accounts/{email}/login", static async (
            string email,
            // Nullable because a minimal-API `bool` bound from the query string
            // is required, and every caller that predates this flag sends no
            // query at all: absent is off, which is what the ordinary login is.
            bool? supersede,
            RosterFile rosterFile,
            ProfileFolderStore profiles,
            ClaudeStateFile stateFile,
            ILoginSessionRunner runner,
            IBrowserLauncher browsers,
            SharedStoreSlots slots,
            WslSwitch coordinator,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
            if (parsed.IsFailure)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            AccountEmail target = parsed.Value;
            RosterEntry? entry = (await rosterFile.ReadAsync(cancellationToken)).Find(target);
            if (entry is null)
            {
                return Refused("NotOnRoster", target.Value + " is not on the roster; add it first so its browser mapping is known.");
            }

            // A login into the parked folder of the account the machine is already
            // signed in as would leave that account holding two logins, which is the
            // one thing the single-holder rule exists to prevent.
            if ((await stateFile.ReadAccountBlockAsync(cancellationToken))?.Email == target)
            {
                return Refused("AccountIsLive", "That account is already logged in on this machine; switch away from it before logging it in again.");
            }

            ParkedProfile folder = await profiles.EnsureFolderAsync(target, cancellationToken);

            // The same rule as the guard above, one side over: an empty slot the
            // other side holds is not an account waiting to be logged in, and a
            // login into it would put a second token family on the machine while
            // the first is live in the distro. The reconciliation rule would then
            // drop that side's record, because the slot holds a pair again, and
            // nothing would be left saying the family in the distro exists. A
            // deliberate, banner-carrying "log in again on Windows" over this
            // refusal is the escape hatch design section 11 describes; it is
            // phase 7's, and it needs a refusal here to override.
            AccountEmail? liveAccount = (await stateFile.ReadAccountBlockAsync(cancellationToken))?.Email;
            SlotSnapshot? slot = await slots.ReadAsync(target, folder.FolderPath, folder.HasCredentials, new WindowsHold(liveAccount, null), cancellationToken);
            if (slot is { State: SlotState.InTransit })
            {
                // No override, ever. A hand-off in flight means the pair is
                // between two live directories, and a login into the slot it is
                // heading for or coming back to would be a second family made
                // against a state neither side can yet name.
                return Refused("SlotInTransit", "A hand-off for that account is in flight; nothing may log it in again until that settles.");
            }

            if (slot is { State: SlotState.HeldElsewhere, Record: HolderRecord held })
            {
                if (supersede != true)
                {
                    return Refused("HeldByOtherSide", "The " + held.Side.Value + " side of this machine holds that account; switch it away there first, or log in again here to supersede the family it holds.");
                }

                // The escape hatch, and the one place in this design a second
                // token family for an account is allowed to exist. It is worth
                // an extra login only when the hand-off that costs none is
                // unavailable, so a side that answers is told to do the hand-off
                // instead of being superseded behind its back.
                if ((await coordinator.ReadSideAsync(held.Side, cancellationToken)).Online)
                {
                    return Refused("SideIsOnline", "The " + held.Side.Value + " side is answering, so switch that account away there instead; superseding it would make a second token family for no reason.");
                }

                // Written before the login runs, so a login that never finishes
                // still leaves the one statement that the family over there
                // exists. Its own reconciliation drops it again if no family
                // ever lands in this slot.
                await slots.SupersedeAsync(folder.FolderPath, held with { Since = clock.GetUtcNow() }, cancellationToken);
            }

            Result<LoginSession, string> started = await runner.StartAsync(target, folder.FolderPath, cancellationToken);
            if (started.IsFailure)
            {
                return Refused("LoginCouldNotStart", started.Error);
            }

            LoginSession session = started.Value;
            string? browserError = entry.Browser is BrowserFamily browser
                ? browsers.Launch(browser, entry.BrowserProfileDirectory, session.SignInUrl!).Match<string?>(static _ => null, static error => error)
                : "no browser is mapped to this account; open the sign-in URL yourself, or map one with Edit.";
            return Results.Ok(View(session, browserError));
        });

        mutations.MapPost("/login-sessions/{id}/code", static async (
            string id,
            JsonObject body,
            ILoginSessionRunner runner,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(body);
            LoginSessionId session = new(id);
            if (runner.Status(session) is null)
            {
                return Results.NotFound(new { error = "no login session with that id is running" });
            }

            string code = body["code"] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;
            Result<LoginSession, string> submitted = await runner.SubmitCodeAsync(session, code, cancellationToken);
            return submitted.IsFailure
                ? Refused("CodeRefused", submitted.Error)
                : Results.Ok(View(submitted.Value, browserError: null));
        });

        // Mapped on the bare routes, outside the same-origin mutation filter, on
        // purpose: this is a read, like /api/dashboard and /api/browser-profiles.
        // The filter exists to stop a cross-origin page from changing state
        // through a request the browser would send anyway; a cross-origin GET
        // cannot read this response at all without CORS (none is registered, and
        // the host guards refuse a rebound name), and what it carries, the
        // session state and the sign-in URL, is what the POST that created the
        // session already handed the same page. Guarding reads on the loopback
        // surface is #7's per-instance token, for every read at once; putting
        // this one behind the mutation filter would demand a mutation header
        // and an Origin from a request that mutates nothing. (The page does not
        // poll this route today; it posts the code and re-reads the dashboard.)
        routes.MapGet("/api/login-sessions/{id}", static (string id, ILoginSessionRunner runner) =>
            runner.Status(new LoginSessionId(id)) is LoginSession session
                ? Results.Ok(View(session, browserError: null))
                : Results.NotFound(new { error = "no login session with that id is running" }));
    }

    private static LoginSessionView View(LoginSession session, string? browserError) => new(
        session.Id.Value,
        session.Email.Value,
        session.State.ToString(),
        session.Message,
        session.SignInUrl?.AbsoluteUri,
        browserError,
        session.ExpiresAt);

    private static IResult Refused(string refusal, string message) =>
        Results.Json(new SwitchRefusalView(refusal, message), statusCode: StatusCodes.Status409Conflict);
}
