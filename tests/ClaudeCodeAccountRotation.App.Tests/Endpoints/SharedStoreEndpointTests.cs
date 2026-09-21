using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// The shared store through the hosted app over temp roots: the refusal the
/// page gets when the other side holds an account, the plain word every card
/// carries, and the fact that with <c>store.shared</c> off none of it happens.
/// </summary>
public sealed class SharedStoreEndpointTests
{
    private static Uri SwitchUri(string email) => new("/api/accounts/" + Uri.EscapeDataString(email) + "/switch", UriKind.Relative);

    private static Uri LoginUri(string email) => new("/api/accounts/" + Uri.EscapeDataString(email) + "/login", UriKind.Relative);

    private static readonly Uri _dashboard = new("/api/dashboard", UriKind.Relative);

    /// <summary>A slot as a held one really looks: an identity, no pair, and a record naming the other side.</summary>
    private static async Task HeldByWslAsync(AppFactory factory, string email, string refreshToken, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(factory.ProfilesRoot, email);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "profile.json"), AppFactory.AccountJson(email).ToJsonString(), cancellationToken);
        await HolderRecordFile.WriteAsync(
            folder,
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair(refreshToken).Fingerprint, new DateTimeOffset(2026, 9, 20, 14, 2, 0, TimeSpan.Zero)),
            cancellationToken);
    }

    private static JsonObject Card(JsonObject dashboard, string email) =>
        dashboard["accounts"]!.AsArray().Single(card => card!["email"]!.GetValue<string>() == email)!.AsObject();

    [Fact]
    public async Task ASwitchToAnAccountTheOtherSideHoldsIsRefusedWithAConflict()
    {
        using AppFactory factory = new(sharedStore: true);
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        await HeldByWslAsync(factory, "b@example.com", "refresh-b", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe(nameof(SwitchRefusal.HeldByOtherSide));
        // The live pair did not move: a refusal costs the operator nothing.
        (await CredentialFiles.FingerprintAsync(factory.LiveDirectory, TestContext.Current.CancellationToken))
            .ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
    }

    [Fact]
    public async Task WithTheStoreNotSharedTheSameRecordIsIgnoredAndTheRefusalNamesTheMissingPair()
    {
        using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        await HeldByWslAsync(factory, "b@example.com", "refresh-b", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe(nameof(SwitchRefusal.TargetHasNoCredentials));
    }

    /// <summary>Live on a@example.com, with a b@example.com slot the other side holds and a roster entry for it.</summary>
    private static async Task<AppFactory> LiveOnAWithBHeldByWslAsync(bool sharedStore, CancellationToken cancellationToken)
    {
        AppFactory factory = new(sharedStore);
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", cancellationToken);
        await factory.WriteStateFileAsync("a@example.com", cancellationToken);
        factory.Cli.Email = "a@example.com";
        await HeldByWslAsync(factory, "b@example.com", "refresh-b", cancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage added = await client.PostAsJsonAsync(
            new Uri("/api/accounts", UriKind.Relative),
            new { email = "b@example.com", browser = "brave", browserProfileDirectory = "Profile 3" },
            cancellationToken);
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        return factory;
    }

    [Fact]
    public async Task ALoginIntoASlotTheOtherSideHoldsIsRefusedSoNoSecondFamilyIsCreated()
    {
        using AppFactory factory = await LiveOnAWithBHeldByWslAsync(sharedStore: true, TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(LoginUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe("HeldByOtherSide");
        // Nothing was started, so no CLI child ever ran against that folder and
        // the slot did not gain a second family beside the one the distro holds.
        factory.LoginChild.Children.ShouldBeEmpty();
        File.Exists(Path.Combine(factory.ProfilesRoot, "b@example.com", CredentialFiles.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task WithTheStoreNotSharedTheSameSlotStillAcceptsALogin()
    {
        using AppFactory factory = await LiveOnAWithBHeldByWslAsync(sharedStore: false, TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(LoginUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.LoginChild.Children.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task RemovingASlotTheOtherSideHoldsIsRefusedSoTheRecordAndTheRosterEntryStand()
    {
        using AppFactory factory = await LiveOnAWithBHeldByWslAsync(sharedStore: true, TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.DeleteAsync(
            new Uri("/api/accounts/" + Uri.EscapeDataString("b@example.com"), UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe("HeldByOtherSide");
        // The one thing on this side that says the distro holds a pair is still there,
        // no logout was attempted against a folder with nothing in it, and the card stays.
        File.Exists(Path.Combine(factory.ProfilesRoot, "b@example.com", HolderRecordFile.FileName)).ShouldBeTrue();
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(_dashboard, TestContext.Current.CancellationToken))!;
        Card(dashboard, "b@example.com")["roster"].ShouldNotBeNull();
    }

    [Fact]
    public async Task WithTheStoreNotSharedTheSameSlotIsStillRemovable()
    {
        using AppFactory factory = await LiveOnAWithBHeldByWslAsync(sharedStore: false, TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.DeleteAsync(
            new Uri("/api/accounts/" + Uri.EscapeDataString("b@example.com"), UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EveryCardCarriesItsSlotWordAndTheHeldOneNamesTheOtherSide()
    {
        using AppFactory factory = new(sharedStore: true);
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync("c@example.com", "refresh-c", TestContext.Current.CancellationToken);
        await HeldByWslAsync(factory, "b@example.com", "refresh-b", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(_dashboard, TestContext.Current.CancellationToken))!;

        Card(dashboard, "b@example.com")["slot"]!.GetValue<string>().ShouldBe("held-elsewhere");
        Card(dashboard, "c@example.com")["slot"]!.GetValue<string>().ShouldBe("parked");
        Card(dashboard, "a@example.com")["slot"]!.GetValue<string>().ShouldBe("held-here");
        dashboard.ToJsonString().ShouldNotContain("refresh-");
    }

    /// <summary>
    /// The page reads the chip the server sends and the two switch verdicts
    /// beside it, so the slot's plain wire word is not a thing it renders or
    /// branches on. Asserted against the served script because that, not the
    /// file in the tree, is what an operator's browser runs.
    /// </summary>
    [Fact]
    public async Task ThePageRendersNoneOfTheSlotsPlainWords()
    {
        using AppFactory factory = new(sharedStore: true);
        using HttpClient client = factory.CreateClient();

        string script = await client.GetStringAsync(new Uri("/app.js", UriKind.Relative), TestContext.Current.CancellationToken);

        foreach (string word in new[] { "held-here", "held-elsewhere", "in-transit", "never-logged-in" })
        {
            script.ShouldNotContain(word);
        }
    }

    [Fact]
    public async Task WithTheStoreNotSharedNoCardCarriesASlotWord()
    {
        using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync("c@example.com", "refresh-c", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(_dashboard, TestContext.Current.CancellationToken))!;

        dashboard["accounts"]!.AsArray().ShouldAllBe(card => card!["slot"] == null);
    }

    [Fact]
    public async Task ADashboardReadDropsARecordOnASlotThatStillHoldsItsPairAndSaysSo()
    {
        using AppFactory factory = new(sharedStore: true);
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync("b@example.com", "refresh-b", TestContext.Current.CancellationToken);
        await HolderRecordFile.WriteAsync(
            folder,
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-b").Fingerprint, DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(_dashboard, TestContext.Current.CancellationToken))!;

        Card(dashboard, "b@example.com")["slot"]!.GetValue<string>().ShouldBe("parked");
        File.Exists(Path.Combine(folder, HolderRecordFile.FileName)).ShouldBeFalse();
        factory.Logs.Lines.ShouldContain(line => line.Contains("holder record dropped: slot holds a pair", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASwitchRunThroughTheAppLeavesTheIncomingSlotRecordedAndTheOutgoingOneClear()
    {
        using AppFactory factory = new(sharedStore: true);
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync("a@example.com", TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync("b@example.com", "refresh-b", TestContext.Current.CancellationToken);
        factory.Cli.Email = "b@example.com";
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(SwitchUri("b@example.com"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HolderRecord? incoming = await HolderRecordFile.ReadAsync(Path.Combine(factory.ProfilesRoot, "b@example.com"), TestContext.Current.CancellationToken);
        incoming!.Side.ShouldBe(SideName.Windows);
        incoming.Fingerprint.ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        File.Exists(Path.Combine(factory.ProfilesRoot, "a@example.com", HolderRecordFile.FileName)).ShouldBeFalse();
    }
}
