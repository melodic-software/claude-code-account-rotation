using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The other side's CLI logged out and left its live file behind with no login
/// in it. The follower reports that as a dead login, and a switch or a release
/// from it finishes in one call: the dead file goes, the holder record naming
/// that side goes, and the slot needs a login. Nothing holding a refresh token
/// is ever removed.
/// </summary>
public sealed class DeadWslLoginTests : IDisposable
{
    private const string Dead = "a@example.com";
    private const string Target = "b@example.com";

    private readonly FollowerRoots _roots = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _roots.Dispose();

    /// <summary>The two files the CLI can leave behind a logout.</summary>
    public static TheoryData<string> DeadFiles() => new(
        new JsonObject { ["claudeAiOauth"] = new JsonObject { ["accessToken"] = string.Empty, ["refreshToken"] = string.Empty, ["expiresAt"] = 0 } }.ToJsonString(),
        new JsonObject { ["mcpOAuth"] = new JsonObject() }.ToJsonString());

    private async Task WriteDeadLiveAsync(string file)
    {
        await File.WriteAllTextAsync(_roots.LivePath, file, Token);
        await _roots.WriteStateFileAsync(Dead, Token);
    }

    /// <summary>The leader's board: the other side holds A, whose slot is empty and names that side; B is parked.</summary>
    private static async Task SetUpLeaderAsync(WslSwitchHarness harness)
    {
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        await harness.ParkedSlotAsync(Target, "refresh-b", Token);
        await harness.IdentifiedSlotAsync(Dead, Token);
        await HolderRecordFile.WriteAsync(
            harness.FolderFor(Dead),
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-a").Fingerprint, harness.Clock.GetUtcNow()),
            Token);
        harness.Side.OutgoingEmail = Dead;
        harness.Side.OutgoingRefreshToken = null;
        harness.Side.LiveLoginDead = true;
    }

    [Theory]
    [MemberData(nameof(DeadFiles))]
    public async Task TheFollowerReportsALiveFileWithNoLoginAsADeadLogin(string file)
    {
        await WriteDeadLiveAsync(file);
        using FollowerImport follower = _roots.Follower();

        ImportStatus status = await follower.StatusAsync(Token);

        status.LiveLoginDead.ShouldBeTrue();
        status.LiveFingerprint.ShouldBeNull();
        status.LiveAccount?.Email?.Value.ShouldBe(Dead);
    }

    [Theory]
    [MemberData(nameof(DeadFiles))]
    public async Task AFollowerSwitchedFromADeadLoginReplacesTheDeadFileWithTheIncomingPair(string file)
    {
        await WriteDeadLiveAsync(file);
        RefreshTokenFingerprint incoming = await _roots.WriteClaimedAsync(Target, "refresh-b", Token);
        using FollowerImport follower = _roots.Follower();

        Result<ImportAnswer, string> answer = await follower.ImportAsync(_roots.Request(Target, incoming, Dead), Token);
        answer.IsSuccess.ShouldBeTrue(answer.IsFailure ? answer.Error : null);
        // Nothing left this side, which is what the leader's plan named.
        answer.Value.ExportedFingerprint.ShouldBeNull();
        answer.Value.Outgoing.ShouldBeNull();
        Result<ImportResult, string> committed = await follower.CommitAsync(new AccountEmail(Target), Token);

        committed.IsSuccess.ShouldBeTrue(committed.IsFailure ? committed.Error : null);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(incoming);
        (await _roots.StateFile().ReadAccountBlockAsync(Token))?.Email?.Value.ShouldBe(Target);
    }

    [Theory]
    [MemberData(nameof(DeadFiles))]
    public async Task AFollowerLogOutOfADeadLoginRemovesItAndForgetsTheAccount(string file)
    {
        await WriteDeadLiveAsync(file);
        using FollowerImport follower = _roots.Follower();

        Result<LogOutAnswer, string> answer = await follower.LogOutAsync(new AccountEmail(Dead), Token);

        answer.IsSuccess.ShouldBeTrue(answer.IsFailure ? answer.Error : null);
        answer.Value.LoggedOut.ShouldBeTrue();
        if (File.Exists(_roots.LivePath))
        {
            JsonNode.Parse(await File.ReadAllTextAsync(_roots.LivePath, Token))!["claudeAiOauth"].ShouldBeNull();
        }

        (await _roots.StateFile().ReadAccountBlockAsync(Token))?.Email.ShouldBeNull();
        (await follower.StatusAsync(Token)).LiveAccount?.Email.ShouldBeNull();
    }

    /// <summary>
    /// The live file is the CLI's, not only the login's: what it keeps beside
    /// the dead block survives the logout, owner-only, and a file with nothing
    /// else in it goes.
    /// </summary>
    [Fact]
    public async Task AFollowerLogOutKeepsEveryOtherKeyInTheLiveFile()
    {
        JsonObject mcp = new() { ["server|abc"] = new JsonObject { ["accessToken"] = "mcp-access", ["refreshToken"] = "mcp-refresh" } };
        await WriteDeadLiveAsync(new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject { ["accessToken"] = string.Empty, ["refreshToken"] = string.Empty, ["expiresAt"] = 0 },
            ["mcpOAuth"] = mcp.DeepClone(),
        }.ToJsonString());
        using FollowerImport follower = _roots.Follower();

        (await follower.LogOutAsync(new AccountEmail(Dead), Token)).Value.LoggedOut.ShouldBeTrue();
        (await follower.LogOutAsync(new AccountEmail(Dead), Token)).Value.LoggedOut.ShouldBeTrue();

        JsonObject left = JsonNode.Parse(await File.ReadAllTextAsync(_roots.LivePath, Token))!.AsObject();
        left["claudeAiOauth"].ShouldBeNull();
        JsonNode.DeepEquals(left["mcpOAuth"], mcp).ShouldBeTrue();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(_roots.LivePath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task AFollowerLogOutSucceedsFromEitherHalfAlreadyDone()
    {
        // The file already gone, the state file still naming the account.
        await _roots.WriteStateFileAsync(Dead, Token);
        using FollowerImport follower = _roots.Follower();
        (await follower.LogOutAsync(new AccountEmail(Dead), Token)).Value.LoggedOut.ShouldBeTrue();
        (await _roots.StateFile().ReadAccountBlockAsync(Token))?.Email.ShouldBeNull();

        // The state file already cleared, the dead file still there.
        await File.WriteAllTextAsync(_roots.LivePath, DeadFiles().First().Data, Token);
        (await follower.LogOutAsync(new AccountEmail(Dead), Token)).Value.LoggedOut.ShouldBeTrue();
        File.Exists(_roots.LivePath).ShouldBeFalse();
    }

    [Fact]
    public async Task AFollowerLogOutOfALivePairIsADefiniteNoAndRemovesNothing()
    {
        RefreshTokenFingerprint live = await _roots.WriteLiveAsync(Dead, "refresh-a", Token);
        using FollowerImport follower = _roots.Follower();

        Result<LogOutAnswer, string> answer = await follower.LogOutAsync(new AccountEmail(Dead), Token);

        answer.IsSuccess.ShouldBeTrue();
        answer.Value.LoggedOut.ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(live);
    }

    /// <summary>
    /// An empty access token beside a live refresh token is not a logout: the
    /// login can still refresh. It is not reported dead and never removed.
    /// </summary>
    [Fact]
    public async Task ARefreshTokenWithNoAccessTokenIsNotADeadLoginAndIsNeverRemoved()
    {
        string file = new JsonObject { ["claudeAiOauth"] = new JsonObject { ["accessToken"] = string.Empty, ["refreshToken"] = "refresh-a", ["expiresAt"] = 0 } }.ToJsonString();
        await File.WriteAllTextAsync(_roots.LivePath, file, Token);
        await _roots.WriteStateFileAsync(Dead, Token);
        using FollowerImport follower = _roots.Follower();

        (await follower.StatusAsync(Token)).LiveLoginDead.ShouldBeFalse();
        (await follower.LogOutAsync(new AccountEmail(Dead), Token)).Value.LoggedOut.ShouldBeFalse();
        (await File.ReadAllTextAsync(_roots.LivePath, Token)).ShouldBe(file);
    }

    /// <summary>
    /// A file that is neither a pair nor a logout answers a definite "no", not
    /// a failure: asking again finds the same file, and a failure would hold the
    /// leader's journal open for good. The release unwinds and the record stays.
    /// </summary>
    [Fact]
    public async Task AReleaseOfAFileThatIsNeitherAPairNorALogoutUnwindsInsteadOfWedging()
    {
        string file = new JsonObject { ["claudeAiOauth"] = new JsonObject { ["accessToken"] = string.Empty, ["refreshToken"] = 42, ["expiresAt"] = 0 } }.ToJsonString();
        await File.WriteAllTextAsync(_roots.LivePath, file, Token);
        await _roots.WriteStateFileAsync(Dead, Token);
        using FollowerImport follower = _roots.Follower();
        using WslSwitchHarness harness = new();
        await SetUpLeaderAsync(harness);
        harness.Side.OnLogOut = () => follower.LogOutAsync(new AccountEmail(Dead), Token);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SwitchRefusal.SideLoggedInAgain);
        File.Exists(harness.JournalPath).ShouldBeFalse();
        File.Exists(harness.RecordPath(Dead)).ShouldBeTrue();
        (await File.ReadAllTextAsync(_roots.LivePath, Token)).ShouldBe(file);
    }

    [Fact]
    public async Task AFollowerSwitchNeverReplacesALiveFileItCouldNotRead()
    {
        await File.WriteAllTextAsync(_roots.LivePath, "{\"claudeAiOauth\":{\"refreshToken\":\"refresh-a\"", Token);
        RefreshTokenFingerprint incoming = await _roots.WriteClaimedAsync(Target, "refresh-b", Token);
        using FollowerImport follower = _roots.Follower();
        (await follower.ImportAsync(_roots.Request(Target, incoming, Dead), Token)).IsSuccess.ShouldBeTrue();

        Result<ImportResult, string> committed = await follower.CommitAsync(new AccountEmail(Target), Token);

        committed.IsFailure.ShouldBeTrue();
        (await File.ReadAllTextAsync(_roots.LivePath, Token)).ShouldContain("refresh-a");
        File.Exists(_roots.ClaimedPath(Target)).ShouldBeTrue();
    }

    [Fact]
    public async Task ASwitchFromADeadLoginFinishesAndLeavesThatSlotNeedingALogin()
    {
        using WslSwitchHarness harness = new();
        await SetUpLeaderAsync(harness);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Target), quarantineForeignFamily: false, Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        result.Value.Now.ShouldBe(WslSwitchHarness.Email(Target));
        result.Value.ParkedAs.ShouldBeNull();
        result.Value.LoggedOut.ShouldBe(WslSwitchHarness.Email(Dead));
        File.Exists(harness.RecordPath(Dead)).ShouldBeFalse();
        File.Exists(harness.PairPath(Dead)).ShouldBeFalse();
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Target), Token))!.Side.ShouldBe(SideName.Wsl);
        harness.Side.Calls.ShouldBe(["Dashboard", "Import", "Commit"]);
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task AReleaseFromADeadLoginLogsThatSideOutAndLeavesThatSlotNeedingALogin()
    {
        using WslSwitchHarness harness = new();
        await SetUpLeaderAsync(harness);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        result.Value.Now.ShouldBeNull();
        result.Value.ParkedAs.ShouldBeNull();
        result.Value.LoggedOut.ShouldBe(WslSwitchHarness.Email(Dead));
        File.Exists(harness.RecordPath(Dead)).ShouldBeFalse();
        harness.Side.Calls.ShouldBe(["Dashboard", "LogOut"]);
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    /// <summary>
    /// The dashboard read is outside the gate. A side that logged in again
    /// before the import names the account leaving, and the plan said none:
    /// the switch is refused and the record naming that side stays.
    /// </summary>
    [Fact]
    public async Task AStaleDeadReadingRefusesTheSwitchAndKeepsTheHolderRecord()
    {
        using WslSwitchHarness harness = new();
        await SetUpLeaderAsync(harness);
        harness.Side.OnImport = async request =>
        {
            await File.WriteAllTextAsync(request.ExportPath, CredentialFiles.Shape("refresh-a2").ToJsonString(), Token);
            return Result<ImportAnswer, string>.Success(new ImportAnswer(CredentialFiles.Pair("refresh-a2").Fingerprint, WslSwitchHarness.Email(Dead), false, null));
        };
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Target), quarantineForeignFamily: false, Token);

        result.IsFailure.ShouldBeTrue();
        File.Exists(harness.RecordPath(Dead)).ShouldBeTrue();
        (await CredentialFiles.ReadFingerprintAsync(harness.PairPath(Target), Token)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task AReleaseThatSideRefusesBecauseItIsLiveAgainKeepsTheHolderRecord()
    {
        using WslSwitchHarness harness = new();
        await SetUpLeaderAsync(harness);
        harness.Side.OnLogOut = static () => Task.FromResult(Result<LogOutAnswer, string>.Success(new LogOutAnswer(false, "not logged out: a live pair is here again")));
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SwitchRefusal.SideLoggedInAgain);
        File.Exists(harness.RecordPath(Dead)).ShouldBeTrue();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    /// <summary>
    /// A logout whose answer never came back may have happened. The journal
    /// stays open and the record stays, and the next poll asks again and
    /// finishes it; clearing on silence would strand the record for good.
    /// </summary>
    [Fact]
    public async Task ALostLogOutAnswerLeavesTheJournalOpenAndTheNextPollFinishes()
    {
        using WslSwitchHarness harness = new();
        await SetUpLeaderAsync(harness);
        harness.Side.OnLogOut = static () => Task.FromResult(Result<LogOutAnswer, string>.Failure("/api/logout did not answer"));
        using (WslSwitch coordinator = harness.Coordinator())
        {
            Result<WslSwitchOutcome, SwitchRefusal> result =
                await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);
            result.IsFailure.ShouldBeTrue();
        }

        WslSwitchJournalEntry? open = await harness.Journal.ReadOpenAsync(Token);
        open!.StepReached.ShouldBe(WslSwitchStep.Claimed);
        open.LoggedOut.ShouldBe(WslSwitchHarness.Email(Dead));
        File.Exists(harness.RecordPath(Dead)).ShouldBeTrue();

        harness.Side.OnLogOut = null;
        using WslSwitch restarted = harness.Coordinator();
        WslReconciliation reconciled = await restarted.ReconcileAsync(Token);

        reconciled.Banner.ShouldBeNull();
        File.Exists(harness.RecordPath(Dead)).ShouldBeFalse();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ASwitchResumedAfterACrashPastTheCommitDropsTheDeadHolderRecord()
    {
        using WslSwitchHarness harness = new();
        await SetUpLeaderAsync(harness);
        RefreshTokenFingerprint incoming = CredentialFiles.Pair("refresh-b").Fingerprint;
        File.Delete(harness.PairPath(Target));
        await HolderRecordFile.WriteAsync(harness.FolderFor(Target), new HolderRecord(SideName.Wsl, incoming, harness.Clock.GetUtcNow()), Token);
        await harness.Journal.WriteAsync(
            new WslSwitchJournalEntry(
                SideName.Wsl,
                WslSwitchHarness.Email(Target),
                incoming,
                harness.FolderFor(Target),
                null,
                null,
                null,
                WslSwitchStep.Imported,
                harness.Clock.GetUtcNow(),
                LoggedOut: WslSwitchHarness.Email(Dead)),
            Token);
        using WslSwitch coordinator = harness.Coordinator();

        WslReconciliation reconciled = await coordinator.ReconcileAsync(Token);

        reconciled.Banner.ShouldBeNull();
        File.Exists(harness.RecordPath(Dead)).ShouldBeFalse();
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Target), Token))!.Side.ShouldBe(SideName.Wsl);
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }
}
