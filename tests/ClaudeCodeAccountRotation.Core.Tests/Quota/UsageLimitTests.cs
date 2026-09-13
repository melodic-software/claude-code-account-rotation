using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Tests.Quota;

/// <summary>
/// Pins the one place a bucket's display name is decided. Spread across the
/// assembler and the page it would drift, and a bucket the endpoint adds later
/// would render as an enum member's name rather than as the endpoint's own word
/// for it.
/// </summary>
public sealed class UsageLimitTests
{
    [Theory]
    [InlineData("session", LimitKind.Session, null, "5-hour")]
    [InlineData("weekly_all", LimitKind.WeeklyAll, null, "7-day")]
    [InlineData("weekly_scoped", LimitKind.WeeklyScoped, "Fable", "Fable")]
    // A scoped window whose display name the endpoint omitted, and a kind this
    // build has never heard of: both still render, under the endpoint's own word.
    [InlineData("weekly_scoped", LimitKind.WeeklyScoped, null, "scoped")]
    [InlineData("monthly_something", LimitKind.Unknown, null, "monthly_something")]
    public void ALimitIsLabelledByItsKind(string rawKind, LimitKind kind, string? scopeDisplayName, string expected)
    {
        UsageLimit limit = new(rawKind, kind, Group: null, Percent: 12, Severity: null, ResetsAt: null, scopeDisplayName, IsActive: true);

        limit.Label.ShouldBe(expected);
    }
}
