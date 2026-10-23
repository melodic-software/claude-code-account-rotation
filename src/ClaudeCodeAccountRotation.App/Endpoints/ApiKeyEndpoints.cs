using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Accounts;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Quota;
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
using static ClaudeCodeAccountRotation.App.Endpoints.EndpointRefusals;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// An account's Console API key, and its optional Admin key, each pasted once
/// on this machine and kept in the OS store. A key arrives in the request body
/// only, and nothing returns it: each route answers with the roster entry,
/// whose <c>consoleApiKeySetOn</c> and <c>adminKeySetOn</c> are all the page
/// knows, so loading the page never reads the store. A store answers with it
/// the store's name and where Push API key would send the key. With no usable store the
/// key is refused with the cause, and nothing is written.
/// </summary>
internal static partial class ApiKeyEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPut("/accounts/{email}/api-key", static (
            string email,
            JsonObject body,
            RosterFile rosterFile,
            ISecretStore store,
            CredentialMutationGate gate,
            SwitchOptions options,
            TimeProvider clock,
            ILoggerFactory loggers,
            LaneCatalogStore laneCatalog,
            CancellationToken cancellationToken) =>
            PutKeyAsync(SecretKind.ConsoleApiKey, email, body, rosterFile, store, gate, options, clock, loggers, laneCatalog, cancellationToken));

        mutations.MapDelete("/accounts/{email}/api-key", static (
            string email,
            bool? keepStoredKey,
            RosterFile rosterFile,
            ISecretStore store,
            CredentialMutationGate gate,
            SwitchOptions options,
            ILoggerFactory loggers,
            LaneCatalogStore laneCatalog,
            CancellationToken cancellationToken) =>
            DeleteKeyAsync(SecretKind.ConsoleApiKey, email, keepStoredKey, rosterFile, store, gate, options, loggers, laneCatalog, cancellationToken));

        MapAdminKey(mutations);
    }

    /// <summary>
    /// The optional Admin key, kept like the Console key, and the check that
    /// spends it: one listing of the organization's API keys through the Admin
    /// API, on the click only, to say whether the stored Console key is
    /// archived, inactive, expired or near its expiry. The listing is key
    /// metadata, not a model call. Neither key reaches a response or a log.
    /// </summary>
    private static void MapAdminKey(RouteGroupBuilder mutations)
    {
        mutations.MapPut("/accounts/{email}/admin-key", static (
            string email,
            JsonObject body,
            RosterFile rosterFile,
            ISecretStore store,
            CredentialMutationGate gate,
            SwitchOptions options,
            TimeProvider clock,
            ILoggerFactory loggers,
            LaneCatalogStore laneCatalog,
            CancellationToken cancellationToken) =>
            PutKeyAsync(SecretKind.AdminKey, email, body, rosterFile, store, gate, options, clock, loggers, laneCatalog, cancellationToken));

        mutations.MapDelete("/accounts/{email}/admin-key", static (
            string email,
            bool? keepStoredKey,
            RosterFile rosterFile,
            ISecretStore store,
            CredentialMutationGate gate,
            SwitchOptions options,
            ILoggerFactory loggers,
            LaneCatalogStore laneCatalog,
            CancellationToken cancellationToken) =>
            DeleteKeyAsync(SecretKind.AdminKey, email, keepStoredKey, rosterFile, store, gate, options, loggers, laneCatalog, cancellationToken));

        mutations.MapPost("/accounts/{email}/api-key/check", static async (
            string email,
            RosterFile rosterFile,
            ISecretStore store,
            IAdminApiKeysClient admin,
            TimeProvider clock,
            ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(admin);
            ArgumentNullException.ThrowIfNull(clock);
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

            Result<string?, string> consoleKey = await store.ReadAsync(SecretKind.ConsoleApiKey, target, cancellationToken);
            if (consoleKey.IsFailure)
            {
                return Refused("KeyStoreUnavailable", consoleKey.Error);
            }

            if (consoleKey.Value is null)
            {
                return Refused("NoConsoleKey", "No Console API key is stored for " + target.Value + " on this machine. Set it first.");
            }

            Result<string?, string> adminKey = await store.ReadAsync(SecretKind.AdminKey, target, cancellationToken);
            if (adminKey.IsFailure)
            {
                return Refused("KeyStoreUnavailable", adminKey.Error);
            }

            if (adminKey.Value is null)
            {
                return Refused("NoAdminKey", "No Admin key is stored for " + target.Value + " on this machine. Set one to check the Console key.");
            }

            ILogger logger = loggers.CreateLogger(typeof(ApiKeyEndpoints).FullName!);
            Result<IReadOnlyList<ListedApiKey>, AdminApiFailure> listed = await admin.ListAsync(adminKey.Value, cancellationToken);
            if (listed.IsFailure)
            {
                LogCheckFailed(logger, target.Value, listed.Error.Kind);
                return listed.Error.Kind switch
                {
                    AdminApiFailureKind.Unavailable => Refused("AdminApiUnavailable", listed.Error.Message),
                    AdminApiFailureKind.RateLimited => Refused("RateLimited", listed.Error.Message),
                    _ => Results.Json(new { error = listed.Error.Message }, statusCode: StatusCodes.Status502BadGateway),
                };
            }

            DateTimeOffset now = clock.GetUtcNow();
            KeyHintMatch match = PartialKeyHint.Match(consoleKey.Value, listed.Value);
            ApiKeyCheckView view = match.Key is ListedApiKey found
                ? new ApiKeyCheckView("found", null, found.Name, found.Status, found.ExpiresAt, Flag(ApiKeyStanding.Of(found, now)), now)
                : new ApiKeyCheckView("cannotTell", match.Reason, null, null, null, null, now);
            string standing = view.Flag ?? view.Status ?? "none";
            LogChecked(logger, target.Value, view.Outcome, standing);
            return Results.Ok(view);
        });

        mutations.MapPost("/accounts/{email}/api-key/push", PushAsync);
    }

    /// <summary>
    /// Sends the stored Console key, with no <c>setup-token</c> run, to the
    /// key secret of each role in <c>roles</c> when the body names some (the
    /// Set API key role picker: keyed roles only, and only roles the lane
    /// catalog lists when one is configured), else to every keyed role the
    /// account backs: the step after replacing a key, and the repair for a
    /// role left split. The cost gate runs first (<see cref="CostGate"/>).
    /// Every secret's visibility is read back next and the first read that
    /// fails ends it, before any write. The sets run one at a time; a failed
    /// one does not stop the rest, except a rate limit, which stops every one
    /// after it. The roster records the halves that were set, taking each off
    /// the account that held it before.
    /// </summary>
    private static async Task<IResult> PushAsync(
        string email,
        HttpRequest request,
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
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rosterFile);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(laneCatalog);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggers);
        Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
        if (parsed.IsFailure)
        {
            return Results.BadRequest(new { error = parsed.Error });
        }

        Result<JsonObject?, string> body = await OptionalBodyAsync(request, cancellationToken);
        if (body.IsFailure)
        {
            return Results.BadRequest(new { error = body.Error });
        }

        Result<IReadOnlyList<CiRole>?, string> picked = body.Value is JsonObject named
            ? CiTokenEndpoints.ParseRoles(named)
            : Result<IReadOnlyList<CiRole>?, string>.Success(null);
        if (picked.IsFailure)
        {
            return Results.BadRequest(new { error = picked.Error });
        }

        AccountEmail target = parsed.Value;
        RosterEntry? entry = (await rosterFile.ReadAsync(cancellationToken)).Find(target);
        if (entry is null)
        {
            return NotOnRoster(target);
        }

        // Which roles are keyed, from the catalog when one is configured; one
        // that cannot be read refuses before any key moves.
        Result<Func<CiRole, bool>, string> keying = await laneCatalog.KeyedForWriteAsync(cancellationToken);
        if (keying.IsFailure)
        {
            return Refused("CatalogUnreadable", keying.Error);
        }

        IReadOnlyList<CiRole> roles;
        if (picked.Value is IReadOnlyList<CiRole> chosen)
        {
            // With a catalog, only the roles it lists; then only keyed ones.
            LaneCatalog? catalog = laneCatalog.Source is null ? null : laneCatalog.Snapshot.Catalog;
            bool Listed(CiRole role) => catalog is null
                || (role.Scope == CiSecretScope.Organization
                    && string.Equals(role.Owner, catalog.Organization, StringComparison.OrdinalIgnoreCase)
                    && catalog.Roles.Any(named => string.Equals(named.Prefix, role.Prefix, StringComparison.OrdinalIgnoreCase)));
            if (chosen.FirstOrDefault(role => !Listed(role) || !keying.Value(role)) is CiRole refused)
            {
                return Results.BadRequest(new
                {
                    error = "roles: " + refused + (Listed(refused) ? " takes no API key: its lanes read a token only" : " is not in the lane catalog"),
                });
            }

            roles = chosen;
        }
        else
        {
            roles = PushTargets(entry, keying.Value);
            if (roles.Count == 0)
            {
                return Refused("NoKeyHalf", target.Value + " backs no role with a key half; pick one in Set API key, or assign one with Generate CI token.");
            }
        }

        if (entry.ConsoleApiKeySetOn is null)
        {
            return Refused("NoConsoleKey", "paste this account's Console key first (Set API key).");
        }

        if (await CostGateAsync(target, CostAnswer(body.Value), rosterFile, quota, clock, cancellationToken) is IResult gated)
        {
            return gated;
        }

        List<(CiRole Role, SecretTarget Secret)> targets = [];
        foreach (CiRole role in roles)
        {
            Result<SecretTarget, GhApiFailure> access = await github.ReadAccessAsync(role.KeySecret!, cancellationToken);
            if (access.IsFailure)
            {
                return Refused("SecretUnreadable", CiTokenEndpoints.Describe(access.Error));
            }

            targets.Add((role, access.Value));
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
        List<(CiRole Role, DateTimeOffset SetAt)> sent = [];
        List<string> failed = [];
        bool stopped = false;
        foreach ((CiRole role, SecretTarget secret) in targets)
        {
            Result<Unit, string> set = stopped
                ? Result<Unit, string>.Failure("not sent, because an earlier write hit GitHub's rate limit")
                : await writer.SetAsync(secret.Secret, secret.Visibility, secret.Repositories, value, CancellationToken.None);
            if (set.IsSuccess)
            {
                LogKeyPushed(logger, target.Value, secret.Secret.Name);
                sent.Add((role, clock.GetUtcNow()));
                continue;
            }

            LogKeyPushFailed(logger, target.Value, secret.Secret.Name);
            failed.Add(secret.Secret + ": " + set.Error);
            stopped |= set.Error == GhSecretWriter.RateLimitMessage;
        }

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        string message = (sent.Count == 0 ? "No key secret was set." : "Set " + string.Join(", ", sent.Select(static half => half.Role.KeySecret!.ToString())) + " from " + target.Value + ".")
            + (failed.Count == 0 ? string.Empty : " Not set: " + string.Join("; ", failed) + ".");
        RosterEntry? updated = null;
        try
        {
            await rosterFile.UpdateAsync(
                roster =>
                {
                    updated = roster.Find(target);
                    foreach ((CiRole role, DateTimeOffset setAt) in sent)
                    {
                        updated = updated?.ClaimingKey(role, today, setAt);
                    }

                    return updated is null ? roster : roster.With(updated);
                },
                CancellationToken.None,
                via: LaneVia.Push);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The secrets are set whatever went wrong here, so the answer says which.
            return Failed(message + " The roster could not record it (" + exception.GetType().Name + "), so the card does not show it yet; push again to record it.");
        }

        return Results.Ok(new ApiKeyPushView(DashboardAssembler.View(updated, keying.Value), message, failed.Count));
    }

    /// <summary>
    /// The owner's answer to the cost gate from a body's <c>costSettings</c>:
    /// <c>{"autoReload": "off", "extraUsage": "off"}</c>. Anything missing is unknown.
    /// </summary>
    internal static CostSettingsAnswer CostAnswer(JsonObject? body) =>
        body?["costSettings"] is JsonObject settings
            ? new CostSettingsAnswer(CostSettingsAnswer.Setting(Text(settings, "autoReload")), CostSettingsAnswer.Setting(Text(settings, "extraUsage")))
            : CostSettingsAnswer.None;

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>
    /// The cost gate every write of a Console key to a key secret passes
    /// first: null when it lets the key through, else the refusal. The
    /// account's record follows the answer: the date on a pass, cleared when
    /// the owner says a setting is on, and left alone when the owner did not
    /// answer. Nothing has been written to GitHub when this runs.
    /// </summary>
    internal static async Task<IResult?> CostGateAsync(
        AccountEmail email,
        CostSettingsAnswer answer,
        RosterFile rosterFile,
        QuotaState quota,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(rosterFile);
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(clock);
        CostGateRefusal? refusal = CostGate.Check(email, answer, quota.MergedFor(email)?.Merged.ExtraUsage);
        if (refusal?.Code != CostGate.Unconfirmed)
        {
            DateOnly? record = refusal is null ? DateOnly.FromDateTime(clock.GetLocalNow().DateTime) : null;
            await rosterFile.UpdateAsync(
                roster => roster.Find(email) is RosterEntry entry ? roster.With(entry with { CostSettingsConfirmedOn = record }) : roster,
                cancellationToken);
        }

        return refusal is null ? null : Refused(refusal.Code, refusal.Message);
    }

    /// <summary>The request's JSON object, or null when it has no body; anything else is the error.</summary>
    private static async Task<Result<JsonObject?, string>> OptionalBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0 || (request.ContentLength is null && !request.HasJsonContentType()))
        {
            return Result<JsonObject?, string>.Success(null);
        }

        try
        {
            return await request.ReadFromJsonAsync<JsonNode>(cancellationToken) switch
            {
                null => Result<JsonObject?, string>.Success(null),
                JsonObject node => Result<JsonObject?, string>.Success(node),
                _ => Result<JsonObject?, string>.Failure("the body is a JSON object"),
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return Result<JsonObject?, string>.Failure("the body is not JSON");
        }
        catch (InvalidOperationException)
        {
            return Result<JsonObject?, string>.Failure("the body is a JSON object");
        }
    }

    private static string? Flag(ApiKeyWarning? flag) => flag switch
    {
        ApiKeyWarning.Archived => "archived",
        ApiKeyWarning.Inactive => "inactive",
        ApiKeyWarning.Expired => "expired",
        ApiKeyWarning.ExpiresSoon => "expiresSoon",
        _ => null,
    };

    /// <summary>
    /// Stores a pasted key of <paramref name="kind"/> and records the day on
    /// the roster. The key arrives in the body only and is never answered with.
    /// </summary>
    private static async Task<IResult> PutKeyAsync(
        SecretKind kind,
        string email,
        JsonObject body,
        RosterFile rosterFile,
        ISecretStore store,
        CredentialMutationGate gate,
        SwitchOptions options,
        TimeProvider clock,
        ILoggerFactory loggers,
        LaneCatalogStore laneCatalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(laneCatalog);
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
            return Results.BadRequest(new { error = "key must be the " + Title(kind) + ", as text" });
        }

        AccountEmail target = parsed.Value;
        string label = Label(kind);
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
            ? await store.WriteAsync(kind, target, key, CancellationToken.None)
            : usable;
        if (written.IsFailure)
        {
            LogStoreRefused(logger, target.Value);
            return Refused("KeyStoreUnavailable", written.Error);
        }

        LogKeyStored(logger, label, target.Value);
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        Result<RosterEntry, string> recorded = await TryRecordAsync(rosterFile, kind, target, today);
        if (recorded.IsSuccess)
        {
            // An Admin key never goes to GitHub, so it has no push targets.
            IReadOnlyList<string> pushesTo = kind == SecretKind.ConsoleApiKey
                ? [.. PushTargets(recorded.Value, laneCatalog.IsKeyedNow).Select(static role => role.KeySecret!.ToString())]
                : [];
            return Results.Ok(new ApiKeyStoredView(DashboardAssembler.View(recorded.Value, laneCatalog.IsKeyedNow)!, store.DisplayName, pushesTo));
        }

        // A key the roster does not record is one the page cannot show or
        // delete, so it goes back out of the store.
        Result<Unit, string> undone = await store.DeleteAsync(kind, target, CancellationToken.None);
        LogRecordFailedAfterWrite(logger, label, target.Value, undone.IsSuccess);
        return Failed("The key was stored but the roster could not record it ("
            + recorded.Error + "), so "
            + (undone.IsSuccess
                ? "it was deleted from the store again. Try again."
                : "it is still in the store and could not be deleted (" + undone.Error + "); remove it by hand: " + ManualRemoval(kind, target)));
    }

    private static async Task<IResult> DeleteKeyAsync(
        SecretKind kind,
        string email,
        bool? keepStoredKey,
        RosterFile rosterFile,
        ISecretStore store,
        CredentialMutationGate gate,
        SwitchOptions options,
        ILoggerFactory loggers,
        LaneCatalogStore laneCatalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        ArgumentNullException.ThrowIfNull(laneCatalog);
        Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
        if (parsed.IsFailure)
        {
            return Results.BadRequest(new { error = parsed.Error });
        }

        AccountEmail target = parsed.Value;
        string label = Label(kind);
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
        StoredKeyRemoval removal = await DeleteStoredKeysAsync(store, existing, [kind], keepStoredKey ?? false, logger, CancellationToken.None);
        if (removal.Refusal is string refusal)
        {
            return Refused("StoredKeyKept", refusal);
        }

        // "deleted" only when the store said so; a key left behind is logged
        // where it is left, and an unrecorded key the store could not reach
        // is not called deleted.
        if (removal.AllDeleted)
        {
            LogKeyDeleted(logger, label, target.Value);
        }
        else if (removal.Warning is null)
        {
            LogNoKeyRecorded(logger, label, target.Value);
        }

        Result<RosterEntry, string> recorded = await TryRecordAsync(rosterFile, kind, target, null);
        if (recorded.IsFailure)
        {
            LogBadgeStale(logger, label, target.Value);
            return Failed("The key is out of the store but the roster could not record that (" + recorded.Error
                + "), so the card still shows a key. Delete it again to clear the badge.");
        }

        return Results.Ok(new ApiKeyDeletionView(DashboardAssembler.View(recorded.Value, laneCatalog.IsKeyedNow)!, removal.Warning));
    }

    /// <summary>
    /// The key secrets Push API key sends the stored Console key to: one per
    /// keyed role the account backs, never one in a single repository that an
    /// earlier roster may still record.
    /// </summary>
    internal static IReadOnlyList<CiRole> PushTargets(RosterEntry entry, Func<CiRole, bool> keyed) =>
        [.. entry.CiRoles
            .Select(static held => held.Role)
            .Where(role => keyed(role) && role.KeySecret is { Scope: CiSecretScope.Organization })];

    /// <summary>The key's name in a log line.</summary>
    private static string Label(SecretKind kind) => kind == SecretKind.AdminKey ? "Admin key" : "console API key";

    /// <summary>The key's name in a message to the operator.</summary>
    private static string Title(SecretKind kind) => kind == SecretKind.AdminKey ? "Admin key" : "Console API key";

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
    private static async Task<Result<RosterEntry, string>> TryRecordAsync(RosterFile rosterFile, SecretKind kind, AccountEmail target, DateOnly? setOn)
    {
        try
        {
            RosterEntry? updated = await RecordAsync(rosterFile, kind, target, setOn, CancellationToken.None);
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

    private static bool Recorded(RosterEntry entry, SecretKind kind) =>
        (kind == SecretKind.AdminKey ? entry.AdminKeySetOn : entry.ConsoleApiKeySetOn) is not null;

    /// <summary>The command that removes a stored key by hand, on this platform.</summary>
    // secret-tool clear removes only unlocked items, so the keyring is unlocked first.
    internal static string ManualRemoval(SecretKind kind, AccountEmail email) => OperatingSystem.IsWindows()
        ? "cmdkey /delete:" + SecretNames.Target(kind, email)
        : "unlock the keyring, then run secret-tool clear kind " + SecretNames.Kind(kind) + " email " + email.Value;

    /// <summary>
    /// Sets the date on the entry as the roster holds it at write time, so an
    /// edit that landed since the read above is kept. Null when the entry is gone.
    /// </summary>
    private static async Task<RosterEntry?> RecordAsync(RosterFile rosterFile, SecretKind kind, AccountEmail target, DateOnly? setOn, CancellationToken cancellationToken)
    {
        RosterEntry? updated = null;
        await rosterFile.UpdateAsync(
            roster =>
            {
                updated = roster.Find(target) is not RosterEntry entry ? null
                    : kind == SecretKind.AdminKey ? entry with { AdminKeySetOn = setOn }
                    : entry with { ConsoleApiKeySetOn = setOn };
                return updated is null ? roster : roster.With(updated);
            },
            cancellationToken);
        return updated;
    }

    private static IResult NotOnRoster(AccountEmail email) =>
        Results.NotFound(new { error = email.Value + " is not on the roster." });

    // The account and the outcome only: never the key, and not the store's
    // own diagnostic either, which reaches the page instead.
    [LoggerMessage(Level = LogLevel.Information, Message = "{Key} for {Account} stored")]
    private static partial void LogKeyStored(ILogger logger, string key, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Key} for {Account} deleted")]
    private static partial void LogKeyDeleted(ILogger logger, string key, string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the OS store refused a stored key for {Account}")]
    private static partial void LogStoreRefused(ILogger logger, string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "stored key {Target} was left in the OS store at the operator's request; to remove it by hand: {Command}")]
    private static partial void LogStoredKeyLeft(ILogger logger, string target, string command);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Key} for {Account} stored but not recorded on the roster; deleted from the store again: {Undone}")]
    private static partial void LogRecordFailedAfterWrite(ILogger logger, string key, string account, bool undone);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Key} for {Account} deleted from the store but the roster still records it; the card's badge is stale")]
    private static partial void LogBadgeStale(ILogger logger, string key, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "console API key for {Account} pushed to secret {Secret}")]
    private static partial void LogKeyPushed(ILogger logger, string account, string secret);

    [LoggerMessage(Level = LogLevel.Warning, Message = "console API key for {Account} could not be pushed to secret {Secret}")]
    private static partial void LogKeyPushFailed(ILogger logger, string account, string secret);

    [LoggerMessage(Level = LogLevel.Information, Message = "no stored {Key} recorded for {Account}")]
    private static partial void LogNoKeyRecorded(ILogger logger, string key, string account);

    [LoggerMessage(Level = LogLevel.Information, Message = "API key check for {Account}: {Outcome}, {Standing}")]
    private static partial void LogChecked(ILogger logger, string account, string outcome, string standing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "API key check for {Account} could not list keys: {Kind}")]
    private static partial void LogCheckFailed(ILogger logger, string account, AdminApiFailureKind kind);
}

/// <summary>
/// What deleting stored keys came to: a refusal, or done with an optional
/// warning naming a key left behind. <c>AllDeleted</c> is true only when the
/// store confirmed every delete.
/// </summary>
internal sealed record StoredKeyRemoval(string? Refusal, string? Warning, bool AllDeleted);

/// <summary>The roster entry after a push, what was set and what was not, and how many secrets were not.</summary>
internal sealed record ApiKeyPushView(RosterEntryView? Roster, string Message, int Failed);

/// <summary>
/// The roster entry after a key is stored, the store's name as the page says
/// it, and, for a Console key, the key secrets Push API key would send it to.
/// </summary>
internal sealed record ApiKeyStoredView(RosterEntryView Roster, string StoredIn, IReadOnlyList<string> PushesTo);

/// <summary>The roster entry after a key delete, and the warning when the key was left in the store.</summary>
internal sealed record ApiKeyDeletionView(RosterEntryView Roster, string? Warning);

/// <summary>
/// What the Admin API says about the stored Console key: <c>found</c> with the
/// key's name, status, expiry and a flag (<c>archived</c>, <c>inactive</c>,
/// <c>expired</c>, <c>expiresSoon</c>, or null when it is fine), or
/// <c>cannotTell</c> with the reason. Never a key or its hint.
/// </summary>
internal sealed record ApiKeyCheckView(
    string Outcome,
    string? Reason,
    string? Name,
    string? Status,
    DateTimeOffset? ExpiresAt,
    string? Flag,
    DateTimeOffset CheckedAt);
