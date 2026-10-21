using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Tests.Adapters;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// <c>PUT</c> and <c>DELETE /api/accounts/{email}/admin-key</c>, and
/// <c>POST .../api-key/check</c>, which lists the organization's keys through
/// the Admin API on the click only. The store is doubled; the Admin API is the
/// factory's scripted outbound handler, whose bodies follow the documented
/// List API Keys example.
/// </summary>
public sealed class AdminKeyEndpointTests
{
    private const string Parked = "parked@example.com";
    private const string ConsoleKey = "sk-ant-" + "api03-R2D" + "planted-console-key" + "igAA";
    private const string MatchingHint = "sk-ant-" + "api03-R2D...igAA";
    private const string PlantedAdminKey = "sk-ant-" + "admin01-planted-admin-key-7d1e";

    private static Uri Route(string email, string tail) => new("/api/accounts/" + Uri.EscapeDataString(email) + tail, UriKind.Relative);

    private static Uri AdminKeyRoute => Route(Parked, "/admin-key");

    private static Uri CheckRoute => Route(Parked, "/api-key/check");

    private static async Task<(AppFactory Factory, FakeSecretStore Store)> RosteredAsync(CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        FakeSecretStore store = new();
        factory.Overrides = services => services.Replace(ServiceDescriptor.Singleton<ISecretStore>(store));
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage added = await client.PostAsJsonAsync(new Uri("/api/accounts", UriKind.Relative), new { email = Parked }, cancellationToken);
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (factory, store);
    }

    /// <summary>Both keys stored through their routes, as the page stores them.</summary>
    private static async Task<List<string>> StoreBothAsync(HttpClient client)
    {
        using HttpResponseMessage console = await client.PutAsJsonAsync(Route(Parked, "/api-key"), new { key = ConsoleKey }, TestContext.Current.CancellationToken);
        using HttpResponseMessage admin = await client.PutAsJsonAsync(AdminKeyRoute, new { key = PlantedAdminKey }, TestContext.Current.CancellationToken);
        console.StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [await admin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)];
    }

    private static string Listing(params (string Status, string? Hint, string? ExpiresAt)[] keys) =>
        "{\"data\":[" + string.Join(",", keys.Select((key, index) =>
            "{\"id\":\"apikey_0" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\",\"type\":\"api_key\",\"name\":\"ci key\""
            + ",\"status\":\"" + key.Status + "\""
            + ",\"partial_key_hint\":" + (key.Hint is null ? "null" : "\"" + key.Hint + "\"")
            + ",\"expires_at\":" + (key.ExpiresAt is null ? "null" : "\"" + key.ExpiresAt + "\"") + "}"))
        + "],\"has_more\":false,\"first_id\":null,\"last_id\":null}";

    /// <summary>
    /// One scripted Admin API answer. The adapter that dequeues the response
    /// disposes it; CA2000 cannot see that hand-off across the queue.
    /// </summary>
#pragma warning disable CA2000 // Dispose objects before losing scope
    private static void Script(AppFactory factory, HttpStatusCode status, string body) =>
        factory.Outbound.Enqueue(RecordingHandler.Json(status, body));
#pragma warning restore CA2000

    private static async Task<JsonObject> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;

    private static async Task<JsonObject?> RosterOfAsync(HttpClient client)
    {
        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken))!;
        return dashboard["accounts"]!.AsArray()
            .Select(account => account!.AsObject())
            .Single(account => account["email"]!.GetValue<string>() == Parked)["roster"]?.AsObject();
    }

    [Fact]
    public async Task PuttingAnAdminKeyStoresItAndRecordsTheDayWithoutReturningIt()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(AdminKeyRoute, new { key = PlantedAdminKey + "\n" }, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        store.Items[(SecretKind.AdminKey, Parked)].ShouldBe(PlantedAdminKey);
        store.Items.ShouldNotContainKey((SecretKind.ConsoleApiKey, Parked));
        body.ShouldNotContain(PlantedAdminKey);
        // The host's clock reads 2026-09-07 12:00 UTC.
        JsonNode.Parse(body)!["adminKeySetOn"]!.GetValue<string>().ShouldBe("2026-09-07");
        JsonObject roster = (await RosterOfAsync(client))!;
        roster["adminKeySetOn"]!.GetValue<string>().ShouldBe("2026-09-07");
        roster["consoleApiKeySetOn"].ShouldBeNull();
    }

    [Fact]
    public async Task DeletingTheAdminKeyRemovesItAndClearsItsDayAndLeavesTheConsoleKey()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await StoreBothAsync(client);

        using HttpResponseMessage response = await client.DeleteAsync(AdminKeyRoute, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        store.Items.Keys.ShouldBe([(SecretKind.ConsoleApiKey, Parked)]);
        JsonObject roster = (await RosterOfAsync(client))!;
        roster["adminKeySetOn"].ShouldBeNull();
        roster["consoleApiKeySetOn"].ShouldNotBeNull();
        factory.Logs.Lines.ShouldContain(line => line.Contains("Admin key for " + Parked + " deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARecordedAdminKeyTheStoreCannotDeleteHoldsUpTheAccountsRemoval()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage put = await client.PutAsJsonAsync(AdminKeyRoute, new { key = PlantedAdminKey }, TestContext.Current.CancellationToken))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        store.DeleteError = "no Secret Service provider (stand-in)";

        using HttpResponseMessage response = await client.DeleteAsync(Route(Parked, ""), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyAsync(response))["message"]!.GetValue<string>().ShouldContain("claude-code-account-rotation:admin-key:" + Parked);
        (await RosterOfAsync(client))!["adminKeySetOn"].ShouldNotBeNull();
    }

    [Theory]
    [InlineData("archived", "archived")]
    [InlineData("inactive", "inactive")]
    [InlineData("expired", "expired")]
    public async Task TheCheckFindsTheStoredKeyByItsHintAndShowsItsStatus(string status, string flag)
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await StoreBothAsync(client);
        Script(factory, HttpStatusCode.OK, Listing(("active", "sk-ant-" + "api03-Zz9...q0Q0", null), (status, MatchingHint, null)));

        using HttpResponseMessage response = await client.PostAsync(CheckRoute, null, TestContext.Current.CancellationToken);
        JsonObject view = await BodyAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        view["outcome"]!.GetValue<string>().ShouldBe("found");
        view["status"]!.GetValue<string>().ShouldBe(status);
        view["flag"]!.GetValue<string>().ShouldBe(flag);
        view["name"]!.GetValue<string>().ShouldBe("ci key");
    }

    [Fact]
    public async Task AnActiveKeyNearItsExpiryIsFlaggedExpiresSoon()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await StoreBothAsync(client);
        // Ten days after the host's clock.
        Script(factory, HttpStatusCode.OK, Listing(("active", MatchingHint, "2026-09-17T12:00:00Z")));

        using HttpResponseMessage response = await client.PostAsync(CheckRoute, null, TestContext.Current.CancellationToken);
        JsonObject view = await BodyAsync(response);

        view["flag"]!.GetValue<string>().ShouldBe("expiresSoon");
        view["expiresAt"].ShouldNotBeNull();
    }

    [Fact]
    public async Task ANullHintIsCannotTell()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await StoreBothAsync(client);
        Script(factory, HttpStatusCode.OK, Listing(("archived", null, null)));

        using HttpResponseMessage response = await client.PostAsync(CheckRoute, null, TestContext.Current.CancellationToken);
        JsonObject view = await BodyAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        view["outcome"]!.GetValue<string>().ShouldBe("cannotTell");
        view["status"].ShouldBeNull();
        view["reason"]!.GetValue<string>().ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A401Or403ShowsTheAdminApiIsUnavailableForThisAccount(HttpStatusCode status)
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await StoreBothAsync(client);
        Script(factory, status, """{"type":"error","error":{"type":"permission_error","message":"denied"}}""");

        using HttpResponseMessage response = await client.PostAsync(CheckRoute, null, TestContext.Current.CancellationToken);
        JsonObject refusal = await BodyAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        refusal["refusal"]!.GetValue<string>().ShouldBe("AdminApiUnavailable");
        refusal["message"]!.GetValue<string>().ShouldStartWith("Admin API unavailable for this account");
    }

    [Fact]
    public async Task WithNoAdminKeyStoredTheCheckIsRefusedAndCallsNothing()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        using (HttpResponseMessage put = await client.PutAsJsonAsync(Route(Parked, "/api-key"), new { key = ConsoleKey }, TestContext.Current.CancellationToken))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage response = await client.PostAsync(CheckRoute, null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyAsync(response))["refusal"]!.GetValue<string>().ShouldBe("NoAdminKey");
        factory.Outbound.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task LoadingTheDashboardCallsTheAdminApiNever()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        await StoreBothAsync(client);
        int readsBefore = store.Reads;

        await client.GetStringAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);
        await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        factory.Outbound.Requests.ShouldBeEmpty();
        store.Reads.ShouldBe(readsBefore);
    }

    [Fact]
    public async Task TheAdminKeyIsSentAsXApiKeyAndNeverReachesAResponseLogLineOrFile()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        List<string> seen = await StoreBothAsync(client);
        Script(factory, HttpStatusCode.OK, Listing(("active", MatchingHint, null)));
        Script(factory, HttpStatusCode.Unauthorized, "{}");

        using (HttpResponseMessage found = await client.PostAsync(CheckRoute, null, TestContext.Current.CancellationToken))
        {
            found.StatusCode.ShouldBe(HttpStatusCode.OK);
            seen.Add(await found.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (HttpResponseMessage unavailable = await client.PostAsync(CheckRoute, null, TestContext.Current.CancellationToken))
        {
            unavailable.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            seen.Add(await unavailable.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        seen.Add(await client.GetStringAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken));
        using (HttpResponseMessage deleted = await client.DeleteAsync(AdminKeyRoute, TestContext.Current.CancellationToken))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
            seen.Add(await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        factory.Outbound.Requests.Select(request => request.Headers.GetValues("x-api-key").Single()).ShouldBe([PlantedAdminKey, PlantedAdminKey]);
        seen.ShouldAllBe(text => !text.Contains(PlantedAdminKey, StringComparison.Ordinal) && !text.Contains(ConsoleKey, StringComparison.Ordinal));
        factory.Logs.Lines.ShouldContain(line => line.Contains("Admin key for " + Parked + " stored", StringComparison.Ordinal));
        factory.Logs.Lines.ShouldContain(line => line.Contains("API key check for " + Parked, StringComparison.Ordinal));
        factory.Logs.Lines.ShouldNotContain(line => line.Contains(PlantedAdminKey, StringComparison.Ordinal));
        byte[][] needles = [Encoding.UTF8.GetBytes(PlantedAdminKey), Encoding.Unicode.GetBytes(PlantedAdminKey)];
        foreach (string file in Directory.EnumerateFiles(factory.Root, "*", SearchOption.AllDirectories))
        {
            // The running host holds its single-instance lock exclusively; no route writes to it.
            if (Path.GetFileName(file) == "instance.lock")
            {
                continue;
            }

            byte[] bytes = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
            foreach (byte[] needle in needles)
            {
                bytes.AsSpan().IndexOf(needle).ShouldBe(-1, file);
            }
        }
    }

    [Fact]
    public async Task AFollowerMapsNoneOfTheRoutes()
    {
        await using FollowerAppFactory factory = new();
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage put = await client.PutAsJsonAsync(AdminKeyRoute, new { key = PlantedAdminKey }, TestContext.Current.CancellationToken);
        using HttpResponseMessage delete = await client.DeleteAsync(AdminKeyRoute, TestContext.Current.CancellationToken);
        using HttpResponseMessage check = await client.PostAsync(CheckRoute, null, TestContext.Current.CancellationToken);

        put.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        delete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        check.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>The OS store as a dictionary, counting reads, with a cause a test can make deletes fail with.</summary>
    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<(SecretKind Kind, string Email), string> Items { get; } = [];

        public string? DeleteError { get; set; }

        public int Reads { get; private set; }

        public Task<Result<Unit, string>> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<Unit, string>.Success(Unit.Value));

        public Task<Result<Unit, string>> WriteAsync(SecretKind kind, AccountEmail email, string value, CancellationToken cancellationToken)
        {
            Items[(kind, email.Value)] = value;
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }

        public Task<Result<string?, string>> ReadAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Result<string?, string>.Success(Items.GetValueOrDefault((kind, email.Value))));
        }

        public Task<Result<Unit, string>> DeleteAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken)
        {
            if (DeleteError is string error)
            {
                return Task.FromResult(Result<Unit, string>.Failure(error));
            }

            Items.Remove((kind, email.Value));
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }
    }
}
