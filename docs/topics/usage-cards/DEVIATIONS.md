# Deviations: usage-cards

Append-only log of where implementation departed from `PLAN.md`, one decision per entry, each
with its evidence. Types: plan-confirmed, discovery, deviation (plan said / found / chose /
revisit), human-decision.

## Phase 1, sub-brief (a): harness and Core additions

- discovery: `SwitchEndpointTests.AnExpiredParkedLoginYieldsAConflict` built its expired login from
  the wall clock, so registering the test clock as the DI `TimeProvider` made it pass a switch it
  should refuse. Fixed by deriving the instant from the factory clock (`d0f6171`). Every later test
  derives host-visible instants from the clock; `CredentialFiles.Shape` defaults still come from
  the wall clock and read as "not expired" under the frozen host.
- discovery: `FileSystemCredentialPairStore` stays on its explicit `TimeProvider.System`; its
  refresh lock polls `Task.Delay` against `GetUtcNow` and never terminates under a frozen clock.
- plan-confirmed: `SwitchPlanningInput` has one construction site, not two; the defaulted trailing
  parameter compiles unchanged (`f5e8288`).

## Phase 1, sub-brief (b): engine, worker, recovery, DI

- deviation: plan said NSubstitute fakes for the two ports / found NSubstitute is pinned centrally
  but referenced by no test project, so adding it needs a project-file edit and regenerated lock
  files / chose hand-rolled scriptable doubles in
  `tests/ClaudeCodeAccountRotation.App.Tests/Quota/RefreshDoubles.cs` (`448d927`) / revisit: none.
- deviation: plan said `RecoveryFiles` takes the store and the app-data path / found a restore
  warning would otherwise have to name a folder path / chose to inject `ProfileFolderStore` so the
  warning names the account from the folder's identity file (`9359279`) / revisit: none.
- deviation: plan said `ReadFailed` "with the status only" / found the usage adapter's transport
  detail interpolates the exception message, which can carry a host / chose curated constants on
  the card and the detail on the log line only (`9359279`) / revisit: none.
- discovery: two cases the plan did not name. A token-host 401 maps to `TokenRefreshFailed`; a
  second 401 after a successful rotation maps to `ReadFailed` with a fixed "log this account in
  again" message and no further retry (`QuotaRefreshTests`, `448d927`).
- deviation: plan said the adapter factories resolve `TimeProvider` from the provider / found
  `OutboundClientLoggingTests` builds a container from `AddOutboundClients` alone and had no
  `TimeProvider` / chose `services.TryAddSingleton(TimeProvider.System)` inside the helper; the
  composition root's registration and the test factory's replacement still win (`9359279`) /
  revisit: none.
- deviation: plan said one commit per green checkpoint (scaffold, engine, recovery, DI) / found
  the pieces only compile together / chose two commits, engine then tests (`9359279`, `448d927`)
  / revisit: none.
- deviation: plan said TDD one test at a time / found the worker wrote the engine first and the
  tests after, iterating to green / chose to substitute a mutation spot-check on four load-bearing
  facts (restore, lockout floor, under-gate fingerprint compare, spacing), each turning its named
  test red when broken / revisit: the phase verifier and the review lanes check the tests for
  vacuity rather than trusting the red step.
- deviation: plan said `BudgetRefused` reports "read N s ago" from `GapRemaining` / found
  `RefreshBudget` exposes no last-read instant / chose to derive it from the account's latest
  snapshot capture time, and a window-cap refusal (no gap) reports "the read budget for this
  account is spent for now" (`9359279`) / revisit: none.
- discovery: a 401 on the live pair took a budget reservation and never refunded it, so the live
  account's next read waited the 60-second gap for a read that never happened. Sub-brief (c)
  refunds it through `RecordUnauthorized`, the same accounting the parked path uses; the plan's
  per-account sequence named the refund only for the parked case.

## Phase 1, sub-brief (c): views, assembler, routes

- deviation: plan said register the worker as a hosted service / found `AddHostedService<T>` alone
  leaves the worker unresolvable for the route's `TryStart` / chose one singleton plus a hosted
  registration over that instance (`007e75e`) / revisit: none.
- deviation: plan said `AccountCardView.Refresh` defaults to idle / found a record parameter
  default must be a compile-time constant / chose a required positional with both roster call
  sites passing `RefreshStateView.Idle` (`007e75e`) / revisit: none.
- plan-confirmed: `RosterEndpoints` constructs a card at two sites, not three; PATCH returns a
  bare roster view.
- deviation: plan said credits come from the latest snapshot / found the tee never carries
  credits, so the literal rule would blank the line on every statusline write / chose the newest
  source that carries `ExtraUsage`, the same rule as the per-bucket merge (`007e75e`) / revisit:
  none.
- deviation: plan said the endpoint test uses the spike-01 fixture file / found adding a content
  include means a project-file edit outside the fence / chose the fixture's `limits[]` and
  `extra_usage` blocks copied verbatim as a constant in `RefreshEndpointTests` (`007e75e`) /
  revisit: none.
- deviation: plan said TDD one test at a time / found the view-shape change breaks compilation
  repo-wide so no test could run red against the old shape / chose implement-then-test with six
  mutation spot-checks, each killing only its named tests (live-401 refund, per-bucket precedence,
  window-reset rule, stranded override, lockout refusal, same-origin filter) / revisit: the phase
  verifier and review lanes check for vacuity.
- discovery: the overlap 409 is driven by `QuotaState.TryBeginRun()` directly, the exact predicate
  `TryStart` refuses on, because a started pass finishes before a second request can arrive.
- discovery: one same-origin theory started a real pass with nothing scripted; the unscripted
  read threw inside the handler and the engine's per-account catch recorded a read failure, so
  the theory passed while spending the no-network guarantee. Fixed by scripting the read and
  asserting one outbound request (`48515fd`). Worth knowing: the engine's catch-all means a
  test's harness error surfaces as `ReadFailed`, not as a test failure.

## Phase 1, sub-brief (d): page, acceptance script, changelog

- discovery: `check-single-holder.sh` reported SC2310 at baseline too (the rule is enabled in
  `.shellcheckrc`; CI runs shellcheck on `eng/*.sh` only). A per-line disable with its reason now
  sits on the intended `set -e` suppression (`60f7e1c`).
- plan-confirmed: no statusline-tee config key exists; the tee path derives from the live
  directory, so a throwaway instance's tee follows its temp live directory.
- deviation: plan said "refreshing…" for a card whose outcome is not from this pass / found the
  payload carries no per-outcome timestamp / chose to show it only while a pass runs and the
  card's state is `idle`, so during a single-card refresh every other never-read card also reads
  "refreshing..." (`84863cb`) / revisit: expose the outcome's `recordedAt` on the wire if that
  reads wrong in use.
- discovery: the script's default app-data branch was exercised read-only on this machine
  (`recovery=0`); nothing under the real app data was written.

## Phase 2: the snapshot cache file

- deviation: plan said the cache is a plain class / found the analyzers demand `IDisposable`
  over the merge semaphore and refuse an undisposed inline construction in the test harness /
  chose `IDisposable` on the cache and an owned `Cache` member in `RefreshHarness` (`5c2a11b`) /
  revisit: none.
- deviation: plan said `SaveAsync` after every successful read / found a cancelled stopping token
  threw out of the save and left an already-successful read with no recorded outcome / chose to
  run the save to completion under `CancellationToken.None`, the gated write-back's precedent,
  pinned by `ASaveRunsToCompletionUnderAPassTokenThatHasBeenCancelled` (`5c2a11b`) / revisit:
  none.
- deviation: plan said an unparsable e-mail, instant, or out-of-range percent drops the entry /
  found a partially readable limit row is indistinguishable from a torn one / chose "loaded whole
  or not at all", stricter than the tee reader, stated in the test's doc comment (`5c2a11b`) /
  revisit: none.
- deviation: plan said TDD one test at a time / found the no-op stub could not compile under the
  analyzer posture / chose red-first for the round trip and the startup fact, tests-after with
  four mutations for the rest (`5c2a11b`) / revisit: the phase verifier checks for vacuity.

## Phase 3: paused accounts near login expiry

- deviation: plan said paused renewals join the pass / found that appended last they would be
  unreachable on a ten-account roster under a shared bucket, since a 429 at read nine marks
  every later candidate without sending / chose to run renewals first, outside `RefreshOrder`,
  never advancing the ordering key (`c5caeeb`) / revisit: none.
- discovery: a renewal shares the turn's prologue, so a paused folder that is stranded reports
  `Stranded` (and the restore runs), and a paused pair whose file vanished reports `NeedsLogin`;
  neither sends a request.
- deviation: plan said TDD one test at a time / found the two boundary facts pass before the
  change as well as after / chose mutation checks for them (window widened to 30 days; the
  single-account guard dropped), each failing its named fact (`c5caeeb`) / revisit: none.
- discovery: `RefreshRequest.cs`'s doc comment still says "is not paused"; corrected in the Phase 4
  close-out.

## Phase 4: review lanes

- discovery: the sub-brief (b) entry above claimed the token-host 401 and the second-401 cases were
  pinned by tests in `448d927`; the code review found no test for either. The Phase 4 fix pass adds
  them (the entry above stands as written; this one supersedes its claim).
- discovery: the security lane found the per-turn recovery restore ran outside the credential
  gate, that removing a stranded account left a valid lineage in `recovery/`, and that a
  `JsonException` after a successful token POST could drop a rotated pair; all fixed in the Phase 4
  fix pass with their named tests.
- deviation: plan said `UsageSnapshotCache.SaveAsync` takes a cancellation token / found the token
  was ignored by design and a parameter documented as ignored invites misuse / chose to drop it,
  which deleted `ASaveRunsToCompletionUnderAPassTokenThatHasBeenCancelled` because no token can
  now reach the save (`c8218e2`) / revisit: none.
- deviation: the fix pass lowered four "pass failed / sweep failed / cache load failed" log lines
  from Error to Warning while curating them (the lost-lineage line stays at Error) / revisit: raise
  any of them back if the operator wants a pass failure to stand out in the log.
- discovery: the fix-pass verifier found the post-POST catch-all unreachable by the suite (every
  scripted write failure sat inside the retry filter); a further test throws an unfiltered
  exception and asserts an immediate strand.

## PR review: Codex and CI

- discovery: a switch landing mid-pass put one account's usage figures on another's card.
  `QuotaRefresh.CandidatesAsync` fixes the live account's identity once when the pass is
  assembled, the live turn reads the live pair holding no gate, and `LiveDirectorySwitch`
  consulted `QuotaState.InProgress` nowhere, so a switch from A to B between two turns had A's
  turn read B's pair. Fixed by refusing the switch with a new `SwitchRefusal.RefreshInProgress`
  while a pass or a per-card read is in flight, checked under the mutation gate rather than in
  front of it: in front, a switch could still slip in between `TryBeginRun` and the identity
  repair that opens the pass, which takes the gate with a zero wait (`0aebae7`). Residual: a pass
  that begins while a switch already holds the gate skips the live account as unverified and the
  switched-to card can read "needs login" until the next pass, but no ordering now puts another
  account's figures on a card.
- deviation: plan said `SwitchEndpointTests` races two switches through the test server and
  asserts one 200 and one 409 / found the endpoint's gate wait is zero, the test server queues
  each request to the thread pool, and the test has no barrier, so under the Ubuntu leg's load the
  first switch finished before the second was scheduled and both returned 200 (twice in a row;
  green on Windows and locally) / chose to hold the gate permit against a single switch, the
  pattern `RosterEndpointTests` already uses, and renamed the fact
  `ASwitchIsRefusedWhileAnotherCredentialChangeHoldsTheGate`; the real race stays covered by
  `LiveDirectorySwitchTests.ConcurrentSwitchesSerializeAndLeaveOneHolderPerLineage` / revisit:
  none.
