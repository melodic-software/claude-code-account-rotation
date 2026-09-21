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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
