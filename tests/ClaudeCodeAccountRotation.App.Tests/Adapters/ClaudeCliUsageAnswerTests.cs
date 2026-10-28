using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.Process;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// What the CLI's <c>get_usage</c> answer is taken to mean, without a process:
/// every refusal that sends the read to the endpoint instead, the tolerance on
/// the server's capture time, an older answer with no <c>limits[]</c>, and the
/// config directory the child is pointed at for each layout.
/// </summary>
public sealed class ClaudeCliUsageAnswerTests
{
    private const string Id = "usage-1";

    private static readonly DateTimeOffset _started = new(2026, 10, 28, 18, 30, 0, TimeSpan.Zero);

    private static string Line(JsonNode? usage, string subtype = "success", string id = Id) =>
        new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = subtype, ["request_id"] = id, ["response"] = usage },
        }.ToJsonString();

    private static Result<LiveUsageAnswer, string> Parse(JsonNode? usage, string subtype = "success") =>
        ClaudeCliUsageReader.Parse(Line(usage, subtype), Id, _started, _started + TimeSpan.FromSeconds(2));

    private static JsonObject Fresh() => ClaudeCliUsageReaderTests.Usage(_started + TimeSpan.FromSeconds(1));

    [Fact]
    public void AnErrorSubtypeIsARefusal() =>
        Parse(Fresh(), subtype: "error").Error.ShouldBe("the CLI refused the usage request");

    [Fact]
    public void NoUsageForTheLoginIsARefusal()
    {
        JsonObject usage = Fresh();
        usage["rate_limits_available"] = false;

        Parse(usage).Error.ShouldBe("the CLI reports no usage figures for this login");
    }

    [Fact]
    public void NullRateLimitsIsARefusal()
    {
        JsonObject usage = Fresh();
        usage["rate_limits"] = null;

        Parse(usage).Error.ShouldContain("failed inside the CLI");
    }

    [Fact]
    public void AnAnswerWithNoCaptureTimeIsNotTrusted()
    {
        JsonObject usage = Fresh();
        usage["rate_limits"]!.AsObject().Remove("seven_day_breakdown");

        Parse(usage).Error.ShouldContain("no capture time");
    }

    [Fact]
    public void ACaptureTimeWithinTheToleranceBeforeTheStartCounts()
    {
        JsonObject usage = ClaudeCliUsageReaderTests.Usage(_started - ClaudeCliUsageReader.FreshnessTolerance);

        Parse(usage).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ACaptureTimeAheadOfThisClockIsRecordedAsNow()
    {
        JsonObject usage = ClaudeCliUsageReaderTests.Usage(_started + TimeSpan.FromMinutes(5));

        Parse(usage).Value.CapturedAt.ShouldBe(_started + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AResponseToAnotherRequestIsNotTheAnswer() =>
        ClaudeCliUsageReader.Parse(Line(Fresh(), id: "other"), Id, _started, _started).IsFailure.ShouldBeTrue();

    /// <summary>
    /// An answer without the generic array is built from the named windows the
    /// cards know. Codenamed and new windows are ignored, and a window with no
    /// utilization or one outside 0 to 100 is left out rather than shown as 0.
    /// </summary>
    [Fact]
    public void AnAnswerWithoutTheGenericArrayIsBuiltFromTheKnownWindowsOnly()
    {
        JsonObject usage = Fresh();
        JsonObject limits = usage["rate_limits"]!.AsObject();
        limits.Remove("limits");
        limits["seven_day"] = JsonNode.Parse("""{ "utilization": null, "resets_at": null }""");
        limits["model_scoped"] = JsonNode.Parse("""
            [
              { "display_name": "Fable", "utilization": 12, "resets_at": "2026-11-03T22:00:00+00:00" },
              { "display_name": "Broken", "utilization": 140 },
              { "display_name": "Missing" }
            ]
            """);
        limits["brand_new_window"] = JsonNode.Parse("""{ "utilization": 50, "resets_at": null, "limit_dollars": 10 }""");

        JsonArray built = Parse(usage).Value.Body["limits"]!.AsArray();

        built.Select(static entry => entry!["kind"]!.GetValue<string>()).ShouldBe(["session", "weekly_scoped"]);
        built[0]!["percent"]!.GetValue<double>().ShouldBe(23);
        built[1]!["scope"]!["model"]!["display_name"]!.GetValue<string>().ShouldBe("Fable");
    }

    [Fact]
    public void AnAnswerWithNoWindowItKnowsIsARefusal()
    {
        JsonObject usage = Fresh();
        JsonObject limits = usage["rate_limits"]!.AsObject();
        foreach (string known in (string[])["limits", "five_hour", "seven_day", "model_scoped"])
        {
            limits.Remove(known);
        }

        Parse(usage).Error.ShouldContain("no usage window");
    }

    /// <summary>
    /// The leader waits for a read through the other side at least as long as
    /// that side's CLI read and its endpoint fallback together can take, or a
    /// fallback still under way is reported as an unreachable side.
    /// </summary>
    [Fact]
    public void TheLeadersDeadlineCoversTheCliReadAndTheEndpointFallback()
    {
        using HttpClient http = new();
        App.Adapters.Peers.HttpPeerRotationInstance peer = new(Core.Switching.SideName.Wsl, http);

        peer.UsageReadTimeout.ShouldBeGreaterThan(ClaudeCliUsageReader.Bound + App.Adapters.Http.AnthropicEndpoints.RequestTimeout);
    }

    [Fact]
    public void TheDefaultLayoutLeavesTheConfigDirectoryUnset()
    {
        string home = Path.Combine(Path.GetTempPath(), "home");

        Result<string?, string> directory = ClaudeCliUsageReader.ConfigDirectoryFor(Path.Combine(home, ".claude"), Path.Combine(home, ".claude.json"), home);

        directory.IsSuccess.ShouldBeTrue();
        directory.Value.ShouldBeNull();
    }

    [Fact]
    public void AStateFileInsideTheLiveDirectoryNamesIt()
    {
        string live = Path.Combine(Path.GetTempPath(), "elsewhere", "live");

        ClaudeCliUsageReader.ConfigDirectoryFor(live, Path.Combine(live, ".claude.json"), Path.GetTempPath()).Value.ShouldBe(live);
    }

    [Fact]
    public void ALayoutTheCliCannotBePointedAtIsRefused()
    {
        string root = Path.Combine(Path.GetTempPath(), "elsewhere");

        ClaudeCliUsageReader.ConfigDirectoryFor(Path.Combine(root, ".claude"), Path.Combine(root, ".claude.json"), Path.Combine(root, "home"))
            .IsFailure.ShouldBeTrue();
    }
}
