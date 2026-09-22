using System.Globalization;
using System.Text;
using ClaudeCodeAccountRotation.App.Adapters.FileSystem;
using ClaudeCodeAccountRotation.App.Quota;
using ClaudeCodeAccountRotation.App.Switching;
using ClaudeCodeAccountRotation.Core;
using ClaudeCodeAccountRotation.Core.Accounts;
using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Ports;
using ClaudeCodeAccountRotation.Core.Quota;
using ClaudeCodeAccountRotation.Core.Routing;
using ClaudeCodeAccountRotation.Core.Switching;
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
    SharedStoreSlots slots,
    WslSwitch coordinator,
    SwitchOptions options,
    TimeProvider timeProvider,
    IClaudeCliAuthStatus authStatus,
    ILogger<DashboardAssembler> logger)
{
    /// <summary>
    /// The live e-mail whose adopt-live seat was already judged, and whether
    /// that judgment was a refusal. One reference, swapped whole, so a poll
    /// cannot pair one e-mail with another poll's verdict.
    /// </summary>
    private LiveSeatJudgment? _liveSeatJudgment;

    private const string AdoptSentence = "Click Adopt to put the live account on the roster.";

    private const string LoginSentence = "Click Login on the card that needs a login.";

    private const string AddSentence = "Use Add an account, then Login.";

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
        // One read of the other sides per poll, shared by every card: the chip
        // for a held account needs to know whether that side is answering, and
        // its figures can only come from that side's own tee.
        IReadOnlyList<WslSideState> sides = await coordinator.ReadSidesAsync(cancellationToken);

        // The one reading of what this side holds, shared by every slot verdict
        // below: the account the state file names settles the design's "or its
        // rotation" once the CLI has rotated the live token past the record.
        WindowsHold hold = new(liveEmail, livePair?.Fingerprint);

        // Filled as the slots are read, because a superseded family is a fact
        // about one slot and the reading that notices it is the one below.
        List<string> warnings = [];
        List<(AccountCardView Card, AccountStanding Standing)> built = [];
        // One publication for the whole payload: the report, the hand-off line,
        // and the pre-switch windows below are this snapshot's, not a second read.
        DashboardSnapshot published = state.Read();
        if (liveEmail is AccountEmail live)
        {
            ParkedProfile? ownFolder = parked.FirstOrDefault(profile => profile.Email == live);
            string liveFolder = ownFolder?.FolderPath ?? profiles.FolderPathFor(live);
            (UsageSnapshot? observed, string? note) = Attribute(snapshot, live, published.PreSwitchWindows);
            built.Add(Card(
                live,
                isLive: true,
                livePair is not null,
                ownFolder?.FolderPath,
                observed,
                note,
                roster,
                capturedAt,
                livePair?.LoginExpiresAt,
                liveAccount?.ProfileFetchedAt,
                await SlotAsync(live, liveFolder, ownFolder?.HasCredentials ?? false, hold, warnings, cancellationToken),
                sides));
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
                profile.Account?.ProfileFetchedAt,
                await SlotAsync(profile.Email, profile.FolderPath, profile.HasCredentials, hold, warnings, cancellationToken),
                sides));
        }

        // A roster entry the operator added but has not logged in yet owns no
        // identity file, so the folder listing above cannot see it. Its card is
        // what makes Add visible on the page at all.
        foreach (RosterEntry entry in roster.Entries
            .Where(entry => !built.Exists(pair => string.Equals(pair.Card.Email, entry.Email.Value, StringComparison.Ordinal))))
        {
            string folder = profiles.FolderPathFor(entry.Email);
            built.Add(Card(
                entry.Email, isLive: false, hasCredentials: false, folder, observed: null, note: null, roster, capturedAt,
                loginExpiresAt: null,
                loggedInAt: null,
                slot: await SlotAsync(entry.Email, folder, slotHoldsPair: false, hold, warnings, cancellationToken),
                sides));
        }

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

        if (liveEmail is null && livePair is not null)
        {
            warnings.Add("The live directory holds a credential pair but the state file names no account.");
        }

        if (published.LastReconciliation is { SwitchingBlocked: true } blocked)
        {
            warnings.Add(blocked.JournalOutcome);
        }

        if (published.HandOffBanner is string inTransit)
        {
            warnings.Add(inTransit);
        }

        warnings.AddRange(QuarantinedFamilies());

        // Every poll, for as long as they stand: a recovery file that could not be
        // applied is a credential the operator has to act on, and a warning drained
        // by the first page that saw it would be a warning nobody sees.
        warnings.AddRange(quota.RecoveryWarnings);

        return new DashboardView(
            new LiveAccountView(liveEmail?.Value, livePair is not null, livePair?.Fingerprint.Sha256Hex[..12]),
            cards,
            published.LastReconciliation?.Banner,
            await SetupSentenceAsync(cards, liveAccount, cancellationToken),
            warnings,
            capturedAt,
            Pass(capturedAt));
    }

    /// <summary>
    /// The first-run sentence, or null once the roster holds an account the
    /// operator can use: one that is not paused and is either live or already
    /// has credentials. Adopt, then Login, then Add, in that order, because
    /// the machine is usually already logged in and Adopt is the step that
    /// ends that. A live seat adopt-live would refuse as not Max is not
    /// offered Adopt; the sentence falls through to Login or to Add.
    /// </summary>
    private async Task<string?> SetupSentenceAsync(
        List<AccountCardView> cards,
        OAuthAccountBlock? liveAccount,
        CancellationToken cancellationToken)
    {
        if (cards.Exists(static card => card.Roster is { Paused: false } && (card.IsLive || card.HasCredentials)))
        {
            return null;
        }

        if (liveAccount is OAuthAccountBlock liveBlock
            && liveBlock.Email is AccountEmail live
            && cards.Exists(static card => card.IsLive && card.Roster is null)
            && !await LiveSeatRefusedAsync(liveBlock, live, cancellationToken))
        {
            return AdoptSentence;
        }

        if (cards.Exists(static card => card.Roster is not null && !card.IsLive && !card.HeldAway && !card.HasCredentials))
        {
            return LoginSentence;
        }

        return AddSentence;
    }

    /// <summary>
    /// Whether adopt-live would answer 409 for this live seat. The account
    /// block can admit a Max tier and can never refuse one, which is the
    /// admission rule the adopt route already uses, so the CLI is asked only
    /// when the block does not already admit. A judgment is remembered for
    /// that e-mail: a Team or Enterprise seat stays refused, and the
    /// ten-second poll must not spawn the CLI to relearn it.
    /// </summary>
    private async Task<bool> LiveSeatRefusedAsync(
        OAuthAccountBlock liveAccount,
        AccountEmail live,
        CancellationToken cancellationToken)
    {
        if (MaxTierAdmission.Evaluate(null, liveAccount).Verdict == MaxTierVerdict.Admitted)
        {
            return false;
        }

        LiveSeatJudgment? judged = _liveSeatJudgment;
        if (judged is not null && string.Equals(judged.Email, live.Value, StringComparison.Ordinal))
        {
            return judged.Refused;
        }

        Result<ClaudeAuthStatus, string> status = await authStatus.ReadAsync(options.LiveConfigDirectory, cancellationToken);
        bool refused = MaxTierAdmission.Evaluate(status.IsSuccess ? status.Value : null, liveAccount).Verdict == MaxTierVerdict.Refused;
        _liveSeatJudgment = new LiveSeatJudgment(live.Value, refused);
        return refused;
    }

    /// <summary>One live e-mail and the adopt-live refusal already read for it.</summary>
    private sealed record LiveSeatJudgment(string Email, bool Refused);

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
        DateTimeOffset? loggedInAt,
        SlotSnapshot? slot,
        IReadOnlyList<WslSideState> sides)
    {
        WslSideState? holder = Holder(slot, sides);
        bool heldAway = slot?.State is SlotState.HeldElsewhere or SlotState.InTransit;
        List<UsageSnapshot> sources = [];
        if (slot?.State == SlotState.HeldElsewhere)
        {
            // Design 12: the leader reads no usage for a pair it does not hold,
            // so this card carries that side's tee and nothing else. What this
            // side read while it still held the pair is from before the hand-off
            // and would be a figure the account has since moved past.
            if (holder?.Usage is StatuslineSnapshot tee && tee.Account == email)
            {
                sources.Add(UsageSnapshot.FromStatusline(tee, email));
            }

            string side = Named(slot, holder);
            note = "in use by " + side + "; figures come from " + side + " sessions";
            // One family per account, so the expiry of a pair this side does not
            // hold can only come from the side that does, and only while that
            // side says this is the account it holds. Nothing to say beats a
            // number from before the hand-off: the 28-day window is fixed, and a
            // stale one would have the operator schedule the wrong re-login.
            loginExpiresAt = holder is { Online: true } && holder.LiveAccount == email
                ? holder.LoginExpiresAt
                : null;
        }
        else
        {
            if (observed is not null)
            {
                sources.Add(observed);
            }

            if (quota.LatestFor(email) is UsageSnapshot read)
            {
                sources.Add(read);
            }
        }

        MergedUsage? merged = UsageMerge.Merge(sources);
        RosterEntry? entry = roster.Find(email);
        RefreshStateView refresh = Refresh(email, folder);
        // Whether this pair can be made live at all, on either side: the planner
        // both sides go through refuses a stranded folder and an expired login
        // whichever side asked, so the two controls below share one verdict
        // rather than the page offering a side an account its own switch would
        // then refuse.
        bool usable = hasCredentials
            && refresh.State != Kebab(RefreshOutcomeKind.Stranded)
            && !(loginExpiresAt <= capturedAt);
        return (
            new AccountCardView(
                email.Value,
                isLive,
                hasCredentials,
                folder,
                Usage(merged, capturedAt),
                note,
                refresh,
                View(entry),
                LoginExpiresAt: loginExpiresAt,
                LoggedInAt: loggedInAt,
                Slot: Word(slot),
                Chip: Chip(slot, holder),
                CanSwitchHere: usable && !isLive && !heldAway,
                HeldAway: heldAway,
                OfferedTo: [.. sides
                    .Where(side => usable && side.Online && slot?.State == SlotState.Parked && side.LiveAccount != email)
                    .Select(static side => side.Side.Value)]),
            new AccountStanding(email, isLive, entry?.Paused ?? false, hasCredentials, merged?.Merged, loginExpiresAt));
    }

    /// <summary>
    /// The side a slot's pair has gone to, as that side last answered, or null
    /// when the slot names none or none is configured. A record names the side
    /// for both the held and the in-transit states, because the claim writes it
    /// before the pair leaves.
    /// </summary>
    private static WslSideState? Holder(SlotSnapshot? slot, IReadOnlyList<WslSideState> sides) =>
        slot?.Record is HolderRecord record
            ? sides.FirstOrDefault(side => side.Side == record.Side)
            : sides.Count == 1 ? sides[0] : null;

    /// <summary>
    /// The side a slot's pair is with, named: the record's own word first,
    /// because it is the store's statement of who took the pair, then the one
    /// configured side, and only then the default. A card never says "the other
    /// side" when a file on disk names it.
    /// </summary>
    private static string Named(SlotSnapshot? slot, WslSideState? holder) =>
        slot?.Record?.Side.Value ?? holder?.Side.Value ?? SideName.Wsl.Value;

    /// <summary>
    /// One account's chip, in design 12's vocabulary, or null when there is
    /// nothing for it to add: an unshared store, or a slot nothing has ever
    /// been parked in, whose card already says it needs a login.
    /// </summary>
    private static string? Chip(SlotSnapshot? slot, WslSideState? holder)
    {
        string side = Named(slot, holder);
        return slot?.State switch
        {
            SlotState.HeldHere => "live here",
            SlotState.Parked => "parked",
            // Which way this account's own pair is going, read off the suffix of
            // its file in the mailbox: an export is coming back to the store, a
            // claim is going to that side. During a switch's export window one
            // pair is moving each way and each card says its own direction.
            SlotState.InTransit => (slot.Returning ? "in transit from " : "in transit to ") + side,
            SlotState.HeldElsewhere => "in use by " + side + (holder is null or { Online: false } ? " (offline)" : string.Empty),
            _ => null,
        };
    }

    /// <summary>One account's slot as the plain word the wire has carried since the store was shared.</summary>
    private static string? Word(SlotSnapshot? slot) => slot?.State switch
    {
        null => null,
        SlotState.Parked => "parked",
        SlotState.HeldHere => "held-here",
        SlotState.HeldElsewhere => "held-elsewhere",
        SlotState.InTransit => "in-transit",
        _ => "never-logged-in",
    };

    /// <summary>
    /// One account's slot, and the design's reconciliation on the way past: a
    /// record the files contradict is dropped here, on the read that noticed
    /// it, because the page is the only thing that looks at every slot on a
    /// schedule. The drop takes the mutation gate with a zero wait, so a poll
    /// that lands inside a switch leaves that switch's record alone and the
    /// next poll deals with it. Null when the store is not shared, and the card
    /// then carries neither a word nor a chip.
    /// </summary>
    private async Task<SlotSnapshot?> SlotAsync(
        AccountEmail email,
        string folder,
        bool slotHoldsPair,
        WindowsHold hold,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (await slots.ReadAsync(email, folder, slotHoldsPair, hold, cancellationToken) is not SlotSnapshot slot)
        {
            return null;
        }

        if (slot.StaleRecord)
        {
            await slots.DropStaleRecordAsync(email, folder, slot, hold, cancellationToken);
        }

        // A second token family, the one thing in this design allowed to make
        // one. Said on every poll for as long as the record stands, because the
        // rule a second family is held to is that it is always shown and never
        // silent; the only thing that ends the line is that family being
        // quarantined when the other side hands it back.
        if (await slots.ReadSupersededAsync(email, folder, slotHoldsPair, hold, cancellationToken) is SupersededFamily superseded)
        {
            if (superseded.Stale)
            {
                await slots.DropStaleSupersededAsync(email, folder, superseded, hold, cancellationToken);
            }
            else
            {
                warnings.Add(email.Value + " has a second token family on the " + superseded.Record.Side.Value
                    + " side, superseded here on " + superseded.Record.Since.ToString("u", CultureInfo.InvariantCulture)
                    + ". Switching that side away from the account quarantines that family; nothing else touches it.");
            }
        }

        return slot;
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

    /// <summary>
    /// One line per superseded family the store took in and may not use, read
    /// off the quarantine directory itself rather than from anything held in
    /// memory. That is what makes it survive a restart and outlive the switch
    /// that put the file there: nothing clears this line but the operator
    /// deciding what to do with the file it names.
    /// </summary>
    private IEnumerable<string> QuarantinedFamilies()
    {
        string root = options.SupersededQuarantineDirectory;
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(static path => "A superseded token family is quarantined at " + path
                + ". It is never used and never deleted here; delete it yourself once that account has been logged out on the side that held it.");
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
    private (UsageSnapshot? Observed, string? Note) Attribute(
        StatuslineSnapshot? snapshot,
        AccountEmail account,
        PreSwitchWindows? preSwitchWindows)
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

        if (preSwitchWindows is PreSwitchWindows before)
        {
            if (before.FiveHourResetsAt == snapshot.FiveHourResetsAt && before.SevenDayResetsAt == snapshot.SevenDayResetsAt)
            {
                return (null, "Pre-switch windows: this snapshot still carries the outgoing account's reset times.");
            }

            // The first snapshot whose windows differ is the incoming account's own.
            // Cleared only while the published marker is the instance this poll
            // observed. A switch that landed during this poll keeps its marker,
            // including one that carries the same two reset times.
            state.Publish(current => current.WithoutObservedPreSwitchWindows(before));
        }

        return (UsageSnapshot.FromStatusline(snapshot, account), null);
    }
}
