using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Tests.Adapters;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>Puts one on-demand read into the state the page reads, the way a pass does.</summary>
    private static void Record(AppFactory factory, string email, DateTimeOffset capturedAt, params UsageLimit[] limits) =>
        factory.Services.GetRequiredService<QuotaState>().RecordSnapshot(
            new UsageSnapshot(AccountEmail.Parse(email).Value, capturedAt, QuotaSource.OnDemandRefresh, limits, ExtraUsage: null));

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
        return dashboard.GetProperty("accounts").EnumerateArray().Single(card => card.GetProperty("email").GetString() == email);
    }

    private static JsonElement Card(Payload dashboard, string email) =>
        dashboard.Element.GetProperty("accounts").EnumerateArray().Single(card => card.GetProperty("email").GetString() == email);

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
