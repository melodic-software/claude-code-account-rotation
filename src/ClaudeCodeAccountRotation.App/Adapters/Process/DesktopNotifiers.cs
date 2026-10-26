using System.Text;
using System.Xml.Linq;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Ports;

namespace ClaudeCodeAccountRotation.App.Adapters.Process;

/// <summary>
/// A Windows toast through Windows PowerShell 5.1, which can load the WinRT
/// notification types. The toast XML is built here, with every value escaped,
/// and reaches the script base64-encoded, so no alias or address is ever part
/// of the script text.
/// <para>
/// A desktop app's toast needs an AppUserModelID that a Start shortcut carries
/// (<see href="https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotificationmanager.createtoastnotifier"/>,
/// "Sending toast notifications from desktop apps"). This tool installs no
/// shortcut, so the script binds to Windows PowerShell's own Start entry, found
/// with <c>Get-StartApps</c>.
/// </para>
/// </summary>
internal sealed class WindowsToastNotifier(CommandRunner powershell) : IDesktopNotifier
{
    public async Task<Result<Unit, string>> ShowAsync(string title, string body, CancellationToken cancellationToken)
    {
        Result<(int ExitCode, string Output), string> ran = await powershell(
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(title, body)))],
            cancellationToken);
        if (ran.IsFailure)
        {
            return Result<Unit, string>.Failure(ran.Error);
        }

        return ran.Value.ExitCode == 0
            ? Result<Unit, string>.Success(Unit.Value)
            : Result<Unit, string>.Failure("powershell.exe exited with code " + ran.Value.ExitCode + ": " + ran.Value.Output.Trim());
    }

    /// <summary>The toast's XML: the generic template with the title and body as its two text lines.</summary>
    internal static string ToastXml(string title, string body) =>
        new XElement(
            "toast",
            new XElement(
                "visual",
                new XElement("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", title), new XElement("text", body))))
        .ToString(SaveOptions.DisableFormatting);

    internal static string Script(string title, string body) =>
        "$ErrorActionPreference = 'Stop'\n"
        + "[void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]\n"
        + "[void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]\n"
        + "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument\n"
        + "$xml.LoadXml([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(ToastXml(title, body))) + "')))\n"
        + "$app = (Get-StartApps | Where-Object { $_.Name -eq 'Windows PowerShell' } | Select-Object -First 1).AppID\n"
        + "if (-not $app) { throw 'no Start entry to show a toast under' }\n"
        + "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($app).Show([Windows.UI.Notifications.ToastNotification]::new($xml))\n";
}

/// <summary>A desktop notification through libnotify's <c>notify-send</c>, with the title and body as arguments.</summary>
internal sealed class NotifySendNotifier(CommandRunner notifySend) : IDesktopNotifier
{
    public async Task<Result<Unit, string>> ShowAsync(string title, string body, CancellationToken cancellationToken)
    {
        Result<(int ExitCode, string Output), string> ran = await notifySend(["--app-name=claude-code-account-rotation", "--", title, body], cancellationToken);
        return ran.IsFailure ? Result<Unit, string>.Failure(ran.Error)
            : ran.Value.ExitCode == 0 ? Result<Unit, string>.Success(Unit.Value)
            : Result<Unit, string>.Failure("notify-send exited with code " + ran.Value.ExitCode);
    }

    /// <summary>The first <c>notify-send</c> on PATH, or null.</summary>
    public static string? Locate(string? path) =>
        (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static directory => Path.Combine(directory, "notify-send"))
            .FirstOrDefault(File.Exists);
}

/// <summary>Where no desktop notifier is available: the alert still shows on the page.</summary>
internal sealed class UnavailableDesktopNotifier(string reason) : IDesktopNotifier
{
    public Task<Result<Unit, string>> ShowAsync(string title, string body, CancellationToken cancellationToken) =>
        Task.FromResult(Result<Unit, string>.Failure(reason));
}
