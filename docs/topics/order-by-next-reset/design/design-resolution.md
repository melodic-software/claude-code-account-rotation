---
outcome: early-exit-light
tier: B
date: 2026-09-12
---

# Design resolution: order-by-next-reset

Light design. The types this work introduces were already specified by the parent topic's design
record, `docs/topics/claude-subscription-rotation/design/`: `type-inventory.md` fixes
`AccountStanding` as the App-assembled input row and `AccountRanking.Rank(IEnumerable<AccountStanding>,
RoutingPolicy, DateTimeOffset now)` as the ranking signature, and `library-topology.md` line 19 puts
both under `Core/Routing/`. This issue **reuses `AccountStanding`**, adding null defaults on its last
two parameters and nothing else, rather than declaring a parallel row; and it adds the ordering
primitive that Phase 3.2's `Rank` becomes a filter and a truncate over. No parent thread is
re-opened.

Three threads were open; all three are resolved below.

## What the code has that the record does not say

- No routing type exists yet: `RoutingPolicy`, `AccountStanding`, `AccountRanking` and
  `QueueCandidate` return zero grep hits in `src/` and `tests/`, and the configuration record
  carries no routing block or threshold constant.
- `Core/Routing/` does not exist as a directory; this issue opens it.
- The per-bucket merge that produces a card's rows is a private static inside
  `DashboardAssembler` (`Usage`, `Row`, `SameBucket`, `Position`), not a Core function — so
  `AccountStanding.Latest`, a single `UsageSnapshot`, has no producer today.
- `UsageSnapshot` has no derived helpers; the window-reset rule is open-coded as
  `limit.ResetsAt < capturedAt` inside `Row`.
- `RefreshOrder` leans on `Comparer<DateTimeOffset?>.Default` sorting null first, the inverse of
  the grouping this issue needs. The new key is an explicit tuple, not that idiom.

## Type sketch

All in `src/ClaudeCodeAccountRotation.Core/`, BCL only.

```csharp
// Quota/UsageLimit.cs — added member, the single window-reset predicate.
public bool HasResetBy(DateTimeOffset now) => ResetsAt is DateTimeOffset resets && resets < now;

// Quota/UsageMerge.cs — the assembler's per-bucket merge, lifted. Called exactly once per card:
// Merged carries the winning limits and the newest non-null ExtraUsage, which Credits() then reads
// instead of walking the sources again. Newest-wins is strict, so an equal CapturedAt leaves the
// first source's row and the first source's attribution in place.
public sealed record MergedLimit(UsageLimit Limit, UsageSnapshot Source);

public sealed record MergedUsage(
    UsageSnapshot Merged,
    UsageSnapshot Card,
    IReadOnlyList<MergedLimit> Rows);

public static class UsageMerge
{
    public static MergedUsage? Merge(IReadOnlyList<UsageSnapshot> sources);
}

// Routing/AccountStanding.cs — the parent inventory's row, with null defaults on the last two.
public sealed record AccountStanding(
    AccountEmail Email,
    bool IsLive,
    bool IsPaused,
    bool HasCredentials,
    UsageSnapshot? Latest = null,
    DateTimeOffset? LoginExpiresAt = null);

// Routing/AccountAvailability.cs — the ordering primitive.
public enum AvailabilityStanding { Usable, Exhausted, Unread, Paused }

public sealed record AvailabilityKey(
    AvailabilityStanding Standing,
    DateTimeOffset? NextResetAt,
    AccountEmail Email);

public sealed record ArrangedAccount(AvailabilityKey Key, AccountStanding Standing);

public static class AccountAvailability
{
    public static AvailabilityKey KeyFor(
        AccountStanding standing,
        DateTimeOffset now,
        double eligibleFiveHourMaxPercent = 90,
        double eligibleSevenDayMaxPercent = 100);

    public static IComparer<AvailabilityKey> Comparer { get; }

    // The single entry point. One call per payload; the pairs come back ordered by Comparer, so a
    // caller reads each row's group and instant off its own key and never sorts.
    public static IReadOnlyList<ArrangedAccount> Arrange(
        IReadOnlyList<AccountStanding> standings,
        DateTimeOffset now,
        double eligibleFiveHourMaxPercent = 90,
        double eligibleSevenDayMaxPercent = 100);
}
```

Enum declaration order is the group order. `AvailabilityKey` does not implement `IComparable<T>`:
under `AnalysisMode=All`, CA1036 would then demand the comparison operators a record cannot
generate, and a static comparer is the shape `ChromiumLocalStateProfileReader.CompareDirectories`
already established in this repository.

The rule `KeyFor` applies, in order: paused first of all; then unread when `Latest` is null or
carries neither a `session` nor a `weekly_all` figure; then `HasResetBy(now)` per bucket, a reset
bucket counting as 0 and contributing no instant; then exhausted when 5-hour `>=` the first
parameter or 7-day `>=` the second, keyed by the **latest** live reset among the buckets that
exhausted it, since an account exhausted on both frees up only when the later window turns over;
else usable, keyed by the live `weekly_all` reset. The comparer orders group, then instant with null
after every known instant, then ordinal e-mail.

Phase 3.2's `Rank` is then filter-plus-truncate over `Arrange`: drop `Paused`, drop `Exhausted`,
drop rows with `HasCredentials == false` (the dashboard shows such an account, a queue cannot switch
to it), flag `Unread` as "no data", take the first `QueueLength`. The eligibility numbers are
parameters precisely so 3.1's `RoutingPolicy` binds to them without changing a signature, and 3.1
threads the same two numbers into the assembler's `Arrange` call so card order and queue order
cannot diverge on a policy change.

## Threads

### T1 — where the per-bucket merge lives — RESOLVED

**Lifted into Core as `UsageMerge.Merge`, consumed by both the card rows and the ordering key.**
Keying off `QuotaState.LatestFor` alone would let the live card's key be computed from the on-demand
read while its rows show newer tee figures, and would hand Phase 3.2 an `AccountStanding.Latest`
that is not what the operator sees on the card; one merged snapshot makes that disagreement
impossible by construction.

Consequence recorded: `Merge` returns `MergedUsage`, not a bare `UsageSnapshot`. `Row()` decides
whether a row names its own source by `ReferenceEquals(source, card)`, so the merge must hand back
the winning source instance per row alongside the merged whole. The placeholder rows
(`UsageBuckets.Always`) and the row order (`Position`) stay in the App: they are page invariants,
not merge rules.

The seam is one call: `Card(...)` merges once and passes the `MergedUsage?` into `Usage(...)`, which
renders rows from it, and into the `AccountStanding` whose `Latest` is `merged?.Merged`. `Credits()`
reads `ExtraUsage` off that same `Merged` rather than recomputing it. A second `Merge` call anywhere
in the card path would reopen the discrepancy this thread closed, so the count is part of the
decision and not an implementation detail.

### T2 — the key's shape — RESOLVED

**An explicit three-part key — group, instant, ordinal e-mail — over a total order across every
account, including the ones 3.2 will filter out.** A total order is what makes 3.2 a filter rather
than a second sort, and the order among usable accounts is already 3.2's own (earliest `weekly_all`
reset first), which is what its `OrdersByEarliestWeeklyReset` test name demands. The explicit tuple
also avoids `RefreshOrder`'s null-first idiom, which would silently put never-read accounts first —
the opposite of what this issue asks for.

Three sub-decisions recorded. A bucket whose window has reset contributes **no** instant as well as
no percentage, so a past reset can never sort ahead of a live one or render as "resets in 0 s". A
null instant sorts after every known instant *within* its group, never across groups. And an
exhausted account keys on the **latest** live reset among the buckets that exhausted it, not the
earliest: an account over both limits is not free when the first window turns over, and the card
copy "usable in ..." is a claim the earliest instant would make false.

`Arrange` is the single entry point over this key: it keys every standing and returns
`ArrangedAccount(Key, Standing)` pairs already in comparer order. A caller that wanted the key alone
could call `KeyFor` and sort for itself, which is the second ordering this thread exists to
prevent, so the App takes the pairs and reads group and instant off them.

### T3 — the wire form — RESOLVED

**Two fields on `AccountCardView`: `standing`, a lower-case word (`usable` | `exhausted` |
`unread` | `paused`), and `nextResetAt`, a `DateTimeOffset?`.** Both are trailing parameters with
defaults so the two `RosterEndpoints` construction sites compile unchanged, the
`SwitchPlanningInput` precedent. The enum crosses as a word through a hand `Wire()` switch beside
the existing `Wire(QuotaSource)`, because the app configures no JSON enum converter.

No countdown string is formatted server-side. `app.js` already owns `relative(instant, from)` and
`secondsUntil(instant, from)` measured from `dashboard.capturedAt`, never the browser clock — a
deliberate, commented invariant — and formatting the phrase on the server would put a second
now into the payload. The page keeps its plain `forEach`: no client sort, no pinning, no DOM
regrouping.

One page behaviour does change, and it is about stability rather than form: because card order is
now data-dependent, a polled re-render can move a card out from under the pointer, and the Switch
button on it fires without a confirm. A non-forced `render` is therefore skipped while the pointer
or keyboard focus is inside `#cards`. A forced render, the one following the operator's own action,
always runs.

## Not re-opened

No new package, no change to Core's dependency posture, no new route, no change to the refresh
engine or to `RefreshOrder` (pass order is a different ordering), no persistence, and no new public
contract outside the local loopback API this tool already owns.
