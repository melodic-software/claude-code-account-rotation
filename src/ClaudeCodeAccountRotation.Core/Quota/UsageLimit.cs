namespace ClaudeCodeAccountRotation.Core.Quota;

/// <summary>
/// The bucket a <see cref="UsageLimit"/> measures. Parsed from the response's
/// <c>kind</c>; anything the endpoint adds later arrives as
/// <see cref="Unknown"/> and still renders, because the card model is driven by
/// the generic <c>limits[]</c> array and never by a bucket's name.
/// </summary>
public enum LimitKind
{
    Unknown,
    Session,
    WeeklyAll,
    WeeklyScoped,
}

/// <summary>
/// One entry of the usage response's <c>limits[]</c> array: the five-hour
/// session window, the weekly all-models window, or a weekly window scoped to
/// one model.
/// </summary>
public sealed record UsageLimit(
    string RawKind,
    LimitKind Kind,
    string? Group,
    double Percent,
    string? Severity,
    DateTimeOffset? ResetsAt,
    string? ScopeDisplayName,
    bool IsActive)
{
    /// <summary>
    /// What the card calls this bucket. The only place a label is decided: the
    /// assembler and the page both read it, so "5-hour" cannot come to mean one
    /// thing on the server and another in the browser.
    /// <para>
    /// A scoped window is named by the endpoint's own display name rather than by
    /// the model behind it, which is how the page shows "Fable" without any
    /// codename reaching this repository; a scoped window the endpoint did not
    /// name still renders, as "scoped". An <see cref="LimitKind.Unknown"/> kind
    /// falls back to the raw kind for the same reason the enum has that member at
    /// all: a bucket added after this build ships must render as something the
    /// operator can look up, not as the word "Unknown".
    /// </para>
    /// </summary>
    public string Label => Kind switch
    {
        LimitKind.Session => "5-hour",
        LimitKind.WeeklyAll => "7-day",
        LimitKind.WeeklyScoped => ScopeDisplayName ?? "scoped",
        _ => RawKind,
    };
}

/// <summary>The usage-credits block: whether extra usage is on and why not.</summary>
public sealed record ExtraUsageState(bool IsEnabled, string? DisabledReason, bool SpendLimitReached);
