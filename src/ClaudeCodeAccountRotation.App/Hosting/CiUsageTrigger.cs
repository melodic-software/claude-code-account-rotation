using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Endpoints;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Hosting;

/// <summary>
/// <c>--read-ci-usage</c>: asks the running instance for the CI-role pass,
/// with the headers every dashboard write takes, the way the rate-limit stop
/// hook asks for its read, and waits in that one request for the pass to end
/// and the table to come back. Starts nothing of its own. The follower maps no
/// refresh route, so on that side it refuses before sending.
/// </summary>
internal static class CiUsageTrigger
{
    public const string Route = "/api/refresh/ci-roles?wait=true";

    /// <summary>The instance's own wait plus room for its answer, so the instance says it ran out first.</summary>
    public static readonly TimeSpan ClientTimeout = RefreshEndpoints.CiRoleWait + TimeSpan.FromSeconds(30);

    public static async Task<Result<CiUsageReport, string>> PostAsync(HttpClient client, RotationRole role, (string Url, string Token)? running, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (role == RotationRole.Follower)
        {
            return Result<CiUsageReport, string>.Failure("this is the follower, which runs no usage read; run --read-ci-usage with the leader's executable");
        }

        if (running is not var (url, token))
        {
            return Result<CiUsageReport, string>.Failure("no instance is running on this side; start it first");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(url.TrimEnd('/') + Route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(SameOriginMutationFilter.HeaderName, "1");
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK
                && await response.Content.ReadFromJsonAsync<CiUsageReport>(JsonSerializerOptions.Web, cancellationToken) is CiUsageReport report)
            {
                return Result<CiUsageReport, string>.Success(report);
            }

            if (response.StatusCode == HttpStatusCode.Accepted
                && JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["finished"] is null)
            {
                return Result<CiUsageReport, string>.Failure("the instance started the read but does not wait for it; upgrade it to this release to get the table");
            }

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return Result<CiUsageReport, string>.Failure(
                    "the read started but had not finished after " + RefreshEndpoints.CiRoleWait.TotalMinutes.ToString(CultureInfo.InvariantCulture)
                    + " minutes; it is still running, and the dashboard shows each account as it lands");
            }

            string reason = await response.Content.ReadAsStringAsync(cancellationToken);
            string? message = null;
            try
            {
                message = JsonNode.Parse(reason)?["message"]?.GetValue<string>();
            }
            catch (System.Text.Json.JsonException)
            {
                // Not the refusal shape: report the status alone.
            }

            return Result<CiUsageReport, string>.Failure("the instance refused the read (" + (int)response.StatusCode + ")" + (message is null ? string.Empty : ": " + message));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Result<CiUsageReport, string>.Failure("the instance at " + url + " did not answer (" + exception.GetType().Name + ")");
        }
    }

    /// <summary>
    /// One line per CI-role account: its roster index, its role prefixes, how
    /// its turn in the pass went, and each window as its card shows it. The
    /// e-mail column is there only when asked for, so the default output can be
    /// pasted into a CI log or an issue.
    /// </summary>
    public static string Format(CiUsageReport report, bool emails)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Accounts.Count == 0)
        {
            return "No account on the roster backs a CI role.";
        }

        List<string[]> rows = [emails ? ["#", "Roles", "Email", "Read", "Usage"] : ["#", "Roles", "Read", "Usage"]];
        foreach (CiUsageRow row in report.Accounts)
        {
            string usage = string.Join(", ", row.Usage.Limits.Select(Limit));
            string index = row.Index.ToString(CultureInfo.InvariantCulture);
            string roles = string.Join(",", row.Roles);
            rows.Add(emails ? [index, roles, row.Email, row.Refresh.State, usage] : [index, roles, row.Refresh.State, usage]);
        }

        int[] widths = [.. Enumerable.Range(0, rows[0].Length).Select(column => rows.Max(row => row[column].Length))];
        StringBuilder table = new();
        foreach (string[] row in rows)
        {
            _ = table.AppendLine(string.Join("  ", row.Select((cell, column) => column == row.Length - 1 ? cell : cell.PadRight(widths[column]))).TrimEnd());
        }

        return table.ToString().TrimEnd();
    }

    private static string Limit(UsageLimitView limit)
    {
        if (!limit.Known)
        {
            return limit.Label + " unknown";
        }

        if (limit.WindowReset || limit.Percent is not double percent)
        {
            return limit.Label + " reset";
        }

        return limit.Label + " " + percent.ToString("0.#", CultureInfo.InvariantCulture) + "%"
            + (limit.ResetsAt is DateTimeOffset resets ? " resets " + resets.UtcDateTime.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) : string.Empty);
    }
}
