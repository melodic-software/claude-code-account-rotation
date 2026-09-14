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
