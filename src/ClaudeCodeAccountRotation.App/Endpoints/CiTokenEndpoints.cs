using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
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
/// "Generate CI token": <c>claude setup-token</c> for one account, signed in
/// through the browser profile the roster maps to it, with the token handed to
/// <c>gh secret set</c> and never to the page.
/// <para>
/// The same shape as a login: start returns the sign-in URL and opens it, and
/// the code call hands the one-time code to the child. What the page gets back
/// is the session's state and a message from a fixed vocabulary, never the
/// token and never anything the child printed.
/// </para>
/// </summary>
internal static partial class CiTokenEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPost("/accounts/{email}/ci-token", static async (
            string email,
            JsonObject body,
            RosterFile rosterFile,
            ICiTokenSessionRunner runner,
            IBrowserLauncher browsers,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(body);
            Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
            if (parsed.IsFailure)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            Result<CiTokenSecret, string> secret = CiTokenSecret.Parse(Text(body, "secretName"), Text(body, "repository"), Text(body, "organization"));
            if (secret.IsFailure)
            {
                return Results.BadRequest(new { error = secret.Error });
            }

            Result<(OrgSecretVisibility?, IReadOnlyList<string>), string> access = ParseAccess(body, secret.Value);
            if (access.IsFailure)
            {
                return Results.BadRequest(new { error = access.Error });
            }

            RosterEntry? entry = (await rosterFile.ReadAsync(cancellationToken)).Find(parsed.Value);
            if (entry is null)
            {
                return Refused("NotOnRoster", parsed.Value.Value + " is not on the roster; add it first so its browser mapping is known.");
            }

            (OrgSecretVisibility? visibility, IReadOnlyList<string> repositories) = access.Value;
            Result<LoginSession, string> started = await runner.StartAsync(
                new CiTokenRequest(parsed.Value, secret.Value, visibility, repositories),
                cancellationToken);
            if (started.IsFailure)
            {
                return Refused("CiTokenCouldNotStart", started.Error);
            }

            LoginSession session = started.Value;
            string? browserError = entry.Browser is BrowserFamily browser
                ? browsers.Launch(browser, entry.BrowserProfileDirectory, session.SignInUrl!).Match<string?>(static _ => null, static error => error)
                : "no browser is mapped to this account; open the sign-in URL yourself in a browser signed in as it, or map one with Edit.";
            return Results.Ok(View(session, browserError));
        });

        mutations.MapPost("/ci-token-sessions/{id}/code", static async (
            string id,
            JsonObject body,
            ICiTokenSessionRunner runner,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(body);
            LoginSessionId session = new(id);
            if (runner.Status(session) is null)
            {
                return Results.NotFound(new { error = "no CI token session with that id is running" });
            }

            string code = body["code"] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;
            Result<LoginSession, string> submitted = await runner.SubmitCodeAsync(session, code, cancellationToken);
            return submitted.IsFailure
                ? Refused("CodeRefused", submitted.Error)
                : Results.Ok(View(submitted.Value, browserError: null));
        });

        // A read, outside the mutation filter, for the same reasons as the login
        // session read beside it.
        routes.MapGet("/api/ci-token-sessions/{id}", static (string id, ICiTokenSessionRunner runner) =>
            runner.Status(new LoginSessionId(id)) is LoginSession session
                ? Results.Ok(View(session, browserError: null))
                : Results.NotFound(new { error = "no CI token session with that id is running" }));
    }

    /// <summary>
    /// Who may read an organization secret. A repository secret takes neither
    /// field; an organization secret defaults to private, the way <c>gh</c> does,
    /// and <c>selected</c> needs the repositories, by name, that may read it.
    /// </summary>
    private static Result<(OrgSecretVisibility?, IReadOnlyList<string>), string> ParseAccess(JsonObject body, CiTokenSecret secret)
    {
        string? visibility = Text(body, "visibility");
        IReadOnlyList<string> repositories = body["repositories"] is JsonArray array
            ? [.. array.Select(static node => node is JsonValue value && value.TryGetValue(out string? text) ? text.Trim() : string.Empty)]
            : [];
        if (secret.Scope == CiSecretScope.Repository)
        {
            return visibility is null && repositories.Count == 0
                ? Result<(OrgSecretVisibility?, IReadOnlyList<string>), string>.Success((null, []))
                : Result<(OrgSecretVisibility?, IReadOnlyList<string>), string>.Failure("visibility and repositories apply to an organization secret only");
        }

        OrgSecretVisibility? parsed = visibility switch
        {
            null or "private" => OrgSecretVisibility.Private,
            "all" => OrgSecretVisibility.All,
            "selected" => OrgSecretVisibility.Selected,
            _ => null,
        };
        if (parsed is null)
        {
            return Result<(OrgSecretVisibility?, IReadOnlyList<string>), string>.Failure("visibility is private, all, or selected");
        }

        if (parsed == OrgSecretVisibility.Selected)
        {
            if (repositories.Count == 0 || !repositories.All(static name => RepositoryNamePattern().IsMatch(name)))
            {
                return Result<(OrgSecretVisibility?, IReadOnlyList<string>), string>.Failure(
                    "selected visibility needs at least one repository name (letters, digits, '.', '_' or '-')");
            }
        }
        else if (repositories.Count > 0)
        {
            return Result<(OrgSecretVisibility?, IReadOnlyList<string>), string>.Failure("repositories apply only to selected visibility");
        }

        return Result<(OrgSecretVisibility?, IReadOnlyList<string>), string>.Success((parsed, repositories));
    }

    private static string? Text(JsonObject body, string key) =>
        body[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

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

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepositoryNamePattern();
}
