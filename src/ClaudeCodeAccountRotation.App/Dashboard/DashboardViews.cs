using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Dashboard;

/// <summary>What the page renders. No token field exists on any of these types.</summary>
internal sealed record DashboardView(
    LiveAccountView? LiveAccount,
    IReadOnlyList<AccountCardView> Accounts,
    string? Banner,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CapturedAt,
    RefreshView Refresh);

internal sealed record LiveAccountView(string? Email, bool HasCredentials, string? Fingerprint);

internal sealed record AccountCardView(
    string Email,
    bool IsLive,
    bool HasCredentials,
    string? Folder,
    UsageView Usage,
    string? UsageNote,
    RefreshStateView Refresh,
    RosterEntryView? Roster = null);

/// <summary>
/// The roster entry behind a card, or null when the account is on the machine
/// but not on the roster: that is the state Adopt exists to end.
/// </summary>
internal sealed record RosterEntryView(
    string Email,
    string? Alias,
    string? Browser,
    string? BrowserProfileDirectory,
    bool Paused,
    string? Notes);

/// <summary>
/// One browser profile the machine already has, for the roster's profile
/// picker. The browser is lowercased the way a roster entry's is, so the page
/// compares the two without knowing about .NET enum casing.
/// </summary>
internal sealed record BrowserProfileView(string Browser, string Directory, string Name, string? Email);

/// <summary>
/// One account's quota as the card shows it: where the newest figures came
/// from, when they were taken, one row per bucket, and the usage-credits line.
/// <para>
/// A card is merged per bucket across every source that has numbers for the
/// account (the live session's statusline tee, an on-demand read, a cached read
/// from before the last restart), so <see cref="Source"/> and
/// <see cref="CapturedAt"/> name the newest source that contributed a row and a
/// row taken from an older one says so itself. Letting the newest whole snapshot
/// win instead would blank the scoped row every time a live session writes the
/// tee, which carries only two of the buckets.
/// </para>
/// </summary>
internal sealed record UsageView(
    string? Source,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<UsageLimitView> Limits,
    UsageCreditsView? Credits)
{
    /// <summary>
    /// The card of an account nothing has numbers for: the rows the page always
    /// shows, every one of them unknown, and no "as of" line. Shared, so a card
    /// handed back by a roster edit carries the same shape a refreshed one does
    /// and the always-three-rows invariant lives on the server rather than in the
    /// browser.
    /// </summary>
    public static UsageView Unread { get; } = new(
        Source: null,
        CapturedAt: null,
        [.. UsageBuckets.Always.Select(UsageLimitView.Unknown)],
        Credits: null);
}

/// <summary>
/// One bucket on a card: the generic row the page renders whatever the endpoint
/// calls the window.
/// <para>
/// Two flags say what the row knows, and the page reads them in this order.
/// <see cref="Known"/> false means no source carried this bucket at all, which
/// renders as "unknown". <see cref="WindowReset"/> true means a source did carry
/// it but the window has reset since it was captured, so <see cref="Percent"/>
/// is null and the card says so rather than showing a figure that now measures
/// nothing. Otherwise the percentage stands.
/// </para>
/// <para>
/// <see cref="Source"/> and <see cref="CapturedAt"/> are set only when this row
/// came from a different source than the card's own; otherwise the card's one
/// "as of" line speaks for it.
/// </para>
/// </summary>
internal sealed record UsageLimitView(
    string Kind,
    string Label,
    double? Percent,
    DateTimeOffset? ResetsAt,
    string? Severity,
    string? Source,
    DateTimeOffset? CapturedAt,
    bool Known,
    bool WindowReset)
{
    /// <summary>A row for a bucket no source carried: named and ordered, and admittedly unknown.</summary>
    public static UsageLimitView Unknown(UsageLimit bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        return new UsageLimitView(
            bucket.RawKind,
            bucket.Label,
            Percent: null,
            ResetsAt: null,
            Severity: null,
            Source: null,
            CapturedAt: null,
            Known: false,
            WindowReset: false);
    }
}

/// <summary>The usage-credits block: whether extra usage is on, why not, and whether the spend limit stopped it.</summary>
internal sealed record UsageCreditsView(bool Enabled, string? DisabledReason, bool SpendLimitReached);

/// <summary>
/// How one account's last turn in a refresh pass went, or <c>idle</c> before
/// there has been one. <see cref="State"/> is the outcome kind in kebab-case
/// because the app configures no JSON enum converter: every enum-shaped field on
/// this page is a string on the wire.
/// </summary>
internal sealed record RefreshStateView(string State, string? Message, DateTimeOffset? RetryAt)
{
    /// <summary>Nothing has read this account yet, and nothing refused to.</summary>
    public static RefreshStateView Idle { get; } = new("idle", Message: null, RetryAt: null);
}

/// <summary>
/// The pass as a whole rather than one card's share of it: whether one is
/// running, when the host lockout that refuses every read lifts, and one line on
/// what the last pass came to.
/// </summary>
internal sealed record RefreshView(bool InProgress, DateTimeOffset? LockedUntil, string? Summary);

/// <summary>
/// The buckets a card always shows a row for, in the order it shows them, as
/// empty limits whose only job is to name and label a row no source carried.
/// The raw kinds are the usage endpoint's own words, which is what lets a
/// placeholder and a real row be recognised as the same bucket.
/// <para>
/// The scoped bucket is the generic weekly-scoped window with no display name,
/// which <see cref="UsageLimit.Label"/> renders as "scoped". The model behind it
/// is never named here: a card says what the endpoint called it, or nothing.
/// </para>
/// </summary>
internal static class UsageBuckets
{
    public static UsageLimit Session { get; } = Empty("session", LimitKind.Session);

    public static UsageLimit WeeklyAll { get; } = Empty("weekly_all", LimitKind.WeeklyAll);

    public static UsageLimit WeeklyScoped { get; } = Empty("weekly_scoped", LimitKind.WeeklyScoped);

    public static IReadOnlyList<UsageLimit> Always { get; } = [Session, WeeklyAll, WeeklyScoped];

    private static UsageLimit Empty(string rawKind, LimitKind kind) =>
        new(rawKind, kind, Group: null, Percent: 0, Severity: null, ResetsAt: null, ScopeDisplayName: null, IsActive: false);
}

internal sealed record SwitchOutcomeView(
    string Now,
    string? ParkedAs,
    string? CliEmail,
    string? CliError,
    bool IdentityMismatchWarning,
    DateTimeOffset At);

internal sealed record SwitchRefusalView(string Refusal, string Message);

/// <summary>
/// A login in flight, as the page sees it. The sign-in URL is an authorize URL
/// and carries no token; the message comes from the runner's fixed vocabulary,
/// so neither the one-time code nor the CLI's own output ever reaches here.
/// </summary>
internal sealed record LoginSessionView(
    string Id,
    string Email,
    string State,
    string? Message,
    string? SignInUrl,
    string? BrowserError,
    DateTimeOffset ExpiresAt);

/// <summary>Held across requests: the last reconciliation report for the banner.</summary>
internal sealed class DashboardState
{
    public ReconciliationReport? LastReconciliation { get; set; }

    /// <summary>
    /// The windows the tee held immediately before the last switch this tool
    /// performed. A session that was mid-turn at switch time bills its response
    /// to the outgoing account and then writes those windows under the incoming
    /// account's name, so a snapshot still carrying these reset times belongs to
    /// the account that just left, whatever it says. Cleared by the first
    /// snapshot whose reset times differ.
    /// ponytail: in memory, like the reconciliation report beside it. A restart
    /// forgets the stash, and the worst case is one stale card until the next
    /// statusline write.
    /// </summary>
    public PreSwitchWindows? PreSwitchWindows { get; set; }
}

/// <summary>The outgoing account's last known reset times, kept only to disown them.</summary>
internal sealed record PreSwitchWindows(DateTimeOffset? FiveHourResetsAt, DateTimeOffset? SevenDayResetsAt);
