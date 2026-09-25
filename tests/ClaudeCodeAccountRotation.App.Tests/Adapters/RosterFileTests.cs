using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class RosterFileTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    public void Dispose()
    {
        if (Directory.Exists(_appData))
        {
            Directory.Delete(_appData, recursive: true);
        }
    }

    [Fact]
    public async Task AnAbsentFileReadsAsAnEmptyRoster()
    {
        using RosterFile file = new(_appData);

        (await file.ReadAsync(TestContext.Current.CancellationToken)).Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task EveryFieldRoundTrips()
    {
        using RosterFile file = new(_appData);
        RosterEntry entry = new(
            Email("a@example.com"),
            "work",
            BrowserFamily.Brave,
            "Profile 3",
            Paused: true,
            Notes: "the weekly one",
            CiTokenGeneratedOn: new DateOnly(2026, 1, 15));

        await file.UpdateAsync(roster => roster.With(entry), TestContext.Current.CancellationToken);

        RosterEntry? stored = (await file.ReadAsync(TestContext.Current.CancellationToken)).Find(Email("a@example.com"));
        stored.ShouldBe(entry);
    }

    [Fact]
    public async Task AnEntryWithNoCiTokenDateReadsAsNull()
    {
        using RosterFile file = new(_appData);
        RosterEntry entry = new(Email("a@example.com"));

        await file.UpdateAsync(roster => roster.With(entry), TestContext.Current.CancellationToken);

        RosterEntry? stored = (await file.ReadAsync(TestContext.Current.CancellationToken)).Find(Email("a@example.com"));
        stored!.CiTokenGeneratedOn.ShouldBeNull();
    }

    [Fact]
    public async Task ARosterFileWrittenBeforeTheCiTokenFieldExistedStillLoads()
    {
        Directory.CreateDirectory(_appData);
        await File.WriteAllTextAsync(
            Path.Combine(_appData, RosterFile.FileName),
            """{ "accounts": [ { "email": "a@example.com", "alias": "work", "paused": false } ] }""",
            TestContext.Current.CancellationToken);
        using RosterFile file = new(_appData);

        RosterEntry? stored = (await file.ReadAsync(TestContext.Current.CancellationToken)).Find(Email("a@example.com"));

        stored.ShouldNotBeNull();
        stored.Alias.ShouldBe("work");
        stored.CiTokenGeneratedOn.ShouldBeNull();
    }

    [Fact]
    public async Task AnUnparsableFileReadsAsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(_appData);
        await File.WriteAllTextAsync(Path.Combine(_appData, RosterFile.FileName), "{ not json", TestContext.Current.CancellationToken);
        using RosterFile file = new(_appData);

        (await file.ReadAsync(TestContext.Current.CancellationToken)).Entries.ShouldBeEmpty();
    }
}
