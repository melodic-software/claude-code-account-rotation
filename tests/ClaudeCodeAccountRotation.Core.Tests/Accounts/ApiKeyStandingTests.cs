using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

/// <summary>A found key's status, from the Admin API's four, and its expiry against the clock.</summary>
public sealed class ApiKeyStandingTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static ListedApiKey Key(string status, DateTimeOffset? expiresAt = null) => new("apikey_1", "ci", status, null, expiresAt);

    [Theory]
    [InlineData("archived", ApiKeyWarning.Archived)]
    [InlineData("inactive", ApiKeyWarning.Inactive)]
    [InlineData("expired", ApiKeyWarning.Expired)]
    public void EachStatusOtherThanActiveIsShown(string status, ApiKeyWarning expected) =>
        ApiKeyStanding.Of(Key(status), _now).ShouldBe(expected);

    [Fact]
    public void AnActiveKeyWithNoExpiryIsFine() =>
        ApiKeyStanding.Of(Key("active"), _now).ShouldBeNull();

    [Fact]
    public void AnActiveKeyExpiringWithinThirtyDaysExpiresSoon() =>
        ApiKeyStanding.Of(Key("active", _now.AddDays(30)), _now).ShouldBe(ApiKeyWarning.ExpiresSoon);

    [Fact]
    public void AnActiveKeyExpiringPastThirtyDaysIsFine() =>
        ApiKeyStanding.Of(Key("active", _now.AddDays(31)), _now).ShouldBeNull();

    [Fact]
    public void AnActiveKeyWhoseExpiryHasPassedIsExpired() =>
        ApiKeyStanding.Of(Key("active", _now.AddMinutes(-1)), _now).ShouldBe(ApiKeyWarning.Expired);
}
