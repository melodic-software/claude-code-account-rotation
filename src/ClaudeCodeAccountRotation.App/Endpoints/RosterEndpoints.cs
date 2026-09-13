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
/// entirely; there is nothing to revoke. A folder whose pair is stranded in
/// recovery is put back first, so the revocation reaches the lineage that is
/// actually alive.
/// </para>
/// </summary>
internal static class RosterEndpoints
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
            await rosterFile.UpdateAsync(roster => roster.With(entry), cancellationToken);
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

            RosterEntry? existing = (await rosterFile.ReadAsync(cancellationToken)).Find(parsed.Value);
            if (existing is null)
            {
                return Results.NotFound(new { error = parsed.Value.Value + " is not on the roster." });
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

            // Presence-based, key by key: a record of nullable fields cannot tell
            // "clear the alias" from "leave the alias alone", and an edit that
            // silently reset the fields it did not mention would be worse than
            // one that refused.
            RosterEntry updated = existing with
            {
                Alias = body.ContainsKey("alias") ? Text(body, "alias") : existing.Alias,
                Browser = body.ContainsKey("browser") ? browser.Value : existing.Browser,
                BrowserProfileDirectory = body.ContainsKey("browserProfileDirectory") ? directory.Value : existing.BrowserProfileDirectory,
                Paused = Flag(body, "paused") ?? existing.Paused,
                Notes = body.ContainsKey("notes") ? Text(body, "notes") : existing.Notes,
            };
            await rosterFile.UpdateAsync(roster => roster.With(updated), cancellationToken);
            return Results.Ok(DashboardAssembler.View(updated));
        });

        mutations.MapDelete("/accounts/{email}", static async (
            string email,
            bool? logout,
            RosterFile rosterFile,
            ProfileFolderStore profiles,
            ClaudeStateFile stateFile,
            IClaudeCliLogout cli,
            ILoginSessionRunner logins,
            CredentialMutationGate gate,
            RecoveryFiles recovery,
            SwitchOptions options,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(options);
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

                bool hasPair = File.Exists(Path.Combine(folder, FileSystemCredentialPairStore.FileName));
                bool revoke = logout ?? true;
                bool revoked = false;
                if (hasPair && revoke)
                {
                    Result<Unit, string> loggedOut = await cli.LogoutAsync(folder, committed);
                    if (loggedOut.IsFailure)
                    {
                        return Refused(
                            "LogoutFailed",
                            "That account's login could not be revoked, so the folder was kept: " + loggedOut.Error
                            + " Retry, or remove with logout=false to delete the folder without revoking.");
                    }

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

                await rosterFile.UpdateAsync(roster => roster.Without(target), committed);
                return Results.Ok(new RemovalView(
                    target.Value,
                    revoked,
                    hasPair && !revoked
                        ? "The folder was deleted without a logout; that refresh token stays valid until its login expires."
                        : null));
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

            RosterEntry entry = (await rosterFile.ReadAsync(cancellationToken)).Find(live) ?? new RosterEntry(live);
            await rosterFile.UpdateAsync(roster => roster.With(entry), cancellationToken);
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

    private static Result<BrowserFamily?, string> ParseBrowser(JsonObject body, string key)
    {
        if (Text(body, key) is not string value)
        {
            return Result<BrowserFamily?, string>.Success(null);
        }

        return Enum.TryParse(value, ignoreCase: true, out BrowserFamily browser)
            ? Result<BrowserFamily?, string>.Success(browser)
            : Result<BrowserFamily?, string>.Failure("browser must be one of " + string.Join(", ", Enum.GetNames<BrowserFamily>()).ToLowerInvariant());
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
        if (Text(body, "browserProfileDirectory") is not string value)
        {
            return Result<string?, string>.Success(null);
        }

        return BrowserProfileDirectory.Parse(value).Match(
            static directory => Result<string?, string>.Success(directory),
            static error => Result<string?, string>.Failure("browserProfileDirectory: " + error));
    }

    private static string? Text(JsonObject body, string key) =>
        body[key] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static bool? Flag(JsonObject body, string key) =>
        body[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;
}

/// <summary>What a removal did, including whether the refresh token was revoked.</summary>
internal sealed record RemovalView(string Removed, bool LoggedOut, string? Warning);
