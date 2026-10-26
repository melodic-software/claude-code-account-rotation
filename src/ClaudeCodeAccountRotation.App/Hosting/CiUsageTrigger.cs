using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Security;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Configuration;

namespace ClaudeCodeAccountRotation.App.Hosting;

/// <summary>
/// <c>--read-ci-usage</c>: asks the running instance for the CI-role pass,
/// with the headers every dashboard write takes, the way the rate-limit stop
/// hook asks for its read. Starts nothing of its own. The follower maps no
/// refresh route, so on that side it refuses before sending.
/// </summary>
internal static class CiUsageTrigger
{
    public const string Route = "/api/refresh/ci-roles";

    public static async Task<Result<Unit, string>> PostAsync(HttpClient client, RotationRole role, (string Url, string Token)? running, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (role == RotationRole.Follower)
        {
            return Result<Unit, string>.Failure("this is the follower, which runs no usage read; run --read-ci-usage with the leader's executable");
        }

        if (running is not var (url, token))
        {
            return Result<Unit, string>.Failure("no instance is running on this side; start it first");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(url.TrimEnd('/') + Route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(SameOriginMutationFilter.HeaderName, "1");
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return Result<Unit, string>.Success(Unit.Value);
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

            return Result<Unit, string>.Failure("the instance refused the read (" + (int)response.StatusCode + ")" + (message is null ? string.Empty : ": " + message));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return Result<Unit, string>.Failure("the instance at " + url + " did not answer (" + exception.GetType().Name + ")");
        }
    }
}
