using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The leader's park-back against a scripted other side: L1's refusals, the
/// same export gate, and every row of the crash table in the direction where
/// nothing is claimed. Each fact is settled by what the store holds afterwards.
/// </summary>
public sealed class WslReleaseTests
{
    private const string Held = "a@example.com";
    private const string Other = "b@example.com";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<RefreshTokenFingerprint?> FingerprintAtAsync(string path) =>
        CredentialFiles.ReadFingerprintAsync(path, Token);

    /// <summary>
    /// The board a release starts from: this side live on its own account, and
    /// the other side holding A, whose slot is empty and carries the record
    /// that says so.
    /// </summary>
    private static async Task<RefreshTokenFingerprint> SetUpAsync(WslSwitchHarness harness)
    {
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        await harness.IdentifiedSlotAsync(Held, Token);
        RefreshTokenFingerprint held = CredentialFiles.Pair("refresh-a").Fingerprint;
        await HolderRecordFile.WriteAsync(
            harness.FolderFor(Held),
            new HolderRecord(SideName.Wsl, held, harness.Clock.GetUtcNow()),
            Token);
        return held;
    }

    [Fact]
    public async Task AReleaseVerifiesTheExportCommitsAndParksTheReturningPair()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint held = await SetUpAsync(harness);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        // Nothing arrived on that side; the account is back in its slot here.
        result.Value.Now.ShouldBeNull();
        result.Value.ParkedAs.ShouldBe(WslSwitchHarness.Email(Held));
        (await FingerprintAtAsync(harness.PairPath(Held))).ShouldBe(held);
        File.Exists(harness.RecordPath(Held)).ShouldBeFalse();
        JsonNode profile = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(harness.FolderFor(Held), ProfileFolderStore.ProfileFileName),
            Token))!;
        profile["emailAddress"]!.GetValue<string>().ShouldBe(Held);
        harness.MailboxFiles().ShouldBeEmpty();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    /// <summary>
    /// The direction's own asymmetry, and the reason it is worth a fact: a
    /// release takes no pair out of any slot, so L2 is the journal write alone
    /// and the call list carries no claim.
    /// </summary>
    [Fact]
    public async Task AReleaseClaimsNothingAndTakesNoPairOutOfAnySlot()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        await harness.ParkedSlotAsync(Other, "refresh-b", Token);
        using WslSwitch coordinator = harness.Coordinator();

        await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        // The other parked account is untouched: nothing was claimed for anyone.
        (await FingerprintAtAsync(harness.PairPath(Other))).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        File.Exists(harness.RecordPath(Other)).ShouldBeFalse();
        harness.Side.Calls.ShouldBe(["Dashboard", "Import", "Commit"]);
    }

    [Fact]
    public async Task AReleaseOfASideHoldingNothingIsRefusedAndMovesNothing()
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        harness.Side.OutgoingEmail = null;
        harness.Side.OutgoingRefreshToken = null;
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SwitchRefusal.NothingToRelease);
        harness.Side.Calls.ShouldBe(["Dashboard"]);
    }

    [Fact]
    public async Task AReleaseIsRefusedWhenThatSideIsNotAnswering()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        harness.Side.DashboardError = "connection refused";
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SwitchRefusal.SideOffline);
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task AReleaseIsRefusedWhenThatSideCannotNameThePairUnderTheAccount()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        harness.Side.OutgoingRefreshToken = null;
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SwitchRefusal.LiveIdentityUnverified);
    }

    /// <summary>
    /// A slot still holding a pair for an account that is live over there is the
    /// duplicate lineage the whole design refuses, and parking on top of it is
    /// the moment it would become one.
    /// </summary>
    [Fact]
    public async Task AReleaseIsRefusedWhenTheSlotStillHoldsAPairForThatAccount()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        await File.WriteAllTextAsync(harness.PairPath(Held), CredentialFiles.Shape("refresh-stray").ToJsonString(), Token);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SwitchRefusal.LiveIdentityUnverified);
        (await FingerprintAtAsync(harness.PairPath(Held))).ShouldBe(CredentialFiles.Pair("refresh-stray").Fingerprint);
    }

    /// <summary>
    /// Phase 7's in-transit state, from the release side: a mailbox file naming
    /// the account this hand-off would park refuses it, and clears nothing.
    /// </summary>
    [Fact]
    public async Task AReleaseIsRefusedWhileAHandOffForThatAccountIsAlreadyInTransit()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        await harness.WriteExportAsync(Held, "refresh-a", Token);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SwitchRefusal.SlotInTransit);
        // Nothing the release path does clears an in-transit state.
        File.Exists(harness.ExportPath(Held)).ShouldBeTrue();
        File.Exists(harness.RecordPath(Held)).ShouldBeTrue();
    }

    [Fact]
    public async Task TheExportGateRefusesAReleaseWhoseBytesDoNotReadBackNativelyAndNothingIsCommitted()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        harness.Side.OnImport = async request =>
        {
            // The side names the pair it exported; the file on this volume holds
            // some other lineage. That is exactly what the native read catches.
            await File.WriteAllTextAsync(request.ExportPath, CredentialFiles.Shape("refresh-corrupt").ToJsonString(), Token);
            return Result<ImportAnswer, string>.Success(new ImportAnswer(
                CredentialFiles.Pair("refresh-a").Fingerprint,
                WslSwitchHarness.Email(Held),
                false,
                null));
        };
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SwitchRefusal.ExportNotVerified);
        harness.Side.Calls.ShouldNotContain("Commit");
        // The holder record stays: that side is still live on the account, and
        // dropping it would leave a pair over there with nothing naming it.
        File.Exists(harness.RecordPath(Held)).ShouldBeTrue();
        File.Exists(harness.PairPath(Held)).ShouldBeFalse();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    /// <summary>
    /// The asymmetry that would lose the operator an account if it were copied
    /// from the switch: a release claimed nothing, so its unwind must not drop
    /// the slot's holder record, which is the store's only statement that the
    /// pair is on the other side.
    /// </summary>
    [Fact]
    public async Task ARefusedReleaseKeepsTheHolderRecordBecauseThatSideStillHoldsThePair()
    {
        using WslSwitchHarness harness = new();
        await SetUpAsync(harness);
        harness.Side.OnImport = _ => Task.FromResult(Result<ImportAnswer, string>.Success(
            new ImportAnswer(CredentialFiles.Pair("refresh-a").Fingerprint, WslSwitchHarness.Email(Held), false, null)));
        harness.Side.OnCommit = () => Task.FromResult(Result<ImportResult, string>.Failure("the side went away"));
        harness.Side.Status = new ImportStatus(false, null, null, null, "not imported: no record of it here");
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsFailure.ShouldBeTrue();
        HolderRecord? record = await HolderRecordFile.ReadAsync(harness.FolderFor(Held), Token);
        record!.Side.ShouldBe(SideName.Wsl);
        File.Exists(harness.PairPath(Held)).ShouldBeFalse();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(WslSwitchStep.Claimed)]
    [InlineData(WslSwitchStep.ExportVerified)]
    [InlineData(WslSwitchStep.Imported)]
    [InlineData(WslSwitchStep.Parked)]
    public async Task ARestartAtEachStepOfAReleaseFinishesIt(WslSwitchStep reached)
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint held = await SetUpAsync(harness);
        if (reached is not WslSwitchStep.Claimed)
        {
            await harness.WriteExportAsync(Held, "refresh-a", Token);
        }

        if (reached is WslSwitchStep.ExportVerified)
        {
            // The gate passed and that side is still holding the export open,
            // so the resume sends the commit it has already earned.
            harness.Side.Status = new ImportStatus(false, ImportStep.Exported, null, null, "an import is in flight at Exported, before the swap");
        }

        if (reached is WslSwitchStep.Imported or WslSwitchStep.Parked)
        {
            // Past the commit: that side has given the pair up already.
            harness.Side.Status = new ImportStatus(true, null, null, null, "the last release of " + Held + " completed");
        }

        if (reached is WslSwitchStep.Parked)
        {
            // L4 has run in full: the rename and the record's own release, with
            // only the journal left.
            File.Move(harness.ExportPath(Held), harness.PairPath(Held));
            File.Delete(harness.RecordPath(Held));
        }

        await ReleaseJournalAtAsync(harness, reached, held);
        using WslSwitch coordinator = harness.Coordinator();

        WslReconciliation reconciled = await coordinator.ReconcileAsync(Token);

        reconciled.Banner.ShouldBeNull(reconciled.Outcome);
        (await FingerprintAtAsync(harness.PairPath(Held))).ShouldBe(held);
        File.Exists(harness.RecordPath(Held)).ShouldBeFalse();
        harness.MailboxFiles().ShouldBeEmpty();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    /// <summary>
    /// An offline side leaves everything where it is, and the banner says which
    /// way the pair is moving: a release is carrying one back to the store
    /// and claimed nothing, so "stays claimed" would name a claim that is not
    /// there.
    /// </summary>
    [Fact]
    public async Task AReleaseStandingAgainstAnOfflineSideBannersItsOwnDirectionAndClearsNothing()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint held = await SetUpAsync(harness);
        await ReleaseJournalAtAsync(harness, WslSwitchStep.Claimed, held);
        harness.Side.DashboardError = "connection refused";
        using WslSwitch coordinator = harness.Coordinator();

        WslReconciliation reconciled = await coordinator.ReconcileAsync(Token);

        reconciled.Banner.ShouldNotBeNull();
        reconciled.Banner.ShouldContain("in transit from wsl");
        reconciled.Banner.ShouldNotContain("stays claimed");
        File.Exists(harness.JournalPath).ShouldBeTrue();
        File.Exists(harness.RecordPath(Held)).ShouldBeTrue();
    }

    /// <summary>
    /// Phase 7's superseded family, met on the way back: the returning pair is
    /// quarantined rather than promoted over the fresh one, it is never
    /// deleted, and the fresh family in the slot is untouched.
    /// </summary>
    [Fact]
    public async Task AReleaseOfASupersededFamilyIsRefusedAndThenQuarantinedOnTheSecondClick()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint held = await SetUpAsync(harness);
        // The escape hatch's two files: a fresh family in the slot, and the
        // record saying the other side still holds the one it replaced.
        await File.WriteAllTextAsync(harness.PairPath(Held), CredentialFiles.Shape("refresh-fresh").ToJsonString(), Token);
        await SupersededFamilyFile.WriteAsync(
            harness.FolderFor(Held),
            new HolderRecord(SideName.Wsl, held, harness.Clock.GetUtcNow()),
            Token);
        using WslSwitch refusing = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> refused =
            await refusing.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        refused.IsFailure.ShouldBeTrue();
        refused.Error.ShouldBe(SwitchRefusal.ForeignFamily);
        refusing.Dispose();

        using WslSwitch overriding_ = harness.Coordinator();
        Result<WslSwitchOutcome, SwitchRefusal> quarantined =
            await overriding_.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: true, Token);

        quarantined.IsSuccess.ShouldBeTrue(quarantined.IsFailure ? quarantined.Error.ToString() : string.Empty);
        quarantined.Value.ParkedAs.ShouldBeNull();
        quarantined.Value.QuarantinedAt.ShouldNotBeNull();
        // Kept, never promoted, never deleted; and the fresh family stands.
        (await FingerprintAtAsync(quarantined.Value.QuarantinedAt!)).ShouldBe(held);
        (await FingerprintAtAsync(harness.PairPath(Held))).ShouldBe(CredentialFiles.Pair("refresh-fresh").Fingerprint);
        harness.MailboxFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// A family already in quarantine blocks nothing and is never touched: it is
    /// a second, distinct family the operator deliberately made and has been
    /// told about on every poll since, not a duplicate lineage.
    /// </summary>
    [Fact]
    public async Task AQuarantinedFamilyBlocksNoReleaseAndNothingHereDeletesIt()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint held = await SetUpAsync(harness);
        string quarantined = Path.Combine(
            harness.Options.SupersededQuarantineDirectory,
            Held + "-" + held.Sha256Hex[..12],
            FileSystemCredentialPairStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(quarantined)!);
        await File.WriteAllTextAsync(quarantined, CredentialFiles.Shape("refresh-old").ToJsonString(), Token);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.ReleaseAsync(SideName.Wsl, quarantineForeignFamily: false, Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        (await FingerprintAtAsync(harness.PairPath(Held))).ShouldBe(held);
        // Kept exactly as it was: the release path deletes no credential file.
        (await FingerprintAtAsync(quarantined)).ShouldBe(CredentialFiles.Pair("refresh-old").Fingerprint);
    }

    /// <summary>
    /// <c>Cancel</c> reaches a release the same way it reaches a switch, and
    /// the thing it must not do is the same: a claim is taken back only on a
    /// definite "not imported", and a release has none to take back, so what it
    /// clears is the journal and nothing else.
    /// </summary>
    [Fact]
    public async Task CancellingAReleaseClearsTheJournalAndLeavesTheAccountWithThatSide()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint held = await SetUpAsync(harness);
        await ReleaseJournalAtAsync(harness, WslSwitchStep.Claimed, held);
        harness.Side.Status = new ImportStatus(false, null, null, null, "not imported: no record of it here");
        using WslSwitch coordinator = harness.Coordinator();

        Result<string, string> cancelled = await coordinator.CancelAsync(Token);

        cancelled.IsSuccess.ShouldBeTrue(cancelled.IsFailure ? cancelled.Error : string.Empty);
        cancelled.Value.ShouldContain("that side still holds it");
        File.Exists(harness.JournalPath).ShouldBeFalse();
        // The record stays: that side is still live on the account.
        File.Exists(harness.RecordPath(Held)).ShouldBeTrue();
        File.Exists(harness.PairPath(Held)).ShouldBeFalse();
    }

    [Fact]
    public async Task CancelIsUnavailableWhileThatSideHasNotAnsweredAboutARelease()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint held = await SetUpAsync(harness);
        await ReleaseJournalAtAsync(harness, WslSwitchStep.Claimed, held);
        harness.Side.StatusError = "connection refused";
        using WslSwitch coordinator = harness.Coordinator();

        Result<string, string> cancelled = await coordinator.CancelAsync(Token);

        cancelled.IsFailure.ShouldBeTrue();
        File.Exists(harness.JournalPath).ShouldBeTrue();
        File.Exists(harness.RecordPath(Held)).ShouldBeTrue();
    }

    /// <summary>The journal a release writes: nothing arriving, one account leaving.</summary>
    private static Task ReleaseJournalAtAsync(WslSwitchHarness harness, WslSwitchStep step, RefreshTokenFingerprint held) =>
        harness.Journal.WriteAsync(
            new WslSwitchJournalEntry(
                SideName.Wsl,
                null,
                null,
                null,
                WslSwitchHarness.Email(Held),
                held,
                harness.FolderFor(Held),
                step,
                harness.Clock.GetUtcNow(),
                WslSwitchHarness.AccountJson(Held)),
            TestContext.Current.CancellationToken);
}
