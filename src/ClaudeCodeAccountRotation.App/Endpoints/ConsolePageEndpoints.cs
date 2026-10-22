using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Http;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// Open Console: one of the fixed Claude Console key pages in the browser
/// profile the roster maps to the account, so the key is created while signed
/// in as that account. The click starts a browser and nothing else: no
/// GitHub or Anthropic read, and the page name picks a URL the tool holds
/// rather than carrying one.
/// </summary>
internal static class ConsolePageEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        // Behind the mutation filter because it starts a process.
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPost("/accounts/{email}/console-page", static async (
            string email,
            JsonObject body,
            RosterFile rosterFile,
            IBrowserLauncher browsers,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(body);
            Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
            if (parsed.IsFailure)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            string? page = body["page"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
            if (page is null || !AnthropicEndpoints.ConsolePages.TryGetValue(page, out Uri? url))
            {
                return Results.BadRequest(new { error = "page must be one of: " + string.Join(", ", AnthropicEndpoints.ConsolePages.Keys) });
            }

            RosterEntry? entry = (await rosterFile.ReadAsync(cancellationToken)).Find(parsed.Value);
            if (entry is null)
            {
                return Refused("NotOnRoster", parsed.Value.Value + " is not on the roster; add it first so its browser mapping is known.");
            }

            if (entry.Browser is not BrowserFamily browser)
            {
                return Refused("NoBrowserMapped", "no browser is mapped to this account; open " + url.AbsoluteUri + " yourself in a browser signed in as it, or map one with Edit.");
            }

            return browsers.Launch(browser, entry.BrowserProfileDirectory, url).Match(
                _ => Results.Ok(new { opened = url.AbsoluteUri }),
                error => Refused("BrowserDidNotOpen", error));
        });
    }

    private static IResult Refused(string refusal, string message) =>
        Results.Json(new SwitchRefusalView(refusal, message), statusCode: StatusCodes.Status409Conflict);
}
