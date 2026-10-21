using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Configuration;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// "Check roles": lists the Actions secrets of every organization the roster's
/// roles name, and of the one typed on the page, then derives the roles from
/// the secret names, names each role's holder from the roster, and says
/// whether each half the roster records is still the secret the tool set.
/// The same click reads the kill switches in <see cref="KillSwitches"/>.
/// <para>
/// A POST behind the mutation filter, though it writes nothing: each click
/// spends GitHub API calls, so no other site may start one. Nothing calls it
/// but that click. Organizations are read one at a time, a page per call, and
/// the first rate-limit answer ends the check.
/// </para>
/// </summary>
internal static partial class RoleEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPost("/roles/check", static async (
            JsonObject? body,
            RosterFile rosterFile,
            IGhApiReader reader,
            ClaudeCodeAccountRotationConfiguration configuration,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            Roster roster = await rosterFile.ReadAsync(cancellationToken);
            List<string> organizations = [.. roster.Entries
                .SelectMany(static entry => entry.CiRoles)
                .Where(static assignment => assignment.Role.Scope == CiSecretScope.Organization)
                .Select(static assignment => assignment.Role.Owner)];
            string? typed = body?["organization"] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : null;
            if (typed is not null)
            {
                // The organization becomes part of a path, so it is held to
                // GitHub's login rules first.
                Result<CiTokenSecret, string> valid = CiTokenSecret.Parse(CiTokenSecret.DefaultName, null, typed);
                if (valid.IsFailure)
                {
                    return Results.BadRequest(new { error = valid.Error });
                }

                organizations.Add(valid.Value.Owner);
            }

            organizations = [.. organizations.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            if (organizations.Count == 0)
            {
                return Results.BadRequest(new { error = "no role on the roster names an organization; type one to check" });
            }

            List<OrganizationSecrets> read = [];
            List<RoleCheckFailureView> failures = [];
            foreach (string organization in organizations)
            {
                Result<OrganizationSecrets, GhApiFailure> listed = await reader.ListOrganizationSecretsAsync(organization, cancellationToken);
                if (listed.IsSuccess)
                {
                    read.Add(listed.Value);
                }
                else if (listed.Error.RateLimited)
                {
                    return RateLimited(listed.Error);
                }
                else
                {
                    failures.Add(new RoleCheckFailureView(organization, listed.Error.Message));
                }
            }

            // The kill switches of each organization whose secrets were read,
            // then the read-only one of each configured lane repository.
            List<KillSwitchView> switches = [];
            IEnumerable<(KillSwitch Switch, string Owner)> targets = read
                .SelectMany(static listed => KillSwitches.All.Where(static kill => kill.Scope == KillSwitchScope.Organization).Select(kill => (kill, listed.Organization)))
                .Concat((configuration.LaneRepositories ?? []).SelectMany(static repository => KillSwitches.All.Where(static kill => kill.Scope == KillSwitchScope.Repository).Select(kill => (kill, repository))));
            foreach ((KillSwitch kill, string owner) in targets)
            {
                Result<string?, GhApiFailure> answer = await reader.GetVariableAsync(kill.PathIn(owner), cancellationToken);
                if (answer.IsFailure && answer.Error.RateLimited)
                {
                    return RateLimited(answer.Error);
                }

                switches.Add(KillSwitchView.Of(kill, owner, answer));
            }

            var catalog = RoleCatalog.From(read);
            return Results.Ok(View(roster, catalog, RoleSecretCheck.Evaluate(roster, catalog, clock.GetUtcNow(), clock.LocalTimeZone), failures, switches));
        });

        // The limit-stop warning, asked once by the page before it moves roles
        // off the accounts in "from": the failed runs of the last 7 days in each
        // lane repository, at most 30 in all, newest first, then the failed
        // jobs of each and their annotations. GETs only, one at a time; the
        // first rate-limit answer ends the scan as partial. With no lane
        // repositories configured nothing is read. Every account is judged
        // from the same read.
        mutations.MapPost("/roles/limit-stops", static async (
            JsonObject? body,
            RosterFile rosterFile,
            IGhApiReader reader,
            ClaudeCodeAccountRotationConfiguration configuration,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            // One address, or a list of them: every account the move takes a
            // role off, all judged from the one read.
            List<string?> typed = body?["from"] switch
            {
                JsonArray many => [.. many.Select(static item => item is JsonValue one && one.TryGetValue(out string? text) ? text : null)],
                JsonValue one when one.TryGetValue(out string? text) => [text],
                _ => [],
            };
            List<AccountEmail> froms = [];
            foreach (string? address in typed)
            {
                Result<AccountEmail, string> parsed = AccountEmail.Parse(address ?? string.Empty);
                if (parsed.IsFailure)
                {
                    return Results.BadRequest(new { error = "from: " + parsed.Error });
                }

                if (!froms.Contains(parsed.Value))
                {
                    froms.Add(parsed.Value);
                }
            }

            if (froms.Count == 0)
            {
                return Results.BadRequest(new { error = "from: name at least one account" });
            }

            List<string> accounts = [.. froms.Select(static email => email.Value)];
            IReadOnlyList<string> repositories = configuration.LaneRepositories ?? [];
            if (repositories.Count == 0)
            {
                return Results.Ok(new LimitStopView(LimitStopView.NotChecked, accounts, 0, [], []));
            }

            DateTimeOffset now = clock.GetUtcNow();
            List<string> problems = [];
            List<LaneRun> listed = [];
            List<LaneAnnotation> annotations = [];
            int runsRead = 0;
            bool rateLimited = false;
            foreach (string repository in repositories)
            {
                Result<IReadOnlyList<LaneRun>, GhApiFailure> runs = await reader.ListFailedRunsAsync(repository, now - LimitStopScan.Window, cancellationToken);
                if (runs.IsSuccess)
                {
                    listed.AddRange(runs.Value);
                }
                else if (Stops(runs.Error))
                {
                    break;
                }
            }

            IReadOnlyList<LaneRun> newest = rateLimited ? [] : LimitStopScan.Newest(listed, now);
            foreach (LaneRun run in newest)
            {
                Result<IReadOnlyList<long>, GhApiFailure> checkRuns = await reader.ListFailedJobCheckRunsAsync(run, cancellationToken);
                if (checkRuns.IsFailure)
                {
                    if (Stops(checkRuns.Error))
                    {
                        break;
                    }

                    continue;
                }

                bool whole = true;
                foreach (long checkRun in checkRuns.Value)
                {
                    Result<IReadOnlyList<string>, GhApiFailure> messages = await reader.ListAnnotationMessagesAsync(run.Repository, checkRun, cancellationToken);
                    if (messages.IsSuccess)
                    {
                        annotations.AddRange(messages.Value.Select(message => new LaneAnnotation(run, message)));
                        continue;
                    }

                    whole = false;
                    if (Stops(messages.Error))
                    {
                        break;
                    }
                }

                if (rateLimited)
                {
                    break;
                }

                runsRead += whole ? 1 : 0;
            }

            IReadOnlyList<RoleHalfChange> history = [.. (await rosterFile.History.ReadAsync(null, cancellationToken)).Select(static entry => new RoleHalfChange(
                entry.At,
                entry.Role,
                entry.Half == LaneHistoryEntry.KeyHalf ? RoleHalf.Key : RoleHalf.Token,
                entry.Account,
                entry.Previous))];
            Roster roster = await rosterFile.ReadAsync(cancellationToken);
            List<LimitStopHitView> hits = [];
            foreach (AccountEmail account in froms)
            {
                hits.AddRange(LimitStopScan.Hits(account, annotations, history, roster).Select(hit => new LimitStopHitView(
                    account.Value, hit.Run.Repository, hit.Run.Id, hit.Run.Url, hit.Run.CreatedAt, hit.Role.Name, hit.Half == RoleHalf.Token ? "token" : "key", hit.Credential, hit.Label)));
            }

            return Results.Ok(new LimitStopView(
                problems.Count == 0 ? LimitStopView.Checked : LimitStopView.Partial,
                accounts,
                runsRead,
                hits,
                problems));

            // Notes a failed read; true when it was the rate limit, which ends the scan.
            bool Stops(GhApiFailure failure)
            {
                problems.Add(failure.Message);
                rateLimited = failure.RateLimited;
                return rateLimited;
            }
        });

        // Flips one organization kill switch that already exists, sending back
        // the visibility read just before, then reads it back. Only the organization names in the table are writable; no
        // repository or environment variable is ever written, and nothing is
        // created or deleted.
        mutations.MapPost("/kill-switches", static async (
            JsonObject? body,
            IGhVariableWriter writer,
            IGhApiReader reader,
            ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            string? name = body?["name"] is JsonValue named && named.TryGetValue(out string? text) ? text : null;
            if (KillSwitches.Writable(name) is not KillSwitch kill)
            {
                return Results.BadRequest(new { error = "only " + string.Join(", ", KillSwitches.All.Where(static kill => kill.Writable).Select(static kill => kill.Name)) + " can be set here" });
            }

            if (Flag(body?["value"]) is not bool value)
            {
                return Results.BadRequest(new { error = "value is exactly true or false" });
            }

            string? typed = body?["org"] is JsonValue org && org.TryGetValue(out string? orgText) ? orgText : null;
            Result<CiTokenSecret, string> valid = CiTokenSecret.Parse(CiTokenSecret.DefaultName, null, typed);
            if (valid.IsFailure)
            {
                return Results.BadRequest(new { error = valid.Error });
            }

            string organization = valid.Value.Owner;
            ILogger log = loggers.CreateLogger(typeof(RoleEndpoints));

            // The current visibility first, sent back with the value: GitHub's
            // docs do not promise an omitted one is kept. A read that fails
            // writes nothing.
            Result<VariableVisibility, GhApiFailure> visibility = await reader.GetOrganizationVariableVisibilityAsync(organization, kill.Name, cancellationToken);
            if (visibility.IsFailure)
            {
                LogKillSwitchNotSet(log, kill.Name, organization, value, visibility.Error.Message);
                return visibility.Error.RateLimited ? RateLimited(visibility.Error)
                    : visibility.Error.Status == 404 ? Results.NotFound(new { error = kill.Name + " is not set in org " + organization + " or not visible to this sign-in. The tool never creates it." })
                    : Results.Json(new { error = "nothing was written: " + visibility.Error.Message }, statusCode: StatusCodes.Status502BadGateway);
            }

            // Not the request's token, as secret sets do not take it: a page that
            // goes away must not leave a PATCH that may have landed unaudited.
            Result<Unit, GhApiFailure> set = await writer.SetOrganizationVariableAsync(organization, kill.Name, value, visibility.Value, CancellationToken.None);
            if (set.IsFailure)
            {
                LogKillSwitchNotSet(log, kill.Name, organization, value, set.Error.Message);
                return set.Error.RateLimited ? RateLimited(set.Error)
                    : set.Error.Status == 404 ? Results.NotFound(new { error = set.Error.Message })
                    : Results.Json(new { error = set.Error.Message }, statusCode: StatusCodes.Status502BadGateway);
            }

            LogKillSwitchSet(log, kill.Name, organization, value);

            // The write happened; a re-read that fails, a rate limit included,
            // is shown on the switch rather than as a refusal.
            return Results.Ok(KillSwitchView.Of(kill, organization, await reader.GetVariableAsync(kill.PathIn(organization), cancellationToken)));
        });
    }

    // Audit lines: which switch, which organization, the value asked for, and
    // the outcome. Nothing gh printed is in a failure's message.
    [LoggerMessage(Level = LogLevel.Information, Message = "kill switch {Name} in org {Organization} set to {Value}")]
    private static partial void LogKillSwitchSet(ILogger logger, string name, string organization, bool value);

    [LoggerMessage(Level = LogLevel.Warning, Message = "kill switch {Name} in org {Organization} not set to {Value}: {Reason}")]
    private static partial void LogKillSwitchNotSet(ILogger logger, string name, string organization, bool value, string reason);

    private static IResult RateLimited(GhApiFailure failure) =>
        Results.Json(new SwitchRefusalView("RateLimited", failure.Message), statusCode: StatusCodes.Status409Conflict);

    /// <summary>A JSON <c>true</c> or <c>false</c>, or the exact string of one; anything else is null.</summary>
    private static bool? Flag(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue(out bool flag) ? flag
        : value.TryGetValue(out string? text) ? text switch { "true" => true, "false" => false, _ => null }
        : null
        : null;

    /// <summary>
    /// One row per role: each found in a listing, and each the roster claims in
    /// an organization that was read, found or not. A claimed half carries its
    /// verdict; a role nobody claims carries "unclaimed" on each half it has.
    /// </summary>
    private static RoleCheckView View(Roster roster, RoleCatalog catalog, RoleSecretReport report, List<RoleCheckFailureView> failures, List<KillSwitchView> switches)
    {
        Dictionary<CiRole, RoleCheckRowView> rows = [];
        foreach (RoleSecrets found in report.Unclaimed)
        {
            rows[found.Role] = Row(found.Role, null, catalog, found.HasToken ? "unclaimed" : null, found.HasKey ? "unclaimed" : null);
        }

        foreach (ClaimedHalf half in report.Claimed)
        {
            RoleCheckRowView row = rows.GetValueOrDefault(half.Role) ?? Row(half.Role, half.Holder.Value, catalog, null, null);
            rows[half.Role] = half.Half == RoleHalf.Token
                ? row with { Token = Verdict(half.Verdict), TokenReason = Reason(half.Reason) }
                : row with { Key = Verdict(half.Verdict), KeyReason = Reason(half.Reason) };
        }

        // A role a roster entry claims with no half recorded still shows its holder.
        foreach (KeyValuePair<CiRole, RoleSecrets> found in catalog.Roles.Where(found => !rows.ContainsKey(found.Key)))
        {
            string? holder = roster.Entries.FirstOrDefault(entry => entry.CiRoles.Any(held => held.Role.Equals(found.Key)))?.Email.Value;
            rows[found.Key] = Row(found.Key, holder, catalog, null, null);
        }

        return new RoleCheckView(
            report.CheckedAt,
            [.. rows.Values.OrderBy(static row => row.Organization, StringComparer.OrdinalIgnoreCase).ThenBy(static row => row.Prefix, StringComparer.OrdinalIgnoreCase)],
            failures,
            switches);
    }

    private static RoleCheckRowView Row(CiRole role, string? holder, RoleCatalog catalog, string? token, string? key) => new(
        role.Name,
        role.Prefix,
        role.Owner,
        role.TokenSecretName,
        role.KeySecretName,
        catalog.Find(role.Owner, role.TokenSecretName) is not null,
        role.KeySecretName is string keySecret && catalog.Find(role.Owner, keySecret) is not null,
        holder,
        token,
        key);

    private static string? Reason(UnknownReason? reason) => reason switch
    {
        UnknownReason.NoInstant => "noInstant",
        UnknownReason.OlderThanWrite => "olderThanWrite",
        UnknownReason.NoListedTime => "noListedTime",
        _ => null,
    };

    private static string Verdict(RoleSecretVerdict verdict) => verdict switch
    {
        RoleSecretVerdict.Ok => "ok",
        RoleSecretVerdict.ChangedOutsideTool => "changed",
        RoleSecretVerdict.Unknown => "unknown",
        _ => "missing",
    };
}

/// <summary>What Check roles found.</summary>
internal sealed record RoleCheckView(
    DateTimeOffset CheckedAt,
    IReadOnlyList<RoleCheckRowView> Roles,
    IReadOnlyList<RoleCheckFailureView> Failures,
    IReadOnlyList<KillSwitchView> KillSwitches);

/// <summary>Where a kill switch variable lives.</summary>
internal enum KillSwitchScope
{
    Organization,
    Repository,
}

/// <summary>
/// One kill switch variable: where it lives, the values that read as running
/// (<paramref name="Running"/> gets null for a variable GitHub answers 404 for),
/// and whether the page may flip it.
/// </summary>
internal sealed record KillSwitch(string Name, KillSwitchScope Scope, Func<string?, bool> Running, bool Writable)
{
    /// <summary>The variable's REST path under an organization or an <c>owner/name</c> repository already held to GitHub's rules.</summary>
    public string PathIn(string owner) =>
        (Scope == KillSwitchScope.Organization ? "orgs/" : "repos/") + owner + "/actions/variables/" + Name;
}

/// <summary>
/// The one table of kill switches, read by Check roles and the only names
/// the toggle accepts. Values compare exactly: no trim, no case. The lanes
/// switches run only on exactly <c>false</c>; the review switches stop only
/// on exactly <c>true</c>.
/// </summary>
internal static class KillSwitches
{
    public static readonly IReadOnlyList<KillSwitch> All =
    [
        new("CLAUDE_LANES_DISABLED", KillSwitchScope.Organization, static value => value == "false", Writable: true),
        new("CLAUDE_REVIEW_DISABLED", KillSwitchScope.Organization, static value => value != "true", Writable: true),
        new("CLAUDE_SECURITY_REVIEW_DISABLED", KillSwitchScope.Organization, static value => value != "true", Writable: true),
        new("CLAUDE_LANES_REPO_DISABLED", KillSwitchScope.Repository, static value => value == "false", Writable: false),
    ];

    /// <summary>The writable organization switch named exactly <paramref name="name"/>, or null.</summary>
    public static KillSwitch? Writable(string? name) =>
        All.FirstOrDefault(kill => kill.Writable && kill.Scope == KillSwitchScope.Organization && string.Equals(kill.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// One switch as read: its value (null when the variable does not exist),
/// running or stopped, or the reason it could not be read, when
/// <paramref name="State"/> is null.
/// </summary>
internal sealed record KillSwitchView(
    string Name,
    string Scope,
    string Owner,
    bool Exists,
    string? Value,
    string? State,
    bool Writable,
    string? Error)
{
    public static KillSwitchView Of(KillSwitch kill, string owner, Result<string?, GhApiFailure> read)
    {
        ArgumentNullException.ThrowIfNull(kill);
        string scope = kill.Scope == KillSwitchScope.Organization ? "organization" : "repository";
        return read.IsFailure
            ? new KillSwitchView(kill.Name, scope, owner, false, null, null, kill.Writable, read.Error.Message)
            : new KillSwitchView(kill.Name, scope, owner, read.Value is not null, read.Value, kill.Running(read.Value) ? "running" : "stopped", kill.Writable, null);
    }
}

/// <summary>
/// One role: whether each of its secrets exists, who backs it, and the verdict
/// on each half: ok, changed, unknown, missing, unclaimed, or null for a half
/// nobody's record speaks for. An unknown half's reason is noInstant,
/// olderThanWrite or noListedTime.
/// </summary>
internal sealed record RoleCheckRowView(
    string Name,
    string Prefix,
    string Organization,
    string TokenSecret,
    string? KeySecret,
    bool HasToken,
    bool HasKey,
    string? Holder,
    string? Token,
    string? Key,
    string? TokenReason = null,
    string? KeyReason = null);

/// <summary>
/// What one limit-stop scan for <paramref name="Accounts"/> found:
/// <see cref="NotChecked"/> when no lane repository is configured,
/// <see cref="Partial"/> when a read failed or GitHub's rate limit ended it
/// (<paramref name="Problems"/> says which), else <see cref="Checked"/>.
/// <paramref name="RunsRead"/> counts the runs whose jobs were all read.
/// </summary>
internal sealed record LimitStopView(
    string Outcome,
    IReadOnlyList<string> Accounts,
    int RunsRead,
    IReadOnlyList<LimitStopHitView> Hits,
    IReadOnlyList<string> Problems)
{
    public const string NotChecked = "notChecked";

    public const string Partial = "partial";

    public const string Checked = "checked";
}

/// <summary>One run that stopped on a limit of a half <paramref name="Account"/> backed: "subscription limit" on a token half, "credit stop" on a key half.</summary>
internal sealed record LimitStopHitView(
    string Account,
    string Repository,
    long RunId,
    Uri? Url,
    DateTimeOffset CreatedAt,
    string Role,
    string Half,
    string Credential,
    string Label);

/// <summary>An organization whose secrets could not be read, and why.</summary>
internal sealed record RoleCheckFailureView(string Organization, string Error);
