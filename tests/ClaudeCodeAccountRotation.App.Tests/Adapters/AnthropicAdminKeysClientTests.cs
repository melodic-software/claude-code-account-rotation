using System.Net;
using ClaudeCodeAccountRotation.App.Adapters.Http;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The Admin API key listing. Response bodies follow the documented List API
/// Keys example (platform.claude.com, admin-api/apikeys/list-api-keys).
/// </summary>
public sealed class AnthropicAdminKeysClientTests
{
    private const string UserAgent = "claude-code-account-rotation/1.2.3 (+https://github.com/melodic-software/claude-code-account-rotation)";
    private const string AdminKey = "sk-ant-" + "admin01-test-admin-key";

    private static string Item(string id, string status, string? hint, string? expiresAt) =>
        "{\"id\":\"" + id + "\",\"type\":\"api_key\",\"name\":\"" + id + " name\",\"status\":\"" + status + "\""
        + ",\"partial_key_hint\":" + (hint is null ? "null" : "\"" + hint + "\"")
        + ",\"expires_at\":" + (expiresAt is null ? "null" : "\"" + expiresAt + "\"")
        + ",\"created_at\":\"2024-10-30T23:58:27.427722Z\"}";

    private static string Page(bool hasMore, string? lastId, params string[] items) =>
        "{\"data\":[" + string.Join(",", items) + "],\"has_more\":" + (hasMore ? "true" : "false")
        + ",\"first_id\":null,\"last_id\":" + (lastId is null ? "null" : "\"" + lastId + "\"") + "}";

    private static async Task<(Result<IReadOnlyList<ListedApiKey>, AdminApiFailure> Result, RecordingHandler Handler)> ListAsync(params HttpResponseMessage[] responses)
    {
        RecordingHandler handler = new(responses);
        using HttpClient http = new(handler, disposeHandler: false);
        AnthropicAdminKeysClient client = new(http, UserAgent);
        return (await client.ListAsync(AdminKey, TestContext.Current.CancellationToken), handler);
    }

    [Fact]
    public async Task TheRequestSendsTheAdminKeyTheApiVersionAndTheToolsOwnUserAgent()
    {
        (Result<IReadOnlyList<ListedApiKey>, AdminApiFailure> listed, RecordingHandler handler) = await ListAsync(
            RecordingHandler.Json(HttpStatusCode.OK, Page(false, null)));
        using RecordingHandler owned = handler;

        listed.IsSuccess.ShouldBeTrue();
        HttpRequestMessage sent = handler.Requests.Single();
        sent.Method.ShouldBe(HttpMethod.Get);
        sent.RequestUri!.ToString().ShouldBe("https://api.anthropic.com/v1/organizations/api_keys?limit=1000");
        sent.Headers.GetValues("x-api-key").Single().ShouldBe(AdminKey);
        sent.Headers.GetValues("anthropic-version").Single().ShouldBe("2023-06-01");
        string.Join(" ", sent.Headers.GetValues("User-Agent")).ShouldBe(UserAgent);
        sent.Headers.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task PagingFollowsHasMoreWithLastIdAsAfterId()
    {
        (Result<IReadOnlyList<ListedApiKey>, AdminApiFailure> listed, RecordingHandler handler) = await ListAsync(
            RecordingHandler.Json(HttpStatusCode.OK, Page(true, "apikey_01", Item("apikey_01", "active", "sk-ant-" + "api03-R2D...igAA", null))),
            RecordingHandler.Json(HttpStatusCode.OK, Page(false, "apikey_02", Item("apikey_02", "archived", null, "2026-11-01T00:00:00Z"))));
        using RecordingHandler owned = handler;

        handler.Requests.Select(request => request.RequestUri!.Query).ShouldBe(["?limit=1000", "?limit=1000&after_id=apikey_01"]);
        listed.Value.Select(key => key.Id).ShouldBe(["apikey_01", "apikey_02"]);
        listed.Value[0].PartialKeyHint.ShouldBe("sk-ant-" + "api03-R2D...igAA");
        listed.Value[1].Status.ShouldBe("archived");
        listed.Value[1].PartialKeyHint.ShouldBeNull();
        listed.Value[1].ExpiresAt.ShouldBe(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task HasMoreWithNoLastIdStopsWithAFailureRatherThanRereadingThePage()
    {
        (Result<IReadOnlyList<ListedApiKey>, AdminApiFailure> listed, RecordingHandler handler) = await ListAsync(
            RecordingHandler.Json(HttpStatusCode.OK, Page(true, null)));
        using RecordingHandler owned = handler;

        listed.Error.Kind.ShouldBe(AdminApiFailureKind.Failed);
        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A401Or403IsTheAdminApiUnavailableForThisAccount(HttpStatusCode status)
    {
        (Result<IReadOnlyList<ListedApiKey>, AdminApiFailure> listed, RecordingHandler handler) = await ListAsync(
            RecordingHandler.Json(status, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));
        using RecordingHandler owned = handler;

        listed.Error.Kind.ShouldBe(AdminApiFailureKind.Unavailable);
        listed.Error.Message.ShouldStartWith("Admin API unavailable for this account");
        listed.Error.Message.ShouldContain("unavailable for individual accounts");
    }

    [Fact]
    public async Task A429EndsTheListingWithNoRetry()
    {
        (Result<IReadOnlyList<ListedApiKey>, AdminApiFailure> listed, RecordingHandler handler) = await ListAsync(
            RecordingHandler.Json(HttpStatusCode.OK, Page(true, "apikey_01", Item("apikey_01", "active", null, null))),
            RecordingHandler.Json(HttpStatusCode.TooManyRequests, "{}"));
        using RecordingHandler owned = handler;

        listed.Error.Kind.ShouldBe(AdminApiFailureKind.RateLimited);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ABodyWithoutTheDocumentedShapeIsAFailure()
    {
        (Result<IReadOnlyList<ListedApiKey>, AdminApiFailure> listed, RecordingHandler handler) = await ListAsync(
            RecordingHandler.Json(HttpStatusCode.OK, """{"keys":[]}"""));
        using RecordingHandler owned = handler;

        listed.Error.Kind.ShouldBe(AdminApiFailureKind.Failed);
    }
}
