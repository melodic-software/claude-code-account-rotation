using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Switching;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The leader's reading of a store slot: the design's reconciliation table
/// through <c>SlotStateRule</c>, plus the two verdicts that table leaves to the
/// caller, over real files in a temp store.
/// </summary>
public sealed class SharedStoreSlotsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _profilesRoot;
    private readonly CredentialMutationGate _gate = new();

    public SharedStoreSlotsTests()
    {
        _profilesRoot = Path.Combine(_root, "profiles");
        Directory.CreateDirectory(_profilesRoot);
    }

    private static AccountEmail Email(string value) => AccountEmail.Parse(value).Value;

    private SharedStoreSlots Slots(bool enabled = true, ILoginSessionRunner? logins = null) =>
        new(_profilesRoot, enabled, _gate, logins ?? new NoLoginsRunning(), NullLogger<SharedStoreSlots>.Instance);

    private string Folder(string email)
    {
        string folder = Path.Combine(_profilesRoot, email);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task<string> RecordedAsync(string email, SideName side, string refreshToken = "refresh-a")
    {
        string folder = Folder(email);
        await HolderRecordFile.WriteAsync(
            folder,
            new HolderRecord(side, CredentialFiles.Pair(refreshToken).Fingerprint, DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);
        return folder;
    }

    private string Transit(string email, string suffix = "")
    {
        string mailbox = FileSystemCredentialPairStore.MailboxPath(_profilesRoot, SideName.Wsl);
        Directory.CreateDirectory(mailbox);
        string path = Path.Combine(mailbox, FileSystemCredentialPairStore.ClaimedFileName(email) + suffix);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public async Task ASupersededRecordStandsWhileTheSlotHoldsTheFamilyThatReplacedIt()
    {
        string folder = Folder("a@example.com");
        await CredentialFiles.WriteAsync(folder, "refresh-a-fresh", TestContext.Current.CancellationToken);
        await SupersededFamilyFile.WriteAsync(
            folder,
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-a").Fingerprint, DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);

        SupersededFamily? read = await Slots().ReadSupersededAsync(
            Email("a@example.com"), folder, slotHoldsPair: true, default, TestContext.Current.CancellationToken);

        read!.Stale.ShouldBeFalse();
        read.Record.Side.ShouldBe(SideName.Wsl);
    }

    [Fact]
    public async Task ASupersededRecordWithNothingToShowForItIsStaleAndDropped()
    {
        // The login it was written for never put a family in the slot, so the
        // family the other side holds is still the store's and the record is a
        // refusal waiting to happen.
        string folder = Folder("a@example.com");
        await SupersededFamilyFile.WriteAsync(
            folder,
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-a").Fingerprint, DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);
        SharedStoreSlots slots = Slots();

        SupersededFamily read = (await slots.ReadSupersededAsync(
            Email("a@example.com"), folder, slotHoldsPair: false, default, TestContext.Current.CancellationToken))!;
        await slots.DropStaleSupersededAsync(Email("a@example.com"), folder, read, default, TestContext.Current.CancellationToken);

        read.Stale.ShouldBeTrue();
        File.Exists(SupersededFamilyFile.PathIn(folder)).ShouldBeFalse();
    }

    [Fact]
    public async Task ASupersededRecordIsNotStaleWhileTheLoginItWasWrittenForIsStillRunning()
    {
        string folder = Folder("a@example.com");
        await SupersededFamilyFile.WriteAsync(
            folder,
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-a").Fingerprint, DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);

        SupersededFamily? read = await Slots(logins: new LoginRunningAgainst(folder)).ReadSupersededAsync(
            Email("a@example.com"), folder, slotHoldsPair: false, default, TestContext.Current.CancellationToken);

        read!.Stale.ShouldBeFalse();
    }

    /// <summary>A runner that owns one folder, which is all the staleness rule asks it.</summary>
    private sealed class LoginRunningAgainst(string folderPath) : ILoginSessionRunner
    {
        public Task<Result<LoginSession, string>> StartAsync(AccountEmail email, string folder, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<LoginSession, string>> SubmitCodeAsync(LoginSessionId id, string code, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public LoginSession? Status(LoginSessionId id) => null;

        public bool IsRunningAgainst(string folder) => string.Equals(folder, folderPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTheStoreNotSharedNothingIsRead()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Wsl);

        (await Slots(enabled: false).ReadAsync(Email("a@example.com"), folder, slotHoldsPair: false, default, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task ASlotHoldingAPairWithNoRecordIsParked()
    {
        string folder = Folder("a@example.com");

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: true, default, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.Parked);
        slot.StaleRecord.ShouldBeFalse();
    }

    [Fact]
    public async Task ASlotHoldingAPairAndARecordIsParkedWithTheRecordStale()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Wsl);

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: true, default, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.Parked);
        slot.StaleRecord.ShouldBeTrue();
    }

    [Fact]
    public async Task AnEmptySlotWithNoRecordHasNeverBeenLoggedIn()
    {
        string folder = Folder("a@example.com");

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: false, default, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.NeverLoggedIn);
        slot.StaleRecord.ShouldBeFalse();
    }

    [Fact]
    public async Task AnEmptySlotRecordedToTheOtherSideIsHeldElsewhere()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Wsl);

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: false, default, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.HeldElsewhere);
        slot.StaleRecord.ShouldBeFalse();
    }

    [Fact]
    public async Task AWindowsRecordWhoseFingerprintIsTheLivePairIsHeldHere()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Windows);
        WindowsHold hold = new(null, CredentialFiles.Pair("refresh-a").Fingerprint);

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: false, hold, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.HeldHere);
        slot.StaleRecord.ShouldBeFalse();
    }

    [Fact]
    public async Task AWindowsRecordWhosePairTheCliHasRotatedIsStillHeldHereBecauseTheStateFileNamesIt()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Windows);
        WindowsHold hold = new(Email("a@example.com"), CredentialFiles.Pair("refresh-a-rotated").Fingerprint);

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: false, hold, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.HeldHere);
        slot.StaleRecord.ShouldBeFalse();
    }

    [Fact]
    public async Task AWindowsRecordForAPairThisSideDoesNotHoldIsStaleAndTheSlotHasNeverBeenLoggedIn()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Windows);
        WindowsHold hold = new(Email("b@example.com"), CredentialFiles.Pair("refresh-b").Fingerprint);

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: false, hold, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.NeverLoggedIn);
        slot.StaleRecord.ShouldBeTrue();
    }

    [Fact]
    public async Task AnEmptySlotWhoseAccountIsLiveIsHeldHereEvenWithNoRecordToSaySo()
    {
        string folder = Folder("a@example.com");
        WindowsHold hold = new(Email("a@example.com"), CredentialFiles.Pair("refresh-a").Fingerprint);

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: false, hold, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.HeldHere);
    }

    [Fact]
    public async Task AClaimedFileInTheMailboxOutranksEverything()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Wsl);
        Transit("a@example.com");

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: true, default, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.InTransit);
    }

    [Fact]
    public async Task AnExportedFileInTheMailboxIsAlsoATransitFile()
    {
        string folder = Folder("a@example.com");
        Transit("a@example.com", FileSystemCredentialPairStore.IncomingSuffix);

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: false, default, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.InTransit);
    }

    [Fact]
    public async Task AMailboxFileForAnotherAccountLeavesThisSlotAlone()
    {
        string folder = Folder("a@example.com");
        Transit("b@example.com");

        SlotSnapshot? slot = await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: true, default, TestContext.Current.CancellationToken);

        slot!.State.ShouldBe(SlotState.Parked);
    }

    [Fact]
    public async Task DroppingAStaleRecordRemovesTheFile()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Wsl);
        await CredentialFiles.WriteAsync(folder, "refresh-a", TestContext.Current.CancellationToken);
        SlotSnapshot observed = (await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: true, default, TestContext.Current.CancellationToken))!;

        await Slots().DropStaleRecordAsync(Email("a@example.com"), folder, observed, default, TestContext.Current.CancellationToken);

        observed.StaleRecord.ShouldBeTrue();
        File.Exists(Path.Combine(folder, HolderRecordFile.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task ADropWhileAMutationHoldsTheGateLeavesTheRecordForTheNextRead()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Wsl);
        await CredentialFiles.WriteAsync(folder, "refresh-a", TestContext.Current.CancellationToken);
        SlotSnapshot observed = (await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: true, default, TestContext.Current.CancellationToken))!;
        using IDisposable permit = await _gate.AcquireAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);

        await Slots().DropStaleRecordAsync(Email("a@example.com"), folder, observed, default, TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(folder, HolderRecordFile.FileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task ADropDoesNotTakeARecordWrittenSinceTheReadThatJudgedTheOldOneStale()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Wsl);
        await CredentialFiles.WriteAsync(folder, "refresh-a", TestContext.Current.CancellationToken);
        SlotSnapshot observed = (await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: true, default, TestContext.Current.CancellationToken))!;
        // What a switch that finished between the read and the drop leaves: the
        // pair taken to live, and this side's own record in its place.
        File.Delete(Path.Combine(folder, FileSystemCredentialPairStore.FileName));
        await Slots().TakeAsync(folder, CredentialFiles.Pair("refresh-a").Fingerprint, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        await Slots().DropStaleRecordAsync(Email("a@example.com"), folder, observed, default, TestContext.Current.CancellationToken);

        (await HolderRecordFile.ReadAsync(folder, TestContext.Current.CancellationToken))!.Side.ShouldBe(SideName.Windows);
    }

    [Fact]
    public async Task ADropDoesNotTakeARecordThatIsNoLongerStale()
    {
        string folder = await RecordedAsync("a@example.com", SideName.Wsl);
        await CredentialFiles.WriteAsync(folder, "refresh-a", TestContext.Current.CancellationToken);
        SlotSnapshot observed = (await Slots().ReadAsync(Email("a@example.com"), folder, slotHoldsPair: true, default, TestContext.Current.CancellationToken))!;
        // The pair leaves the slot, so the same wsl record now says something true.
        File.Delete(Path.Combine(folder, FileSystemCredentialPairStore.FileName));

        await Slots().DropStaleRecordAsync(Email("a@example.com"), folder, observed, default, TestContext.Current.CancellationToken);

        (await HolderRecordFile.ReadAsync(folder, TestContext.Current.CancellationToken))!.Side.ShouldBe(SideName.Wsl);
    }

    [Fact]
    public async Task WithTheStoreNotSharedTakingASlotWritesNoRecord()
    {
        string folder = Folder("a@example.com");

        await Slots(enabled: false).TakeAsync(folder, CredentialFiles.Pair("refresh-a").Fingerprint, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(folder, HolderRecordFile.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task TakingASlotRecordsThisSideAndReleasingItRemovesTheRecord()
    {
        string folder = Folder("a@example.com");

        await Slots().TakeAsync(folder, CredentialFiles.Pair("refresh-a").Fingerprint, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);
        HolderRecord? taken = await HolderRecordFile.ReadAsync(folder, TestContext.Current.CancellationToken);
        await Slots().ReleaseAsync(folder, TestContext.Current.CancellationToken);

        taken!.Side.ShouldBe(SideName.Windows);
        taken.Fingerprint.ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        File.Exists(Path.Combine(folder, HolderRecordFile.FileName)).ShouldBeFalse();
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
