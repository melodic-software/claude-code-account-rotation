using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClaudeCodeAccountRotation.App.Endpoints;

/// <summary>
/// "Check roles": lists the Actions secrets of every organization the roster's
/// roles name, and of the one typed on the page, then derives the roles from
/// the secret names, names each role's holder from the roster, and says
/// whether each half the roster records is still the secret the tool set.
/// <para>
/// A POST behind the mutation filter, though it writes nothing: each click
/// spends GitHub API calls, so no other site may start one. Nothing calls it
/// but that click. Organizations are read one at a time, a page per call, and
/// the first rate-limit answer ends the check.
/// </para>
/// </summary>
internal static class RoleEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        RouteGroupBuilder mutations = routes.MapGroup("/api").AddEndpointFilter<SameOriginMutationFilter>();

        mutations.MapPost("/roles/check", static async (
            JsonObject? body,
            RosterFile rosterFile,
            IGhApiReader reader,
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
                    return Results.Json(new SwitchRefusalView("RateLimited", listed.Error.Message), statusCode: StatusCodes.Status409Conflict);
                }
                else
                {
                    failures.Add(new RoleCheckFailureView(organization, listed.Error.Message));
                }
            }

            var catalog = RoleCatalog.From(read);
            return Results.Ok(View(roster, catalog, RoleSecretCheck.Evaluate(roster, catalog, clock.GetUtcNow()), failures));
        });
    }

    /// <summary>
    /// One row per role: each found in a listing, and each the roster claims in
    /// an organization that was read, found or not. A claimed half carries its
    /// verdict; a role nobody claims carries "unclaimed" on each half it has.
    /// </summary>
    private static RoleCheckView View(Roster roster, RoleCatalog catalog, RoleSecretReport report, List<RoleCheckFailureView> failures)
    {
        Dictionary<CiRole, RoleCheckRowView> rows = [];
        foreach (RoleSecrets found in report.Unclaimed)
        {
            rows[found.Role] = Row(found.Role, null, catalog, found.HasToken ? "unclaimed" : null, found.HasKey ? "unclaimed" : null);
        }

        foreach (ClaimedHalf half in report.Claimed)
        {
            RoleCheckRowView row = rows.GetValueOrDefault(half.Role) ?? Row(half.Role, half.Holder.Value, catalog, null, null);
            rows[half.Role] = half.Half == RoleHalf.Token ? row with { Token = Verdict(half.Verdict) } : row with { Key = Verdict(half.Verdict) };
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
            failures);
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

    private static string Verdict(RoleSecretVerdict verdict) => verdict switch
    {
        RoleSecretVerdict.Ok => "ok",
        RoleSecretVerdict.ChangedOutsideTool => "changed",
        RoleSecretVerdict.Unknown => "unknown",
        _ => "missing",
    };
}

/// <summary>What Check roles found.</summary>
internal sealed record RoleCheckView(DateTimeOffset CheckedAt, IReadOnlyList<RoleCheckRowView> Roles, IReadOnlyList<RoleCheckFailureView> Failures);

/// <summary>
/// One role: whether each of its secrets exists, who backs it, and the verdict
/// on each half: ok, changed, unknown, missing, unclaimed, or null for a half
/// nobody's record speaks for.
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
    string? Key);

/// <summary>An organization whose secrets could not be read, and why.</summary>
internal sealed record RoleCheckFailureView(string Organization, string Error);
