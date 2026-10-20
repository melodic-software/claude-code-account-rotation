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
            RosterEntry? updated = await RecordAsync(rosterFile, target, today, cancellationToken);
            return updated is null ? NotOnRoster(target) : Results.Ok(DashboardAssembler.View(updated));
        });

        mutations.MapDelete("/accounts/{email}/api-key", static async (
            string email,
            bool? keepStoredKey,
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
            RosterEntry? existing = (await rosterFile.ReadAsync(cancellationToken)).Find(target);
            if (existing is null)
            {
                return NotOnRoster(target);
            }

            ILogger logger = loggers.CreateLogger(typeof(ApiKeyEndpoints).FullName!);
            StoredKeyRemoval removal = await DeleteStoredKeysAsync(store, existing, [SecretKind.ConsoleApiKey], keepStoredKey ?? false, logger, cancellationToken);
            if (removal.Refusal is string refusal)
            {
                return Refused("StoredKeyKept", refusal);
            }

            if (removal.Warning is null)
            {
                LogKeyDeleted(logger, target.Value);
            }

            RosterEntry? updated = await RecordAsync(rosterFile, target, null, cancellationToken);
            return updated is null
                ? NotOnRoster(target)
                : Results.Ok(new ApiKeyDeletionView(DashboardAssembler.View(updated)!, removal.Warning));
        });
    }

    /// <summary>
    /// Deletes each kind of stored key for the account. A kind the roster
    /// records no key for has nothing to delete, so a failure there is
    /// ignored. For a recorded key, a store that cannot be reached or refuses
    /// is a refusal naming the cause, unless <paramref name="keepStoredKey"/>
    /// says to go ahead: then the key is left, and the warning (also logged)
    /// names it and the command that removes it by hand.
    /// </summary>
    internal static async Task<StoredKeyRemoval> DeleteStoredKeysAsync(
        ISecretStore store,
        RosterEntry entry,
        IEnumerable<SecretKind> kinds,
        bool keepStoredKey,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(kinds);
        List<string> left = [];
        foreach (SecretKind kind in kinds)
        {
            Result<Unit, string> deleted = await store.DeleteAsync(kind, entry.Email, cancellationToken);
            if (deleted.IsSuccess || !Recorded(entry, kind))
            {
                continue;
            }

            string target = SecretNames.Target(kind, entry.Email);
            if (!keepStoredKey)
            {
                LogStoreRefused(logger, entry.Email.Value);
                return new StoredKeyRemoval(
                    "The OS store could not delete the stored key " + target + ": " + deleted.Error
                    + " Nothing was removed from the roster. Fix that and try again, or retry with keepStoredKey=true to go ahead and leave the key in the store.",
                    null);
            }

            LogStoredKeyLeft(logger, target);
            left.Add("The stored key " + target + " was left in the OS store (" + deleted.Error + "); remove it by hand with: " + ManualRemoval(kind, entry.Email));
        }

        return new StoredKeyRemoval(null, left.Count == 0 ? null : string.Join(" ", left));
    }

    // Only the Console key has a roster record so far.
    private static bool Recorded(RosterEntry entry, SecretKind kind) =>
        kind == SecretKind.ConsoleApiKey && entry.ConsoleApiKeySetOn is not null;

    /// <summary>The command that removes a stored key by hand, on this platform.</summary>
    internal static string ManualRemoval(SecretKind kind, AccountEmail email) => OperatingSystem.IsWindows()
        ? "cmdkey /delete:" + SecretNames.Target(kind, email)
        : "secret-tool clear kind " + SecretNames.Kind(kind) + " email " + email.Value;

    /// <summary>
    /// Sets the date on the entry as the roster holds it at write time, so an
    /// edit that landed since the read above is kept. Null when the entry is gone.
    /// </summary>
    private static async Task<RosterEntry?> RecordAsync(RosterFile rosterFile, AccountEmail target, DateOnly? setOn, CancellationToken cancellationToken)
    {
        RosterEntry? updated = null;
        await rosterFile.UpdateAsync(
            roster =>
            {
                updated = roster.Find(target) is RosterEntry entry ? entry with { ConsoleApiKeySetOn = setOn } : null;
                return updated is null ? roster : roster.With(updated);
            },
            cancellationToken);
        return updated;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "the OS store refused a stored key for {Account}")]
    private static partial void LogStoreRefused(ILogger logger, string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "stored key {Target} was left in the OS store at the operator's request")]
    private static partial void LogStoredKeyLeft(ILogger logger, string target);
}

/// <summary>What deleting stored keys came to: a refusal, or done with an optional warning naming a key left behind.</summary>
internal sealed record StoredKeyRemoval(string? Refusal, string? Warning);

/// <summary>The roster entry after a key delete, and the warning when the key was left in the store.</summary>
internal sealed record ApiKeyDeletionView(RosterEntryView Roster, string? Warning);
