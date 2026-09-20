using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Switching;
using Shouldly;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// Every row of the follower's crash table, driven by writing the journal and
/// the files a crash at that step would have left and asserting what a fresh
/// reconciliation does with them.
/// </summary>
public sealed class ImportReconcilerTests : IDisposable
{
    private const string OutgoingEmail = "a@example.com";
    private const string IncomingEmail = "b@example.com";

    private readonly FollowerRoots _roots = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _roots.Dispose();

    private async Task<ImportJournalEntry> EntryAsync(ImportStep step, RefreshTokenFingerprint incoming, RefreshTokenFingerprint? outgoing)
    {
        ImportJournalEntry entry = new(
            new AccountEmail(IncomingEmail),
            incoming,
            _roots.ClaimedPath(IncomingEmail),
            _roots.ExportPath(OutgoingEmail),
            outgoing is null ? null : new AccountEmail(OutgoingEmail),
            outgoing,
            FollowerRoots.AccountJson(IncomingEmail),
            outgoing is null ? null : FollowerRoots.AccountJson(OutgoingEmail),
            step,
            _roots.Clock.GetUtcNow());
        await _roots.Journal().WriteAsync(entry, Token);
        return entry;
    }

    [Fact]
    public async Task NoJournalIsNoImportInFlight()
    {
        await _roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);

        ImportReconciliation done = await _roots.Reconciler().ReconcileAsync(Token);

        done.Imported.ShouldBeFalse();
        done.Outgoing.ShouldBeNull();
    }

    [Fact]
    public async Task PlannedUnwindsToNotImportedWithNothingMoved()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);
        RefreshTokenFingerprint fb = await _roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        await EntryAsync(ImportStep.Planned, fb, fa);

        ImportReconciliation done = await _roots.Reconciler().ReconcileAsync(Token);

        done.Imported.ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.ClaimedPath(IncomingEmail), Token)).ShouldBe(fb);
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
    }

    [Fact]
    public async Task StagedDeletesTheStagingFileAndReportsNotImported()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);
        RefreshTokenFingerprint fb = await _roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        await File.WriteAllTextAsync(_roots.StagingPath, CredentialFiles.Shape("refresh-b").ToJsonString(), Token);
        await EntryAsync(ImportStep.Staged, fb, fa);

        ImportReconciliation done = await _roots.Reconciler().ReconcileAsync(Token);

        done.Imported.ShouldBeFalse();
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExportedWithTheSwapNotDoneDeletesTheExportAndTheStagingFile()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(OutgoingEmail, "refresh-a", Token);
        RefreshTokenFingerprint fb = await _roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        await File.WriteAllTextAsync(_roots.StagingPath, CredentialFiles.Shape("refresh-b").ToJsonString(), Token);
        await File.WriteAllTextAsync(_roots.ExportPath(OutgoingEmail), CredentialFiles.Shape("refresh-a").ToJsonString(), Token);
        await EntryAsync(ImportStep.Exported, fb, fa);

        ImportReconciliation done = await _roots.Reconciler().ReconcileAsync(Token);

        done.Imported.ShouldBeFalse();
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        File.Exists(_roots.ExportPath(OutgoingEmail)).ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExportedOverALiveFileThatAlreadyHoldsTheIncomingPairContinuesTheTornSwap()
    {
        // F5 landed and the process died before the journal write. Unwinding here
        // would delete the export of a lineage that is no longer live anywhere,
        // which is the one way this design could lose a pair.
        var fa = RefreshTokenFingerprint.FromRefreshToken("refresh-a");
        var fb = RefreshTokenFingerprint.FromRefreshToken("refresh-b");
        await CredentialFiles.WriteAsync(_roots.LiveDirectory, "refresh-b", Token);
        await _roots.WriteStateFileAsync(OutgoingEmail, Token);
        await _roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        await File.WriteAllTextAsync(_roots.ExportPath(OutgoingEmail), CredentialFiles.Shape("refresh-a").ToJsonString(), Token);
        await EntryAsync(ImportStep.Exported, fb, fa);

        ImportReconciliation done = await _roots.Reconciler().ReconcileAsync(Token);

        done.Imported.ShouldBeTrue();
        done.Outgoing?.Value.ShouldBe(OutgoingEmail);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fb);
        File.Exists(_roots.ClaimedPath(IncomingEmail)).ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(OutgoingEmail), Token)).ShouldBe(fa);
        (await _roots.StateFile().ReadAccountBlockAsync(Token))!.Email!.Value.Value.ShouldBe(IncomingEmail);
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task SwappedContinuesThroughReleaseAndPatch()
    {
        var fa = RefreshTokenFingerprint.FromRefreshToken("refresh-a");
        var fb = RefreshTokenFingerprint.FromRefreshToken("refresh-b");
        await CredentialFiles.WriteAsync(_roots.LiveDirectory, "refresh-b", Token);
        await _roots.WriteStateFileAsync(OutgoingEmail, Token);
        await _roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        await File.WriteAllTextAsync(_roots.ExportPath(OutgoingEmail), CredentialFiles.Shape("refresh-a").ToJsonString(), Token);
        await EntryAsync(ImportStep.Swapped, fb, fa);

        ImportReconciliation done = await _roots.Reconciler().ReconcileAsync(Token);

        done.Imported.ShouldBeTrue();
        File.Exists(_roots.ClaimedPath(IncomingEmail)).ShouldBeFalse();
        (await _roots.StateFile().ReadAccountBlockAsync(Token))!.Email!.Value.Value.ShouldBe(IncomingEmail);
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReleasedContinuesThroughPatch()
    {
        var fa = RefreshTokenFingerprint.FromRefreshToken("refresh-a");
        var fb = RefreshTokenFingerprint.FromRefreshToken("refresh-b");
        await CredentialFiles.WriteAsync(_roots.LiveDirectory, "refresh-b", Token);
        await _roots.WriteStateFileAsync(OutgoingEmail, Token);
        await File.WriteAllTextAsync(_roots.ExportPath(OutgoingEmail), CredentialFiles.Shape("refresh-a").ToJsonString(), Token);
        await EntryAsync(ImportStep.Released, fb, fa);

        ImportReconciliation done = await _roots.Reconciler().ReconcileAsync(Token);

        done.Imported.ShouldBeTrue();
        (await _roots.StateFile().ReadAccountBlockAsync(Token))!.Email!.Value.Value.ShouldBe(IncomingEmail);
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
    }

    [Fact]
    public async Task PatchedFinishesAtTheLastImportRecord()
    {
        var fa = RefreshTokenFingerprint.FromRefreshToken("refresh-a");
        var fb = RefreshTokenFingerprint.FromRefreshToken("refresh-b");
        await CredentialFiles.WriteAsync(_roots.LiveDirectory, "refresh-b", Token);
        await _roots.WriteStateFileAsync(IncomingEmail, Token);
        await File.WriteAllTextAsync(_roots.ExportPath(OutgoingEmail), CredentialFiles.Shape("refresh-a").ToJsonString(), Token);
        await EntryAsync(ImportStep.Patched, fb, fa);

        ImportReconciliation done = await _roots.Reconciler().ReconcileAsync(Token);

        done.Imported.ShouldBeTrue();
        done.Outgoing?.Value.ShouldBe(OutgoingEmail);
        LastImportEntry? last = await _roots.Journal().ReadLastImportAsync(Token);
        last.ShouldNotBeNull();
        last.Outgoing?.Value.ShouldBe(OutgoingEmail);
        last.OutgoingFingerprint.ShouldBe(fa);
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
    }

    [Fact]
    public async Task AnOutgoingPairRotatedWhileTheFollowerWasDownStillUnwindsWithoutLosingIt()
    {
        // The stale lock was stolen and the CLI rotated A while the follower was
        // down, so the live fingerprint is neither fa nor fb. The staging file is
        // still there, which is the evidence that F5 never ran.
        var fa = RefreshTokenFingerprint.FromRefreshToken("refresh-a");
        var fb = RefreshTokenFingerprint.FromRefreshToken("refresh-b");
        await CredentialFiles.WriteAsync(_roots.LiveDirectory, "refresh-a-rotated", Token);
        await _roots.WriteStateFileAsync(OutgoingEmail, Token);
        await _roots.WriteClaimedAsync(IncomingEmail, "refresh-b", Token);
        await File.WriteAllTextAsync(_roots.StagingPath, CredentialFiles.Shape("refresh-b").ToJsonString(), Token);
        await File.WriteAllTextAsync(_roots.ExportPath(OutgoingEmail), CredentialFiles.Shape("refresh-a").ToJsonString(), Token);
        await EntryAsync(ImportStep.Exported, fb, fa);

        ImportReconciliation done = await _roots.Reconciler().ReconcileAsync(Token);

        done.Imported.ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token))
            .ShouldBe(RefreshTokenFingerprint.FromRefreshToken("refresh-a-rotated"));
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        File.Exists(_roots.ExportPath(OutgoingEmail)).ShouldBeFalse();
        (await _roots.NonStagingFilesHoldingAsync(fb, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(0);
    }
}
