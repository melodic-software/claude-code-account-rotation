using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;
using Shouldly;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The follower's staged import over two temp roots: the export gate, the lock
/// budget, the no-outgoing case, and the negative paths. Every assertion is
/// against what is on disk, by fingerprint.
/// </summary>
public sealed class FollowerImportTests : IDisposable
{
    private const string OutgoingEmail = "a@example.com";
    private const string IncomingEmail = "b@example.com";
    private const string OutgoingToken = "refresh-a";
    private const string IncomingToken = "refresh-b";

    private readonly FollowerRoots _roots = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _roots.Dispose();

    /// <summary>Seeds A live in WSL and B claimed in the mailbox, the ordinary hand-off.</summary>
    private async Task<(RefreshTokenFingerprint Outgoing, RefreshTokenFingerprint Incoming)> SeedAsync()
    {
        RefreshTokenFingerprint outgoing = await _roots.WriteLiveAsync(OutgoingEmail, OutgoingToken, Token);
        RefreshTokenFingerprint incoming = await _roots.WriteClaimedAsync(IncomingEmail, IncomingToken, Token);
        return (outgoing, incoming);
    }

    [Fact]
    public async Task ImportAloneExportsTheOutgoingPairAndLeavesTheLivePairUntouched()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();

        Result<ImportAnswer, string> answer = await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        answer.IsSuccess.ShouldBeTrue(answer.IsFailure ? answer.Error : null);
        answer.Value.ExportedFingerprint.ShouldBe(fa);
        answer.Value.Outgoing?.Value.ShouldBe(OutgoingEmail);
        // The one fact the export gate rests on: F4 has run and F5 has not.
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(OutgoingEmail), Token)).ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.StagingPath, Token)).ShouldBe(fb);
        (await _roots.Journal().ReadOpenAsync(Token))!.StepReached.ShouldBe(ImportStep.Exported);
    }

    [Fact]
    public async Task ACommitAfterTheExportSwapsReleasesAndPatches()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        Result<ImportResult, string> result = await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error : null);
        result.Value.Outgoing?.Value.ShouldBe(OutgoingEmail);
        result.Value.OutgoingFingerprint.ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fb);
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        File.Exists(_roots.ClaimedPath(IncomingEmail)).ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(OutgoingEmail), Token)).ShouldBe(fa);
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        (await _roots.StateFile().ReadAccountBlockAsync(Token))!.Email!.Value.Value.ShouldBe(IncomingEmail);
    }

    [Fact]
    public async Task EveryFingerprintLandsInExactlyOneNonStagingFileAfterACompletedImport()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);

        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task NoCodePathReachesTheSwapWithoutACommit()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();

        // Every request the follower serves, in every order, with no commit among
        // them: the live pair must still be A at the end of all of it.
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        await follower.StatusAsync(Token);

        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
    }

    [Fact]
    public async Task ACommitIsRefusedInEveryJournalStateButExported()
    {
        (_, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();

        Result<ImportResult, string> beforeAnyImport = await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);
        beforeAnyImport.IsFailure.ShouldBeTrue();
        beforeAnyImport.Error.ShouldContain("not imported");

        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);

        // The journal is cleared by F8, so a second commit finds no Exported state.
        Result<ImportResult, string> afterTheImport = await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);
        afterTheImport.IsSuccess.ShouldBeTrue();
        afterTheImport.Value.AlreadyImported.ShouldBeTrue();
    }

    [Fact]
    public async Task AnAbortFromExportedDeletesTheExportAndTheStagingFileAndLeavesTheLivePair()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        Result<Unit, string> aborted = await follower.AbortAsync(new AccountEmail(IncomingEmail), Token);

        aborted.IsSuccess.ShouldBeTrue(aborted.IsFailure ? aborted.Error : null);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.ExportPath(OutgoingEmail)).ShouldBeFalse();
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        // The outgoing pair is recoverable because it never stopped being live,
        // and the incoming pair is back to being the leader's to unclaim.
        (await FollowerRoots.FingerprintOfAsync(_roots.ClaimedPath(IncomingEmail), Token)).ShouldBe(fb);
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFailedNativeReadBackLeavesTheOutgoingPairRecoverable()
    {
        // The leader's L3b stands in here: it reads the export natively, finds it
        // truncated, and aborts instead of committing. The operator loses a switch,
        // not a login, which is the whole point of the gate.
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        await File.WriteAllTextAsync(_roots.ExportPath(OutgoingEmail), "{\"claudeAiOauth\":", Token);
        CredentialPair? nativeRead = await StagedImportCredentialPairStore.ReadFreshAsync(_roots.ExportPath(OutgoingEmail), Token);
        nativeRead.ShouldBeNull();

        await follower.AbortAsync(new AccountEmail(IncomingEmail), Token);

        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnAbsentConfirmationNeverSwapsAndLeavesTheOutgoingPairRecoverable()
    {
        // No commit and no abort ever arrive: the leader died at L3b. The idle
        // self-abort unwinds the hold, and A is still live.
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower(idleTimeout: TimeSpan.FromSeconds(120), heartbeatInterval: TimeSpan.FromMilliseconds(30));
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        _roots.Clock.Advance(TimeSpan.FromSeconds(121));
        await WaitUntilAsync(() => !File.Exists(_roots.StagingPath));

        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.ExportPath(OutgoingEmail)).ShouldBeFalse();
        Result<ImportResult, string> late = await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);
        late.IsFailure.ShouldBeTrue();
        late.Error.ShouldContain("not imported");
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ACommitLaterThanTheBudgetSelfAbortsWithTheLivePairUntouched()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower(commitBudget: TimeSpan.FromSeconds(20));
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        _roots.Clock.Advance(TimeSpan.FromSeconds(21));
        Result<ImportResult, string> late = await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);

        late.IsFailure.ShouldBeTrue();
        late.Error.ShouldContain("not imported");
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        File.Exists(_roots.ExportPath(OutgoingEmail)).ShouldBeFalse();
    }

    [Fact]
    public async Task ALivePairRotatedBetweenTheExportAndTheCommitMakesTheSwapRefuse()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        // A session stole the lock and rotated A's token while the leader was
        // reading the export.
        await CredentialFiles.WriteAsync(_roots.LiveDirectory, "refresh-a-rotated", Token);
        var rotated = RefreshTokenFingerprint.FromRefreshToken("refresh-a-rotated");

        Result<ImportResult, string> commit = await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);

        commit.IsFailure.ShouldBeTrue();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(rotated);
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        // The export of the pre-rotation pair goes with the abort: the lineage is
        // still live here, so the copy in the mailbox would be the second holder.
        File.Exists(_roots.ExportPath(OutgoingEmail)).ShouldBeFalse();
        fa.ShouldNotBe(rotated);
    }

    [Fact]
    public async Task TheLockDirectoryMtimeIsRestampedWhileTheHoldLasts()
    {
        (_, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower(heartbeatInterval: TimeSpan.FromMilliseconds(30));
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        Directory.Exists(_roots.RefreshLockDirectory).ShouldBeTrue();
        // The acquire stamps the directory from the lock's own clock, and the
        // heartbeat re-stamps from it. Anchoring the baseline to the test clock
        // here as well keeps this fact about the heartbeat alone: it holds
        // whatever the wall clock reads, even if the acquire's stamp regressed.
        Directory.SetLastWriteTimeUtc(_roots.RefreshLockDirectory, _roots.Clock.GetUtcNow().UtcDateTime);
        DateTime before = Directory.GetLastWriteTimeUtc(_roots.RefreshLockDirectory);
        _roots.Clock.Advance(TimeSpan.FromSeconds(19));
        await WaitUntilAsync(() => Directory.Exists(_roots.RefreshLockDirectory)
            && Directory.GetLastWriteTimeUtc(_roots.RefreshLockDirectory) > before);

        Directory.GetLastWriteTimeUtc(_roots.RefreshLockDirectory).ShouldBeGreaterThan(before);
        await follower.AbortAsync(new AccountEmail(IncomingEmail), Token);
    }

    [Fact]
    public async Task ASelfAbortThatCannotFinishLeavesTheFollowerServingRatherThanFaultingIt()
    {
        // The idle self-abort runs on a timer thread, where nothing is awaiting
        // it: an escaping exception is not a failed request but a faulted
        // process, and it would take the follower down along with the import it
        // was tidying up. Phase 3 flagged this and left it. A directory where
        // the export should be is the cheapest way to make the delete throw.
        (_, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower(
            idleTimeout: TimeSpan.FromSeconds(1),
            heartbeatInterval: TimeSpan.FromMilliseconds(30));
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        File.Delete(_roots.ExportPath(OutgoingEmail));
        Directory.CreateDirectory(_roots.ExportPath(OutgoingEmail));

        _roots.Clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(200, Token);

        // Still answering, and still holding the import it could not unwind.
        ImportStatus status = await follower.StatusAsync(Token, new AccountEmail(IncomingEmail));
        status.Imported.ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldNotBeNull();
        Directory.Delete(_roots.ExportPath(OutgoingEmail));
    }

    [Fact]
    public async Task TheRefreshLockIsHeldFromTheExportUntilTheCommitAndReleasedAfterIt()
    {
        (_, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();

        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        Directory.Exists(_roots.RefreshLockDirectory).ShouldBeTrue();

        await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);
        Directory.Exists(_roots.RefreshLockDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task AFollowerWithNoLivePairAnswersExportedNoneAndImportsWithNothingExported()
    {
        // The state both WSL lanes are in after they were logged out by hand, and
        // therefore the state of the first real hand-off.
        RefreshTokenFingerprint fb = await _roots.WriteClaimedAsync(IncomingEmail, IncomingToken, Token);
        using FollowerImport follower = _roots.Follower();

        Result<ImportAnswer, string> answer = await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        answer.IsSuccess.ShouldBeTrue(answer.IsFailure ? answer.Error : null);
        answer.Value.ExportedFingerprint.ShouldBeNull();
        answer.Value.Outgoing.ShouldBeNull();
        Directory.EnumerateFiles(_roots.Mailbox, "*.incoming").ShouldBeEmpty();

        Result<ImportResult, string> result = await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error : null);
        result.Value.Outgoing.ShouldBeNull();
        result.Value.OutgoingFingerprint.ShouldBeNull();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fb);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AClaimedFileRewrittenBetweenTheRequestAndTheStageFailsTheVerifyAndUnwinds()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        // The leader promised fb; what is on disk when F3 runs is something else.
        await File.WriteAllTextAsync(
            _roots.ClaimedPath(IncomingEmail),
            CredentialFiles.Shape("refresh-something-else").ToJsonString(),
            Token);
        using FollowerImport follower = _roots.Follower();

        Result<ImportAnswer, string> answer = await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        answer.IsFailure.ShouldBeTrue();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        File.Exists(_roots.ExportPath(OutgoingEmail)).ShouldBeFalse();
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        Directory.Exists(_roots.RefreshLockDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task AnExportReadBackMismatchUnwindsAndLeavesTheLivePairUntouched()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        // The mailbox refuses the export's bytes: a directory in the export's place
        // is a write that cannot land, which is the shape a short or failed 9P
        // write takes from this side.
        Directory.CreateDirectory(_roots.ExportPath(OutgoingEmail));

        Result<ImportAnswer, string> answer = await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        answer.IsFailure.ShouldBeTrue();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AReIssuedRequestAfterACompletedImportIsAnsweredAlreadyImported()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);

        Result<ImportAnswer, string> again = await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        again.IsSuccess.ShouldBeTrue(again.IsFailure ? again.Error : null);
        again.Value.AlreadyImported.ShouldBeTrue();
        again.Value.Result!.Outgoing?.Value.ShouldBe(OutgoingEmail);
        again.Value.Result.OutgoingFingerprint.ShouldBe(fa);
        // Nothing moved a second time.
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fb);
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AReIssuedRequestWhileTheHoldIsOpenReturnsTheSameExportedAnswer()
    {
        // L3a is re-issued after a leader restart; F1 must not export twice.
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        Result<ImportAnswer, string> again = await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        again.IsSuccess.ShouldBeTrue(again.IsFailure ? again.Error : null);
        again.Value.ExportedFingerprint.ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
    }

    [Fact]
    public async Task AnImportForAnotherAccountWhileAHoldIsOpenIsRefused()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        RefreshTokenFingerprint fc = await _roots.WriteClaimedAsync("c@example.com", "refresh-c", Token);
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        Result<ImportAnswer, string> other = await follower.ImportAsync(_roots.Request("c@example.com", fc), Token);

        other.IsFailure.ShouldBeTrue();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
    }

    [Fact]
    public async Task ARequestNamingAPathOutsideTheMailboxIsRefusedBeforeAnythingIsCopied()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        string outside = Path.Combine(_roots.Root, "elsewhere.credentials.json");
        await File.WriteAllTextAsync(outside, CredentialFiles.Shape(IncomingToken).ToJsonString(), Token);

        Result<ImportAnswer, string> claimedOutside = await follower.ImportAsync(
            _roots.Request(IncomingEmail, fb) with { ClaimedPath = outside },
            Token);
        Result<ImportAnswer, string> exportOutside = await follower.ImportAsync(
            _roots.Request(IncomingEmail, fb) with { ExportPath = Path.Combine(_roots.Root, "stolen.json") },
            Token);

        claimedOutside.IsFailure.ShouldBeTrue();
        claimedOutside.Error.ShouldContain("mailbox");
        exportOutside.IsFailure.ShouldBeTrue();
        exportOutside.Error.ShouldContain("mailbox");
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(Path.Combine(_roots.Root, "stolen.json")).ShouldBeFalse();
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        Directory.Exists(_roots.RefreshLockDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task ALivePairTheStateFileNamesNoAccountForIsRefusedRatherThanExported()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(OutgoingEmail, OutgoingToken, Token);
        RefreshTokenFingerprint fb = await _roots.WriteClaimedAsync(IncomingEmail, IncomingToken, Token);
        await File.WriteAllTextAsync(_roots.StateFilePath, "{\"numStartups\":4}", Token);
        using FollowerImport follower = _roots.Follower();

        Result<ImportAnswer, string> answer = await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        answer.IsFailure.ShouldBeTrue();
        answer.Error.ShouldContain("names no account");
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.ExportPath(OutgoingEmail)).ShouldBeFalse();
        Directory.Exists(_roots.RefreshLockDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task AnUnwindAskedForAfterTheSwapFinishesTheImportInsteadOfDeletingTheExport()
    {
        // The swap landed and something then asked the hold to unwind: a commit
        // that failed part way through F6 to F8, the idle self-abort, an abort
        // arriving late. The outgoing pair exists only as the export by then, so
        // deleting it would lose the lineage rather than strand it — the one
        // failure in this chain that costs a login.
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        // F5 by hand, behind the follower's back: the journal still reads Exported.
        File.Move(_roots.StagingPath, _roots.LivePath, overwrite: true);
        (await _roots.Journal().ReadOpenAsync(Token))!.StepReached.ShouldBe(ImportStep.Exported);

        Result<Unit, string> aborted = await follower.AbortAsync(new AccountEmail(IncomingEmail), Token);

        // The abort is refused, not obeyed, and says why: answering "aborted" for
        // an import that happened would have the leader unclaim a slot whose
        // claimed file F6 has just deleted and never park the export.
        aborted.IsFailure.ShouldBeTrue();
        aborted.Error.ShouldContain("not aborted");
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(OutgoingEmail), Token)).ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fb);
        File.Exists(_roots.ClaimedPath(IncomingEmail)).ShouldBeFalse();
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ACommitRetriedAfterAFirstOneDiedPastTheSwapAnswersImported()
    {
        // The first commit swapped and then failed before its journal write, so
        // the journal still reads Exported and the live pair is already the
        // incoming one. The retry must finish it and say so: answering "not
        // imported" would have the leader unclaim a slot whose claimed file F6
        // deletes, and never park the outgoing pair's export.
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        File.Move(_roots.StagingPath, _roots.LivePath, overwrite: true);

        Result<ImportResult, string> retried = await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);

        retried.IsSuccess.ShouldBeTrue(retried.IsFailure ? retried.Error : null);
        retried.Value.Outgoing?.Value.ShouldBe(OutgoingEmail);
        retried.Value.OutgoingFingerprint.ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fb);
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(OutgoingEmail), Token)).ShouldBe(fa);
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ACommitRetriedAfterAFirstOneThrewPartWayThroughTheBookkeepingFinishesIt()
    {
        // The first commit swapped, released the claimed file, and then threw at
        // F7, leaving the hold open and the journal at Released. The retry must
        // finish the import and answer imported, not "the journal reads
        // Released" until the idle timer happens to get there.
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        bool failOnce = true;
        ClaudeStateFile flaky = new(_roots.StateFilePath, _ =>
        {
            if (failOnce)
            {
                failOnce = false;
                throw new IOException("the state file is locked");
            }

            return Task.CompletedTask;
        });
        using FollowerImport follower = _roots.Follower(stateFile: flaky);
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        await Should.ThrowAsync<IOException>(() => follower.CommitAsync(new AccountEmail(IncomingEmail), Token));
        (await _roots.Journal().ReadOpenAsync(Token))!.StepReached.ShouldBe(ImportStep.Released);

        Result<ImportResult, string> retried = await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);

        retried.IsSuccess.ShouldBeTrue(retried.IsFailure ? retried.Error : null);
        retried.Value.Outgoing?.Value.ShouldBe(OutgoingEmail);
        retried.Value.OutgoingFingerprint.ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fb);
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(OutgoingEmail), Token)).ShouldBe(fa);
        (await _roots.StateFile().ReadAccountBlockAsync(Token))!.Email?.Value.ShouldBe(IncomingEmail);
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        Directory.Exists(_roots.RefreshLockDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task AnAccountBlockThatDoesNotNameTheIncomingAccountIsRefusedBeforeStaging()
    {
        // F7 installs this block after the swap. An empty one would leave the
        // state file naming no one and refuse every later import; a non-string
        // email would throw after the swap on every reconciliation pass.
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        ImportRequest request = _roots.Request(IncomingEmail, fb);

        foreach (System.Text.Json.Nodes.JsonObject block in new System.Text.Json.Nodes.JsonObject[]
        {
            [],
            FollowerRoots.AccountJson("c@example.com"),
            new() { ["emailAddress"] = 42 },
        })
        {
            Result<ImportAnswer, string> answer = await follower.ImportAsync(request with { Account = block }, Token);

            answer.IsFailure.ShouldBeTrue();
            answer.Error.ShouldContain("account block");
        }

        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.ClaimedPath(IncomingEmail)).ShouldBeTrue();
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        Directory.Exists(_roots.RefreshLockDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task AStatusReadTakenWhileACommitIsPatchingWaitsForItAndNeverPairsOneAccountWithTheOthersFingerprint()
    {
        // The read is issued from inside F7, after the swap and before the state
        // file names the incoming account: the one window where reading the two
        // files independently answers B's fingerprint beside A's name. It is given
        // time to finish there; serialized with the commit, it cannot.
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        FollowerImport? follower = null;
        Task<ImportStatus>? during = null;
        ClaudeStateFile patching = new(_roots.StateFilePath, async _ =>
        {
            during ??= follower!.StatusAsync(Token);
            await Task.WhenAny(during, Task.Delay(200, Token));
        });
        using FollowerImport created = _roots.Follower(stateFile: patching);
        follower = created;
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        ImportStatus held = await follower.StatusAsync(Token);
        await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);
        ImportStatus snapshot = await during!;

        held.LiveFingerprint.ShouldBe(fa);
        held.LiveAccount?.Email?.Value.ShouldBe(OutgoingEmail);
        snapshot.LiveFingerprint.ShouldBe(fb);
        snapshot.LiveAccount?.Email?.Value.ShouldBe(IncomingEmail);
    }

    [Fact]
    public async Task ARequestWhoseClaimedAndExportPathsAreTheSameFileIsRefused()
    {
        // F4 would write the export over the claimed file it had just staged
        // from, and F6 would then delete the outgoing pair's only copy: a
        // request that passed every fingerprint check and still lost a pair.
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        string shared = _roots.ClaimedPath(IncomingEmail);

        Result<ImportAnswer, string> answer = await follower.ImportAsync(
            _roots.Request(IncomingEmail, fb) with { ExportPath = shared },
            Token);

        answer.IsFailure.ShouldBeTrue();
        answer.Error.ShouldContain("same file");
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(shared, Token)).ShouldBe(fb);
        Directory.Exists(_roots.RefreshLockDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task AReconcilerUnwindLeavesNoStateThatReadsAsATornSwap()
    {
        // The reconciler's own cleanup, interrupted after its first step. With the
        // staging file deleted first it would leave journal=Exported and no
        // staging file; a later rotation of the live pair would then read as a
        // torn F5 and finish an import whose F5 never ran. Ordered as it is, the
        // export goes first and the evidence stays unambiguous.
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(OutgoingEmail, OutgoingToken, Token);
        RefreshTokenFingerprint fb = await _roots.WriteClaimedAsync(IncomingEmail, IncomingToken, Token);
        using (FollowerImport follower = _roots.Follower())
        {
            await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        }

        await _roots.Reconciler().ReconcileAsync(Token);

        File.Exists(_roots.StagingPath).ShouldBeFalse();
        File.Exists(_roots.ExportPath(OutgoingEmail)).ShouldBeFalse();
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.ClaimedPath(IncomingEmail), Token)).ShouldBe(fb);
    }

    [Fact]
    public async Task ACleanupInterruptedAfterItsStagingDeleteIsStillReadAsNotSwapped()
    {
        // The state an unwind would leave if it deleted the staging file first and
        // then died: journal at Exported, no staging file, a live pair. By the
        // absence of the staging file alone that is indistinguishable from a torn
        // F5, and finishing it would delete the incoming pair's only copy in the
        // mailbox and record an import that never happened. The live fingerprint
        // is what tells them apart: a cleanup never changes it.
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(OutgoingEmail, OutgoingToken, Token);
        RefreshTokenFingerprint fb = await _roots.WriteClaimedAsync(IncomingEmail, IncomingToken, Token);
        using (FollowerImport follower = _roots.Follower())
        {
            await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
            File.Delete(_roots.StagingPath);
        }

        ImportReconciliation done = await _roots.Reconciler().ReconcileAsync(Token);

        done.Imported.ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.ClaimedPath(IncomingEmail), Token)).ShouldBe(fb);
        File.Exists(_roots.ExportPath(OutgoingEmail)).ShouldBeFalse();
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AStatusReadForOneAccountIgnoresAnImportInFlightForAnother()
    {
        (_, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        File.Move(_roots.StagingPath, _roots.LivePath, overwrite: true);

        ImportStatus other = await follower.StatusAsync(Token, new AccountEmail("c@example.com"));

        other.Imported.ShouldBeFalse();
        other.Detail.ShouldContain(IncomingEmail);
    }

    [Fact]
    public async Task AStatusReadWhileTheSwapIsFinishingReportsImported()
    {
        (_, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        File.Move(_roots.StagingPath, _roots.LivePath, overwrite: true);

        ImportStatus status = await follower.StatusAsync(Token);

        // The journal still reads Exported, and the leader must not read that as
        // "nothing happened" and unclaim a slot whose pair is already live here.
        status.JournalStep.ShouldBe(ImportStep.Exported);
        status.Imported.ShouldBeTrue();
    }

    [Fact]
    public async Task AnImportCancelledPartWayThroughGivesTheGateAndTheLockBack()
    {
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => follower.ImportAsync(_roots.Request(IncomingEmail, fb), cancelled.Token));

        // A cancellation is not an IOException, so without a cleanup path that
        // does not use the failed token the process would hold the refresh lock
        // and the mutation gate until it restarted.
        Directory.Exists(_roots.RefreshLockDirectory).ShouldBeFalse();
        Result<ImportAnswer, string> next = await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        next.IsSuccess.ShouldBeTrue(next.IsFailure ? next.Error : null);
        next.Value.ExportedFingerprint.ShouldBe(fa);
    }

    [Fact]
    public async Task AnImportCancelledWhileWaitingForTheRefreshLockGivesTheGateBack()
    {
        // The lock is held by someone else, so the import waits for it with the
        // gate permit already taken and no hold yet built to give it back.
        (RefreshTokenFingerprint fa, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();
        await follower.StatusAsync(Token);
        Directory.CreateDirectory(_roots.RefreshLockDirectory);
        Directory.SetLastWriteTimeUtc(_roots.RefreshLockDirectory, _roots.Clock.GetUtcNow().UtcDateTime);
        using var hangUp = CancellationTokenSource.CreateLinkedTokenSource(Token);
        hangUp.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(
            () => follower.ImportAsync(_roots.Request(IncomingEmail, fb), hangUp.Token));

        Directory.Delete(_roots.RefreshLockDirectory);
        Result<ImportAnswer, string> next = await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);
        next.IsSuccess.ShouldBeTrue(next.IsFailure ? next.Error : null);
        next.Value.ExportedFingerprint.ShouldBe(fa);
    }

    public static bool OnUnix => !OperatingSystem.IsWindows();

    /// <summary>
    /// The staging file is the one F5 renames over the live pair, and a rename
    /// carries the mode with it: a staging file at the umask default would
    /// hand the CLI's own owner-only credential file a world-readable mode on
    /// every import. One leg runs per machine; CI runs both.
    /// </summary>
    [Fact(SkipUnless = nameof(OnUnix), Skip = "File modes are a Unix behavior")]
    public async Task TheStagedAndSwappedPairsAreOwnerOnlyOnUnix()
    {
        (_, RefreshTokenFingerprint fb) = await SeedAsync();
        using FollowerImport follower = _roots.Follower();

        await follower.ImportAsync(_roots.Request(IncomingEmail, fb), Token);

        // The guard is for the platform analyzer; the attribute keeps the leg off Windows.
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(_roots.StagingPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await follower.CommitAsync(new AccountEmail(IncomingEmail), Token);

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(_roots.LivePath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Token);
        }
    }
}
