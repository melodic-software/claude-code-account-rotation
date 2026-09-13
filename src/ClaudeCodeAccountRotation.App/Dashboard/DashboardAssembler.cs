using System.Globalization;
using System.Text;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.App.Dashboard;

/// <summary>
/// Builds the page's model from three sources that only partly overlap: the
/// live directory, the profile folders on disk, and the roster the operator
/// edits. An account gets one card whichever of the three it appears in, so a
/// roster entry that has never been logged in still shows up (as "needs
/// login") and a folder that predates the roster still shows up (with Adopt
/// offered). Every read first repairs a state file a session wrote a stale
/// block back into, so the page never shows the outgoing account as live for
/// long. Ranking joins in a later phase.
/// </summary>
internal sealed class DashboardAssembler(
    ClaudeStateFile stateFile,
    ICredentialPairStore pairs,
    ProfileFolderStore profiles,
    RosterFile rosterFile,
    RateLimitGuardTeeFileReader tee,
    LiveDirectorySwitch executor,
    DashboardState state,
    QuotaState quota,
    RecoveryFiles recovery,
    TimeProvider timeProvider)
{
    /// <summary>The roster entry as the page reads it.</summary>
    public static RosterEntryView? View(RosterEntry? entry) => entry is null
        ? null
        : new RosterEntryView(
            entry.Email.Value,
            entry.Alias,
            entry.Browser?.ToString().ToLowerInvariant(),
            entry.BrowserProfileDirectory,
            entry.Paused,
            entry.Notes);

    public async Task<DashboardView> AssembleAsync(CancellationToken cancellationToken)
    {
        _ = await executor.RepairStaleIdentityAsync(cancellationToken);
        OAuthAccountBlock? liveAccount = await stateFile.ReadAccountBlockAsync(cancellationToken);
        CredentialPair? livePair = await pairs.ReadLiveAsync(cancellationToken);
        IReadOnlyList<ParkedProfile> parked = await profiles.ListAsync(cancellationToken);
        StatuslineSnapshot? snapshot = await tee.ReadAsync(cancellationToken);
        Roster roster = await rosterFile.ReadAsync(cancellationToken);
        AccountEmail? liveEmail = liveAccount?.Email;
        DateTimeOffset capturedAt = timeProvider.GetUtcNow();

        List<AccountCardView> cards = [];
        if (liveEmail is AccountEmail live)
        {
            ParkedProfile? ownFolder = parked.FirstOrDefault(profile => profile.Email == live);
            (UsageSnapshot? observed, string? note) = Attribute(snapshot, live);
            cards.Add(Card(live, isLive: true, livePair is not null, ownFolder?.FolderPath, observed, note, roster, capturedAt));
        }

        cards.AddRange(parked
            .Where(profile => profile.Email != liveEmail)
            .Select(profile => Card(profile.Email, isLive: false, profile.HasCredentials, profile.FolderPath, observed: null, note: null, roster, capturedAt)));

        // A roster entry the operator added but has not logged in yet owns no
        // identity file, so the folder listing above cannot see it. Its card is
        // what makes Add visible on the page at all.
        List<AccountCardView> rosterOnly = [.. roster.Entries
            .Where(entry => !cards.Exists(card => string.Equals(card.Email, entry.Email.Value, StringComparison.Ordinal)))
            .Select(entry => Card(entry.Email, isLive: false, hasCredentials: false, profiles.FolderPathFor(entry.Email), observed: null, note: null, roster, capturedAt))];
        cards.AddRange(rosterOnly);
        cards.Sort(static (left, right) => string.CompareOrdinal(left.Email, right.Email));

        List<string> warnings = [];
        if (liveEmail is null && livePair is not null)
        {
            warnings.Add("The live directory holds a credential pair but the state file names no account.");
        }

        if (state.LastReconciliation is { SwitchingBlocked: true } blocked)
        {
            warnings.Add(blocked.JournalOutcome);
        }

        // Every poll, for as long as they stand: a recovery file that could not be
        // applied is a credential the operator has to act on, and a warning drained
        // by the first page that saw it would be a warning nobody sees.
        warnings.AddRange(quota.RecoveryWarnings);

        return new DashboardView(
            new LiveAccountView(liveEmail?.Value, livePair is not null, livePair?.Fingerprint.Sha256Hex[..12]),
            cards,
            state.LastReconciliation?.Banner,
            warnings,
            capturedAt,
            Pass(capturedAt));
    }

    /// <summary>One card, whichever of the three sources it came from: the same usage merge and the same refresh state for all of them.</summary>
    private AccountCardView Card(
        AccountEmail email,
        bool isLive,
        bool hasCredentials,
        string? folder,
        UsageSnapshot? observed,
        string? note,
        Roster roster,
        DateTimeOffset capturedAt) =>
        new(email.Value,
            isLive,
            hasCredentials,
            folder,
            Usage(observed, quota.LatestFor(email), capturedAt),
            note,
            Refresh(email, folder),
            View(roster.Find(email)));

    /// <summary>
    /// Merges every source that has numbers for one account into the rows the
    /// card shows: the five-hour and seven-day rows first, then every scoped row,
    /// then anything the endpoint has added since this build shipped.
    /// <para>
    /// The merge is per bucket, not per snapshot. The tee carries two of the
    /// buckets and an on-demand read carries three or more, so whichever of them
    /// happened to be written last would otherwise blank the rows it does not
    /// know about. Each row therefore comes from whichever source that carries
    /// that bucket captured it last, and a row from an older source than the
    /// card's own names its source itself.
    /// </para>
    /// <para>
    /// A bucket no source carries is still emitted, unknown, so the card admits
    /// what it does not know instead of showing a shorter list; and a row whose
    /// window has reset since it was captured loses its percentage, because a
    /// figure from the window before this one measures nothing the operator can
    /// act on.
    /// </para>
    /// </summary>
    private static UsageView Usage(UsageSnapshot? observed, UsageSnapshot? read, DateTimeOffset capturedAt)
    {
        List<UsageSnapshot> sources = [];
        if (observed is not null)
        {
            sources.Add(observed);
        }

        if (read is not null)
        {
            sources.Add(read);
        }

        if (sources.Count == 0)
        {
            return UsageView.Unread;
        }

        List<(UsageSnapshot? Source, UsageLimit Limit)> rows = [];
        foreach (UsageSnapshot source in sources)
        {
            foreach (UsageLimit limit in source.Limits)
            {
                int existing = rows.FindIndex(row => SameBucket(row.Limit, limit));
                if (existing < 0)
                {
                    rows.Add((source, limit));
                }
                else if (source.CapturedAt > rows[existing].Source!.CapturedAt)
                {
                    rows[existing] = (source, limit);
                }
            }
        }

        foreach (UsageLimit bucket in UsageBuckets.Always)
        {
            if (!rows.Exists(row => row.Limit.Kind == bucket.Kind))
            {
                rows.Add((null, bucket));
            }
        }

        // The card's own line names the newest source that actually contributed a
        // row, falling back to the newest source there is when none did: a tee
        // observation that carried no percentages at all is still an "as of".
        UsageSnapshot card = rows.Where(row => row.Source is not null).Select(row => row.Source!).MaxBy(source => source.CapturedAt)
            ?? sources.MaxBy(source => source.CapturedAt)!;

        return new UsageView(
            Wire(card.Source),
            card.CapturedAt,
            [.. rows.OrderBy(row => Position(row.Limit.Kind)).Select(row => Row(row.Source, row.Limit, card, capturedAt))],
            // The newest source that has a credits block rather than the newest
            // source outright, for the reason the rows are merged per bucket: the
            // tee never carries one, and a tee write must not blank the line.
            Credits(sources.Where(source => source.ExtraUsage is not null).MaxBy(source => source.CapturedAt)?.ExtraUsage));
    }

    private static UsageLimitView Row(UsageSnapshot? source, UsageLimit limit, UsageSnapshot card, DateTimeOffset capturedAt)
    {
        if (source is null)
        {
            return UsageLimitView.Unknown(limit);
        }

        bool reset = limit.ResetsAt is DateTimeOffset resets && resets < capturedAt;
        bool own = ReferenceEquals(source, card);
        return new UsageLimitView(
            limit.RawKind,
            limit.Label,
            reset ? null : limit.Percent,
            limit.ResetsAt,
            limit.Severity,
            own ? null : Wire(source.Source),
            own ? null : source.CapturedAt,
            Known: true,
            WindowReset: reset);
    }

    private static UsageCreditsView? Credits(ExtraUsageState? credits) => credits is null
        ? null
        : new UsageCreditsView(credits.IsEnabled, credits.DisabledReason, credits.SpendLimitReached);

    /// <summary>
    /// Whether two limits measure the same window. The two named buckets are
    /// identified by kind alone; a scoped or unrecognised one needs its raw kind
    /// and its display name too, because an account can hold several of either
    /// and merging them would let one model's figure overwrite another's.
    /// </summary>
    private static bool SameBucket(UsageLimit left, UsageLimit right) =>
        left.Kind == right.Kind
        && (left.Kind is LimitKind.Session or LimitKind.WeeklyAll
            || (string.Equals(left.RawKind, right.RawKind, StringComparison.Ordinal)
                && string.Equals(left.ScopeDisplayName, right.ScopeDisplayName, StringComparison.Ordinal)));

    /// <summary>The order the page lists the buckets in; ties keep the order the sources gave them.</summary>
    private static int Position(LimitKind kind) => kind switch
    {
        LimitKind.Session => 0,
        LimitKind.WeeklyAll => 1,
        LimitKind.WeeklyScoped => 2,
        _ => 3,
    };

    /// <summary>A source as the page spells it. No JSON enum converter is configured, so every enum crosses the wire as a word.</summary>
    private static string Wire(QuotaSource source) => source switch
    {
        QuotaSource.StatuslineSnapshot => "snapshot",
        QuotaSource.OnDemandRefresh => "refresh",
        _ => "cached",
    };

    /// <summary>
    /// One card's refresh state. A folder holding a recovery file is stranded
    /// whatever the last outcome said: the strand outlives the pass that caused
    /// it and is what the page disables Switch on, so a later budget refusal must
    /// not paper over it.
    /// </summary>
    private RefreshStateView Refresh(AccountEmail email, string? folder)
    {
        if (folder is string stranded && recovery.HasRecoveryFor(stranded))
        {
            return new RefreshStateView(Kebab(RefreshOutcomeKind.Stranded), RefreshMessages.Stranded, RetryAt: null);
        }

        return quota.OutcomeFor(email) is RefreshOutcome outcome
            ? new RefreshStateView(Kebab(outcome.Kind), outcome.Message, outcome.RetryAt)
            : RefreshStateView.Idle;
    }

    /// <summary>The pass as a whole: running or not, the lockout that is still standing, and what the last one came to.</summary>
    private RefreshView Pass(DateTimeOffset capturedAt)
    {
        DateTimeOffset? lockedUntil = quota.LockedUntil(capturedAt);
        List<string> counts = [.. (quota.LastPassSummary ?? new Dictionary<RefreshOutcomeKind, int>())
            .Where(static count => count.Value > 0)
            .OrderBy(static count => count.Key)
            .Select(static count => count.Value.ToString(CultureInfo.InvariantCulture) + " " + Kebab(count.Key).Replace('-', ' '))];
        return new RefreshView(
            quota.InProgress,
            lockedUntil,
            counts.Count == 0 ? null : "last pass: " + string.Join(", ", counts));
    }

    /// <summary>
    /// An outcome kind as the wire spells it: <c>SessionWillRefresh</c> becomes
    /// <c>session-will-refresh</c>. Derived rather than listed in a switch, so a
    /// kind added later cannot reach the page under a name nobody updated.
    /// </summary>
    private static string Kebab(RefreshOutcomeKind kind)
    {
        string name = kind.ToString();
        StringBuilder kebab = new(name.Length + 4);
        foreach (char letter in name)
        {
            if (char.IsUpper(letter) && kebab.Length > 0)
            {
                _ = kebab.Append('-');
            }

            _ = kebab.Append(char.ToLowerInvariant(letter));
        }

        return kebab.ToString();
    }

    /// <summary>
    /// Decides whether the tee's observation belongs to <paramref name="account"/>.
    /// The tee file is last-writer-wins across every session on the machine, so a
    /// snapshot is only ever shown on the card it names, and there are four ways
    /// it names nothing usable:
    /// <list type="number">
    /// <item>no snapshot at all (the file is absent, unreadable, or carries no
    /// <c>rate_limits</c>): the card simply has no numbers;</item>
    /// <item>the snapshot carries no <c>account.email</c>, because the writer
    /// predates that field or could not attribute the observation;</item>
    /// <item>the snapshot names another account;</item>
    /// <item>the snapshot names this account but still carries the windows the
    /// outgoing account left behind, which is what a session mid-turn at switch
    /// time writes.</item>
    /// </list>
    /// </summary>
    private (UsageSnapshot? Observed, string? Note) Attribute(StatuslineSnapshot? snapshot, AccountEmail account)
    {
        if (snapshot is null)
        {
            return (null, null);
        }

        if (snapshot.Account is not AccountEmail observed)
        {
            return (null, "The statusline snapshot names no account, so it is not shown here.");
        }

        if (observed != account)
        {
            return (null, "The statusline snapshot names " + observed.Value + ", not this account.");
        }

        if (state.PreSwitchWindows is PreSwitchWindows before)
        {
            if (before.FiveHourResetsAt == snapshot.FiveHourResetsAt && before.SevenDayResetsAt == snapshot.SevenDayResetsAt)
            {
                return (null, "Pre-switch windows: this snapshot still carries the outgoing account's reset times.");
            }

            // The first snapshot whose windows differ is the incoming account's own.
            state.PreSwitchWindows = null;
        }

        return (UsageSnapshot.FromStatusline(snapshot, account), null);
    }
}
