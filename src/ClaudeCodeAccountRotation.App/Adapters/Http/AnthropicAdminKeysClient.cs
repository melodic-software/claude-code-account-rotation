using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Adapters.Http;

/// <summary>
/// Lists the organization's API keys through the Admin API, page by page, with
/// the Admin key in <c>x-api-key</c> and the tool's own User-Agent. Key
/// metadata only, read on a click: nothing here touches the model API.
/// Basis: https://platform.claude.com/docs/en/api/admin-api/apikeys/list-api-keys.
/// </summary>
internal sealed class AnthropicAdminKeysClient : IAdminApiKeysClient
{
    /// <summary>The page size the endpoint allows at most, so a big organization takes few requests.</summary>
    private const int PageSize = 1000;

    /// <summary>
    /// The most pages one check reads. Basis: judgment: 50,000 keys is past any
    /// organization this tool serves, and a listing that never ends must not
    /// keep the check reading.
    /// </summary>
    private const int MaximumPages = 50;

    private readonly HttpClient _http;
    private readonly string _userAgent;

    public AnthropicAdminKeysClient(HttpClient http, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        http.Timeout = AnthropicEndpoints.RequestTimeout;
        _http = http;
        _userAgent = userAgent;
    }

    public async Task<Result<IReadOnlyList<ListedApiKey>, AdminApiFailure>> ListAsync(string adminKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminKey);
        List<ListedApiKey> keys = [];
        string? after = null;
        for (int page = 0; page < MaximumPages; page++)
        {
            Result<Page, AdminApiFailure> read = await ReadPageAsync(adminKey, after, cancellationToken);
            if (read.IsFailure)
            {
                return Result<IReadOnlyList<ListedApiKey>, AdminApiFailure>.Failure(read.Error);
            }

            keys.AddRange(read.Value.Keys);
            if (!read.Value.HasMore)
            {
                return Result<IReadOnlyList<ListedApiKey>, AdminApiFailure>.Success(keys);
            }

            if (read.Value.LastId is null)
            {
                return Failure("the key listing said there was more but gave no last_id to read it from");
            }

            after = read.Value.LastId;
        }

        return Failure("the key listing ran past " + MaximumPages.ToString(CultureInfo.InvariantCulture) + " pages");
    }

    private async Task<Result<Page, AdminApiFailure>> ReadPageAsync(string adminKey, string? after, CancellationToken cancellationToken)
    {
        string url = AnthropicEndpoints.AdminApiKeysUrl + "?limit=" + PageSize.ToString(CultureInfo.InvariantCulture)
            + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after));
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Add("x-api-key", adminKey);
        request.Headers.Add("anthropic-version", AnthropicEndpoints.ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return PageFailure(AdminApiFailureKind.Failed, "the Admin API could not be reached: " + exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PageFailure(AdminApiFailureKind.Failed, "the Admin API did not answer within " + AnthropicEndpoints.RequestTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return PageFailure(AdminApiFailureKind.Unavailable, "Admin API unavailable for this account ("
                    + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                    + "). The Admin API is unavailable for individual accounts, and it takes an Admin key, not a Console API key.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return PageFailure(AdminApiFailureKind.RateLimited, "the Admin API is rate limiting this key (429); the check stopped and does not retry");
            }

            if (!response.IsSuccessStatusCode)
            {
                return PageFailure(AdminApiFailureKind.Failed, "the Admin API answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            try
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
                return Parse(body.RootElement) is Page parsed
                    ? Result<Page, AdminApiFailure>.Success(parsed)
                    : PageFailure(AdminApiFailureKind.Failed, "the key listing did not have the documented shape");
            }
            catch (JsonException exception)
            {
                return PageFailure(AdminApiFailureKind.Failed, "the key listing did not parse: " + exception.Message);
            }
        }
    }

    /// <summary>One page, or null when it is not the documented shape.</summary>
    private static Page? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("has_more", out JsonElement hasMore) || hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        List<ListedApiKey> keys = [];
        foreach (JsonElement item in data.EnumerateArray())
        {
            if (Text(item, "id") is not string id || Text(item, "status") is not string status)
            {
                return null;
            }

            DateTimeOffset? expiresAt = null;
            if (Text(item, "expires_at") is string expires)
            {
                if (!DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                {
                    return null;
                }

                expiresAt = parsed;
            }

            keys.Add(new ListedApiKey(id, Text(item, "name") ?? "", status, Text(item, "partial_key_hint"), expiresAt));
        }

        return new Page(keys, hasMore.GetBoolean(), Text(root, "last_id"));
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Result<Page, AdminApiFailure> PageFailure(AdminApiFailureKind kind, string message) =>
        Result<Page, AdminApiFailure>.Failure(new AdminApiFailure(kind, message));

    private static Result<IReadOnlyList<ListedApiKey>, AdminApiFailure> Failure(string message) =>
        Result<IReadOnlyList<ListedApiKey>, AdminApiFailure>.Failure(new AdminApiFailure(AdminApiFailureKind.Failed, message));

    private sealed record Page(List<ListedApiKey> Keys, bool HasMore, string? LastId);
}
