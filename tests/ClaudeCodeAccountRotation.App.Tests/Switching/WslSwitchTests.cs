using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The leader's coordinator against a scripted other side: L1's refusals, the
/// export gate of L3b, and every row of design 9.3's leader crash table. Each
/// fact is settled by what the store holds afterwards, because the return value
/// is only the coordinator's own account of what it did.
/// </summary>
public sealed class WslSwitchTests
{
    private const string Incoming = "b@example.com";
    private const string Outgoing = "a@example.com";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<RefreshTokenFingerprint?> FingerprintAtAsync(string path) =>
        CredentialFiles.ReadFingerprintAsync(path, Token);

    /// <summary>
    /// The board every hand-off fact starts from: this side live on its own
    /// account, the incoming account parked in the store, and the other side
    /// holding the outgoing one.
    /// </summary>
    private static async Task<RefreshTokenFingerprint> SetUpAsync(WslSwitchHarness harness)
    {
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        return await harness.ParkedSlotAsync(Incoming, "refresh-b", Token);
    }

    [Fact]
    public async Task ASwitchOfTheOtherSideClaimsVerifiesCommitsAndParksTheOutgoingPair()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint incoming = await SetUpAsync(harness);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        result.Value.Now.ShouldBe(WslSwitchHarness.Email(Incoming));
        result.Value.ParkedAs.ShouldBe(WslSwitchHarness.Email(Outgoing));

        // The incoming account's slot: a record naming the side that now holds
        // the pair, and no pair, because a pair is moved and never copied.
        HolderRecord? record = await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token);
        record!.Side.ShouldBe(SideName.Wsl);
        record.Fingerprint.ShouldBe(incoming);
        File.Exists(harness.PairPath(Incoming)).ShouldBeFalse();

        // The outgoing account's slot: the export promoted into it, its identity
        // written from the block the commit handed back, and no record left.
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        File.Exists(harness.RecordPath(Outgoing)).ShouldBeFalse();
        JsonNode profile = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(harness.FolderFor(Outgoing), ProfileFolderStore.ProfileFileName),
            Token))!;
        profile["emailAddress"]!.GetValue<string>().ShouldBe(Outgoing);

        harness.MailboxFiles().ShouldBeEmpty();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ASideThatHeldNothingSkipsTheExportGateAndStillCommits()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        harness.Side.OutgoingEmail = null;
        harness.Side.OutgoingRefreshToken = null;
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        result.Value.ParkedAs.ShouldBeNull();
        // The native read is not a call to the other side, so the proof that no
        // gate ran is that the commit was sent with no export on disk to verify:
        // a gate that had run against nothing would have aborted instead.
        harness.Side.Calls.ShouldBe(["Dashboard", "Import", "Commit"]);
        harness.MailboxFiles().ShouldBeEmpty();
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token))!.Side.ShouldBe(SideName.Wsl);
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task AnImportThatWasAlreadyDoneSendsNoCommitAndStillParks()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        harness.Side.OnImport = async request =>
        {
            // F1 answered from its own record: the swap ran on a call whose answer
            // was lost, so the export is already on disk and the result is known.
            await harness.WriteExportAsync(Outgoing, "refresh-a", Token);
            StagedImportCredentialPairStore.DeleteIfPresent(harness.ClaimedPath(Incoming));
            return Result<ImportAnswer, string>.Success(
                new ImportAnswer(null, WslSwitchHarness.Email(Outgoing), true, harness.Side.Result()));
        };
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        harness.Side.Calls.ShouldNotContain("Commit");
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token))!.Side.ShouldBe(SideName.Wsl);
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task AnImportThatDoesNotAnswerLeavesTheJournalOpenAtClaimedWithNothingUnclaimed()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint incoming = await SetUpAsync(harness);
        // The side may have staged and swapped before the answer was lost, so the
        // claim stands until that side's own files say otherwise.
        harness.Side.OnImport = static _ =>
            Task.FromResult(Result<ImportAnswer, string>.Failure("the side did not answer within 60 s"));
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.Error.ShouldBe(SwitchRefusal.SideOffline);
        WslSwitchJournalEntry? open = await harness.Journal.ReadOpenAsync(Token);
        open!.StepReached.ShouldBe(WslSwitchStep.Claimed);
        (await FingerprintAtAsync(harness.ClaimedPath(Incoming))).ShouldBe(incoming);
        File.Exists(harness.PairPath(Incoming)).ShouldBeFalse();
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token))!.Side.ShouldBe(SideName.Wsl);
    }

    [Fact]
    public async Task AResumptionWhoseSideAnswersNotImportedPutsTheClaimBackAndRefuses()
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        RefreshTokenFingerprint incoming = CredentialFiles.Pair("refresh-b").Fingerprint;
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        await JournalAtAsync(harness, WslSwitchStep.ExportVerified, incoming);
        using WslSwitch coordinator = harness.Coordinator();

        WslReconciliation reconciled = await coordinator.ReconcileAsync(Token);

        reconciled.Outcome.ShouldContain(nameof(SwitchRefusal.PeerDidNotImport));
        // Only a definite answer from that side may take a claim back, so the
        // unclaim is downstream of one status read and of nothing else.
        harness.Side.Calls.ShouldBe(["Dashboard", "Status"]);
        (await FingerprintAtAsync(harness.PairPath(Incoming))).ShouldBe(incoming);
        File.Exists(harness.ClaimedPath(Incoming)).ShouldBeFalse();
        File.Exists(harness.RecordPath(Incoming)).ShouldBeFalse();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ACommitThatDoesNotAnswerAsksWhatHappenedAndUnclaimsOnlyOnADefiniteNotImported()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint incoming = await SetUpAsync(harness);
        harness.Side.OnCommit = static () =>
            Task.FromResult(Result<ImportResult, string>.Failure("the connection was reset"));
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.Error.ShouldBe(SwitchRefusal.PeerDidNotImport);
        harness.Side.Calls.ShouldContain("Status");
        (await FingerprintAtAsync(harness.PairPath(Incoming))).ShouldBe(incoming);
        File.Exists(harness.RecordPath(Incoming)).ShouldBeFalse();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"acc","refreshTok""")]
    [InlineData("not json at all")]
    public async Task AnExportWhoseBytesFailTheNativeGateAbortsAndUnclaims(string? exported)
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint incoming = await SetUpAsync(harness);
        // The side names the pair it says it exported; the bytes on the volume do
        // not back it up, which is the one crossing the gate exists to catch.
        harness.Side.OnImport = async request =>
        {
            if (exported is not null)
            {
                await File.WriteAllTextAsync(request.ExportPath, exported, Token);
            }

            return Result<ImportAnswer, string>.Success(new ImportAnswer(
                CredentialFiles.Pair("refresh-a").Fingerprint,
                WslSwitchHarness.Email(Outgoing),
                false,
                null));
        };
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        await AssertGateRefusedAsync(harness, result, incoming);
    }

    [Fact]
    public async Task AnExportWhoseFingerprintIsNotTheOneTheSideNamedIsRefusedTheSameWay()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint incoming = await SetUpAsync(harness);
        // A readable pair of some other lineage: the bytes parse, so only the
        // fingerprint compare separates it from the pair the side promised.
        harness.Side.OnImport = async request =>
        {
            await File.WriteAllTextAsync(request.ExportPath, CredentialFiles.Shape("refresh-someone-else").ToJsonString(), Token);
            return Result<ImportAnswer, string>.Success(new ImportAnswer(
                CredentialFiles.Pair("refresh-a").Fingerprint,
                WslSwitchHarness.Email(Outgoing),
                false,
                null));
        };
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        await AssertGateRefusedAsync(harness, result, incoming);
    }

    private static async Task AssertGateRefusedAsync(
        WslSwitchHarness harness,
        Result<WslSwitchOutcome, SwitchRefusal> result,
        RefreshTokenFingerprint incoming)
    {
        result.Error.ShouldBe(SwitchRefusal.ExportNotVerified);
        harness.Side.Calls.ShouldContain("Abort");
        harness.Side.Calls.ShouldNotContain("Commit");
        // The claim came back: the slot holds its own pair again and the record
        // that said the other side had it is gone.
        (await FingerprintAtAsync(harness.PairPath(Incoming))).ShouldBe(incoming);
        File.Exists(harness.RecordPath(Incoming)).ShouldBeFalse();
        File.Exists(harness.JournalPath).ShouldBeFalse();
        // Nothing was swapped on this side either.
        (await CredentialFiles.FingerprintAsync(harness.LiveDirectory, Token)).ShouldBe(CredentialFiles.Pair("refresh-w").Fingerprint);
        File.Exists(harness.PairPath(Outgoing)).ShouldBeFalse();
    }

    [Fact]
    public async Task ARestartAtClaimedReIssuesTheImportAndFinishesTheHandOff()
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        RefreshTokenFingerprint incoming = CredentialFiles.Pair("refresh-b").Fingerprint;
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        await JournalAtAsync(harness, WslSwitchStep.Claimed, incoming);
        using WslSwitch coordinator = harness.Coordinator();

        WslReconciliation reconciled = await coordinator.ReconcileAsync(Token);

        reconciled.Outcome.ShouldContain("finished from " + nameof(WslSwitchStep.Claimed));
        reconciled.Banner.ShouldBeNull();
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        harness.MailboxFiles().ShouldBeEmpty();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ARestartAtExportVerifiedWhoseSideStillHoldsTheExportCommitsAndParks()
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        RefreshTokenFingerprint incoming = CredentialFiles.Pair("refresh-b").Fingerprint;
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        await harness.WriteExportAsync(Outgoing, "refresh-a", Token);
        await JournalAtAsync(harness, WslSwitchStep.ExportVerified, incoming);
        // The hold is still open over there and the export already passed the
        // gate, so the commit is sent and the export is not read a second time.
        harness.Side.Status = new ImportStatus(false, ImportStep.Exported, null, null, "the hold is still open");
        using WslSwitch coordinator = harness.Coordinator();

        WslReconciliation reconciled = await coordinator.ReconcileAsync(Token);

        reconciled.Outcome.ShouldContain("finished from " + nameof(WslSwitchStep.ExportVerified));
        harness.Side.Calls.ShouldContain("Commit");
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token))!.Side.ShouldBe(SideName.Wsl);
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task TheLeaderCrashedAfterTheCommitResolvesForwardAndNeverUnclaims()
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        RefreshTokenFingerprint incoming = CredentialFiles.Pair("refresh-b").Fingerprint;
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        // What the swap leaves: the claimed pair is live over there, the outgoing
        // pair is the export, and that side's own journal has been cleared.
        StagedImportCredentialPairStore.DeleteIfPresent(harness.ClaimedPath(Incoming));
        await harness.WriteExportAsync(Outgoing, "refresh-a", Token);
        await harness.IdentifiedSlotAsync(Outgoing, Token);
        await JournalAtAsync(harness, WslSwitchStep.ExportVerified, incoming);
        harness.Side.Status = new ImportStatus(true, null, incoming, null, "imported: the live pair here is the one that was claimed");
        using WslSwitch coordinator = harness.Coordinator();

        WslReconciliation reconciled = await coordinator.ReconcileAsync(Token);

        reconciled.Outcome.ShouldContain("finished from " + nameof(WslSwitchStep.ExportVerified));
        // One question was asked and it settled the row: no import was re-issued
        // and no abort was sent against a side that has already swapped.
        harness.Side.Calls.ShouldBe(["Dashboard", "Status"]);
        // L4 ran: the export is the outgoing account's parked pair now.
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        // And the claim was never taken back: the incoming slot still carries the
        // record naming the side whose live directory now holds that pair.
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token))!.Side.ShouldBe(SideName.Wsl);
        File.Exists(harness.PairPath(Incoming)).ShouldBeFalse();
        harness.MailboxFiles().ShouldBeEmpty();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ARestartAtImportedParksTheExportWithoutAskingTheSideAnything()
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        RefreshTokenFingerprint incoming = CredentialFiles.Pair("refresh-b").Fingerprint;
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        StagedImportCredentialPairStore.DeleteIfPresent(harness.ClaimedPath(Incoming));
        await harness.WriteExportAsync(Outgoing, "refresh-a", Token);
        await JournalAtAsync(harness, WslSwitchStep.Imported, incoming, withOutgoingAccount: true);
        using WslSwitch coordinator = harness.Coordinator();

        WslReconciliation reconciled = await coordinator.ReconcileAsync(Token);

        reconciled.Outcome.ShouldContain("finished from " + nameof(WslSwitchStep.Imported));
        // Everything the park needs was in the journal, so no round trip was made.
        harness.Side.Calls.ShouldBeEmpty();
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        File.Exists(Path.Combine(harness.FolderFor(Outgoing), ProfileFolderStore.ProfileFileName)).ShouldBeTrue();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ARestartAtParkedOnlyClearsTheJournal()
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        RefreshTokenFingerprint incoming = CredentialFiles.Pair("refresh-b").Fingerprint;
        await harness.IdentifiedSlotAsync(Incoming, Token);
        await HolderRecordFile.WriteAsync(
            harness.FolderFor(Incoming),
            new HolderRecord(SideName.Wsl, incoming, harness.Clock.GetUtcNow()),
            Token);
        await harness.ParkedSlotAsync(Outgoing, "refresh-a", Token);
        await JournalAtAsync(harness, WslSwitchStep.Parked, incoming, withOutgoingAccount: true);
        using WslSwitch coordinator = harness.Coordinator();

        WslReconciliation reconciled = await coordinator.ReconcileAsync(Token);

        reconciled.Outcome.ShouldContain("finished from " + nameof(WslSwitchStep.Parked));
        harness.Side.Calls.ShouldBeEmpty();
        File.Exists(harness.JournalPath).ShouldBeFalse();
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token))!.Side.ShouldBe(SideName.Wsl);
    }

    [Theory]
    [InlineData(WslSwitchStep.Claimed)]
    [InlineData(WslSwitchStep.ExportVerified)]
    public async Task AnOfflineSideLeavesBothTheClaimAndTheRecordAloneAndReportsInTransit(WslSwitchStep reached)
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        RefreshTokenFingerprint incoming = CredentialFiles.Pair("refresh-b").Fingerprint;
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        if (reached is WslSwitchStep.ExportVerified)
        {
            await harness.WriteExportAsync(Outgoing, "refresh-a", Token);
        }

        await JournalAtAsync(harness, reached, incoming);
        harness.Side.DashboardError = "the distribution is not running";
        using WslSwitch coordinator = harness.Coordinator();

        WslReconciliation reconciled = await coordinator.ReconcileAsync(Token);

        reconciled.Banner.ShouldNotBeNull().ShouldContain("in transit");
        harness.Side.Calls.ShouldBe(["Dashboard"]);
        (await FingerprintAtAsync(harness.ClaimedPath(Incoming))).ShouldBe(incoming);
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token))!.Side.ShouldBe(SideName.Wsl);
        (await harness.Journal.ReadOpenAsync(Token))!.StepReached.ShouldBe(reached);
    }

    [Fact]
    public async Task NothingButTheNativeExportReadSitsBetweenTheExportedAnswerAndTheCommit()
    {
        // The follower holds its live pair and its lock open from the Exported
        // answer until the commit arrives, on a budget it self-aborts against.
        // The leader's own step in between is one file read on this volume, which
        // is not a call to that side and so leaves no entry here.
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        harness.Side.Calls.ShouldBe(["Dashboard", "Import", "Commit"]);
    }

    [Fact]
    public async Task ASwitchOfASideWithNoPeerConfiguredIsRefusedAsOffline()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        using WslSwitch coordinator = harness.Coordinator(withPeer: false);

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.Error.ShouldBe(SwitchRefusal.SideOffline);
        harness.Side.Calls.ShouldBeEmpty();
        (await FingerprintAtAsync(harness.PairPath(Incoming))).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    [Fact]
    public async Task ASwitchOfASideWhoseDashboardCannotBeReadIsRefusedAsOffline()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        harness.Side.DashboardError = "connection refused";
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.Error.ShouldBe(SwitchRefusal.SideOffline);
        harness.Side.Calls.ShouldBe(["Dashboard"]);
        harness.MailboxFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task ASideReportingAnotherVersionIsRefusedAsOfflineAndSentNoImport()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        harness.Side.Version = "0.0.0-a-follower-built-from-other-sources";
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.Error.ShouldBe(SwitchRefusal.SideOffline);
        harness.Side.Calls.ShouldBe(["Dashboard"]);
        (await FingerprintAtAsync(harness.PairPath(Incoming))).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        harness.MailboxFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task ASwitchToTheAccountThatSideAlreadyHoldsIsRefused()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        harness.Side.OutgoingEmail = Incoming;
        harness.Side.OutgoingRefreshToken = "refresh-b";
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.Error.ShouldBe(SwitchRefusal.AlreadyOnTarget);
        harness.MailboxFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task ASwitchToASlotThisSideHoldsIsRefusedAsHeldByTheOtherSide()
    {
        using WslSwitchHarness harness = new();
        // This side is live on the target and its slot says so: the pair is in
        // the Windows live directory, so the other side cannot be handed it.
        await harness.WriteLeaderLiveAsync(Incoming, "refresh-b", Token);
        await harness.IdentifiedSlotAsync(Incoming, Token);
        await HolderRecordFile.WriteAsync(
            harness.FolderFor(Incoming),
            new HolderRecord(SideName.Windows, CredentialFiles.Pair("refresh-b").Fingerprint, harness.Clock.GetUtcNow()),
            Token);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.Error.ShouldBe(SwitchRefusal.HeldByOtherSide);
        harness.MailboxFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task ASwitchToASlotWithAFileInTheMailboxIsRefusedAsInTransit()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        Directory.CreateDirectory(harness.Mailbox);
        await File.WriteAllTextAsync(harness.ClaimedPath(Incoming), string.Empty, Token);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.Error.ShouldBe(SwitchRefusal.SlotInTransit);
        (await FingerprintAtAsync(harness.PairPath(Incoming))).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    [Fact]
    public async Task ASwitchToASlotWithNoPairIsRefusedForWantOfCredentials()
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        await harness.IdentifiedSlotAsync(Incoming, Token);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), Token);

        result.Error.ShouldBe(SwitchRefusal.TargetHasNoCredentials);
        harness.MailboxFiles().ShouldBeEmpty();
    }

    /// <summary>The journal as the step named would have left it, written by hand for a restart.</summary>
    private static Task JournalAtAsync(
        WslSwitchHarness harness,
        WslSwitchStep step,
        RefreshTokenFingerprint incoming,
        bool withOutgoingAccount = false) =>
        harness.Journal.WriteAsync(
            new WslSwitchJournalEntry(
                SideName.Wsl,
                WslSwitchHarness.Email(Incoming),
                incoming,
                harness.FolderFor(Incoming),
                WslSwitchHarness.Email(Outgoing),
                CredentialFiles.Pair("refresh-a").Fingerprint,
                harness.FolderFor(Outgoing),
                step,
                harness.Clock.GetUtcNow(),
                withOutgoingAccount ? WslSwitchHarness.AccountJson(Outgoing) : null),
            TestContext.Current.CancellationToken);
}
