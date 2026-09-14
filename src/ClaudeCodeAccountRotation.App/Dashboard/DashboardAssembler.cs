using System.Globalization;
using System.Text;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Routing;
using Microsoft.Extensions.Logging;

namespace ClaudeCodeAccountRotation.App.Dashboard;

/// <summary>
/// Builds the page's model from three sources that only partly overlap: the
/// live directory, the profile folders on disk, and the roster the operator
/// edits. An account gets one card whichever of the three it appears in, so a
/// roster entry that has never been logged in still shows up (as "needs
/// login") and a folder that predates the roster still shows up (with Adopt
/// offered). Every read first repairs a state file a session wrote a stale
/// block back into, so the page never shows the outgoing account as live for
/// long. The cards come out in the order the accounts free up, which is the Core
/// availability order and not one computed here; the ranked queue of switch
/// candidates, a filter and a truncation over that same order, joins later.
/// </summary>
internal sealed partial class DashboardAssembler(
    ClaudeStateFile stateFile,
    ICredentialPairStore pairs,
    ProfileFolderStore profiles,
    RosterFile rosterFile,
    RateLimitGuardTeeFileReader tee,
    LiveDirectorySwitch executor,
    DashboardState state,
    QuotaState quota,
    RecoveryFiles recovery,
    TimeProvider timeProvider,
    ILogger<DashboardAssembler> logger)
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

        List<(AccountCardView Card, AccountStanding Standing)> built = [];
        if (liveEmail is AccountEmail live)
        {
            ParkedProfile? ownFolder = parked.FirstOrDefault(profile => profile.Email == live);
            (UsageSnapshot? observed, string? note) = Attribute(snapshot, live);
            built.Add(Card(live, isLive: true, livePair is not null, ownFolder?.FolderPath, observed, note, roster, capturedAt, livePair?.LoginExpiresAt, liveAccount?.ProfileFetchedAt));
        }

        // A read per parked folder rather than a projection, because the expiry
        // sits inside the credential file and only the file can answer for it.
        foreach (ParkedProfile profile in parked.Where(candidate => candidate.Email != liveEmail))
        {
            built.Add(Card(
                profile.Email,
                isLive: false,
                profile.HasCredentials,
                profile.FolderPath,
                observed: null,
                note: null,
                roster,
                capturedAt,
                await ReadLoginExpiryAsync(profile, cancellationToken),
                profile.Account?.ProfileFetchedAt));
        }

        // A roster entry the operator added but has not logged in yet owns no
        // identity file, so the folder listing above cannot see it. Its card is
        // what makes Add visible on the page at all.
        List<(AccountCardView Card, AccountStanding Standing)> rosterOnly = [.. roster.Entries
            .Where(entry => !built.Exists(pair => string.Equals(pair.Card.Email, entry.Email.Value, StringComparison.Ordinal)))
            .Select(entry => Card(entry.Email, isLive: false, hasCredentials: false, profiles.FolderPathFor(entry.Email), observed: null, note: null, roster, capturedAt, loginExpiresAt: null, loggedInAt: null))];
        built.AddRange(rosterOnly);

        // One arrangement for the whole payload, and the cards go out in the
        // order it hands back: the group and the instant on each card are read
        // off the key that placed it, never derived a second time here, so what
        // the operator reads on a card cannot disagree with where the card is.
        // The match back to a card is by the standing instance the arrangement
        // was handed, not by e-mail: two entries can share an address (a
        // hand-copied profile folder is not de-duplicated), and a dictionary
        // keyed on the address would throw on the second one where the page
        // used to just show two cards.
        var byStanding = built.ToDictionary(
            static pair => pair.Standing,
            static pair => pair.Card,
            (IEqualityComparer<AccountStanding>)ReferenceEqualityComparer.Instance);
        List<AccountCardView> cards = [.. AccountAvailability
            .Arrange([.. built.Select(static pair => pair.Standing)], capturedAt)
            .Select(arranged => byStanding[arranged.Standing] with
            {
                Standing = Wire(arranged.Key.Standing),
                NextResetAt = arranged.Key.NextResetAt,
            })];

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

    /// <summary>
    /// One card, whichever of the three sources it came from, beside the standing
    /// the ordering keys on. Both come out of one merge, called once here: a
    /// second call would let the rows the operator reads and the figures the
    /// order was computed from drift apart on the live card, which is exactly the
    /// disagreement the merged snapshot exists to make impossible.
    /// </summary>
    private (AccountCardView Card, AccountStanding Standing) Card(
        AccountEmail email,
        bool isLive,
        bool hasCredentials,
        string? folder,
        UsageSnapshot? observed,
        string? note,
        Roster roster,
        DateTimeOffset capturedAt,
        DateTimeOffset? loginExpiresAt,
        DateTimeOffset? loggedInAt)
    {
        List<UsageSnapshot> sources = [];
        if (observed is not null)
        {
            sources.Add(observed);
        }

        if (quota.LatestFor(email) is UsageSnapshot read)
        {
            sources.Add(read);
        }

        MergedUsage? merged = UsageMerge.Merge(sources);
        RosterEntry? entry = roster.Find(email);
        return (
            new AccountCardView(
                email.Value,
                isLive,
                hasCredentials,
                folder,
                Usage(merged, capturedAt),
                note,
                Refresh(email, folder),
                View(entry),
                LoginExpiresAt: loginExpiresAt,
                LoggedInAt: loggedInAt),
            new AccountStanding(email, isLive, entry?.Paused ?? false, hasCredentials, merged?.Merged, loginExpiresAt));
    }

    /// <summary>
    /// The login expiry inside one parked folder's credential file, or null when
    /// the folder holds no file or the file cannot be read.
    /// <para>
    /// Any failure to read or parse the file, whatever its shape, costs the card
    /// one blank line and the log one warning, never the page: a missing key, a
    /// value of the wrong type, an epoch outside the range an instant can hold, a
    /// torn write, a permission error, or whatever the next writer into a profile
    /// folder invents. The kinds are not listed in the filter, because the list is
    /// what would go stale; only cancellation is let through.
    /// </para>
    /// <para>
    /// The folder's own leaf reaches the log beside the store's reason. The reason
    /// can name the file path, since it is the store's own exception message, and a
    /// framework exception can quote the argument it rejected, so it can name a
    /// number the file holds, but never a secret the file holds.
    /// </para>
    /// </summary>
    private async Task<DateTimeOffset?> ReadLoginExpiryAsync(ParkedProfile profile, CancellationToken cancellationToken)
    {
        if (!profile.HasCredentials)
        {
            return null;
        }

        try
        {
            return (await pairs.ReadParkedAsync(profile.FolderPath, cancellationToken))?.LoginExpiresAt;
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogLoginExpiryUnreadable(FolderName(profile.FolderPath), exception.Message);
            return null;
        }
    }

    private static string FolderName(string folderPath) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "login expiry unreadable for {Folder}: {Reason}")]
    private partial void LogLoginExpiryUnreadable(string folder, string reason);

    /// <summary>
    /// The merged numbers as the card shows them: the five-hour and seven-day
    /// rows first, then every scoped row, then anything the endpoint has added
    /// since this build shipped.
    /// <para>
    /// The merge itself belongs to the Core rule that the ordering also keys on,
    /// so nothing about which figure wins is decided here. What is decided here
    /// is what the page owes a reader: a bucket no source carries is still
    /// emitted, unknown, so the card admits what it does not know instead of
    /// showing a shorter list, and the rows come out in the order the page lists
    /// them rather than the order the sources happened to arrive in.
    /// </para>
    /// <para>
    /// A row whose window has reset since it was captured loses its percentage,
    /// because a figure from the window before this one measures nothing the
    /// operator can act on.
    /// </para>
    /// </summary>
    private static UsageView Usage(MergedUsage? merged, DateTimeOffset capturedAt)
    {
        if (merged is null)
        {
            return UsageView.Unread;
        }

        List<(UsageSnapshot? Source, UsageLimit Limit)> rows = [.. merged.Rows.Select(static row => ((UsageSnapshot?)row.Source, row.Limit))];
        foreach (UsageLimit bucket in UsageBuckets.Always)
        {
            if (!rows.Exists(row => row.Limit.Kind == bucket.Kind))
            {
                rows.Add((null, bucket));
            }
        }

        return new UsageView(
            Wire(merged.Card.Source),
            merged.Card.CapturedAt,
            [.. rows.OrderBy(row => Position(row.Limit.Kind)).Select(row => Row(row.Source, row.Limit, merged.Card, capturedAt))],
            Credits(merged.Merged.ExtraUsage));
    }

    private static UsageLimitView Row(UsageSnapshot? source, UsageLimit limit, UsageSnapshot card, DateTimeOffset capturedAt)
    {
        if (source is null)
        {
            return UsageLimitView.Unknown(limit);
        }

        bool reset = limit.HasResetBy(capturedAt);
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
    /// Where an account stands, as the page spells it. The word a card cannot
    /// derive for itself: a page holding only an instant cannot tell an account
    /// that is free now from one nobody has read.
    /// <para>
    /// Every member is named and the default throws, because a member added later
    /// would otherwise reach the page as "no usage read yet": the one word that
    /// tells the operator nothing is known about an account, said about an account
    /// this code simply has no word for yet.
    /// </para>
    /// </summary>
    private static string Wire(AvailabilityStanding standing) => standing switch
    {
        AvailabilityStanding.Usable => "usable",
        AvailabilityStanding.Exhausted => "exhausted",
        AvailabilityStanding.Unread => "unread",
        AvailabilityStanding.Paused => "paused",
        _ => throw new ArgumentOutOfRangeException(nameof(standing)),
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
