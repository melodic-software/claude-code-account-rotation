using System.Text;
using System.Xml.Linq;
using ClaudeCodeAccountRotation.App.Adapters.Process;

namespace ClaudeCodeAccountRotation.App.Tests.Adapters;

/// <summary>
/// An alias is operator text and reaches the toast XML and the script; neither
/// may be broken or steered by it. The toast's shape is the generic template's
/// two text lines from the toast schema.
/// </summary>
public sealed class WindowsToastNotifierTests
{
    private const string Hostile = "a<b>&'\"; Remove-Item x $(whoami)";

    [Fact]
    public void TheTitleAndBodyAreTheToastsTwoTextLinesVerbatim()
    {
        var toast = XElement.Parse(WindowsToastNotifier.ToastXml(Hostile, "body"));

        toast.Descendants("binding").Single().Attribute("template")!.Value.ShouldBe("ToastGeneric");
        toast.Descendants("text").Select(static text => text.Value).ShouldBe([Hostile, "body"]);
    }

    [Fact]
    public void NoAliasTextReachesTheScriptExceptAsBase64()
    {
        string script = WindowsToastNotifier.Script(Hostile, "body");

        script.ShouldNotContain("Remove-Item");
        script.ShouldNotContain("whoami");
        script.ShouldContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(WindowsToastNotifier.ToastXml(Hostile, "body"))));
    }
}
