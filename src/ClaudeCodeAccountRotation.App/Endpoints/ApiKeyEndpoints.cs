using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// An account's Console API key, pasted once on this machine and kept in the
/// OS store. The key arrives in the request body only, and nothing returns it:
/// each route answers with the roster entry, whose <c>consoleApiKeySetOn</c>
/// is all the page knows, so loading the page never reads the store. With no
/// usable store the key is refused with the cause, and nothing is written.
/// </summary>
internal static partial class ApiKeyEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPut("/accounts/{email}/api-key", static async (
            string email,
            JsonObject body,
            RosterFile rosterFile,
            ISecretStore store,
            TimeProvider clock,
            ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(body);
            ArgumentNullException.ThrowIfNull(clock);
            ArgumentNullException.ThrowIfNull(loggers);
            Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
            if (parsed.IsFailure)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            // Trimmed, since a paste often brings a line break with it.
            string? key = body["key"] is JsonValue value && value.TryGetValue(out string? text) ? text.Trim() : null;
            if (string.IsNullOrEmpty(key))
            {
                return Results.BadRequest(new { error = "key must be the Console API key, as text" });
            }

            AccountEmail target = parsed.Value;
            if ((await rosterFile.ReadAsync(cancellationToken)).Find(target) is null)
            {
                return NotOnRoster(target);
            }

            ILogger logger = loggers.CreateLogger(typeof(ApiKeyEndpoints).FullName!);
            Result<Unit, string> usable = await store.ProbeAsync(cancellationToken);
            Result<Unit, string> written = usable.IsSuccess
                ? await store.WriteAsync(SecretKind.ConsoleApiKey, target, key, cancellationToken)
                : usable;
            if (written.IsFailure)
            {
                LogStoreRefused(logger, target.Value);
                return Refused("KeyStoreUnavailable", written.Error);
            }

            LogKeyStored(logger, target.Value);
            var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
            return await RecordAsync(rosterFile, target, today, cancellationToken);
        });

        mutations.MapDelete("/accounts/{email}/api-key", static async (
            string email,
            RosterFile rosterFile,
            ISecretStore store,
            ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(loggers);
            Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
            if (parsed.IsFailure)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            AccountEmail target = parsed.Value;
            if ((await rosterFile.ReadAsync(cancellationToken)).Find(target) is null)
            {
                return NotOnRoster(target);
            }

            ILogger logger = loggers.CreateLogger(typeof(ApiKeyEndpoints).FullName!);
            Result<Unit, string> deleted = await store.DeleteAsync(SecretKind.ConsoleApiKey, target, cancellationToken);
            if (deleted.IsFailure)
            {
                LogStoreRefused(logger, target.Value);
                return Refused("KeyStoreUnavailable", deleted.Error);
            }

            LogKeyDeleted(logger, target.Value);
            return await RecordAsync(rosterFile, target, null, cancellationToken);
        });
    }

    /// <summary>
    /// Sets the date on the entry as the roster holds it at write time, so an
    /// edit that landed since the read above is kept.
    /// </summary>
    private static async Task<IResult> RecordAsync(RosterFile rosterFile, AccountEmail target, DateOnly? setOn, CancellationToken cancellationToken)
    {
        RosterEntry? updated = null;
        await rosterFile.UpdateAsync(
            roster =>
            {
                updated = roster.Find(target) is RosterEntry entry ? entry with { ConsoleApiKeySetOn = setOn } : null;
                return updated is null ? roster : roster.With(updated);
            },
            cancellationToken);
        return updated is null ? NotOnRoster(target) : Results.Ok(DashboardAssembler.View(updated));
    }

    private static IResult NotOnRoster(AccountEmail email) =>
        Results.NotFound(new { error = email.Value + " is not on the roster." });

    private static IResult Refused(string refusal, string message) =>
        Results.Json(new SwitchRefusalView(refusal, message), statusCode: StatusCodes.Status409Conflict);

    // The account and the outcome only: never the key, and not the store's
    // own diagnostic either, which reaches the page instead.
    [LoggerMessage(Level = LogLevel.Information, Message = "console API key for {Account} stored")]
    private static partial void LogKeyStored(ILogger logger, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "console API key for {Account} deleted")]
    private static partial void LogKeyDeleted(ILogger logger, string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the OS store refused the console API key for {Account}")]
    private static partial void LogStoreRefused(ILogger logger, string account);
}
