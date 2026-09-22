using System.Net;
using System.Text.Json;
using ClaudeCodeAccountRotation.App.Adapters.Http;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class AnthropicUsageEndpointClientTests
{
    private const string UserAgent = "claude-code-account-rotation/1.2.3 (+https://github.com/melodic-software/claude-code-account-rotation)";
    private const string UsageBody = """{"limits":[{"kind":"session","percent":43,"resets_at":"2026-09-08T00:00:00Z","is_active":true}]}""";

    [Fact]
    public async Task AnOkResponseReturnsTheBodyUnparsed()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, UsageBody));
        using HttpClient http = new(handler);
        AnthropicUsageEndpointClient client = new(http, UserAgent, TimeProvider.System);

        Result<JsonDocument, UsageReadFailure> read = await client.ReadUsageAsync("access-token", TestContext.Current.CancellationToken);

        read.IsSuccess.ShouldBeTrue();
        using JsonDocument body = read.Value;
        UsageResponseParser.ParseLimits(body.RootElement).Value.Count.ShouldBe(1);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EveryRequestSaysWhoTheToolIsAndAsksForTheOAuthBeta()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, UsageBody));
        using HttpClient http = new(handler);
        AnthropicUsageEndpointClient client = new(http, UserAgent, TimeProvider.System);

        (await client.ReadUsageAsync("access-token", TestContext.Current.CancellationToken)).Value.Dispose();

        HttpRequestMessage sent = handler.Requests.Single();
        sent.Method.ShouldBe(HttpMethod.Get);
        sent.RequestUri!.ToString().ShouldBe("https://api.anthropic.com/api/oauth/usage");
        string.Join(" ", sent.Headers.GetValues("User-Agent")).ShouldBe(UserAgent);
        sent.Headers.GetValues("anthropic-beta").Single().ShouldBe("oauth-2025-04-20");
        sent.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        sent.Headers.Authorization.Parameter.ShouldBe("access-token");
    }

    [Fact]
    public void TheRequestTimeoutIsTwentySeconds()
    {
        using RecordingHandler handler = new();
        using HttpClient http = new(handler);

        _ = new AnthropicUsageEndpointClient(http, UserAgent, TimeProvider.System);

        http.Timeout.ShouldBe(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ARateLimitedResponseCarriesItsRetryAfterAndIsNotRetried()
    {
        using HttpResponseMessage limited = new(HttpStatusCode.TooManyRequests);
        limited.Headers.Add("Retry-After", "300");
        using RecordingHandler handler = new(limited);
        using HttpClient http = new(handler);
        AnthropicUsageEndpointClient client = new(http, UserAgent, TimeProvider.System);

        Result<JsonDocument, UsageReadFailure> read = await client.ReadUsageAsync("access-token", TestContext.Current.CancellationToken);

        read.IsFailure.ShouldBeTrue();
        read.Error.Kind.ShouldBe(UsageReadFailureKind.RateLimited);
        read.Error.RetryAfter.ShouldBe(TimeSpan.FromSeconds(300));
        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    // A header the tool does not control must not park an account for a year,
    // nor a clock skew produce a negative wait.
    [InlineData("999999", 3600)]
    [InlineData("3601", 3600)]
    [InlineData("300", 300)]
    public async Task AnAbsurdRetryAfterIsClampedToAnHour(string retryAfter, int expectedSeconds)
    {
        using HttpResponseMessage limited = new(HttpStatusCode.TooManyRequests);
        limited.Headers.Add("Retry-After", retryAfter);
        using RecordingHandler handler = new(limited);
        using HttpClient http = new(handler);
        AnthropicUsageEndpointClient client = new(http, UserAgent, TimeProvider.System);

        Result<JsonDocument, UsageReadFailure> read = await client.ReadUsageAsync("access-token", TestContext.Current.CancellationToken);

        read.Error.RetryAfter.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public async Task AnUnauthorizedResponseIsTypedSoAParkedPairCanBeRefreshed()
    {
        using RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using HttpClient http = new(handler);
        AnthropicUsageEndpointClient client = new(http, UserAgent, TimeProvider.System);

        Result<JsonDocument, UsageReadFailure> read = await client.ReadUsageAsync("access-token", TestContext.Current.CancellationToken);

        read.Error.Kind.ShouldBe(UsageReadFailureKind.Unauthorized);
        read.Error.RetryAfter.ShouldBeNull();
    }

    [Fact]
    public async Task ARedirectIsATransportFailureAndIsNotFollowed()
    {
        using HttpResponseMessage redirect = new(HttpStatusCode.TemporaryRedirect);
        redirect.Headers.Location = new Uri("https://redirect.example/usage");
        using RecordingHandler handler = new(redirect);
        using HttpClient http = new(handler);
        AnthropicUsageEndpointClient client = new(http, UserAgent, TimeProvider.System);

        Result<JsonDocument, UsageReadFailure> read = await client.ReadUsageAsync("access-token", TestContext.Current.CancellationToken);

        read.IsFailure.ShouldBeTrue();
        read.Error.Kind.ShouldBe(UsageReadFailureKind.Transport);
        read.Error.Detail.ShouldBe("the endpoint answered 307");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests.Single().RequestUri!.ToString().ShouldBe("https://api.anthropic.com/api/oauth/usage");
    }

    [Fact]
    public async Task AServerErrorIsATransportFailureCarryingItsStatus()
    {
        using RecordingHandler handler = new(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using HttpClient http = new(handler);
        AnthropicUsageEndpointClient client = new(http, UserAgent, TimeProvider.System);

        Result<JsonDocument, UsageReadFailure> read = await client.ReadUsageAsync("access-token", TestContext.Current.CancellationToken);

        read.Error.Kind.ShouldBe(UsageReadFailureKind.Transport);
        read.Error.Detail.ShouldContain("503");
    }

    [Fact]
    public async Task AnUnparsableBodyIsAMalformedBodyFailure()
    {
        using RecordingHandler handler = new(RecordingHandler.Json(HttpStatusCode.OK, "{not json"));
        using HttpClient http = new(handler);
        AnthropicUsageEndpointClient client = new(http, UserAgent, TimeProvider.System);

        Result<JsonDocument, UsageReadFailure> read = await client.ReadUsageAsync("access-token", TestContext.Current.CancellationToken);

        read.Error.Kind.ShouldBe(UsageReadFailureKind.MalformedBody);
    }

    [Fact]
    public void TheUserAgentNamesTheToolItsVersionAndItsHome()
    {
        AnthropicEndpoints.UserAgent("claude-code-account-rotation", "1.2.3+abcdef")
            .ShouldBe("claude-code-account-rotation/1.2.3 (+https://github.com/melodic-software/claude-code-account-rotation)");
    }
}
