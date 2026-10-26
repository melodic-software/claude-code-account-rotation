using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>The highest level one account's window has fired at, and when that window resets.</summary>
public sealed record LatchedAlert(AccountEmail Account, string Window, AlertLevel Level, DateTimeOffset? ResetsAt);

/// <summary>
/// Fires each threshold once per window: an alert passes <see cref="Admit"/>
/// only when its window has not yet fired at that level. A window's latch is
/// dropped once its recorded reset has passed, so the next window fires again.
/// A window with no reset instant stays latched. Not thread-safe: one caller,
/// the refresh pass, owns it.
/// </summary>
public sealed class AlertLatch
{
    private readonly Dictionary<(AccountEmail Account, string Window), LatchedAlert> _latched = [];

    public AlertLatch(IEnumerable<LatchedAlert>? entries = null)
    {
        foreach (LatchedAlert entry in entries ?? [])
        {
            _latched[(entry.Account, entry.Window)] = entry;
        }
    }

    /// <summary>What to keep across a restart.</summary>
    public IReadOnlyList<LatchedAlert> Entries => [.. _latched.Values];

    /// <summary>The alerts not yet fired for their window at their level, latching them.</summary>
    public IReadOnlyList<UsageAlert> Admit(IReadOnlyList<UsageAlert> alerts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        foreach ((AccountEmail Account, string Window) key in _latched.Where(pair => pair.Value.ResetsAt is DateTimeOffset resets && resets < now).Select(static pair => pair.Key).ToList())
        {
            _latched.Remove(key);
        }

        List<UsageAlert> fresh = [];
        foreach (UsageAlert alert in alerts)
        {
            if (_latched.TryGetValue((alert.Account, alert.Window), out LatchedAlert? held) && held.Level >= alert.Level)
            {
                continue;
            }

            _latched[(alert.Account, alert.Window)] = new LatchedAlert(alert.Account, alert.Window, alert.Level, alert.ResetsAt);
            fresh.Add(alert);
        }

        return fresh;
    }
}
