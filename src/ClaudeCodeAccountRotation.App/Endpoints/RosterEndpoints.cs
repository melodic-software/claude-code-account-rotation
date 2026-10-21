using System.Globalization;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Accounts;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Configuration;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// Add, edit, remove, and adopt-live: everything the operator needs to get the
/// other nine accounts onto this machine without editing a file by hand.
/// <para>
/// Only Max accounts join. The tier is read from the CLI under the account's
/// own folder and from the folder's account block; a Team or Enterprise seat is
/// refused with its reason, because that seat exposes no usage buckets and is
/// never rotated. An account with nothing to read yet joins as "needs login"
/// and is judged again once it has a login.
/// </para>
/// <para>
/// Removal revokes by default (<c>?logout=true</c>). Deleting a folder leaves
/// recoverable bytes holding a refresh token valid for the rest of its 28 days,
/// so the revocation is the removal, and a failed logout refuses the delete
/// rather than silently leaving a live token behind. <c>?logout=false</c> is
/// the operator's deliberate override: the folder goes and the response says
/// the token was not revoked. A folder that holds no pair skips the logout
/// entirely; there is nothing to revoke. The revocation and the folder deletion
/// are each logged with the account and the outcome, and neither line carries
/// the token or the logout's own diagnostic. A folder whose pair is stranded in
/// recovery is put back first, so the revocation reaches the lineage that is
/// actually alive.
/// </para>
/// </summary>
internal static partial class RosterEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPost("/accounts", static async (
            JsonObject body,
            RosterFile rosterFile,
            ProfileFolderStore profiles,
            ClaudeStateFile stateFile,
            IClaudeCliAuthStatus cli,
            ClaudeCodeAccountRotationConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(body);
            Result<AccountEmail, string> parsed = AccountEmail.Parse(Text(body, "email") ?? string.Empty);
            if (parsed.IsFailure)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            AccountEmail email = parsed.Value;

            // Read early so a duplicate spawns no CLI and touches no folder; the
            // write below checks again on the roster it is handed.
            if ((await rosterFile.ReadAsync(cancellationToken)).Find(email) is not null)
            {
                return Refused("AlreadyOnRoster", email.Value + " is already on the roster.");
            }

            Result<BrowserFamily?, string> browser = ParseBrowser(body, "browser");
            if (browser.IsFailure)
            {
                return Results.BadRequest(new { error = browser.Error });
            }

            Result<string?, string> directory = ParseProfileDirectory(body);
            if (directory.IsFailure)
            {
                return Results.BadRequest(new { error = directory.Error });
            }

            (MaxTierVerdict verdict, string? reason) = await JudgeAsync(email, profiles, stateFile, cli, configuration, cancellationToken);
            if (verdict == MaxTierVerdict.Refused)
            {
                return Refused("NotAMaxAccount", reason!);
            }

            ParkedProfile folder = await profiles.EnsureFolderAsync(email, cancellationToken);
            RosterEntry entry = new(
                email,
                Text(body, "alias"),
                browser.Value,
                directory.Value,
                Paused: false,
                Text(body, "notes"));
            bool added = false;
            await rosterFile.UpdateAsync(
                roster =>
                {
                    added = roster.Find(email) is null;
                    return added ? roster.With(entry) : roster;
                },
                cancellationToken);
            if (!added)
            {
                return Refused("AlreadyOnRoster", email.Value + " is already on the roster.");
            }

            return Results.Ok(new AccountCardView(
                email.Value,
                IsLive: false,
                folder.HasCredentials,
                folder.FolderPath,
                // Nothing has read this account yet, and the card still carries the
                // rows the page always shows: the three-row shape is the server's
                // to keep, whichever route hands a card back.
                UsageView.Unread,
                UsageNote: null,
                RefreshStateView.Idle,
                DashboardAssembler.View(entry)));
        });

        routes.MapGet("/api/lane-history", static async (
            string? since,
            RosterFile rosterFile,
            CancellationToken cancellationToken) =>
        {
            DateTimeOffset? from = null;
            if (since is not null)
            {
                if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedSince))
                {
                    return Results.BadRequest(new { error = "since must be a date or an ISO 8601 time" });
                }

                from = parsedSince;
            }

            IReadOnlyList<LaneHistoryEntry> entries = await rosterFile.History.ReadAsync(from, cancellationToken);
            return Results.Ok(new JsonArray([.. entries.Select(static entry => (JsonNode)entry.ToJson())]));
        });

        mutations.MapPatch("/accounts/{email}", static async (
            string email,
            JsonObject body,
            RosterFile rosterFile,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(body);
            Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
            if (parsed.IsFailure)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            Result<BrowserFamily?, string> browser = ParseBrowser(body, "browser");
            if (browser.IsFailure)
            {
                return Results.BadRequest(new { error = browser.Error });
            }

            Result<string?, string> directory = ParseProfileDirectory(body);
            if (directory.IsFailure)
            {
                return Results.BadRequest(new { error = directory.Error });
            }

            // The operator's own record of when they ran `claude setup-token`;
            // nothing here reads GitHub or the token itself.
            Result<DateOnly?, string> ciTokenGeneratedOn = ParseDate(body, "ciTokenGeneratedOn");
            if (ciTokenGeneratedOn.IsFailure)
            {
                return Results.BadRequest(new { error = ciTokenGeneratedOn.Error });
            }

            Result<CiTokenSecret?, string> ciTokenSecret = ParseCiTokenSecret(body);
            if (ciTokenSecret.IsFailure)
            {
                return Results.BadRequest(new { error = ciTokenSecret.Error });
            }

            Result<IReadOnlyList<CiRoleAssignment>?, string> ciRoles = ParseCiRoles(body);
            if (ciRoles.IsFailure)
            {
                return Results.BadRequest(new { error = ciRoles.Error });
            }

            Result<IReadOnlyList<CiRole>, string> removeCiRoles = ParseRemoveCiRoles(body);
            if (removeCiRoles.IsFailure)
            {
                return Results.BadRequest(new { error = removeCiRoles.Error });
            }

            // Built from the roster as the write finds it, so an assignment that
            // landed after the page was drawn is what this edit is judged against.
            IResult? refusal = null;
            RosterEntry? updated = null;
            await rosterFile.UpdateAsync(
                roster =>
                {
                    if (roster.Find(parsed.Value) is not RosterEntry existing)
                    {
                        refusal = Results.NotFound(new { error = parsed.Value.Value + " is not on the roster." });
                        return roster;
                    }

                    (updated, refusal) = Edited(roster, existing, body, browser.Value, directory.Value, ciTokenGeneratedOn.Value, ciTokenSecret.Value, ciRoles.Value, removeCiRoles.Value);
                    return updated is null ? roster : roster.With(updated);
                },
                cancellationToken,
                via: LaneVia.Edit);
            return refusal ?? Results.Ok(DashboardAssembler.View(updated));
        });

        mutations.MapDelete("/accounts/{email}", static async (
            string email,
            bool? logout,
            bool? keepStoredKey,
            RosterFile rosterFile,
            ProfileFolderStore profiles,
            ClaudeStateFile stateFile,
            IClaudeCliLogout cli,
            ILoginSessionRunner logins,
            CredentialMutationGate gate,
            RecoveryFiles recovery,
            SharedStoreSlots slots,
            SwitchOptions options,
            ISecretStore secrets,
            ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(loggers);
            // A static class cannot be a logger type argument. The category is
            // still this type's name, which is what ILogger<T> would have used.
            ILogger logger = loggers.CreateLogger(typeof(RosterEndpoints).FullName!);
            Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
            if (parsed.IsFailure)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            AccountEmail target = parsed.Value;
            IDisposable? permit = null;
            try
            {
                // A removal revokes a login and deletes a folder, so it is a credential
                // mutation and takes the one gate like every other. Without it the
                // delete could land between a switch's journal write and its unpark,
                // taking away the folder the switch is about to rename out of, past the
                // point where that switch can back out.
                try
                {
                    permit = await gate.AcquireAsync(options.MutationGateTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    return Refused("MutationInProgress", "Another credential change is in progress; try the removal again in a moment.");
                }

                // Read under the gate, not before it: a switch waiting on the gate can
                // make this account the live one, and the answer from before that
                // switch would be the answer to a question nobody asked.
                if ((await stateFile.ReadAccountBlockAsync(cancellationToken))?.Email == target)
                {
                    return Refused("AccountIsLive", "That account is live; switch to another account first.");
                }

                string folder = profiles.FolderPathFor(target);

                // A slot the other side holds is empty here and full there. Removing
                // it would skip the logout for want of a local pair, delete the
                // holder record that is the only thing on this side saying the pair
                // exists, take the account off the roster, and report it logged out
                // while its refresh token is live in the distro. Re-adding it would
                // then start a second, untracked login. Read under the gate with the
                // guards above, for the same reason they are.
                bool slotHoldsPair = File.Exists(Path.Combine(folder, FileSystemCredentialPairStore.FileName));
                if (await slots.ReadAsync(target, folder, slotHoldsPair, new WindowsHold(null, null), cancellationToken)
                    is { State: SlotState.HeldElsewhere or SlotState.InTransit })
                {
                    return Refused(
                        "HeldByOtherSide",
                        "The other side of this machine holds that account, or a hand-off for it is in flight; switch it away there first, so the removal can revoke the login it really has.");
                }

                // The escape hatch's own case, which the verdict above cannot
                // see: the slot reads Parked because the re-login put a fresh
                // family in it, and the family the other side still holds is
                // recorded beside it. Removing here would revoke the fresh
                // family, delete the record with the folder, and report success
                // while a live refresh token stayed in the distro with nothing
                // left on this machine naming it. The empty hold costs nothing
                // here: its only use is the "this side is live on the account"
                // half of the staleness rule, and the guard above has already
                // refused that case.
                if (await slots.ReadSupersededAsync(target, folder, slotHoldsPair, new WindowsHold(null, null), cancellationToken)
                    is { Stale: false } superseded)
                {
                    return Refused(
                        "SupersededFamilyStanding",
                        "The " + superseded.Record.Side.Value + " side still holds a second token family for that account, made when it was logged in again here. Switch that side away from it first, which quarantines that family; removing now would leave a live login there with nothing on this machine naming it.");
                }

                if (logins.IsRunningAgainst(folder))
                {
                    // The gate alone does not cover this: a login holds it only at the
                    // end, so the child is still alive on an authenticated pipe. Delete
                    // now and the operator's next paste recreates the folder with a
                    // fresh, unrevoked token for the account they were told was gone.
                    return Refused(
                        "LoginInProgress",
                        "A login is running against that account's folder; finish it or let it expire before removing the account.");
                }

                // From here on the request's own token is not consulted, the way a
                // switch stops consulting it past its journal write: a browser that
                // navigates away must not leave a revoked login beside a kept folder.
                CancellationToken committed = CancellationToken.None;

                // A stranded folder holds the pair a rotation replaced while the
                // working lineage waits in the recovery directory. Revoking what the
                // folder holds would kill the dead pair and leave the live one valid
                // for the rest of its login, in a file belonging to an account the
                // roster no longer names. So the restore runs first, under the gate
                // this handler already holds, and the logout revokes what the account
                // really has. A restore that cannot apply refuses the removal
                // outright rather than choosing which lineage to leave behind.
                if (recovery.HasRecoveryFor(folder) && !await recovery.RestoreAsync(folder, committed))
                {
                    return Refused(
                        "StrandedInRecovery",
                        "That account's rotated credentials are held in the recovery directory and could not be put back, so a removal now would revoke the wrong login. The warning on this page says what that account needs; remove it once the recovery file is resolved.");
                }

                // Read again rather than reusing the value the slot guard took: the
                // restore above can have put a pair back into the folder, and a
                // logout skipped on the older answer would leave that login valid.
                bool hasPair = File.Exists(Path.Combine(folder, FileSystemCredentialPairStore.FileName));
                bool revoke = logout ?? true;
                bool revoked = false;
                if (hasPair && revoke)
                {
                    Result<Unit, string> loggedOut = await cli.LogoutAsync(folder, committed);
                    if (loggedOut.IsFailure)
                    {
                        // loggedOut.Error reaches the page as a classified sentence.
                        // It does not reach the log: the port's diagnostic can repeat
                        // what the child printed.
                        LogLogoutFailed(logger, target.Value);
                        return Refused(
                            "LogoutFailed",
                            "That account's login could not be revoked, so the folder was kept: " + loggedOut.Error
                            + " Retry, or remove with logout=false to delete the folder without revoking.");
                    }

                    LogLogoutRevoked(logger, target.Value);
                    revoked = true;
                }

                if (Directory.Exists(folder))
                {
                    // Registers the folder with the store's own discovery guard, which
                    // refuses to delete a path it has never listed. A folder that has
                    // never been logged in carries no identity and so is never listed.
                    await profiles.EnsureFolderAsync(target, committed);
                    await profiles.DeleteFolderAsync(folder, committed);
                }

                // Both stored keys go with the account. A recorded key the store
                // cannot delete keeps the entry and its record, so a retry (which
                // finds no folder left to revoke or delete) comes back here, unless
                // the operator says to leave the key: then the response names it.
                RosterEntry entry = (await rosterFile.ReadAsync(committed)).Find(target) ?? new RosterEntry(target);
                StoredKeyRemoval keys = await ApiKeyEndpoints.DeleteStoredKeysAsync(
                    secrets, entry, Enum.GetValues<SecretKind>(), keepStoredKey ?? false, logger, committed);
                if (keys.Refusal is string keyRefusal)
                {
                    return Refused("StoredKeyKept", "The account's login and folder were handled, but it stays on the roster. " + keyRefusal);
                }

                await rosterFile.UpdateAsync(roster => roster.Without(target), committed, via: LaneVia.Delete);
                string? folderWarning = hasPair && !revoked
                    ? "The folder was deleted without a logout; that refresh token stays valid until its login expires."
                    : null;
                return Results.Ok(new RemovalView(
                    target.Value,
                    revoked,
                    folderWarning is null || keys.Warning is null ? folderWarning ?? keys.Warning : folderWarning + " " + keys.Warning));
            }
            finally
            {
                permit?.Dispose();
            }
        });

        mutations.MapPost("/accounts/{email}/adopt-live", static async (
            string email,
            RosterFile rosterFile,
            ProfileFolderStore profiles,
            ClaudeStateFile stateFile,
            IClaudeCliAuthStatus cli,
            ClaudeCodeAccountRotationConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            Result<AccountEmail, string> parsed = AccountEmail.Parse(email);
            if (parsed.IsFailure)
            {
                return Results.BadRequest(new { error = parsed.Error });
            }

            OAuthAccountBlock? liveAccount = await stateFile.ReadAccountBlockAsync(cancellationToken);
            if (liveAccount?.Email is not AccountEmail live)
            {
                return Refused("NoLiveAccount", "The state file names no live account to adopt.");
            }

            if (live != parsed.Value)
            {
                return Refused("NotTheLiveAccount", "The live account is " + live.Value + ", not " + parsed.Value.Value + ".");
            }

            Result<ClaudeAuthStatus, string> status = await cli.ReadAsync(configuration.LiveConfigDirectory, cancellationToken);
            (MaxTierVerdict verdict, string? reason) = MaxTierAdmission.Evaluate(status.IsSuccess ? status.Value : null, liveAccount);
            if (verdict == MaxTierVerdict.Refused)
            {
                return Refused("NotAMaxAccount", reason!);
            }

            // Found in the roster the write is handed, so an edit that landed
            // since the checks above is kept rather than written over.
            RosterEntry entry = new(live);
            await rosterFile.UpdateAsync(
                roster =>
                {
                    entry = roster.Find(live) ?? new RosterEntry(live);
                    return roster.With(entry);
                },
                cancellationToken);
            return Results.Ok(new AccountCardView(
                live.Value,
                IsLive: true,
                HasCredentials: true,
                profiles.FolderPathFor(live),
                // Adopting a folder reads nothing from the usage endpoint; the next
                // poll or the next pass fills these rows in.
                UsageView.Unread,
                UsageNote: null,
                RefreshStateView.Idle,
                DashboardAssembler.View(entry)));
        });
    }

    /// <summary>
    /// The tier evidence for an account that is not yet on the roster. The CLI
    /// is only run where there is a login to read: under the live directory when
    /// the account is the live one, or under its own folder when that folder
    /// holds a pair. A brand-new account has neither, and spawning the CLI on a
    /// folder that does not exist would cost a process and leave residue to say
    /// nothing.
    /// </summary>
    private static async Task<(MaxTierVerdict Verdict, string? Reason)> JudgeAsync(
        AccountEmail email,
        ProfileFolderStore profiles,
        ClaudeStateFile stateFile,
        IClaudeCliAuthStatus cli,
        ClaudeCodeAccountRotationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        OAuthAccountBlock? liveAccount = await stateFile.ReadAccountBlockAsync(cancellationToken);
        if (liveAccount?.Email == email)
        {
            Result<ClaudeAuthStatus, string> live = await cli.ReadAsync(configuration.LiveConfigDirectory, cancellationToken);
            return MaxTierAdmission.Evaluate(live.IsSuccess ? live.Value : null, liveAccount);
        }

        string folder = profiles.FolderPathFor(email);
        if (!Directory.Exists(folder))
        {
            return (MaxTierVerdict.Unknown, null);
        }

        return await ParkedFolderAdmission.JudgeAsync(folder, profiles, cli, cancellationToken);
    }

    private static IResult Refused(string refusal, string message) =>
        Results.Json(new SwitchRefusalView(refusal, message), statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// What a PATCH leaves of <paramref name="existing"/>, the entry as the roster
    /// holds it at write time, or why it is refused.
    /// <para>
    /// Presence-based, key by key: a record of nullable fields cannot tell "clear
    /// the alias" from "leave the alias alone", and an edit that silently reset
    /// the fields it did not mention would be worse than one that refused.
    /// </para>
    /// <para>
    /// Edit only takes roles off: <c>removeCiRoles</c> drops each role it names
    /// from this entry and touches no other, and <c>ciRoles</c>, the whole list,
    /// may name only halves the entry holds with the same dates, so a list read
    /// before another account took a half cannot take it back. The one claim is
    /// the repair: a single secret named by hand claims that role's token half,
    /// for a CI token whose secret was set but whose roster write failed. It is
    /// dated by the request's <c>ciTokenGeneratedOn</c> or, when the request
    /// sends none, by the date the entry already holds; with neither it is refused.
    /// It is refused too when another account holds that token half set on a
    /// later day, or set through the tool (with an instant) on the same day: that
    /// half is newer than the token being recorded, so a form drawn before it was
    /// set must not take it.
    /// </para>
    /// </summary>
    private static (RosterEntry? Updated, IResult? Refusal) Edited(
        Roster roster,
        RosterEntry existing,
        JsonObject body,
        BrowserFamily? browser,
        string? directory,
        DateOnly? ciTokenGeneratedOn,
        CiTokenSecret? ciTokenSecret,
        IReadOnlyList<CiRoleAssignment>? ciRoles,
        IReadOnlyList<CiRole> removeCiRoles)
    {
        if (ciRoles?.FirstOrDefault(named => !Holds(existing, named)) is CiRoleAssignment notHeld)
        {
            return (null, Refused(
                "RoleHalfNotHeld",
                existing.Email.Value + " does not back " + notHeld.Role + " with the halves and dates named, and Edit only takes roles off; Generate CI token assigns one. Reload the page and edit again."));
        }

        DateOnly? date = body.ContainsKey("ciTokenGeneratedOn") ? ciTokenGeneratedOn : existing.CiTokenGeneratedOn;
        RosterEntry edited = existing with
        {
            Alias = body.ContainsKey("alias") ? Text(body, "alias") : existing.Alias,
            Browser = body.ContainsKey("browser") ? browser : existing.Browser,
            BrowserProfileDirectory = body.ContainsKey("browserProfileDirectory") ? directory : existing.BrowserProfileDirectory,
            Paused = Flag(body, "paused") ?? existing.Paused,
            Notes = body.ContainsKey("notes") ? Text(body, "notes") : existing.Notes,
            CiTokenGeneratedOn = date,
        };

        IEnumerable<CiRoleAssignment> roles = (ciRoles ?? existing.CiRoles).Where(assignment => !removeCiRoles.Contains(assignment.Role));

        // A cleared date leaves no token behind any role, so every token half
        // goes with it, and a role left with no half goes too.
        if (body.ContainsKey("ciTokenGeneratedOn") && date is null)
        {
            roles = roles
                .Select(static assignment => assignment with { TokenSetOn = null, TokenSetAt = null })
                .Where(static assignment => assignment.HasKey);
        }

        // The date settles once, on the final list: the newest token half's,
        // or none once the last one goes (see RosterEntry.WithCiRoles).
        RosterEntry updated = edited.WithCiRoles(roles);
        if (ciTokenSecret is CiTokenSecret secret)
        {
            if (date is not DateOnly setOn)
            {
                return (null, Results.BadRequest(new { error = "a CI secret needs the date its token was generated (ciTokenGeneratedOn)" }));
            }

            var role = CiRole.FromTokenSecret(secret);
            if (roster.Entries.FirstOrDefault(other => other.Email != existing.Email && other.CiRoles.Any(held =>
                    held.Role.Equals(role) && (held.TokenSetOn > setOn || (held.TokenSetOn == setOn && held.TokenSetAt is not null)))) is RosterEntry holder)
            {
                return (null, Refused(
                    "TokenHalfHeldElsewhere",
                    holder.Email.Value + " holds the token of " + role + ", set after " + setOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + " or through the tool that same day, so it is newer than the token this repair records and naming that secret here would take it from them. To move the role, run Generate CI token for it again."));
            }

            updated = updated.ClaimingToken(role, setOn, setAt: null);
        }

        return (updated, null);
    }

    /// <summary>Whether <paramref name="entry"/> holds every half <paramref name="named"/> names, with the same dates and instants.</summary>
    private static bool Holds(RosterEntry entry, CiRoleAssignment named) =>
        entry.CiRoles.FirstOrDefault(held => held.Role.Equals(named.Role)) is CiRoleAssignment held
        && (!named.HasToken || (named.TokenSetOn, named.TokenSetAt) == (held.TokenSetOn, held.TokenSetAt))
        && (!named.HasKey || (named.KeySetOn, named.KeySetAt) == (held.KeySetOn, held.KeySetAt));

    private static Result<BrowserFamily?, string> ParseBrowser(JsonObject body, string key)
    {
        string invalid = "browser must be one of " + string.Join(", ", Enum.GetNames<BrowserFamily>()).ToLowerInvariant();
        Result<string?, string> value = RequiredString(body, key, invalid);
        if (value.IsFailure)
        {
            return Result<BrowserFamily?, string>.Failure(value.Error);
        }

        if (value.Value is not string text)
        {
            return Result<BrowserFamily?, string>.Success(null);
        }

        return Enum.TryParse(text, ignoreCase: true, out BrowserFamily browser)
            ? Result<BrowserFamily?, string>.Success(browser)
            : Result<BrowserFamily?, string>.Failure(invalid);
    }

    /// <summary>
    /// The browser-profile directory a body names, or null when it names none.
    /// The rule itself lives in <see cref="BrowserProfileDirectory"/>, shared
    /// with the launcher, so a value the roster accepts is one the launcher
    /// will emit and a value written into the roster file by other means is
    /// held to the same rule on its way out.
    /// </summary>
    private static Result<string?, string> ParseProfileDirectory(JsonObject body)
    {
        Result<string?, string> value = RequiredString(body, "browserProfileDirectory", "browserProfileDirectory must be a string");
        if (value.IsFailure)
        {
            return value;
        }

        if (value.Value is not string text)
        {
            return Result<string?, string>.Success(null);
        }

        return BrowserProfileDirectory.Parse(text).Match(
            static directory => Result<string?, string>.Success(directory),
            static error => Result<string?, string>.Failure("browserProfileDirectory: " + error));
    }

    /// <summary>
    /// The CI secret a body names: an object with <c>name</c> and exactly one of
    /// <c>repository</c> or <c>organization</c>, held to GitHub's rules, or null
    /// when the key is absent or JSON null. Anything else is refused rather than
    /// read as absent, for the same reason <see cref="RequiredString"/> refuses.
    /// </summary>
    private static Result<CiTokenSecret?, string> ParseCiTokenSecret(JsonObject body)
    {
        if (!body.TryGetPropertyValue("ciTokenSecret", out JsonNode? node) || node is null)
        {
            return Result<CiTokenSecret?, string>.Success(null);
        }

        if (node is not JsonObject secret)
        {
            return Result<CiTokenSecret?, string>.Failure("ciTokenSecret must be an object with name and repository or organization");
        }

        Result<CiTokenSecret, string> parsed = CiTokenSecret.Parse(Text(secret, "name"), Text(secret, "repository"), Text(secret, "organization"));
        return parsed.IsSuccess
            ? Result<CiTokenSecret?, string>.Success(parsed.Value)
            : Result<CiTokenSecret?, string>.Failure("ciTokenSecret: " + parsed.Error);
    }

    /// <summary>
    /// The whole list of CI roles a body names, or null when it names none.
    /// Each item is a role (<c>prefix</c>, or <c>tokenSecret</c> for a token-only
    /// one, and exactly one of <c>repository</c> or <c>organization</c>) and the
    /// halves set from this account: <c>tokenSetOn</c> and <c>keySetOn</c>, each
    /// with its optional instant. A half is named by its date, so an instant
    /// without its date is refused, as is a key half on a role that has none.
    /// JSON null clears every role.
    /// </summary>
    private static Result<IReadOnlyList<CiRoleAssignment>?, string> ParseCiRoles(JsonObject body)
    {
        if (!body.TryGetPropertyValue("ciRoles", out JsonNode? node))
        {
            return Result<IReadOnlyList<CiRoleAssignment>?, string>.Success(null);
        }

        if (node is null)
        {
            return Result<IReadOnlyList<CiRoleAssignment>?, string>.Success([]);
        }

        if (node is not JsonArray items)
        {
            return Result<IReadOnlyList<CiRoleAssignment>?, string>.Failure("ciRoles must be a list of roles");
        }

        List<CiRoleAssignment> roles = [];
        foreach (JsonNode? item in items)
        {
            Result<CiRoleAssignment, string> role = ParseCiRole(item);
            if (role.IsFailure)
            {
                return Result<IReadOnlyList<CiRoleAssignment>?, string>.Failure("ciRoles: " + role.Error);
            }

            if (roles.Exists(held => held.Role.Equals(role.Value.Role)))
            {
                return Result<IReadOnlyList<CiRoleAssignment>?, string>.Failure("ciRoles: " + role.Value.Role + " is named twice");
            }

            roles.Add(role.Value);
        }

        return Result<IReadOnlyList<CiRoleAssignment>?, string>.Success(roles);
    }

    /// <summary>
    /// The roles a body takes off the account, each named as a <c>ciRoles</c>
    /// item is but with no halves, or none when the key is absent or JSON null.
    /// </summary>
    private static Result<IReadOnlyList<CiRole>, string> ParseRemoveCiRoles(JsonObject body)
    {
        if (!body.TryGetPropertyValue("removeCiRoles", out JsonNode? node) || node is null)
        {
            return Result<IReadOnlyList<CiRole>, string>.Success([]);
        }

        if (node is not JsonArray items)
        {
            return Result<IReadOnlyList<CiRole>, string>.Failure("removeCiRoles must be a list of roles");
        }

        List<CiRole> roles = [];
        foreach (JsonNode? item in items)
        {
            Result<CiRole, string> role = ParseRoleName(item);
            if (role.IsFailure)
            {
                return Result<IReadOnlyList<CiRole>, string>.Failure("removeCiRoles: " + role.Error);
            }

            roles.Add(role.Value);
        }

        return Result<IReadOnlyList<CiRole>, string>.Success(roles);
    }

    /// <summary>The role an item names: <c>prefix</c> or <c>tokenSecret</c>, and exactly one of <c>repository</c> or <c>organization</c>.</summary>
    private static Result<CiRole, string> ParseRoleName(JsonNode? node)
    {
        if (node is not JsonObject item)
        {
            return Result<CiRole, string>.Failure("each role is an object with prefix and repository or organization");
        }

        const string notText = "prefix, tokenSecret, repository and organization are strings";
        Result<string?, string> prefix = RequiredString(item, "prefix", notText);
        Result<string?, string> tokenSecret = RequiredString(item, "tokenSecret", notText);
        Result<string?, string> repository = RequiredString(item, "repository", notText);
        Result<string?, string> organization = RequiredString(item, "organization", notText);
        return prefix.IsFailure || tokenSecret.IsFailure || repository.IsFailure || organization.IsFailure
            ? Result<CiRole, string>.Failure(notText)
            : CiRole.Parse(prefix.Value, Blank(tokenSecret.Value), Blank(repository.Value), Blank(organization.Value));
    }

    private static Result<CiRoleAssignment, string> ParseCiRole(JsonNode? node)
    {
        Result<CiRole, string> role = ParseRoleName(node);
        if (role.IsFailure)
        {
            return Result<CiRoleAssignment, string>.Failure(role.Error);
        }

        var item = (JsonObject)node!;
        Result<DateOnly?, string> tokenSetOn = ParseDate(item, "tokenSetOn");
        Result<DateOnly?, string> keySetOn = ParseDate(item, "keySetOn");
        Result<DateTimeOffset?, string> tokenSetAt = ParseInstant(item, "tokenSetAt");
        Result<DateTimeOffset?, string> keySetAt = ParseInstant(item, "keySetAt");
        string? invalid = tokenSetOn.IsFailure ? tokenSetOn.Error
            : keySetOn.IsFailure ? keySetOn.Error
            : tokenSetAt.IsFailure ? tokenSetAt.Error
            : keySetAt.IsFailure ? keySetAt.Error
            : tokenSetAt.Value is not null && tokenSetOn.Value is null ? "a token half needs the date its token was set (tokenSetOn)"
            : keySetAt.Value is not null && keySetOn.Value is null ? "a key half needs the date its key was set (keySetOn)"
            : keySetOn.Value is not null && !role.Value.HasKeyHalf ? role.Value + " has a token secret only"
            : tokenSetOn.Value is null && keySetOn.Value is null ? role.Value + " names neither half (tokenSetOn or keySetOn)"
            : null;
        return invalid is not null
            ? Result<CiRoleAssignment, string>.Failure(invalid)
            : Result<CiRoleAssignment, string>.Success(new CiRoleAssignment(role.Value, tokenSetOn.Value, tokenSetAt.Value, keySetOn.Value, keySetAt.Value));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Result<DateOnly?, string> ParseDate(JsonObject body, string key)
    {
        string invalid = key + " must be a date (yyyy-mm-dd)";
        Result<string?, string> value = RequiredString(body, key, invalid);
        if (value.IsFailure)
        {
            return Result<DateOnly?, string>.Failure(value.Error);
        }

        if (value.Value is not string text)
        {
            return Result<DateOnly?, string>.Success(null);
        }

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)
            ? Result<DateOnly?, string>.Success(parsed)
            : Result<DateOnly?, string>.Failure(invalid);
    }

    private static Result<DateTimeOffset?, string> ParseInstant(JsonObject body, string key)
    {
        string invalid = key + " must be an ISO 8601 instant with its offset";
        Result<string?, string> value = RequiredString(body, key, invalid);
        if (value.IsFailure)
        {
            return Result<DateTimeOffset?, string>.Failure(value.Error);
        }

        if (value.Value is not string text)
        {
            return Result<DateTimeOffset?, string>.Success(null);
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? Result<DateTimeOffset?, string>.Success(parsed)
            : Result<DateTimeOffset?, string>.Failure(invalid);
    }

    /// <summary>
    /// The string a body names for <paramref name="key"/>, or null when the key
    /// is absent or explicitly JSON null. A key present with a non-string value
    /// (a number, bool, array, or object) is refused with
    /// <paramref name="errorMessage"/> rather than read as absent: a nullable
    /// field's PATCH clears on presence, so treating a malformed value the same
    /// as "not sent" would silently wipe the field instead of rejecting the
    /// request.
    /// </summary>
    private static Result<string?, string> RequiredString(JsonObject body, string key, string errorMessage)
    {
        if (!body.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return Result<string?, string>.Success(null);
        }

        return node is JsonValue value && value.TryGetValue(out string? text)
            ? Result<string?, string>.Success(text)
            : Result<string?, string>.Failure(errorMessage);
    }

    private static string? Text(JsonObject body, string key) =>
        body[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static bool? Flag(JsonObject body, string key) =>
        body[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    // The account and the outcome only. The logout diagnostic is not an outcome.
    [LoggerMessage(Level = LogLevel.Information, Message = "logout for {Account} revoked")]
    private static partial void LogLogoutRevoked(ILogger logger, string account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "logout for {Account} failed")]
    private static partial void LogLogoutFailed(ILogger logger, string account);
}

/// <summary>What a removal did, including whether the refresh token was revoked.</summary>
internal sealed record RemovalView(string Removed, bool LoggedOut, string? Warning);
