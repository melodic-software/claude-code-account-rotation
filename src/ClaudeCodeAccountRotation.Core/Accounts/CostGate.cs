using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Accounts;

/// <summary>What the owner said one account's billing setting is.</summary>
public enum CostSetting
{
    Unknown,
    Off,
    On,
}

/// <summary>
/// The owner's answer, for one account, to the two settings that can charge
/// beyond prepaid credits and the plan: the Console's auto-reload and the
/// plan's extra usage. A setting the request does not name is unknown.
/// </summary>
public sealed record CostSettingsAnswer(CostSetting AutoReload, CostSetting ExtraUsage)
{
    public static readonly CostSettingsAnswer None = new(CostSetting.Unknown, CostSetting.Unknown);

    public bool BothOff => AutoReload == CostSetting.Off && ExtraUsage == CostSetting.Off;

    /// <summary>A wire value: <c>off</c> or <c>on</c>, any case; anything else is unknown.</summary>
    public static CostSetting Setting(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "OFF" => CostSetting.Off,
        "ON" => CostSetting.On,
        _ => CostSetting.Unknown,
    };
}

/// <summary>Why a key was not let through: a fixed code for the page and a sentence for the owner.</summary>
public sealed record CostGateRefusal(string Code, string Message);

/// <summary>
/// The check that runs before a Console key reaches any role's key secret:
/// auto-reload and extra usage must both be off for the account. No API this
/// tool can call reads the Console's auto-reload, so the owner's answer is
/// required on every write and nothing is assumed from silence. Extra usage
/// is also read from the account's usage response when one is held; a read
/// that says it is on refuses whatever the owner answered.
/// </summary>
public static class CostGate
{
    public const string SettingOn = "CostSettingOn";
    public const string Unconfirmed = "CostSettingsUnconfirmed";
    public const string ExtraUsageRead = "ExtraUsageOn";

    public static CostGateRefusal? Check(AccountEmail email, CostSettingsAnswer answer, ExtraUsageState? extraUsageRead)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.AutoReload == CostSetting.On || answer.ExtraUsage == CostSetting.On)
        {
            string which = answer.AutoReload == CostSetting.On && answer.ExtraUsage == CostSetting.On ? "auto-reload and extra usage are"
                : answer.AutoReload == CostSetting.On ? "auto-reload is"
                : "extra usage is";
            return new CostGateRefusal(
                SettingOn,
                which + " on for " + email.Value + ", so its key was not sent. Turn it off first: no lane key may run on an account that can charge beyond its credits.");
        }

        if (!answer.BothOff)
        {
            return new CostGateRefusal(
                Unconfirmed,
                "confirm that auto-reload and extra usage are both off for " + email.Value + " before its key is sent. This tool cannot read auto-reload, so it does not send a key without your answer.");
        }

        if (extraUsageRead is { IsEnabled: true })
        {
            return new CostGateRefusal(
                ExtraUsageRead,
                "the last usage read for " + email.Value + " says extra usage is on, so its key was not sent. Turn it off, refresh the account's usage, and try again.");
        }

        return null;
    }
}
