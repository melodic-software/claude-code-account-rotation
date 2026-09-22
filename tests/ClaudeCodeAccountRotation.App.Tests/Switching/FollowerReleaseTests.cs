using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Switching;
using Shouldly;

namespace ClaudeCodeAccountRotation.App.Tests.Switching;

/// <summary>
/// The follower's half of the park-back: the same two calls, the same export
/// gate, the same commit budget, with the incoming half of the request empty.
/// Every assertion is against what is on disk, by fingerprint.
/// </summary>
public sealed class FollowerReleaseTests : IDisposable
{
    private const string HeldEmail = "a@example.com";
    private const string OtherEmail = "b@example.com";
    private const string HeldToken = "refresh-a";
    private const string OtherToken = "refresh-b";

    private readonly FollowerRoots _roots = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _roots.Dispose();

    [Fact]
    public async Task AReleaseExportsTheLivePairAndStopsWithItStillLive()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport follower = _roots.Follower();

        Result<ImportAnswer, string> answer = await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);

        answer.IsSuccess.ShouldBeTrue(answer.IsFailure ? answer.Error : null);
        answer.Value.ExportedFingerprint.ShouldBe(fa);
        answer.Value.Outgoing?.Value.ShouldBe(HeldEmail);
        // F4 has run and F5 has not, which is the whole of what the gate rests on.
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(HeldEmail), Token)).ShouldBe(fa);
        // Nothing arrives, so nothing is staged: the step is its journal write alone.
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        (await _roots.Journal().ReadOpenAsync(Token))!.StepReached.ShouldBe(ImportStep.Exported);
        (await _roots.Journal().ReadOpenAsync(Token))!.IsRelease.ShouldBeTrue();
    }

    [Fact]
    public async Task ACommitAfterAReleasesExportLeavesThisSideHoldingNothing()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);

        Result<ImportResult, string> result = await follower.CommitAsync(new AccountEmail(HeldEmail), Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error : null);
        result.Value.Outgoing?.Value.ShouldBe(HeldEmail);
        result.Value.OutgoingFingerprint.ShouldBe(fa);
        File.Exists(_roots.LivePath).ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(HeldEmail), Token)).ShouldBe(fa);
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
    }

    /// <summary>
    /// A state file still naming the account whose pair has left would have the
    /// leader's next L1 read an account beside no fingerprint and refuse every
    /// switch to that side with <c>LiveIdentityUnverified</c>.
    /// </summary>
    [Fact]
    public async Task AReleaseClearsTheStateFileSoThisSideReportsHoldingNothing()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);
        await follower.CommitAsync(new AccountEmail(HeldEmail), Token);

        (await _roots.StateFile().ReadAccountBlockAsync(Token))?.Email.ShouldBeNull();
        ImportStatus status = await follower.StatusAsync(Token);
        status.LiveAccount?.Email.ShouldBeNull();
        status.LiveFingerprint.ShouldBeNull();
        // The owner record goes with it: it names a pair this side no longer has.
        File.Exists(Path.Combine(_roots.AppData, "state", "live-owner.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task EveryFingerprintLandsInExactlyOneNonStagingFileAfterACompletedRelease()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);
        await follower.CommitAsync(new AccountEmail(HeldEmail), Token);

        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task NoCodePathReleasesTheLivePairWithoutACommit()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport follower = _roots.Follower();

        // Every request the follower serves, twice, with no commit among them.
        for (int pass = 0; pass < 2; pass++)
        {
            await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);
            await follower.StatusAsync(Token, new AccountEmail(HeldEmail));
            await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);
            await follower.StatusAsync(Token);
        }

        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
    }

    [Fact]
    public async Task AnAbortOfAReleaseDeletesTheExportAndLeavesTheLivePair()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);

        Result<Unit, string> aborted = await follower.AbortAsync(new AccountEmail(HeldEmail), Token);

        aborted.IsSuccess.ShouldBeTrue(aborted.IsFailure ? aborted.Error : null);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.ExportPath(HeldEmail)).ShouldBeFalse();
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AReleaseOfAnAccountThisSideIsNotLiveOnIsRefusedWithNothingExported()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport follower = _roots.Follower();

        Result<ImportAnswer, string> answer = await follower.ImportAsync(
            _roots.ReleaseRequest(OtherEmail, fa),
            Token);

        answer.IsFailure.ShouldBeTrue();
        answer.Error.ShouldContain(HeldEmail);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.ExportPath(OtherEmail)).ShouldBeFalse();
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
    }

    [Fact]
    public async Task AReleaseWhoseLivePairRotatedSinceThePlanExportsThePairThisSideHolds()
    {
        RefreshTokenFingerprint rotated = await _roots.WriteLiveAsync(HeldEmail, "refresh-rotated", Token);
        var planned = RefreshTokenFingerprint.FromRefreshToken(HeldToken);
        using FollowerImport follower = _roots.Follower();

        Result<ImportAnswer, string> answer = await follower.ImportAsync(
            _roots.ReleaseRequest(HeldEmail, planned),
            Token);

        answer.IsSuccess.ShouldBeTrue(answer.IsFailure ? answer.Error : null);
        answer.Value.ExportedFingerprint.ShouldBe(rotated);
        answer.Value.Outgoing?.Value.ShouldBe(HeldEmail);
        // F4 has run and F5 has not: the live file still holds the rotated pair
        // until the existing commit path removes it.
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(rotated);
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(HeldEmail), Token)).ShouldBe(rotated);
        File.Exists(_roots.StagingPath).ShouldBeFalse();
        ImportJournalEntry exported = (await _roots.Journal().ReadOpenAsync(Token))!;
        exported.StepReached.ShouldBe(ImportStep.Exported);
        exported.IsRelease.ShouldBeTrue();
        exported.OutgoingFingerprint.ShouldBe(rotated);

        // The existing commit path is what removes the live file. The export stays
        // until the leader parks it, so this fingerprint has one non-staging copy.
        Result<ImportResult, string> committed = await follower.CommitAsync(new AccountEmail(HeldEmail), Token);

        committed.IsSuccess.ShouldBeTrue(committed.IsFailure ? committed.Error : null);
        committed.Value.OutgoingFingerprint.ShouldBe(rotated);
        File.Exists(_roots.LivePath).ShouldBeFalse();
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(HeldEmail), Token)).ShouldBe(rotated);
        (await _roots.NonStagingFilesHoldingAsync(rotated, Token)).Count.ShouldBe(1);
        (await _roots.NonStagingFilesHoldingAsync(planned, Token)).Count.ShouldBe(0);
    }

    [Fact]
    public async Task AReleaseOfASideHoldingNothingIsRefused()
    {
        using FollowerImport follower = _roots.Follower();

        Result<ImportAnswer, string> answer = await follower.ImportAsync(
            _roots.ReleaseRequest(HeldEmail, RefreshTokenFingerprint.FromRefreshToken(HeldToken)),
            Token);

        answer.IsFailure.ShouldBeTrue();
        answer.Error.ShouldContain("nothing to hand back");
    }

    /// <summary>
    /// The aliasing the record's direction exists to stop. A release of A and a
    /// later switch of A back to this side name the same account; answering the
    /// switch from the release's own row would hand the leader an outgoing
    /// account its journal never planned to park, which its own gate then
    /// refuses with the claim left in the mailbox and the journal open.
    /// </summary>
    [Fact]
    public async Task AReleaseThenASwitchBackToTheSameAccountIsNotAnsweredAlreadyImported()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);
        await follower.CommitAsync(new AccountEmail(HeldEmail), Token);
        // The leader parks the export and later claims the same pair back.
        File.Move(_roots.ExportPath(HeldEmail), _roots.ClaimedPath(HeldEmail));

        Result<ImportAnswer, string> answer = await follower.ImportAsync(_roots.Request(HeldEmail, fa, HeldEmail), Token);

        answer.IsSuccess.ShouldBeTrue(answer.IsFailure ? answer.Error : null);
        answer.Value.AlreadyImported.ShouldBeFalse();
        // It really imported: this side is live on A again, from the claim.
        await follower.CommitAsync(new AccountEmail(HeldEmail), Token);
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
    }

    [Fact]
    public async Task ASwitchThenAReleaseOfTheSameAccountIsNotAnsweredAlreadyImported()
    {
        RefreshTokenFingerprint fb = await _roots.WriteClaimedAsync(OtherEmail, OtherToken, Token);
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.Request(OtherEmail, fb, HeldEmail), Token);
        await follower.CommitAsync(new AccountEmail(OtherEmail), Token);

        Result<ImportAnswer, string> answer = await follower.ImportAsync(_roots.ReleaseRequest(OtherEmail, fb), Token);

        answer.IsSuccess.ShouldBeTrue(answer.IsFailure ? answer.Error : null);
        answer.Value.AlreadyImported.ShouldBeFalse();
        (await _roots.Journal().ReadOpenAsync(Token))!.IsRelease.ShouldBeTrue();
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(OtherEmail), Token)).ShouldBe(fb);
    }

    [Fact]
    public async Task ACommitOfAReleasePastTheBudgetSelfAbortsWithTheLivePairIntact()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport follower = _roots.Follower(commitBudget: TimeSpan.FromSeconds(20));
        await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);
        _roots.Clock.Advance(TimeSpan.FromSeconds(21));

        Result<ImportResult, string> result = await follower.CommitAsync(new AccountEmail(HeldEmail), Token);

        result.IsFailure.ShouldBeTrue();
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.ExportPath(HeldEmail)).ShouldBeFalse();
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
    }

    /// <summary>
    /// The release's own torn F5: the live pair is gone and the journal still
    /// reads <c>Exported</c>. An unwind there would delete the export of a
    /// lineage that is live nowhere, so the reconciler finishes forward.
    /// </summary>
    [Fact]
    public async Task ATornRemovalIsFinishedRatherThanUnwound()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using (FollowerImport follower = _roots.Follower())
        {
            await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);
        }

        // F5 behind the follower's back, with the journal left at Exported.
        File.Delete(_roots.LivePath);

        ImportReconciliation reconciled = await _roots.Reconciler().ReconcileAsync(Token);

        reconciled.Imported.ShouldBeTrue(reconciled.Outcome);
        (await FollowerRoots.FingerprintOfAsync(_roots.ExportPath(HeldEmail), Token)).ShouldBe(fa);
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
    }

    /// <summary>
    /// The other half of that rule: a release stopped before F5 is unwound, and
    /// its export — the redundant copy — goes while the live pair stays.
    /// </summary>
    [Fact]
    public async Task AReleaseStoppedBeforeTheRemovalIsUnwoundWithTheLivePairKept()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using (FollowerImport follower = _roots.Follower())
        {
            await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);
        }

        ImportReconciliation reconciled = await _roots.Reconciler().ReconcileAsync(Token);

        reconciled.Imported.ShouldBeFalse(reconciled.Outcome);
        reconciled.Outcome.ShouldContain("release");
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token)).ShouldBe(fa);
        File.Exists(_roots.ExportPath(HeldEmail)).ShouldBeFalse();
        (await _roots.NonStagingFilesHoldingAsync(fa, Token)).Count.ShouldBe(1);
    }

    /// <summary>
    /// From the review. A follower that dies at <c>Exported</c> stops
    /// re-stamping the refresh lock, and past the 60 s stale threshold a
    /// session in the distro can take that lock and rotate the pair that is
    /// still sitting there. Reading a rotation as a removal would finish the
    /// release over a live credential and have the leader park the
    /// pre-rotation export beside it: two families of one account, which is the
    /// one thing this design exists to prevent.
    /// </summary>
    [Fact]
    public async Task ALivePairRotatedWhileAReleaseWasStoppedIsNotMistakenForOneThatWasHandedBack()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using (FollowerImport follower = _roots.Follower())
        {
            await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);
        }

        // The session's rotation: the live file is still there and holds some
        // other lineage. Nothing was handed back.
        await CredentialFiles.WriteAsync(_roots.LiveDirectory, "refresh-rotated", Token);

        ImportReconciliation reconciled = await _roots.Reconciler().ReconcileAsync(Token);

        reconciled.Imported.ShouldBeFalse(reconciled.Outcome);
        // The rotated pair is still live here and the redundant export is gone,
        // so this account has exactly one family and that side still holds it.
        (await FollowerRoots.FingerprintOfAsync(_roots.LivePath, Token))
            .ShouldBe(RefreshTokenFingerprint.FromRefreshToken("refresh-rotated"));
        File.Exists(_roots.ExportPath(HeldEmail)).ShouldBeFalse();
        (await _roots.Journal().ReadOpenAsync(Token)).ShouldBeNull();
        // And the state file still names the account whose pair is live here.
        (await _roots.StateFile().ReadAccountBlockAsync(Token))!.Email!.Value.Value.ShouldBe(HeldEmail);
    }

    /// <summary>
    /// From the review. A completed release of X leaves a record keyed on X. A
    /// switch of X back that is unwound before its commit must not let that
    /// record answer the leader's commit "already imported": the leader would
    /// clear its journal over a claim still in the mailbox, and X would be in
    /// transit for good with no pass able to resolve it.
    /// </summary>
    [Fact]
    public async Task ACommitIsNeverAnsweredFromTheRecordOfAnEarlierTransactionForTheSameAccount()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport released = _roots.Follower();
        await released.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);
        await released.CommitAsync(new AccountEmail(HeldEmail), Token);
        released.Dispose();

        // The leader parks the export and claims the same pair back.
        File.Move(_roots.ExportPath(HeldEmail), _roots.ClaimedPath(HeldEmail));
        using (FollowerImport switching = _roots.Follower())
        {
            await switching.ImportAsync(_roots.Request(HeldEmail, fa, HeldEmail), Token);
        }

        // The follower restarts and its crash table unwinds the switch, which
        // never reached its swap.
        using FollowerImport restarted = _roots.Follower();
        Result<ImportResult, string> committed = await restarted.CommitAsync(new AccountEmail(HeldEmail), Token);

        committed.IsFailure.ShouldBeTrue(
            "the leader must be told this account is not imported, so it unclaims rather than clearing its journal over a claim in the mailbox");
        // The claim is still there for the leader to put back, and nothing here
        // pretends the switch happened.
        (await FollowerRoots.FingerprintOfAsync(_roots.ClaimedPath(HeldEmail), Token)).ShouldBe(fa);
        (await restarted.StatusAsync(Token, new AccountEmail(HeldEmail))).Imported.ShouldBeFalse();
    }

    [Fact]
    public async Task AStatusReadAboutAnotherAccountNeverReportsThisReleaseAsItsOwn()
    {
        RefreshTokenFingerprint fa = await _roots.WriteLiveAsync(HeldEmail, HeldToken, Token);
        using FollowerImport follower = _roots.Follower();
        await follower.ImportAsync(_roots.ReleaseRequest(HeldEmail, fa), Token);

        ImportStatus status = await follower.StatusAsync(Token, new AccountEmail(OtherEmail));

        status.Imported.ShouldBeFalse();
        status.Detail.ShouldContain(HeldEmail);
    }
}
