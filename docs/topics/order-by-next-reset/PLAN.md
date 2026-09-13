# order-by-next-reset

Planned 2026-09-12 for issue #47 (order the dashboard cards by which account frees up next).
Parent topic: `docs/topics/claude-subscription-rotation/PLAN.md`, Phase 3 item 3.2. Design:
`design/design-resolution.md` (Tier B light design onto the parent's `Core/Routing/` rows).
Exploration: `.work/order-by-next-reset/EXPLORE.md` and its seven sidecars.

## Brief

### TLDR

- Card order today is one line: `cards.Sort(... string.CompareOrdinal(left.Email, right.Email))` at
  the end of `DashboardAssembler.AssembleAsync`. `app.js` renders `dashboard.accounts` with a plain
  `forEach` and pins nothing, so ordering is entirely a server concern and #47 is one server-side
  change plus two new per-card fields.
- Build the ordering as a **Core primitive** — a per-account standing plus a key and a comparer
  giving a total order — so that parent Phase 3.2's `AccountRanking.Rank` becomes a filter and a
  truncate over this order rather than a second, competing ordering. Nothing of Phase 3 exists yet
  (`RoutingPolicy`, `AccountStanding`, `AccountRanking`, `QueueCandidate` return zero grep hits in
  `src/` and `tests/`), so this issue is where the shared row and the shared order are introduced.

### The issue, as filed

Issue #47: order the cards by when each account next becomes usable, so the account that frees up
soonest reads first; an account whose quota has never been read has no position and is grouped
rather than interleaved; a paused account is out of the rotation; the live account is identifiable
wherever it lands rather than being pinned to the top; each card says how long until it frees up
("resets in 2h 14m"). Related and separate: visual regrouping (#50), login age (#49), alias (#48).

Read against the code, "rather than being pinned to the top" is already the status quo — no pin
exists — and "identifiable wherever it lands" is already satisfied by the `live` class and badge
`app.js` gives the live card.

### Goal

The dashboard lists accounts in the order they free up: the ones usable now first, by earliest
7-day reset, so the subscription closest to its window boundary is used first; then the exhausted
ones by when they actually come back; then the ones never read; then the paused ones. Each card
carries one line naming where it stands and, when there is a wait, how long that wait is, measured
from the payload's own instant. The rule lives in one pure Core function that Phase 3.2 filters and
truncates rather than re-sorts.

### Constraints

- Work happens on `feat-order-by-next-reset` in a worktree, off `main` at `5f27f60`. Never on the
  main checkout.
- **No real e-mail address, machine path, or user name in any tracked file.** `bash
  eng/check-no-machine-paths.sh` is the CI gate and scans `src tests README.md docs`; test data uses
  `example.com` addresses, as every existing suite does.
- Stage files by name, never `git add -A`. Run `git checkout -- '**/packages.lock.json'` after any
  build that drifts them; `dotnet restore --locked-mode` is what CI runs.
- Core stays BCL-only: no package reference, no project reference, no config binding. Thresholds are
  constructor or method parameters, the `RefreshBudget` precedent.
- Every host-visible instant in App tests comes from `factory.Clock`, never wall clock.
- Analyzer posture: `AnalysisMode=All` with `TreatWarningsAsErrors` and
  `EnforceCodeStyleInBuild`. Every CA and escalated IDE finding is a build error, and the prior
  chain's `docs/topics/usage-cards/DEVIATIONS.md` records that a no-op stub could not compile. The
  red step is therefore a test written against the real signature with a minimal implementation
  behind it, never `throw new NotImplementedException()`.
- Repo rules: Conventional Commits titles, `.claude/rules/pr-body-contract.md`, and the personal-org
  commit identity this checkout's `includeIf` supplies (verify `git config user.email` before
  committing; it must never report a work address).
- The live dashboard on the operator's machine (127.0.0.1:48211) is not stopped, restarted,
  reinstalled, seeded, or read by this work. Tests run against temp directories and the recording
  handler.

### Acceptance criteria

- `GET /api/dashboard` lists `accounts` in this total order: accounts usable now, by earliest
  `weekly_all` reset; then accounts exhausted now, by the **latest** live reset among the buckets
  that exhausted them, because that is the reset which actually frees the account; then accounts
  never read; then paused accounts. Ordinal e-mail breaks every tie, and within a group a card with
  no key instant sorts after every card that has one.
- A bucket whose `resetsAt` is earlier than the payload's `capturedAt` counts as **0 / usable**
  before the exhaustion test runs, exactly as `DashboardAssembler.Row` already blanks it. A cached
  `100 %` from a window that has since reset therefore sorts usable, and contributes no key instant.
- Exhausted means, after that rule: 5-hour `>= 90` or 7-day `>= 100`. Both numbers are parameters
  with those defaults (`EligibleFiveHourMaxPercent`, `EligibleSevenDayMaxPercent`), so Phase 3.1's
  `RoutingPolicy` binds to them later without a signature change. The scoped (Fable) bucket is not
  in the key; the card keeps its row.
- Each card carries `standing` (`usable` | `exhausted` | `unread` | `paused`) and `nextResetAt`
  (an instant or null), and the page renders one short line under the existing "as of" line:
  `usable now` / `usable in 2 h 14 min` / `no usage read yet` / `paused`, the countdown measured
  from `dashboard.capturedAt` through the existing `relative()`. No client sort, no pinning, no DOM
  regrouping: `app.js` keeps its plain `forEach`.
- The live card's ordering key cannot disagree with the rows printed on it: `UsageMerge.Merge` runs
  exactly once per card, and the rows the operator reads and the standing that keys the order are
  both taken from that one `MergedUsage`.
- `dotnet build -c Release` 0 warnings; `dotnet test -c Release` green with no test lost (baseline
  428 on `5f27f60`, 1 skipped); `dotnet format whitespace --verify-no-changes`, `typos .`,
  markdownlint on changed markdown, `shellcheck eng/*.sh`, and `bash eng/check-no-machine-paths.sh`
  clean.

### Captured assumptions

- `DashboardAssembler` reads `TimeProvider` once per assemble into `capturedAt` and that instant is
  the single "now" for the whole payload. The ordering is applied at that same instant, not at a
  second clock read.
- `AccountStanding.LoginExpiresAt` is passed as `null` throughout this issue. Login expiry is #49's
  chain; the field exists because the parent's type inventory specifies it and 3.2 will fill it.
- After a restart every account with a cache entry has a `resetsAt` and therefore a position;
  `UsageSnapshotCache` replays every entry through `QuotaState.RecordSnapshot`, so the
  window-reset-before-exhaustion rule is the common path after a restart, not an edge case.
- `SwitchEndpointTests.cs:54` asserts card order for two never-read accounts
  (`["a@example.com", "b@example.com"]`). Both land in the `unread` group and tie-break by ordinal
  e-mail, so it **stays green unchanged**. It is not updated.
- The two other `AccountCardView` construction sites (`RosterEndpoints.cs:98` and `:312`) hand back
  a card for an account nothing has read; trailing parameters with defaults keep them compiling
  unchanged, the `SwitchPlanningInput` precedent.

### Out-of-scope, stated

- The refresh engine and `RefreshOrder` (pass order, a different ordering this issue must not
  disturb), spike 02b, and anything that stops, restarts, reinstalls, or reads the operator's live
  dashboard on 127.0.0.1:48211.
- Phase 3.1 config binding of `RoutingPolicy`, and parent items 3.3 to 3.5 (`SwitchAdvisor`,
  decision-point auto-refresh, the queue panel).
- Visual regrouping or a paused section (#50), login age and refresh-token expiry (#49), editable
  alias (#48).
- Editing the parent plan's Phase 3 body. This chain adds only a dated scope-change note, planned as
  a Phase 3 work item.

## Plan

**Standards grounding:** no standards index exists — no `.claude/standards.yaml` at the repository
root and no `docs/standards/` directory — so resolution falls to the inference rung. What shaped
this plan instead: `.claude/rules/pr-body-contract.md` (the only rule file, ambient); the
sync-managed analyzer pair `Directory.Build.props` + `eng/dotnet-analysis/` with the repo-owned
`.globalconfig`; the Core csproj's stated BCL-only layer rule; the parent design record
(`design/type-inventory.md` rows for `AccountStanding`, `RoutingPolicy` and the `AccountRanking.Rank`
signature, its glossary row reserving `queue` and rejecting `Order` and `Rotation`, and
`design/library-topology.md` line 19 placing routing types under `Core/Routing/`); and the repo's own
conventions as recorded in `.work/order-by-next-reset/EXPLORE-patterns.md` and
`EXPLORE-constraints.md` (Core static pure function plus one sibling `<Type>Tests`, full-sentence
test names over Shouldly with a hand-rolled `TestClock`, long "why" doc comments, `IReadOnlyList`
returns, collection-expression spreads, ordinal tie-breaks). The two sibling chains
`docs/topics/usage-cards/` and `docs/topics/profile-order-and-card-label/` are the process template.

Scale: Medium (about eleven files across Core, App, tests, the page, and two markdown files).
Three phases, sequential. Integration-first is not used: the wire change is two fields on one
record, so the Core rule leads and the page trails.

Baseline to record before Phase 1: `dotnet test -c Release` total on `5f27f60` (expected 428, 1
skipped). Every later count is stated relative to that number.

### Phase 1: The merge function and the ordering primitive, in Core [DONE]

Review: architecture

Landed 2026-09-13 as `5e44f87` (the merge lift) and `a825814` (the ordering primitive). Every
Sanity Check bullet below held on re-run; a fresh-context verifier passed all ten of its criteria,
reading `UsageMerge` as line-for-line the assembler's private loop and the golden fixture as the
approved sequence. Test total 454 against a baseline of 431 on `7b1c62b` (the plan's 428 was the
pre-plan count). `HasResetBy` is pinned through the availability suite rather than a `UsageLimit`
fact of its own. `MergedUsage.Merged.Limits` is in merge-insertion order, not `Position` order;
Phase 2 keeps ordering rows in the App.

Tests first, in the `RefreshOrderTests` shape: `public sealed class <Type>Tests` with a class-level
doc comment arguing what the suite pins, full-sentence fact names, Shouldly, and the Core project's
own hand-rolled `TestClock` where a clock is needed (instants here are plain `DateTimeOffset`
constants, as in `RefreshOrderTests`). Red is achieved by writing each fact against the real
signature with a minimal body behind it — a key that returns the group only, before the instant and
the tie-break exist — never a `NotImplementedException` stub, which the analyzer posture rejects.

Work items, in order.

1. `src/ClaudeCodeAccountRotation.Core/Quota/UsageLimit.cs`: add
   `public bool HasResetBy(DateTimeOffset now) => ResetsAt is DateTimeOffset resets && resets < now;`
   with a doc comment naming the failure it prevents (a figure from the window before this one
   measures nothing the operator can act on). This becomes the **single** window-reset predicate:
   `DashboardAssembler.Row` calls it in Phase 2 instead of open-coding the comparison, and the
   ordering key calls it before testing exhaustion.
2. `src/ClaudeCodeAccountRotation.Core/Quota/UsageMerge.cs` (new): lift the assembler's private
   per-bucket merge into Core, unchanged in behaviour.
   - `public sealed record MergedLimit(UsageLimit Limit, UsageSnapshot Source);`
   - `public sealed record MergedUsage(UsageSnapshot Merged, UsageSnapshot Card, IReadOnlyList<MergedLimit> Rows);`
   - `public static MergedUsage? Merge(IReadOnlyList<UsageSnapshot> sources)` — null when `sources`
     is empty; otherwise the existing rule verbatim: newest source per bucket, `SameBucket` moved
     across with it, `Card` = the newest source that actually contributed a row falling back to the
     newest source there is, `Merged` = a synthesized `UsageSnapshot` carrying `Card.CapturedAt`,
     `Card.Source`, the winning limits, and the newest non-null `ExtraUsage`.
   - `MergedLimit.Source` is the **input instance**, never a `with` copy: `DashboardAssembler.Row`
     decides whether a row names its own source by `ReferenceEquals(source, card)`.
   - `ExtraUsage` is picked once here, the newest non-null across the sources, and lives on
     `Merged`. The App's `Credits(...)` then reads `merged.Merged.ExtraUsage` instead of walking the
     sources a second time, so there is one rule for which extra-usage figure a card shows.
   - The newest-wins comparison is **strict** (`>`) and the card's source is the **first** newest
     source, which is what `MaxBy` already returns today. Two sources captured at the same instant
     therefore leave the first one's row and the first one's attribution in place; the lift
     preserves both halves.
   - `Position(LimitKind)` and the always-three-rows placeholder (`UsageBuckets`) stay in the App:
     they are the page's row order and the page's "admit what you do not know" invariant, not a
     merge rule.
3. `src/ClaudeCodeAccountRotation.Core/Routing/AccountStanding.cs` (new): the parent type inventory's
   row, with null defaults added on the last two parameters:
   `public sealed record AccountStanding(AccountEmail Email, bool IsLive, bool IsPaused, bool HasCredentials, UsageSnapshot? Latest = null, DateTimeOffset? LoginExpiresAt = null);`
   so a caller that knows neither compiles. Names, order and types are the inventory's; the defaults
   are the only addition. This is the row 3.2 ranks; it is introduced here and not re-declared there.
4. `src/ClaudeCodeAccountRotation.Core/Routing/AccountAvailability.cs` (new), the primitive:
   - `public enum AvailabilityStanding { Usable, Exhausted, Unread, Paused }` — declaration order is
     the group order, and the comparer sorts on it.
   - `public sealed record AvailabilityKey(AvailabilityStanding Standing, DateTimeOffset? NextResetAt, AccountEmail Email);`
   - `public sealed record ArrangedAccount(AvailabilityKey Key, AccountStanding Standing);`, the
     pair `Arrange` hands back, so one call gives a caller both the order and, per row, the group
     and the instant it sorted by.
   - `public static class AccountAvailability` with
     `public static AvailabilityKey KeyFor(AccountStanding standing, DateTimeOffset now, double eligibleFiveHourMaxPercent = 90, double eligibleSevenDayMaxPercent = 100)`,
     `public static IComparer<AvailabilityKey> Comparer { get; }`, and
     `public static IReadOnlyList<ArrangedAccount> Arrange(IReadOnlyList<AccountStanding> standings, DateTimeOffset now, double eligibleFiveHourMaxPercent = 90, double eligibleSevenDayMaxPercent = 100)`,
     which keys every standing and returns the pairs ordered by `Comparer`. `Arrange` is the single
     entry point every consumer uses; `KeyFor` stays public because the Core suite pins the rule on
     one account at a time, and no caller outside that suite sorts a list itself.
   - The rule, in order: **paused** first of all (a paused account's numbers do not change its
     placement); then **unread** when `Latest` is null or carries no `session` and no `weekly_all`
     figure; then apply `HasResetBy(now)` per bucket, treating a reset bucket as 0 and as having no
     instant; then **exhausted** when `session >= eligibleFiveHourMaxPercent` or
     `weekly_all >= eligibleSevenDayMaxPercent`, with `NextResetAt` = the **latest** live `ResetsAt`
     among the buckets that exhausted it, because an account exhausted on both buckets frees up only
     when the later window resets and an earlier instant would promise availability it does not
     have; else **usable**, with `NextResetAt` = the live `weekly_all.ResetsAt`. `Unread` and
     `Paused` carry `NextResetAt = null`.
   - The comparer: group, then instant with **null after every known instant within the group**,
     then `StringComparer.Ordinal` on `Email.Value`. `AvailabilityKey` deliberately does **not**
     implement `IComparable<T>` — CA1036 would then require the comparison operators a record
     cannot generate, and a static comparer is the shape `ChromiumLocalStateProfileReader`'s
     multi-level comparator already established.
   - Doc comments argue the rules in house style and describe the type as a total order that a
     ranked queue filters and truncates: drop `Paused`, drop `Exhausted`, flag `Unread` as "no
     data", take the first however many are wanted. They name no phase number and no type that does
     not exist yet; the phase pointer lives in this plan and in the parent-plan note. The
     eligibility numbers are parameters so a routing policy can bind to them without changing this
     signature.
5. `tests/ClaudeCodeAccountRotation.Core.Tests/Quota/UsageMergeTests.cs` (new): the merge's own five
   facts, `TheNewestSourceWinsPerBucket`,
   `ASourceThatCarriesFewerBucketsDoesNotBlankTheOthers` (the tee-plus-read case the assembler
   suite already pins end-to-end), `TheMergedSnapshotNamesTheNewestContributingSource`,
   `NoSourcesMergeToNothing`, and `ASourceWithTheSameCaptureInstantDoesNotStealTheRow`, which drives
   two sources at an equal `CapturedAt` and asserts both that the first source keeps the row and
   that the card's chosen source is that first newest source: the two halves the strict `>` and
   `MaxBy` give today, and the two the lift must preserve.
6. `tests/ClaudeCodeAccountRotation.Core.Tests/Routing/AccountAvailabilityTests.cs` (new). Eighteen
   facts, each one an assertion sentence:
   - `AUsableAccountComesBeforeAnExhaustedOne`
   - `ExhaustedAccountsAreOrderedByTheResetThatActuallyFreesThem`
   - `AnAccountExhaustedOnBothBucketsKeysOnTheLaterReset`
   - `AnAccountNeverReadIsGroupedAfterTheExhaustedOnes`
   - `AnAccountWhoseOnlyFigureIsScopedIsUnread`
   - `APausedAccountSortsLast`
   - `APausedAccountSortsLastEvenWithHeadroom`
   - `APausedAccountThatIsAlsoLiveStillSortsLast`
   - `TheLiveAccountIsNotPinnedToTheTop`
   - `AWindowThatHasResetSinceCaptureCountsAsUsable` — a cached `100` whose `ResetsAt` is before
     `now`; asserts both the `Usable` standing **and** `NextResetAt is null`, so a past instant can
     never sort ahead of a live one or render as "resets in 0 s"
   - `FiveHourAtThresholdIsExhaustedAndOneBelowItIsUsable`
   - `SevenDayExhaustedAtOneHundredAndUsableAtNinetyNine`
   - `AScopedBucketDoesNotChangeTheStandingOrTheKey`
   - `OrdersByEarliestWeeklyResetAmongUsableAccounts`
   - `AccountsWithTheSameKeyInstantAreOrderedByEmail`
   - `AUsableAccountWithNoResetTimeSortsAfterTheOnesThatHaveOne`
   - `TheThresholdsAreParametersSoAPolicyCanMoveThem`
   - `TheOperatorsRosterShapeOrdersAsPresented`, the golden-order fixture of work item 7
7. The golden-order fixture, in the same suite. This is the operator's anonymized roster shape, as
   read from the running dashboard at planning time and carried here so the order can be judged
   against real numbers rather than against invented ones. Ten accounts at
   `now = 2026-09-13T00:30:00Z`, addressed `a@example.com` through `j@example.com`. Only the session
   and `weekly_all` buckets are seeded; the scoped bucket is omitted except on `h`, where it is
   `100` with the weekly instant, so the fixture proves the scoped bucket is ignored rather than
   merely asserting it elsewhere.

   | Account | session | `weekly_all` | scoped |
   |---|---|---|---|
   | `a` | 0, no reset | 42, resets 2026-09-15T09:00Z | none |
   | `b` | 0 | 84, resets 2026-09-16T00:00Z | none |
   | `c` (`IsLive`) | 23, resets 2026-09-13T04:50Z | 65, resets 2026-09-15T22:00Z | none |
   | `d` | 0 | 3, resets 2026-09-16T03:00Z | none |
   | `e` | 4, resets 2026-09-13T05:20Z | 31, resets 2026-09-18T08:00Z | none |
   | `f` | 3, resets 2026-09-13T05:20Z | 58, resets 2026-09-16T07:00Z | none |
   | `g` | 0 | 0, resets 2026-09-18T23:00Z | none |
   | `h` | 0, resets 2026-09-13T05:30Z | 93, resets 2026-09-13T16:00Z | 100, resets 2026-09-13T16:00Z |
   | `i` | 0 | 30, resets 2026-09-16T10:00Z | none |
   | `j` | 0 | 38, resets 2026-09-17T23:59Z | none |

   The fact asserts the exact sequence `h, a, c, b, d, f, i, j, e, g`, and separately that `c` is
   live and is not first. It is the one check that reads as a judgement rather than as a rule: the
   operator judges this sequence at plan approval, and if the sequence is wrong the key is wrong,
   whatever the per-rule facts say.
8. `docs/topics/order-by-next-reset/design/design-resolution.md` already records the type sketch and
   the three resolved threads; no design change is expected in this phase. If one is forced, it is a
   divergence, not an edit made in passing.

**Sanity Check:**

- `dotnet test -c Release` exit 0, failed 0, total >= baseline + 23 (eighteen ordering facts and
  five merge facts).
- `ls src/ClaudeCodeAccountRotation.Core/Routing/` lists `AccountStanding.cs` and
  `AccountAvailability.cs`.
- `grep -c ": IComparable" src/ClaudeCodeAccountRotation.Core/Routing/AccountAvailability.cs` prints
  `0`. The anchored form is the one that means what it says: the bare word appears in the doc
  comment explaining why the interface is not implemented.
- `grep -c "HasResetBy" src/ClaudeCodeAccountRotation.Core/Quota/UsageLimit.cs` is `>= 1`.
- `grep -c "eligibleFiveHourMaxPercent" src/ClaudeCodeAccountRotation.Core/Routing/AccountAvailability.cs`
  is `>= 2` and `grep -c "= 90" ...` is `>= 1`; likewise `eligibleSevenDayMaxPercent` with `= 100`.
- Each of `OrdersByEarliestWeeklyReset`, `FiveHourAtThreshold`, `SevenDayExhausted`, `Paused`:
  `grep -c "<name>" tests/ClaudeCodeAccountRotation.Core.Tests/Routing/AccountAvailabilityTests.cs`
  prints `>= 1` (these are the four substrings parent 3.2's own sanity grep looks for).
- `grep -rn "PackageReference\|ProjectReference" src/ClaudeCodeAccountRotation.Core/ClaudeCodeAccountRotation.Core.csproj | wc -l`
  prints `0`.
- `grep -rn "example.com" tests/ClaudeCodeAccountRotation.Core.Tests/Routing/AccountAvailabilityTests.cs | wc -l`
  is `>= 1` and `bash eng/check-no-machine-paths.sh` exits 0.

**Files affected (Phase 1)**

| File | Action | What changes |
|---|---|---|
| `src/ClaudeCodeAccountRotation.Core/Quota/UsageLimit.cs` | Modify | `HasResetBy(now)`, the single window-reset predicate |
| `src/ClaudeCodeAccountRotation.Core/Quota/UsageMerge.cs` | Create | The per-bucket merge, lifted from the assembler |
| `src/ClaudeCodeAccountRotation.Core/Routing/AccountStanding.cs` | Create | The parent inventory's input row |
| `src/ClaudeCodeAccountRotation.Core/Routing/AccountAvailability.cs` | Create | Standing enum, key, `ArrangedAccount`, comparer, `KeyFor`, `Arrange` |
| `tests/ClaudeCodeAccountRotation.Core.Tests/Quota/UsageMergeTests.cs` | Create | Five merge facts |
| `tests/ClaudeCodeAccountRotation.Core.Tests/Routing/AccountAvailabilityTests.cs` | Create | Eighteen ordering facts, the golden-order fixture among them |

### Phase 2: The assembler applies the order and emits the two fields [DONE]

Review: code-design

Landed 2026-09-13 as `e37b6d0` (the assembler applies the order) and `13aa037` (a fix-up). Every
Sanity Check bullet below held on re-run; a fresh-context verifier passed all ten of its criteria.
It also found that matching the arranged cards back by e-mail would fault the endpoint on a
hand-copied profile folder naming an account twice, where the page used to show two cards; the
fix-up matches by the `AccountStanding` instance the arrangement was handed and pins the two-card
behaviour with `TwoProfileFoldersNamingTheSameAccountBothShowACard`. Test total 457 against 454
at the end of Phase 1.

1. `src/ClaudeCodeAccountRotation.App/Dashboard/DashboardViews.cs`: two trailing parameters on
   `AccountCardView`, after `Roster`, each with a default so `RosterEndpoints.cs:98` and `:312`
   compile unchanged:
   `string Standing = "unread", DateTimeOffset? NextResetAt = null`. Doc-comment the pair: the word
   is the group the server placed the card in and the instant is the key it sorted by, so the page
   can say how long the wait is without re-deriving anything. Enum to wire through a hand `Wire()`
   switch beside the existing `Wire(QuotaSource)` — no JSON enum converter is configured, so every
   enum-shaped field crosses as a lower-case word.
2. `src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs`:
   - `Card(...)` calls `UsageMerge.Merge([...])` **exactly once** for that account and passes the
     resulting `MergedUsage?` into `Usage(...)`, which renders its rows from that value instead of
     merging again. From the same value it builds one `AccountStanding` (`IsLive`,
     `roster.Find(email)?.Paused ?? false`, `hasCredentials`, `Latest` = `merged?.Merged`,
     `LoginExpiresAt` = **null**, #49 owns it). One merge per card is what makes the rows and the
     standing incapable of disagreeing; two calls would reintroduce exactly the discrepancy T1
     closed.
   - `Usage(...)` keeps `UsageBuckets.Always` placeholders, `Position`, the credits rule and
     `UsageView.Unread` exactly where they are, and `Row` calls `limit.HasResetBy(capturedAt)`
     instead of open-coding `limit.ResetsAt < capturedAt`. `Credits(...)` reads
     `merged.Merged.ExtraUsage` rather than recomputing the newest non-null figure from the sources.
     The private `SameBucket` moves to Core with the merge; `Position`, `Wire`, `Credits` and `Row`
     stay.
   - `AssembleAsync` builds one `AccountStanding` and one card per account, then calls
     `AccountAvailability.Arrange(standings, capturedAt)` **once** for the whole list and emits the
     cards in the order it hands back, matching each `ArrangedAccount` to its card by e-mail and
     stamping that card's `Standing` and `NextResetAt` from its own `Key`. The single
     `cards.Sort(static (left, right) => string.CompareOrdinal(left.Email, right.Email))` goes away.
     The assembler never calls `KeyFor`, never sorts, and never re-derives group, instant or
     tie-break: there is one ordering in the process, and one entry point into it.
   - The class doc comment's "Ranking joins in a later phase" becomes an accurate sentence: the card
     order is the Core availability order, and ranking (the queue of three) still joins later.
3. `tests/ClaudeCodeAccountRotation.App.Tests/Dashboard/DashboardAssemblerTests.cs`: one new fact,
   `TheCardsAreOrderedByWhenEachAccountFreesUpNext`. Seeding path, spelled out because cards come
   only from the live account, the parked folders, and the roster: write a roster of five entries
   (one of them `Paused: true`) with `using RosterFile roster = new(factory.AppData);` and
   `UpdateAsync` (CA2000 is live in the test project, so the instance is either disposed by that
   `using` or resolved from `factory.Services`, never constructed loose), then
   `factory.Services.GetRequiredService<QuotaState>().RecordSnapshot(...)` for the ones that must be
   usable or exhausted — a roster-only card reads `quota.LatestFor`, so no profile folder is needed.
   Every instant is derived from `factory.Clock.GetUtcNow()`. Assert the `accounts` e-mail sequence
   with an order-sensitive `ShouldBe`, and assert `standing` and `nextResetAt` on two of the cards
   (one usable, one exhausted). Addresses are `example.com`.
   A second fact, `ACachedFigureFromAWindowThatHasResetSortsUsable`, drives the same path with a
   cached `100` whose reset is behind `factory.Clock`, asserting `standing` is `usable` and
   `nextResetAt` is null through the payload.
4. `tests/ClaudeCodeAccountRotation.App.Tests/Endpoints/SwitchEndpointTests.cs`: **unchanged, and
   deliberately so.** Both accounts there are never read, so both land in `unread` and tie-break by
   ordinal e-mail: `["a@example.com", "b@example.com"]` still holds. It is the regression that
   proves the new order did not disturb the old one for unread accounts.

**Sanity Check:**

- `dotnet test -c Release` exit 0, failed 0, total >= baseline + 25; `SwitchEndpointTests` passes
  with no edit (`git diff --stat -- tests/ClaudeCodeAccountRotation.App.Tests/Endpoints/SwitchEndpointTests.cs`
  prints nothing).
- `grep -c "CompareOrdinal" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs` prints
  `0`; `grep -c "Arrange" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs` is
  `>= 1`; `grep -c "KeyFor" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs` prints
  `0`, the one call being `Arrange`.
- `grep -c "UsageMerge" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs` is `>= 1`;
  `grep -c "private static bool SameBucket" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs`
  prints `0`.
- `grep -c "ResetsAt < capturedAt" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs`
  prints `0`; `grep -c "HasResetBy" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs`
  is `>= 1`.
- `grep -c "NextResetAt" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardViews.cs` is `>= 1`.
- `grep -c "TheCardsAreOrderedByWhenEachAccountFreesUpNext" tests/ClaudeCodeAccountRotation.App.Tests/Dashboard/DashboardAssemblerTests.cs`
  prints `1`; likewise `ACachedFigureFromAWindowThatHasResetSortsUsable`.
- `dotnet build -c Release` exit 0 with 0 warnings; `git status --short -- '**/packages.lock.json'`
  empty.

**Files affected (Phase 2)**

| File | Action | What changes |
|---|---|---|
| `src/ClaudeCodeAccountRotation.App/Dashboard/DashboardViews.cs` | Modify | `Standing` and `NextResetAt` on `AccountCardView`, with defaults |
| `src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs` | Modify | Merges once per card, builds `AccountStanding`, emits the order `Arrange` returns |
| `tests/ClaudeCodeAccountRotation.App.Tests/Dashboard/DashboardAssemblerTests.cs` | Modify | Two order and standing facts through `AppFactory` |
| `src/ClaudeCodeAccountRotation.App/Endpoints/RosterEndpoints.cs` | KEEP | Audited: both card sites compile unchanged on the trailing defaults |
| `tests/ClaudeCodeAccountRotation.App.Tests/Endpoints/SwitchEndpointTests.cs` | KEEP | Audited: stays green, two unread accounts, ordinal tie-break |

### Phase 3: The card line, and the close-out [DONE]

Landed 2026-09-13 as `a8c04f1` (the card line and the render guard) and `3f3e682` (the
CHANGELOG entries and the parent-plan note). Every Sanity Check bullet below held on re-run and a
fresh-context verifier passed all six of its criteria. The parent-plan note leads with a lower-case
`scope-change` so the grep in this section finds it. Items 6 and 7 close in the pull request.

1. `src/ClaudeCodeAccountRotation.App/wwwroot/app.js`: a `nextReset(account, at)` helper beside
   `refreshState`, and one `element("p", "next-reset", ...)` appended inside `usage(account,
   dashboard)` directly after the card's `asof` paragraph. Copy, using the existing `relative()`
   verbatim (so the operator reads "in 2 h 14 min", the page's established phrasing, rather than the
   issue's shorthand "2h 14m"):

   | `standing` | line |
   |---|---|
   | `usable` | `usable now` |
   | `exhausted`, with an instant | `usable in 2 h 14 min` |
   | `exhausted`, no instant | *(line omitted; the rows already say what is unknown)* |
   | `unread` | `no usage read yet` |
   | `paused` | `paused` |

   The usable line does not repeat the countdown: the 7-day row on the same card already says
   "resets in 2 h 14 min", and a second copy of that phrase directly under it reads as two different
   facts. What the operator cannot see anywhere else is the word, so the word is the line.

   No sort, no filter, no partition is added: `dashboard.accounts.forEach` stays exactly as it is,
   and `innerHTML` use does not change (the existing count of 2 holds).
2. `src/ClaudeCodeAccountRotation.App/wwwroot/app.js`, page stability: `render(dashboard, force)`
   skips a **non-forced** render while the pointer or keyboard focus is inside `#cards`, the guard
   being `cards.matches(":hover") || cards.contains(document.activeElement)`. Card order is now
   data-dependent, a poll can reorder the list under the operator's cursor, and the Switch button
   fires without a confirm, so a reorder between aim and click switches the wrong account. A forced
   render, the one that follows the operator's own action, still runs: the guard suppresses
   surprises, never feedback.
3. `src/ClaudeCodeAccountRotation.App/wwwroot/app.css`: a `.next-reset` rule in the muted token,
   matching `.asof`. One rule, no layout change.
4. `CHANGELOG.md` under `[Unreleased]`: an `### Added` entry for the card order and the per-card
   "frees up next" line (#47), and a `### Changed` entry naming the two new fields on each dashboard
   account and the polled re-render now deferring while a card is hovered or focused.
5. `docs/topics/claude-subscription-rotation/PLAN.md`: **a dated scope-change note under Phase 3
   item 3.2 only**, carrying the literal token `scope-change` in its heading so the note is
   greppable. It records that #47 built `AccountStanding` and the total order that 3.2
   truncates; that 3.2 is therefore filter-plus-truncate over `AccountAvailability.Arrange` rather
   than its own sort; that 3.2's filter must also drop rows with `HasCredentials == false`, since
   `Arrange` orders every account the dashboard shows and an account with no credentials is not a
   switch target; that 3.1 must thread `RoutingPolicy`'s two thresholds into `DashboardAssembler`'s
   `Arrange` call as well as into `Rank`, so card order and queue order can never diverge on a
   policy change; that the eligibility thresholds are already parameters awaiting 3.1's
   `RoutingPolicy`; and that four of 3.2's six sanity-grep substrings (`Paused`,
   `SevenDayExhausted`, `FiveHourAtThreshold`, `OrdersByEarliestWeeklyReset`) are already satisfied
   by `AccountAvailabilityTests`, so 3.2's own new tests are `TruncatesToQueueLength` and
   `FlagsUrgentWeeklyReset` — the grep line's `AccountRankingTests` should be read as "either
   suite". No tick, no item edit, no re-scoping of 3.1 or 3.3 to 3.5.
6. PR body, drafted **before** `gh pr create` per `.claude/rules/pr-body-contract.md`: Conventional
   Commits title (`feat: order the dashboard cards by which account frees up next (#47)`), body
   opening `Closes #47` and carrying non-empty `## Summary`, `## Fix`, `## Verification`, and
   `## Related` sections (`## Related` naming #50, #49, #48 and parent 3.2). Then the gate list:
   `dotnet build -c Release`, `dotnet test -c Release`, `dotnet format whitespace
   --verify-no-changes`, `typos .`, markdownlint on the changed markdown, `shellcheck eng/*.sh`,
   `bash eng/check-no-machine-paths.sh`, and `git status --short -- '**/packages.lock.json'` empty.
7. This topic's phase tags to `[DONE]`; a fresh-context phase verifier on the diff before the PR.

**Sanity Check:**

- `grep -c "next-reset" src/ClaudeCodeAccountRotation.App/wwwroot/app.js` is `>= 1`;
  `grep -c "innerHTML" src/ClaudeCodeAccountRotation.App/wwwroot/app.js` prints `2`;
  `grep -c "accounts.sort\|accounts.filter" src/ClaudeCodeAccountRotation.App/wwwroot/app.js` prints
  `0`.
- `grep -c ':hover' src/ClaudeCodeAccountRotation.App/wwwroot/app.js` is `>= 1`, the render guard.
- `grep -c "next-reset" src/ClaudeCodeAccountRotation.App/wwwroot/app.css` is `>= 1`.
- `grep -c "#47" CHANGELOG.md` is `>= 1`.
- `grep -c "scope-change" docs/topics/claude-subscription-rotation/PLAN.md` prints `1`, against `0`
  on `5f27f60`: the token is absent from the parent plan today, so the note must carry it
  literally. `git diff --stat 5f27f60 -- docs/topics/claude-subscription-rotation/PLAN.md` shows
  insertions only.
- `grep -c '^### Phase.*\[DONE\]' docs/topics/order-by-next-reset/PLAN.md` prints `3` before the PR
  merges. The pattern is anchored on the heading because the bare `[DONE]` string already matches
  body lines in this file.
- `bash eng/check-no-machine-paths.sh` exits 0; `markdownlint-cli2` clean on the changed markdown.

**Files affected (Phase 3)**

| File | Action | What changes |
|---|---|---|
| `src/ClaudeCodeAccountRotation.App/wwwroot/app.js` | Modify | `nextReset()` helper, one line per card, and the hovered-or-focused render guard |
| `src/ClaudeCodeAccountRotation.App/wwwroot/app.css` | Modify | One `.next-reset` rule |
| `CHANGELOG.md` | Modify | Added / Changed entries for #47 |
| `docs/topics/claude-subscription-rotation/PLAN.md` | Modify | One dated scope-change note under 3.2 |
| `docs/topics/order-by-next-reset/PLAN.md` | Modify | Phase tags to `[DONE]` |

## Alternatives considered

| Alternative | Why rejected | Switch condition |
|---|---|---|
| Key off `QuotaState.LatestFor` alone, leaving the merge in the assembler | The live card's key would be computed from the on-demand read while its rows show tee figures; the two can disagree, and 3.2 would inherit an `AccountStanding.Latest` that is not what the card shows | If lifting the merge forces a behaviour change in any existing assembler fact, keep the merge in the App for this issue and record the live-card discrepancy as a residual for 3.2 |
| Sort in `app.js` instead of the server | Two orderings, one of them untestable in this repo (no JavaScript harness exists) and neither reusable by 3.2 | Never for ordering; only if a future issue needs a per-viewer order the server cannot know |
| One blanket exhaustion threshold instead of two parameters | 3.1 binds two distinct numbers (90 and 100); one number now means a signature change later, which is the thing this issue exists to avoid | If the operator reports the two-number rule reads confusingly on the card before 3.1 lands |
| Include the scoped (Fable) bucket in the key | An account can hold several scoped windows, keyed by raw kind plus display name, so "the" scoped reset is not a single instant; parent 3.1 and 3.2 name only the two windows. Rejected pending Decisions row 6, which the operator has not yet confirmed | If the operator overrides row 6, or reports scoped exhaustion driving real switches; it then arrives as a policy flag, not a change to the key's shape |
| Pin the live card to the top | No pin exists today, and the issue asks for the opposite ("identifiable wherever it lands", already true via the `live` class and badge) | Only on an explicit operator request |
| A visual paused section in the page | New DOM structure belongs to the visual design pass (#50); paused-last server-side already delivers the ordering half | If #50 lands first and wants the grouping driven by the `standing` field this issue already emits |
| Name the primitive `AccountOrder` / `RotationOrder` / anything with "queue" | The parent glossary reserves `queue` for `QueueCandidate` / `AccountRanking.Rank` and lists `Order` and `Rotation` as rejected synonyms | None; the alternative name held in reserve is `NextAvailability` |

## Test strategy

- **Test-first throughout.** Red is a test written against the real signature with a minimal
  implementation behind it (group-only key, then instant, then tie-break), because
  `AnalysisMode=All` + `TreatWarningsAsErrors` rejects a `NotImplementedException` stub — the prior
  chain recorded exactly this in `docs/topics/usage-cards/DEVIATIONS.md`. That minimal body already
  carries `ArgumentNullException.ThrowIfNull` on every public entry point, because CA1062 is live
  and a body without it does not compile. Red is therefore a failing assertion, never a compile
  error: a phase that cannot build has not reached red.
- **Test boundaries, settled by approving this plan.** (a) `AccountAvailability.Arrange` and
  `KeyFor` — new, the Core suite's public surface. (b) `UsageMerge.Merge` — new. (c)
  `GET /api/dashboard` through `AppFactory` — existing, for the payload's `accounts` order and the
  two new fields. No other boundary is driven; an implementer picking a different one is a
  divergence.
- Core suites follow `RefreshOrderTests`: no package reference, plain `DateTimeOffset` constants or
  the project's own `TestClock`, Shouldly, full-sentence fact names, `example.com` addresses.
- App facts seed through the container (`QuotaState.RecordSnapshot`) and the roster file, never
  through wall-clock instants; `factory.Clock` is the only clock.
- Edge cases explicitly covered: a window that reset since capture (standing **and** null instant);
  an account exhausted on both buckets, keyed on the later of the two resets; the threshold
  boundaries on both buckets (90/89 and 100/99); a scoped bucket present but ignored, and an account
  whose only figure is scoped, which is unread; a paused account with plenty of headroom and a
  paused account that is also live; the live account not pinned; equal instants; equal merge capture
  instants; a usable account with no reset time at all.
- The golden-order fixture is the semantic check the per-rule facts cannot be: ten accounts in the
  operator's own roster shape, one asserted sequence. A rule change that keeps every unit fact green
  and still reorders the real roster fails here.
- Existing tests updated: none. `SwitchEndpointTests.cs:54` stays green unchanged and is the
  regression guard for the unread group.
- Not tested by design: the page, including the hovered-or-focused render guard. No JavaScript
  harness exists in this repository; `app.js` is verified by reading and by the payload the App test
  asserts, the posture the prior chain recorded. The guard is therefore a judgement call the
  operator confirms on the live page, not an assertion.

## Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Lifting the merge changes card rendering subtly | Med | High | Move it verbatim, `MergedLimit.Source` as the input instance so `ReferenceEquals` still works; the whole existing `DashboardAssemblerTests` suite is the regression net and must stay green with no edits to its existing facts |
| CA1036 / CA1051 / CA1002 turn the new public Core types into build errors | Med | Low | No `IComparable` on the key (a static `IComparer` instead), `IReadOnlyList` returns, `ArgumentNullException.ThrowIfNull` on every public entry point |
| A past `ResetsAt` becomes the key instant and sorts a stale card first | Med | Med | The key drops a reset bucket's instant along with its percentage, with its own test asserting both halves |
| Phase 3.2 later needs a different order after all | Low | Med | The order is `Arrange`, parameterized on the two numbers 3.2 filters by; if 3.2 diverges, it filters a different group rather than re-sorting |
| The operator finds the new order disorienting on a live page | Low | Low | `standing` is on the wire, so a follow-up can group or label without another server change; `git revert` restores the ordinal sort in one commit |
| A poll reorders the cards under the operator's cursor and the unconfirmed Switch button hits the wrong account | Med | Med | The hovered-or-focused render guard in Phase 3 item 2: a non-forced render is deferred while the pointer or focus is inside `#cards`. Med/Med before the guard, Low after |
| A stale cached percent drives the order | Med | Med | Guarded only by the window-reset rule, which drops a figure whose window has already turned over. Per-row age is on the card, but the order does not disclose it. **Accepted for this issue:** the `standing` word is on the wire, so a follow-up can surface staleness without another server change |
| A tee-written far-future reset instant sorts a healthy account last and renders an absurd countdown | Low | Med | **Accepted:** `JsonInstant` already nulls an out-of-range value, and a null instant sorts after the known ones within its group rather than corrupting them. Note it as a follow-up if the operator ever sees it |
| The key has no capacity dimension: an account with 1 percent weekly left resetting soonest sorts first | Med | Low | **By design:** the order is burn-down, using the subscription closest to its boundary first. The golden-order fixture makes the consequence visible and the operator judges that sequence at approval; parent 3.3's `SwitchAdvisor` owns the 5-hour headroom threshold that turns "next to free up" into "worth switching to" |
| The parent-plan note drifts from what 3.2 actually finds | Low | Low | The note states what exists and names the suite; it ticks nothing and edits no item |

## Blast radius

**LOW–MEDIUM.** About eleven files; the behaviour change is the sequence of cards on the operator's
local dashboard plus one line of copy per card. Nothing is persisted, no token is touched, no
credential file is written, no outbound request is made, and the refresh engine and `RefreshOrder`
are untouched. The one non-trivial edit is lifting the per-bucket merge into Core, which is a
behaviour-preserving move guarded by the existing assembler suite; `git revert` undoes the whole
change cleanly. It sits above LOW because the merge lift touches the code path that renders every
card, on the operator's live tool.

## Stress-test summary

**Fresh-context plan review.** One critical, five important, seven suggestions.

- Critical: the exhausted key took the **earliest** reset among the exhausting buckets, so an
  account exhausted on both would have sorted, and rendered, as free before it actually was. Fixed:
  the key is the latest live reset, with its own fact.
- Important, all five fixed: `Arrange` was not the single entry point (the assembler keyed per card
  and sorted itself); the `[DONE]` sanity grep was unanchored and already matched body lines; two
  sanity greps demanded exact counts a doc comment or a second call site would break, and are now
  `: IComparable` prints `0` and `HasResetBy` is `>= 1`; a poll could reorder cards under an
  unconfirmed Switch; and the merge lift did not pin tie-order on an equal capture instant.
- Suggestions: applied except two. `MergedUsage` keeps its `Merged` member, because
  `AccountStanding.Latest` is exactly that value and removing it would push the synthesis back into
  the App. The doc comment's forward reference to a phase number was removed rather than reworded:
  the type describes itself as a total order a ranked queue filters and truncates, and the phase
  pointer lives in this plan and in the parent-plan note.

**Devil's advocate: PROCEED WITH REVISIONS.** Two high, three medium, four low.

- High: the key carries no capacity dimension, answered by the golden-order fixture, a Risks row,
  and the handoff of the headroom threshold to parent 3.3; and the polled reorder under an
  unconfirmed Switch, answered by the render guard.
- Medium: the merge seam was implicit and is now named (one `Merge` per card, `ExtraUsage` picked
  there); the stale-percent exposure is recorded and accepted; the golden-order fixture is the
  semantic check the per-rule facts cannot be.
- Low: the tie-order fact, the corrected suite counts (thirteen ordering facts had grown to
  eighteen), the tee-written far-future instant row, and `IsActive`, which nothing reads.

Blast radius confirmed **LOW–MEDIUM**: no credential path, no new route, no persistence.

## Execution shape

Fully sequential: Phase 1 gates Phase 2 (the assembler consumes types Phase 1 creates), Phase 2
gates Phase 3 (the page renders fields Phase 2 emits). No parallel wave: the three phases share
`DashboardAssembler.cs` and its suite transitively, and the whole change is roughly 400 lines.

### Phase file-overlap matrix

| Phase | Files | Overlaps with |
|---|---|---|
| 1 | `Core/Quota/UsageLimit.cs`, `Core/Quota/UsageMerge.cs`, `Core/Routing/*`, two Core test files | none |
| 2 | `App/Dashboard/DashboardAssembler.cs`, `App/Dashboard/DashboardViews.cs`, `App.Tests/Dashboard/DashboardAssemblerTests.cs` | consumes Phase 1's types |
| 3 | `wwwroot/app.js`, `wwwroot/app.css`, `CHANGELOG.md`, two PLAN.md files | consumes Phase 2's wire fields |

### Dependency graph

- Phase 1 → Phase 2: the assembler calls `UsageMerge.Merge` once per card and
  `AccountAvailability.Arrange` once per payload.
- Phase 2 → Phase 3: `app.js` reads `account.standing` and `account.nextResetAt`.
- Phase 3's parent-plan note and PR body depend on Phases 1 and 2 having landed as described.

### Per-phase routing table

| Phase | Surface | Basis |
|---|---|---|
| 1 | main session, or one implementer worker | self-contained Core work with a precise brief; the whole phase fits one turn budget |
| 2 | main session, or the same implementer worker | the merge lift wants the Phase 1 context in the same head; splitting it costs a re-read |
| 3 | main session | copy, a parent-plan note, and the PR body are judgment and outward actions |

If a worker is used, its brief carries the ALLOWED list from that phase's Files-affected table,
FORBIDDEN on everything else including both PLAN.md files, and the standard divergence-escalation
clause. Sequential fallback: on a divergence report, the main session finishes that phase inline;
the later phases are unaffected because nothing runs concurrently.

## Decisions made (gate-passed)

Every row below was settled by the brief owner before planning, except the rows tagged
`[EXEC-SHAPE]`, which are this plan's own (rows 5, 8, 9, 10, 15, 18, 20, 21 and 22 wholly; rows 1,
11 and 12 in the part noted). Row 6 is the one row nobody has confirmed and is tagged accordingly.

| # | Decision | Tag |
|---|---|---|
| 1 | The primitive is `Core/Routing/AccountAvailability` (enum `AvailabilityStanding`, record `AvailabilityKey`, `KeyFor` / `Comparer` / `Arrange`); reserve alternative `NextAvailability`. Not "queue", "Order", or "Rotation" — the parent glossary reserves all three | brief + `[EXEC-SHAPE]` name |
| 2 | Group order usable → exhausted → never-read → paused, then instant, then ordinal e-mail | brief |
| 3 | Exhausted = 5-hour `>= 90` or 7-day `>= 100`, as parameters with those defaults; no config binding here (3.1 binds later) | brief |
| 4 | The window-reset rule runs before the exhaustion test; a reset bucket counts as 0 / usable | brief |
| 5 | A reset bucket also contributes **no** key instant, and a null instant sorts after every known instant within its group | `[EXEC-SHAPE]` |
| 6 | The scoped (Fable) bucket is out of the key; the card keeps its row. Confirmed by the operator at approval on 2026-09-12, knowing that three live accounts sit at 100 percent Fable with weekly headroom and sort `usable` under this rule | brief, confirmed at approval |
| 7 | The per-bucket merge is lifted into Core and feeds both the rows and the key | brief |
| 8 | The merge returns `MergedUsage(Merged, Card, Rows)`, not a bare `UsageSnapshot`: `Row()` needs per-row attribution and compares the winning source by reference | `[EXEC-SHAPE]` — deviation from the brief's literal wording, same intent |
| 9 | `AvailabilityKey` does not implement `IComparable<T>` (CA1036 under `AnalysisMode=All`); a static `IComparer<AvailabilityKey>` carries the order | `[EXEC-SHAPE]` |
| 10 | `UsageLimit.HasResetBy(now)` is the single window-reset predicate, called by both the key and `Row` | `[EXEC-SHAPE]` |
| 11 | Wire fields `standing` (lower-case word) and `nextResetAt`; trailing parameters with defaults so the two `RosterEndpoints` card sites compile unchanged | brief + `[EXEC-SHAPE]` defaults |
| 12 | The countdown is rendered in `app.js` through the existing `relative()` from `dashboard.capturedAt`; the card copy is the table in Phase 3 | brief + `[EXEC-SHAPE]` copy |
| 13 | Paused sorts last server-side; no client sort, no pinning, no DOM regrouping | brief |
| 14 | `SwitchEndpointTests.cs:54` is kept green unchanged, not updated | brief |
| 15 | `AccountStanding.LoginExpiresAt` is null throughout this issue (#49 owns login age) | `[EXEC-SHAPE]` |
| 16 | The red step is a real minimal implementation, never a `NotImplementedException` stub | brief |
| 17 | The parent plan gets one dated scope-change note under 3.2 and nothing else, planned as a Phase 3 work item | brief |
| 18 | Three phases, fully sequential, all main-session or one implementer worker | `[EXEC-SHAPE]` |
| 19 | An exhausted account keys on the **latest** live reset among the buckets that exhausted it, not the earliest: it frees up only when the later window turns over | brief, revised at review |
| 20 | `Arrange` is the single entry point, returning `ArrangedAccount(Key, Standing)` pairs already ordered; the assembler calls it once per payload and never sorts or calls `KeyFor` | `[EXEC-SHAPE]` |
| 21 | `render(dashboard, force)` skips a non-forced render while the pointer or focus is inside `#cards`; a forced render still runs | `[EXEC-SHAPE]` |
| 22 | The usable card's line is `usable now`, not a second countdown: the 7-day row beside it already says "resets in ..." | `[EXEC-SHAPE]` |

## Open questions

The six questions `EXPLORE-questions.md` raised were resolved by the brief owner before planning and
are carried in the Decisions table above. Two things were put to the operator at approval on
2026-09-12 and both were approved as written:

1. **Decisions row 6, the scoped (Fable) bucket staying out of the key.** Three live accounts sit
   at 100 percent Fable with weekly headroom; under this rule each sorts `usable`. The operator
   accepted that; a third threshold is the switch condition in Alternatives if it reads wrong.
2. **The golden-order sequence**, Phase 1 work item 7: ten accounts in the operator's own roster
   shape ordering `h, a, c, b, d, f, i, j, e, g`, the account nearest its weekly boundary first.

None remain open.

## Handoff to implementation

### User-approval gates

- The `[EXEC-SHAPE]` rows above are this plan's own calls; the operator can override any of them
  before implementation starts. Row 8 (the merge's return shape) is the one that departs from the
  brief's literal wording and is surfaced in the PR body as well.
- Row 6, the scoped (Fable) bucket staying out of the key, and the golden-order sequence in Phase 1
  work item 7 were both put to the operator at approval on 2026-09-12 and both approved as
  written.
- Anything that would edit the parent plan beyond the single dated note under 3.2, or that would
  change `SwitchEndpointTests.cs`, stops and asks.
- Any need to start, restart, or read the operator's live dashboard stops and asks; nothing in this
  plan requires it.

### Execution shape ([EXEC-SHAPE] tagged)

- `[EXEC-SHAPE]` Three sequential phases; Core first, then the assembler, then the page and the
  close-out. Integration-first is not used because the wire change is two fields on one record.
- `[EXEC-SHAPE]` `UsageMerge.Merge` runs once per card, and the rows and the standing both come out
  of that one value; the assembler never merges twice.
- `[EXEC-SHAPE]` `Arrange` is the single entry point: one call for the whole payload, returning
  `ArrangedAccount` pairs already ordered. The assembler reads group and instant off each key and
  never sorts, never calls `KeyFor`, and never re-derives the ordering.
- `[EXEC-SHAPE]` Placeholder rows (`UsageBuckets.Always`) and row order (`Position`) stay in the
  App: they are page invariants, not merge rules.

### Mechanical work

- Commit boundaries: `docs(plan)` for this topic first, then one `feat` commit per phase. The PR
  squash-merges, so intermediate commits cost nothing.
- After every build: `git checkout -- '**/packages.lock.json'` if it drifted, and stage by name.
- Verification checkpoints: each phase's Sanity Check list; the Phase 3 gate list; a fresh-context
  phase verifier on the diff before the PR.
- Sequential; the fallback from a worker divergence is to finish that phase in the main session.
