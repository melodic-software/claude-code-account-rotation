using System.Net;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.Http;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class ClaudeOAuthTokenRefreshClientTests
{
    private const string UserAgent = "claude-code-account-rotation/1.2.3 (+https://github.com/melodic-software/claude-code-account-rotation)";

    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-09-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static string TokenBody(string refreshToken = "refresh-new") =>
        new JsonObject
        {
            ["access_token"] = "access-new",
            ["refresh_token"] = refreshToken,
            ["expires_in"] = 28800,
            ["refresh_token_expires_in"] = 2419200,
            ["scope"] = "user:inference user:profile",
        }.ToJsonString();

    [Fact]
    public async Task AnOkResponseReturnsTheRotatedTokensWithBothExpiriesRecomputed()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, TokenBody()));
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.IsSuccess.ShouldBeTrue();
        refreshed.Value.AccessToken.ShouldBe("access-new");
        refreshed.Value.RefreshToken.ShouldBe("refresh-new");
        refreshed.Value.AccessTokenExpiresAt.ShouldBe(_now.AddSeconds(28800));
        refreshed.Value.LoginExpiresAt.ShouldBe(_now.AddSeconds(2419200));
        refreshed.Value.Scopes.ShouldBe(["user:inference", "user:profile"]);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheRequestSaysWhoTheToolIsAndCarriesTheRefreshGrant()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, TokenBody()));
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        HttpRequestMessage sent = handler.Requests.Single();
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.RequestUri!.ToString().ShouldBe("https://platform.claude.com/v1/oauth/token");
        string.Join(" ", sent.Headers.GetValues("User-Agent")).ShouldBe(UserAgent);
        sent.Headers.Contains("anthropic-beta").ShouldBeFalse();
        sent.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");

        JsonObject payload = JsonNode.Parse(handler.Bodies.Single()!)!.AsObject();
        payload["grant_type"]!.GetValue<string>().ShouldBe("refresh_token");
        payload["refresh_token"]!.GetValue<string>().ShouldBe("refresh-old");
        payload["client_id"]!.GetValue<string>().ShouldBe("9d1c250a-e61b-44d9-88ed-5944d1962f5e");
    }

    [Fact]
    public async Task AResponseWithoutARotatedTokenKeepsTheOne()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, """{"access_token":"access-new","expires_in":28800}"""));
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.Value.RefreshToken.ShouldBe("refresh-old");
        refreshed.Value.LoginExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task ARateLimitedResponseCarriesItsRetryAfterAndIsNotRetried()
    {
        using HttpResponseMessage limited = new(HttpStatusCode.TooManyRequests);
        limited.Headers.Add("Retry-After", "300");
        using RecordingHandler handler = new(limited);
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.Error.Kind.ShouldBe(UsageReadFailureKind.RateLimited);
        refreshed.Error.RetryAfter.ShouldBe(TimeSpan.FromSeconds(300));
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnAbsoluteRetryAfterDateBecomesAWait()
    {
        using HttpResponseMessage limited = new(HttpStatusCode.TooManyRequests);
        limited.Headers.Add("Retry-After", _now.AddSeconds(120).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        using RecordingHandler handler = new(limited);
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.Error.RetryAfter.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task ARetryAfterDateAlreadyPastBecomesNoWaitAtAll()
    {
        using HttpResponseMessage limited = new(HttpStatusCode.TooManyRequests);
        limited.Headers.Add("Retry-After", _now.AddSeconds(-120).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        using RecordingHandler handler = new(limited);
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.Error.RetryAfter.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task ARetryAfterDateFarInTheFutureIsClampedToAnHour()
    {
        using HttpResponseMessage limited = new(HttpStatusCode.TooManyRequests);
        limited.Headers.Add("Retry-After", _now.AddDays(30).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        using RecordingHandler handler = new(limited);
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.Error.RetryAfter.ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ARedirectIsATransportFailureAndIsNotFollowed()
    {
        using HttpResponseMessage redirect = new(HttpStatusCode.TemporaryRedirect);
        redirect.Headers.Location = new Uri("https://redirect.example/token");
        using RecordingHandler handler = new(redirect);
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.IsFailure.ShouldBeTrue();
        refreshed.Error.Kind.ShouldBe(UsageReadFailureKind.Transport);
        refreshed.Error.Detail.ShouldBe("the endpoint answered 307");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests.Single().RequestUri!.ToString().ShouldBe("https://platform.claude.com/v1/oauth/token");
    }

    [Fact]
    public async Task ARefusedRefreshTokenIsARejectedLogin()
    {
        using RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.Error.Kind.ShouldBe(UsageReadFailureKind.LoginRejected);
        refreshed.Error.Detail.ShouldBe("the token endpoint rejected the refresh token (401 unknown error)");
    }

    [Fact]
    public async Task AnInvalidGrantIsARejectedLoginNamingOnlyTheOAuthError()
    {
        // The 400 a revoked or expired login gets. The free-text description is
        // the endpoint's, not the tool's, so only the error code is kept.
        using RecordingHandler handler = new(RecordingHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Refresh token refresh-old not found"}"""));
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.Error.Kind.ShouldBe(UsageReadFailureKind.LoginRejected);
        refreshed.Error.Detail.ShouldBe("the token endpoint rejected the refresh token (400 invalid_grant)");
        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"error":"invalid_client"}""", "the token endpoint answered 400 invalid_client")]
    [InlineData("""{"error":"invalid_request","error_description":"refresh-old"}""", "the token endpoint answered 400 invalid_request")]
    [InlineData("<html>blocked</html>", "the token endpoint answered 400 unknown error")]
    [InlineData("""{"error":"Invalid Grant: refresh-old"}""", "the token endpoint answered 400 unknown error")]
    [InlineData("""{"error":42}""", "the token endpoint answered 400 unknown error")]
    public async Task ABadRequestThatIsNotInvalidGrantSaysNothingAboutTheLoginAndIsTransport(string body, string detail)
    {
        // invalid_client, invalid_request, or a proxy's page: the client or the
        // path is wrong, not the login. Marking it dead would quarantine a
        // healthy pair, so it is retried like any other transport failure, and
        // only the sanitized error code is kept.
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.Error.Kind.ShouldBe(UsageReadFailureKind.Transport);
        refreshed.Error.Detail.ShouldBe(detail);
    }

    [Fact]
    public async Task AResponseWithoutAnAccessTokenIsAMalformedBodyFailure()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, """{"expires_in":28800}"""));
        using HttpClient http = new(handler);
        ClaudeOAuthTokenRefreshClient client = new(http, UserAgent, new FixedClock(_now));

        Result<RefreshedTokens, UsageReadFailure> refreshed = await client.RefreshAsync("refresh-old", TestContext.Current.CancellationToken);

        refreshed.Error.Kind.ShouldBe(UsageReadFailureKind.MalformedBody);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
