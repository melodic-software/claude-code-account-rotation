using System.Text.Json;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Tests.Quota;

public sealed class UsageResponseParserTests
{
    /// <summary>Spike 01's recorded response shape, codenamed null buckets included.</summary>
    private static JsonDocument Spike01() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "usage-response-spike01.json")));

    [Fact]
    public void ParsesEveryLimitInTheSpikeFixture()
    {
        using JsonDocument body = Spike01();

        IReadOnlyList<UsageLimit> limits = UsageResponseParser.ParseLimits(body.RootElement).Value;

        limits.Count.ShouldBe(3);
        limits.Select(limit => limit.Kind).ShouldBe([LimitKind.Session, LimitKind.WeeklyAll, LimitKind.WeeklyScoped]);
        limits[0].Percent.ShouldBe(43);
        limits[0].Group.ShouldBe("session");
        limits[0].ResetsAt.ShouldBe(DateTimeOffset.Parse("2026-09-04T05:29:59Z", System.Globalization.CultureInfo.InvariantCulture));
        limits[1].Percent.ShouldBe(20);
        limits.ShouldAllBe(limit => limit.IsActive);
    }

    [Fact]
    public void WeeklyScopedCarriesItsScopeDisplayName()
    {
        using JsonDocument body = Spike01();

        IReadOnlyList<UsageLimit> limits = UsageResponseParser.ParseLimits(body.RootElement).Value;

        UsageLimit scoped = limits.Single(limit => limit.Kind == LimitKind.WeeklyScoped);
        scoped.ScopeDisplayName.ShouldBe("Fable");
        scoped.Percent.ShouldBe(34);
        scoped.Severity.ShouldBe("warning");
    }

    [Fact]
    public void LimitsWithoutAScopeCarryNoDisplayName()
    {
        using JsonDocument body = Spike01();

        IReadOnlyList<UsageLimit> limits = UsageResponseParser.ParseLimits(body.RootElement).Value;

        limits.Where(limit => limit.Kind != LimitKind.WeeklyScoped).ShouldAllBe(limit => limit.ScopeDisplayName == null);
    }

    [Fact]
    public void AnUnknownKindStillParsesAndKeepsItsRawName()
    {
        using var body = JsonDocument.Parse("""
            {"limits":[{"kind":"monthly_something_new","group":"monthly","percent":7.5,"resets_at":1789315200,"is_active":true}]}
            """);

        UsageLimit limit = UsageResponseParser.ParseLimits(body.RootElement).Value.Single();

        limit.Kind.ShouldBe(LimitKind.Unknown);
        limit.RawKind.ShouldBe("monthly_something_new");
        limit.Percent.ShouldBe(7.5);
        limit.ResetsAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1789315200));
    }

    [Theory]
    // The endpoint's number, not the tool's: out of DateTimeOffset's range it
    // must leave the reset time unknown rather than throw out of the parse.
    [InlineData(1000000000000000000L)]
    [InlineData(-1000000000000000000L)]
    public void AnOutOfRangeResetTimeLeavesTheLimitWithoutOne(long absurd)
    {
        using var body = JsonDocument.Parse(
            """{"limits":[{"kind":"session","percent":43,"resets_at":"""
            + absurd.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ""","is_active":true}]}""");

        UsageLimit limit = UsageResponseParser.ParseLimits(body.RootElement).Value.Single();

        limit.ResetsAt.ShouldBeNull();
        limit.Percent.ShouldBe(43);
        limit.Kind.ShouldBe(LimitKind.Session);
    }

    [Theory]
    [InlineData("""{"limits":[{"kind":"session","is_active":true}]}""")]
    [InlineData("""{"limits":[{"kind":"weekly_all","percent":null,"is_active":true}]}""")]
    [InlineData("""{"limits":[{"kind":"weekly_scoped","percent":"43","is_active":true}]}""")]
    public void AKnownLimitWithNoReadablePercentFails(string json)
    {
        using var body = JsonDocument.Parse(json);

        Result<IReadOnlyList<UsageLimit>, string> parsed = UsageResponseParser.ParseLimits(body.RootElement);

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error.ShouldContain("no readable percent");
    }

    [Fact]
    public void AnUnknownKindWithNoReadablePercentIsDropped()
    {
        using var body = JsonDocument.Parse("""
            {"limits":[{"kind":"monthly_something_new","is_active":true},{"kind":"session","percent":43,"is_active":true}]}
            """);

        UsageLimit limit = UsageResponseParser.ParseLimits(body.RootElement).Value.Single();

        limit.Kind.ShouldBe(LimitKind.Session);
        limit.Percent.ShouldBe(43);
    }

    [Fact]
    public void ABodyWithNoLimitsArrayFails()
    {
        using var body = JsonDocument.Parse("""{"five_hour":{"utilization":43}}""");

        Result<IReadOnlyList<UsageLimit>, string> parsed = UsageResponseParser.ParseLimits(body.RootElement);

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error.ShouldContain("no limits array");
    }

    [Fact]
    public void ParsesTheExtraUsageBlock()
    {
        using JsonDocument body = Spike01();

        ExtraUsageState extra = UsageResponseParser.ParseExtraUsage(body.RootElement)!;

        extra.IsEnabled.ShouldBeFalse();
        extra.DisabledReason.ShouldBe("not_enabled");
        extra.SpendLimitReached.ShouldBeFalse();
    }

    [Fact]
    public void ABodyWithoutExtraUsageYieldsNoState()
    {
        using var body = JsonDocument.Parse("""{"limits":[]}""");

        UsageResponseParser.ParseExtraUsage(body.RootElement).ShouldBeNull();
    }
}
