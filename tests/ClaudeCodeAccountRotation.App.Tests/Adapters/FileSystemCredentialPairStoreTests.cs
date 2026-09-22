using System.Runtime.InteropServices;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

public sealed class FileSystemCredentialPairStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claude-code-account-rotation-tests", Guid.NewGuid().ToString("N"));
    private readonly string _liveDirectory;
    private readonly string _profilesRoot;
    private readonly FileSystemCredentialPairStore _store;

    public FileSystemCredentialPairStoreTests()
    {
        _liveDirectory = Path.Combine(_root, "live");
        _profilesRoot = Path.Combine(_root, "profiles");
        Directory.CreateDirectory(_liveDirectory);
        Directory.CreateDirectory(_profilesRoot);
        _store = new FileSystemCredentialPairStore(_liveDirectory, _profilesRoot, TimeProvider.System);
    }

    private string Folder(string name) => Path.Combine(_profilesRoot, name);

    private async Task<(int Files, int Distinct)> HolderCountsAsync(params string[] folders)
    {
        List<RefreshTokenFingerprint> fingerprints = [];
        foreach (string directory in folders.Prepend(_liveDirectory))
        {
            RefreshTokenFingerprint? fingerprint = await CredentialFiles.FingerprintAsync(directory, TestContext.Current.CancellationToken);
            if (fingerprint is RefreshTokenFingerprint present)
            {
                fingerprints.Add(present);
            }
        }

        return (fingerprints.Count, fingerprints.Distinct().Count());
    }

    [Fact]
    public async Task ReadLiveReturnsNullWhenNoPairIsPresent()
    {
        (await _store.ReadLiveAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ReadLiveParsesThePair()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);

        CredentialPair? pair = await _store.ReadLiveAsync(TestContext.Current.CancellationToken);

        pair!.Fingerprint.ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
    }

    [Fact]
    public async Task TenSwitchesLeaveEachRefreshTokenInExactlyOneFile()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(Folder("b"), "refresh-b", TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(Folder("c"), "refresh-c", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Folder("a"));
        string[] folders = [Folder("a"), Folder("b"), Folder("c")];
        string liveOwner = Folder("a");

        for (int switchIndex = 0; switchIndex < 10; switchIndex++)
        {
            string target = folders[(switchIndex + 1) % folders.Length];
            await _store.MoveLiveToParkedAsync(liveOwner, TestContext.Current.CancellationToken);
            await _store.MoveParkedToLiveAsync(target, TestContext.Current.CancellationToken);
            liveOwner = target;

            (int files, int distinct) = await HolderCountsAsync(folders);
            files.ShouldBe(3);
            distinct.ShouldBe(3);
        }
    }

    [Fact]
    public async Task ParkRefusesWhenTheFolderAlreadyHoldsAPair()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(Folder("a"), "refresh-stale", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(() => _store.MoveLiveToParkedAsync(Folder("a"), TestContext.Current.CancellationToken));

        (await HolderCountsAsync(Folder("a"))).ShouldBe((2, 2));
    }

    [Fact]
    public async Task UnparkRefusesWhenTheLiveDirectoryAlreadyHoldsAPair()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(Folder("b"), "refresh-b", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(() => _store.MoveParkedToLiveAsync(Folder("b"), TestContext.Current.CancellationToken));

        (await HolderCountsAsync(Folder("b"))).ShouldBe((2, 2));
    }

    [Fact]
    public async Task UnparkSetsTheLiveFileModificationTimeToNow()
    {
        await CredentialFiles.WriteAsync(Folder("b"), "refresh-b", TestContext.Current.CancellationToken);
        string parkedPath = Path.Combine(Folder("b"), CredentialFiles.FileName);
        File.SetLastWriteTimeUtc(parkedPath, DateTime.UtcNow.AddHours(-1));

        await _store.MoveParkedToLiveAsync(Folder("b"), TestContext.Current.CancellationToken);

        DateTime liveWriteTime = File.GetLastWriteTimeUtc(Path.Combine(_liveDirectory, CredentialFiles.FileName));
        (DateTime.UtcNow - liveWriteTime).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WriteParkedRefusesWhenTheFileNoLongerHoldsTheExpectedPair()
    {
        await CredentialFiles.WriteAsync(Folder("b"), "refresh-b", TestContext.Current.CancellationToken);
        CredentialPair rotated = CredentialFiles.Pair("refresh-b2");

        Result<Unit, string> result = await _store.WriteParkedAsync(
            Folder("b"), rotated, CredentialFiles.Pair("refresh-other").Fingerprint, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        (await CredentialFiles.FingerprintAsync(Folder("b"), TestContext.Current.CancellationToken)).ShouldBe(CredentialFiles.Pair("refresh-b").Fingerprint);
    }

    [Fact]
    public async Task WriteParkedReplacesThePairWhenTheFingerprintMatches()
    {
        await CredentialFiles.WriteAsync(Folder("b"), "refresh-b", TestContext.Current.CancellationToken);
        CredentialPair rotated = CredentialFiles.Pair("refresh-b2");

        Result<Unit, string> result = await _store.WriteParkedAsync(
            Folder("b"), rotated, CredentialFiles.Pair("refresh-b").Fingerprint, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        (await CredentialFiles.FingerprintAsync(Folder("b"), TestContext.Current.CancellationToken)).ShouldBe(rotated.Fingerprint);
    }

    [Fact]
    public async Task FreshLockFileNameIgnoresTheDaemonLockAndOldFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_liveDirectory, "daemon.lock"), "", TestContext.Current.CancellationToken);
        string oldLock = Path.Combine(_liveDirectory, "old.lock");
        await File.WriteAllTextAsync(oldLock, "", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(oldLock, DateTime.UtcNow.AddMinutes(-2));

        _store.FreshLockFileName(TimeSpan.FromSeconds(60)).ShouldBeNull();

        await File.WriteAllTextAsync(Path.Combine(_liveDirectory, "refresh.lock"), "", TestContext.Current.CancellationToken);
        _store.FreshLockFileName(TimeSpan.FromSeconds(60)).ShouldBe("refresh.lock");
    }

    [Fact]
    public async Task ANestedPathUnderTheProfilesRootIsRefusedLikeThePathsOutsideIt()
    {
        // The profile store already refuses a path deeper than one level; the pair store
        // must apply the same rule, or a request-built path reaches past the profile folders.
        string nested = Path.Combine(_profilesRoot, "a", "b");

        await Should.ThrowAsync<ArgumentException>(() => _store.ReadParkedAsync(nested, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => _store.ReadParkedAsync(Path.Combine(_root, "elsewhere"), TestContext.Current.CancellationToken));
    }

    private string Mailbox(string fileName) =>
        Path.Combine(FileSystemCredentialPairStore.MailboxPath(_profilesRoot, SideName.Wsl), fileName);

    [Fact]
    public async Task AClaimMovesTheParkedPairIntoTheMailboxAndLeavesTheSlotEmpty()
    {
        await CredentialFiles.WriteAsync(Folder("a@example.com"), "refresh-a", TestContext.Current.CancellationToken);

        string claimed = await _store.ClaimToMailboxAsync(Folder("a@example.com"), SideName.Wsl, TestContext.Current.CancellationToken);

        claimed.ShouldBe(Mailbox("a@example.com" + FileSystemCredentialPairStore.FileName));
        File.Exists(Path.Combine(Folder("a@example.com"), FileSystemCredentialPairStore.FileName)).ShouldBeFalse();
        (await CredentialFiles.ReadFingerprintAsync(claimed, TestContext.Current.CancellationToken))
            .ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
    }

    [Fact]
    public async Task AClaimOfASlotWithNoPairFailsRatherThanCreatingAnEmptyMailboxFile()
    {
        Directory.CreateDirectory(Folder("a@example.com"));

        await Should.ThrowAsync<FileNotFoundException>(() => _store.ClaimToMailboxAsync(Folder("a@example.com"), SideName.Wsl, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASecondClaimOverAnOccupiedMailboxIsRefusedRatherThanOverwriting()
    {
        await CredentialFiles.WriteAsync(Folder("a@example.com"), "refresh-a", TestContext.Current.CancellationToken);
        await _store.ClaimToMailboxAsync(Folder("a@example.com"), SideName.Wsl, TestContext.Current.CancellationToken);
        await CredentialFiles.WriteAsync(Folder("a@example.com"), "refresh-a-again", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(() => _store.ClaimToMailboxAsync(Folder("a@example.com"), SideName.Wsl, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APromoteMovesTheExportIntoTheSlotWhenItsFingerprintIsTheVerifiedOne()
    {
        await WriteExportAsync("a@example.com", "refresh-a");

        Result<Unit, string> promoted = await _store.PromoteFromMailboxAsync(
            Folder("a@example.com"), SideName.Wsl, CredentialFiles.Pair("refresh-a").Fingerprint, TestContext.Current.CancellationToken);

        promoted.IsSuccess.ShouldBeTrue(promoted.IsFailure ? promoted.Error : "");
        (await CredentialFiles.FingerprintAsync(Folder("a@example.com"), TestContext.Current.CancellationToken))
            .ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        File.Exists(Mailbox("a@example.com" + FileSystemCredentialPairStore.FileName + FileSystemCredentialPairStore.IncomingSuffix)).ShouldBeFalse();
    }

    [Fact]
    public async Task APromoteOfAnExportWithTheWrongFingerprintMovesNothing()
    {
        await WriteExportAsync("a@example.com", "refresh-a");

        Result<Unit, string> promoted = await _store.PromoteFromMailboxAsync(
            Folder("a@example.com"), SideName.Wsl, CredentialFiles.Pair("refresh-b").Fingerprint, TestContext.Current.CancellationToken);

        promoted.IsFailure.ShouldBeTrue();
        File.Exists(Path.Combine(Folder("a@example.com"), FileSystemCredentialPairStore.FileName)).ShouldBeFalse();
        File.Exists(Mailbox("a@example.com" + FileSystemCredentialPairStore.FileName + FileSystemCredentialPairStore.IncomingSuffix)).ShouldBeTrue();
    }

    [Fact]
    public async Task APromoteWithNoExportToPromoteIsARefusalRatherThanAFault()
    {
        Result<Unit, string> promoted = await _store.PromoteFromMailboxAsync(
            Folder("a@example.com"), SideName.Wsl, CredentialFiles.Pair("refresh-a").Fingerprint, TestContext.Current.CancellationToken);

        promoted.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task APromoteOfATruncatedExportIsARefusalRatherThanAFault()
    {
        string mailbox = FileSystemCredentialPairStore.MailboxPath(_profilesRoot, SideName.Wsl);
        Directory.CreateDirectory(mailbox);
        await File.WriteAllTextAsync(
            Mailbox("a@example.com" + FileSystemCredentialPairStore.FileName + FileSystemCredentialPairStore.IncomingSuffix),
            "{ truncated",
            TestContext.Current.CancellationToken);

        Result<Unit, string> promoted = await _store.PromoteFromMailboxAsync(
            Folder("a@example.com"), SideName.Wsl, CredentialFiles.Pair("refresh-a").Fingerprint, TestContext.Current.CancellationToken);

        promoted.IsFailure.ShouldBeTrue();
        File.Exists(Path.Combine(Folder("a@example.com"), FileSystemCredentialPairStore.FileName)).ShouldBeFalse();
    }

    private async Task WriteExportAsync(string folderName, string refreshToken)
    {
        string mailbox = FileSystemCredentialPairStore.MailboxPath(_profilesRoot, SideName.Wsl);
        Directory.CreateDirectory(mailbox);
        await File.WriteAllTextAsync(
            Mailbox(folderName + FileSystemCredentialPairStore.FileName + FileSystemCredentialPairStore.IncomingSuffix),
            CredentialFiles.Shape(refreshToken).ToJsonString(),
            TestContext.Current.CancellationToken);
    }

    public static bool OnUnix => !OperatingSystem.IsWindows();

    public static bool OnWindows => OperatingSystem.IsWindows();

    public static bool OnLinuxX64 =>
        OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64;

    [Fact]
    public void WindowsDriveRootsMatchIgnoringCase()
    {
        // The Windows branch of the shared helper, called directly: a Linux
        // runner has no drive-letter parser, and the rule is the comparison.
        // The roots are built from pieces so the source does not contain a drive path.
        SameVolume.WindowsPathRootsMatch(DriveRoot('C'), DriveRoot('c')).ShouldBeTrue();
        SameVolume.WindowsPathRootsMatch(DriveRoot('C'), DriveRoot('C')).ShouldBeTrue();
        SameVolume.WindowsPathRootsMatch(DriveRoot('C'), DriveRoot('D')).ShouldBeFalse();
    }

    private static string DriveRoot(char letter) => string.Concat(letter, ':', '\\');

    [Fact(SkipUnless = nameof(OnUnix), Skip = "Device ids are the Unix volume check; Windows compares drive roots")]
    public void DifferentDeviceIdsAreNotOneVolumeWhenThePathRootIsTheSame()
    {
        SameVolume.OnOneVolume(_liveDirectory, _profilesRoot, static _ => 1L).ShouldBeTrue();
        SameVolume.OnOneVolume(
            _liveDirectory,
            _profilesRoot,
            path => Path.GetFullPath(path).StartsWith(_liveDirectory, StringComparison.Ordinal) ? 1L : 2L).ShouldBeFalse();
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "Device ids are the Unix volume check; Windows compares drive roots")]
    public async Task AMoveOnOneInjectedDeviceRenamesThePair()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        var store = new FileSystemCredentialPairStore(_liveDirectory, _profilesRoot, TimeProvider.System, static _ => 1L);

        await store.MoveLiveToParkedAsync(Folder("a"), TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(_liveDirectory, CredentialFiles.FileName)).ShouldBeFalse();
        (await CredentialFiles.FingerprintAsync(Folder("a"), TestContext.Current.CancellationToken))
            .ShouldBe(CredentialFiles.Pair("refresh-a").Fingerprint);
        CopiesOf("refresh-a").ShouldBe(1);
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "Device ids are the Unix volume check; Windows compares drive roots")]
    public async Task AMoveAcrossInjectedDeviceIdsIsRefusedAndCreatesNoSecondFile()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        Func<string, long?> deviceId = path =>
            Path.GetFullPath(path).StartsWith(_liveDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? 1L : 2L;
        var store = new FileSystemCredentialPairStore(_liveDirectory, _profilesRoot, TimeProvider.System, deviceId);
        string parked = Path.Combine(Folder("a"), CredentialFiles.FileName);

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => store.MoveLiveToParkedAsync(Folder("a"), TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("different volumes");
        refusal.Message.ShouldContain("never a copy");
        File.Exists(Path.Combine(_liveDirectory, CredentialFiles.FileName)).ShouldBeTrue();
        File.Exists(parked).ShouldBeFalse();
        CopiesOf("refresh-a").ShouldBe(1);
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "rename(2) EXDEV is the Unix refusal; Windows compares drive roots")]
    public async Task ARenameThatReportsCrossDeviceIsRefusedAndCreatesNoSecondFile()
    {
        // The device ids match, which is what a bind mount looks like, and the
        // rename reports EXDEV. File.Move would copy; this must not.
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        var store = new FileSystemCredentialPairStore(
            _liveDirectory,
            _profilesRoot,
            TimeProvider.System,
            static _ => 1L,
            static (_, _) => SameVolume.CrossDeviceError);
        string parked = Path.Combine(Folder("a"), CredentialFiles.FileName);

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => store.MoveLiveToParkedAsync(Folder("a"), TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("different volumes");
        refusal.Message.ShouldContain("never a copy");
        File.Exists(Path.Combine(_liveDirectory, CredentialFiles.FileName)).ShouldBeTrue();
        File.Exists(parked).ShouldBeFalse();
        CopiesOf("refresh-a").ShouldBe(1);
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "A missing device id fails closed on Unix; Windows compares drive roots")]
    public async Task AMoveIsRefusedWhenTheDeviceIdCannotBeReadAndNoSecondFileIsCreated()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        var store = new FileSystemCredentialPairStore(_liveDirectory, _profilesRoot, TimeProvider.System, static _ => null);

        await Should.ThrowAsync<InvalidOperationException>(() => store.MoveLiveToParkedAsync(Folder("a"), TestContext.Current.CancellationToken));

        File.Exists(Path.Combine(_liveDirectory, CredentialFiles.FileName)).ShouldBeTrue();
        File.Exists(Path.Combine(Folder("a"), CredentialFiles.FileName)).ShouldBeFalse();
        CopiesOf("refresh-a").ShouldBe(1);
    }

    [Fact(SkipUnless = nameof(OnLinuxX64), Skip = "stat is implemented for linux-x64")]
    public void StatDeviceIdsPutProcAndTheTempDirectoryOnDifferentVolumes()
    {
        long? proc = SameVolume.DeviceId("/proc");
        long? temp = SameVolume.DeviceId("/tmp");

        proc.ShouldNotBeNull();
        temp.ShouldNotBeNull();
        proc.ShouldNotBe(temp);
        SameVolume.OnOneVolume("/proc/self", "/tmp", SameVolume.DeviceId).ShouldBeFalse();
    }

    [Fact(SkipUnless = nameof(OnUnix), Skip = "Symbolic links are the Unix form of this refusal")]
    public async Task ASymlinkedProfileFolderIsRefusedAndThePairStaysLive()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(_profilesRoot, "a");
        Directory.CreateSymbolicLink(link, outside);

        ArgumentException refusal = await Should.ThrowAsync<ArgumentException>(
            () => _store.MoveLiveToParkedAsync(link, TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("symbolic link");
        refusal.Message.ShouldNotContain(link);
        refusal.Message.ShouldNotContain(outside);
        File.Exists(Path.Combine(_liveDirectory, CredentialFiles.FileName)).ShouldBeTrue();
        File.Exists(Path.Combine(outside, CredentialFiles.FileName)).ShouldBeFalse();
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Junctions are a Windows reparse point")]
    public async Task AJunctionProfileFolderIsRefusedAndThePairStaysLive()
    {
        await CredentialFiles.WriteAsync(_liveDirectory, "refresh-a", TestContext.Current.CancellationToken);
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string junction = Path.Combine(_profilesRoot, "a");
        if (!WindowsJunction.TryCreate(junction, outside))
        {
            Assert.Skip("This process cannot create a directory junction.");
        }

        ArgumentException refusal = await Should.ThrowAsync<ArgumentException>(
            () => _store.MoveLiveToParkedAsync(junction, TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("junction");
        refusal.Message.ShouldNotContain(junction);
        refusal.Message.ShouldNotContain(outside);
        File.Exists(Path.Combine(_liveDirectory, CredentialFiles.FileName)).ShouldBeTrue();
        File.Exists(Path.Combine(outside, CredentialFiles.FileName)).ShouldBeFalse();
    }

    [Fact(SkipUnless = nameof(OnLinuxX64), Skip = "stat is implemented for linux-x64")]
    public void ASymlinkOntoAnotherVolumeIsNotOneVolume()
    {
        string link = Path.Combine(_root, "to-proc");
        Directory.CreateSymbolicLink(link, "/proc");

        SameVolume.OnOneVolume(link, _root, SameVolume.DeviceId).ShouldBeFalse();
    }

    [Fact(SkipUnless = nameof(OnLinuxX64), Skip = "stat is implemented for linux-x64")]
    public void APathThatDoesNotExistYetTakesItsAncestorsDeviceId()
    {
        string missing = Path.Combine(_root, "not-created", "pair");

        SameVolume.DeviceId(missing).ShouldBe(SameVolume.DeviceId(_root));
        SameVolume.OnOneVolume(
            Path.Combine(_liveDirectory, CredentialFiles.FileName),
            Path.Combine(_profilesRoot, "a", CredentialFiles.FileName),
            SameVolume.DeviceId).ShouldBeTrue();
    }

    private int CopiesOf(string refreshToken) =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Count(path => File.ReadAllText(path).Contains(refreshToken, StringComparison.Ordinal));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
