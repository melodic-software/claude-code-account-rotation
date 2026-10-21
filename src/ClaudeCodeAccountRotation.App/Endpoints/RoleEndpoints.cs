using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Configuration;
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

            Result<Unit, GhApiFailure> set = await writer.SetOrganizationVariableAsync(organization, kill.Name, value, visibility.Value, cancellationToken);
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

/// <summary>An organization whose secrets could not be read, and why.</summary>
internal sealed record RoleCheckFailureView(string Organization, string Error);
