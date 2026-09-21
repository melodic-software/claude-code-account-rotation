using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

/// <summary>
/// The login flow end to end over the real runner and a scripted CLI: the URL
/// the browser is handed, a rejected code, a retry, the ten-minute expiry, the
/// profile rewrite, and what never leaves the process.
/// </summary>
public sealed class LoginEndpointTests
{
    private const string LiveEmail = "live@example.com";
    private const string ParkedEmail = "parked@example.com";
    private const string StaleEmail = "someone-else@example.com";
    private const string Code = "123456-secret-one-time-code";

    private static readonly Uri _accounts = new("/api/accounts", UriKind.Relative);

    private static Uri Account(string email, string suffix = "") =>
        new("/api/accounts/" + Uri.EscapeDataString(email) + suffix, UriKind.Relative);

    private static Uri CodePath(string id) => new("/api/login-sessions/" + id + "/code", UriKind.Relative);

    private static Uri SessionPath(string id) => new("/api/login-sessions/" + id, UriKind.Relative);

    private static string FolderOf(AppFactory factory, string email) =>
        Path.Combine(factory.ProfilesRoot, ProfileFolderName.FromEmail(AccountEmail.Parse(email).Value));

    /// <summary>A machine on one account, with a second account added to the roster and mapped to a browser profile.</summary>
    private static async Task<AppFactory> RosteredAsync(CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        factory.Cli.Email = LiveEmail;
        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage added = await client.PostAsJsonAsync(
            _accounts,
            new { email = ParkedEmail, browser = "brave", browserProfileDirectory = "Profile 3" },
            cancellationToken);
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        return factory;
    }

    private static async Task<JsonObject> StartLoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.PostAsync(Account(ParkedEmail, "/login"), content: null, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
    }

    private static async Task<JsonObject> SubmitCodeAsync(HttpClient client, string id, string code, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(CodePath(id), new { code }, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
    }

    [Fact]
    public async Task TheBrowserIsHandedTheAuthorizeUrlWithNoEscapeBytesAndTheEmailPreFilled()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        JsonObject session = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        factory.Browser.Launched.Count.ShouldBe(1);
        (BrowserFamily browser, string? profile, Uri url) = factory.Browser.Launched[0];
        browser.ShouldBe(BrowserFamily.Brave);
        profile.ShouldBe("Profile 3");
        // The CLI printed that URL inside OSC 8 hyperlink escapes. Not one of those
        // control bytes may reach the launcher, and the query must survive intact.
        url.AbsoluteUri.Any(char.IsControl).ShouldBeFalse();
        url.AbsoluteUri.ShouldBe(factory.LoginChild.Last.SignInUrl);
        url.AbsolutePath.ShouldEndWith("/oauth/authorize");
        // --email is what puts login_hint on the URL, and login_hint is what pre-fills
        // the address in the mapped browser profile.
        url.Query.ShouldContain("login_hint=" + Uri.EscapeDataString(ParkedEmail));
        session["signInUrl"]!.GetValue<string>().ShouldBe(factory.LoginChild.Last.SignInUrl);
        session["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Pending));
    }

    [Fact]
    public async Task TheLoginRunsUnderTheAccountsOwnFolderAndNoOtherConfigRoot()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        await StartLoginAsync(client, TestContext.Current.CancellationToken);

        ScriptedLoginChild child = factory.LoginChild.Last;
        child.ConfigDirectory.ShouldBe(FolderOf(factory, ParkedEmail));
        child.ConfigDirectory.ShouldNotBe(factory.LiveDirectory);
        child.Arguments.ShouldBe(["auth", "login", "--email", ParkedEmail]);
    }

    [Fact]
    public async Task ARejectedCodeLeavesTheSessionOpenForARetryWithinItsExpiry()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);
        string id = started["id"]!.GetValue<string>();

        JsonObject rejected = await SubmitCodeAsync(client, id, "000000-wrong", TestContext.Current.CancellationToken);

        // The CLI keeps the prompt open after a bad code, so the session must stay
        // pending: not failed, and not hung waiting for a process that never exits.
        rejected["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Pending));
        rejected["message"]!.GetValue<string>().ShouldContain("rejected");
        factory.LoginChild.Last.Killed.ShouldBeFalse();

        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        factory.LoginChild.OnCode = LoginChildScript.CompleteLoginAsync;
        JsonObject accepted = await SubmitCodeAsync(client, id, Code, TestContext.Current.CancellationToken);

        accepted["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Completed));
        factory.LoginChild.Last.CodesWritten.ShouldBe(["000000-wrong", Code]);
    }

    [Fact]
    public async Task ASessionExpiresAtTenMinutesAndTheChildIsKilled()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);
        string id = started["id"]!.GetValue<string>();

        factory.Clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        JsonObject before = (await client.GetFromJsonAsync<JsonObject>(SessionPath(id), TestContext.Current.CancellationToken))!;
        before["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Pending));
        factory.LoginChild.Last.Killed.ShouldBeFalse();

        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        JsonObject after = (await client.GetFromJsonAsync<JsonObject>(SessionPath(id), TestContext.Current.CancellationToken))!;

        after["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Expired));
        factory.LoginChild.Last.Killed.ShouldBeTrue();
        using HttpResponseMessage late = await client.PostAsJsonAsync(CodePath(id), new { code = Code }, TestContext.Current.CancellationToken);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CompletionRewritesTheProfileFromTheFreshLoginAndPrunesTheResidue()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        // The folder already names another account, the way a folder logged in once
        // before does. That block must not survive the new login.
        string folder = FolderOf(factory, ParkedEmail);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "profile.json"),
            AppFactory.AccountJson(StaleEmail).ToJsonString(),
            TestContext.Current.CancellationToken);
        factory.LoginChild.OnCode = LoginChildScript.CompleteLoginAsync;
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject completed = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        completed["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Completed));
        JsonObject profile = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(folder, "profile.json"), TestContext.Current.CancellationToken))!.AsObject();
        profile["emailAddress"]!.GetValue<string>().ShouldBe(ParkedEmail);
        File.Exists(Path.Combine(folder, CredentialFiles.FileName)).ShouldBeTrue();
        // Pruned after the rewrite, never before: the state file is where the fresh
        // block came from, so a prune that ran first would have left the stale one.
        File.Exists(Path.Combine(folder, ".claude.json")).ShouldBeFalse();
        Directory.Exists(Path.Combine(folder, "backups")).ShouldBeFalse();
    }

    /// <summary>
    /// What the operator is told when the browser step signed them in as a seat
    /// that is never rotated. It has to name the subscription the CLI reported:
    /// one address can carry both an Enterprise seat and a personal Max account,
    /// and the only way to know which one was picked is to be told.
    /// </summary>
    private static string RefusedMessage(string subscriptionType) =>
        "That login was refused: that account reports a \"" + subscriptionType
        + "\" subscription; only Max accounts are rotated, and a Team or Enterprise seat never is."
        + " The credentials it wrote were revoked with `claude auth logout` and deleted, so nothing joined the rotation."
        + " Log in again and pick the Max account at the sign-in step.";

    [Theory]
    [InlineData("enterprise")]
    [InlineData("team")]
    public async Task ALoginThatCompletesAsATeamOrEnterpriseSeatIsRevokedAndLeavesNothingToSwitchTo(string subscriptionType)
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        // One address, two accounts behind it: the browser offers both, and this is
        // the misclick. The seat must not reach the rotation on the way back.
        factory.Cli.SubscriptionType = subscriptionType;
        factory.LoginChild.OnCode = LoginChildScript.CompleteLoginAsync;
        string folder = FolderOf(factory, ParkedEmail);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject refused = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        refused["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        refused["message"]!.GetValue<string>().ShouldBe(RefusedMessage(subscriptionType));
        // Revoked at the source, under that folder and no other, then the pair the
        // login wrote is gone: a switch admits any folder holding one.
        factory.Cli.LogoutCalls.ShouldBe([folder]);
        File.Exists(Path.Combine(folder, CredentialFiles.FileName)).ShouldBeFalse();
        using HttpResponseMessage switched = await client.PostAsync(Account(ParkedEmail, "/switch"), content: null, TestContext.Current.CancellationToken);
        switched.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await switched.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!
            .GetValue<string>().ShouldBe(nameof(SwitchRefusal.TargetHasNoCredentials));
        // The roster entry stays, so the operator can log in again and pick the
        // other account behind the same address.
        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken))!;
        dashboard.ToJsonString().ShouldContain(ParkedEmail);
    }

    [Fact]
    public async Task ARefusedLoginWhoseRevocationFailsStillLeavesNothingToSwitchTo()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "enterprise";
        factory.Cli.LogoutError = "claude auth logout exited with code 1";
        factory.LoginChild.OnCode = LoginChildScript.CompleteLoginAsync;
        string folder = FolderOf(factory, ParkedEmail);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject refused = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        // A switchable Enterprise seat is worse than an unrevoked token, so the
        // pair goes either way and the message says the token was not revoked.
        refused["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        refused["message"]!.GetValue<string>().ShouldContain("\"enterprise\" subscription");
        refused["message"]!.GetValue<string>().ShouldContain("could not be revoked");
        File.Exists(Path.Combine(folder, CredentialFiles.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task AnUnreadableTierAfterALoginFailsClosedRatherThanAdmitting()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        factory.Cli.ReadError = "claude auth status printed no JSON object";
        factory.LoginChild.OnCode = LoginChildScript.CompleteLoginAsync;
        string folder = FolderOf(factory, ParkedEmail);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject refused = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        refused["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        refused["message"]!.GetValue<string>().ShouldContain("did not report what kind of account signed in");
        factory.Cli.LogoutCalls.ShouldBe([folder]);
        File.Exists(Path.Combine(folder, CredentialFiles.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task ALoginThatCompletesAsMaxIsAdmittedAndNothingIsRevoked()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        factory.LoginChild.OnCode = LoginChildScript.CompleteLoginAsync;
        string folder = FolderOf(factory, ParkedEmail);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject completed = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        completed["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Completed));
        completed["message"]!.GetValue<string>().ShouldBe("Logged in as " + ParkedEmail + ".");
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        File.Exists(Path.Combine(folder, CredentialFiles.FileName)).ShouldBeTrue();
    }

    /// <summary>
    /// A recorded identity with the Max rate-limit tier, the way a folder that
    /// once held a Max login carries one. Nothing in the folder proves which
    /// login wrote it, so it must never vouch for the seat that just signed in.
    /// </summary>
    private static JsonObject StaleMaxIdentity()
    {
        JsonObject identity = AppFactory.AccountJson(ParkedEmail);
        identity["organizationRateLimitTier"] = "default_claude_max";
        return identity;
    }

    private static async Task<byte[]> SeedPairAsync(string folder, string refreshToken, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        await CredentialFiles.WriteAsync(folder, refreshToken, cancellationToken);
        return await File.ReadAllBytesAsync(Path.Combine(folder, CredentialFiles.FileName), cancellationToken);
    }

    [Fact]
    public async Task AStaleProfileCannotVouchForASeatWhoseAdoptionFailed()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = FolderOf(factory, ParkedEmail);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "profile.json"), StaleMaxIdentity().ToJsonString(), TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "enterprise";
        factory.LoginChild.OnCode = async (child, code) =>
        {
            // The pair lands, but the state file names no account, so adoption has
            // nothing to rewrite the profile from and the stale one stays standing.
            await CredentialFiles.WriteAsync(child.ConfigDirectory, "refresh-" + child.Email, CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(child.ConfigDirectory, ".claude.json"),
                new JsonObject { ["numStartups"] = 1 }.ToJsonString(),
                CancellationToken.None);
            child.Exit();
        };
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject refused = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        refused["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        refused["message"]!.GetValue<string>().ShouldBe(RefusedMessage("enterprise"));
        factory.Cli.LogoutCalls.ShouldBe([folder]);
        File.Exists(Path.Combine(folder, CredentialFiles.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task AStaleStateFileCannotVouchForTheSeatThatSignedIn()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = FolderOf(factory, ParkedEmail);
        Directory.CreateDirectory(folder);
        // What a residue-kept login leaves behind: a state file naming the earlier
        // account, with its Max tier. Adoption reads it as if it were fresh.
        await File.WriteAllTextAsync(
            Path.Combine(folder, ".claude.json"),
            new JsonObject { ["numStartups"] = 1, ["oauthAccount"] = StaleMaxIdentity() }.ToJsonString(),
            TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "enterprise";
        factory.LoginChild.OnCode = async (child, code) =>
        {
            await CredentialFiles.WriteAsync(child.ConfigDirectory, "refresh-" + child.Email, CancellationToken.None);
            child.Exit();
        };
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject refused = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        refused["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        refused["message"]!.GetValue<string>().ShouldBe(RefusedMessage("enterprise"));
        factory.Cli.LogoutCalls.ShouldBe([folder]);
        File.Exists(Path.Combine(folder, CredentialFiles.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task AnAbandonedReLoginLeavesTheWorkingPairUntouched()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = FolderOf(factory, ParkedEmail);
        byte[] before = await SeedPairAsync(folder, "refresh-old", TestContext.Current.CancellationToken);
        // The CLI ends without writing, and then cannot even say what the folder
        // holds. The pair it never touched is the operator's working login.
        factory.Cli.ReadError = "claude auth status printed no JSON object";
        factory.LoginChild.OnCode = static (child, code) =>
        {
            child.Exit();
            return Task.CompletedTask;
        };
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject ended = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        ended["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        ended["message"]!.GetValue<string>().ShouldContain("left as it was");
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        (await File.ReadAllBytesAsync(Path.Combine(folder, CredentialFiles.FileName), TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task AReLoginThatExpiresLeavesTheWorkingPairUntouched()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = FolderOf(factory, ParkedEmail);
        byte[] before = await SeedPairAsync(folder, "refresh-old", TestContext.Current.CancellationToken);
        factory.Cli.ReadError = "claude auth status printed no JSON object";
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);
        string id = started["id"]!.GetValue<string>();

        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        JsonObject expired = (await client.GetFromJsonAsync<JsonObject>(SessionPath(id), TestContext.Current.CancellationToken))!;
        // Expiry settles the session in the request; the folder is finished on the
        // pump afterwards, and that finish is what must leave the pair alone.
        var runner = (ClaudeCliLoginSessionRunner)factory.Services.GetRequiredService<ILoginSessionRunner>();
        await runner.FinishedAsync(new LoginSessionId(id)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        JsonObject after = (await client.GetFromJsonAsync<JsonObject>(SessionPath(id), TestContext.Current.CancellationToken))!;

        expired["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Expired));
        after["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Expired));
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        (await File.ReadAllBytesAsync(Path.Combine(folder, CredentialFiles.FileName), TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task AReLoginWhoseChildOutlivesTheExpiryStillLeavesTheWorkingPairUntouched()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = FolderOf(factory, ParkedEmail);
        byte[] before = await SeedPairAsync(folder, "refresh-old", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);
        string id = started["id"]!.GetValue<string>();

        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        factory.LoginChild.Last.Exit();
        // Nothing polled the session, so the pump's own finish is what records the expiry here.
        var runner = (ClaudeCliLoginSessionRunner)factory.Services.GetRequiredService<ILoginSessionRunner>();
        await runner.FinishedAsync(new LoginSessionId(id)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        JsonObject after = (await client.GetFromJsonAsync<JsonObject>(SessionPath(id), TestContext.Current.CancellationToken))!;

        after["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Expired));
        after["message"]!.GetValue<string>().ShouldContain("left as it was");
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        (await File.ReadAllBytesAsync(Path.Combine(folder, CredentialFiles.FileName), TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task AwaitingTheFinishOfAnUnknownSessionThrows()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        var runner = (ClaudeCliLoginSessionRunner)factory.Services.GetRequiredService<ILoginSessionRunner>();

        // A finish that completed for an id the runner never had would let a test
        // assert on a folder nothing had finished writing.
        await Should.ThrowAsync<ArgumentException>(() => runner.FinishedAsync(LoginSessionId.New()));
    }

    [Fact]
    public async Task AnUnjudgeableRewriteOfAnExistingPairIsKeptAndNamed()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = FolderOf(factory, ParkedEmail);
        await SeedPairAsync(folder, "refresh-old", TestContext.Current.CancellationToken);
        // The file changed under the login, and the CLI cannot say what signed in.
        // The folder held an admitted login before this one, so nothing proves the
        // pair is the new login's to delete: keep it and say what to do.
        factory.Cli.ReadError = "claude auth status printed no JSON object";
        factory.LoginChild.OnCode = async (child, code) =>
        {
            await CredentialFiles.WriteAsync(child.ConfigDirectory, "refresh-new", CancellationToken.None);
            child.Exit();
        };
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject kept = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        kept["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        kept["message"]!.GetValue<string>().ShouldContain("could not be checked");
        kept["message"]!.GetValue<string>().ShouldContain("Remove the account from the roster");
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), TestContext.Current.CancellationToken)).ShouldContain("refresh-new");
    }

    [Fact]
    public async Task AnUnjudgeableRewriteOfAnExistingPairRewritesNoProfileAndPrunesNothing()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = FolderOf(factory, ParkedEmail);
        await SeedPairAsync(folder, "refresh-old", TestContext.Current.CancellationToken);
        string profilePath = Path.Combine(folder, "profile.json");
        await File.WriteAllTextAsync(profilePath, StaleMaxIdentity().ToJsonString(), TestContext.Current.CancellationToken);
        byte[] profileBefore = await File.ReadAllBytesAsync(profilePath, TestContext.Current.CancellationToken);
        factory.Cli.ReadError = "claude auth status printed no JSON object";
        factory.LoginChild.OnCode = async (child, code) =>
        {
            await File.WriteAllTextAsync(
                Path.Combine(child.ConfigDirectory, ".claude.json"),
                new JsonObject { ["numStartups"] = 1, ["oauthAccount"] = AppFactory.AccountJson(child.Email) }.ToJsonString(),
                CancellationToken.None);
            await CredentialFiles.WriteAsync(child.ConfigDirectory, "refresh-new", CancellationToken.None);
            child.Exit();
        };
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject kept = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        // A folder kept because nothing could judge it is kept whole: the profile the
        // earlier login wrote still stands, and the state file beside it is still there.
        kept["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        (await File.ReadAllBytesAsync(profilePath, TestContext.Current.CancellationToken)).ShouldBe(profileBefore);
        File.Exists(Path.Combine(folder, ".claude.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task AFaultWhileJudgingLeavesThePairInPlaceAndSaysSo()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = FolderOf(factory, ParkedEmail);
        await SeedPairAsync(folder, "refresh-old", TestContext.Current.CancellationToken);
        // The judgment faults rather than answering either way. A fault escaping the
        // pump would leave a finished login reading "still waiting" until its expiry,
        // and the fault's own text, which can name a path, may not reach the page.
        factory.Cli.ReadFault = new InvalidOperationException("the adapter threw");
        factory.LoginChild.OnCode = async (child, code) =>
        {
            await CredentialFiles.WriteAsync(child.ConfigDirectory, "refresh-new", CancellationToken.None);
            child.Exit();
        };
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject faulted = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        faulted["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Failed));
        faulted["message"]!.GetValue<string>().ShouldContain("failed");
        faulted["message"]!.GetValue<string>().ShouldContain("Remove the account from the roster");
        faulted["message"]!.GetValue<string>().ShouldNotContain("the adapter threw");
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), TestContext.Current.CancellationToken)).ShouldContain("refresh-new");
    }

    [Fact]
    public async Task AReLoginThatCompletesAsMaxReplacesTheOldPair()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        string folder = FolderOf(factory, ParkedEmail);
        await SeedPairAsync(folder, "refresh-old", TestContext.Current.CancellationToken);
        factory.LoginChild.OnCode = LoginChildScript.CompleteLoginAsync;
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject completed = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        completed["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Completed));
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), TestContext.Current.CancellationToken)).ShouldContain("refresh-" + ParkedEmail);
    }

    [Fact]
    public async Task AFolderThatCannotBeTidiedStillCompletesRatherThanHanging()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        List<FileStream> held = [];
        factory.LoginChild.OnCode = async (child, code) =>
        {
            // The state file first, then the exclusive handle on it, and only then the
            // pair and the exit that lets the runner finish: something else is holding
            // that file open, the way a scanner holds one written a second ago. The
            // pair is on disk, so the login worked, but the tidy-up cannot run, and
            // that must never strand the operator.
            JsonObject state = new() { ["numStartups"] = 1, ["oauthAccount"] = AppFactory.AccountJson(child.Email) };
            string statePath = Path.Combine(child.ConfigDirectory, ".claude.json");
            Directory.CreateDirectory(child.ConfigDirectory);
            await File.WriteAllTextAsync(statePath, state.ToJsonString(), TestContext.Current.CancellationToken);
            held.Add(new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.None));
            await CredentialFiles.WriteAsync(child.ConfigDirectory, "refresh-" + child.Email, TestContext.Current.CancellationToken);
            child.Exit();
        };
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        JsonObject completed = await SubmitCodeAsync(client, started["id"]!.GetValue<string>(), Code, TestContext.Current.CancellationToken);

        try
        {
            completed["state"]!.GetValue<string>().ShouldBe(nameof(LoginSessionState.Completed));
            completed["message"]!.GetValue<string>().ShouldContain("left exactly as it is");
        }
        finally
        {
            foreach (FileStream stream in held)
            {
                await stream.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task TheCodeReachesStandardInputAndNoArgumentResponseLogLineOrFile()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);
        string id = started["id"]!.GetValue<string>();

        // The scripted CLI echoes the code back in its rejection, which is the worst
        // a real one could do. None of that may reach the page, a log, or a file.
        JsonObject rejected = await SubmitCodeAsync(client, id, Code, TestContext.Current.CancellationToken);
        JsonObject status = (await client.GetFromJsonAsync<JsonObject>(SessionPath(id), TestContext.Current.CancellationToken))!;

        factory.LoginChild.Last.CodesWritten.ShouldBe([Code]);
        string.Join(" ", factory.LoginChild.Last.Arguments).ShouldNotContain(Code);
        rejected.ToJsonString().ShouldNotContain(Code);
        status.ToJsonString().ShouldNotContain(Code);
        factory.Logs.Lines.ShouldNotContain(line => line.Contains(Code, StringComparison.Ordinal));
        // Nothing under the profiles root, where a login writes, may hold it either.
        Directory.EnumerateFiles(factory.ProfilesRoot, "*", SearchOption.AllDirectories)
            .ShouldNotContain(path => File.ReadAllText(path).Contains(Code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACodeCarryingALineBreakIsRefusedBeforeItReachesTheChild()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        JsonObject started = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            CodePath(started["id"]!.GetValue<string>()),
            new { code = "123456\ny" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        factory.LoginChild.Last.CodesWritten.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASecondLoginForTheSameFolderIsRefusedWhileTheFirstIsRunning()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await StartLoginAsync(client, TestContext.Current.CancellationToken);

        using HttpResponseMessage second = await client.PostAsync(Account(ParkedEmail, "/login"), content: null, TestContext.Current.CancellationToken);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        factory.LoginChild.Children.Count.ShouldBe(1);
    }

    [Fact]
    public async Task LoginForTheLiveAccountIsRefusedSoOneAccountNeverHoldsTwoLogins()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsync(Account(LiveEmail, "/adopt-live"), content: null, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.PostAsync(Account(LiveEmail, "/login"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!
            .GetValue<string>().ShouldBe("AccountIsLive");
        factory.LoginChild.Children.ShouldBeEmpty();
    }

    [Fact]
    public async Task LoginForAnAccountThatIsNotOnTheRosterIsRefused()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(Account(StaleEmail, "/login"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!
            .GetValue<string>().ShouldBe("NotOnRoster");
    }

    [Fact]
    public async Task ACliThatPrintsNoSignInUrlFailsTheLoginRatherThanLeavingItRunning()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        factory.LoginChild.PrintsUrl = false;
        using HttpClient client = factory.CreateMutatingClient();

        // The child ends without a URL, which is what the runner waits on.
        Task<HttpResponseMessage> pending = client.PostAsync(Account(ParkedEmail, "/login"), content: null, TestContext.Current.CancellationToken);
        while (factory.LoginChild.Children.Count == 0)
        {
            await Task.Yield();
        }

        factory.LoginChild.Last.Exit();
        using HttpResponseMessage response = await pending;

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!
            .GetValue<string>().ShouldBe("LoginCouldNotStart");
        factory.LoginChild.Last.Killed.ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnopenableBrowserStillReturnsTheUrlSoTheOperatorCanOpenItByHand()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        factory.Browser.Error = "no brave installation was found";
        using HttpClient client = factory.CreateMutatingClient();

        JsonObject session = await StartLoginAsync(client, TestContext.Current.CancellationToken);

        session["browserError"]!.GetValue<string>().ShouldContain("brave");
        session["signInUrl"]!.GetValue<string>().ShouldStartWith(LoginChildScript.AuthorizeBase);
    }

    [Fact]
    public async Task ALoginWithoutTheCustomHeaderIsForbidden()
    {
        await using AppFactory factory = await RosteredAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(Account(ParkedEmail, "/login"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.LoginChild.Children.ShouldBeEmpty();
    }

    [Fact]
    public void TheAuthorizeUrlIsReadOutOfAnOsc8Hyperlink() =>
        HyperlinkedUrlIsExtracted("https://claude.com/cai/oauth/authorize?code=true&login_hint=a%40example.com");

    [Fact]
    public void TheAuthorizePathIsWhatIsAnchoredOnNotTheHostOrThePrompt() =>
        HyperlinkedUrlIsExtracted("https://claude.ai/oauth/authorize?code=true");

    /// <summary>
    /// The URL as the spike saw it: framed by OSC 8 escapes, with another URL on
    /// the line before it that is not the one wanted.
    /// </summary>
    private static void HyperlinkedUrlIsExtracted(string printed)
    {
        const char escape = (char)0x1b;
        const char bell = (char)0x07;
        string line = "Visit https://example.com/docs first.\r\n"
            + escape + "]8;;" + printed + bell + printed + escape + "]8;;" + bell + "\r\nPaste code here if prompted > ";

        Uri? found = ClaudeCliLoginSessionRunner.ExtractAuthorizeUrl(line);

        found.ShouldNotBeNull();
        found.AbsoluteUri.ShouldBe(printed);
    }

    [Fact]
    public void TextWithNoAuthorizeUrlYieldsNothing()
    {
        ClaudeCliLoginSessionRunner.ExtractAuthorizeUrl("Opening https://claude.com/settings in your browser.\r\n").ShouldBeNull();
    }
}
