using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The three ways out of a state the ordinary hand-off cannot finish: the
/// second token family the escape hatch is allowed to make and the quarantine
/// that ends it, the banner a hand-off nobody could finish carries, and the
/// operator's <c>Cancel</c>.
/// <para>
/// Every fact runs over temp roots with fake pairs. Nothing here logs in,
/// reads a real credential file, or points a coordinator at the operator's
/// store.
/// </para>
/// </summary>
public sealed class EscapeHatchTests
{
    private const string Incoming = "b@example.com";

    /// <summary>The account the other side is live on, and the one the escape hatch supersedes.</summary>
    private const string Outgoing = "a@example.com";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<RefreshTokenFingerprint?> FingerprintAtAsync(string path) =>
        CredentialFiles.ReadFingerprintAsync(path, Token);

    /// <summary>
    /// The board the escape hatch leaves behind: the other side still live on
    /// <see cref="Outgoing"/> with the family it always had, and that account's
    /// slot holding a <b>fresh</b> family from a login made here while that
    /// side was unreachable, with the record that says so.
    /// </summary>
    private static async Task<RefreshTokenFingerprint> SupersededAsync(WslSwitchHarness harness)
    {
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        RefreshTokenFingerprint incoming = await harness.ParkedSlotAsync(Incoming, "refresh-b", Token);
        _ = await harness.ParkedSlotAsync(Outgoing, "refresh-a-fresh", Token);
        await SupersededFamilyFile.WriteAsync(
            harness.FolderFor(Outgoing),
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-a").Fingerprint, harness.Clock.GetUtcNow()),
            Token);
        return incoming;
    }

    private static string QuarantineRoot(WslSwitchHarness harness) =>
        harness.Options.SupersededQuarantineDirectory;

    private static string[] QuarantinedFiles(WslSwitchHarness harness) =>
        Directory.Exists(QuarantineRoot(harness))
            ? Directory.GetFiles(QuarantineRoot(harness), "*", SearchOption.AllDirectories)
            : [];

    [Fact]
    public async Task AForeignFamilyRefusesTheHandOffAndMovesNothing()
    {
        using WslSwitchHarness harness = new();
        await SupersededAsync(harness);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), quarantineForeignFamily: false, Token);

        result.Error.ShouldBe(SwitchRefusal.ForeignFamily);
        // Refused at L1, so nothing was claimed, nothing was asked of that side
        // beyond the dashboard read the plan is built on, and both families are
        // exactly where they were.
        harness.Side.Calls.ShouldBe(["Dashboard"]);
        harness.MailboxFiles().ShouldBeEmpty();
        File.Exists(harness.JournalPath).ShouldBeFalse();
        (await FingerprintAtAsync(harness.PairPath(Incoming))).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a-fresh").Fingerprint);
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token)).ShouldBeNull();
    }

    [Fact]
    public async Task TheForeignFamilyRefusalIsAboutTheAccountThatSideHoldsAndNotAboutTheTarget()
    {
        // "Blocks every WSL switch for that account": the verdict is formed
        // from the account the other side is live on, so it answers the same
        // whichever account the switch is heading to.
        using WslSwitchHarness harness = new();
        await SupersededAsync(harness);
        _ = await harness.ParkedSlotAsync("c@example.com", "refresh-c", Token);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> toB =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), quarantineForeignFamily: false, Token);
        Result<WslSwitchOutcome, SwitchRefusal> toC =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email("c@example.com"), quarantineForeignFamily: false, Token);

        toB.Error.ShouldBe(SwitchRefusal.ForeignFamily);
        toC.Error.ShouldBe(SwitchRefusal.ForeignFamily);
    }

    [Fact]
    public async Task ASlotOccupiedWithNothingExplainingItIsStillRefusedWhateverTheRequestAsksFor()
    {
        // The override is not a skeleton key. Without a superseded record the
        // occupied slot is the duplicate lineage the design refuses outright,
        // and asking for a quarantine does not make it one.
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        _ = await harness.ParkedSlotAsync(Incoming, "refresh-b", Token);
        _ = await harness.ParkedSlotAsync(Outgoing, "refresh-a-fresh", Token);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), quarantineForeignFamily: true, Token);

        result.Error.ShouldBe(SwitchRefusal.LiveIdentityUnverified);
        harness.MailboxFiles().ShouldBeEmpty();
        QuarantinedFiles(harness).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheOverrideQuarantinesTheReturningFamilyAndNeverPromotesIt()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint incoming = await SupersededAsync(harness);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), quarantineForeignFamily: true, Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        // Nothing was parked, and the outcome says so rather than naming an
        // account whose pair never reached its slot.
        result.Value.ParkedAs.ShouldBeNull();
        string quarantined = QuarantinedFiles(harness).ShouldHaveSingleItem();
        result.Value.QuarantinedAt.ShouldBe(quarantined);
        (await FingerprintAtAsync(quarantined)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);

        // The slot still holds the family the escape hatch's login put there:
        // the returning one was never promoted over it.
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a-fresh").Fingerprint);
        // The record that sanctioned the quarantine has done its work and goes.
        (await SupersededFamilyFile.ReadAsync(harness.FolderFor(Outgoing), Token)).ShouldBeNull();
        // And the hand-off itself completed: the target is on that side, its
        // record says so, nothing is left in a mailbox and no journal is open.
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token))!.Fingerprint.ShouldBe(incoming);
        harness.MailboxFiles().ShouldBeEmpty();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task AHandOffClaimedBeforeTheEscapeHatchRanStillQuarantinesWhenItResumes()
    {
        // The sequence the journal cannot carry a flag for: the switch was
        // claimed while that side was reachable, the side died, the operator
        // used the escape hatch on the outgoing account, and the side came
        // back. L4 decides from the files, so the export is quarantined rather
        // than refusing to park for ever.
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint incoming = await SupersededAsync(harness);
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        await harness.Journal.WriteAsync(
            new WslSwitchJournalEntry(
                SideName.Wsl,
                WslSwitchHarness.Email(Incoming),
                incoming,
                harness.FolderFor(Incoming),
                WslSwitchHarness.Email(Outgoing),
                CredentialFiles.Pair("refresh-a").Fingerprint,
                harness.FolderFor(Outgoing),
                WslSwitchStep.Claimed,
                harness.Clock.GetUtcNow(),
                WslSwitchHarness.AccountJson(Outgoing)),
            Token);
        using WslSwitch resumed = harness.Coordinator();

        WslReconciliation pass = await resumed.ReconcileAsync(Token);

        pass.Banner.ShouldBeNull();
        string quarantined = QuarantinedFiles(harness).ShouldHaveSingleItem();
        (await FingerprintAtAsync(quarantined)).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a-fresh").Fingerprint);
        harness.MailboxFiles().ShouldBeEmpty();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ASupersededRecordWithNoFamilyBehindItIsIgnoredAndTheHandOffParksNormally()
    {
        // A login the operator abandoned leaves the record with nothing to show
        // for it: no fresh pair in the slot and the account not live here.
        // Believing it would refuse a legitimate hand-off and then quarantine
        // the account's only family.
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        _ = await harness.ParkedSlotAsync(Incoming, "refresh-b", Token);
        await harness.IdentifiedSlotAsync(Outgoing, Token);
        await SupersededFamilyFile.WriteAsync(
            harness.FolderFor(Outgoing),
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-a").Fingerprint, harness.Clock.GetUtcNow()),
            Token);
        using WslSwitch coordinator = harness.Coordinator();

        Result<WslSwitchOutcome, SwitchRefusal> result =
            await coordinator.SwitchToAsync(SideName.Wsl, WslSwitchHarness.Email(Incoming), quarantineForeignFamily: false, Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        result.Value.ParkedAs.ShouldBe(WslSwitchHarness.Email(Outgoing));
        (await FingerprintAtAsync(harness.PairPath(Outgoing))).ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        QuarantinedFiles(harness).ShouldBeEmpty();
    }

    [Fact]
    public async Task AStuckHandOffSaysHowLongOnlyAfterADayAndClearsNothingEitherWay()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint incoming = await SupersededAsync(harness);
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        await harness.Journal.WriteAsync(
            new WslSwitchJournalEntry(
                SideName.Wsl,
                WslSwitchHarness.Email(Incoming),
                incoming,
                harness.FolderFor(Incoming),
                null,
                null,
                null,
                WslSwitchStep.Claimed,
                harness.Clock.GetUtcNow(),
                null),
            Token);
        harness.Side.DashboardError = "the distro is not running";

        using WslSwitch coordinator = harness.Coordinator();
        string? fresh = (await coordinator.ReconcileAsync(Token)).Banner;
        harness.Clock.Advance(WslSwitch.StuckAfter + TimeSpan.FromHours(1));
        string? stuck = (await coordinator.ReconcileAsync(Token)).Banner;

        fresh.ShouldNotBeNull();
        fresh.ShouldNotContain("in transit for");
        stuck.ShouldNotBeNull();
        stuck.ShouldContain("in transit for 25 hours");
        // Neither pass touched the state it was describing: the claim is still
        // in the mailbox, the record still names the side, and the journal is
        // still open. Nothing clears an in-transit state on a timer.
        harness.MailboxFiles().ShouldHaveSingleItem();
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token)).ShouldNotBeNull();
        File.Exists(harness.JournalPath).ShouldBeTrue();
    }

    [Fact]
    public async Task AnExportForAnAccountNothingCanNameLeavesTheJournalOpenWithABannerThatSaysSo()
    {
        // The limitation the coordinator was left with: that side has already
        // swapped and the pair it exported is not the account this hand-off
        // planned to park, so nothing here can name a slot for it. The refusal
        // unwinds nothing, and the only thing telling the operator a credential
        // is standing in a mailbox is this line.
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint incoming = await SupersededAsync(harness);
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        await harness.WriteExportAsync(Outgoing, "refresh-somebody-else", Token);
        await harness.Journal.WriteAsync(
            new WslSwitchJournalEntry(
                SideName.Wsl,
                WslSwitchHarness.Email(Incoming),
                incoming,
                harness.FolderFor(Incoming),
                WslSwitchHarness.Email(Outgoing),
                CredentialFiles.Pair("refresh-a").Fingerprint,
                harness.FolderFor(Outgoing),
                WslSwitchStep.Claimed,
                harness.Clock.GetUtcNow(),
                WslSwitchHarness.AccountJson(Outgoing)),
            Token);
        harness.Side.OnImport = static _ => Task.FromResult(Result<ImportAnswer, string>.Success(new ImportAnswer(
            null,
            null,
            true,
            new ImportResult(
                WslSwitchHarness.Email("someone-else@example.com"),
                CredentialFiles.Pair("refresh-somebody-else").Fingerprint,
                WslSwitchHarness.AccountJson("someone-else@example.com"),
                false))));

        using WslSwitch coordinator = harness.Coordinator();
        WslReconciliation pass = await coordinator.ReconcileAsync(Token);

        pass.Banner.ShouldNotBeNull();
        pass.Banner.ShouldContain(Incoming);
        pass.Banner.ShouldContain("already imported");
        File.Exists(harness.JournalPath).ShouldBeTrue();
        harness.MailboxFiles().Count.ShouldBe(2);
    }

    [Fact]
    public async Task AMailboxFileNoJournalPointsAtAndNoSideWillAnswerForCarriesABanner()
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        harness.Side.DashboardError = "the distro is not running";
        harness.Side.StatusError = "the distro is not running";

        using WslSwitch coordinator = harness.Coordinator();
        WslReconciliation pass = await coordinator.ReconcileAsync(Token);

        pass.Banner.ShouldNotBeNull();
        pass.Banner.ShouldContain(Incoming);
        harness.MailboxFiles().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CancelIsUnavailableWhileTheSideHasNotAnswered()
    {
        using WslSwitchHarness harness = new();
        RefreshTokenFingerprint incoming = await StalledClaimAsync(harness);
        harness.Side.DashboardError = "the distro is not running";
        harness.Side.StatusError = "the distro is not running";

        using WslSwitch coordinator = harness.Coordinator();
        Result<string, string> cancelled = await coordinator.CancelAsync(Token);

        cancelled.IsFailure.ShouldBeTrue();
        cancelled.Error.ShouldContain("not answering");
        harness.MailboxFiles().ShouldHaveSingleItem();
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token))!.Fingerprint.ShouldBe(incoming);
        File.Exists(harness.JournalPath).ShouldBeTrue();
    }

    [Fact]
    public async Task CancelIsUnavailableWhileThatSideStillHoldsTheImportOpen()
    {
        using WslSwitchHarness harness = new();
        _ = await StalledClaimAsync(harness);
        harness.Side.Status = new ImportStatus(false, ImportStep.Exported, null, null, "the hold is still open here");

        using WslSwitch coordinator = harness.Coordinator();
        Result<string, string> cancelled = await coordinator.CancelAsync(Token);

        cancelled.IsFailure.ShouldBeTrue();
        cancelled.Error.ShouldContain("still has the import");
        harness.MailboxFiles().ShouldHaveSingleItem();
        File.Exists(harness.JournalPath).ShouldBeTrue();
    }

    [Fact]
    public async Task CancelIsUnavailableOnceThatSideSaysItHasImportedThePair()
    {
        using WslSwitchHarness harness = new();
        _ = await StalledClaimAsync(harness);
        harness.Side.Status = new ImportStatus(true, null, null, null, "imported here");

        using WslSwitch coordinator = harness.Coordinator();
        Result<string, string> cancelled = await coordinator.CancelAsync(Token);

        cancelled.IsFailure.ShouldBeTrue();
        cancelled.Error.ShouldContain("finishes rather than cancels");
        harness.MailboxFiles().ShouldHaveSingleItem();
        File.Exists(harness.JournalPath).ShouldBeTrue();
    }

    [Fact]
    public async Task CancelPutsTheClaimBackOnlyOnADefiniteNotImported()
    {
        using WslSwitchHarness harness = new();
        _ = await StalledClaimAsync(harness);

        using WslSwitch coordinator = harness.Coordinator();
        Result<string, string> cancelled = await coordinator.CancelAsync(Token);

        cancelled.IsSuccess.ShouldBeTrue(cancelled.IsFailure ? cancelled.Error : string.Empty);
        (await FingerprintAtAsync(harness.PairPath(Incoming))).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
        (await HolderRecordFile.ReadAsync(harness.FolderFor(Incoming), Token)).ShouldBeNull();
        harness.MailboxFiles().ShouldBeEmpty();
        File.Exists(harness.JournalPath).ShouldBeFalse();
    }

    [Fact]
    public async Task CancelWithNothingInTransitChangesNothing()
    {
        using WslSwitchHarness harness = new();
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);

        using WslSwitch coordinator = harness.Coordinator();
        Result<string, string> cancelled = await coordinator.CancelAsync(Token);

        cancelled.IsFailure.ShouldBeTrue();
        cancelled.Error.ShouldContain("nothing to cancel");
    }

    /// <summary>A claim in the mailbox with the journal open at it: the state every Cancel fact starts from.</summary>
    private static async Task<RefreshTokenFingerprint> StalledClaimAsync(WslSwitchHarness harness)
    {
        await harness.WriteLeaderLiveAsync("w@example.com", "refresh-w", Token);
        RefreshTokenFingerprint incoming = CredentialFiles.Pair("refresh-b").Fingerprint;
        await harness.ClaimBySideAsync(Incoming, "refresh-b", Token);
        await harness.Journal.WriteAsync(
            new WslSwitchJournalEntry(
                SideName.Wsl,
                WslSwitchHarness.Email(Incoming),
                incoming,
                harness.FolderFor(Incoming),
                null,
                null,
                null,
                WslSwitchStep.Claimed,
                harness.Clock.GetUtcNow(),
                null),
            Token);
        return incoming;
    }
}
