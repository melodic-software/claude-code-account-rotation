using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Dashboard;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.App.Tests.Adapters;
using ClaudeCodeAccountRotation.App.Tests.Switching;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeCodeAccountRotation.App.Tests.Dashboard;

/// <summary>
/// The tee file is last-writer-wins across every session on the machine, so a
/// card shows a snapshot only when the snapshot names that card's account and
/// is not the outgoing account's windows carried across a switch (6.6, AC 10).
/// <para>
/// The tee is also only one of the sources a card is built from, so the rest of
/// these facts are about the merge: a card always shows the same rows, each row
/// comes from whichever source captured that bucket last, and a row admits it
/// when nothing knows it or when its window has moved on.
/// </para>
/// </summary>
public sealed class DashboardAssemblerTests
{
    private const string LiveEmail = "dev.a@example.com";
    private const string OtherEmail = "dev.b@example.com";

    // Five roster accounts whose ordinal order is deliberately not the order they
    // free up in, so an assertion on the sequence cannot pass by accident.
    private const string FirstEmail = "a@example.com";
    private const string SecondEmail = "b@example.com";
    private const string ThirdEmail = "c@example.com";
    private const string FourthEmail = "d@example.com";
    private const string FifthEmail = "e@example.com";

    private const string AdoptSetup = "Click Adopt to put the live account on the roster.";

    private const string LoginSetup = "Click Login on the card that needs a login.";

    private const string AddSetup = "Use Add an account, then Login.";

    [Fact]
    public async Task ASnapshotNamingTheLiveAccountIsShownOnItsCard()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "live-token", TestContext.Current.CancellationToken);
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(LiveEmail));

        JsonElement card = await LiveCardAsync(factory);

        Limit(card, 0).GetProperty("label").GetString().ShouldBe("5-hour");
        Limit(card, 0).GetProperty("percent").GetDouble().ShouldBe(69);
        Limit(card, 1).GetProperty("label").GetString().ShouldBe("7-day");
        Limit(card, 1).GetProperty("percent").GetDouble().ShouldBe(43);
        // The tee carries two of the three buckets, and the card still shows all three.
        Limit(card, 2).GetProperty("label").GetString().ShouldBe("scoped");
        Limit(card, 2).GetProperty("known").GetBoolean().ShouldBeFalse();
        Usage(card).GetProperty("source").GetString().ShouldBe("snapshot");
        Usage(card).GetProperty("capturedAt").GetDateTimeOffset()
            .ShouldBe(DateTimeOffset.Parse("2026-09-07T15:33:52Z", System.Globalization.CultureInfo.InvariantCulture));
        card.GetProperty("usageNote").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AnAbsentTeeFileLeavesTheCardWithoutNumbersAndWithoutANote()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);

        JsonElement card = await LiveCardAsync(factory);

        ShouldBeAllUnknown(card);
        card.GetProperty("usageNote").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AnUnreadableTeeFileLeavesTheCardWithoutNumbers()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await WriteTeeAsync(factory, """{"captured_at":"2026-09-07T15:33:52Z","rate_lim""");

        JsonElement card = await LiveCardAsync(factory);

        ShouldBeAllUnknown(card);
        card.GetProperty("usageNote").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task ASnapshotWithNoAccountEmailIsUnattributed()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(email: null));

        JsonElement card = await LiveCardAsync(factory);

        ShouldBeAllUnknown(card);
        card.GetProperty("usageNote").GetString()!.ShouldContain("names no account");
    }

    [Fact]
    public async Task ASnapshotNamingAnotherAccountIsUnattributed()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(OtherEmail));

        JsonElement card = await LiveCardAsync(factory);

        ShouldBeAllUnknown(card);
        card.GetProperty("usageNote").GetString()!.ShouldContain(OtherEmail);
    }

    [Fact]
    public async Task ANeverReadParkedCardShowsThreeUnknownRowsAndAnIdleRefreshState()
    {
        // An account nothing has read renders as unknown rather than as zero or as
        // an empty card: the operator must be able to tell "no quota used" from
        // "nobody has looked" (issue #52).
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);

        JsonElement card = await CardAsync(factory, OtherEmail);

        ShouldBeAllUnknown(card);
        Usage(card).GetProperty("credits").ValueKind.ShouldBe(JsonValueKind.Null);
        card.GetProperty("refresh").GetProperty("state").GetString().ShouldBe("idle");
        card.GetProperty("refresh").GetProperty("message").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheLiveCardKeepsAScopedRowANewerTeeWriteDoesNotCarry()
    {
        // The tee is rewritten by every live session and never carries the scoped
        // window, so a whole-snapshot precedence rule would blank that row the
        // moment a session wrote its statusline. The merge is per bucket instead.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "live-token", TestContext.Current.CancellationToken);
        var read = DateTimeOffset.Parse("2026-09-07T15:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Record(factory, LiveEmail, read, Scoped("Fable", 34, factory.Clock.GetUtcNow().AddDays(2)));
        // Written after that read, and carrying only the two windows the tee knows.
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(LiveEmail));

        JsonElement card = await LiveCardAsync(factory);

        Usage(card).GetProperty("source").GetString().ShouldBe("snapshot");
        Limit(card, 0).GetProperty("percent").GetDouble().ShouldBe(69);
        Limit(card, 0).GetProperty("source").ValueKind.ShouldBe(JsonValueKind.Null);
        Limit(card, 2).GetProperty("label").GetString().ShouldBe("Fable");
        Limit(card, 2).GetProperty("known").GetBoolean().ShouldBeTrue();
        Limit(card, 2).GetProperty("percent").GetDouble().ShouldBe(34);
        // The older source is named on the row, because the card's own line is not it.
        Limit(card, 2).GetProperty("source").GetString().ShouldBe("refresh");
        Limit(card, 2).GetProperty("capturedAt").GetDateTimeOffset().ShouldBe(read);
    }

    [Fact]
    public async Task TheLiveCardsStandingFollowsTheMergedFiguresNotTheCachedReadAlone()
    {
        // The standing an order is keyed on and the row that explains it have to
        // come off the same merge: a regression that built the standing from the
        // cached read alone, bypassing the tee, would still show a mild session
        // figure on the card and would still pass every ordering fact above,
        // because every one of them seeds through the cache alone.
        await using AppFactory factory = new();
        DateTimeOffset now = factory.Clock.GetUtcNow();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "live-token", TestContext.Current.CancellationToken);
        // The cached on-demand read: both windows mild, both resetting ahead.
        Record(
            factory,
            LiveEmail,
            now.AddHours(-2),
            new UsageLimit("session", LimitKind.Session, "session", 10, "ok", now.AddHours(3), null, IsActive: true),
            Weekly(20, now.AddDays(3)));
        // The tee, rewritten after that read, carries only the two windows it
        // always does, and its session figure alone clears the exhaustion line.
        DateTimeOffset sessionResetsAt = now.AddHours(4);
        await WriteTeeAsync(
            factory,
            TeeWithSessionPercent(LiveEmail, now.AddMinutes(-5), sessionPercent: 95, sessionResetsAt, weeklyPercent: 43, now.AddDays(2)));

        JsonElement card = await LiveCardAsync(factory);

        // 95 clears the 90 session threshold; 43 stays well under the 100 weekly
        // one, so the exhausted key is the session reset alone.
        card.GetProperty("standing").GetString().ShouldBe("exhausted");
        card.GetProperty("nextResetAt").GetDateTimeOffset().ShouldBe(sessionResetsAt);
        // The row ties to the same figure the standing used, not to the cached 10.
        Limit(card, 0).GetProperty("percent").GetDouble().ShouldBe(95);
    }

    [Fact]
    public async Task ARowWhoseWindowResetSinceItWasCapturedLosesItsPercentage()
    {
        // A figure from the window before this one measures nothing the operator
        // can act on, so the row says the window reset rather than showing it.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);
        Record(
            factory,
            OtherEmail,
            factory.Clock.GetUtcNow().AddHours(-6),
            new UsageLimit("session", LimitKind.Session, "session", 71, "ok", factory.Clock.GetUtcNow().AddHours(-1), null, IsActive: true),
            new UsageLimit("weekly_all", LimitKind.WeeklyAll, "weekly", 22, "ok", factory.Clock.GetUtcNow().AddDays(3), null, IsActive: true));

        JsonElement card = await CardAsync(factory, OtherEmail);

        Limit(card, 0).GetProperty("known").GetBoolean().ShouldBeTrue();
        Limit(card, 0).GetProperty("windowReset").GetBoolean().ShouldBeTrue();
        Limit(card, 0).GetProperty("percent").ValueKind.ShouldBe(JsonValueKind.Null);
        // The window that has not reset is untouched.
        Limit(card, 1).GetProperty("windowReset").GetBoolean().ShouldBeFalse();
        Limit(card, 1).GetProperty("percent").GetDouble().ShouldBe(22);
    }

    [Fact]
    public async Task AStrandedFolderRendersStrandedAndNoMessageCarriesAPathOrAToken()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);
        await StrandAsync(factory, folder);

        Payload dashboard = await DashboardAsync(factory);
        JsonElement card = Card(dashboard, OtherEmail);

        card.GetProperty("refresh").GetProperty("state").GetString().ShouldBe("stranded");
        card.GetProperty("refresh").GetProperty("message").GetString().ShouldBe("credentials stranded in recovery");
        string payload = dashboard.Raw.ToJsonString();
        payload.ShouldNotContain("refresh-");
        payload.ShouldNotContain("access-");
        // The card's folder is a path by design; a message never is, because a
        // store's own error string carries one and must not reach the page.
        foreach (string message in dashboard.Messages)
        {
            message.ShouldNotContain("refresh-");
            message.ShouldNotContain("access-");
            message.ShouldNotContain("/");
            message.ShouldNotContain("\\");
        }
    }

    [Fact]
    public async Task APassInFlightRendersAsInProgress()
    {
        // The page's existing poll is how a pass is watched, so the dashboard has
        // to say a pass is running before any card has changed.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        QuotaState state = factory.Services.GetRequiredService<QuotaState>();
        state.TryBeginRun().ShouldBeTrue();
        try
        {
            Payload dashboard = await DashboardAsync(factory);

            dashboard.Raw["refresh"]!["inProgress"]!.GetValue<bool>().ShouldBeTrue();
        }
        finally
        {
            state.EndRun();
        }
    }

    [Fact]
    public async Task ARecoveryWarningThatCouldNotBeClearedReachesThePage()
    {
        // A recovery file whose folder has no pair to compare against cannot be
        // applied and must not be deleted: the warning is the only way the operator
        // learns that account needs a login.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);
        await StrandAsync(factory, folder);
        File.Delete(Path.Combine(folder, CredentialFiles.FileName));

        await factory.Services.GetRequiredService<RecoveryFiles>().RestoreAllAsync(TestContext.Current.CancellationToken);
        Payload dashboard = await DashboardAsync(factory);

        dashboard.Raw["warnings"]!.AsArray()
            .Select(static warning => warning!.GetValue<string>())
            .ShouldContain(warning => warning.Contains(OtherEmail, StringComparison.Ordinal) && warning.Contains("log that account in again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreSwitchWindowsAreUnattributed()
    {
        await using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "outgoing-token", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync(OtherEmail, TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(LiveEmail, "incoming-token", TestContext.Current.CancellationToken);
        factory.Cli.Email = LiveEmail;
        // The tee as it stood before the switch: the outgoing account's windows.
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(OtherEmail));

        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage switched = await client.PostAsync(
            new Uri("/api/accounts/" + Uri.EscapeDataString(LiveEmail) + "/switch", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        switched.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        // A session that was mid-turn at switch time writes the outgoing
        // account's windows back under the incoming account's name.
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(LiveEmail));
        JsonElement card = await LiveCardAsync(factory, client);

        card.GetProperty("email").GetString().ShouldBe(LiveEmail);
        ShouldBeAllUnknown(card);
        card.GetProperty("usageNote").GetString()!.ShouldContain("Pre-switch windows");

        // The first snapshot whose reset times differ is the incoming account's own.
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(LiveEmail, fiveHourResetsAt: 1788900400, sevenDayResetsAt: 1789415200));
        JsonElement fresh = await LiveCardAsync(factory, client);

        Limit(fresh, 0).GetProperty("percent").GetDouble().ShouldBe(69);
        fresh.GetProperty("usageNote").ValueKind.ShouldBe(JsonValueKind.Null);

        // And the stash is spent, not merely stepped over: the same reset times
        // again are now this account's own, because the windows have moved on once.
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(LiveEmail));
        JsonElement later = await LiveCardAsync(factory, client);

        Limit(later, 1).GetProperty("percent").GetDouble().ShouldBe(43);
        later.GetProperty("usageNote").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task ASwitchWithNoReadableTeeClearsAnEarlierStash()
    {
        // Two switches. The first stashes the outgoing windows; the second cannot
        // read the tee, and must not leave the first switch's values behind to
        // disown a later account's own snapshot whose reset times happen to match.
        await using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "outgoing-token", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync(OtherEmail, TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(LiveEmail, "incoming-token", TestContext.Current.CancellationToken);
        factory.Cli.Email = LiveEmail;
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(OtherEmail));

        using HttpClient client = factory.CreateMutatingClient();
        await SwitchAsync(client, LiveEmail);

        // Back the other way, with the tee gone at switch time.
        File.Delete(Path.Combine(factory.LiveDirectory, "rate-limit-guard", "rate-limits.json"));
        factory.Cli.Email = OtherEmail;
        (await SwitchAsync(client, OtherEmail)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        // The stash is gone, so these windows belong to whoever the tee names.
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(OtherEmail));
        JsonElement card = await LiveCardAsync(factory, client);

        card.GetProperty("email").GetString().ShouldBe(OtherEmail);
        Limit(card, 0).GetProperty("percent").GetDouble().ShouldBe(69);
        card.GetProperty("usageNote").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static Task<HttpResponseMessage> SwitchAsync(HttpClient client, string email) =>
        client.PostAsync(
            new Uri("/api/accounts/" + Uri.EscapeDataString(email) + "/switch", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task AnAbsurdResetTimeInTheTeeBreaksNeitherEndpoint()
    {
        // The tee is written by other processes. A number outside DateTimeOffset's
        // range used to throw out of the read, and both endpoints read the tee, so
        // one line of that file could 500 the page and the switch alike.
        await using AppFactory factory = new();
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "outgoing-token", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync(OtherEmail, TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(LiveEmail, "incoming-token", TestContext.Current.CancellationToken);
        factory.Cli.Email = LiveEmail;
        await WriteTeeAsync(factory, RateLimitGuardTeeFileReaderTests.Tee(OtherEmail, fiveHourResetsAt: 1000000000000000000L));

        using HttpClient client = factory.CreateMutatingClient();
        using HttpResponseMessage dashboard = await client.GetAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);
        using HttpResponseMessage switched = await client.PostAsync(
            new Uri("/api/accounts/" + Uri.EscapeDataString(LiveEmail) + "/switch", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        dashboard.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        // The switch reached the planner and ran rather than failing on the tee read.
        switched.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task ACachedSnapshotRendersAsCachedWithItsOriginalCaptureTime()
    {
        // A figure the cache file put back after a restart is the same figure it
        // always was, and the card says so: "via cached, as of" the moment it was
        // read, never the moment it was loaded. A reader who cannot tell the two
        // apart cannot tell a fresh card from a day-old one.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);
        DateTimeOffset captured = factory.Clock.GetUtcNow().AddHours(-4);
        factory.Services.GetRequiredService<QuotaState>().RecordSnapshot(new UsageSnapshot(
            AccountEmail.Parse(OtherEmail).Value,
            captured,
            QuotaSource.Cached,
            // A window still open, so the row keeps its percentage: a reset one
            // blanks through the same rule that keeps a cached figure honest.
            [new UsageLimit("session", LimitKind.Session, "session", 43, "ok", factory.Clock.GetUtcNow().AddHours(1), null, IsActive: true)],
            ExtraUsage: null));

        JsonElement card = await CardAsync(factory, OtherEmail);

        Usage(card).GetProperty("source").GetString().ShouldBe("cached");
        Usage(card).GetProperty("capturedAt").GetDateTimeOffset().ShouldBe(captured);
        Limit(card, 0).GetProperty("percent").GetDouble().ShouldBe(43);
        Limit(card, 0).GetProperty("windowReset").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task TheCardsAreOrderedByWhenEachAccountFreesUpNext()
    {
        // The operator reads the list top down, so the account that frees up
        // soonest has to be the one at the top: the usable accounts by the weekly
        // window closest to turning over, then the exhausted ones by when they
        // actually come back, then the ones nothing has read, then the ones taken
        // out of the rotation.
        await using AppFactory factory = new();
        DateTimeOffset now = factory.Clock.GetUtcNow();
        await factory.WriteStateFileAsync(FirstEmail, TestContext.Current.CancellationToken);
        await RosterAsync(
            factory,
            Entry(FirstEmail),
            Entry(SecondEmail),
            Entry(ThirdEmail),
            Entry(FourthEmail),
            Entry(FifthEmail, paused: true));
        Record(factory, FirstEmail, now.AddHours(-1), Weekly(42, now.AddDays(3)));
        Record(factory, SecondEmail, now.AddHours(-1), Weekly(100, now.AddDays(1)));
        Record(factory, ThirdEmail, now.AddHours(-1), Weekly(58, now.AddHours(1)));
        // The earliest window of all, and last anyway: the roster decides whether
        // an account is a candidate, and its numbers do not argue with that.
        Record(factory, FifthEmail, now.AddHours(-1), Weekly(4, now.AddMinutes(30)));

        using HttpClient client = factory.CreateClient();
        JsonElement dashboard = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);

        // The live account is second, and is not pinned to the top.
        dashboard.GetProperty("accounts").EnumerateArray()
            .Select(static card => card.GetProperty("email").GetString())
            .ShouldBe([ThirdEmail, FirstEmail, SecondEmail, FourthEmail, FifthEmail]);
        JsonElement usable = Card(dashboard, ThirdEmail);
        usable.GetProperty("standing").GetString().ShouldBe("usable");
        usable.GetProperty("nextResetAt").GetDateTimeOffset().ShouldBe(now.AddHours(1));
        JsonElement exhausted = Card(dashboard, SecondEmail);
        exhausted.GetProperty("standing").GetString().ShouldBe("exhausted");
        exhausted.GetProperty("nextResetAt").GetDateTimeOffset().ShouldBe(now.AddDays(1));
    }

    [Fact]
    public async Task TwoProfileFoldersNamingTheSameAccountBothShowACard()
    {
        // ProfileFolderStore.ListAsync does not de-duplicate by e-mail, so a
        // hand-copied folder can name the same account a second time. The page
        // must render both cards, not fault matching the arrangement back to them.
        await using AppFactory factory = new();
        await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);
        string duplicate = Path.Combine(factory.ProfilesRoot, "dev-b-duplicate");
        Directory.CreateDirectory(duplicate);
        await File.WriteAllTextAsync(
            Path.Combine(duplicate, "profile.json"),
            AppFactory.AccountJson(OtherEmail).ToJsonString(),
            TestContext.Current.CancellationToken);

        using HttpClient client = factory.CreateClient();
        JsonElement dashboard = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);

        dashboard.GetProperty("accounts").EnumerateArray()
            .Count(card => card.GetProperty("email").GetString() == OtherEmail)
            .ShouldBe(2);
    }

    [Fact]
    public async Task ACachedFigureFromAWindowThatHasResetSortsUsable()
    {
        // A hundred percent from a window that has since turned over measures
        // nothing: the account is free now. It must not sort as exhausted, and it
        // must state no wait at all, because the only instant it has is in the
        // past and would render as a countdown that already expired.
        await using AppFactory factory = new();
        DateTimeOffset now = factory.Clock.GetUtcNow();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await RosterAsync(factory, Entry(LiveEmail), Entry(FirstEmail));
        factory.Services.GetRequiredService<QuotaState>().RecordSnapshot(new UsageSnapshot(
            AccountEmail.Parse(FirstEmail).Value,
            now.AddHours(-6),
            QuotaSource.Cached,
            [Weekly(100, now.AddHours(-1))],
            ExtraUsage: null));

        JsonElement card = await CardAsync(factory, FirstEmail);

        card.GetProperty("standing").GetString().ShouldBe("usable");
        card.GetProperty("nextResetAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task ASpentAccountWhoseResetCannotBeDatedCarriesNoInstant()
    {
        // Spent, with no reset instant to spend it against: the card still says
        // the account is out, and says nothing about when it returns, because the
        // page renders the line from this field and would otherwise have to
        // invent the hour.
        await using AppFactory factory = new();
        DateTimeOffset now = factory.Clock.GetUtcNow();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await RosterAsync(factory, Entry(LiveEmail), Entry(FirstEmail));
        Record(factory, FirstEmail, now.AddHours(-1), Weekly(100));

        JsonElement card = await CardAsync(factory, FirstEmail);

        card.GetProperty("standing").GetString().ShouldBe("exhausted");
        card.GetProperty("nextResetAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AParkedCardTakesItsLoginExpiryFromItsPairAndSaysNothingAboutAgeWithoutAStamp()
    {
        // The expiry is the pair's refreshTokenExpiresAt and nothing else; a
        // profile that was written before the CLI stamped profileFetchedAt leaves
        // the age half of the line off rather than guessing it.
        await using AppFactory factory = new();
        DateTimeOffset expiry = factory.Clock.GetUtcNow().AddDays(16);
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken, loginExpiresAt: expiry);

        JsonElement card = await CardAsync(factory, OtherEmail);

        card.GetProperty("loginExpiresAt").GetDateTimeOffset().ShouldBe(expiry);
        card.GetProperty("loggedInAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AParkedCardTakesItsLoginAgeFromTheProfilesOwnStamp()
    {
        await using AppFactory factory = new();
        DateTimeOffset loggedInAt = factory.Clock.GetUtcNow().AddDays(-12);
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken, profileFetchedAt: loggedInAt);

        JsonElement card = await CardAsync(factory, OtherEmail);

        card.GetProperty("loggedInAt").GetDateTimeOffset().ShouldBe(loggedInAt);
    }

    [Fact]
    public async Task ARosterOnlyCardCarriesNeitherInstant()
    {
        // An account the operator has added but never logged in owns no pair and
        // no profile, so there is nothing to date: both fields are null and the
        // page shows no login line at all.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await RosterAsync(factory, Entry(LiveEmail), Entry(FirstEmail));

        JsonElement card = await CardAsync(factory, FirstEmail);

        card.GetProperty("hasCredentials").GetBoolean().ShouldBeFalse();
        card.GetProperty("loginExpiresAt").ValueKind.ShouldBe(JsonValueKind.Null);
        card.GetProperty("loggedInAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheLiveCardTakesItsInstantsFromTheLivePairAndTheStateFilesOwnBlock()
    {
        // The live account's credentials are not in a profile folder and its
        // identity is not in a profile.json, so both instants come from the two
        // files the live directory and the state file actually hold.
        await using AppFactory factory = new();
        DateTimeOffset expiry = factory.Clock.GetUtcNow().AddDays(16);
        DateTimeOffset loggedInAt = factory.Clock.GetUtcNow().AddDays(-12);
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken, profileFetchedAt: loggedInAt);
        await File.WriteAllTextAsync(
            Path.Combine(factory.LiveDirectory, CredentialFiles.FileName),
            CredentialFiles.Shape("live-token", loginExpiresAt: expiry).ToJsonString(),
            TestContext.Current.CancellationToken);

        JsonElement card = await LiveCardAsync(factory);

        card.GetProperty("loginExpiresAt").GetDateTimeOffset().ShouldBe(expiry);
        card.GetProperty("loggedInAt").GetDateTimeOffset().ShouldBe(loggedInAt);
    }

    [Fact]
    public async Task ATornParkedCredentialFileLeavesTheExpiryBlankAndStillListsTheAccount()
    {
        // One unreadable file is one blank line, never a failed page: the card is
        // still built, still says it has credentials (which is file existence),
        // and the folder alone reaches the log.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();
        // Torn after the host has started, the way a stranded pair is: startup's
        // own sweep reads every parked pair, and this fact is about the poll.
        await File.WriteAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), "{", TestContext.Current.CancellationToken);

        JsonElement dashboard = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);
        JsonElement card = Card(dashboard, OtherEmail);

        card.GetProperty("hasCredentials").GetBoolean().ShouldBeTrue();
        card.GetProperty("loginExpiresAt").ValueKind.ShouldBe(JsonValueKind.Null);
        factory.Logs.Lines.ShouldContain(line => line.Contains("login expiry unreadable for", StringComparison.Ordinal));
        factory.Logs.Lines.ShouldNotContain(line => line.Contains("refresh-b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStuckUnreadableParkedFileWarnsOnce()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();
        await File.WriteAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), "{", TestContext.Current.CancellationToken);

        using HttpResponseMessage first = await client.GetAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);
        using HttpResponseMessage second = await client.GetAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);

        first.IsSuccessStatusCode.ShouldBeTrue();
        second.IsSuccessStatusCode.ShouldBeTrue();
        factory.Logs.Lines.Count(line => line.Contains("login expiry unreadable for", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task ATornLiveCredentialFileReturnsTheDashboardWithADegradedLiveCard()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(factory.LiveDirectory, CredentialFiles.FileName),
            "{",
            TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement card = Card(document.RootElement, LiveEmail);

        card.GetProperty("isLive").GetBoolean().ShouldBeTrue();
        card.GetProperty("hasCredentials").GetBoolean().ShouldBeTrue();
        card.GetProperty("loginExpiresAt").ValueKind.ShouldBe(JsonValueKind.Null);
        factory.Logs.Lines.ShouldContain(line => line.Contains("live credentials unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AParkedCredentialFileWithAnEpochOutOfRangeLeavesTheExpiryBlankAndStillListsTheAccount()
    {
        // Well-formed JSON the parser still cannot turn into a pair: every field
        // is the shape the CLI writes and refreshTokenExpiresAt is a whole number
        // of milliseconds, just one no instant can hold. The card costs the same
        // as a torn file does, because unreadable is unreadable.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        string folder = await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();
        JsonObject shape = CredentialFiles.Shape(
            "refresh-b",
            accessTokenExpiresAt: factory.Clock.GetUtcNow().AddHours(8),
            loginExpiresAt: factory.Clock.GetUtcNow().AddDays(28));
        shape["claudeAiOauth"]!["refreshTokenExpiresAt"] = 99999999999999999L;
        await File.WriteAllTextAsync(Path.Combine(folder, CredentialFiles.FileName), shape.ToJsonString(), TestContext.Current.CancellationToken);

        JsonElement dashboard = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);
        JsonElement card = Card(dashboard, OtherEmail);

        card.GetProperty("hasCredentials").GetBoolean().ShouldBeTrue();
        card.GetProperty("loginExpiresAt").ValueKind.ShouldBe(JsonValueKind.Null);
        factory.Logs.Lines.ShouldContain(line => line.Contains("login expiry unreadable for", StringComparison.Ordinal));
        factory.Logs.Lines.ShouldNotContain(line => line.Contains("refresh-b", StringComparison.Ordinal));
    }

    /// <summary>The rows a card shows before anything has numbers for it: named, ordered, and every one of them unknown.</summary>
    private static void ShouldBeAllUnknown(JsonElement card)
    {
        Usage(card).GetProperty("source").ValueKind.ShouldBe(JsonValueKind.Null);
        Usage(card).GetProperty("capturedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        Usage(card).GetProperty("limits").GetArrayLength().ShouldBe(3);
        for (int row = 0; row < 3; row++)
        {
            Limit(card, row).GetProperty("known").GetBoolean().ShouldBeFalse();
            Limit(card, row).GetProperty("percent").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        Usage(card).GetProperty("limits").EnumerateArray()
            .Select(static limit => limit.GetProperty("label").GetString())
            .ShouldBe(["5-hour", "7-day", "scoped"]);
    }

    [Fact]
    public async Task TheLiveAccountsChipSaysItIsLiveHereAndNoSideIsOfferedIt()
    {
        await using AppFactory factory = await SharedStoreAsync(Side());

        JsonElement card = await CardAsync(factory, LiveEmail);

        card.GetProperty("chip").GetString().ShouldBe("live here");
        card.GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
        OfferedTo(card).ShouldBeEmpty();
    }

    [Fact]
    public async Task AParkedAccountsChipSaysParkedAndBothSwitchesOfferIt()
    {
        await using AppFactory factory = await SharedStoreAsync(Side());

        JsonElement card = await CardAsync(factory, ParkedEmail);

        card.GetProperty("chip").GetString().ShouldBe("parked");
        card.GetProperty("canSwitchHere").GetBoolean().ShouldBeTrue();
        OfferedTo(card).ShouldBe(["wsl"]);
    }

    /// <summary>
    /// A parked pair whose login has run out is no more switchable by the other
    /// side than by this one — its planner refuses it with
    /// <c>TargetLoginExpired</c> — so the picker does not offer it either.
    /// </summary>
    [Fact]
    public async Task AParkedAccountWhoseLoginHasExpiredIsOfferedToNoSide()
    {
        await using AppFactory factory = await SharedStoreAsync(Side());
        _ = await factory.ParkedProfileAsync(
            ExpiredEmail,
            "refresh-expired",
            TestContext.Current.CancellationToken,
            loginExpiresAt: factory.Clock.GetUtcNow().AddDays(-1));

        JsonElement card = await CardAsync(factory, ExpiredEmail);

        card.GetProperty("chip").GetString().ShouldBe("parked");
        card.GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
        OfferedTo(card).ShouldBeEmpty();
    }

    /// <summary>
    /// The card R6 is about: the pair is in the other side's live directory, so
    /// neither this side's Switch nor that side's may take it until that side
    /// parks it back.
    /// </summary>
    [Fact]
    public async Task AnAccountTheWslSideHoldsSaysSoAndNeitherSwitchOffersIt()
    {
        await using AppFactory factory = await SharedStoreAsync(Side());

        JsonElement card = await CardAsync(factory, HeldEmail);

        card.GetProperty("chip").GetString().ShouldBe("in use by wsl");
        card.GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
        OfferedTo(card).ShouldBeEmpty();
        // The slot is empty by design, not by a missing login, and this is the
        // flag the page reads before it says anything about the credential.
        card.GetProperty("heldAway").GetBoolean().ShouldBeTrue();
        card.GetProperty("hasCredentials").GetBoolean().ShouldBeFalse();
    }

    /// <summary>Design 11's "distro off" row: the pair is still there, and the chip says why nothing can reach it.</summary>
    [Fact]
    public async Task AnAccountTheWslSideHoldsWhileThatSideIsOfflineSaysOffline()
    {
        FakePeerRotationInstance side = Side();
        side.DashboardError = "the distribution is not running";
        await using AppFactory factory = await SharedStoreAsync(side);

        JsonElement card = await CardAsync(factory, HeldEmail);

        card.GetProperty("chip").GetString().ShouldBe("in use by wsl (offline)");
        card.GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
        OfferedTo(card).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAccountWhoseHandOffIsInFlightSaysWhereItIsGoing()
    {
        FakePeerRotationInstance side = Side();
        await using AppFactory factory = await SharedStoreAsync(side);
        await InTransitAsync(factory, side, ParkedEmail);

        JsonElement card = await CardAsync(factory, ParkedEmail);

        card.GetProperty("chip").GetString().ShouldBe("in transit to wsl");
        card.GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
        OfferedTo(card).ShouldBeEmpty();
    }

    /// <summary>
    /// The other direction, which the record cannot tell apart on its own: a
    /// pair coming back to the store reads as an <i>export</i> in the mailbox,
    /// and a card that read the record for this would tell an operator watching
    /// a park-back that the pair was heading the other way.
    /// </summary>
    [Fact]
    public async Task AnAccountBeingHandedBackSaysItIsComingFromThatSide()
    {
        FakePeerRotationInstance side = Side();
        // The side stopped answering mid-hand-off, which is what leaves an
        // export standing long enough for a poll to draw a card over it: a
        // reachable side finishes the park-back on the same poll.
        side.DashboardError = "the distribution is not running";
        await using AppFactory factory = await SharedStoreAsync(side);
        await ReturningAsync(factory, HeldEmail);

        JsonElement card = await CardAsync(factory, HeldEmail);

        card.GetProperty("chip").GetString().ShouldBe("in transit from wsl");
        card.GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
        OfferedTo(card).ShouldBeEmpty();
    }

    /// <summary>
    /// Design 12: the leader never reads a held pair's usage, so the card's
    /// figures are the follower's own tee, carried on its dashboard, and the
    /// card says where they came from and when they were taken.
    /// </summary>
    [Fact]
    public async Task AWslHeldAccountsFiguresComeFromThatSidesTeeWithItsCaptureAge()
    {
        FakePeerRotationInstance side = Side();
        side.Tee = new StatuslineSnapshot(
            _teeCapturedAt,
            SessionId: null,
            AccountEmail.Parse(HeldEmail).Value,
            FiveHourPercent: 31,
            FiveHourResetsAt: _teeCapturedAt.AddHours(5),
            SevenDayPercent: 12,
            SevenDayResetsAt: _teeCapturedAt.AddDays(4));
        await using AppFactory factory = await SharedStoreAsync(side);

        JsonElement card = await CardAsync(factory, HeldEmail);

        Usage(card).GetProperty("source").GetString().ShouldBe("snapshot");
        Usage(card).GetProperty("capturedAt").GetDateTimeOffset().ShouldBe(_teeCapturedAt);
        Limit(card, 0).GetProperty("percent").GetDouble().ShouldBe(31);
        Limit(card, 1).GetProperty("percent").GetDouble().ShouldBe(12);
        card.GetProperty("usageNote").GetString().ShouldBe("in use by wsl; figures come from wsl sessions");
    }

    /// <summary>
    /// No session has run on that side since this account's windows last reset,
    /// so its tee names some other account (or nothing at all) and the card has
    /// no figures for this one. Every row admits it rather than showing what
    /// Windows last read before the pair left.
    /// </summary>
    [Fact]
    public async Task AWslHeldAccountWithNoSessionSinceItsResetReadsUnknownRatherThanAStalePercentage()
    {
        FakePeerRotationInstance side = Side();
        side.Tee = new StatuslineSnapshot(
            _teeCapturedAt,
            SessionId: null,
            AccountEmail.Parse(ParkedEmail).Value,
            FiveHourPercent: 88,
            FiveHourResetsAt: _teeCapturedAt.AddHours(5),
            SevenDayPercent: 77,
            SevenDayResetsAt: _teeCapturedAt.AddDays(4));
        await using AppFactory factory = await SharedStoreAsync(side);
        // What Windows read while it still held the pair, which is exactly the
        // stale percentage this card must not show.
        Record(factory, HeldEmail, _teeCapturedAt.AddDays(-1), Weekly(64, _teeCapturedAt.AddDays(6)));

        JsonElement card = await CardAsync(factory, HeldEmail);

        ShouldBeAllUnknown(card);
        Usage(card).GetProperty("source").ValueKind.ShouldBe(JsonValueKind.Null);
        card.GetProperty("usageNote").GetString().ShouldBe("in use by wsl; figures come from wsl sessions");
    }

    /// <summary>
    /// The number the month is planned around. One family per account means one
    /// login expiry, and for an account the other side holds there is no local
    /// file left to read it from — the 28-day window is fixed and a refresh does
    /// not move it, so a card that silently dropped this would hide the one
    /// re-login the operator has to schedule.
    /// </summary>
    [Fact]
    public async Task AWslHeldAccountsLoginExpiryComesFromTheSideThatHoldsIt()
    {
        FakePeerRotationInstance side = Side();
        side.OutgoingEmail = HeldEmail;
        side.LoginExpiresAt = _teeCapturedAt.AddDays(3);
        await using AppFactory factory = await SharedStoreAsync(side);

        JsonElement card = await CardAsync(factory, HeldEmail);

        card.GetProperty("loginExpiresAt").GetDateTimeOffset().ShouldBe(_teeCapturedAt.AddDays(3));
    }

    /// <summary>
    /// With the distribution off there is no one to ask, and the slot holds no
    /// file: the card says nothing about the expiry rather than something from
    /// before the hand-off.
    /// </summary>
    [Fact]
    public async Task AWslHeldAccountsLoginExpiryIsAbsentWhileThatSideIsOffline()
    {
        FakePeerRotationInstance side = Side();
        side.OutgoingEmail = HeldEmail;
        side.LoginExpiresAt = _teeCapturedAt.AddDays(3);
        side.DashboardError = "the distribution is not running";
        await using AppFactory factory = await SharedStoreAsync(side);

        JsonElement card = await CardAsync(factory, HeldEmail);

        card.GetProperty("loginExpiresAt").ValueKind.ShouldBe(JsonValueKind.Null);
        card.GetProperty("chip").GetString().ShouldBe("in use by wsl (offline)");
    }

    /// <summary>A tee whose windows have moved on keeps its age and loses its figures.</summary>
    [Fact]
    public async Task AWslHeldAccountsFiguresGoWhenTheirWindowHasResetSinceTheyWereTaken()
    {
        FakePeerRotationInstance side = Side();
        side.Tee = new StatuslineSnapshot(
            _teeCapturedAt,
            SessionId: null,
            AccountEmail.Parse(HeldEmail).Value,
            FiveHourPercent: 31,
            FiveHourResetsAt: _teeCapturedAt.AddMinutes(1),
            SevenDayPercent: 12,
            SevenDayResetsAt: _teeCapturedAt.AddMinutes(1));
        await using AppFactory factory = await SharedStoreAsync(side);

        JsonElement card = await CardAsync(factory, HeldEmail);

        Limit(card, 0).GetProperty("percent").ValueKind.ShouldBe(JsonValueKind.Null);
        Limit(card, 0).GetProperty("windowReset").GetBoolean().ShouldBeTrue();
        Limit(card, 1).GetProperty("percent").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AnEmptyRosterNamesAdoptWhenTheLiveAccountCanJoin()
    {
        // The live card is not on the roster, so the next step is Adopt, even
        // when another card is waiting on Login. The sentence is its own field.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await RosterAsync(factory, Entry(FirstEmail));
        factory.Services.GetRequiredService<DashboardState>().Publish(current => current with
        {
            LastReconciliation = new ReconciliationReport([], "clear", false, "A duplicate lineage is quarantined."),
        });

        Payload dashboard = await DashboardAsync(factory);

        SetupOf(dashboard).ShouldBe(AdoptSetup);
        dashboard.Element.GetProperty("banner").GetString().ShouldBe("A duplicate lineage is quarantined.");
        dashboard.Element.GetProperty("banner").GetString().ShouldNotBe(SetupOf(dashboard));
        dashboard.Element.GetProperty("warnings").EnumerateArray()
            .Select(static warning => warning.GetString())
            .ShouldNotContain(AdoptSetup);
        Card(dashboard, FirstEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ARosterCardThatNeedsLoginIsNamedWhenNothingLiveCanBeAdopted()
    {
        await using AppFactory factory = new();
        await RosterAsync(factory, Entry(FirstEmail));

        Payload dashboard = await DashboardAsync(factory);

        SetupOf(dashboard).ShouldBe(LoginSetup);
        dashboard.Element.GetProperty("banner").ValueKind.ShouldBe(JsonValueKind.Null);
        Card(dashboard, FirstEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task AnEmptyDashboardNamesAddThenLogin()
    {
        await using AppFactory factory = new();

        Payload dashboard = await DashboardAsync(factory);

        SetupOf(dashboard).ShouldBe(AddSetup);
        dashboard.Element.GetProperty("banner").ValueKind.ShouldBe(JsonValueKind.Null);
        dashboard.Element.GetProperty("warnings").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task ALiveSeatAdoptWouldRefuseNamesAddRatherThanAdopt()
    {
        // The same evidence adopt-live refuses with 409 NotAMaxAccount. The
        // sentence must not send the operator at that button.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "enterprise";

        Payload dashboard = await DashboardAsync(factory);

        SetupOf(dashboard).ShouldBe(AddSetup);
        dashboard.Element.GetProperty("banner").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AnUnreadableLiveSeatIsAskedAgainOnceTheCliAnswers()
    {
        // The first read failed, so the seat is unknown and Adopt is still the
        // step the route would allow. A later read that reports enterprise has
        // to replace that sentence; remembering the failure as "not refused"
        // would leave Adopt on the page for the rest of the process.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        factory.Cli.ReadError = "the CLI printed nothing";

        Payload first = await DashboardAsync(factory);

        SetupOf(first).ShouldBe(AdoptSetup);

        factory.Cli.ReadError = null;
        factory.Cli.SubscriptionType = "enterprise";

        Payload second = await DashboardAsync(factory);

        SetupOf(second).ShouldBe(AddSetup);
    }

    [Fact]
    public async Task ARememberedRefusalExpiresAndALaterMaxAnswerNamesAdopt()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "enterprise";

        Payload first = await DashboardAsync(factory);
        SetupOf(first).ShouldBe(AddSetup);

        // Inside the minute the memory stands, so a quiet upgrade does not
        // spawn the CLI on the very next poll.
        factory.Cli.SubscriptionType = "max";
        Payload held = await DashboardAsync(factory);
        SetupOf(held).ShouldBe(AddSetup);

        factory.Clock.Advance(DashboardAssembler.LiveSeatJudgmentLifetime);

        Payload second = await DashboardAsync(factory);
        SetupOf(second).ShouldBe(AdoptSetup);
    }

    [Fact]
    public async Task ARememberedMaxAnswerExpiresAndALaterEnterpriseAnswerNamesAdd()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "max";

        Payload first = await DashboardAsync(factory);
        SetupOf(first).ShouldBe(AdoptSetup);

        factory.Clock.Advance(DashboardAssembler.LiveSeatJudgmentLifetime);
        factory.Cli.SubscriptionType = "enterprise";

        Payload second = await DashboardAsync(factory);
        SetupOf(second).ShouldBe(AddSetup);
    }

    [Fact]
    public async Task AChangedRateLimitTierAsksAgainBeforeTheJudgmentExpires()
    {
        // The tier string is part of the memory. A new one is a different
        // login, so the sentence follows the CLI now instead of waiting out
        // the minute.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "enterprise";

        Payload first = await DashboardAsync(factory);
        SetupOf(first).ShouldBe(AddSetup);

        JsonObject account = AppFactory.AccountJson(LiveEmail);
        account["organizationRateLimitTier"] = "default_claude_ai";
        await File.WriteAllTextAsync(
            factory.StateFilePath,
            new JsonObject { ["numStartups"] = 3, ["oauthAccount"] = account }.ToJsonString(),
            TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "max";

        Payload second = await DashboardAsync(factory);
        SetupOf(second).ShouldBe(AdoptSetup);
    }

    [Fact]
    public async Task ARefusedLiveSeatFallsThroughToLoginWhenARosterCardNeedsIt()
    {
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "enterprise";
        await RosterAsync(factory, Entry(FirstEmail));

        Payload dashboard = await DashboardAsync(factory);

        SetupOf(dashboard).ShouldBe(LoginSetup);
    }

    [Fact]
    public async Task ALiveMaxTierStillNamesAdoptWhenTheCliReportsAnotherSubscription()
    {
        // The account block admits a Max tier before the CLI's subscription is
        // read, which is the adopt route's own order. A tier that already
        // admits is not a 409, so the sentence still says Adopt.
        await using AppFactory factory = new();
        JsonObject account = AppFactory.AccountJson(LiveEmail);
        account["organizationRateLimitTier"] = "default_claude_max_20x";
        await File.WriteAllTextAsync(
            factory.StateFilePath,
            new JsonObject { ["numStartups"] = 3, ["oauthAccount"] = account }.ToJsonString(),
            TestContext.Current.CancellationToken);
        factory.Cli.SubscriptionType = "enterprise";

        Payload dashboard = await DashboardAsync(factory);

        SetupOf(dashboard).ShouldBe(AdoptSetup);
    }

    [Fact]
    public async Task AUsableRosterAccountClearsTheSetupSentence()
    {
        // One non-paused account that is live, or one that already has
        // credentials, is enough. The sentence is null, and Switch stays on
        // canSwitchHere.
        await using AppFactory factory = new();
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);
        await RosterAsync(factory, Entry(LiveEmail), Entry(OtherEmail), Entry(FirstEmail));

        Payload dashboard = await DashboardAsync(factory);

        dashboard.Element.GetProperty("setup").ValueKind.ShouldBe(JsonValueKind.Null);
        Card(dashboard, OtherEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeTrue();
        Card(dashboard, LiveEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task APausedAccountWithCredentialsDoesNotClearTheSentenceAndCanStillBeSwitchedTo()
    {
        await using AppFactory factory = new();
        await factory.ParkedProfileAsync(OtherEmail, "refresh-b", TestContext.Current.CancellationToken);
        await RosterAsync(factory, Entry(OtherEmail, paused: true));

        Payload dashboard = await DashboardAsync(factory);

        SetupOf(dashboard).ShouldBe(AddSetup);
        Card(dashboard, OtherEmail).GetProperty("canSwitchHere").GetBoolean().ShouldBeTrue();
    }

    /// <summary>The instant the fake side's tee is taken at, three hours before the factory's own clock.</summary>
    private static readonly DateTimeOffset _teeCapturedAt = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);

    private const string ParkedEmail = "parked@example.com";

    private const string HeldEmail = "held@example.com";

    private const string ExpiredEmail = "expired@example.com";

    /// <summary>A side that answers, holds nothing, and reports this build's own version.</summary>
    private static FakePeerRotationInstance Side() => new(mailbox: Path.GetTempPath()) { OutgoingEmail = null, OutgoingRefreshToken = null };

    private static IReadOnlyList<string?> OfferedTo(JsonElement card) =>
        [.. card.GetProperty("offeredTo").EnumerateArray().Select(static side => side.GetString())];

    /// <summary>
    /// A shared store holding the three slots every chip fact reads: the live
    /// one, a parked one, and one the WSL side has taken, with
    /// <paramref name="side"/> standing in for the distribution's own process.
    /// </summary>
    private static async Task<AppFactory> SharedStoreAsync(FakePeerRotationInstance side)
    {
        AppFactory factory = new(sharedStore: true);
        factory.Overrides = services => services.Replace(ServiceDescriptor.Singleton(
            new PeerRegistry([new Peer(side, null, factory.ProfilesRoot)])));
        await CredentialFiles.WriteAsync(factory.LiveDirectory, "refresh-live", TestContext.Current.CancellationToken);
        await factory.WriteStateFileAsync(LiveEmail, TestContext.Current.CancellationToken);
        _ = await factory.ParkedProfileAsync(ParkedEmail, "refresh-parked", TestContext.Current.CancellationToken);
        string held = Path.Combine(factory.ProfilesRoot, HeldEmail);
        Directory.CreateDirectory(held);
        await File.WriteAllTextAsync(
            Path.Combine(held, "profile.json"),
            AppFactory.AccountJson(HeldEmail).ToJsonString(),
            TestContext.Current.CancellationToken);
        await HolderRecordFile.WriteAsync(
            held,
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-held").Fingerprint, _teeCapturedAt),
            TestContext.Current.CancellationToken);
        return factory;
    }

    /// <summary>
    /// A claim in flight, as the coordinator's L2 leaves one: the record goes in
    /// first, then the pair leaves the slot for that side's mailbox. The side is
    /// told to answer "imported", because a claim with no journal whose side
    /// says it never imported is one reconciliation puts straight back.
    /// </summary>
    private static async Task InTransitAsync(AppFactory factory, FakePeerRotationInstance side, string email)
    {
        side.Status = new ImportStatus(Imported: true, JournalStep: null, LiveFingerprint: null, LiveAccount: null, "imported already");
        string folder = Path.Combine(factory.ProfilesRoot, email);
        await HolderRecordFile.WriteAsync(
            folder,
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-parked").Fingerprint, _teeCapturedAt),
            TestContext.Current.CancellationToken);
        string mailbox = FileSystemCredentialPairStore.MailboxPath(factory.ProfilesRoot, SideName.Wsl);
        Directory.CreateDirectory(mailbox);
        File.Move(
            Path.Combine(folder, FileSystemCredentialPairStore.FileName),
            Path.Combine(mailbox, FileSystemCredentialPairStore.ClaimedFileName(email)));
    }

    /// <summary>
    /// What a release in flight leaves in the store: the slot's record still
    /// names the side that holds the account, and its file in the mailbox is an
    /// export rather than a claim.
    /// </summary>
    private static async Task ReturningAsync(AppFactory factory, string email)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string mailbox = FileSystemCredentialPairStore.MailboxPath(factory.ProfilesRoot, SideName.Wsl);
        Directory.CreateDirectory(mailbox);
        await File.WriteAllTextAsync(
            Path.Combine(mailbox, FileSystemCredentialPairStore.ClaimedFileName(email) + FileSystemCredentialPairStore.IncomingSuffix),
            CredentialFiles.Shape("refresh-held").ToJsonString(),
            token);
        // With a journal over it the orphan pass leaves it alone and the crash
        // table owns it, which is what a release interrupted mid-flight is.
        await new WslSwitchJournal(factory.AppData).WriteAsync(
            new WslSwitchJournalEntry(
                SideName.Wsl,
                null,
                null,
                null,
                AccountEmail.Parse(email).Value,
                CredentialFiles.Pair("refresh-held").Fingerprint,
                Path.Combine(factory.ProfilesRoot, email),
                WslSwitchStep.ExportVerified,
                _teeCapturedAt),
            token);
    }

    /// <summary>Puts one on-demand read into the state the page reads, the way a pass does.</summary>
    private static void Record(AppFactory factory, string email, DateTimeOffset capturedAt, params UsageLimit[] limits) =>
        factory.Services.GetRequiredService<QuotaState>().RecordSnapshot(
            new UsageSnapshot(AccountEmail.Parse(email).Value, capturedAt, QuotaSource.OnDemandRefresh, limits, ExtraUsage: null));

    /// <summary>The seven-day window, the one the usable order is keyed on. No reset instant means the endpoint gave none.</summary>
    private static UsageLimit Weekly(double percent, DateTimeOffset? resetsAt = null) =>
        new("weekly_all", LimitKind.WeeklyAll, "weekly", percent, "ok", resetsAt, null, IsActive: true);

    /// <summary>A roster entry for an account the operator has put on the machine but not logged in.</summary>
    private static RosterEntry Entry(string email, bool paused = false) =>
        new(AccountEmail.Parse(email).Value, Paused: paused);

    /// <summary>
    /// Writes the roster the page reads. A roster entry alone is enough for a
    /// card, so an ordering fact needs no profile folder: the numbers come from
    /// the quota state and the pause flag from here.
    /// </summary>
    private static async Task RosterAsync(AppFactory factory, params RosterEntry[] entries)
    {
        using RosterFile roster = new(factory.AppData);
        _ = await roster.UpdateAsync(_ => new Roster(entries), TestContext.Current.CancellationToken);
    }

    /// <summary>A weekly window the endpoint scoped to one model and named itself.</summary>
    private static UsageLimit Scoped(string displayName, double percent, DateTimeOffset resetsAt) =>
        new("weekly_scoped", LimitKind.WeeklyScoped, "weekly", percent, "warning", resetsAt, displayName, IsActive: true);

    /// <summary>Parks a rotated pair in the recovery directory, which is what "stranded" means on a card.</summary>
    private static async Task StrandAsync(AppFactory factory, string folder) =>
        (await factory.Services.GetRequiredService<RecoveryFiles>().WriteAsync(
            folder,
            CredentialFiles.Pair("refresh-b").Fingerprint,
            CredentialFiles.Pair("refresh-rotated"),
            TestContext.Current.CancellationToken)).ShouldBeTrue();

    private static JsonElement Usage(JsonElement card) => card.GetProperty("usage");

    private static JsonElement Limit(JsonElement card, int index) => Usage(card).GetProperty("limits")[index];

    /// <summary>
    /// The tee file shaped like <see cref="RateLimitGuardTeeFileReaderTests.Tee"/>,
    /// but with a session figure that helper cannot produce: the merge fact needs
    /// a session percentage past the exhaustion line, not the helper's fixed 69.
    /// </summary>
    private static string TeeWithSessionPercent(
        string email,
        DateTimeOffset capturedAt,
        double sessionPercent,
        DateTimeOffset sessionResetsAt,
        double weeklyPercent,
        DateTimeOffset weeklyResetsAt) =>
        new JsonObject
        {
            ["captured_at"] = capturedAt.ToString("O"),
            ["session_id"] = "00000000-0000-4000-8000-000000000002",
            ["session_name"] = "a session",
            ["rate_limits"] = new JsonObject
            {
                ["five_hour"] = new JsonObject { ["used_percentage"] = sessionPercent, ["resets_at"] = sessionResetsAt.ToUnixTimeSeconds() },
                ["seven_day"] = new JsonObject { ["used_percentage"] = weeklyPercent, ["resets_at"] = weeklyResetsAt.ToUnixTimeSeconds() },
            },
            ["account"] = new JsonObject { ["email"] = email },
        }.ToJsonString();

    private static async Task WriteTeeAsync(AppFactory factory, string content)
    {
        string path = Path.Combine(factory.LiveDirectory, "rate-limit-guard", "rate-limits.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> LiveCardAsync(AppFactory factory, HttpClient? existing = null)
    {
        HttpClient client = existing ?? factory.CreateClient();
        JsonElement dashboard = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);
        return dashboard.GetProperty("accounts").EnumerateArray().Single(card => card.GetProperty("isLive").GetBoolean());
    }

    private static async Task<JsonElement> CardAsync(AppFactory factory, string email)
    {
        using HttpClient client = factory.CreateClient();
        JsonElement dashboard = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken);
        return Card(dashboard, email);
    }

    private static JsonElement Card(JsonElement dashboard, string email) =>
        dashboard.GetProperty("accounts").EnumerateArray().Single(card => card.GetProperty("email").GetString() == email);

    private static JsonElement Card(Payload dashboard, string email) => Card(dashboard.Element, email);

    private static string? SetupOf(Payload dashboard)
    {
        JsonElement setup = dashboard.Element.GetProperty("setup");
        return setup.ValueKind == JsonValueKind.Null ? null : setup.GetString();
    }

    private static async Task<Payload> DashboardAsync(AppFactory factory)
    {
        using HttpClient client = factory.CreateClient();
        return new Payload(await client.GetStringAsync(new Uri("/api/dashboard", UriKind.Relative), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// One dashboard payload read two ways: as a node tree for the assertions that
    /// need the whole document, and as an element for the per-card reads. Every
    /// string the page renders as a sentence is collected once, because the
    /// assertion that matters is about messages and not about a card's folder,
    /// which is a path by design.
    /// </summary>
    private sealed class Payload(string body)
    {
        public JsonNode Raw { get; } = JsonNode.Parse(body)!;

        public JsonElement Element { get; } = JsonDocument.Parse(body).RootElement.Clone();

        public IEnumerable<string> Messages
        {
            get
            {
                foreach (JsonNode? warning in Raw["warnings"]!.AsArray())
                {
                    yield return warning!.GetValue<string>();
                }

                foreach (JsonNode? card in Raw["accounts"]!.AsArray())
                {
                    if (card!["usageNote"] is JsonNode note)
                    {
                        yield return note.GetValue<string>();
                    }

                    if (card["refresh"]!["message"] is JsonNode message)
                    {
                        yield return message.GetValue<string>();
                    }
                }

                if (Raw["refresh"]!["summary"] is JsonNode summary)
                {
                    yield return summary.GetValue<string>();
                }
            }
        }
    }
}
