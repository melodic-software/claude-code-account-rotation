using ClaudeCodeAccountRotation.Core.Identity;

namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>
/// One observation of the subscription windows as a live session saw them,
/// read from the <c>rate-limits</c> tee file. This is the free tier of the
/// refresh contract: the numbers a session already paid for, with no request of
/// the tool's own. <see cref="Account"/> is the tee's <c>account.email</c>,
/// which the writer stamps only when it could attribute the observation, so it
/// is absent on an older writer and on an observation it could not attribute.
/// </summary>
public sealed record StatuslineSnapshot(
    DateTimeOffset CapturedAt,
    string? SessionId,
    AccountEmail? Account,
    double? FiveHourPercent,
    DateTimeOffset? FiveHourResetsAt,
    double? SevenDayPercent,
    DateTimeOffset? SevenDayResetsAt);
