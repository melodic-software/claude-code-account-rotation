using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Tests.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Peers;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Switching;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>
/// What a refresh pass does with a slot this side does not hold: it records
/// the outcome and sends nothing. The counters on the two scripted ports are
/// the evidence, because a pass that reached either of them would have moved
/// them.
/// </summary>
public sealed class SharedStoreRefreshTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task HeldByWslAsync(RefreshHarness harness, string email, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(harness.ProfilesRoot, email);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "profile.json"), AppFactory.AccountJson(email).ToJsonString(), cancellationToken);
        await HolderRecordFile.WriteAsync(
            folder,
            new HolderRecord(SideName.Wsl, CredentialFiles.Pair("refresh-" + email).Fingerprint, DateTimeOffset.UnixEpoch),
            cancellationToken);
    }

    private static async Task InTransitAsync(RefreshHarness harness, string email, CancellationToken cancellationToken)
    {
        string mailbox = FileSystemCredentialPairStore.MailboxPath(harness.ProfilesRoot, SideName.Wsl);
        Directory.CreateDirectory(mailbox);
        await File.WriteAllTextAsync(
            Path.Combine(mailbox, FileSystemCredentialPairStore.ClaimedFileName(email)),
            string.Empty,
            cancellationToken);
    }

    [Fact]
    public async Task AHeldSlotIsRecordedAsHeldElsewhereAndCostsNoRequest()
    {
        using RefreshHarness harness = new(sharedStore: true);
        await HeldByWslAsync(harness, "a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.HeldElsewhere);
        harness.Usage.Calls.ShouldBe(0);
        harness.Tokens.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AParkedSlotWithAHandOffInFlightIsRecordedTheSameWayAndCostsNoRequest()
    {
        using RefreshHarness harness = new(sharedStore: true);
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        await InTransitAsync(harness, "a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.HeldElsewhere);
        harness.Usage.Calls.ShouldBe(0);
        harness.Tokens.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task APausedAccountTheOtherSideHoldsSendsNothing()
    {
        using RefreshHarness harness = new(sharedStore: true);
        await HeldByWslAsync(harness, "a@example.com", Token);
        await harness.PauseAsync("a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.HeldElsewhere);
        harness.Tokens.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task WithTheStoreNotSharedTheSameRecordIsIgnoredAndTheSlotReportsItNeedsALogin()
    {
        using RefreshHarness harness = new();
        await HeldByWslAsync(harness, "a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.NeedsLogin);
    }

    [Fact]
    public async Task AParkedSlotBesideAHeldOneIsStillRead()
    {
        using RefreshHarness harness = new(sharedStore: true);
        await HeldByWslAsync(harness, "a@example.com", Token);
        await harness.ParkAsync("b@example.com", "refresh-b", harness.Valid, Token);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.HeldElsewhere);
        harness.OutcomeFor("b@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Read);
        harness.Usage.Calls.ShouldBe(1);
    }

    private static FakePeerRotationInstance Side() => new(mailbox: Path.GetTempPath()) { OutgoingEmail = null, OutgoingRefreshToken = null };

    private static Result<PeerUsageRead, string> Answer(PeerUsageOutcome outcome, string? body = null, TimeSpan? retryAfter = null) =>
        Result<PeerUsageRead, string>.Success(new PeerUsageRead(outcome, body is null ? null : JsonNode.Parse(body)!.AsObject(), retryAfter, outcome.ToString()));

    /// <summary>
    /// A Refresh reads the account the other side holds, through that side and
    /// with its live access token (#185). The leader sends no request of its
    /// own and no token POST, names the pair its holder record expects, and
    /// keeps the figures as an on-demand read.
    /// </summary>
    [Fact]
    public async Task AHeldSlotIsReadThroughTheSideThatHoldsIt()
    {
        FakePeerRotationInstance side = Side();
        side.UsageAnswers.Enqueue(Answer(PeerUsageOutcome.Read, """{"limits":[{"kind":"session","percent":43,"is_active":true}]}"""));
        using RefreshHarness harness = new(sharedStore: true, side);
        await HeldByWslAsync(harness, "a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.One(RefreshHarness.Email("a@example.com")), Token);

        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Read);
        side.UsageReads.ShouldBe([(RefreshHarness.Email("a@example.com"), CredentialFiles.Pair("refresh-a@example.com").Fingerprint)]);
        UsageSnapshot read = harness.State.LatestFor(RefreshHarness.Email("a@example.com"))!;
        read.Source.ShouldBe(QuotaSource.OnDemandRefresh);
        read.Limits.Single().Percent.ShouldBe(43);
        harness.Usage.Calls.ShouldBe(0);
        harness.Tokens.Calls.ShouldBe(0);
    }

    /// <summary>A side that does not answer leaves the card saying so, and spends nothing.</summary>
    [Fact]
    public async Task AHeldSlotWhoseSideDoesNotAnswerSaysWhy()
    {
        FakePeerRotationInstance side = Side();
        using RefreshHarness harness = new(sharedStore: true, side);
        await HeldByWslAsync(harness, "a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        RefreshOutcome outcome = harness.OutcomeFor("a@example.com")!;
        outcome.Kind.ShouldBe(RefreshOutcomeKind.HeldElsewhere);
        outcome.Message.ShouldBe(RefreshMessages.SideUnreachable(SideName.Wsl));
        harness.Budget.GapRemaining(RefreshHarness.Email("a@example.com")).ShouldBeNull();
    }

    /// <summary>
    /// That side's access token is expired or rejected: the session there owns
    /// the lineage, so nobody refreshes it for a read, and the card says why.
    /// </summary>
    [Fact]
    public async Task AHeldSlotWhoseSessionMustRefreshSaysSoAndSendsNoTokenPost()
    {
        FakePeerRotationInstance side = Side();
        side.UsageAnswers.Enqueue(Answer(PeerUsageOutcome.SessionWillRefresh));
        using RefreshHarness harness = new(sharedStore: true, side);
        await HeldByWslAsync(harness, "a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.SessionWillRefresh);
        harness.OutcomeFor("a@example.com")!.Message.ShouldBe(RefreshMessages.SessionWillRefreshOn(SideName.Wsl));
        harness.Tokens.Calls.ShouldBe(0);
    }

    /// <summary>
    /// One usage host whichever side asks it: a 429 relayed by that side locks
    /// out this pass exactly as one answered here would, and the parked account
    /// after it sends nothing.
    /// </summary>
    [Fact]
    public async Task ARateLimitRelayedByTheSideLocksOutThePass()
    {
        FakePeerRotationInstance side = Side();
        side.UsageAnswers.Enqueue(Answer(PeerUsageOutcome.RateLimited, retryAfter: TimeSpan.FromSeconds(120)));
        using RefreshHarness harness = new(sharedStore: true, side);
        await HeldByWslAsync(harness, "a@example.com", Token);
        await harness.ParkAsync("b@example.com", "refresh-b", harness.Valid, Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.OutcomeFor("a@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.RateLimited);
        harness.State.UsageLockedUntil.ShouldBe(harness.Clock.GetUtcNow() + TimeSpan.FromSeconds(120));
        harness.OutcomeFor("b@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.RateLimited);
        harness.Usage.Calls.ShouldBe(0);
    }

    /// <summary>A side that answers it does not hold the account now says so; nothing was spent.</summary>
    [Fact]
    public async Task AHeldSlotTheSideNoLongerHoldsSaysSo()
    {
        FakePeerRotationInstance side = Side();
        side.UsageAnswers.Enqueue(Answer(PeerUsageOutcome.NotHeld));
        using RefreshHarness harness = new(sharedStore: true, side);
        await HeldByWslAsync(harness, "a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.OutcomeFor("a@example.com")!.Message.ShouldBe(RefreshMessages.NotHeldBy(SideName.Wsl));
        harness.Budget.GapRemaining(RefreshHarness.Email("a@example.com")).ShouldBeNull();
    }

    /// <summary>A pair in transit has no holder to ask; the card says a hand-off is in flight.</summary>
    [Fact]
    public async Task AHandOffInFlightSaysSoAndAsksNoSide()
    {
        FakePeerRotationInstance side = Side();
        using RefreshHarness harness = new(sharedStore: true, side);
        await harness.ParkAsync("a@example.com", "refresh-a", harness.Expired, Token);
        await InTransitAsync(harness, "a@example.com", Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.OutcomeFor("a@example.com")!.Message.ShouldBe(RefreshMessages.InTransit);
        side.UsageReads.ShouldBeEmpty();
    }
}
