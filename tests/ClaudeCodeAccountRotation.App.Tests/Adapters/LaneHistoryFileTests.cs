using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// The lane history: which account backed which half of which role, appended
/// by every roster update and read back by the page and the limit-stop warning.
/// </summary>
public sealed class LaneHistoryFileTests : IDisposable
{
    private static readonly CiRole _pair = new("AUTOMATIONS", CiSecretScope.Organization, "octo");
    private static readonly CiRole _tokenOnly = new(string.Empty, CiSecretScope.Organization, "octo");
    private static readonly CiRole _repoRole = new("LANE", CiSecretScope.Repository, "octo/one");

    private readonly string _appData = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));

    private string HistoryPath => Path.Combine(_appData, LaneHistoryFile.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_appData))
        {
            Directory.Delete(_appData, recursive: true);
        }
    }

    private static RosterEntry Holding(string email, CiRole role, bool token = true, bool key = true, string? notes = null)
    {
        DateOnly day = new(2026, 9, 30);
        RosterEntry entry = new(AccountEmail.Parse(email).Value, Alias: notes, Browser: null, BrowserProfileDirectory: null, Paused: false, Notes: notes);
        return entry.WithCiRoles([new CiRoleAssignment(role, token ? day : null, null, key && role.HasKeyHalf ? day : null, null)]);
    }

    private async Task<string[]> LinesAsync() =>
        (await File.ReadAllTextAsync(HistoryPath, TestContext.Current.CancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task MovingARoleFromOneAccountToAnotherAppendsOneLinePerHalf()
    {
        using RosterFile file = new(_appData, _clock);
        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _pair)), TestContext.Current.CancellationToken, via: "assign");
        _clock.Advance(TimeSpan.FromHours(1));

        await file.UpdateAsync(roster => roster.With(Holding("b@example.com", _pair)), TestContext.Current.CancellationToken, via: "push");

        string[] lines = await LinesAsync();
        lines.Length.ShouldBe(4);
        JsonObject token = JsonNode.Parse(lines[2])!.AsObject();
        JsonObject key = JsonNode.Parse(lines[3])!.AsObject();
        token["at"]!.GetValue<string>().ShouldBe("2026-10-01T11:00:00.0000000Z");
        token["role"]!.AsObject()["prefix"]!.GetValue<string>().ShouldBe("AUTOMATIONS");
        token["role"]!.AsObject()["organization"]!.GetValue<string>().ShouldBe("octo");
        token["half"]!.GetValue<string>().ShouldBe("token");
        token["account"]!.GetValue<string>().ShouldBe("b@example.com");
        token["previous"]!.GetValue<string>().ShouldBe("a@example.com");
        token["via"]!.GetValue<string>().ShouldBe("push");
        key["half"]!.GetValue<string>().ShouldBe("key");
        key["account"]!.GetValue<string>().ShouldBe("b@example.com");
        key["previous"]!.GetValue<string>().ShouldBe("a@example.com");
    }

    [Fact]
    public async Task ATokenOnlyRoleAppendsOneLine()
    {
        using RosterFile file = new(_appData, _clock);

        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _tokenOnly)), TestContext.Current.CancellationToken);

        string[] lines = await LinesAsync();
        lines.Length.ShouldBe(1);
        JsonObject line = JsonNode.Parse(lines[0])!.AsObject();
        line["half"]!.GetValue<string>().ShouldBe("token");
        line["account"]!.GetValue<string>().ShouldBe("a@example.com");
        line["previous"].ShouldBeNull();
        line["role"]!.AsObject()["prefix"]!.GetValue<string>().ShouldBe(string.Empty);
        line["via"]!.GetValue<string>().ShouldBe("edit");
    }

    [Fact]
    public async Task ARepositoryRoleNamesItsRepository()
    {
        using RosterFile file = new(_appData, _clock);

        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _repoRole, key: false)), TestContext.Current.CancellationToken);

        JsonObject role = JsonNode.Parse((await LinesAsync())[0])!.AsObject()["role"]!.AsObject();
        role["repository"]!.GetValue<string>().ShouldBe("octo/one");
        role.ContainsKey("organization").ShouldBeFalse();
    }

    [Fact]
    public async Task RemovingTheAccountThatBacksARoleRecordsTheHalvesWithNoAccount()
    {
        using RosterFile file = new(_appData, _clock);
        AccountEmail a = AccountEmail.Parse("a@example.com").Value;
        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _pair)), TestContext.Current.CancellationToken);

        await file.UpdateAsync(roster => roster.Without(a), TestContext.Current.CancellationToken, via: "delete");

        string[] lines = await LinesAsync();
        lines.Length.ShouldBe(4);
        JsonObject gone = JsonNode.Parse(lines[2])!.AsObject();
        gone["account"].ShouldBeNull();
        gone["previous"]!.GetValue<string>().ShouldBe("a@example.com");
        gone["via"]!.GetValue<string>().ShouldBe("delete");
    }

    [Fact]
    public async Task AnUpdateThatChangesNoRoleHalfAppendsNothing()
    {
        using RosterFile file = new(_appData, _clock);
        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _pair)), TestContext.Current.CancellationToken);

        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _pair, notes: "renamed")), TestContext.Current.CancellationToken);

        (await LinesAsync()).Length.ShouldBe(2);
    }

    [Fact]
    public async Task EveryWrittenLineCarriesOnlyTheAllowedNamesAndDates()
    {
        using RosterFile file = new(_appData, _clock);
        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _pair, notes: "sk-ant-oat01-planted")), TestContext.Current.CancellationToken, via: "assign");
        await file.UpdateAsync(roster => roster.With(Holding("b@example.com", _repoRole, key: false)), TestContext.Current.CancellationToken, via: "push");
        await file.UpdateAsync(roster => roster.With(Holding("c@example.com", _pair)), TestContext.Current.CancellationToken, via: "edit");
        await file.UpdateAsync(roster => roster.Without(AccountEmail.Parse("c@example.com").Value), TestContext.Current.CancellationToken, via: "delete");

        string[] lines = await LinesAsync();

        lines.Length.ShouldBeGreaterThan(4);
        foreach (string line in lines)
        {
            JsonObject entry = JsonNode.Parse(line)!.AsObject();
            entry.Select(static pair => pair.Key).Order().ShouldBe(["account", "at", "half", "previous", "role", "via"]);
            entry["role"]!.AsObject().Select(static pair => pair.Key).ShouldBeSubsetOf(["prefix", "organization", "repository", "tokenSecret"]);
            entry["half"]!.GetValue<string>().ShouldBeOneOf("token", "key");
            entry["via"]!.GetValue<string>().ShouldBeOneOf("assign", "push", "edit", "delete");
            foreach (string name in new[] { "account", "previous" })
            {
                if (entry[name] is JsonNode value)
                {
                    AccountEmail.Parse(value.GetValue<string>()).IsSuccess.ShouldBeTrue();
                }
            }
        }
    }

    [Fact]
    public async Task AHistoryAppendThatFailsLeavesTheRosterWrittenAndLogsAWarning()
    {
        RecordingLogger<RosterFile> logger = new();
        using RosterFile file = new(_appData, _clock, logger);
        Directory.CreateDirectory(HistoryPath);

        Roster updated = await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _tokenOnly)), TestContext.Current.CancellationToken);

        updated.Find(AccountEmail.Parse("a@example.com").Value).ShouldNotBeNull();
        (await file.ReadAsync(TestContext.Current.CancellationToken)).Find(AccountEmail.Parse("a@example.com").Value).ShouldNotBeNull();
        string line = logger.Lines.Single();
        line.ShouldContain(LaneHistoryFile.FileName);
        line.ShouldContain(nameof(UnauthorizedAccessException));
        line.ShouldNotContain("a@example.com");
    }

    [Fact]
    public async Task ATruncatedLastLineIsSkippedAndEveryEarlierLineIsRead()
    {
        using RosterFile file = new(_appData, _clock);
        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _pair)), TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(HistoryPath, "{\"at\":\"2026-10-01T12:00:00.0000000Z\",\"role\":{\"pre", TestContext.Current.CancellationToken);

        IReadOnlyList<LaneHistoryEntry> entries = await file.History.ReadAsync(since: null, TestContext.Current.CancellationToken);

        entries.Count.ShouldBe(2);
        entries.Select(static entry => entry.Half).ShouldBe(["token", "key"]);
    }

    [Fact]
    public async Task AnAppendAfterATruncatedLineIsStillRead()
    {
        using RosterFile file = new(_appData, _clock);
        Directory.CreateDirectory(_appData);
        await File.WriteAllTextAsync(HistoryPath, "{\"at\":\"2026-10-01", TestContext.Current.CancellationToken);

        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _tokenOnly)), TestContext.Current.CancellationToken);

        IReadOnlyList<LaneHistoryEntry> entries = await file.History.ReadAsync(since: null, TestContext.Current.CancellationToken);
        entries.Single().Account.ShouldBe("a@example.com");
    }

    [Fact]
    public async Task ALineWithAnUnknownRouteIsSkipped()
    {
        using RosterFile file = new(_appData, _clock);
        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _tokenOnly)), TestContext.Current.CancellationToken, via: LaneVia.Assign);
        string good = (await LinesAsync())[0];
        await File.AppendAllTextAsync(HistoryPath, good.Replace("\"assign\"", "\"teleport\"", StringComparison.Ordinal) + "\n", TestContext.Current.CancellationToken);

        IReadOnlyList<LaneHistoryEntry> entries = await file.History.ReadAsync(since: null, TestContext.Current.CancellationToken);

        entries.Single().Via.ShouldBe("assign");
    }

    [Fact]
    public async Task AMissingFileReadsAsEmpty()
    {
        using RosterFile file = new(_appData, _clock);

        (await file.History.ReadAsync(since: null, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SinceKeepsOnlyEntriesAtOrAfterIt()
    {
        using RosterFile file = new(_appData, _clock);
        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _tokenOnly)), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(3));
        await file.UpdateAsync(roster => roster.With(Holding("b@example.com", _tokenOnly)), TestContext.Current.CancellationToken);

        IReadOnlyList<LaneHistoryEntry> entries = await file.History.ReadAsync(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        entries.Single().Account.ShouldBe("b@example.com");
    }

    [Fact]
    public async Task AnUpdateWithoutAViaIsRecordedAsAnEdit()
    {
        using RosterFile file = new(_appData, _clock);

        await file.UpdateAsync(roster => roster.With(Holding("a@example.com", _tokenOnly)), TestContext.Current.CancellationToken);

        (await file.History.ReadAsync(since: null, TestContext.Current.CancellationToken)).Single().Via.ShouldBe("edit");
    }
}
