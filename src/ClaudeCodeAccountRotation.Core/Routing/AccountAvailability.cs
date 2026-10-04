using ClaudeCodeAccountRotation.Core.Identity;
using ClaudeCodeAccountRotation.Core.Quota;

namespace ClaudeCodeAccountRotation.Core.Routing;

/// <summary>
/// Where an account stands, and in declaration order, because that order is the
/// order the accounts are listed in: the ones that can be worked now, then the
/// ones whose only spent window is scoped to one model, then the ones back within
/// hours, then the ones back within days, then the ones nothing is known about,
/// then the ones the operator took out of the rotation. Adding a
/// member in the middle moves every account below it, so a new member belongs
/// where it should be read. One exception: an unread account that is in use is
/// listed right after the usable ones (<see cref="AvailabilityKey.InUse"/>).
/// <para>
/// <see cref="Limited"/> is a spent five-hour window with weekly quota left: a
/// pause of hours, not an exhausted account. <see cref="Exhausted"/> is a spent
/// weekly window and nothing else. <see cref="ModelLimited"/> is a spent weekly
/// window scoped to one model (the endpoint's "Fable" row) with the 5-hour and
/// 7-day windows still open. Other models can still be worked, so it is not
/// limited; but the tool cannot know which model a session will run, and the
/// operator works mostly on that one, so it is not usable either and nothing
/// recommends it. It stays switchable by hand.
/// </para>
/// </summary>
public enum AvailabilityStanding
{
    Usable,
    ModelLimited,
    Limited,
    Exhausted,
    Unread,
    Paused,
}

/// <summary>
/// What one account sorts by: the group it is in, the instant it frees up (null
/// when there is no wait to state, or none that can be dated), and its address
/// as the final tie-break. <paramref name="InUse"/> is set only on an unread
/// account that is live here or held by another side, which the comparer lifts
/// above the limited and exhausted groups. <paramref name="LimitedModel"/> is
/// set only on a <see cref="AvailabilityStanding.ModelLimited"/> key: the label
/// of the spent scoped window, so the card names it without re-deriving it.
/// <para>
/// The key carries no comparison of its own. A record cannot generate the
/// comparison operators the analyzer demands alongside one, and the order here
/// is a policy about accounts rather than a property of the value, so it lives
/// in <see cref="AccountAvailability.Comparer"/> where a caller can see which
/// rule it is sorting by.
/// </para>
/// </summary>
public sealed record AvailabilityKey(
    AvailabilityStanding Standing,
    DateTimeOffset? NextResetAt,
    AccountEmail Email,
    bool InUse = false,
    string? LimitedModel = null);

/// <summary>
/// One account beside the key it sorted by, so a caller reads the group and the
/// instant off the same value that placed it and never re-derives either.
/// </summary>
public sealed record ArrangedAccount(AvailabilityKey Key, AccountStanding Standing);

/// <summary>
/// The order accounts free up in: a total order over every account there is.
/// <para>
/// The operator pays for each subscription by the week whether or not it is
/// spent, so the account to work next is the one whose window turns over
/// soonest; anything left in that window when it resets is lost, and anything
/// left in the others is not. That is why the usable accounts are ordered by
/// their weekly reset rather than by how much headroom they have.
/// </para>
/// <para>
/// It is a total order, and over every account, so that a ranked queue is a
/// filter and a truncation over this one list rather than a second ordering that
/// can disagree with the page: keep only the `Usable`
/// accounts (a paused, model-limited, limited, exhausted or unread one is not
/// offered next),
/// take as many of them as are wanted. The
/// eligibility figures are parameters for the same reason, so a policy can move
/// them without changing a signature here.
/// </para>
/// </summary>
public static class AccountAvailability
{
    /// <summary>
    /// Where one account stands, and the instant it frees up, applied in this
    /// order: paused first of all, because an account taken out of the rotation
    /// is not a candidate whatever its numbers say; then unread, when neither of
    /// the two windows that decide anything has been read, because a missing
    /// figure is not a zero and an account nobody can say anything about must not
    /// lead the list; then the window-reset rule, which drops a window that has
    /// already turned over, figure and instant together; then exhausted, on the
    /// weekly window being spent, keyed by the <b>later</b> of the resets that
    /// block it, because an account over both limits is not free when the first
    /// of them turns over, and by no instant at all when one of those windows
    /// carries no reset, because the later of them is then unknown; then
    /// limited, on the five-hour window alone being spent, keyed by its reset;
    /// then model-limited, on a weekly window scoped to one model being spent at
    /// the 7-day threshold, keyed by when it turns over under the same later-of
    /// rule; else usable, keyed by the weekly reset. A scoped figure alone still
    /// leaves an account unread: it says nothing of the two windows every model
    /// draws on.
    /// </summary>
    public static AvailabilityKey KeyFor(
        AccountStanding standing,
        DateTimeOffset now,
        double eligibleFiveHourMaxPercent = 100,
        double eligibleSevenDayMaxPercent = 100)
    {
        ArgumentNullException.ThrowIfNull(standing);
        if (standing.IsPaused)
        {
            return new AvailabilityKey(AvailabilityStanding.Paused, NextResetAt: null, standing.Email);
        }

        UsageLimit? session = Bucket(standing, LimitKind.Session);
        UsageLimit? weekly = Bucket(standing, LimitKind.WeeklyAll);
        if (session is null && weekly is null)
        {
            return new AvailabilityKey(AvailabilityStanding.Unread, NextResetAt: null, standing.Email, standing.IsLive || standing.HeldElsewhere);
        }

        // A window that has turned over since it was read is a window at zero
        // with no wait left, so its figure and its instant are dropped together.
        // Keeping the instant would sort a card that is free now ahead of every
        // card that really does free up next, and date the wait in the past.
        session = session?.HasResetBy(now) == true ? null : session;
        weekly = weekly?.HasResetBy(now) == true ? null : weekly;

        bool sessionSpent = (session?.Percent ?? 0) >= eligibleFiveHourMaxPercent;
        bool weeklySpent = (weekly?.Percent ?? 0) >= eligibleSevenDayMaxPercent;
        if (!sessionSpent && !weeklySpent)
        {
            List<UsageLimit> scopedSpent = [.. standing.Latest!.Limits.Where(limit =>
                limit.Kind == LimitKind.WeeklyScoped && !limit.HasResetBy(now) && limit.Percent >= eligibleSevenDayMaxPercent)];
            return scopedSpent.Count == 0
                ? new AvailabilityKey(AvailabilityStanding.Usable, weekly?.ResetsAt, standing.Email)
                : new AvailabilityKey(
                    AvailabilityStanding.ModelLimited,
                    LatestOrUnknown(scopedSpent.Select(static limit => limit.ResetsAt)),
                    standing.Email,
                    LimitedModel: string.Join(", ", scopedSpent.Select(static limit => limit.Label).Distinct(StringComparer.Ordinal)));
        }

        if (!weeklySpent)
        {
            return new AvailabilityKey(AvailabilityStanding.Limited, session?.ResetsAt, standing.Email);
        }

        // The account comes back when the last of the windows that spent it
        // turns over, so one of them carrying no reset leaves the whole wait
        // undatable: the other window's instant would promise a return the
        // undated one may still be blocking.
        List<DateTimeOffset?> frees = [];
        if (sessionSpent)
        {
            frees.Add(session?.ResetsAt);
        }

        if (weeklySpent)
        {
            frees.Add(weekly?.ResetsAt);
        }

        return new AvailabilityKey(AvailabilityStanding.Exhausted, LatestOrUnknown(frees), standing.Email);
    }

    private static DateTimeOffset? LatestOrUnknown(IEnumerable<DateTimeOffset?> resets)
    {
        List<DateTimeOffset?> all = [.. resets];
        return all.Contains(null) ? null : all.Max();
    }

    private static UsageLimit? Bucket(AccountStanding standing, LimitKind kind) =>
        standing.Latest?.Limits.FirstOrDefault(limit => limit.Kind == kind);

    /// <summary>
    /// Group, then the instant it frees up with an unknown one after every known
    /// one inside that group, then the address. The tie-break is not cosmetic:
    /// accounts refreshed in one pass share a reset instant often enough that an
    /// unspecified order would reshuffle the page between two polls of the same
    /// state.
    /// </summary>
    public static IComparer<AvailabilityKey> Comparer { get; } = System.Collections.Generic.Comparer<AvailabilityKey>.Create(Compare);

    /// <summary>
    /// Every account, keyed and ordered. The single entry point: a caller that
    /// keyed the accounts itself and sorted them would be the second ordering in
    /// the process, and the one the operator is not looking at.
    /// <para>
    /// Each <see cref="ArrangedAccount.Standing"/> handed back is the caller's own
    /// instance, unchanged and never copied, so a caller may match it to whatever
    /// it holds beside it by reference identity. That is the only match that
    /// holds: two accounts can carry the same address, and matching on the address
    /// would fail on the pair.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ArrangedAccount> Arrange(
        IReadOnlyList<AccountStanding> standings,
        DateTimeOffset now,
        double eligibleFiveHourMaxPercent = 100,
        double eligibleSevenDayMaxPercent = 100)
    {
        ArgumentNullException.ThrowIfNull(standings);
        return [.. standings
            .Select(standing => new ArrangedAccount(
                KeyFor(standing, now, eligibleFiveHourMaxPercent, eligibleSevenDayMaxPercent),
                standing))
            .OrderBy(arranged => arranged.Key, Comparer)];
    }

    private static int Compare(AvailabilityKey? left, AvailabilityKey? right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        int byStanding = Rank(left).CompareTo(Rank(right));
        if (byStanding != 0)
        {
            return byStanding;
        }

        int byInstant = CompareInstants(left.NextResetAt, right.NextResetAt);
        return byInstant != 0
            ? byInstant
            : StringComparer.Ordinal.Compare(left.Email.Value, right.Email.Value);
    }

    /// <summary>
    /// The group's place in the list. An unread account that is in use, live
    /// here or held by another side, sits right after the usable ones rather than
    /// below every limited and exhausted account: someone chose it a moment ago,
    /// usually because it was usable, and its figures are only missing because
    /// no read or session has reported on it since. Sinking it to the bottom
    /// would hide the one card the operator is most likely to be looking for.
    /// It is still not <see cref="AvailabilityStanding.Usable"/>, so a ranked
    /// queue keeping only usable accounts does not offer it.
    /// </summary>
    private static int Rank(AvailabilityKey key) =>
        key.Standing == AvailabilityStanding.Unread && key.InUse
            ? (2 * (int)AvailabilityStanding.Usable) + 1
            : 2 * (int)key.Standing;

    /// <summary>
    /// Earliest first, and an unknown instant after every known one. The default
    /// nullable comparer sorts null ahead of every value, which the refresh order
    /// relies on for "never read first" and which would read here as "frees up
    /// soonest" about the one account whose window nobody can date.
    /// </summary>
    private static int CompareInstants(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is not DateTimeOffset known)
        {
            return right is null ? 0 : 1;
        }

        return right is DateTimeOffset other ? known.CompareTo(other) : -1;
    }
}
