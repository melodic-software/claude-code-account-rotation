using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Switching;

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

/// <summary>
/// One account as the page renders it.
/// <para>
/// <c>Standing</c> and <c>NextResetAt</c> say where the server placed this card
/// in the list and why, so the page states the wait without re-deriving it.
/// <c>Standing</c> is the group the account is in, a lower-case word because the
/// app configures no JSON enum converter, and <c>NextResetAt</c> is the instant
/// the card sorted by: when the account frees up, or null when there is no wait
/// to state or none that can be dated.
/// </para>
/// <para>
/// <c>LoginExpiresAt</c> is when the login itself runs out: the
/// <c>refreshTokenExpiresAt</c> of the account's credential pair, live or
/// parked. <c>LoggedInAt</c> is when that login was last made: the
/// <c>profileFetchedAt</c> the CLI stamped on the account block, read from the
/// state file for the live account and from <c>profile.json</c> for a parked
/// one. Either is null when its source is absent, carries no such instant, or
/// cannot be read, and the page leaves that half of its line off.
/// </para>
/// <para>
/// <c>Slot</c> is what this account's slot in the shared store holds, as one
/// plain lower-case word, or null when the store is not shared. It says where
/// the pair is; <c>Chip</c> is how the page says it, in the vocabulary design
/// 12 fixes: <c>live here</c>, <c>parked</c>, <c>in use by wsl</c> (with
/// <c>(offline)</c> when that side is not answering) and
/// <c>in transit to wsl</c>. A slot nothing has ever been parked in carries no
/// chip, because the card's own credential word already says "needs login".
/// </para>
/// <para>
/// <c>CanSwitchHere</c> and <c>OfferedTo</c> are the two switch controls'
/// enabled state, decided here rather than in the browser: this side's Switch
/// button, and the sides whose picker may offer this account. Both are false
/// or empty for a pair another side holds or one in transit, which is R6's
/// "the page disables the button"; the planner refuses it anyway when the
/// button is bypassed.
/// </para>
/// <para>
/// <c>HeldAway</c> is the one fact the page still branches on: a slot whose
/// pair is on the other side has no login to renew and nothing to remove here.
/// </para>
/// <para>
/// Every optional member carries a default, so a route that hands back a card
/// for an account nothing has read constructs one unchanged.
/// </para>
/// </summary>
internal sealed record AccountCardView(
    string Email,
    bool IsLive,
    bool HasCredentials,
    string? Folder,
    UsageView Usage,
    string? UsageNote,
    RefreshStateView Refresh,
    RosterEntryView? Roster = null,
    string Standing = "unread",
    DateTimeOffset? NextResetAt = null,
    DateTimeOffset? LoginExpiresAt = null,
    DateTimeOffset? LoggedInAt = null,
    string? Slot = null,
    string? Chip = null,
    bool CanSwitchHere = false,
    bool HeldAway = false,
    IReadOnlyList<string>? OfferedTo = null);

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
/// placeholder and a real row be recognized as the same bucket.
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

/// <summary>
/// A refused switch as the page reads it: the enum name, which is the stable
/// handle an operator can search for, and one sentence saying what happened.
/// <para>
/// Every route that refuses a switch answers in this vocabulary, so the same
/// refusal reads the same whether it came from this side's switch or the other
/// side's. Every member is named and the default throws, following
/// <see cref="DashboardAssembler"/>'s own rule for the wire: a member added
/// later would otherwise reach the page as its bare enum name, which is the
/// thing this type exists to stop.
/// </para>
/// </summary>
internal sealed record SwitchRefusalView(string Refusal, string Message)
{
    public static SwitchRefusalView Of(SwitchRefusal refusal) => new(refusal.ToString(), Describe(refusal));

    private static string Describe(SwitchRefusal refusal) => refusal switch
    {
        SwitchRefusal.TargetIsLiveDirectory => "The target folder is the live config directory.",
        SwitchRefusal.TargetHasNoCredentials => "That account has no parked credentials; log in first.",
        SwitchRefusal.TargetHasNoAccountBlock => "That profile folder carries no account identity.",
        SwitchRefusal.AlreadyOnTarget => "That account is already live.",
        SwitchRefusal.SharesLiveRefreshToken => "That parked pair is the live pair's own lineage; a second holder is never created.",
        SwitchRefusal.RefreshLockPresent => "A session is refreshing its token right now; try again in a moment.",
        SwitchRefusal.TargetStrandedInRecovery => "This account's credentials are stranded in recovery after a failed refresh; a restart or a per-card refresh restores them.",
        SwitchRefusal.TargetLoginExpired => "That account's login has expired; log in again.",
        SwitchRefusal.SwitchingBlockedByManagedPolicy => "A device-managed login policy pins this machine to one organization.",
        SwitchRefusal.ManagedPolicyUnreadable => "A device-managed login policy exists but could not be read; switching stays off until it can be.",
        SwitchRefusal.LiveIdentityUnverified => "The live identity could not be verified; see the banner.",
        SwitchRefusal.HeldByOtherSide => "The other side of this machine has that account's pair in its own live directory. Switch that side off it first: one account holds one pair per machine, and it is moved rather than copied.",
        // Raised for a mailbox file naming the target and for one naming the
        // account this side would park, which is the planner's
        // OutgoingSlotInTransit flag rather than a refusal of its own.
        SwitchRefusal.SlotInTransit => "A hand-off is in flight for that account or for the one this side would park, so neither pair may move until it finishes or is cancelled.",
        SwitchRefusal.SideOffline => "That side is not answering, or is not a build this one will hand a pair to; the side's own line says which. Start it and try again.",
        SwitchRefusal.ExportNotVerified => "The pair that side exported did not read back on this volume as the pair it named, so the switch was refused and nothing was swapped.",
        SwitchRefusal.PeerDidNotImport => "That side did not complete the import, so the account has been put back in its slot.",
        SwitchRefusal.ForeignFamily => "That side is signed in to that account with a second token family, made when it was logged in again here while that side was unreachable. Switching that side away would hand the family back, and the store keeps one family per account. Switch again with quarantine to move that family into quarantine instead, where it is kept and never used.",
        SwitchRefusal.NothingToRelease => "That side is answering and holds no account, so there is nothing to hand back.",
        SwitchRefusal.MutationInProgress => "Another credential change is in progress.",
        SwitchRefusal.RefreshInProgress => "A usage refresh is reading this machine's accounts right now; switch again when it finishes.",
        SwitchRefusal.LoginInProgress => "A login is running against one of those folders; finish it or let it expire first.",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
    };
}

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

/// <summary>
/// One publication of the values held across requests. A reader that takes this
/// reference sees the reconciliation report, the hand-off line, and the
/// pre-switch windows together.
/// <para>
/// <c>HandOffBanner</c> is what the last hand-off reconciliation left
/// standing, or null when it left nothing: a claim the other side has not
/// answered for is a pair the operator can see nowhere else, since the slot is
/// claimed and neither side holds it yet. It is a warning rather than the
/// banner because it blocks nothing on this side; the account's own card
/// already says <c>in-transit</c>.
/// </para>
/// <para>
/// <c>PreSwitchWindows</c> is the windows the tee held immediately before
/// the last switch this tool performed. A session that was mid-turn at switch
/// time bills its response to the outgoing account and then writes those
/// windows under the incoming account's name, so a snapshot still carrying
/// these reset times belongs to the account that just left, whatever it says.
/// Cleared by the first snapshot whose reset times differ.
/// ponytail: in memory, like the reconciliation report beside it. A restart
/// forgets the stash, and the worst case is one stale card until the next
/// statusline write.
/// </para>
/// </summary>
internal sealed record DashboardSnapshot(
    ReconciliationReport? LastReconciliation,
    string? HandOffBanner,
    PreSwitchWindows? PreSwitchWindows)
{
    /// <summary>Nothing published yet.</summary>
    public static DashboardSnapshot Empty { get; } = new(null, null, null);

    /// <summary>
    /// Drops the pre-switch marker when it is the instance <paramref name="observed"/>
    /// names. A later switch can publish the same two reset times in a new instance;
    /// record equality would treat that as the marker this poll observed and clear it.
    /// </summary>
    public DashboardSnapshot WithoutObservedPreSwitchWindows(PreSwitchWindows observed) =>
        ReferenceEquals(PreSwitchWindows, observed)
            ? this with { PreSwitchWindows = null }
            : this;
}

/// <summary>
/// The slot those values are published through. Writers swap in one
/// <see cref="DashboardSnapshot"/>; readers copy that reference out.
/// </summary>
internal sealed class DashboardState
{
    // One reference. The snapshot's fields are written on the publishing thread
    // before this swap, and Volatile.Read sees those writes, so a reader cannot
    // take a report from one publication and windows or a hand-off line from
    // another. CompareExchange is the conditional form of Interlocked.Exchange:
    // the next snapshot is built from the one it replaces, and the swap lands
    // only while that one is still current.
    private DashboardSnapshot _published = DashboardSnapshot.Empty;

    /// <summary>The latest publication.</summary>
    public DashboardSnapshot Read() => Volatile.Read(ref _published);

    /// <summary>
    /// Publishes the snapshot <paramref name="update"/> builds from the
    /// publication it replaces. The function may run again when another write
    /// lands first, so it only builds the next snapshot.
    /// </summary>
    public void Publish(Func<DashboardSnapshot, DashboardSnapshot> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        DashboardSnapshot observed = Volatile.Read(ref _published);
        while (true)
        {
            DashboardSnapshot next = update(observed);
            // Reference, not record equality: two publications of the same
            // numbers are still different snapshots, and only the reference
            // CompareExchange compared is the one this swap replaced.
            DashboardSnapshot prior = Interlocked.CompareExchange(ref _published, next, observed);
            if (ReferenceEquals(prior, observed))
            {
                return;
            }

            observed = prior;
        }
    }
}

/// <summary>The outgoing account's last known reset times, kept only to disown them.</summary>
internal sealed record PreSwitchWindows(DateTimeOffset? FiveHourResetsAt, DateTimeOffset? SevenDayResetsAt);
