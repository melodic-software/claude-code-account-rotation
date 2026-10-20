using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// <c>PUT</c> and <c>DELETE /api/accounts/{email}/api-key</c>, and the account
/// removal that takes both stored keys with it. The store is doubled except in
/// the one test that runs the real composition on a Linux host.
/// </summary>
public sealed class ApiKeyEndpointTests
{
    private const string Parked = "parked@example.com";
    private const string Other = "other@example.com";
    private const string PlantedKey = "sk-ant-api03-planted-console-key-4c7e";

    public static bool OnLinux => OperatingSystem.IsLinux();

    private static Uri ApiKey(string email) => new("/api/accounts/" + Uri.EscapeDataString(email) + "/api-key", UriKind.Relative);

    private static Uri Account(string email) => new("/api/accounts/" + Uri.EscapeDataString(email), UriKind.Relative);

    /// <summary>A factory with the store doubled and <paramref name="emails"/> on the roster.</summary>
    private static async Task<(AppFactory Factory, FakeSecretStore Store)> RosteredAsync(CancellationToken cancellationToken, params string[] emails)
    {
        AppFactory factory = new();
        FakeSecretStore store = new();
        factory.Overrides = services => services.Replace(ServiceDescriptor.Singleton<ISecretStore>(store));
        await AddAsync(factory, cancellationToken, emails.Length == 0 ? [Parked] : emails);
        return (factory, store);
    }

    private static async Task AddAsync(AppFactory factory, CancellationToken cancellationToken, params string[] emails)
    {
        using HttpClient client = factory.CreateMutatingClient();
        foreach (string email in emails)
        {
            using HttpResponseMessage added = await client.PostAsJsonAsync(new Uri("/api/accounts", UriKind.Relative), new { email }, cancellationToken);
            added.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private static async Task<JsonObject?> RosterOfAsync(HttpClient client, string email)
    {
        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken))!;
        return dashboard["accounts"]!.AsArray()
            .Select(account => account!.AsObject())
            .Single(account => account["email"]!.GetValue<string>() == email)["roster"]?.AsObject();
    }

    private static string[] AppDataListing(AppFactory factory) =>
        [.. Directory.EnumerateFileSystemEntries(factory.AppData, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task PuttingAKeyStoresItAndRecordsTheDayWithoutReturningIt()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(ApiKey(Parked), new { key = "  " + PlantedKey + "\n" }, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        store.Items[(SecretKind.ConsoleApiKey, Parked)].ShouldBe(PlantedKey);
        body.ShouldNotContain(PlantedKey);
        // The host's clock reads 2026-09-07 12:00 UTC, which is that day locally anywhere within 11 hours of UTC.
        JsonNode.Parse(body)!["consoleApiKeySetOn"]!.GetValue<string>().ShouldBe("2026-09-07");
        (await RosterOfAsync(client, Parked))!["consoleApiKeySetOn"]!.GetValue<string>().ShouldBe("2026-09-07");
    }

    [Fact]
    public async Task DeletingTheKeyRemovesItFromTheStoreAndClearsTheDay()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage put = await client.PutAsJsonAsync(ApiKey(Parked), new { key = PlantedKey }, TestContext.Current.CancellationToken);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage response = await client.DeleteAsync(ApiKey(Parked), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        store.Items.ShouldNotContainKey((SecretKind.ConsoleApiKey, Parked));
        (await RosterOfAsync(client, Parked))!["consoleApiKeySetOn"].ShouldBeNull();
    }

    [Fact]
    public async Task AStoreThatCannotKeepAKeyIsA409WithItsCauseAndWritesNothing()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        store.ProbeError = "no Secret Service provider (stand-in)";
        using HttpClient client = factory.CreateMutatingClient();
        string[] before = AppDataListing(factory);
        string roster = await File.ReadAllTextAsync(Path.Combine(factory.AppData, "roster.json"), TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.PutAsJsonAsync(ApiKey(Parked), new { key = PlantedKey }, TestContext.Current.CancellationToken);
        JsonObject refusal = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        refusal["message"]!.GetValue<string>().ShouldBe("no Secret Service provider (stand-in)");
        store.Items.ShouldBeEmpty();
        AppDataListing(factory).ShouldBe(before);
        (await File.ReadAllTextAsync(Path.Combine(factory.AppData, "roster.json"), TestContext.Current.CancellationToken)).ShouldBe(roster);
    }

    [Fact(SkipUnless = nameof(OnLinux), Skip = "The Secret Service is the Linux store")]
    public async Task OnALinuxHostWithNoUsableSecretServiceTheRealStoreRefusesWithItsCauseAndNoFileAppears()
    {
        await using AppFactory factory = new();
        await AddAsync(factory, TestContext.Current.CancellationToken, Parked);
        Result<Unit, string> probe = await factory.Services.GetRequiredService<ISecretStore>().ProbeAsync(TestContext.Current.CancellationToken);
        if (probe.IsSuccess)
        {
            Assert.Skip("this host runs a Secret Service provider, so the store accepts a key");
        }

        using HttpClient client = factory.CreateMutatingClient();
        string[] before = AppDataListing(factory);

        using HttpResponseMessage response = await client.PutAsJsonAsync(ApiKey(Parked), new { key = PlantedKey }, TestContext.Current.CancellationToken);
        string message = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["message"]!.GetValue<string>();

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        message.ShouldBe(probe.Error);
        (message.StartsWith("libsecret is not installed", StringComparison.Ordinal) || message.StartsWith("no Secret Service provider", StringComparison.Ordinal))
            .ShouldBeTrue(message);
        AppDataListing(factory).ShouldBe(before);
    }

    [Fact]
    public async Task ABlankMissingOrNonTextKeyIsA400AndAKeyInTheUrlIsNeverRead()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        object[] bodies = [new { key = "" }, new { key = " \t\n " }, new { }, new { key = 42 }, new { key = (string?)null }];

        foreach (object body in bodies)
        {
            using HttpResponseMessage response = await client.PutAsJsonAsync(ApiKey(Parked), body, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using HttpRequestMessage queried = new(HttpMethod.Put, new Uri(ApiKey(Parked) + "?key=" + PlantedKey, UriKind.Relative));
        using HttpResponseMessage fromUrl = await client.SendAsync(queried, TestContext.Current.CancellationToken);
        using HttpResponseMessage fromUrlWithBody = await client.PutAsJsonAsync(
            new Uri(ApiKey(Parked) + "?key=" + PlantedKey, UriKind.Relative), new { }, TestContext.Current.CancellationToken);

        fromUrl.IsSuccessStatusCode.ShouldBeFalse();
        fromUrlWithBody.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        store.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task AnAccountOffTheRosterIsNotFound()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage put = await client.PutAsJsonAsync(ApiKey("stranger@example.com"), new { key = PlantedKey }, TestContext.Current.CancellationToken);
        using HttpResponseMessage delete = await client.DeleteAsync(ApiKey("stranger@example.com"), TestContext.Current.CancellationToken);

        put.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        delete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        store.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ACrossOriginPutIsRefused()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(ApiKey(Parked), new { key = PlantedKey }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeFalse();
        store.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NoResponseLogLineOrFileHoldsAPlantedKey()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        using HttpClient client = factory.CreateMutatingClient();
        List<string> seen = [];

        using (HttpResponseMessage put = await client.PutAsJsonAsync(ApiKey(Parked), new { key = PlantedKey }, TestContext.Current.CancellationToken))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.OK);
            seen.Add(await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        store.Items[(SecretKind.ConsoleApiKey, Parked)].ShouldBe(PlantedKey);
        seen.Add(await client.GetStringAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken));
        seen.Add(await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken));
        store.ProbeError = "no Secret Service provider (stand-in)";
        using (HttpResponseMessage refused = await client.PutAsJsonAsync(ApiKey(Parked), new { key = PlantedKey }, TestContext.Current.CancellationToken))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            seen.Add(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (HttpResponseMessage deleted = await client.DeleteAsync(ApiKey(Parked), TestContext.Current.CancellationToken))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
            seen.Add(await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        seen.ShouldAllBe(text => !text.Contains(PlantedKey, StringComparison.Ordinal));
        factory.Logs.Lines.ShouldNotBeEmpty();
        factory.Logs.Lines.ShouldContain(line => line.Contains("console API key for " + Parked + " stored", StringComparison.Ordinal));
        factory.Logs.Lines.ShouldNotContain(line => line.Contains(PlantedKey, StringComparison.Ordinal));
        byte[][] needles = [Encoding.UTF8.GetBytes(PlantedKey), Encoding.Unicode.GetBytes(PlantedKey)];
        foreach (string file in Directory.EnumerateFiles(factory.Root, "*", SearchOption.AllDirectories))
        {
            // The running host holds its single-instance lock exclusively; no
            // route writes to it.
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
    public async Task RemovingAnAccountDeletesBothOfItsStoredKeysAndNoOtherAccounts()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken, Parked, Other);
        await using AppFactory owned = factory;
        store.Items[(SecretKind.ConsoleApiKey, Parked)] = "console";
        store.Items[(SecretKind.AdminKey, Parked)] = "admin";
        store.Items[(SecretKind.ConsoleApiKey, Other)] = "other-console";
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.DeleteAsync(Account(Parked), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        store.Items.Keys.ShouldBe([(SecretKind.ConsoleApiKey, Other)]);
    }

    [Fact]
    public async Task ARefusedRemovalLeavesTheStoredKeys()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        await factory.WriteStateFileAsync(Parked, TestContext.Current.CancellationToken);
        store.Items[(SecretKind.ConsoleApiKey, Parked)] = "console";
        store.Items[(SecretKind.AdminKey, Parked)] = "admin";
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.DeleteAsync(Account(Parked), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        store.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AKeyTheStoreWillNotDeleteKeepsTheAccountOnTheRosterUntilARetrySucceeds()
    {
        (AppFactory factory, FakeSecretStore store) = await RosteredAsync(TestContext.Current.CancellationToken);
        await using AppFactory owned = factory;
        store.Items[(SecretKind.ConsoleApiKey, Parked)] = "console";
        store.DeleteError = "the Secret Service collection is locked; unlock it and try again";
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage refused = await client.DeleteAsync(Account(Parked), TestContext.Current.CancellationToken);
        JsonObject refusal = (await refused.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        refusal["message"]!.GetValue<string>().ShouldContain("collection is locked");
        (await RosterOfAsync(client, Parked)).ShouldNotBeNull();

        store.DeleteError = null;
        using HttpResponseMessage retried = await client.DeleteAsync(Account(Parked), TestContext.Current.CancellationToken);

        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        store.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFollowerMapsNeitherRoute()
    {
        await using FollowerAppFactory factory = new();
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage put = await client.PutAsJsonAsync(ApiKey(Parked), new { key = PlantedKey }, TestContext.Current.CancellationToken);
        using HttpResponseMessage delete = await client.DeleteAsync(ApiKey(Parked), TestContext.Current.CancellationToken);

        put.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        delete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>The OS store as a dictionary, with a cause a test can make it refuse with.</summary>
    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<(SecretKind Kind, string Email), string> Items { get; } = [];

        public string? ProbeError { get; set; }

        public string? DeleteError { get; set; }

        public int Writes { get; private set; }

        public Task<Result<Unit, string>> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProbeError is string error ? Result<Unit, string>.Failure(error) : Result<Unit, string>.Success(Unit.Value));

        public Task<Result<Unit, string>> WriteAsync(SecretKind kind, AccountEmail email, string value, CancellationToken cancellationToken)
        {
            Writes++;
            Items[(kind, email.Value)] = value;
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }

        public Task<Result<string?, string>> ReadAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken) =>
            Task.FromResult(Result<string?, string>.Success(Items.GetValueOrDefault((kind, email.Value))));

        public Task<Result<Unit, string>> DeleteAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken)
        {
            if (DeleteError is string error && Items.ContainsKey((kind, email.Value)))
            {
                return Task.FromResult(Result<Unit, string>.Failure(error));
            }

            Items.Remove((kind, email.Value));
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }
    }
}
