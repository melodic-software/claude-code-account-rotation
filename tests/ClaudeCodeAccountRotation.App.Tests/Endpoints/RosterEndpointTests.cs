using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeCodeAccountRotation.App.Tests.Endpoints;

public sealed class RosterEndpointTests
{
    private const string LiveEmail = "live@example.com";
    private const string NewEmail = "new@example.com";
    private const string ParkedEmail = "parked@example.com";

    private static readonly Uri _accounts = new("/api/accounts", UriKind.Relative);

    private static Uri Account(string email, string suffix = "") =>
        new("/api/accounts/" + Uri.EscapeDataString(email) + suffix, UriKind.Relative);

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    private static string FolderOf(AppFactory factory, string email) =>
        Path.Combine(factory.ProfilesRoot, ProfileFolderName.FromEmail(Email(email)));

    private static async Task<AppFactory> LiveOnAsync(CancellationToken cancellationToken)
    {
        AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", cancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, cancellationToken);
        factory.Cli.Email = LiveEmail;
        return factory;
    }

    private static async Task<Roster> StoredRosterAsync(AppFactory factory, CancellationToken cancellationToken)
    {
        using RosterFile file = new(factory.AppData);
        return await file.ReadAsync(cancellationToken);
    }

    /// <summary>
    /// Strands a rotated pair for a folder, the state a write-back that could not
    /// land leaves behind. Written after the host has started, because startup's
    /// own sweep would otherwise apply it before the request under test.
    /// </summary>
    private static async Task StrandAsync(
        AppFactory factory,
        string folder,
        string parkedRefreshToken,
        string rotatedRefreshToken,
        CancellationToken cancellationToken) =>
        (await factory.Services.GetRequiredService<RecoveryFiles>().WriteAsync(
            folder,
            RefreshTokenFingerprint.FromRefreshToken(parkedRefreshToken),
            CredentialFiles.Pair(rotatedRefreshToken),
            cancellationToken)).ShouldBeTrue();

    private static async Task<JsonObject?> CardAsync(HttpClient client, string email, CancellationToken cancellationToken)
    {
        JsonObject dashboard = (await client.GetFromJsonAsync<JsonObject>(new Uri("/api/dashboard", UriKind.Relative), cancellationToken))!;
        return dashboard["accounts"]!.AsArray()
            .FirstOrDefault(card => card!["email"]!.GetValue<string>() == email) as JsonObject;
    }

    [Fact]
    public async Task AddCreatesTheProfileFolderAndPutsTheAccountOnTheRoster()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            _accounts,
            new { email = NewEmail, alias = "weekly", browser = "brave", browserProfileDirectory = "Profile 3" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.Exists(FolderOf(factory, NewEmail)).ShouldBeTrue();
        RosterEntry? stored = (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(NewEmail));
        stored.ShouldNotBeNull();
        stored.Alias.ShouldBe("weekly");
        stored.Browser.ShouldBe(BrowserFamily.Brave);
        stored.BrowserProfileDirectory.ShouldBe("Profile 3");

        JsonObject card = (await CardAsync(client, NewEmail, TestContext.Current.CancellationToken))!;
        card["hasCredentials"]!.GetValue<bool>().ShouldBeFalse();
        card["roster"]!["browserProfileDirectory"]!.GetValue<string>().ShouldBe("Profile 3");
    }

    [Fact]
    public async Task TheCardAddHandsBackNamesBothLoginInstantsAndKnowsNeither()
    {
        // Add reads no credential file and no profile, so the card it returns has
        // nothing to date the login by. The fields are still on the wire, null, so
        // the page renders the new card with the same shape a polled one has.
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            _accounts,
            new { email = NewEmail },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject card = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        card.ContainsKey("loginExpiresAt").ShouldBeTrue();
        card["loginExpiresAt"].ShouldBeNull();
        card.ContainsKey("loggedInAt").ShouldBeTrue();
        card["loggedInAt"].ShouldBeNull();
    }

    [Fact]
    public async Task AddingTheSameAccountTwiceIsRefused()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);

        using HttpResponseMessage second = await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("team")]
    [InlineData("enterprise")]
    public async Task AddRefusesANonMaxAccountWithTheReason(string subscriptionType)
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(ParkedEmail, "refresh-parked", TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = subscriptionType;
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(_accounts, new { email = ParkedEmail }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe("NotAMaxAccount");
        body["message"]!.GetValue<string>().ShouldContain(subscriptionType);
        (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task PauseRoundTripsThroughTheRosterFileAndOntoTheCard()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(ParkedEmail, "refresh-parked", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = ParkedEmail }, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(Account(ParkedEmail), new { paused = true }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(ParkedEmail))!.Paused.ShouldBeTrue();
        JsonObject card = (await CardAsync(client, ParkedEmail, TestContext.Current.CancellationToken))!;
        card["roster"]!["paused"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Theory]
    // The directory becomes "--profile-directory=<value>", which the browser
    // resolves under its User Data directory: a separator or a dot-segment
    // would point the login at a profile the roster never named. One segment,
    // exactly as the browser wrote it, is the only shape that goes through.
    [InlineData("../../Other")]
    [InlineData("Profile 3/..")]
    [InlineData("Profile\\3")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(" Profile 3")]
    [InlineData("Profile 3 ")]
    // Windows resolves each of these to something other than the literal name:
    // a stripped trailing dot, an NTFS stream, a device, a control character.
    [InlineData("Profile 3.")]
    [InlineData("Default::$INDEX_ALLOCATION")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM¹")]
    [InlineData("LPT².txt")]
    [InlineData("Profile\t3")]
    public async Task AddRefusesADirectoryThatIsNotASinglePathSegment(string directory)
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            _accounts,
            new { email = NewEmail, browser = "edge", browserProfileDirectory = directory },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["error"]!.GetValue<string>().ShouldContain("directory");
        (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Entries.ShouldBeEmpty();
        Directory.Exists(FolderOf(factory, NewEmail)).ShouldBeFalse();
    }

    [Fact]
    public async Task PatchRefusesADirectoryThatIsNotASinglePathSegmentAndKeepsTheOldOne()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail, browser = "edge", browserProfileDirectory = "Profile 3" }, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(Account(NewEmail), new { browserProfileDirectory = "..\\Profile 4" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        RosterEntry stored = (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(NewEmail))!;
        stored.BrowserProfileDirectory.ShouldBe("Profile 3");
    }

    [Fact]
    public async Task PatchLeavesTheFieldsItDoesNotMention()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail, alias = "weekly", browser = "chrome" }, TestContext.Current.CancellationToken);

        await client.PatchAsJsonAsync(Account(NewEmail), new { paused = true }, TestContext.Current.CancellationToken);

        RosterEntry stored = (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(NewEmail))!;
        stored.Alias.ShouldBe("weekly");
        stored.Browser.ShouldBe(BrowserFamily.Chrome);
    }

    [Fact]
    public async Task PatchStoresNotesAndClearsAPresentAliasWithoutChangingTheOther()
    {
        // The heading's rename sends alias and nothing else, and the Edit panel
        // sends notes even when the operator blanks the warning. A present key
        // clears; a missing key leaves the field alone.
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(
            _accounts,
            new { email = NewEmail, alias = "weekly", notes = "not the other seat" },
            TestContext.Current.CancellationToken);

        using HttpResponseMessage renamed = await client.PatchAsJsonAsync(
            Account(NewEmail),
            new { alias = "weekly seat" },
            TestContext.Current.CancellationToken);

        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        RosterEntry stored = (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(NewEmail))!;
        stored.Alias.ShouldBe("weekly seat");
        stored.Notes.ShouldBe("not the other seat");
        JsonObject card = (await CardAsync(client, NewEmail, TestContext.Current.CancellationToken))!;
        card["roster"]!["notes"]!.GetValue<string>().ShouldBe("not the other seat");

        using HttpResponseMessage cleared = await client.PatchAsJsonAsync(
            Account(NewEmail),
            new { alias = (string?)null },
            TestContext.Current.CancellationToken);

        cleared.StatusCode.ShouldBe(HttpStatusCode.OK);
        stored = (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(NewEmail))!;
        stored.Alias.ShouldBeNull();
        stored.Notes.ShouldBe("not the other seat");

        using HttpResponseMessage clearedNotes = await client.PatchAsJsonAsync(
            Account(NewEmail),
            new { notes = "   " },
            TestContext.Current.CancellationToken);

        clearedNotes.StatusCode.ShouldBe(HttpStatusCode.OK);
        stored = (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(NewEmail))!;
        stored.Notes.ShouldBeNull();
        stored.Alias.ShouldBeNull();
    }

    [Fact]
    public async Task PatchMarksTheCiTokenHolderAndClearsAnyOtherHolder()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync(_accounts, new { email = ParkedEmail }, TestContext.Current.CancellationToken);
        await client.PatchAsJsonAsync(Account(NewEmail), new { ciTokenGeneratedOn = "2026-01-15" }, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            Account(ParkedEmail),
            new { ciTokenGeneratedOn = "2026-06-01" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Roster stored = await StoredRosterAsync(factory, TestContext.Current.CancellationToken);
        stored.Find(Email(ParkedEmail))!.CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 6, 1));
        stored.Find(Email(NewEmail))!.CiTokenGeneratedOn.ShouldBeNull();
        JsonObject card = (await CardAsync(client, ParkedEmail, TestContext.Current.CancellationToken))!;
        card["roster"]!["ciTokenGeneratedOn"]!.GetValue<string>().ShouldBe("2026-06-01");
    }

    [Fact]
    public async Task PatchRefusesAnUnparsableCiTokenDate()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.PatchAsJsonAsync(
            Account(NewEmail),
            new { ciTokenGeneratedOn = "not a date" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(NewEmail))!.CiTokenGeneratedOn.ShouldBeNull();
    }

    [Fact]
    public async Task PatchOnAnAccountThatIsNotOnTheRosterIsNotFound()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PatchAsJsonAsync(Account(NewEmail), new { paused = true }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveRevokesTheLoginAndDeletesTheFolder()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(ParkedEmail, "refresh-parked", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = ParkedEmail }, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.DeleteAsync(Account(ParkedEmail), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.Exists(folder).ShouldBeFalse();
        (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(ParkedEmail)).ShouldBeNull();
        factory.Cli.LogoutCalls.ShouldBe([folder]);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["loggedOut"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task AFailedLogoutKeepsTheFolderRatherThanStrandingALiveToken()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(ParkedEmail, "refresh-parked", TestContext.Current.CancellationToken);
        factory.Cli.LogoutError = "the CLI could not reach the token endpoint";
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.DeleteAsync(Account(ParkedEmail), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Directory.Exists(folder).ShouldBeTrue();
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("LogoutFailed");
    }

    [Fact]
    public async Task ARemovalPutsAStrandedPairBackBeforeRevokingIt()
    {
        // A stranded folder holds the pair a rotation replaced; the working
        // lineage is in the recovery directory. Revoking what the folder holds
        // would kill the dead pair and leave the live one valid for the rest of
        // its login, in a file belonging to an account the roster no longer names
        // and no card ever shows again. The logout here fails on purpose, because
        // a successful one deletes the folder and takes the evidence with it.
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(ParkedEmail, "refresh-parked", TestContext.Current.CancellationToken);
        factory.Cli.LogoutError = "the CLI could not reach the token endpoint";
        using HttpClient client = factory.CreateMutatingClient();
        await StrandAsync(factory, folder, "refresh-parked", "refresh-rotated", TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.DeleteAsync(Account(ParkedEmail), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        // The restore ran first, so what the logout was pointed at is the lineage
        // the account actually has.
        factory.Cli.LogoutCalls.ShouldBe([folder]);
        (await CredentialFiles.FingerprintAsync(folder, TestContext.Current.CancellationToken))
            .ShouldBe(RefreshTokenFingerprint.FromRefreshToken("refresh-rotated"));
        factory.Services.GetRequiredService<RecoveryFiles>().HasRecoveryFor(folder).ShouldBeFalse();
    }

    [Fact]
    public async Task ARemovalIsRefusedWhileARecoveryFileCannotBeApplied()
    {
        // The restore cannot run: the folder has no pair left for the
        // compare-and-swap to replace. Deleting the folder now would leave the
        // recovery file holding a live refresh token for an account nothing on
        // this machine names any more, so the removal is refused and says what to
        // resolve first.
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(ParkedEmail, "refresh-parked", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await StrandAsync(factory, folder, "refresh-parked", "refresh-rotated", TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(folder, CredentialFiles.FileName));

        using HttpResponseMessage response = await client.DeleteAsync(Account(ParkedEmail), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe("StrandedInRecovery");
        body["message"]!.GetValue<string>().ShouldContain("recovery");
        Directory.Exists(folder).ShouldBeTrue();
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        factory.Services.GetRequiredService<RecoveryFiles>().HasRecoveryFor(folder).ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveWithLogoutFalseDeletesTheFolderAndSaysTheTokenWasNotRevoked()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(ParkedEmail, "refresh-parked", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.DeleteAsync(Account(ParkedEmail, "?logout=false"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.Exists(folder).ShouldBeFalse();
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["loggedOut"]!.GetValue<bool>().ShouldBeFalse();
        body["warning"]!.GetValue<string>().ShouldContain("stays valid");
    }

    [Fact]
    public async Task RemovingAnAccountThatHasNeverLoggedInRunsNoLogout()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.DeleteAsync(Account(NewEmail), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.Exists(FolderOf(factory, NewEmail)).ShouldBeFalse();
        factory.Cli.LogoutCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemovingTheLiveAccountIsRefused()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.DeleteAsync(Account(LiveEmail), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("AccountIsLive");
        factory.Cli.LogoutCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemovingAnAccountIsRefusedWhileAnotherCredentialChangeHoldsTheGate()
    {
        // The removal revokes a login and deletes a folder, so it belongs behind the
        // one gate every other credential-touching operation takes. Landing between a
        // switch's journal write and its unpark takes away the folder that switch is
        // about to rename out of, past the point where it can back out.
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(ParkedEmail, "refresh-parked", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = ParkedEmail }, TestContext.Current.CancellationToken);
        using IDisposable permit = await factory.Services
            .GetRequiredService<CredentialMutationGate>()
            .AcquireAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);

        using HttpResponseMessage response = await client.DeleteAsync(Account(ParkedEmail), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!
            .GetValue<string>().ShouldBe("MutationInProgress");
        Directory.Exists(folder).ShouldBeTrue();
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(ParkedEmail)).ShouldNotBeNull();
    }

    [Fact]
    public async Task RemovingAnAccountALoginIsRunningAgainstIsRefused()
    {
        // The login child is still alive on an authenticated pipe. Revoke and delete
        // now and the operator's next paste recreates the folder with a fresh,
        // unrevoked token for the account the response said had been removed.
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(ParkedEmail, "refresh-parked", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();
        await client.PostAsJsonAsync(_accounts, new { email = ParkedEmail }, TestContext.Current.CancellationToken);
        using HttpResponseMessage started = await client.PostAsync(Account(ParkedEmail, "/login"), content: null, TestContext.Current.CancellationToken);
        started.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage response = await client.DeleteAsync(Account(ParkedEmail), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!
            .GetValue<string>().ShouldBe("LoginInProgress");
        Directory.Exists(folder).ShouldBeTrue();
        factory.Cli.LogoutCalls.ShouldBeEmpty();
        (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(ParkedEmail)).ShouldNotBeNull();
    }

    [Fact]
    public async Task AdoptLivePutsTheLiveAccountOnTheRoster()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(Account(LiveEmail, "/adopt-live"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Find(Email(LiveEmail)).ShouldNotBeNull();
        JsonObject card = (await CardAsync(client, LiveEmail, TestContext.Current.CancellationToken))!;
        card["isLive"]!.GetValue<bool>().ShouldBeTrue();
        card["roster"]!["email"]!.GetValue<string>().ShouldBe(LiveEmail);
    }

    [Fact]
    public async Task AdoptLiveRefusesANonMaxSeatWithTheReason()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "enterprise";
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(Account(LiveEmail, "/adopt-live"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        body["refusal"]!.GetValue<string>().ShouldBe("NotAMaxAccount");
        body["message"]!.GetValue<string>().ShouldContain("enterprise");
        (await StoredRosterAsync(factory, TestContext.Current.CancellationToken)).Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task AdoptLiveOnAnAccountThatIsNotLiveIsRefused()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateMutatingClient();

        using HttpResponseMessage response = await client.PostAsync(Account(NewEmail, "/adopt-live"), content: null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!["refusal"]!.GetValue<string>().ShouldBe("NotTheLiveAccount");
    }

    [Fact]
    public async Task ThePageDrivesEveryRosterOperationAndTheLogin()
    {
        await using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        string html = await client.GetStringAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        string script = await client.GetStringAsync(new Uri("/app.js", UriKind.Relative), TestContext.Current.CancellationToken);

        html.ShouldContain("Add an account");
        script.ShouldContain("/api/accounts");
        script.ShouldContain("adopt-live");
        script.ShouldContain("\"PATCH\"");
        script.ShouldContain("\"DELETE\"");
        // The Login button, the panel that shows the sign-in URL, and the code field
        // the operator pastes into. The URL-paste field belongs to the mechanism the
        // spike did not select and is deliberately absent.
        script.ShouldContain("\"/login\"");
        script.ShouldContain("startLogin");
        script.ShouldContain("signInUrl");
        script.ShouldContain("/api/login-sessions/");
        script.ShouldContain("paste the code here");
        // The code goes in the body, never in a path.
        script.ShouldContain("{ code: code }");
        // The ten-second poll must leave a half-typed code alone, the way it already
        // leaves an open Edit panel alone, and the same hold covers a display name
        // that is only half typed.
        script.ShouldContain("details.edit[open], details.login[open], input.rename");
    }

    [Fact]
    public async Task ThePageRenamesFromTheHeadingAndKeepsNotesOnTheEditPanel()
    {
        await using AppFactory factory = new();
        using HttpClient client = factory.CreateClient();

        string script = await client.GetStringAsync(new Uri("/app.js", UriKind.Relative), TestContext.Current.CancellationToken);

        // The heading is a display name. With no alias the local part stands in,
        // and the address is on its own line either way so the account stays identifiable.
        script.ShouldContain("function localPart");
        script.ShouldContain("function displayName");
        script.ShouldContain("alias || localPart(account.email)");
        script.ShouldContain("element(\"p\", \"address\", account.email)");
        script.ShouldNotContain("if (alias) { card.appendChild(element(\"p\", \"address\"");
        // Rename is the heading itself, labeled, not only the disclosure under Remove.
        script.ShouldContain("Rename this account");
        script.ShouldContain("\"rename-label\", \"Rename\"");
        script.ShouldContain("input.rename");
        script.ShouldContain("{ alias: alias }");
        // Notes stay on the Edit panel. The shared builder is still the display
        // name, browser, and profile, and a blank note is sent so it can clear.
        script.ShouldContain("labeled(form, \"Notes\", notes)");
        script.ShouldContain("body.notes = notes.value.trim() || null");
        script.ShouldContain("labeled(container, \"Display name\", alias)");
        // The CI token marker: an edit-panel date field, presence-based on save
        // like every other field, and a badge that shows on whichever one card
        // carries it.
        script.ShouldContain("ciToken.type = \"date\"");
        script.ShouldContain("body.ciTokenGeneratedOn = ciToken.value || null");
        script.ShouldContain("badge ci-token");
        // Leaving the field by pressing another control must not disable that
        // control before its click runs, and opening Edit must not refresh the card.
        script.ShouldContain("pointerdown");
        script.ShouldContain("details.edit");
        script.ShouldContain("setTimeout(function () { finish(true); }, 0)");
    }

    [Fact]
    public async Task ARosterMutationWithoutTheCustomHeaderIsForbidden()
    {
        await using AppFactory factory = await LiveOnAsync(TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(_accounts, new { email = NewEmail }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Directory.Exists(FolderOf(factory, NewEmail)).ShouldBeFalse();
    }
}
