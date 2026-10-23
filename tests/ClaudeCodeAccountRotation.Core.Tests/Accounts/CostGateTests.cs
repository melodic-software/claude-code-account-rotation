using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Tests.Accounts;

/// <summary>
/// Issue #317 item 1: a role key goes live only when auto-reload and extra
/// usage are both off; either on refuses, and an answer that cannot be
/// established refuses too.
/// </summary>
public sealed class CostGateTests
{
    private static readonly AccountEmail _email = AccountEmail.Parse("lane@example.com").Value;

    private static readonly ExtraUsageState _readOn = new(IsEnabled: true, null, SpendLimitReached: false);
    private static readonly ExtraUsageState _readOff = new(IsEnabled: false, "disabled_by_user", SpendLimitReached: false);

    [Theory]
    [InlineData(CostSetting.On, CostSetting.Off)]
    [InlineData(CostSetting.Off, CostSetting.On)]
    [InlineData(CostSetting.On, CostSetting.On)]
    [InlineData(CostSetting.On, CostSetting.Unknown)]
    public void EitherSettingOnRefuses(CostSetting autoReload, CostSetting extraUsage) =>
        CostGate.Check(_email, new CostSettingsAnswer(autoReload, extraUsage), _readOff)!.Code.ShouldBe("CostSettingOn");

    [Theory]
    [InlineData(CostSetting.Unknown, CostSetting.Unknown)]
    [InlineData(CostSetting.Off, CostSetting.Unknown)]
    [InlineData(CostSetting.Unknown, CostSetting.Off)]
    public void AnUnknownSettingRefuses(CostSetting autoReload, CostSetting extraUsage) =>
        CostGate.Check(_email, new CostSettingsAnswer(autoReload, extraUsage), null)!.Code.ShouldBe("CostSettingsUnconfirmed");

    [Fact]
    public void AReadThatSaysExtraUsageIsOnRefusesWhateverTheOwnerSaid() =>
        CostGate.Check(_email, new CostSettingsAnswer(CostSetting.Off, CostSetting.Off), _readOn)!.Code.ShouldBe("ExtraUsageOn");

    [Fact]
    public void BothOffPassesWithOrWithoutARead()
    {
        CostGate.Check(_email, new CostSettingsAnswer(CostSetting.Off, CostSetting.Off), _readOff).ShouldBeNull();
        CostGate.Check(_email, new CostSettingsAnswer(CostSetting.Off, CostSetting.Off), null).ShouldBeNull();
    }

    [Theory]
    [InlineData("off", CostSetting.Off)]
    [InlineData(" OFF ", CostSetting.Off)]
    [InlineData("on", CostSetting.On)]
    [InlineData("false", CostSetting.Unknown)]
    [InlineData("", CostSetting.Unknown)]
    [InlineData(null, CostSetting.Unknown)]
    public void OnlyOffAndOnAreAnswers(string? text, CostSetting expected) =>
        CostSettingsAnswer.Setting(text).ShouldBe(expected);
}
