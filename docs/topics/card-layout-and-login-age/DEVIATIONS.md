# Deviations log: card-layout-and-login-age

Append-only. One entry per decision. Types: plan-confirmed, discovery, deviation, human-decision.

## Phase 1

- **deviation** (2026-09-14, worker, Phase 1). Plan said: Sanity Check `! grep -q "UtcNow"` on
  `DashboardAssemblerTests.cs`. Found: the check fails at the baseline `2b65a31` (ten
  `factory.Clock.GetUtcNow()` lines, the form the plan mandates). Chose: read the intent and check
  `grep -c "DateTimeOffset.UtcNow"` prints `0` instead (it does, at `2f2abe3`). Revisit: none; the
  plan's Sanity Check wording is corrected in the same commit as the Phase 1 mark.
- **deviation** (2026-09-14, worker, Phase 1). Plan said: `dotnet test -c Release --filter …` for
  the filtered run. Found: the xUnit v3 runner on Microsoft.Testing.Platform has no `--filter`.
  Chose: `--filter-class "*DashboardAssemblerTests" --filter-class "*RosterEndpointTests"` (62
  passed, 0 failed); the unfiltered run is the gate the plan relies on. Revisit: none.
- **deviation** (2026-09-14, worker, Phase 1). Plan said: `ReadLoginExpiryAsync(profile)`. Found:
  `ICredentialPairStore.ReadParkedAsync` requires a cancellation token. Chose: thread the ambient
  token, as every other call on the assemble path does (`DashboardAssembler.cs:214`). Revisit: none.
- **discovery** (2026-09-14, worker, Phase 1). `LiveDirectorySwitch.QuarantineDuplicateLineagesAsync`
  (`src/ClaudeCodeAccountRotation.App/Switching/LiveDirectorySwitch.cs:511`) calls `ReadPairAsync`
  on every parked folder unguarded during startup reconciliation, so a torn parked credential file
  present at boot fails host startup with a `JsonReaderException`. Out of this chain's fence;
  belongs with the error-mapping work (#9). Not fixed here. Outcome: unverified beyond the worker's
  reading of the call path; no test written.
- **discovery** (2026-09-14, verifier, Phase 1). `CredentialPair.FromJson`
  (`src/ClaudeCodeAccountRotation.Core/Identity/CredentialPair.cs:57-58`) reads `accessToken` with
  `GetValue<string>()`, which throws `InvalidOperationException` when the key holds a non-string
  value; that type is outside the guard's four and would fail the payload. Not a torn-file shape;
  left as is, with the startup read above, for the error-mapping work (#9). Outcome: unverified,
  no test written.
- **plan-confirmed** (2026-09-14, verifier, Phase 1). All fourteen Phase 1 criteria PASS on
  `2f2abe3`: build 0 warnings, `dotnet test -c Release` 474 total, 0 failed, 1 skipped (repeated
  main-side; one earlier run showed 1 failed while a second build ran in the same worktree, and two
  clean re-runs passed).
- **plan-confirmed** (2026-09-14, orchestrator, before Phase 1). `ProfileFolderStore.ListAsync`
  never lists a profile with a null `Account`; a parsed profile without `profileFetchedAt` has
  `ProfileFetchedAt == null`. `FileSystemCredentialPairStore.WriteParkedAsync` is atomic (temp file,
  fsync, replace) and both writers go through it, so the parked-read guard defends against external
  writers, not this process. Baseline `dotnet test -c Release`: 468 total, 1 skipped, 0 failed.

## Phase 2

- **deviation** (2026-09-14, worker, Phase 2). Plan said: four page helpers (`stateChip`,
  `loginLine`, `loginExpired`, `ago`). Found: the seven-day test is needed by both the line's `warn`
  class and the action-row condition. Chose: a fifth helper `loginSoon(account, at)`; the
  action-row condition is `!hasCredentials || loginSoon`, which covers the expired case because
  `secondsUntil` clamps at zero (`app.js:405-407`). Revisit: none.
- **deviation** (2026-09-14, worker, Phase 2). Plan said: the `login-expiry` line goes "after the
  `next-reset` line". Found: the Brief and design T5 say "at the usage block's foot", which is
  after the `refresh-state` line. Chose: the foot. Revisit: none.
- **deviation** (2026-09-14, worker, Phase 2). Plan said: the standing line is suppressed on a
  paused card. Chose: flip the existing `standing === "paused"` branch of `nextReset` to null
  rather than add a roster test; the wire `standing` is derived from the same roster flag
  (`DashboardAssembler.cs:186`, `AccountAvailability.cs:85-87`). Revisit: none.
- **deviation** (2026-09-14, worker, Phase 2). `editPanel(account, offerLogin)` gains a second
  parameter; the `Log in again` button inside it is `type="button"` via `actionButton`, so it
  cannot submit the Edit form. `at` is computed per card inside the `forEach` so no changed line
  falls within the render guard's diff context. Revisit: none.
- **plan-confirmed** (2026-09-14, verifier, Phase 2). All seventeen Phase 2 criteria PASS on
  `0e7da2a`; build 0 warnings, 474 tests, 0 failed, `node --check` clean. Note for the operator: a
  login younger than 48 hours reads `logged in 3 h 12 min ago`, mirroring `relative()`'s buckets;
  the `<N> d ago` shape applies past 48 hours.
