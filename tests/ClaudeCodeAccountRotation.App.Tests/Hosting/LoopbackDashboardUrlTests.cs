using ClaudeCodeAccountRotation.App.Hosting;

namespace ClaudeCodeAccountRotation.App.Tests.Hosting;

/// <summary>
/// A fixed port binds through <c>ListenLocalhost</c>, which reports
/// <c>localhost</c> rather than 127.0.0.1. Without it being read as loopback,
/// the default launch printed no dashboard URL and <c>--open</c> opened nothing.
/// </summary>
public sealed class LoopbackDashboardUrlTests
{
    [Theory]
    [InlineData("http://localhost:48211", "http://127.0.0.1:48211")]
    [InlineData("http://127.0.0.1:50123", "http://127.0.0.1:50123")]
    public void TheBoundLoopbackAddressIsTheDashboardUrl(string bound, string expected) =>
        AppComposition.LoopbackDashboardUrl([bound]).ShouldBe(expected);

    [Fact]
    public void NoLoopbackAddressIsNoUrl() =>
        AppComposition.LoopbackDashboardUrl(["http://[::1]:48211"]).ShouldBeNull();
}
