using ClaudeCodeAccountRotation.Core.Accounts;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

/// <summary>
/// The one lane-outcome parser, held to the lane-outcome record v1 contract,
/// ci-workflows <c>docs/lane-outcome.md</c> (at 1bd4981): the first marker
/// anywhere in the message, one JSON value after it, and the legacy
/// <c>key=value</c> tokens when the JSON cannot be read or carries another
/// <c>v</c>.
/// </summary>
public sealed class LaneOutcomeTests
{
    // The three ```json examples of the contract doc, verbatim.
    private const string KeyLegCreditStop = """
        {
          "v": 1,
          "lane": "intake-triage",
          "outcome": "failure",
          "class": "credit-stop",
          "credential": { "secret": "AUTOMATIONS_CLAUDE_API_KEY", "kind": "api-key", "role": "AUTOMATIONS" },
          "handoff": true,
          "error": { "type": "billing_error", "requestId": "req_fake0000000000000000" },
          "turns": 1, "maxTurns": 30, "models": ["claude-sonnet-5"]
        }
        """;

    private const string SubscriptionLegSuccess =
        """{"v":1,"lane":"intake-triage","outcome":"success","credential":{"secret":"AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN","kind":"oauth-token","role":"AUTOMATIONS"},"fallback":{"reason":"credit-stop"},"turns":5,"models":["claude-sonnet-5"]}""";

    private const string ReviewLaneRateLimit =
        """{"v":1,"lane":"pr-review","outcome":"failure","class":"rate-limit","credential":{"secret":"CLAUDE_CODE_OAUTH_TOKEN","kind":"oauth-token"},"resets":{"at":"2026-10-22T05:00:00Z","window":"five_hour"}}""";

    // The contract doc's whole annotation message, verbatim.
    private const string WholeMessage =
        """AI review skipped: usage limit reached; re-run after it resets (Claude review exited with: failure class=rate-limit resets=2026-10-22T05:00:00Z resets-window=five_hour credential=CLAUDE_CODE_OAUTH_TOKEN). lane-outcome {"v":1,"lane":"pr-review","outcome":"failure","class":"rate-limit","credential":{"secret":"CLAUDE_CODE_OAUTH_TOKEN","kind":"oauth-token"},"resets":{"at":"2026-10-22T05:00:00Z","window":"five_hour"}} Last SDK result: {...}""";

    private static string Annotation(string record) => "Claude lane stopped on a limit.\nlane-outcome " + record;

    [Fact]
    public void TheContractsWholeMessageIsARateLimitOnTheLegacySingleToken() =>
        LaneOutcomes.Parse(WholeMessage).ShouldBe(new LaneOutcome("rate-limit", "CLAUDE_CODE_OAUTH_TOKEN"));

    [Fact]
    public void TheContractsWholeMessageWithItsJsonMadeUnreadableStillReadsItsLegacyTokens() =>
        LaneOutcomes.Parse(WholeMessage.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal)).ShouldBe(new LaneOutcome("rate-limit", "CLAUDE_CODE_OAUTH_TOKEN"));

    [Fact]
    public void TheKeyLegCreditStopExampleReadsItsClassAndSecret() =>
        LaneOutcomes.Parse(Annotation(KeyLegCreditStop)).ShouldBe(new LaneOutcome("credit-stop", "AUTOMATIONS_CLAUDE_API_KEY"));

    [Fact]
    public void TheSubscriptionLegSuccessExampleHasNoClass() =>
        LaneOutcomes.Parse(Annotation(SubscriptionLegSuccess)).ShouldBe(new LaneOutcome(null, "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"));

    [Fact]
    public void TheReviewLaneExampleIsARateLimitOnTheLegacySingleToken() =>
        LaneOutcomes.Parse(Annotation(ReviewLaneRateLimit)).ShouldBe(new LaneOutcome("rate-limit", "CLAUDE_CODE_OAUTH_TOKEN"));

    [Fact]
    public void TheMarkerIsFoundMidLineWithNoNewline() =>
        LaneOutcomes.Parse("Claude lane stopped. lane-outcome " + ReviewLaneRateLimit + " Last turn ran long.")
            .ShouldNotBeNull().Credential.ShouldBe("CLAUDE_CODE_OAUTH_TOKEN");

    [Fact]
    public void ARecordFollowedByFiveThousandCharactersOfFreeTextIsRead() =>
        LaneOutcomes.Parse(Annotation(ReviewLaneRateLimit) + "\n" + new string('x', 5000))
            .ShouldBe(new LaneOutcome("rate-limit", "CLAUDE_CODE_OAUTH_TOKEN"));

    [Fact]
    public void UnknownFieldsAndClassesAreIgnored() =>
        LaneOutcomes.Parse(Annotation("{\"v\":1,\"lane\":\"x\",\"outcome\":\"failure\",\"class\":\"brand-new\",\"future\":{\"deep\":[1,2]},\"credential\":{\"secret\":\"X_CLAUDE_API_KEY\",\"kind\":\"api-key\",\"extra\":true}}"))
            .ShouldBe(new LaneOutcome("brand-new", "X_CLAUDE_API_KEY"));

    [Fact]
    public void AnotherVersionFallsBackToTheLegacyTokens() =>
        LaneOutcomes.Parse("lane=intake class=rate-limit credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN lane-outcome {\"v\":2,\"class\":\"credit-stop\",\"credential\":{\"secret\":\"OTHER_CLAUDE_API_KEY\"}}")
            .ShouldBe(new LaneOutcome("rate-limit", "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"));

    [Theory]
    [InlineData("lane-outcome {\"v\":1,\"class\":")]
    [InlineData("lane-outcome not json")]
    [InlineData("lane-outcome [1,2]")]
    [InlineData("lane-outcome {\"v\":1,\"class\":\"credit-stop\",\"credential\":{\"secret\":\"lower\"}}")]
    public void AnUnreadableRecordFallsBackToTheLegacyTokens(string record) =>
        LaneOutcomes.Parse("lane=intake class=credit-stop credential=AUTOMATIONS_CLAUDE_API_KEY " + record)
            .ShouldBe(new LaneOutcome("credit-stop", "AUTOMATIONS_CLAUDE_API_KEY"));

    [Theory]
    [InlineData("class=rate-limit … credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN).")]
    [InlineData("class=rate-limit credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN)")]
    [InlineData("class=rate-limit credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN.")]
    [InlineData("class=rate-limit credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN,")]
    [InlineData("class=rate-limit credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN;")]
    public void ALegacyCredentialWithTrailingPunctuationYieldsTheName(string message) =>
        LaneOutcomes.Parse(message).ShouldNotBeNull().Credential.ShouldBe("AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN");

    [Theory]
    [InlineData("class=rate-limit credential=lower")]
    [InlineData("class=rate-limit credential=1AUTOMATIONS")]
    [InlineData("class=rate-limit credential=")]
    [InlineData("class=rate-limit")]
    [InlineData("Process completed with exit code 1.")]
    [InlineData("")]
    [InlineData(null)]
    public void AMessageWithNoValidCredentialIsNoRecord(string? message) =>
        LaneOutcomes.Parse(message).ShouldBeNull();

    [Fact]
    public void ACredentialOfOneHundredOneCharactersIsNoRecord()
    {
        LaneOutcomes.Parse("class=rate-limit credential=A" + new string('B', 99)).ShouldNotBeNull();
        LaneOutcomes.Parse("class=rate-limit credential=A" + new string('B', 100)).ShouldBeNull();
    }

    [Fact]
    public void FailureClassIsNotClass() =>
        LaneOutcomes.Parse("failure-class=rate-limit credential=AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN")
            .ShouldBe(new LaneOutcome(null, "AUTOMATIONS_CLAUDE_CODE_OAUTH_TOKEN"));

    [Theory]
    [InlineData("rate-limit", true)]
    [InlineData("credit-stop", true)]
    [InlineData("rate-limited", false)]
    [InlineData("timeout", false)]
    public void RateLimitAndCreditStopAreTheLimitClasses(string value, bool limit) =>
        LaneOutcomes.LimitClasses.Contains(value).ShouldBe(limit);

    [Fact]
    public void OnlyTheTextBeforeLastSdkResultIsRead() =>
        LaneOutcomes.Parse("Claude lane failed. Last SDK result: lane-outcome " + ReviewLaneRateLimit + " class=rate-limit credential=CLAUDE_CODE_OAUTH_TOKEN")
            .ShouldBeNull();

    [Fact]
    public void TheFirstRecordBeforeLastSdkResultIsReadNotASecondAfterIt() =>
        LaneOutcomes.Parse(Annotation(KeyLegCreditStop) + "\nLast SDK result: lane-outcome " + ReviewLaneRateLimit)
            .ShouldNotBeNull().Credential.ShouldBe("AUTOMATIONS_CLAUDE_API_KEY");
}
