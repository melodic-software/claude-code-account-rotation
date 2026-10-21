using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>What Generate CI token records on the roster: the role's token half, to the instant.</summary>
public sealed class ClaudeCliSetupTokenRunnerRoleTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _appData = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly SetupTokenScript _script = new();
    private readonly RecordingSecretWriter _secrets = new();
    private readonly RosterFile _roster;

    public ClaudeCliSetupTokenRunnerRoleTests()
    {
        Directory.CreateDirectory(_appData);
        _roster = new RosterFile(_appData);
    }

    public void Dispose()
    {
        _roster.Dispose();
        Directory.Delete(_appData, recursive: true);
    }

    [Fact]
    public async Task GeneratingATokenRecordsItsHalfAtTheClocksInstantAndKeepsTheKeyHalf()
    {
        AccountEmail email = AccountEmail.Parse("ci@example.com").Value;
        CiRole automations = new("AUTOMATIONS", CiSecretScope.Organization, "octo");
        DateOnly keySetOn = new(2026, 9, 1);
        await _roster.UpdateAsync(
            roster => roster.With(new RosterEntry(email) { CiRoles = [new CiRoleAssignment(automations, KeySetOn: keySetOn)] }),
            TestContext.Current.CancellationToken);
        UtcClock clock = new(_now);
        using ClaudeCliSetupTokenRunner runner = new(_script.Start, _secrets, _roster, clock, new RecordingLogger<ClaudeCliSetupTokenRunner>(), TimeSpan.FromSeconds(10));
        CiTokenSecret secret = CiTokenSecret.Parse("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", null, "octo").Value;

        LoginSession started = (await runner.StartAsync(new CiTokenRequest(email, secret, OrgSecretVisibility.All, []), TestContext.Current.CancellationToken)).Value;
        await runner.SubmitCodeAsync(started.Id, "code#state", TestContext.Current.CancellationToken);
        await runner.FinishedAsync(started.Id);

        RosterEntry entry = (await _roster.ReadAsync(TestContext.Current.CancellationToken)).Find(email)!;
        CiRoleAssignment role = entry.CiRoles.Single();
        role.Role.ShouldBe(automations);
        role.TokenSetAt.ShouldNotBeNull();
        (role.TokenSetAt.Value - _now).Duration().ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
        role.TokenSetOn.ShouldBe(new DateOnly(2026, 10, 20));
        role.KeySetOn.ShouldBe(keySetOn);
        entry.CiTokenGeneratedOn.ShouldBe(new DateOnly(2026, 10, 20));
        string history = await File.ReadAllTextAsync(Path.Combine(_appData, LaneHistoryFile.FileName), TestContext.Current.CancellationToken);
        history.ShouldContain("\"via\":\"assign\"");
        history.ShouldNotContain(SetupTokenScript.Token);
    }

    [Fact]
    public async Task AnAccountRemovedWhileItsSessionRanIsNotPutBackAndTheHalfItSetLeavesThePreviousHolder()
    {
        AccountEmail email = AccountEmail.Parse("ci@example.com").Value;
        AccountEmail previous = AccountEmail.Parse("previous@example.com").Value;
        CiRole automations = new("AUTOMATIONS", CiSecretScope.Organization, "octo");
        CiRole reviews = new("REVIEWS", CiSecretScope.Organization, "octo");
        await _roster.UpdateAsync(
            roster => roster
                .With(new RosterEntry(email))
                .With(new RosterEntry(previous).ClaimingToken(automations, new DateOnly(2026, 1, 1), null).ClaimingToken(reviews, new DateOnly(2026, 1, 1), null)),
            TestContext.Current.CancellationToken);
        using ClaudeCliSetupTokenRunner runner = new(_script.Start, _secrets, _roster, new UtcClock(_now), new RecordingLogger<ClaudeCliSetupTokenRunner>(), TimeSpan.FromSeconds(10));
        CiTokenSecret secret = CiTokenSecret.Parse("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN", null, "octo").Value;

        LoginSession started = (await runner.StartAsync(new CiTokenRequest(email, secret, OrgSecretVisibility.All, []), TestContext.Current.CancellationToken)).Value;
        await _roster.UpdateAsync(roster => roster.Without(email), TestContext.Current.CancellationToken);
        await runner.SubmitCodeAsync(started.Id, "code#state", TestContext.Current.CancellationToken);
        await runner.FinishedAsync(started.Id);

        LoginSession settled = runner.Status(started.Id)!;
        settled.State.ShouldBe(LoginSessionState.Completed);
        settled.Message!.ShouldContain("the roster was not updated because that account was removed");
        Roster stored = await _roster.ReadAsync(TestContext.Current.CancellationToken);
        stored.Find(email).ShouldBeNull();
        stored.Find(previous)!.CiRoles.ShouldBe([new CiRoleAssignment(reviews, new DateOnly(2026, 1, 1))]);
    }

    /// <summary>A fixed clock whose local zone is UTC, so the day it reads is the same on every machine.</summary>
    private sealed class UtcClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
