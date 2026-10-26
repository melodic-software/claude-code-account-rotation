using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Quota;

/// <summary>
/// Turns the alerts a pass computed into desktop notifications, each threshold
/// once per window (<see cref="AlertLatch"/>). The latch is kept in
/// <c>alert-latch.json</c> under app data, so a restart does not notify again
/// for a window that already fired. A notification names the account, the CI
/// roles it backs, and the best spare account. It never acts.
/// </summary>
internal sealed partial class UsageAlertNotifier
{
    public const string FileName = "alert-latch.json";

    private readonly string _path;
    private readonly IDesktopNotifier _desktop;
    private readonly ILogger<UsageAlertNotifier> _logger;
    private AlertLatch? _latch;

    public UsageAlertNotifier(string appDataDirectory, IDesktopNotifier desktop, ILogger<UsageAlertNotifier> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        _path = Path.Combine(Path.GetFullPath(appDataDirectory), FileName);
        _desktop = desktop;
        _logger = logger;
    }

    public async Task NotifyAsync(
        IReadOnlyList<UsageAlert> alerts,
        Roster roster,
        Func<AccountEmail, IReadOnlyList<UsageLimit>> limits,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roster);
        _latch ??= new AlertLatch(await ReadAsync(cancellationToken));
        IReadOnlyList<UsageAlert> fresh = _latch.Admit(alerts, now);
        if (fresh.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await AtomicJsonFile.WriteAsync(_path, ToJson(_latch.Entries), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A latch that could not be kept costs one repeat after a restart, not the notification.
            LogLatchNotWritten(exception.GetType().Name);
        }

        foreach (UsageAlert alert in fresh)
        {
            (string title, string body) = Text(alert, roster, limits, now);
            Result<Unit, string> shown = await _desktop.ShowAsync(title, body, cancellationToken);
            if (shown.IsFailure)
            {
                LogNotShown(shown.Error);
            }
        }
    }

    /// <summary>The notification's two lines. Percentages are whole numbers, as the cards show them.</summary>
    internal static (string Title, string Body) Text(UsageAlert alert, Roster roster, Func<AccountEmail, IReadOnlyList<UsageLimit>> limits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(roster);
        RosterEntry? entry = roster.Find(alert.Account);
        string title = Name(entry, alert.Account) + ": " + alert.Window + " usage at " + Percent(alert.Percent);
        StringBuilder body = new();
        IReadOnlyList<string> roles = [.. (entry?.CiRoles ?? []).Select(static assignment => assignment.Role.Name)];
        body.Append(roles.Count == 1 ? "Backs CI role " : "Backs CI roles ").Append(string.Join(", ", roles)).Append(". ");
        body.Append("Past the ").Append(Percent(alert.Threshold)).Append(alert.Level == AlertLevel.Critical ? " critical threshold. " : " warning. ");
        if (UsageAlerts.BestSpare(roster, alert.Account, limits, now) is AccountEmail spare)
        {
            double used = limits(spare).Where(limit => !limit.HasResetBy(now)).Max(static limit => limit.Percent);
            body.Append("Best spare: ").Append(Name(roster.Find(spare), spare)).Append(" (").Append(Percent((decimal)used)).Append(" used).");
        }
        else
        {
            body.Append("No spare account has headroom.");
        }

        return (title, body.ToString());
    }

    private static string Name(RosterEntry? entry, AccountEmail email) =>
        string.IsNullOrWhiteSpace(entry?.Alias) ? email.Value : entry.Alias;

    private static string Percent(decimal value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    private async Task<IReadOnlyList<LatchedAlert>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (JsonNode.Parse(await SharedFileReader.ReadAllBytesAsync(_path, cancellationToken)) is not JsonArray array)
            {
                return [];
            }

            List<LatchedAlert> entries = [];
            foreach (JsonObject item in array.OfType<JsonObject>())
            {
                if (item["account"]?.GetValue<string>() is string account
                    && AccountEmail.Parse(account) is { IsSuccess: true } email
                    && item["window"]?.GetValue<string>() is string window
                    && Enum.TryParse(item["level"]?.GetValue<string>(), ignoreCase: true, out AlertLevel level))
                {
                    DateTimeOffset? resets = item["resetsAt"]?.GetValue<string>() is string text
                        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
                        ? parsed
                        : null;
                    entries.Add(new LatchedAlert(email.Value, window, level, resets));
                }
            }

            return entries;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or FormatException)
        {
            // Missing or unreadable: start with nothing latched, which at worst notifies once more.
            return [];
        }
    }

    private static JsonArray ToJson(IReadOnlyList<LatchedAlert> entries) =>
        [.. entries.Select(static entry => (JsonNode)new JsonObject
        {
            ["account"] = entry.Account.Value,
            ["window"] = entry.Window,
            ["level"] = entry.Level == AlertLevel.Critical ? "critical" : "warning",
            ["resetsAt"] = entry.ResetsAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        })];

    [LoggerMessage(Level = LogLevel.Warning, Message = "a usage alert notification was not shown: {Reason}")]
    private partial void LogNotShown(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the usage alert latch could not be written ({Failure})")]
    private partial void LogLatchNotWritten(string failure);
}
