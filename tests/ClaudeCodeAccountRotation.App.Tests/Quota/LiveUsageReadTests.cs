using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Tests.Quota;

/// <summary>
/// The refresh pass's live read through the CLI (#220): the live account goes
/// through the CLI and nothing else when it answers fresh, through the usage
/// endpoint with the reason logged when it does not, and a parked account
/// never goes near the CLI.
/// </summary>
public sealed class LiveUsageReadTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<RefreshHarness> LiveAsync()
    {
        RefreshHarness harness = new();
        await harness.WriteLiveIdentityAsync("live@example.com", Token);
        await harness.WriteLivePairAsync("refresh-live", harness.Valid, Token);
        return harness;
    }

    [Fact]
    public async Task AFreshCliAnswerIsTheLiveReadAndTheEndpointIsNotAsked()
    {
        using RefreshHarness harness = await LiveAsync();
        DateTimeOffset captured = harness.Clock.GetUtcNow() - TimeSpan.FromSeconds(3);
        harness.LiveUsage.Answers.Enqueue(ScriptedLiveUsage.Ok(captured, percent: 61));

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Usage.Calls.ShouldBe(0);
        harness.LiveUsage.Calls.ShouldBe(1);
        harness.OutcomeFor("live@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.Read);
        UsageSnapshot snapshot = harness.State.LatestFor(RefreshHarness.Email("live@example.com"))!;
        snapshot.Limits.Single().Percent.ShouldBe(61);
        snapshot.CapturedAt.ShouldBe(captured);
    }

    [Fact]
    public async Task ACliFailureFallsBackToTheEndpointWithTheLiveTokenAndLogsWhy()
    {
        using RefreshHarness harness = await LiveAsync();
        harness.LiveUsage.Answers.Enqueue(Result<LiveUsageAnswer, string>.Failure("the CLI answered with a stale snapshot"));
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.Usage.AccessTokens.ShouldBe(["access-refresh-live"]);
        harness.State.LatestFor(RefreshHarness.Email("live@example.com"))!.Limits.Single().Percent.ShouldBe(43);
        harness.RefreshLog.Lines.ShouldContain(line =>
            line.Contains("live@example.com was not read through the CLI", StringComparison.Ordinal)
            && line.Contains("stale snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAnswerFromAnotherLiveAccountIsDiscarded()
    {
        // The CLI reads whatever is live when it runs. A switch that lands
        // while it runs makes its figures another account's, so the endpoint
        // is read with the token of the pair the pass chose.
        using RefreshHarness harness = await LiveAsync();
        harness.LiveUsage.Answers.Enqueue(ScriptedLiveUsage.Ok(harness.Clock.GetUtcNow(), percent: 99));
        harness.LiveUsage.BeforeAnswer = () => harness.WriteLiveIdentityAsync("other@example.com", Token).GetAwaiter().GetResult();
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        await harness.Engine.RunAsync(RefreshRequest.One(RefreshHarness.Email("live@example.com")), Token);

        harness.Usage.AccessTokens.ShouldBe(["access-refresh-live"]);
        harness.State.LatestFor(RefreshHarness.Email("live@example.com"))!.Limits.Single().Percent.ShouldBe(43);
        harness.RefreshLog.Lines.ShouldContain(line => line.Contains("live account changed while the CLI ran", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AParkedAccountIsNeverReadThroughTheCli()
    {
        using RefreshHarness harness = new();
        await harness.ParkAsync("b@example.com", "refresh-b", harness.Valid, Token);
        harness.Usage.Answers.Enqueue(ScriptedUsage.Ok());

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.LiveUsage.Calls.ShouldBe(0);
        harness.Usage.AccessTokens.ShouldBe(["access-refresh-b"]);
    }

    [Fact]
    public async Task AnExpiredLiveTokenStartsNoCli()
    {
        // The CLI would refresh it; the live lineage is the running session's
        // to renew, so the expired-token short-circuit still comes first.
        using RefreshHarness harness = new();
        await harness.WriteLiveIdentityAsync("live@example.com", Token);
        await harness.WriteLivePairAsync("refresh-live", harness.Expired, Token);

        await harness.Engine.RunAsync(RefreshRequest.All, Token);

        harness.LiveUsage.Calls.ShouldBe(0);
        harness.OutcomeFor("live@example.com")!.Kind.ShouldBe(RefreshOutcomeKind.SessionWillRefresh);
    }
}
