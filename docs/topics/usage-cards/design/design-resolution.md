---
outcome: early-exit
tier: B
date: 2026-09-11
---

# Design resolution: usage-cards

Light design. The types, contracts, and module boundaries this work composes were resolved by the
parent topic's design record, `docs/topics/claude-subscription-rotation/design/`: thread T3 (the
quota data model, `UsageSnapshot` over a generic `limits[]`), T4 (the three refresh tiers and the
request budget), T5 (credential refresh on 401, write-back of the rotated pair), and the
`type-inventory.md` rows for `QuotaSource`, `UsageLimit`, `UsageSnapshot`, `RefreshBudget`,
`IUsageEndpointClient`, `ITokenRefreshClient`, `ICredentialPairStore.WriteParkedAsync`,
`QuotaRefresh`, `UsageSnapshotCache`, `DashboardAssembler`, and the two refresh routes. Nothing here
re-opens a thread.

## What the code has that the record does not say

The record is the intended shape; the code is the authority on what exists. Drift found while
planning, so the plan builds on the code:

- `RefreshBudget` exposes `LockedOutFor(AccountEmail)` returning the remaining `TimeSpan?`, not
  `LockedOutUntil`; it also has `RecordUnauthorized` (the 401 refund from the PR #15 security
  review). It refuses reads and never waits: no pacing of any kind lives in it.
- `UsageSnapshot` has no derived `FiveHourPercent` or `SevenDayResetsAt` helpers; it is the five
  positional members only.
- The configuration record carries no `RefreshBudgetSettings` and no `RoutingPolicy`; the budget's
  defaults are the constructor's.
- `UsageSnapshotCache`, `QuotaRefresh`, `AccountStanding`, and the recovery-file path do not exist.
- The tee reader parses only `five_hour` and `seven_day`, so a tee-sourced snapshot can never carry
  the scoped (Fable) bucket; only an endpoint read can.

## Type sketch, all in the App project unless named otherwise

- Core: `UsageSnapshot.FromStatusline(StatuslineSnapshot, AccountEmail)` turning the tee's two
  windows into `Session` and `WeeklyAll` limits with `Source = StatuslineSnapshot`. Pure.
- Core: `RefreshOrder.Order` (never read first, then oldest successful read), `UsageLimit.Label`,
  `RefreshBudget.GapRemaining`, and `SwitchRefusal.TargetStrandedInRecovery` with its planner
  input, so a folder whose rotated pair sits in recovery can never be switched to.
- `App/Quota/QuotaRefreshWorker`: a `BackgroundService` on a bounded channel (the
  `StateFileWatcher` shape); the routes start a run (202) and the dashboard poll observes it, so a
  browser abort or the shutdown drain never cuts a token rotation in half.
- `App/Quota/QuotaRefresh`: the engine the worker runs. Reads one account or every account,
  paces through an injected delay, resolves the transient typed clients per use, takes the
  `CredentialMutationGate` (2-second wait) only around the token POST plus compare-and-swap
  write-back under `CancellationToken.None`, and reports per-account outcomes as a flat
  `(Kind, Message, RetryAt, RecordedAt)` record.
- `App/Quota/QuotaState`: in-memory singleton holding the latest `UsageSnapshot` per account, the
  last refresh outcome per account, the pass-level rate lockout, and the refresh-in-progress flag.
- `App/Quota/UsageSnapshotCache`: `<appdata>/state/usage-cache.json` through `AtomicJsonFile`;
  loaded once at start into `QuotaState` with `Source = Cached`, saved after every pass. No token
  field.
- `App/Quota/RecoveryFiles`: writes `<appdata>/recovery/<folder>.credentials.json` (owner-only)
  when a write-back fails after retries, and restores it at the next start through the same
  compare-and-swap.
- Views: `UsageView`, `UsageLimitView`, `UsageCreditsView`, `RefreshStateView` replace
  `StatuslineQuotaView` on `AccountCardView`; every enum-shaped field is a string on the wire.
- Endpoints: `POST /api/refresh`, `POST /api/accounts/{email}/refresh` in the same-origin mutation
  group; refusals are 409 `SwitchRefusalView` tokens, never 429.

No new package, no change to Core's dependency posture, no new public contract outside the local
loopback API this tool already owns.
