using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
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
            CredentialMutationGate gate,
            SwitchOptions options,
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
            ILogger logger = loggers.CreateLogger(typeof(ApiKeyEndpoints).FullName!);
            // The one credential gate, as account removal takes it, so a removal
            // cannot land between this check of the roster and its record: that
            // would leave a key in the store for an account the roster no longer names.
            using IDisposable? permit = await TryAcquireAsync(gate, options, cancellationToken);
            if (permit is null)
            {
                return Busy();
            }

            if ((await rosterFile.ReadAsync(cancellationToken)).Find(target) is null)
            {
                return NotOnRoster(target);
            }

            Result<Unit, string> usable = await store.ProbeAsync(cancellationToken);
            // Not the request's token from here: a write the store may still
            // complete must be followed by its record, whatever the browser does.
            Result<Unit, string> written = usable.IsSuccess
                ? await store.WriteAsync(SecretKind.ConsoleApiKey, target, key, CancellationToken.None)
                : usable;
            if (written.IsFailure)
            {
                LogStoreRefused(logger, target.Value);
                return Refused("KeyStoreUnavailable", written.Error);
            }

            LogKeyStored(logger, target.Value);
            var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
            Result<RosterEntry, string> recorded = await TryRecordAsync(rosterFile, target, today);
            if (recorded.IsSuccess)
            {
                return Results.Ok(DashboardAssembler.View(recorded.Value));
            }

            // A key the roster does not record is one the page cannot show or
            // delete, so it goes back out of the store.
            Result<Unit, string> undone = await store.DeleteAsync(SecretKind.ConsoleApiKey, target, CancellationToken.None);
            LogRecordFailedAfterWrite(logger, target.Value, undone.IsSuccess);
            return Failed("The key was stored but the roster could not record it ("
                + recorded.Error + "), so "
                + (undone.IsSuccess
                    ? "it was deleted from the store again. Try again."
                    : "it is still in the store and could not be deleted (" + undone.Error + "); remove it by hand: " + ManualRemoval(SecretKind.ConsoleApiKey, target)));
        });

        mutations.MapDelete("/accounts/{email}/api-key", static async (
            string email,
            bool? keepStoredKey,
            RosterFile rosterFile,
            ISecretStore store,
            CredentialMutationGate gate,
            SwitchOptions options,
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
            ILogger logger = loggers.CreateLogger(typeof(ApiKeyEndpoints).FullName!);
            using IDisposable? permit = await TryAcquireAsync(gate, options, cancellationToken);
            if (permit is null)
            {
                return Busy();
            }

            RosterEntry? existing = (await rosterFile.ReadAsync(cancellationToken)).Find(target);
            if (existing is null)
            {
                return NotOnRoster(target);
            }

            // Past the read, the request's token is not consulted: a delete the
            // store has done must reach the roster.
            StoredKeyRemoval removal = await DeleteStoredKeysAsync(store, existing, [SecretKind.ConsoleApiKey], keepStoredKey ?? false, logger, CancellationToken.None);
            if (removal.Refusal is string refusal)
            {
                return Refused("StoredKeyKept", refusal);
            }

            // "deleted" only when the store said so; a key left behind is logged
            // where it is left, and an unrecorded key the store could not reach
            // is not called deleted.
            if (removal.AllDeleted)
            {
                LogKeyDeleted(logger, target.Value);
            }
            else if (removal.Warning is null)
            {
                LogNoKeyRecorded(logger, target.Value);
            }

            Result<RosterEntry, string> recorded = await TryRecordAsync(rosterFile, target, null);
            if (recorded.IsFailure)
            {
                LogBadgeStale(logger, target.Value);
                return Failed("The key is out of the store but the roster could not record that (" + recorded.Error
                    + "), so the card still shows a key. Delete it again to clear the badge.");
            }

            return Results.Ok(new ApiKeyDeletionView(DashboardAssembler.View(recorded.Value)!, removal.Warning));
        });

        mutations.MapPost("/accounts/{email}/api-key/push", PushAsync);
    }

    /// <summary>
    /// Re-sends the stored Console key to the key secret of every role the
    /// account backs that has one, with no <c>setup-token</c> run: the step after
    /// replacing a key, and the repair for a role left split. Every secret's
    /// visibility is read back first and the first read that fails ends it,
    /// before any write. The sets run one at a time; a failed one does not stop
    /// the rest, except a rate limit, which stops every one after it. The
    /// roster records the halves that were set.
    /// </summary>
    private static async Task<IResult> PushAsync(
        string email,
        RosterFile rosterFile,
        ISecretStore store,
        ICiSecretWriter writer,
        IGhApiReader github,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rosterFile);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggers);
        Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
        if (parsed.IsFailure)
        {
            return Results.BadRequest(new { error = parsed.Error });
        }

        AccountEmail target = parsed.Value;
        RosterEntry? entry = (await rosterFile.ReadAsync(cancellationToken)).Find(target);
        if (entry is null)
        {
            return NotOnRoster(target);
        }

        List<CiTokenSecret> secrets = [.. entry.CiRoles.Select(static held => held.Role.KeySecret).OfType<CiTokenSecret>()];
        if (secrets.Count == 0)
        {
            return Refused("NoKeyHalf", target.Value + " backs no role with a key half; assign one with Generate CI token.");
        }

        if (entry.ConsoleApiKeySetOn is null)
        {
            return Refused("NoConsoleKey", "paste this account's Console key first (Set API key).");
        }

        List<SecretTarget> targets = [];
        foreach (CiTokenSecret secret in secrets)
        {
            Result<SecretTarget, GhApiFailure> access = await github.ReadAccessAsync(secret, cancellationToken);
            if (access.IsFailure)
            {
                return Refused("SecretUnreadable", CiTokenEndpoints.Describe(access.Error));
            }

            targets.Add(access.Value);
        }

        Result<string?, string> key = await store.ReadAsync(SecretKind.ConsoleApiKey, target, cancellationToken);
        if (key.IsFailure)
        {
            return Refused("KeyStoreUnavailable", key.Error);
        }

        if (key.Value is not string value)
        {
            return Refused("NoConsoleKey", "the OS store holds no Console key for " + target.Value + "; paste it again (Set API key).");
        }

        // From the first write on, the request's token is not consulted: a set
        // that reached GitHub must reach the roster.
        ILogger logger = loggers.CreateLogger(typeof(ApiKeyEndpoints).FullName!);
        List<(CiTokenSecret Secret, DateTimeOffset SetAt)> sent = [];
        List<string> failed = [];
        bool stopped = false;
        foreach (SecretTarget secret in targets)
        {
            Result<Unit, string> set = stopped
                ? Result<Unit, string>.Failure("not sent, because an earlier write hit GitHub's rate limit")
                : await writer.SetAsync(secret.Secret, secret.Visibility, secret.Repositories, value, CancellationToken.None);
            if (set.IsSuccess)
            {
                LogKeyPushed(logger, target.Value, secret.Secret.Name);
                sent.Add((secret.Secret, clock.GetUtcNow()));
                continue;
            }

            LogKeyPushFailed(logger, target.Value, secret.Secret.Name);
            failed.Add(secret.Secret + ": " + set.Error);
            stopped |= set.Error == GhSecretWriter.RateLimitMessage;
        }

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        RosterEntry? updated = null;
        await rosterFile.UpdateAsync(
            roster =>
            {
                updated = roster.Find(target);
                foreach ((CiTokenSecret secret, DateTimeOffset setAt) in sent)
                {
                    if (updated?.CiRoles.FirstOrDefault(held => secret.Equals(held.Role.KeySecret)) is CiRoleAssignment held)
                    {
                        updated = updated.ClaimingKey(held.Role, today, setAt);
                    }
                }

                return updated is null ? roster : roster.With(updated);
            },
            CancellationToken.None,
            via: LaneVia.Push);

        string message = (sent.Count == 0 ? "No key secret was set." : "Set " + string.Join(", ", sent.Select(static half => half.Secret.ToString())) + " from " + target.Value + ".")
            + (failed.Count == 0 ? string.Empty : " Not set: " + string.Join("; ", failed) + ".");
        return Results.Ok(new ApiKeyPushView(DashboardAssembler.View(updated), message, failed.Count));
    }

    private static async Task<IDisposable?> TryAcquireAsync(CredentialMutationGate gate, SwitchOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return await gate.AcquireAsync(options.MutationGateTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static IResult Busy() =>
        Refused("MutationInProgress", "Another credential change is in progress; try again in a moment.");

    private static IResult Failed(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status500InternalServerError);

    /// <summary>
    /// Records the date with no cancellation, since the store has already
    /// changed. A failure is the reason; a vanished entry is one too.
    /// </summary>
    private static async Task<Result<RosterEntry, string>> TryRecordAsync(RosterFile rosterFile, AccountEmail target, DateOnly? setOn)
    {
        try
        {
            RosterEntry? updated = await RecordAsync(rosterFile, target, setOn, CancellationToken.None);
            return updated is null
                ? Result<RosterEntry, string>.Failure(target.Value + " is no longer on the roster")
                : Result<RosterEntry, string>.Success(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<RosterEntry, string>.Failure(exception.GetType().Name);
        }
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
        bool allDeleted = true;
        foreach (SecretKind kind in kinds)
        {
            Result<Unit, string> deleted = await store.DeleteAsync(kind, entry.Email, cancellationToken);
            allDeleted &= deleted.IsSuccess;
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
                    null,
                    AllDeleted: false);
            }

            string manual = ManualRemoval(kind, entry.Email);
            LogStoredKeyLeft(logger, target, manual);
            left.Add("The stored key " + target + " was left in the OS store (" + deleted.Error + "); remove it by hand: " + manual);
        }

        return new StoredKeyRemoval(null, left.Count == 0 ? null : string.Join(" ", left), allDeleted);
    }

    // Only the Console key has a roster record so far.
    private static bool Recorded(RosterEntry entry, SecretKind kind) =>
        kind == SecretKind.ConsoleApiKey && entry.ConsoleApiKeySetOn is not null;

    /// <summary>The command that removes a stored key by hand, on this platform.</summary>
    // secret-tool clear removes only unlocked items, so the keyring is unlocked first.
    internal static string ManualRemoval(SecretKind kind, AccountEmail email) => OperatingSystem.IsWindows()
        ? "cmdkey /delete:" + SecretNames.Target(kind, email)
        : "unlock the keyring, then run secret-tool clear kind " + SecretNames.Kind(kind) + " email " + email.Value;

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "stored key {Target} was left in the OS store at the operator's request; to remove it by hand: {Command}")]
    private static partial void LogStoredKeyLeft(ILogger logger, string target, string command);

    [LoggerMessage(Level = LogLevel.Error, Message = "console API key for {Account} stored but not recorded on the roster; deleted from the store again: {Undone}")]
    private static partial void LogRecordFailedAfterWrite(ILogger logger, string account, bool undone);

    [LoggerMessage(Level = LogLevel.Error, Message = "console API key for {Account} deleted from the store but the roster still records it; the card's badge is stale")]
    private static partial void LogBadgeStale(ILogger logger, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "console API key for {Account} pushed to secret {Secret}")]
    private static partial void LogKeyPushed(ILogger logger, string account, string secret);

    [LoggerMessage(Level = LogLevel.Warning, Message = "console API key for {Account} could not be pushed to secret {Secret}")]
    private static partial void LogKeyPushFailed(ILogger logger, string account, string secret);

    [LoggerMessage(Level = LogLevel.Information, Message = "no stored console API key recorded for {Account}")]
    private static partial void LogNoKeyRecorded(ILogger logger, string account);
}

/// <summary>
/// What deleting stored keys came to: a refusal, or done with an optional
/// warning naming a key left behind. <c>AllDeleted</c> is true only when the
/// store confirmed every delete.
/// </summary>
internal sealed record StoredKeyRemoval(string? Refusal, string? Warning, bool AllDeleted);

/// <summary>The roster entry after a push, what was set and what was not, and how many secrets were not.</summary>
internal sealed record ApiKeyPushView(RosterEntryView? Roster, string Message, int Failed);

/// <summary>The roster entry after a key delete, and the warning when the key was left in the store.</summary>
internal sealed record ApiKeyDeletionView(RosterEntryView Roster, string? Warning);
