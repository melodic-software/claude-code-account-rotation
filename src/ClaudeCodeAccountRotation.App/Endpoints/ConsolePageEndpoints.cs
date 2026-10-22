using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Http;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static ClaudeCodeAccountRotation.App.Endpoints.EndpointRefusals;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// Open claude.ai and Open Console: one of the fixed Anthropic pages in the
/// browser profile the roster maps to the account, so the page opens signed in
/// as that account, and a key is created there as it. The click starts a browser and nothing else: no
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
            if (page is null || !AnthropicEndpoints.AccountPages.TryGetValue(page, out Uri? url))
            {
                return Results.BadRequest(new { error = "page must be one of: " + string.Join(", ", AnthropicEndpoints.AccountPages.Keys) });
            }

            RosterEntry? entry = (await rosterFile.ReadAsync(cancellationToken)).Find(parsed.Value);
            if (entry is null)
            {
                return NotOnRosterForBrowser(parsed.Value);
            }

            if (entry.Browser is not BrowserFamily browser)
            {
                return Refused("NoBrowserMapped", "no browser is mapped to this account; open " + url.AbsoluteUri + " yourself in a browser signed in as it, or map one with Edit.");
            }

            return browsers.Launch(browser, entry.BrowserProfileDirectory, url).Match(
                static _ => Results.Ok(new { }),
                static error => Refused("BrowserDidNotOpen", error));
        });
    }
}
