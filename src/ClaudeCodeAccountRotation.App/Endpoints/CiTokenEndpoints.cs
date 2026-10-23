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
using static ClaudeCodeAccountRotation.App.Endpoints.EndpointRefusals;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// "Generate CI token": <c>claude setup-token</c> for one account, signed in
/// through the browser profile the roster maps to it, with the token handed to
/// <c>gh secret set</c> and never to the page.
/// <para>
/// The request names either <c>roles</c>, each written whole (the token, and
/// the account's stored Console key for a <see cref="CiRole.Keyed"/> role), with every
/// organization secret's visibility read back first so setting it keeps it;
/// or, as release 2.1.0 did, one <c>secretName</c> with its owner and
/// visibility, set as named. Neither writes a secret in one repository
/// (<see cref="CiTokenSecret.RepositorySecretRefusal"/>).
/// </para>
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
            IGhApiReader github,
            ISecretStore store,
            IBrowserLauncher browsers,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(body);
            ArgumentNullException.ThrowIfNull(github);
            Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
            if (parsed.IsFailure)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            Result<IReadOnlyList<CiRole>?, string> roles = ParseRoles(body);
            if (roles.IsFailure)
            {
                return Results.BadRequest(new { error = roles.Error });
            }

            CiTokenRequest? request = null;
            if (roles.Value is null)
            {
                Result<CiTokenSecret, string> secret = CiTokenSecret.Parse(Text(body, "secretName"), Text(body, "repository"), Text(body, "organization"));
                if (secret.IsFailure)
                {
                    return Results.BadRequest(new { error = secret.Error });
                }

                if (secret.Value.Scope == CiSecretScope.Repository)
                {
                    return Results.BadRequest(new { error = CiTokenSecret.RepositorySecretRefusal });
                }

                Result<(OrgSecretVisibility?, IReadOnlyList<string>), string> access = ParseAccess(body);
                if (access.IsFailure)
                {
                    return Results.BadRequest(new { error = access.Error });
                }

                (OrgSecretVisibility? visibility, IReadOnlyList<string> repositories) = access.Value;
                request = new CiTokenRequest(parsed.Value, secret.Value, visibility, repositories);
            }

            CiTokenSecret? unnamed = request?.Roles[0].Token is { Visibility: null, Secret.Scope: CiSecretScope.Organization } target ? target.Secret : null;

            RosterEntry? entry = (await rosterFile.ReadAsync(cancellationToken)).Find(parsed.Value);
            if (entry is null)
            {
                return NotOnRosterForBrowser(parsed.Value);
            }

            if (unnamed is not null)
            {
                // An organization secret named with no visibility keeps the one
                // it has: gh would otherwise make it private.
                // A new secret named in the role pattern is a role's, visible to
                // all repositories; any other new one is private, as gh made it.
                Result<SecretTarget, GhApiFailure> read = await github.ReadAccessAsync(
                    unnamed,
                    cancellationToken,
                    whenNew: CiRole.FromTokenSecret(unnamed).TokenSecretOverride is null ? OrgSecretVisibility.All : OrgSecretVisibility.Private);
                if (read.IsFailure)
                {
                    return Refused("SecretUnreadable", Describe(read.Error));
                }

                request = new CiTokenRequest(parsed.Value, unnamed, read.Value.Visibility, read.Value.Repositories);
            }

            if (roles.Value is IReadOnlyList<CiRole> named)
            {
                // Before any token exists: a keyed role written whole needs the
                // key beside the token, or it would be left half from this account.
                if (named.FirstOrDefault(static role => role.Keyed) is CiRole keyed)
                {
                    // The store, not only the roster's record of it: a key the
                    // store has lost would leave every key half unset.
                    Result<string?, string> stored = entry.ConsoleApiKeySetOn is null
                        ? Result<string?, string>.Success(null)
                        : await store.ReadAsync(SecretKind.ConsoleApiKey, parsed.Value, cancellationToken);
                    if (stored.IsFailure)
                    {
                        return Refused("KeyStoreUnavailable", stored.Error);
                    }

                    if (stored.Value is null)
                    {
                        return Refused("NoConsoleKey", "paste this account's Console key first (Set API key): " + keyed + " has a key half.");
                    }
                }

                Result<CiTokenRequest, string> resolved = await ResolveAsync(parsed.Value, named, github, cancellationToken);
                if (resolved.IsFailure)
                {
                    return Refused("SecretUnreadable", resolved.Error);
                }

                request = resolved.Value;
            }

            Result<LoginSession, string> started = await runner.StartAsync(request!, cancellationToken);
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
    /// The <c>roles</c> list, or null when the body names none. Each item names
    /// <c>prefix</c> (empty for the legacy role) or <c>tokenSecret</c> for a
    /// token-only role, then <c>repository</c> or <c>organization</c>.
    /// </summary>
    private static Result<IReadOnlyList<CiRole>?, string> ParseRoles(JsonObject body)
    {
        if (!body.TryGetPropertyValue("roles", out JsonNode? node))
        {
            return Result<IReadOnlyList<CiRole>?, string>.Success(null);
        }

        if (node is not JsonArray items || items.Count == 0)
        {
            return Result<IReadOnlyList<CiRole>?, string>.Failure("roles is a list of at least one role");
        }

        List<CiRole> roles = [];
        foreach (JsonNode? item in items)
        {
            if (item is not JsonObject role)
            {
                return Result<IReadOnlyList<CiRole>?, string>.Failure("each role is an object with prefix and repository or organization");
            }

            Result<CiRole, string> parsed = CiRole.Parse(Text(role, "prefix"), Text(role, "tokenSecret"), Text(role, "repository"), Text(role, "organization"));
            if (parsed.IsFailure)
            {
                return Result<IReadOnlyList<CiRole>?, string>.Failure("roles: " + parsed.Error);
            }

            if (parsed.Value.Scope == CiSecretScope.Repository)
            {
                return Result<IReadOnlyList<CiRole>?, string>.Failure("roles: " + CiTokenSecret.RepositorySecretRefusal);
            }

            if (roles.Contains(parsed.Value))
            {
                return Result<IReadOnlyList<CiRole>?, string>.Failure("roles: " + parsed.Value + " is named twice");
            }

            roles.Add(parsed.Value);
        }

        return Result<IReadOnlyList<CiRole>?, string>.Success(roles);
    }

    /// <summary>
    /// Each role's secrets with who may read them now (see
    /// <see cref="GhApiReads.ReadAccessAsync"/>). The first read that fails
    /// ends it, before any token is made.
    /// </summary>
    private static async Task<Result<CiTokenRequest, string>> ResolveAsync(
        AccountEmail email,
        IReadOnlyList<CiRole> roles,
        IGhApiReader github,
        CancellationToken cancellationToken)
    {
        List<RoleTarget> targets = [];
        foreach (CiRole role in roles)
        {
            Result<SecretTarget, GhApiFailure> token = await github.ReadAccessAsync(role.TokenSecret, cancellationToken);
            if (token.IsFailure)
            {
                return Result<CiTokenRequest, string>.Failure(Describe(token.Error));
            }

            SecretTarget? key = null;
            if (role.Keyed && role.KeySecret is CiTokenSecret keySecret)
            {
                Result<SecretTarget, GhApiFailure> read = await github.ReadAccessAsync(keySecret, cancellationToken);
                if (read.IsFailure)
                {
                    return Result<CiTokenRequest, string>.Failure(Describe(read.Error));
                }

                key = read.Value;
            }

            targets.Add(new RoleTarget(role, token.Value, key));
        }

        return Result<CiTokenRequest, string>.Success(new CiTokenRequest(email, targets));
    }

    /// <summary>Why a read of who may see a secret failed, as a write that never started says it.</summary>
    internal static string Describe(GhApiFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.RateLimited
            ? "GitHub's API rate limit was reached while reading who may see the role secrets, so nothing was set and nothing retried it. Wait for the limit to reset, then try again."
            : failure.Message;
    }

    /// <summary>
    /// Who may read an organization secret. One named with neither field keeps
    /// what GitHub has (null here), and <c>selected</c> needs the repositories,
    /// by name, that may read it.
    /// </summary>
    private static Result<(OrgSecretVisibility?, IReadOnlyList<string>), string> ParseAccess(JsonObject body)
    {
        string? visibility = Text(body, "visibility");
        IReadOnlyList<string> repositories = body["repositories"] is JsonArray array
            ? [.. array.Select(static node => node is JsonValue value && value.TryGetValue(out string? text) ? text.Trim() : string.Empty)]
            : [];

        // None named: the caller reads it back from GitHub (see ReadAccessAsync).
        if (visibility is null)
        {
            return repositories.Count == 0
                ? Result<(OrgSecretVisibility?, IReadOnlyList<string>), string>.Success((null, []))
                : Result<(OrgSecretVisibility?, IReadOnlyList<string>), string>.Failure("repositories apply only to selected visibility");
        }

        OrgSecretVisibility? parsed = visibility switch
        {
            "private" => OrgSecretVisibility.Private,
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

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepositoryNamePattern();
}
