using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// What a stored key's answer tells the page: the store's own name, and the
/// key secrets Push API key would send a Console key to.
/// </summary>
public sealed class ApiKeyStoredViewTests
{
    private const string Email = "lane@example.com";

    // Built from pieces, so the source holds nothing a secret scanner would take for a real key.
    private const string PlantedKey = "sk-ant-" + "api03-planted-stored-view-2b8e";

    private static Uri Account(string email, string path) => new("/api/accounts/" + Uri.EscapeDataString(email) + path, UriKind.Relative);

    /// <summary>A rostered account backing a role with a key half and one with a token only.</summary>
    private static async Task<AppFactory> StartAsync(bool withRoles, CancellationToken cancellationToken)
    {
        AppFactory factory = new()
        {
            Overrides = services => services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new NamedStore())),
        };
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage added = await client.PostAsJsonAsync(new Uri("/api/accounts", UriKind.Relative), new { email = Email }, cancellationToken);
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        if (withRoles)
        {
            RosterFile rosterFile = factory.Services.GetRequiredService<RosterFile>();
            RosterEntry entry = (await rosterFile.ReadAsync(cancellationToken)).Find(AccountEmail.Parse(Email).Value)!;
            RosterEntry backing = entry
                .ClaimingToken(new CiRole("AUTOMATIONS", CiSecretScope.Organization, "octo"), new DateOnly(2026, 6, 1), null)
                .ClaimingToken(new CiRole(string.Empty, CiSecretScope.Repository, "octo/lane-one"), new DateOnly(2026, 6, 1), null);
            await rosterFile.UpdateAsync(roster => roster.With(backing), cancellationToken);
        }

        return factory;
    }

    private static async Task<JsonObject> PutAsync(AppFactory factory, string path)
    {
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage response = await client.PutAsJsonAsync(Account(Email, path), new { key = PlantedKey }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task AStoredConsoleKeyNamesTheStoreAndEachKeySecretPushWouldSendItTo()
    {
        await using AppFactory factory = await StartAsync(withRoles: true, TestContext.Current.CancellationToken);

        JsonObject body = await PutAsync(factory, "/api-key");

        body["storedIn"]!.GetValue<string>().ShouldBe("the test vault");
        // The token-only legacy role has no key half, so Push never sends it a key.
        body["pushesTo"]!.AsArray().Select(static node => node!.GetValue<string>()).ShouldBe(["AUTOMATIONS_CLAUDE_API_KEY in org octo"]);
        body["roster"]!["consoleApiKeySetOn"].ShouldNotBeNull();
        body.ToJsonString().ShouldNotContain(PlantedKey);
    }

    [Fact]
    public async Task AnAccountBackingNoKeyHalfHasNoPushTargets()
    {
        await using AppFactory factory = await StartAsync(withRoles: false, TestContext.Current.CancellationToken);

        JsonObject body = await PutAsync(factory, "/api-key");

        body["storedIn"]!.GetValue<string>().ShouldBe("the test vault");
        body["pushesTo"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAdminKeyIsNeverPushedWhateverRolesTheAccountBacks()
    {
        await using AppFactory factory = await StartAsync(withRoles: true, TestContext.Current.CancellationToken);

        JsonObject body = await PutAsync(factory, "/admin-key");

        body["storedIn"]!.GetValue<string>().ShouldBe("the test vault");
        body["pushesTo"]!.AsArray().ShouldBeEmpty();
        body["roster"]!["adminKeySetOn"].ShouldNotBeNull();
    }

    /// <summary>An OS store that always answers, under a name no real store has.</summary>
    private sealed class NamedStore : ISecretStore
    {
        private readonly Dictionary<(SecretKind, string), string> _items = [];

        public string DisplayName => "the test vault";

        public Task<Result<Unit, string>> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Result<Unit, string>.Success(Unit.Value));

        public Task<Result<Unit, string>> WriteAsync(SecretKind kind, AccountEmail email, string value, CancellationToken cancellationToken)
        {
            _items[(kind, email.Value)] = value;
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }

        public Task<Result<string?, string>> ReadAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken) =>
            Task.FromResult(Result<string?, string>.Success(_items.GetValueOrDefault((kind, email.Value))));

        public Task<Result<Unit, string>> DeleteAsync(SecretKind kind, AccountEmail email, CancellationToken cancellationToken)
        {
            _items.Remove((kind, email.Value));
            return Task.FromResult(Result<Unit, string>.Success(Unit.Value));
        }
    }
}
