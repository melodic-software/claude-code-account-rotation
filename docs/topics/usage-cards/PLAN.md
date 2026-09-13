# usage-cards

Planned 2026-09-11 for issue #52 (usage cards: the 5-hour, 7-day, and Fable buckets per account
with source and capture time, plus a Refresh all action). Parent topic:
`docs/topics/claude-subscription-rotation/PLAN.md`, Phase 2 items 2.5 and 2.6. Design:
`design/design-resolution.md` (early exit onto the parent's threads T3 to T5).

## Brief

### TLDR

- Ten accounts are logged in and the page shows no quota for any of them. The parser, the budget,
  both endpoint adapters, and the tee reader exist and are tested (parent items 2.1 to 2.4, PR #15),
  but nothing composes them: there is no refresh engine, no refresh route, and `app.js` never reads
  the tee-derived numbers the dashboard payload already carries for the live card.
- Build the engine as a hosted background pass the routes start and the page's existing poll
  observes, replace the 5h/7d-shaped card view with a per-bucket view that names its source and
  capture time, render it, and add Refresh all. A parked account's access token is almost always
  expired when it is read, so the token refresh and compare-and-swap write-back is the normal path
  of a pass, not the exception, and it lands in the first phase with its recovery file and the
  switch refusal that protects a stranded folder.

### The issue, as filed

Issue #52, "What to build": per card, the five-hour bucket, the seven-day bucket, and the Fable
bucket, each with a percentage and its reset time, plus the source the figure came from and when
it was captured ("a card that silently shows a stale number is worse than one that admits it is
stale"); a "Refresh all" action that reads every account once, honouring the refresh budget
("roughly eight reads per rolling five minutes per token under an honest User-Agent, then a
300-second lockout"; one read per account per refresh, never a timer poll); an account whose quota
has never been read renders as unknown rather than as zero or empty. Acceptance: Refresh all, then
compare one card against that account's usage page on claude.ai. Related, filed separately:
ordering by next availability (#47), login age and refresh-token expiry (#49), and spike 02b (per
token or per client), whose "shared" answer re-scopes the refresh pacing.

### Goal

After Refresh all, every card that has credentials and that the rate window allowed shows its
5-hour, 7-day, and Fable buckets with the percentage, the reset time, the source the figure came
from, and when it was captured; a card the window did not allow says "rate limited, retry in N s"
and is first in line for the next pass; a card never read shows unknown rather than zero; a card
that could not be read says why; and one usage read per account per pass is all the endpoint ever
sees, under the budget and paced.

### Constraints

- The live dashboard on the operator's machine and everything under the per-user app data
  directory are the operator's live tool with ten real accounts: nothing here stops, restarts,
  reinstalls, seeds, patches, or reads it. Tests run against temp directories, fake ports, and a
  recording HTTP handler. No outbound request leaves a test. Spike 02b (parent item 2.0) needs
  real parked tokens and is not run here. No throwaway instance is started against a config that
  leaves any path at its default: `ConfigurationDefaults.ForUser` points the live directory, state
  file, and profiles root at the real machine and `GET /api/dashboard` takes the credential gate
  and can patch the state file.
- Work happens on `feat-usage-cards` in a worktree, never on the main checkout, off `main` at
  `ed25bbb`.
- Repo rules hold: Conventional Commits, `.claude/rules/pr-body-contract.md`, `bash
  eng/check-no-machine-paths.sh` clean, no real address, machine path, or user name in any tracked
  file, stage by name, `git checkout -- '**/packages.lock.json'` after any build that drifts them,
  commit identity is the `kyle-sexton` noreply address.
- Layer rule: Core holds pure types, ports, and functions and references no package; App holds the
  engine, adapters, endpoints, and the page. No new package.
- Security rules the feature inherits: both new routes sit in a `SameOriginMutationFilter` group;
  no token reaches a view, a log line, the cache file, or the page; a parked pair is rewritten only
  through `ICredentialPairStore.WriteParkedAsync` (compare-and-swap on the fingerprint) under the
  `CredentialMutationGate` with `ILoginSessionRunner.IsRunningAgainst(folder)` checked under the
  gate; the live pair is never refreshed by this tool; the old refresh token is dead the moment the
  token endpoint answers 200, so a rotated pair is never discarded and the token POST plus the
  write-back run under `CancellationToken.None`, bounded by the adapter's 20-second timeout and
  the write retry budget, never by a request's token.
- Parent Phase 2 sanity greps hold: no codenamed bucket name in `src/`
  (`seven_day_opus|seven_day_sonnet|tangelo|nimbus_quill|cinder_cove`); the cards are driven by the
  generic `limits[]` array.

### Acceptance criteria

- `POST /api/refresh` answers 202 at once and starts one pass over every account that has
  credentials and is not paused, ordered by last successful read (never read first), one usage
  read each; the pass runs in the hosted worker, `GET /api/dashboard` reports `refresh.inProgress`
  while it runs and each card's state as it lands, and the page then shows, per card, the 5-hour
  row, the 7-day row, every scoped row (`Fable`) each with percent and reset time, or "unknown" for
  a bucket the source did not carry, plus "as of <time> via refresh|snapshot|cached" and the
  usage-credits line. A bucket whose reset time has passed since its capture renders unknown with
  "window reset since last read".
- A parked account whose access token is expired, or whose read answers 401, gets exactly one token
  POST and its rotated pair written back by compare-and-swap before the usage read is retried once;
  the written file carries the new `refreshToken`, `accessToken`, `expiresAt`, and
  `refreshTokenExpiresAt` (kept as it was when the response omits `refresh_token_expires_in`),
  with sibling keys untouched. The live pair gets zero token POSTs and a 401 on it renders
  "session will refresh". A pair that is missing or changed when re-read under the gate, or a
  folder with a login in flight, gets zero token POSTs and reports "skipped" with the reason.
- A write-back that fails, by `Result` or by exception, after the retry budget lands the rotated
  pair in an owner-only file under `<appdata>/recovery/`; the card shows "credentials stranded in
  recovery"; a switch to that folder is refused server-side with `TargetStrandedInRecovery`; the
  next start and the next per-card refresh both try the restore through the same compare-and-swap;
  a restore whose folder was legitimately rewritten since (fingerprint mismatch) moves the file to
  `<appdata>/recovery/stale/` and warns; a restore whose parked file is absent keeps the file and
  warns, naming the account to log in again. A failed recovery write reports "lost" with the
  instruction to log in again. Nothing in the restore can stop the app from starting.
- A 429 on the usage host ends the pass; the account that got it and every account after it report
  "rate limited, retry in N s" and keep their place at the head of the next pass; every usage read
  is refused until `Retry-After` expires (300 s when the header is absent or zero). A 429 on the
  token host ends the pass the same way with its own lockout and message. Over three consecutive
  passes with a 429 at the ninth read, every account is read at least once. A budget refusal
  (seventh read in five minutes, or within 60 s of the last read) reports "read N s ago" and sends
  nothing; the reservation is taken only when a request is about to be sent.
- An account never read renders unknown on every row with no "as of" line. A snapshot from the tee
  renders "via snapshot"; a snapshot loaded from the cache file after a restart renders "via
  cached" with its original capture time. The live card merges per bucket: each row comes from the
  most recent source that carries it, so a tee write after a refresh never blanks the Fable row.
- `POST /api/accounts/{email}/refresh` reads one account under the same rules (and restores a
  stranded one first); 400 bad e-mail, 404 unknown account, 409 `RefreshInProgress` while any pass
  or single read runs, 409 `RateLimited` under a lockout.
- Both routes refuse without the `X-Claude-Code-Account-Rotation` header (403) and with a
  cross-site `Origin` (403), with no outbound request sent. No card message and no dashboard
  payload for a stranded card contains a path separator, `refresh-`, or `access-`.
- `dotnet build -c Release` 0 warnings; `dotnet test -c Release` green with no test lost (baseline
  343, 1 skipped); `dotnet format whitespace --verify-no-changes`, `typos .`, markdownlint on
  changed markdown, `shellcheck eng/*.sh tests/acceptance/*.sh`, and `bash
  eng/check-no-machine-paths.sh` clean; the parent Phase 2 codenamed-bucket grep prints 0.
- Live acceptance, the operator's, after installing the merged build: Refresh all, then one card
  agrees with that account's Settings > Usage on claude.ai; `bash
  tests/acceptance/check-single-holder.sh` prints `duplicates=0` and lists the recovery directory
  (expected empty). Recorded in `tests/acceptance/README.md` by the operator, not by this chain.

### Captured assumptions

- The tee file (`<live>/rate-limit-guard/rate-limits.json`) carries `account.email` after a drain,
  so the live card's tee attribution keeps working; the tee never carries the scoped bucket and is
  rewritten by every live session, which is why the live card merges per bucket.
- Whether the endpoint's rate bucket is per token or shared per client is unknown (spike 02b never
  recorded; spike 02's own note reads "for one client identity", which leans shared). This plan
  therefore does not promise the parent's AC 4 bound ("every card within 60 s"): under a shared
  bucket a pass populates about eight cards and the rest wait for the next window. The pass is
  safe under both hypotheses (stop on the first 429, honour `Retry-After` for every read, keep the
  unread accounts first in line, skip the doomed read when the access token is already expired, and
  log reads-in-window so the operator can settle the keying from the log). Phase 4 records the AC 4
  re-scope in the parent plan as a dated scope-change note; the operator can reverse it by running
  spike 02b, which the parent still lists under 2.0.
- The token host and the usage host differ, so token refreshes do not consume the usage bucket.
- The CLI's `.oauth_refresh.lock` belongs to the live directory; a parked folder has no CLI session
  refreshing it (a login in flight is what `IsRunningAgainst` guards), so a parked write-back does
  not take that lock.
- The pass runs in a hosted `BackgroundService`, so the host's shutdown drain (30 s by default)
  waits for the account in flight; one gated unit (a 20-second-bounded POST plus the write retry
  budget) fits inside it. The pass checks the stopping token only between accounts.
- The budget window and the lockouts live in memory (parent T4: "tracked in memory per account");
  a restart forgets them. Accepted: the operator restarts the tool to install a build, not to dodge
  a lockout, and the endpoint enforces its own limit regardless.
- A recovery file is a second on-disk holder of a valid refresh token for as long as it exists;
  `check-single-holder.sh` is extended to list that directory so the invariant stays provable.

### Out-of-scope

- Ordering the cards by next window reset (#47), card layout and login age (#46, #49), editable
  alias (#48), the visual design pass (#50): each is its own chain. "Login expires in N days" from
  parent 2.6 is therefore left to #49; this plan exposes nothing about login expiry on the card.
- The Phase 3 decision-point auto-refresh and ranking (`AccountStanding`, `RoutingPolicy`).
- Running spike 02b.
- Revoking the orphan lineage a stale recovery file holds (it is moved aside, owner-only, and
  named in a warning).

## Plan

Standards grounding: no standards index exists at `docs/standards/` and no `.claude/standards.yaml`;
the repo's only rule file is the ambient `pr-body-contract.md`. The parent design's T12 (org
`testing.md`, `overlays/dotnet.md`) fixes the test-seam posture this plan follows: Core functions
under unit tests with fixture JSON and clock control, file classes against real temp directories,
HTTP adapters against a fake handler, endpoint tests through `WebApplicationFactory` with the
out-of-process ports doubled, NSubstitute only where a double needs call assertions. Scale is
Medium-to-Large (about twenty-five files across Core, App, tests, the page, and the acceptance
scripts), so the repo's own conventions in `.work/usage-cards/EXPLORE-existing-patterns.md` and
`EXPLORE-constraints.md` are the rest of the grounding: static-lambda endpoints in a mutation
group, 409 refusal tokens, gate acquisition in try/finally, `AtomicJsonFile` for every owned file,
long "why" doc comments, assertion-sentence test names over Shouldly with `TestClock`.

Build technique: tracer bullet. Phase 1 is the integration slice from engine to page; Phases 2 and
3 are separable additions the parent plan specifies; Phase 4 is the close-out.

Pre-flight consumer check for the `/api/dashboard` contract change (`quota`/`quotaNote` →
`usage`/`usageNote`/`refresh`): `grep -rn "quota\b\|quotaNote\|StatuslineQuotaView" src/ tests/
--include=*.cs --include=*.js` finds only `DashboardAssembler.cs`, `DashboardViews.cs`,
`DashboardAssemblerTests.cs`, and (the word alone) `index.html`'s tagline. `app.js` reads neither
field. Both consumers move in Phase 1.

### Phase 1: The refresh engine, the two routes, the per-bucket view, and the page [DONE]

Work items, in order. TDD: each production item below is preceded by its red test. Baseline on
`main` at `ed25bbb`: 343 tests (1 skipped); `grep -c innerHTML app.js` prints 2.

1. Core, `src/ClaudeCodeAccountRotation.Core/Quota/`:
   - `UsageSnapshot.FromStatusline(StatuslineSnapshot snapshot, AccountEmail account)`: one
     `UsageLimit` per window the tee carries a percent for (`RawKind "session"`/`Session`,
     `RawKind "weekly_all"`/`WeeklyAll`, `Group null`, `Severity null`, `IsActive true`);
     `Source = StatuslineSnapshot`; `ExtraUsage null`. Tests:
     `UsageSnapshotTests.ATeeSnapshotBecomesTwoLimitsSourcedFromTheStatusline` and
     `AWindowWithoutAPercentIsOmitted`.
   - `UsageLimit.Label`: `Session` → `5-hour`, `WeeklyAll` → `7-day`, `WeeklyScoped` →
     `ScopeDisplayName ?? "scoped"`, `Unknown` → `RawKind`. The only place a label is decided.
     `[Theory]`.
   - `RefreshOrder.Order(IReadOnlyList<RefreshCandidate>)`: pure. `RefreshCandidate(AccountEmail
     Email, DateTimeOffset? LastReadAt)`; never read first, then oldest `LastReadAt`, ties by
     ordinal e-mail. Tests: `RefreshOrderTests` (never-read first; oldest next; tie order).
   - `RefreshBudget.GapRemaining(AccountEmail)`: the time until the 60-second gap since the last
     successful read has passed, `null` when no gap applies. Test in `RefreshBudgetTests`.
   - `SwitchRefusal.TargetStrandedInRecovery` and `SwitchPlanningInput.TargetHasRecoveryFile`
     (a trailing positional parameter defaulting to `false`, so the two existing construction
     sites compile unchanged); `SwitchPlanner.Plan` refuses when it is set, before the
     login-expiry check. Test in `SwitchPlannerTests`.
2. Engine, new folder `src/ClaudeCodeAccountRotation.App/Quota/`:
   - `RefreshOutcome(RefreshOutcomeKind Kind, string Message, DateTimeOffset? RetryAt,
     DateTimeOffset RecordedAt)`: flat, the codebase's result shape. `RefreshOutcomeKind`:
     `Read`, `SessionWillRefresh`, `RateLimited`, `BudgetRefused`, `Skipped`, `NeedsLogin`,
     `Stranded`, `Lost`, `TokenRefreshFailed`, `ReadFailed`. Messages are curated constants or
     built from account e-mail and seconds only, never from a store error string (those carry
     paths).
   - `QuotaState` (singleton, one `Lock`): `Latest` per account, `LastOutcome` per account,
     `UsageLockedUntil`, `TokenLockedUntil`, `InProgress`, `LastPassSummary` (counts by kind),
     `RecoveryWarnings` keyed by folder (set by a restore that could not apply, removed when the
     file is resolved; the `DashboardState.LastReconciliation` shape, not a drained queue); and a
     `TaskCompletionSource` created by `TryStart` under the lock and completed by the worker in
     `finally`, exposed as `Task CurrentRun` for the tests to await (the `FinishedAsync`
     precedent; creating it at start closes the race between the 202 and the worker's pickup). No
     token field on any member.
   - `RefreshRequest` (`All` or `One(AccountEmail)`) and `QuotaRefreshWorker : BackgroundService`
     on a bounded `Channel<RefreshRequest>` of capacity 1 (the `StateFileWatcher` shape):
     `TryStart(request)` sets `InProgress` and writes the channel, false when a run is in flight;
     `ExecuteAsync` drains the channel and calls `QuotaRefresh.RunAsync(request, stoppingToken)`,
     clearing `InProgress` in `finally` and logging any exception rather than letting it end the
     service.
   - `QuotaRefresh.RunAsync(RefreshRequest, CancellationToken stopping)`: constructor takes
     `ICredentialPairStore`, `ProfileFolderStore`, `RosterFile`, `ClaudeStateFile`,
     `LiveDirectorySwitch` (for `RepairStaleIdentityAsync`), `Func<IUsageEndpointClient>` and
     `Func<ITokenRefreshClient>` (the typed clients are transient; a singleton must not capture
     one), `RefreshBudget`, `QuotaState`, `UsageSnapshotCache` (a no-op seam until Phase 2 makes it real),
     `CredentialMutationGate`, `ILoginSessionRunner`, `RecoveryFiles`, `TimeProvider`, `ILogger`,
     and the pacing delegate `Func<TimeSpan, CancellationToken, Task> pace` (production:
     `(d, ct) => Task.Delay(d, timeProvider, ct)` registered in `AppComposition`; tests: a recorder
     returning completed).
     - Candidate set: the live account as `RepairStaleIdentityAsync` then the state file report it
       (`Busy` or `NoProfileBlock` → the live account is `Skipped "live identity unverified"`),
       read with the live pair's access token; every parked profile with credentials whose roster
       entry is not paused; roster entries without credentials yield `NeedsLogin`. Order through
       `RefreshOrder` with `LastReadAt = Latest?.CapturedAt` of an on-demand or cached read (the
       tee does not count); a lockout, a skip, or a budget refusal never advances it.
     - Per account, in this order: `stopping` requested → stop; `UsageLockedUntil` or
       `TokenLockedUntil` in the future → `RateLimited` with that instant, no request; a stranded
       folder → try `RecoveryFiles.RestoreAsync(folder)` first (below); the live pair with
       `AccessTokenExpiresAt <= now` → `SessionWillRefresh`, no request; a parked pair whose
       `AccessTokenExpiresAt <= now` → the gated refresh unit; then `budget.TryReserve`
       (false → `BudgetRefused` with `GapRemaining ?? LockedOutFor`, no request); GET through a
       fresh client, disposing the `JsonDocument`; 401 on a parked pair → `budget.RecordUnauthorized`
       (the refund that lets the retry ride the original reservation), the gated unit, then one
       retry GET through `TryReserve` again so a second 401 is accounted the way the budget's own
       rule says; 401 on the live pair →
       `SessionWillRefresh`; 429 → `budget.RecordLockout`, `UsageLockedUntil = now + (RetryAfter is
       null or zero ? 300 s : RetryAfter)`, this and every later candidate `RateLimited`, the pass
       ends; 200 → `UsageResponseParser`, `UsageSnapshot` with `Source = OnDemandRefresh` into
       `QuotaState.Latest`, `UsageSnapshotCache.SaveAsync` for that account; parse failure →
       `ReadFailed`; transport → `ReadFailed` with the status only.
     - The gated refresh unit, under `CancellationToken.None` throughout: `gate.AcquireAsync(2 s,
       CancellationToken.None)` (the poll's identity repair takes the gate with a zero wait on every
       dashboard read, so a zero wait here would skip accounts at random), `catch (TimeoutException)`
       → `Skipped "another credential change is in progress"`; under the gate: refuse when
       `IsRunningAgainst(folder)` (`Skipped "a login is in progress"`); re-read the parked pair,
       and a missing pair or a fingerprint different from the pre-gate read → `Skipped "the pair
       changed underneath the refresh"`, no POST; POST through a fresh client; `RateLimited` from
       the token host → `TokenLockedUntil` (same floor), the pass ends; `Transport`/`MalformedBody`
       → `TokenRefreshFailed`; success → build the new pair by copying `Raw` and replacing
       `accessToken`, `refreshToken`, `expiresAt` (epoch ms), and `refreshTokenExpiresAt` (epoch
       ms when `LoginExpiresAt` is present, the existing value kept when it is not);
       `WriteParkedAsync(folder, pair, expectedFingerprint)` inside a catch of `IOException`,
       `UnauthorizedAccessException`, and `InvalidDataException`; a `Result` failure of the
       fingerprint kind is deterministic and strands at once; an exception or a transient failure
       retries through the pacer at 250 ms, 500 ms, 1 s (jittered ±20 %), three attempts; on final
       failure `RecoveryFiles.WriteAsync(folder, expectedFingerprint, pair)` → `Stranded`, and a
       failure there → `Lost` (logged at Error with the folder name and both 12-hex fingerprints,
       never a token). The gate is released in `finally`. The audit line on success carries the
       folder name and the old and new 12-hex fingerprints only.
     - Spacing: `pace(1 s, stopping)` before every candidate after the first that sends a request.
     - `RunAsync` never throws past the worker: per-account exceptions become `ReadFailed` with a
       fixed message and an Error log line.
   - `RecoveryFiles`: `Directory.CreateDirectory` first (the `RosterFile` convention), then
     `WriteAsync(folder, expectedFingerprint, pair)` to `<appdata>/recovery/<folder-name>.credentials.json`
     through `AtomicJsonFile` (owner-only) with the envelope `{ folder, expectedFingerprint, pair }`;
     `HasRecoveryFor(folder)`; `RestoreAsync(folder)` and `RestoreAllAsync()`: per file, inside its
     own try/catch, `WriteParkedAsync(folder, pair, expectedFingerprint)`; success deletes the file;
     a fingerprint mismatch moves it to `<appdata>/recovery/stale/` and records a warning; an absent
     parked pair or an exception keeps the file and records a warning naming the account and "log
     in again"; a malformed envelope is moved to `stale/` and warned. Warnings go to
     `QuotaState.RecoveryWarnings` keyed by folder and are cleared when that folder's file is
     resolved. `RestoreAllAsync` is called from `StartupReconciliation.StartAsync` after
     `ReconcileAsync`, wrapped so nothing it does can fail startup.
   - `AppComposition` sets `HostOptions.ShutdownTimeout` explicitly, with a comment naming the
     bound it covers: one gated unit in the worst case (2 s gate wait, a 20 s POST, three write
     attempts each wrapping `AtomicBytesFile`'s 2 s transient retry, 1.75 s of pacer) is about
     30 s, so the timeout is 45 s. A kill mid-write leaves a credential-shaped temp the startup
     sweep quarantines rather than deletes, so the pair is recoverable by hand, not lost.
   - `LiveDirectorySwitch`: `SwitchPlanningInput.TargetHasRecoveryFile =
     recovery.HasRecoveryFor(targetFolder)` computed under the gate; `SwitchEndpoints.Describe`
     gains the refusal's message.
   - `AppComposition`: register `RefreshBudget` (defaults), `QuotaState`, `RecoveryFiles`,
     `QuotaRefresh`, the pacer, the two client factories, `QuotaRefreshWorker` as a hosted service;
     the two adapter factories resolve `TimeProvider` from the provider instead of `TimeProvider.System`.
3. Views and assembler, `src/ClaudeCodeAccountRotation.App/Dashboard/`:
   - Replace `StatuslineQuotaView` with `UsageView(string? Source, DateTimeOffset? CapturedAt,
     IReadOnlyList<UsageLimitView> Limits, UsageCreditsView? Credits)`,
     `UsageLimitView(string Kind, string Label, double? Percent, DateTimeOffset? ResetsAt, string?
     Severity, string? Source, DateTimeOffset? CapturedAt, bool Known, bool WindowReset)`,
     `UsageCreditsView(bool Enabled, string? DisabledReason, bool SpendLimitReached)`, and
     `RefreshStateView(string State, string? Message, DateTimeOffset? RetryAt)`. `State` is the
     outcome kind in kebab-case (`idle` when there is none): `idle`, `read`,
     `session-will-refresh`, `rate-limited`, `budget-refused`, `skipped`, `needs-login`,
     `stranded`, `lost`, `token-refresh-failed`, `read-failed`. `Source` is
     `snapshot|refresh|cached`. `AccountCardView` gains `UsageView Usage` (always present),
     `string? UsageNote`, `RefreshStateView Refresh` (default idle); `Quota`/`QuotaNote` go. The
     three positional call sites in `RosterEndpoints` (create, PATCH, adopt-live) are updated.
     `Limits` always lists the `5-hour` and `7-day` rows, then every `WeeklyScoped` row, then every
     `Unknown` row; a row the sources lack is `Known false`; a row whose `ResetsAt` is earlier than
     the dashboard's `CapturedAt` is `WindowReset true` and `Percent null`. When no source has been
     read the scoped row is still emitted, labelled `Fable` when the account's latest read carried
     one and `scoped` otherwise.
   - `DashboardAssembler`: the live card's tee snapshot (attributed exactly as today, note strings
     unchanged) becomes `UsageSnapshot.FromStatusline`; every card merges per bucket across the tee
     snapshot (live only) and `QuotaState.Latest`: each row comes from the source with the later
     `CapturedAt` among those that carry that kind; the card-level `Source`/`CapturedAt` is the
     newest of the sources used, and a row whose source differs carries its own. `Refresh` comes
     from `QuotaState.LastOutcome`, `stranded` when `RecoveryFiles.HasRecoveryFor(folder)`.
     `DashboardView` gains `RefreshView Refresh(bool InProgress, DateTimeOffset? LockedUntil,
     string? Summary)`; `QuotaState.RecoveryWarnings` are appended to `Warnings` on every read
     for as long as they stand.
   - `DashboardAssemblerTests`: the eight existing facts move to `usage.limits[0].percent`; new:
     never-read parked card has three `known false` rows, `usage.source null`, `refresh.state idle`;
     the live card keeps its Fable row from an older on-demand read when a newer tee write lacks
     it; a row whose window reset since capture is unknown; a stranded folder renders `stranded`
     and its payload contains no `refresh-`, `access-`, or path separator; a running pass renders
     `refresh.inProgress true`.
4. Endpoints, `src/ClaudeCodeAccountRotation.App/Endpoints/RefreshEndpoints.cs`, mapped from
   `AppComposition.MapRoutes` in a `SameOriginMutationFilter` group: `POST /api/refresh` → 202
   `{ started: true }`, 409 `RefreshInProgress` ("A refresh is already running") when
   `TryStart` is false, 409 `RateLimited` (message with the seconds) under either lockout;
   `POST /api/accounts/{email}/refresh` → 400 bad e-mail, 404 when it is neither the live account
   nor a parked profile nor a roster entry, 409 as above, 202.
5. Test harness, `tests/ClaudeCodeAccountRotation.App.Tests/`:
   - `RecordingHandler` gains `Enqueue(HttpResponseMessage)`; `AppFactory` gains `Outbound` (one
     shared `RecordingHandler`, empty by default so an unexpected outbound call throws) wired through
     `ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => Outbound))` with a
     capturing lambda so both named clients share one queue; `TestClock` replaces `TimeProvider`
     in `ConfigureTestServices`; a `Pacer` recorder replaces the pacing delegate.
   - Every new test derives `expiresAt`/`refreshTokenExpiresAt` from the test clock; fake token
     responses mint `refresh-…`/`access-…` prefixed tokens so the secrets assertions bite.
6. Tests:
   - `App.Tests/Quota/QuotaRefreshTests` over a temp profiles root with the real
     `FileSystemCredentialPairStore`, NSubstitute fakes for the two ports (returned from the
     factories), `TestClock`, a recording pacer, the real `RefreshBudget` and `QuotaState`, and a
     `LiveDirectorySwitch` over the temp root: `ParkedPairUnauthorizedRefreshesOnceThenRetriesOnce`
     (valid `expiresAt`, scripted 401 then 200: one POST, two GETs, new `refreshTokenExpiresAt ==
     clock + refresh_token_expires_in`, untouched sibling keys),
     `AnExpiredParkedAccessTokenSkipsTheDoomedRead` (one POST, one GET),
     `AResponseWithoutRefreshExpiryKeepsTheOldValue`, `LivePairIsNeverRefreshed`,
     `AMissingPairUnderTheGateSendsNoTokenPost` (file deleted between the two reads),
     `APairThatChangedUnderTheGateIsNotRewritten`, `ALoginInFlightBlocksTheWriteBack`,
     `AWriteBackExceptionRetriesThenStrands` (a decorating store that throws `IOException` on every
     write: three pacer waits, recovery file with the new fingerprint, `Stranded`; the same test with
     a `Result` fingerprint failure strands without a wait), `AStrandedFolderIsRestoredOnDemand`
     (restore succeeds, file gone, then a normal read), `AStaleRecoveryFileIsMovedAside`,
     `ARecoveryWriteFailureReportsLost`, `AUsageRateLimitEndsThePassAndLocksEveryLaterRead`,
     `ATokenRateLimitEndsThePass`, `AMissingRetryAfterLocksForFiveMinutes`,
     `ThreePassesWithARateLimitAtNineReadEveryAccount`, `TheBudgetRefusesASecondReadInsideTheGap`
     (with the reservation not spent by a skipped unit), `ThePassPacesOneSecondBetweenReads`,
     `AStoppingTokenEndsThePassBetweenAccounts`, `TheLiveAccountIsSkippedWhenItsIdentityIsBusy`,
     and `NoLogLineOrOutcomeCarriesAToken`.
   - `Core.Tests`: `RefreshOrderTests`, `UsageSnapshotTests`, the `UsageLimit.Label` theory, the
     `RefreshBudget.GapRemaining` fact, the `SwitchPlanner` stranded refusal.
   - `App.Tests/Endpoints/RefreshEndpointTests` over `AppFactory`: the guard trio for each route
     (no header → 403 and zero requests; cross-site Origin → 403; Origin measured against the
     configured listen address), a happy pass over one parked account using the spike-01 fixture
     body (202, await `QuotaState.CurrentRun`, then `/api/dashboard` shows three known rows and
     `via refresh`), the overlap 409, the lockout 409, and `SwitchEndpointTests.ASwitchToAStrandedFolderIsRefused`.
   - `App.Tests/Hosting/StartupReconciliationTests` (or the existing startup test file): a
     malformed recovery file does not stop the app and surfaces as a warning.
7. Page, `src/ClaudeCodeAccountRotation.App/wwwroot/`:
   - `index.html`: a `Refresh all` button in the header next to `#captured`, and a
     `#refresh-state` line (in progress, lockout, last pass summary).
   - `app.js`: a `usage(account)` section per card built with `element()` (no `innerHTML`): one
     `.limit` row per `usage.limits` entry with the label, a `.bar > .fill` (width from percent,
     `data-severity`), the percent text or `unknown` (or `window reset since last read`), and
     `resets <relative time>`; an `.asof` line `as of <local time> via <source>` when
     `usage.source` is set, and a per-row "as of" when a row's source differs; the credits line
     when present; `account.usageNote` when present; a `.refresh-state` line from `account.refresh`
     (`rate limited, retry in N s` clamped at zero, `read N s ago`, `session will refresh`,
     `credentials stranded in recovery`, `credentials lost; log in again`, `refreshing…` while
     `dashboard.refresh.inProgress` and the state is not yet from this pass, or the message); a
     per-card `Refresh` button; `Switch` and `Refresh` disabled while `dashboard.refresh.inProgress`
     or `busy`, and `Switch` disabled when `refresh.state === "stranded"`. Both buttons go through
     `mutate()`. Relative times are computed from `dashboard.capturedAt`.
   - `app.css`: `.limit`, `.bar`, `.fill`, severity colours from the existing `--warn`/`--error`
     tokens, `.asof` in `--muted`.
8. `tests/acceptance/check-single-holder.sh`: also scans `<appdata>/recovery/` (and `stale/`),
   listing any file found under a separate `recovery=` line; `tests/acceptance/README.md` gains
   the Refresh all step and the expected `recovery=0`.
9. `CHANGELOG.md` under `[Unreleased]`: an `### Added` entry for the usage rows, the source and
   capture line, Refresh all and per-card refresh, the two routes, the recovery file, and the
   stranded-folder switch refusal (#52); a `### Changed` entry for the dashboard payload (`quota`
   replaced by `usage` and `refresh`).

**Sanity Check:**

- `dotnet test -c Release` exit 0, failed 0, total ≥ 343 + 30.
- `ls src/ClaudeCodeAccountRotation.App/Quota/` lists `QuotaRefresh.cs`, `QuotaRefreshWorker.cs`, `QuotaState.cs`, `RecoveryFiles.cs`, `RefreshOutcome.cs`.
- `grep -c "MapPost(\"/refresh\"" src/ClaudeCodeAccountRotation.App/Endpoints/RefreshEndpoints.cs` ≥ 1, `grep -c "MapPost(\"/accounts/{email}/refresh\"" src/ClaudeCodeAccountRotation.App/Endpoints/RefreshEndpoints.cs` ≥ 1, `grep -c "AddEndpointFilter<SameOriginMutationFilter>" src/ClaudeCodeAccountRotation.App/Endpoints/RefreshEndpoints.cs` ≥ 1.
- `grep -rn "StatuslineQuotaView" src/ tests/ | wc -l` prints `0`.
- `grep -rn "seven_day_opus\|seven_day_sonnet\|tangelo\|nimbus_quill\|cinder_cove" src/ | wc -l` prints `0`.
- `grep -c "IsRunningAgainst" src/ClaudeCodeAccountRotation.App/Quota/QuotaRefresh.cs` ≥ 1; `grep -c "CancellationToken.None" src/ClaudeCodeAccountRotation.App/Quota/QuotaRefresh.cs` ≥ 1; `grep -c "TargetStrandedInRecovery" src/ClaudeCodeAccountRotation.Core/Switching/SwitchPlanner.cs` ≥ 1.
- `grep -c "account.usage\|account.refresh" src/ClaudeCodeAccountRotation.App/wwwroot/app.js` ≥ 2; `grep -c "innerHTML" src/ClaudeCodeAccountRotation.App/wwwroot/app.js` prints `2`.
- `grep -c "recovery" tests/acceptance/check-single-holder.sh` ≥ 1; `shellcheck tests/acceptance/*.sh` clean.

**Done (2026-09-12):** four sub-briefs, nine commits `f5e8288` to `60f7e1c`; `dotnet test -c
Release` went from 343 to 399 (1 skipped) with build, format, shellcheck, markdownlint, typos,
and the machine-path check clean. A fresh-context phase verifier passed every sanity check and
acceptance criterion and killed 15 of 15 mutations against the named tests. Untested by design:
the 2-second gate wait and its timeout branch, the window-cap refusal sentence, and the page
(no JavaScript harness; verified by reading and by a throwaway instance's payload). The security
and code review lanes ran on the whole branch after Phase 3; their findings and the fix pass are
recorded under Phase 4. Deviations from the plan text are in `DEVIATIONS.md`.

### Phase 2: The snapshot cache file [DONE]

1. `App/Quota/UsageSnapshotCache`: `<appdata>/state/usage-cache.json` through `AtomicJsonFile`;
   one entry per account with `capturedAt`, `source`, the limits (kind, raw kind, group, percent,
   severity, reset, scope name, active) and credits; no token field. `LoadAsync` at start fills
   `QuotaState.Latest` with `Source = Cached`, keeping the original `CapturedAt`; a missing, torn,
   or unparsable file loads nothing (the tee reader's tolerance pattern). `SaveAsync(account,
   snapshot)` after every successful read (Phase 1 left the call site). A cached row whose window
   reset since capture renders unknown through the Phase 1 rule, which is what keeps "via cached"
   from inviting stale-data confusion (parent T4's switch condition, evaluated: kept).
2. Tests: `UsageSnapshotCacheTests` (round trip; torn file loads nothing; a pass that wrote back a
   pair leaves no `refresh-`/`access-` text in the file).
   `DashboardAssemblerTests.ACachedSnapshotRendersAsCachedWithItsOriginalCaptureTime`.
3. `CHANGELOG.md`: extend the Phase 1 entry with the restart behaviour.

**Sanity Check:**

- `dotnet test -c Release` exit 0; `grep -c "Cached" src/ClaudeCodeAccountRotation.App/Quota/UsageSnapshotCache.cs` ≥ 1.
- `grep -rn "usage-cache.json" src/ClaudeCodeAccountRotation.App/ | wc -l` ≥ 1 and the path is composed from `AppDataDirectory`, never a literal root (`bash eng/check-no-machine-paths.sh` clean).

**Done (2026-09-12):** `5c2a11b`, 399 → 407 tests. Verified with Phase 3 by one fresh-context
verifier (12 of 12 criteria, every mutation killed by its named test). The merge mutex is
verified by reading only; no test drives two concurrent saves.

### Phase 3: Paused accounts near login expiry [DONE]

1. In `QuotaRefresh.RunAsync` for an `All` request, a paused parked pair whose `LoginExpiresAt` is
   within 7 days of now is included with the gated refresh unit only (one token POST, write-back,
   no usage read), so a paused pair's 28-day login never lapses silently; one further out, or with
   no `LoginExpiresAt`, is `Skipped "paused"`.
2. Tests: `QuotaRefreshTests.PausedPairNearLoginExpiryIsRefreshed` (5 days out → one POST, zero
   GETs; 20 days out → zero POSTs).
3. `CHANGELOG.md`: one sentence in the Phase 1 entry.

**Sanity Check:**

- `dotnet test -c Release` exit 0; `grep -c "PausedPairNearLoginExpiryIsRefreshed" tests/ClaudeCodeAccountRotation.App.Tests/Quota/QuotaRefreshTests.cs` prints `1`.

**Done (2026-09-12):** `c5caeeb`, 407 → 410 tests. Renewals run first in the pass, outside
`RefreshOrder`, so a 429 earlier in the pass cannot starve them; the verifier confirmed the
placement by reading and the window and single-account guards by mutation.

### Phase 4: Close-out [DONE]

1. Parent plan `docs/topics/claude-subscription-rotation/PLAN.md`: tick 2.1 to 2.4 with a dated
   note naming PR #15; tick 2.5 and 2.6 with a dated note naming this PR and what moved out (login
   expiry text to #49, spike 02b still open under 2.0); in the Phase 2 Sanity Check block, a dated
   scope-change note that the spike-02b line stays open, that the live-acceptance line's "login
   expires in N days" moved to #49, and that AC 4's 60-second bound is not promised until spike 02b
   answers per-token (the pass populates what the window allows and reports the rest); amend the
   Phase 3 sanity line that says "the one-second spacing lives in `RefreshBudget` pacing" to name
   the injected pacer with a dated note.
2. Gates: `dotnet build -c Release` (0 warnings), `dotnet test -c Release`, `dotnet format
   whitespace --verify-no-changes`, `typos .`, markdownlint on the changed markdown, `shellcheck
   eng/*.sh tests/acceptance/*.sh`, `bash eng/check-no-machine-paths.sh`, `git status --short --
   '**/packages.lock.json'` empty.
3. Fresh-context `implementation:phase-verifier` on the diff; the code review and security review
   lanes, pointed at the cancellation boundary, the strand-and-restore path, and the recovery
   directory's place in the single-holder invariant.
4. This topic's phase tags to `[DONE]`, PR through `/source-control:pull-request`, squash-merge on
   green.

**Sanity Check:**

- `grep -c "\[x\] \*\*2\.[1-6]\*\*" docs/topics/claude-subscription-rotation/PLAN.md` prints `6`.
- `grep -c "scope-change" docs/topics/claude-subscription-rotation/PLAN.md` ≥ 2 (Phase 2 sanity and Phase 3 sanity notes).
- `grep -c "\[DONE\]" docs/topics/usage-cards/PLAN.md` prints `4` before the PR is merged.
- `gh pr view --json state` prints `MERGED`.

**Done (2026-09-12):** the parent plan carries the six ticks and three dated scope-change notes.
The security lane (no P1/P2; three P3: the per-turn restore ran outside the gate, removing a
stranded account left a valid lineage in `recovery/`, a `JsonException` after the token POST could
drop a rotated pair; three P4; one P5) and the code lane (no blockers; five should-fix, eleven
nits) ran on the whole branch; every finding was fixed in `c8218e2`, `2efd3ab`, `3abef18`, and a
fresh-context verifier of that fix pass found one unguarded path (the post-POST catch-all), closed
by a further test. 343 → 427 tests plus that test. The PR, the squash-merge, and the install of the
merged build on the operator's machine are the last items.

## Blast radius

HIGH. About twenty-five files across Core, App, tests, the page, and the acceptance scripts; the
production change rewrites parked credential files (the security-sensitive surface), adds a
credential-bearing recovery location, adds a switch refusal, replaces a payload shape the page and
the assembler tests depend on, adds a hosted background worker and two mutating routes. `git
revert` undoes it cleanly and nothing is persisted outside the tool's own app data besides the
parked pair rewrite, which is the operation's purpose. The security-sensitive trigger matches: the
formal stress-test ran.

## Stress-test summary

Two fresh-context reviews ran on the first draft: a plan reviewer (4 critical, 14 important, 10
suggestions) and `/planning:devils-advocate` (3 critical, 12 high, 10 medium, 6 low). Every finding
was checked against the engine's inputs, the store, the page, and the tests before it changed the
plan:

- Confirmed and applied, structural: the pass moved out of the HTTP request into a hosted
  `BackgroundService` (the request's abort mid-POST would strand a rotated token, the 30 s
  shutdown drain was shorter than a pass, the 10-second poll rebuilds every card with enabled
  buttons, and the poll's zero-wait gate take would skip accounts at random); the token POST and
  the write-back run under `CancellationToken.None` bounded by the adapter timeout; the refresh
  unit waits 2 s for the gate; the server-side switch refusal for a stranded folder moved from
  follow-up into Phase 1 (a page-only disable would let a switch move the dead pair to live, after
  which no restore can apply); a missing pair under the gate is a refusal before any POST.
- Confirmed and applied, correctness: write-back failures arrive as exceptions as well as results
  (only fingerprint failures are deterministic); `Retry-After` can be null or zero (300 s floor);
  ordering by outcome time starved the tail under a shared bucket (now by last successful read,
  with a three-pass coverage test); the reservation was spent by a skipped unit (now taken only
  before a request); the typed clients are transient so a singleton must resolve them per use; the
  restore at startup could fail the host (now guarded per file); the recovery write itself can fail
  (`Lost`); `refreshTokenExpiresAt` must survive a response that omits the field; whole-snapshot
  precedence blanked the live card's Fable row on the next tee write (now merged per bucket); the
  harness the tests needed (settable outbound handler, enqueueable script, DI `TimeProvider`)
  did not exist and is now a work item; the read-only-file fault injection was Windows-only (now a
  throwing store decorator); a stranded state had no exit (restore on demand, stale files moved
  aside); the refusal token `MutationInProgress` carried the wrong message (`RefreshInProgress`);
  three positional `AccountCardView` call sites; the recovery directory sat outside the
  single-holder check (script extended); curated card messages so a store error's path never
  reaches the page; countdown clamped at zero; expiries derived from the test clock.
- Confirmed and recorded, not fixed: the parent's AC 4 bound is not promised while the bucket
  keying is unknown (Captured assumptions; Phase 4 records the scope change in the parent); the
  budget window and lockouts are in-memory and a restart forgets them; the orphan lineage in a
  stale recovery file is not revoked; the no-timer grep on `App/Quota/` is dropped from this plan's
  sanity list because the injected pacer's real justification is `TestClock`, not the grep.
- Checked and declined: persisting the lockout across restarts (T4 says in memory, and the endpoint
  enforces its own limit); rendering a single header line instead of per-card budget refusals (the
  pass summary line covers it); running spike 02b (needs real tokens under the protected app data).

## Execution shape

Four phases, fully sequential: Phase 2 and Phase 3 edit `QuotaRefresh` and its tests, which Phase 1
creates, and Phase 4 reads the result of all three. No parallel wave.

| Phase | Surface | Basis |
|---|---|---|
| 1 | four sequential implementer sub-briefs, each committed at green, main session verifies | one tracer bullet is too large for one worker's turn budget; the sub-briefs are (a) harness and Core additions, (b) engine, worker, recovery, and DI, (c) views, assembler, routes, and their tests, (d) page, acceptance script, and CHANGELOG |
| 2 | sub-agent | bounded, file-disjoint from the page |
| 3 | sub-agent | bounded |
| 4 | main session | judgment and outward actions |

## Open questions

- None blocking. The rate-bucket keying stays unknown by design (Captured assumptions).

## Handoff to implementation

### User-approval gates

- The session's opening instruction pre-authorized plan → implement → PR → squash-merge on green
  for #52, so no approval round blocks execution; every tagged decision below is surfaced in the PR
  body and in the handoff for the operator to override afterwards.
- `[FALLBACK — confirm or override]` The parent's AC 4 bound ("every card within 60 s") is not
  promised until spike 02b answers per-token; a pass populates what the window allows and the rest
  wait, first in line. The operator can reverse this by running spike 02b.
- `[FALLBACK — confirm or override]` A 429 on either host ends the pass and locks every read until
  `Retry-After` (300 s when absent or zero) expires.
- `[FALLBACK — confirm or override]` A stale recovery file (its folder rewritten since) is moved to
  `recovery/stale/` and named in a warning; the orphan lineage is not revoked.
- `[FALLBACK — confirm or override]` A pair missing or changed under the gate, or a folder with a
  login in flight, is skipped for that pass with a named reason rather than retried.

### Execution shape ([EXEC-SHAPE] tagged)

- `[EXEC-SHAPE]` The pass is a hosted `BackgroundService` started by the route (202) and observed
  through the dashboard poll, rather than a synchronous request: the request's abort and the
  shutdown drain were both shorter than a pass.
- `[EXEC-SHAPE]` The token POST and the write-back are one gated unit under
  `CancellationToken.None`; usage reads run outside the gate. The gate's own doc scopes it to the
  write-back, and a switch between the POST and the write-back would strand the rotated pair.
- `[EXEC-SHAPE]` A parked pair whose access token is already expired skips the doomed usage read
  and refreshes first; the 401 path stays for a nominally valid token the endpoint rejects.
- `[EXEC-SHAPE]` Pacing is an injected delay delegate rather than `Task.Delay` in the engine:
  `TestClock` overrides only `GetUtcNow`, so a `TimeProvider`-driven `Task.Delay` would sleep in
  tests.
- `[EXEC-SHAPE]` Passes order by last successful read so successive passes rotate coverage if the
  bucket is shared.
- `[EXEC-SHAPE]` Refusals are 409 with a refusal token (`RateLimited`, `RefreshInProgress`), never
  429; the page has no 429 handling and every refusal today is a 409.
- `[EXEC-SHAPE]` `StatuslineQuotaView` is replaced, not extended; the tee becomes a
  `UsageSnapshot` and every card merges per bucket. Enum-shaped fields are strings on the wire
  because the app configures no HTTP enum converter. `Usage` is always present so the three-row
  invariant lives on the server.
- `[EXEC-SHAPE]` The snapshot cache file (Phase 2) and the paused-near-expiry refresh (Phase 3) are
  in scope as separable phases: the parent plan specifies both under 2.5, and "via cached" is
  meaningless without the file; the window-reset rule is what keeps a cached figure honest.
- `[EXEC-SHAPE]` The stranded-folder switch refusal is a Core `SwitchRefusal` member and a planner
  input, the shape every other refusal takes.
- `[EXEC-SHAPE]` Sequential phases, one implementer per phase, tests in `App.Tests/Quota/` and
  `App.Tests/Endpoints/` beside the existing patterns.

### Mechanical work

- Commit boundaries: `docs(plan)` for this topic first; one `feat` commit per Phase 1 sub-brief
  and per later phase (Phase 4's parent-plan edit rides the last one); the PR squash-merges, so
  the intermediate commits cost nothing and a worker lost mid-brief costs one brief.
- Verification checkpoints: each phase's sanity list; the Phase 4 gate list; a fresh-context phase
  verifier and the code and security review lanes before the PR.
- Sequential; nothing to fall back from.
