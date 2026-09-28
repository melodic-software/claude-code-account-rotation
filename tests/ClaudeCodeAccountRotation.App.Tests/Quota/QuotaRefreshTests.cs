using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>
/// The refresh pass over a real credential store on disk, with the two outbound
/// ports scripted. The facts here are the security-sensitive ones: exactly one
/// token POST per account per pass, the rotated pair written back or parked
/// where it can be recovered, the live pair never refreshed, and no token in a
/// log line or on a card.
/// </summary>
public sealed class QuotaRefreshTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static long Epoch(DateTimeOffset instant) => instant.ToUnixTimeMilliseconds();

    [Fact]
    public async Task ParkedPairUnauthorizedRefreshesOnceThenRetriesOnce()
    {
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Failed(UsageReadFailureKind.Unauthorized));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(1);
        harness.Usage.Calls.ShouldBe(2);
        harness.Usage.AccessTokens[1].ShouldBe("access-a2");
        JsonObject oauth = await RefreshHarness.ParkedOAuthAsync(folder, Token);
        oauth["refreshToken"]!.GetValue<string>().ShouldBe("refresh-a2");
        oauth["expiresAt"]!.GetValue<long>().ShouldBe(Epoch(harness.Valid));
        oauth["refreshTokenExpiresAt"]!.GetValue<long>().ShouldBe(Epoch(harness.Clock.GetUtcNow().AddDays(28)));
        // The keys the tool knows nothing about survive the rewrite.
        oauth["subscriptionType"]!.GetValue<string>().ShouldBe("max");
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Read);
    }

    [Fact]
    public async Task AnExpiredParkedAccessTokenSkipsTheDoomedRead()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(1);
        harness.Usage.Calls.ShouldBe(1);
        harness.Usage.AccessTokens.Single().ShouldBe("access-a2");
    }

    [Fact]
    public async Task AResponseWithoutRefreshExpiryKeepsTheOldValue()
    {
        using RefreshHarness harness = new();
        DateTimeOffset loginExpiry = harness.Clock.GetUtcNow().AddDays(10);
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token, loginExpiry);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, loginExpiresAt: null));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        JsonObject oauth = await RefreshHarness.ParkedOAuthAsync(folder, Token);
        oauth["refreshToken"]!.GetValue<string>().ShouldBe("refresh-a2");
        oauth["refreshTokenExpiresAt"]!.GetValue<long>().ShouldBe(Epoch(loginExpiry));
    }

    [Fact]
    public async Task LivePairIsNeverRefreshed()
    {
        using RefreshHarness harness = new();
        await harness.WriteLiveIdentityAsync("live@example.com", Token);
        await harness.WriteLivePairAsync("refresh-live", harness.Expired, Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(0);
        harness.Usage.Calls.ShouldBe(0);
        harness.OutcomeFor("live@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.SessionWillRefresh);
    }

    [Fact]
    public async Task AnUnauthorizedLiveReadDoesNotStartTheGapClock()
    {
        // The endpoint rejected the token rather than serving the read, so the
        // reservation it took is refunded exactly as it is on the parked path.
        // Without that refund the live account waits out the sixty-second gap for
        // a read that never happened, which on the live card is the one account
        // the operator is most likely to refresh twice in a row.
        using RefreshHarness harness = new();
        await harness.WriteLiveIdentityAsync("live@example.com", Token);
        await harness.WriteLivePairAsync("refresh-live", harness.Valid, Token);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Failed(UsageReadFailureKind.Unauthorized));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        await harness.Engine.RunAsync(RefreshRequest.All, Token);
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Usage.Calls.ShouldBe(2);
        harness.Tokens.Calls.ShouldBe(0);
        harness.OutcomeFor("live@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Read);
    }

    [Fact]
    public async Task AMissingPairUnderTheGateSendsNoTokenPost()
    {
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        // The question is asked under the gate, immediately before the re-read.
        harness.Logins.OnAsk = _ => File.Delete(Path.Combine(folder, CredentialFiles.FileName));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(0);
        harness.Usage.Calls.ShouldBe(0);
        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.Skipped);
        outcome.Message.ShouldBe(RefreshMessages.PairChanged);
    }

    [Fact]
    public async Task APairThatChangedUnderTheGateIsNotRewritten()
    {
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Logins.OnAsk = _ => File.WriteAllText(
            Path.Combine(folder, CredentialFiles.FileName),
            CredentialFiles.Shape("refresh-somebody-else", harness.Expired, harness.Clock.GetUtcNow().AddDays(28)).ToJsonString());

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(0);
        harness.OutcomeFor("a@example.com")!.Message.ShouldBe(RefreshMessages.PairChanged);
        (await RefreshHarness.ParkedOAuthAsync(folder, Token))["refreshToken"]!.GetValue<string>().ShouldBe("refresh-somebody-else");
    }

    [Fact]
    public async Task ALoginInFlightBlocksTheWriteBack()
    {
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Logins.Busy.Add(Path.GetFullPath(folder));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(0);
        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.Skipped);
        outcome.Message.ShouldBe(RefreshMessages.LoginInProgress);
    }

    [Fact]
    public async Task AWriteBackExceptionRetriesThenStrands()
    {
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Store.ThrowOnWriteWith = FaultyPairStore.Locked;
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Store.WriteAttempts.ShouldBe(3);
        harness.Waits.Requested.Count.ShouldBe(3);
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Stranded);
        harness.Recovery.HasRecoveryFor(folder).ShouldBeTrue();
        // The rotated pair is what was parked, not the dead one still in the folder.
        JsonObject envelope = await RecoveryEnvelopeAsync(harness, "a@example.com");
        CredentialPair rescued = CredentialPair.FromJson(envelope["pair"]!.AsObject()).Value;
        rescued.Fingerprint.ShouldBe(RefreshTokenFingerprint.FromRefreshToken("refresh-a2"));
        envelope["expectedFingerprint"]!.GetValue<string>()
            .ShouldBe(RefreshTokenFingerprint.FromRefreshToken("refresh-a").Sha256Hex);
        harness.Usage.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ATornCredentialFileDuringTheWriteBackRetriesThenStrands()
    {
        // The store re-reads the folder's file to compare fingerprints, so a file
        // caught mid-write throws a parse failure out of the write-back rather
        // than any of the file-system failures the retry loop was written for.
        // Before this was a retry, that exception reached the turn's catch-all and
        // the rotated pair — the only living lineage, the old one already dead —
        // was dropped on the floor with no recovery file to show for it.
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Store.ThrowOnWriteWith = new JsonException("'{' is an invalid start of a value");
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Store.WriteAttempts.ShouldBe(3);
        harness.Waits.Requested.Count.ShouldBe(3);
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Stranded);
        harness.Recovery.HasRecoveryFor(folder).ShouldBeTrue();
        CredentialPair rescued = CredentialPair.FromJson((await RecoveryEnvelopeAsync(harness, "a@example.com"))["pair"]!.AsObject()).Value;
        rescued.Fingerprint.ShouldBe(RefreshTokenFingerprint.FromRefreshToken("refresh-a2"));
    }

    [Fact]
    public async Task AnUnexpectedWriteBackFailureStrandsAtOnce()
    {
        // The retry loop names the failures it expects. This is one nobody
        // thought of, and past the endpoint's answer the old refresh token is
        // already dead, so the only alternative to parking the rotated pair is
        // the turn's catch-all dropping the account's one living lineage. No
        // retry: nothing about an unforeseen failure says a second attempt is
        // any more likely to land than the first.
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Store.ThrowOnWriteWith = new InvalidOperationException("the store faulted in a way the retry loop does not name");
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Store.WriteAttempts.ShouldBe(1);
        harness.Waits.Requested.ShouldBeEmpty();
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Stranded);
        harness.Recovery.HasRecoveryFor(folder).ShouldBeTrue();
        CredentialPair rescued = CredentialPair.FromJson((await RecoveryEnvelopeAsync(harness, "a@example.com"))["pair"]!.AsObject()).Value;
        rescued.Fingerprint.ShouldBe(RefreshTokenFingerprint.FromRefreshToken("refresh-a2"));
    }

    [Fact]
    public async Task AWriteBackRefusedByTheStoreStrandsWithoutARetry()
    {
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Store.RefuseWriteWith = "the parked pair is no longer the one being replaced";
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Store.WriteAttempts.ShouldBe(1);
        harness.Waits.Requested.ShouldBeEmpty();
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Stranded);
        harness.Recovery.HasRecoveryFor(folder).ShouldBeTrue();
    }

    [Fact]
    public async Task AStrandedFolderIsRestoredOnDemand()
    {
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Store.ThrowOnWriteWith = FaultyPairStore.Locked;
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));
        await harness.Engine.RunAsync(RefreshRequest.All, Token);
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Stranded);

        harness.Store.ThrowOnWriteWith = null;
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Recovery.HasRecoveryFor(folder).ShouldBeFalse();
        (await RefreshHarness.ParkedOAuthAsync(folder, Token))["refreshToken"]!.GetValue<string>().ShouldBe("refresh-a2");
        // No second rotation: the restored pair's own access token is still good.
        harness.Tokens.Calls.ShouldBe(1);
        harness.Usage.AccessTokens.Single().ShouldBe("access-a2");
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Read);
    }

    [Fact]
    public async Task AStaleRecoveryFileIsMovedAside()
    {
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Store.ThrowOnWriteWith = FaultyPairStore.Locked;
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));
        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        // The folder was legitimately rewritten since, so the recovery file's
        // compare-and-swap can never apply: it is an orphan lineage.
        harness.Store.ThrowOnWriteWith = null;
        await harness.ParkAsync("a@example.com", "refresh-a3", harness.Valid, Token);
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Recovery.HasRecoveryFor(folder).ShouldBeFalse();
        Directory.GetFiles(Path.Combine(harness.AppData, "recovery", "stale")).Length.ShouldBe(1);
        (await RefreshHarness.ParkedOAuthAsync(folder, Token))["refreshToken"]!.GetValue<string>().ShouldBe("refresh-a3");
        harness.State.RecoveryWarnings.ShouldContain(warning => warning.Contains("a@example.com", StringComparison.Ordinal));
        // Moved aside is resolved, so the account is read rather than reported stranded.
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Read);
    }

    [Fact]
    public async Task ARecoveryWriteFailureReportsLost()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        // A file where the recovery directory must go: the one portable way to
        // make the last-resort write fail on every operating system this runs on.
        await File.WriteAllTextAsync(Path.Combine(harness.AppData, "recovery"), "not a directory", Token);
        harness.Store.ThrowOnWriteWith = FaultyPairStore.Locked;
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.Lost);
        outcome.Message.ShouldBe(RefreshMessages.Lost);
        string lost = harness.RefreshLog.Lines.Single(line => line.Contains("is lost", StringComparison.Ordinal));
        lost.ShouldContain(RefreshTokenFingerprint.FromRefreshToken("refresh-a").Sha256Hex[..12]);
        lost.ShouldContain(RefreshTokenFingerprint.FromRefreshToken("refresh-a2").Sha256Hex[..12]);
        lost.ShouldNotContain("refresh-", Case.Sensitive);
    }

    [Fact]
    public async Task AUsageRateLimitEndsThePassAndLocksEveryLaterRead()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        await harness.ParkAsync("b@example.com", "refresh-b", harness.Valid, Token);
        await harness.ParkAsync("c@example.com", "refresh-c", harness.Valid, Token);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Failed(UsageReadFailureKind.RateLimited, TimeSpan.FromSeconds(30)));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Usage.Calls.ShouldBe(1);
        DateTimeOffset until = harness.Clock.GetUtcNow().AddSeconds(30);
        harness.State.UsageLockedUntil.ShouldBe(until);
        foreach (string email in new[] { "a@example.com", "b@example.com", "c@example.com" })
        {
            RefreshOutcome outcome = harness.OutcomeFor(email)!;
            outcome.Kind.ShouldBe(RefreshOutcomeKind.RateLimited, email);
            outcome.RetryAt.ShouldBe(until);
            outcome.Message.ShouldBe("rate limited, retry in 30 s");
        }
    }

    [Fact]
    public async Task ATokenRateLimitEndsThePass()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        await harness.ParkAsync("b@example.com", "refresh-b", harness.Expired, Token);
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Failed(UsageReadFailureKind.RateLimited, TimeSpan.FromSeconds(30)));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(1);
        harness.Usage.Calls.ShouldBe(0);
        harness.State.TokenLockedUntil.ShouldBe(harness.Clock.GetUtcNow().AddSeconds(30));
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.RateLimited);
        harness.OutcomeFor("b@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.RateLimited);
    }

    [Fact]
    public async Task AMissingRetryAfterLocksForFiveMinutes()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Failed(UsageReadFailureKind.RateLimited, retryAfter: null));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.State.UsageLockedUntil.ShouldBe(harness.Clock.GetUtcNow().AddSeconds(300));
        harness.OutcomeFor("a@example.com")!.Message.ShouldBe("rate limited, retry in 300 s");
    }

    [Fact]
    public async Task ThreePassesWithARateLimitAtNineReadEveryAccount()
    {
        using RefreshHarness harness = new();
        string[] accounts = [.. Enumerable.Range(0, 10).Select(index => "a" + index + "@example.com")];
        foreach (string account in accounts)
        {
            await harness.ParkAsync(account, "refresh-" + account, harness.Valid, Token);
        }

        int readsThisPass = 0;
        harness.Usage.AnswerAt = _ => ++readsThisPass == 9
            ? ScriptedUsage.Failed(UsageReadFailureKind.RateLimited, retryAfter: null)
            : ScriptedUsage.Ok();

        for (int pass = 0; pass < 3; pass++)
        {
            readsThisPass = 0;
            await harness.Engine.RunAsync(RefreshRequest.All, Token);
            // Past the 300-second lockout and the budget's own 60-second gap.
            harness.Clock.Advance(TimeSpan.FromSeconds(301));
        }

        // Eight reads land and the ninth is refused, every pass: the coverage
        // claim is only worth anything if the rate limit really bit each time.
        harness.Usage.Calls.ShouldBe(27);
        foreach (string account in accounts)
        {
            harness.State.LatestFor(RefreshHarness.Email(account)).ShouldNotBeNull(account);
        }
    }

    [Fact]
    public async Task TheBudgetRefusesASecondReadInsideTheGap()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        // Its gated unit is refused, so it must not spend a reservation.
        string blocked = await harness.ParkAsync("b@example.com", "refresh-b", harness.Expired, Token);
        harness.Logins.Busy.Add(Path.GetFullPath(blocked));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        await harness.Engine.RunAsync(RefreshRequest.All, Token);
        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Usage.Calls.ShouldBe(1);
        RefreshOutcome refused = harness.OutcomeFor("a@example.com")!;
        refused.Kind.ShouldBe(RefreshOutcomeKind.BudgetRefused);
        refused.Message.ShouldBe("read 0 s ago");
        refused.RetryAt.ShouldBe(harness.Clock.GetUtcNow().AddSeconds(60));
        harness.Budget.GapRemaining(RefreshHarness.Email("b@example.com")).ShouldBeNull();
    }

    [Fact]
    public async Task ThePassPacesOneSecondBetweenReads()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        await harness.ParkAsync("b@example.com", "refresh-b", harness.Valid, Token);
        await harness.ParkAsync("c@example.com", "refresh-c", harness.Valid, Token);
        harness.Usage.AnswerAt = _ => ScriptedUsage.Ok();

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Usage.Calls.ShouldBe(3);
        harness.Waits.Requested.ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)]);
    }

    [Fact]
    public async Task AStoppingTokenEndsThePassBetweenAccounts()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        await harness.ParkAsync("b@example.com", "refresh-b", harness.Valid, Token);
        using CancellationTokenSource stopping = new();
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());
        harness.Usage.BeforeAnswer = stopping.Cancel;

        await harness.Engine.RunAsync(RefreshRequest.All, stopping.Token);

        harness.Usage.Calls.ShouldBe(1);
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Read);
        harness.OutcomeFor("b@example.com").ShouldBeNull();
    }

    [Fact]
    public async Task TheLiveAccountIsSkippedWhenItsIdentityIsBusy()
    {
        using RefreshHarness harness = new();
        await harness.WriteLiveIdentityAsync("live@example.com", Token);
        await harness.WriteLivePairAsync("refresh-live", harness.Valid, Token);
        // A switch or a login holds the gate, so the stale-identity repair cannot
        // run and nothing here can say whose pair the live directory holds.
        using IDisposable held = await harness.Gate.AcquireAsync(TimeSpan.Zero, Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Usage.Calls.ShouldBe(0);
        RefreshOutcome outcome = harness.OutcomeFor("live@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.Skipped);
        outcome.Message.ShouldBe(RefreshMessages.LiveIdentityUnverified);
    }

    [Fact]
    public async Task APausedPairNearLoginExpiryCostsNoTokenRequest()
    {
        // A parked token is refreshed for a read or by the session a switch hands
        // it to, never to keep it warm: a refresh does not extend the fixed
        // twenty-eight-day login anyway.
        using RefreshHarness harness = new();
        await harness.ParkAsync(
            "a@example.com",
            "refresh-a",
            harness.Expired,
            Token,
            harness.Clock.GetUtcNow().AddDays(2));
        await harness.PauseAsync("a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(0);
        harness.Usage.Calls.ShouldBe(0);
        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.Skipped);
        outcome.Message.ShouldBe(RefreshMessages.Paused);
    }

    [Fact]
    public async Task ASingleAccountRefreshOfAPausedAccountSendsNothing()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync(
            "a@example.com",
            "refresh-a",
            harness.Expired,
            Token,
            harness.Clock.GetUtcNow().AddDays(5));
        await harness.PauseAsync("a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.One(RefreshHarness.Email("a@example.com")), Token);

        harness.Tokens.Calls.ShouldBe(0);
        harness.Usage.Calls.ShouldBe(0);
        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.Skipped);
        outcome.Message.ShouldBe(RefreshMessages.Paused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARestoreWaitsForTheGateAndALoginInFlight(bool loginInFlight)
    {
        // A restore rewrites a credential file by compare-and-swap, so it is a
        // credential mutation exactly like the write-back that stranded the pair
        // in the first place. Outside the gate it could land between a switch's
        // journal write and its unpark; against a login in flight it would rewrite
        // the folder that login is about to be judged on by digest. Either way the
        // file stays where it is and the turn reports why.
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Store.ThrowOnWriteWith = FaultyPairStore.Locked;
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));
        await harness.Engine.RunAsync(RefreshRequest.All, Token);
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Stranded);
        harness.Store.ThrowOnWriteWith = null;
        harness.Clock.Advance(TimeSpan.FromMinutes(2));

        IDisposable? heldGate = null;
        if (loginInFlight)
        {
            harness.Logins.Busy.Add(Path.GetFullPath(folder));
        }
        else
        {
            heldGate = await harness.Gate.AcquireAsync(TimeSpan.Zero, Token);
        }

        try
        {
            await harness.Engine.RunAsync(RefreshRequest.All, Token);
        }
        finally
        {
            heldGate?.Dispose();
        }

        // Nothing was written: the folder still holds the pair the rotation
        // replaced, and the rotated one is still waiting in recovery.
        harness.Recovery.HasRecoveryFor(folder).ShouldBeTrue();
        (await RefreshHarness.ParkedOAuthAsync(folder, Token))["refreshToken"]!.GetValue<string>().ShouldBe("refresh-a");
        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.Skipped);
        outcome.Message.ShouldBe(loginInFlight ? RefreshMessages.LoginInProgress : RefreshMessages.MutationInProgress);
        // The turn ended at the restore: no read, and no second rotation.
        harness.Tokens.Calls.ShouldBe(1);
        harness.Usage.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ASecondUnauthorizedAfterARotationStopsWithoutAnotherRefresh()
    {
        // A freshly rotated access token the endpoint still rejects is not
        // something another rotation can fix, and each one kills a working refresh
        // token to find that out. The retry happens once and then the card says
        // what the operator has to do.
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Failed(UsageReadFailureKind.Unauthorized));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Failed(UsageReadFailureKind.Unauthorized));
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Rotated("a2", harness.Valid, harness.Clock.GetUtcNow().AddDays(28)));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(1);
        harness.Usage.Calls.ShouldBe(2);
        harness.Usage.AccessTokens[1].ShouldBe("access-a2");
        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.ReadFailed);
        outcome.Message.ShouldBe(RefreshMessages.ReadFailedUnauthorized);
    }

    [Fact]
    public async Task ATokenHostFailureReportsTokenRefreshFailed()
    {
        // The token host answered something other than new credentials, so the old
        // pair is untouched and still the account's only lineage. Reading with its
        // expired access token would spend a reservation on a certain 401.
        using RefreshHarness harness = new();
        string folder = await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        harness.Tokens.Answers.Enqueue(ScriptedTokens.Failed(UsageReadFailureKind.Transport));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Tokens.Calls.ShouldBe(1);
        harness.Usage.Calls.ShouldBe(0);
        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.TokenRefreshFailed);
        outcome.Message.ShouldBe(RefreshMessages.TokenRefreshFailed);
        harness.Recovery.HasRecoveryFor(folder).ShouldBeFalse();
        (await RefreshHarness.ParkedOAuthAsync(folder, Token))["refreshToken"]!.GetValue<string>().ShouldBe("refresh-a");
    }

    [Fact]
    public async Task AnUnparsableBodyReportsReadFailed()
    {
        // A 200 whose body this build cannot read is the one failure the card must
        // not round down to "unknown": the numbers on it would be the last pass's.
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok("""{"quota":{"five_hour":43}}"""));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Usage.Calls.ShouldBe(1);
        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.ReadFailed);
        outcome.Message.ShouldBe(RefreshMessages.ReadFailedMalformed);
        harness.State.LatestFor(RefreshHarness.Email("a@example.com")).ShouldBeNull();
    }

    [Fact]
    public async Task ATransportFailureReportsReadFailed()
    {
        // Unreachable, timed out, or any status but 401 and 429: one sentence for
        // all of them, because the adapter's own detail interpolates a message
        // that can name the host and this one is rendered on the page.
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Failed(UsageReadFailureKind.Transport));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Usage.Calls.ShouldBe(1);
        harness.Tokens.Calls.ShouldBe(0);
        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.ReadFailed);
        outcome.Message.ShouldBe(RefreshMessages.ReadFailedTransport);
    }

    [Fact]
    public async Task ASingleAccountRefreshLeavesTheLastPassSummaryAlone()
    {
        // The summary is the header's one line about the roster as a whole, so
        // only a pass over the roster may write it. A single card's Refresh
        // counts one account, and "last pass: 1 read" under nine cards it never
        // looked at says the other nine were tried and came to nothing.
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        await harness.ParkAsync("b@example.com", "refresh-b", harness.Valid, Token);
        harness.Usage.AnswerAt = _ => ScriptedUsage.Ok();
        await harness.Engine.RunAsync(RefreshRequest.All, Token);
        IReadOnlyDictionary<RefreshOutcomeKind, int> afterThePass = harness.State.LastPassSummary!;
        afterThePass[RefreshOutcomeKind.Read].ShouldBe(2);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.Engine.RunAsync(RefreshRequest.One(RefreshHarness.Email("a@example.com")), Token);

        // The single read really happened, so this is about what it recorded and
        // not about a request that did nothing.
        harness.Usage.Calls.ShouldBe(3);
        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Read);
        harness.State.LastPassSummary.ShouldBeSameAs(afterThePass);
    }

    [Fact]
    public async Task ABudgetRefusalAfterACachedReadSaysHowLongAgoItWasRead()
    {
        // A figure loaded from the cache file was read from the endpoint too, by
        // the run before this one, so the refusal owes the operator the same
        // sentence it owes after a read this run made. Without it the card said
        // the read budget was spent and left them guessing how stale the numbers
        // on it were.
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Valid, Token);
        AccountEmail account = RefreshHarness.Email("a@example.com");
        harness.State.RecordSnapshot(new UsageSnapshot(
            account,
            harness.Clock.GetUtcNow().AddSeconds(-30),
            QuotaSource.Cached,
            [],
            ExtraUsage: null));
        // The reservation the previous run's read would have taken, so the gap is
        // what refuses this one.
        harness.Budget.TryReserve(account).ShouldBeTrue();
        harness.Clock.Advance(TimeSpan.FromSeconds(2));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Usage.Calls.ShouldBe(0);
        RefreshOutcome refused = harness.OutcomeFor("a@example.com")!;
        refused.Kind.ShouldBe(RefreshOutcomeKind.BudgetRefused);
        refused.Message.ShouldBe("read 32 s ago");
    }

    [Fact]
    public async Task NoLogLineOrOutcomeCarriesAToken()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        await harness.ParkAsync("b@example.com", "refresh-b", harness.Expired, Token);
        // One account rotates and lands; the other rotates and strands, so the
        // rotation log, the strand log, and the recovery envelope are all written.
        harness.Logins.OnAsk = folder =>
            harness.Store.ThrowOnWriteWith = folder.Contains("b@example.com", StringComparison.Ordinal) ? FaultyPairStore.Locked : null;
        harness.Tokens.AnswerAt = call => ScriptedTokens.Rotated("rotated" + call, harness.Valid, harness.Clock.GetUtcNow().AddDays(28));
        harness.Usage.AnswerAt = _ => ScriptedUsage.Ok();

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Read);
        harness.OutcomeFor("b@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Stranded);
        List<string> everythingWritten =
        [
            .. harness.RefreshLog.Lines,
            .. harness.RecoveryLog.Lines,
            .. harness.State.RecoveryWarnings,
            harness.OutcomeFor("a@example.com")!.Message,
            harness.OutcomeFor("b@example.com")!.Message,
        ];
        everythingWritten.ShouldNotBeEmpty();
        everythingWritten.ShouldAllBe(line => !line.Contains("refresh-", StringComparison.Ordinal));
        everythingWritten.ShouldAllBe(line => !line.Contains("access-", StringComparison.Ordinal));
    }

    private static async Task<JsonObject> RecoveryEnvelopeAsync(RefreshHarness harness, string email)
    {
        string path = Path.Combine(harness.AppData, "recovery", email + ".credentials.json");
        return JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
    }
}
