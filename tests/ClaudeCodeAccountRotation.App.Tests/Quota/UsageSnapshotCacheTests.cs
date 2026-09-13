using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>
/// The file that lets a card keep its numbers across a restart. The operator
/// restarts the tool to install a build, and a pass costs the rate window, so
/// the alternative to this file is ten cards reading "unknown" until the window
/// pays for them again.
/// <para>
/// Two properties matter more than the round trip itself. The file holds
/// percentages and instants and nothing else, because it is a second on-disk
/// artefact written beside credential files and must never become one; and
/// every read of it is tolerant, because it is read at startup, where an
/// unreadable file must cost its own contents rather than the tool.
/// </para>
/// </summary>
public sealed class UsageSnapshotCacheTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASavedSnapshotComesBackCachedWithItsOriginalCaptureTime()
    {
        using CacheFixture fixture = new();
        DateTimeOffset capturedA = _now.AddHours(-3);
        DateTimeOffset capturedB = _now.AddMinutes(-20);
        await fixture.Cache.SaveAsync(Email("a@example.com"), Snapshot("a@example.com", capturedA, Session(43)));
        await fixture.Cache.SaveAsync(Email("b@example.com"), Snapshot("b@example.com", capturedB, Scoped(61)));

        IReadOnlyList<UsageSnapshot> loaded = await fixture.Cache.LoadAsync(Token);

        loaded.Count.ShouldBe(2);
        UsageSnapshot a = loaded.Single(snapshot => snapshot.Account.Value == "a@example.com");
        // Cached, not the source that captured it: the card says "via cached" so
        // the operator knows the figure predates this run of the tool.
        a.Source.ShouldBe(QuotaSource.Cached);
        // The capture time is the original, never the load time, or "as of" would
        // claim the numbers were read at startup.
        a.CapturedAt.ShouldBe(capturedA);
        UsageLimit limit = a.Limits.Single();
        limit.RawKind.ShouldBe("session");
        limit.Kind.ShouldBe(LimitKind.Session);
        limit.Group.ShouldBe("session");
        limit.Percent.ShouldBe(43);
        limit.Severity.ShouldBe("ok");
        limit.ResetsAt.ShouldBe(_now.AddHours(2));
        limit.IsActive.ShouldBeTrue();
        a.ExtraUsage!.IsEnabled.ShouldBeTrue();
        a.ExtraUsage.DisabledReason.ShouldBe("not_configured");
        a.ExtraUsage.SpendLimitReached.ShouldBeFalse();

        UsageSnapshot b = loaded.Single(snapshot => snapshot.Account.Value == "b@example.com");
        b.CapturedAt.ShouldBe(capturedB);
        b.Limits.Single().ScopeDisplayName.ShouldBe("Fable");
        b.Limits.Single().Kind.ShouldBe(LimitKind.WeeklyScoped);
    }

    [Fact]
    public async Task ATornFileLoadsNothing()
    {
        // The file is written atomically, so a torn one means something else went
        // wrong: a crash mid-rename on an older build, a disk error, a hand edit.
        // Whatever it was, startup reads this and must survive it.
        using CacheFixture fixture = new();
        await fixture.WriteRawAsync("""{"a@example.com": {"capturedAt": "2026-09-07T09:0""");

        IReadOnlyList<UsageSnapshot> loaded = await fixture.Cache.LoadAsync(Token);

        loaded.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASecondSaveForOneAccountReplacesItsEntryAndKeepsTheOther()
    {
        // One pass saves each account as its read lands, so the merge is the whole
        // point of the file: a save that wrote only its own account would leave
        // the other nine cards unknown after a restart.
        using CacheFixture fixture = new();
        await fixture.Cache.SaveAsync(Email("a@example.com"), Snapshot("a@example.com", _now.AddHours(-3), Session(43)));
        await fixture.Cache.SaveAsync(Email("b@example.com"), Snapshot("b@example.com", _now.AddHours(-2), Session(12)));
        await fixture.Cache.SaveAsync(Email("a@example.com"), Snapshot("a@example.com", _now, Session(77)));

        IReadOnlyList<UsageSnapshot> loaded = await fixture.Cache.LoadAsync(Token);

        loaded.Count.ShouldBe(2);
        UsageSnapshot a = loaded.Single(snapshot => snapshot.Account.Value == "a@example.com");
        a.Limits.Single().Percent.ShouldBe(77);
        a.CapturedAt.ShouldBe(_now);
        loaded.Single(snapshot => snapshot.Account.Value == "b@example.com").Limits.Single().Percent.ShouldBe(12);
    }

    [Fact]
    public async Task AnEntryWhosePercentIsOutsideTheScaleIsNotLoaded()
    {
        // The whole entry goes, not just the row: unlike the statusline tee, whose
        // windows are independent observations, an entry here is one read of one
        // account, and a file this tool wrote that no longer adds up is not a file
        // to take the readable half of. The other account is untouched, because a
        // tolerant read costs its own entry and never the rest.
        using CacheFixture fixture = new();
        await fixture.WriteRawAsync("""
            {
              "a@example.com": {
                "capturedAt": "2026-09-07T09:00:00.0000000+00:00",
                "source": "OnDemandRefresh",
                "limits": [
                  { "rawKind": "session", "kind": "Session", "group": null, "percent": 140, "severity": null, "resetsAt": null, "scopeDisplayName": null, "isActive": true }
                ],
                "extraUsage": null
              },
              "b@example.com": {
                "capturedAt": "2026-09-07T09:00:00.0000000+00:00",
                "source": "OnDemandRefresh",
                "limits": [
                  { "rawKind": "session", "kind": "Session", "group": null, "percent": 12, "severity": null, "resetsAt": null, "scopeDisplayName": null, "isActive": true }
                ],
                "extraUsage": null
              }
            }
            """);

        IReadOnlyList<UsageSnapshot> loaded = await fixture.Cache.LoadAsync(Token);

        loaded.Single().Account.Value.ShouldBe("b@example.com");
    }

    [Fact]
    public async Task APassThatRotatedAPairLeavesNoTokenInTheCacheFile()
    {
        // The file sits in the same app data as the recovery files, which do hold
        // credentials. This one never may: it is written on the same code path as
        // a token rotation, moments after the engine held both halves of a
        // credential pair in memory, and the scripted rotation mints tokens with
        // the prefixes every secrets assertion in this repository looks for.
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(1);
        string cached = await File.ReadAllTextAsync(
            Path.Combine(harness.AppData, "state", UsageSnapshotCache.FileName),
            Token);
        // The read really did land in the file, so the assertions below are about
        // a populated cache and not about an empty one.
        cached.ShouldContain("a@example.com");
        cached.ShouldContain("43");
        cached.ShouldNotContain("refresh-");
        cached.ShouldNotContain("access-");
    }

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    private static UsageLimit Session(double percent) =>
        new("session", LimitKind.Session, "session", percent, "ok", _now.AddHours(2), null, IsActive: true);

    private static UsageLimit Scoped(double percent) =>
        new("weekly_scoped", LimitKind.WeeklyScoped, "weekly", percent, "warning", _now.AddDays(4), "Fable", IsActive: true);

    private static UsageSnapshot Snapshot(string email, DateTimeOffset capturedAt, params UsageLimit[] limits) =>
        new(Email(email), capturedAt, QuotaSource.OnDemandRefresh, limits, new ExtraUsageState(IsEnabled: true, "not_configured", SpendLimitReached: false));

    /// <summary>One cache over a temp app-data directory, deleted with the test.</summary>
    private sealed class CacheFixture : IDisposable
    {
        public CacheFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Cache = new UsageSnapshotCache(new SwitchOptions(
                Path.Combine(Root, "live"),
                Path.Combine(Root, ".claude.json"),
                Path.Combine(Root, "profiles"),
                Root,
                TimeSpan.FromSeconds(2),
                MutationGateTimeout: TimeSpan.Zero));
        }

        public string Root { get; }

        public UsageSnapshotCache Cache { get; }

        /// <summary>The file the cache owns, for the facts about what is in it and what a torn one does.</summary>
        public string FilePath => Path.Combine(Root, "state", UsageSnapshotCache.FileName);

        /// <summary>Puts bytes in the cache's place that the cache did not write: a torn file, or one edited by hand.</summary>
        public async Task WriteRawAsync(string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(FilePath, content, TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
