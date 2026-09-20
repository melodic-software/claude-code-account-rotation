using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.Core.Identity;
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
    public async Task APausedAccountTheOtherSideHoldsIsNotRenewedEither()
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
}
