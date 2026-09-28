using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Adapters.Http;

/// <summary>
/// Renews one parked pair through the OAuth token endpoint, exactly as spike 03
/// did. The rotated tokens are returned to the caller and written nowhere:
/// this client owns no file, and the old refresh token is dead the moment the
/// endpoint answers 200, so the caller must persist what comes back.
/// </summary>
internal sealed class ClaudeOAuthTokenRefreshClient : ITokenRefreshClient
{
    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly TimeProvider _timeProvider;

    public ClaudeOAuthTokenRefreshClient(HttpClient http, string userAgent, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        http.Timeout = AnthropicEndpoints.RequestTimeout;
        _http = http;
        _userAgent = userAgent;
        _timeProvider = timeProvider;
    }

    public async Task<Result<RefreshedTokens, UsageReadFailure>> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        JsonObject payload = new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = AnthropicEndpoints.ClaudeCodeClientId,
        };
        using HttpRequestMessage request = new(HttpMethod.Post, AnthropicEndpoints.TokenUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return Failure(UsageReadFailureKind.Transport, "the token endpoint could not be reached: " + exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(UsageReadFailureKind.Transport, "the token refresh timed out after " + AnthropicEndpoints.RequestTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return Result<RefreshedTokens, UsageReadFailure>.Failure(AnthropicEndpoints.Failure(response, _timeProvider));
            }

            try
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
                return Tokens(body.RootElement, refreshToken);
            }
            catch (JsonException exception)
            {
                return Failure(UsageReadFailureKind.MalformedBody, "the token response did not parse: " + exception.Message);
            }
        }
    }

    /// <summary>
    /// The response shape spike 03 recorded. The login's own expiry is
    /// recomputed from <c>refresh_token_expires_in</c> rather than carried over,
    /// so the pair records whatever expiry the endpoint reports.
    /// </summary>
    private Result<RefreshedTokens, UsageReadFailure> Tokens(JsonElement body, string sentRefreshToken)
    {
        string? accessToken = Text(body, "access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            return Failure(UsageReadFailureKind.MalformedBody, "the token response carried no access_token");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        return Result<RefreshedTokens, UsageReadFailure>.Success(new RefreshedTokens(
            accessToken,
            // An endpoint that declines to rotate leaves the pair on the token it sent.
            Text(body, "refresh_token") ?? sentRefreshToken,
            now + TimeSpan.FromSeconds(Number(body, "expires_in") ?? 0),
            Number(body, "refresh_token_expires_in") is double loginSeconds ? now + TimeSpan.FromSeconds(loginSeconds) : null,
            Text(body, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []));
    }

    private static Result<RefreshedTokens, UsageReadFailure> Failure(UsageReadFailureKind kind, string detail) =>
        Result<RefreshedTokens, UsageReadFailure>.Failure(new UsageReadFailure(kind, detail));

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            ? number
            : null;
}
