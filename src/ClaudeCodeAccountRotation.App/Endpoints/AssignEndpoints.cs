using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Accounts;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using static ClaudeCodeAccountRotation.App.Endpoints.EndpointRefusals;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// "Assign to role": sends an account's stored CI token, its stored Console
/// key, or both, to one role's secrets, with no sign-in and no new token.
/// <para>
/// The body names <c>role</c> (as one item of Generate CI token's
/// <c>roles</c>) and <c>halves</c>: <c>token</c>, <c>key</c> or <c>both</c>.
/// With a lane catalog configured the role must be one it lists or one of the
/// configured extra token secrets. Every refusal comes before any write: a half
/// not stored, a key for a role whose lanes read none, the cost gate for a key,
/// and a secret whose visibility cannot be read back. The writes then run one
/// at a time, token first, and a rate limit stops the one after it. The roster
/// records each half set, taking it off the account that held it, and the lane
/// history says so. No value reaches a response or a log.
/// </para>
/// </summary>
internal static partial class AssignEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>().MapPost("/accounts/{email}/assign", AssignAsync);
    }

    private static async Task<IResult> AssignAsync(
        string email,
        JsonObject body,
        RosterFile rosterFile,
        ISecretStore store,
        ICiSecretWriter writer,
        IGhApiReader github,
        TimeProvider clock,
        ILoggerFactory loggers,
        LaneCatalogStore laneCatalog,
        QuotaState quota,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(rosterFile);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggers);
        ArgumentNullException.ThrowIfNull(laneCatalog);
        Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
        if (parsed.IsFailure)
        {
            return Results.BadRequest(new { error = parsed.Error });
        }

        (bool token, bool key)? halves = body["halves"] is JsonValue value && value.TryGetValue(out string? text)
            ? text switch
            {
                "token" => (true, false),
                "key" => (false, true),
                "both" => (true, true),
                _ => null,
            }
            : null;
        if (halves is not (bool wantToken, bool wantKey))
        {
            return Results.BadRequest(new { error = "halves is token, key, or both" });
        }

        Result<IReadOnlyList<CiRole>?, string> named = body["role"] is JsonNode role
            ? CiTokenEndpoints.ParseRoles(new JsonObject { ["roles"] = new JsonArray(role.DeepClone()) })
            : Result<IReadOnlyList<CiRole>?, string>.Failure("role is one role, as Generate CI token names one");
        if (named.IsFailure)
        {
            return Results.BadRequest(new { error = named.Error });
        }

        CiRole target = named.Value![0];
        AccountEmail account = parsed.Value;
        RosterEntry? entry = (await rosterFile.ReadAsync(cancellationToken)).Find(account);
        if (entry is null)
        {
            return Results.NotFound(new { error = account.Value + " is not on the roster." });
        }

        Result<Func<CiRole, bool>, string> keying = await laneCatalog.KeyedForWriteAsync(cancellationToken);
        if (keying.IsFailure)
        {
            return Refused("CatalogUnreadable", keying.Error);
        }

        // With a catalog, only a role it lists or an extra token secret in its
        // organization, so a typo never creates a secret.
        if (laneCatalog.Source is not null && laneCatalog.Snapshot.Catalog is LaneCatalog catalog)
        {
            bool inOrganization = string.Equals(target.Owner, catalog.Organization, StringComparison.OrdinalIgnoreCase);
            bool listed = inOrganization
                && (laneCatalog.IsExtra(target)
                    || catalog.Roles.Any(listedRole => target.TokenSecretOverride is null && string.Equals(listedRole.Prefix, target.Prefix, StringComparison.OrdinalIgnoreCase)));
            if (!listed)
            {
                return Results.BadRequest(new { error = "role: " + target + " is not in the lane catalog or extraTokenSecrets" });
            }
        }

        if (wantKey && (!keying.Value(target) || target.KeySecret is null))
        {
            return Refused("NoKeySecret", target + " takes no API key: its lanes read a token only. Assign its token alone.");
        }

        string? tokenValue = null;
        DateOnly? storedOn = entry.CiTokenStoredOn;
        if (wantToken)
        {
            Result<string?, string> read = entry.CiTokenStoredOn is null
                ? Result<string?, string>.Success(null)
                : await store.ReadAsync(SecretKind.CiToken, account, cancellationToken);
            if (read.IsFailure)
            {
                return Refused("KeyStoreUnavailable", read.Error);
            }

            if (read.Value is null)
            {
                return Refused("NoStoredToken", "No CI token is stored for " + account.Value + " on this machine. Generate CI token, or paste one with Store CI token.");
            }

            tokenValue = read.Value;
        }

        string? keyValue = null;
        if (wantKey)
        {
            Result<string?, string> read = entry.ConsoleApiKeySetOn is null
                ? Result<string?, string>.Success(null)
                : await store.ReadAsync(SecretKind.ConsoleApiKey, account, cancellationToken);
            if (read.IsFailure)
            {
                return Refused("KeyStoreUnavailable", read.Error);
            }

            if (read.Value is null)
            {
                return Refused("NoConsoleKey", "No Console API key is stored for " + account.Value + " on this machine. Set it first (Set API key).");
            }

            keyValue = read.Value;

            // The key goes live here, so the cost gate runs before any secret is read or set.
            if (await ApiKeyEndpoints.CostGateAsync(account, ApiKeyEndpoints.CostAnswer(body), rosterFile, quota, clock, cancellationToken) is IResult gated)
            {
                return gated;
            }
        }

        List<(bool IsKey, SecretTarget Secret, string Value)> writes = [];
        foreach ((bool isKey, CiTokenSecret secret, string? half) in new[] { (false, target.TokenSecret, tokenValue), (true, target.KeySecret!, keyValue) })
        {
            if (half is null)
            {
                continue;
            }

            Result<SecretTarget, GhApiFailure> access = await github.ReadAccessAsync(secret, cancellationToken);
            if (access.IsFailure)
            {
                return Refused("SecretUnreadable", CiTokenEndpoints.Describe(access.Error));
            }

            writes.Add((isKey, access.Value, half));
        }

        // From the first write on, the request's token is not consulted: a set
        // that reached GitHub must reach the roster.
        ILogger logger = loggers.CreateLogger(typeof(AssignEndpoints).FullName!);
        List<(bool IsKey, DateTimeOffset SetAt)> sent = [];
        List<string> failed = [];
        bool stopped = false;
        foreach ((bool isKey, SecretTarget secret, string half) in writes)
        {
            Result<Unit, string> set = stopped
                ? Result<Unit, string>.Failure("not sent, because an earlier write hit GitHub's rate limit")
                : await writer.SetAsync(secret.Secret, secret.Visibility, secret.Repositories, half, CancellationToken.None);
            if (set.IsSuccess)
            {
                LogAssigned(logger, isKey ? "key" : "token", account.Value, secret.Secret.Name);
                sent.Add((isKey, clock.GetUtcNow()));
                continue;
            }

            LogAssignFailed(logger, isKey ? "key" : "token", account.Value, secret.Secret.Name);
            failed.Add(secret.Secret + ": " + set.Error);
            stopped |= set.Error == GhSecretWriter.RateLimitMessage;
        }

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        string message = (sent.Count == 0
                ? "Nothing was set."
                : "Set " + string.Join(", ", sent.Select(half => (half.IsKey ? target.KeySecret! : target.TokenSecret).ToString())) + " from " + account.Value + ".")
            + (failed.Count == 0 ? string.Empty : " Not set: " + string.Join("; ", failed) + ".");
        RosterEntry? updated = null;
        bool removed = false;
        try
        {
            await rosterFile.UpdateAsync(
                roster =>
                {
                    if (sent.Count == 0)
                    {
                        updated = roster.Find(account);
                        return roster;
                    }

                    // An account removed while the writes ran is not put back,
                    // but each half it set is still taken off the account that
                    // held it: that secret no longer holds their value.
                    RosterEntry? found = roster.Find(account);
                    removed = found is null;
                    RosterEntry entry = found ?? new RosterEntry(account);
                    DateOnly tokenDay = entry.CiTokenStoredOn ?? storedOn ?? today;
                    foreach ((bool isKey, DateTimeOffset setAt) in sent)
                    {
                        // The token half keeps the day its token was stored, so
                        // its expiry runs from when the token was made.
                        entry = isKey ? entry.ClaimingKey(target, today, setAt) : entry.ClaimingToken(target, tokenDay, setAt);
                    }

                    updated = removed ? null : entry;
                    return removed ? roster.With(entry).Without(account) : roster.With(entry);
                },
                CancellationToken.None,
                via: LaneVia.Assign);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Json(
                new { error = message + " The roster could not record it (" + exception.GetType().Name + "), so the card does not show it yet; assign again to record it." },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        if (removed)
        {
            message += " " + account.Value + " was removed while this ran, so no account on the roster backs those halves now.";
        }

        return Results.Ok(new AssignView(DashboardAssembler.View(updated, laneCatalog.IsKeyedNow), message, failed.Count));
    }

    // Names only: never a value.
    [LoggerMessage(Level = LogLevel.Information, Message = "stored {Half} of {Account} assigned to secret {Secret}")]
    private static partial void LogAssigned(ILogger logger, string half, string account, string secret);

    [LoggerMessage(Level = LogLevel.Warning, Message = "stored {Half} of {Account} could not be assigned to secret {Secret}")]
    private static partial void LogAssignFailed(ILogger logger, string half, string account, string secret);
}

/// <summary>The roster entry after Assign to role, what was set and what was not, and how many secrets were not.</summary>
internal sealed record AssignView(RosterEntryView? Roster, string Message, int Failed);
