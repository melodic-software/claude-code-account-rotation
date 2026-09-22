# cross-os-rotation

Planned 2026-09-20 for issue #65 (run the WSL side of each machine off the same account store as
Windows, with one token family per account). Parent topic:
`docs/topics/claude-subscription-rotation/PLAN.md`. Design: `design/design-resolution.md`, which is
the tracked record of the chosen shared-store design and of the measurements it rests on; the
working drafts live in an untracked memory slice and are not a citable source for anything
load-bearing.

## Brief

### TLDR

- Each fleet machine runs Claude Code on two sides: native Windows and a WSL distro. Today only the
  Windows side has an account store, so the WSL side needs its own logins. This chain gives the WSL
  side the **same** store: one token family per account per machine, checked out to whichever side
  is using it, with a `holder.json` record in the empty slot saying which side has it.
- The Windows app stays the only writer of store slots. A WSL switch is clicked on the Windows page,
  the leader claims the slot by rename, and a follower process inside the distro performs a staged,
  fingerprint-verified, journaled copy across the volume boundary. Nothing needs a cross-side lock,
  which matters because a cross-side exclusive `mkdir` was measured to double-acquire
  (`design/design-resolution.md` section 3).
- The cost this buys down: the 28-day login window is **fixed** and a refresh does not slide it
  (measured; design section 2). Two token families per account would therefore mean 20 re-logins per
  machine per month instead of 10, forever. One family is what makes the shared store worth building.
- **The operator is not using WSL until this works.** The phasing is therefore a tracer bullet: the
  first five phases deliver one account handed from the Windows store to WSL and back, on one
  machine, with every crash-safety test that path needs. Breadth (all ten accounts on the page, the
  second machine, usage figures for held accounts, escape hatches, UI polish) comes after, in phases
  6 to 8. **WSL is usable at the end of phase 5.**

### The issue, as filed

Issue #65: the WSL lane on each machine cannot use the rotation's accounts. The operator wants one
central place, on Windows, for accounts, labels, browser profiles, and logins; a selection per side,
so Windows and WSL can be on different accounts at once; and above all never to log in ten times on
Windows and ten more in WSL, and never to repeat that monthly.

### Goal

From the Windows page, switch either side of the machine to any account in the store, with exactly
one token family per account per machine, no login ever performed inside the distro, and the
single-holder invariant provable by `check-single-holder.sh` over all four roots.

### Constraints

- **Time-to-usable is a first-class constraint.** The operator is holding off on WSL until the
  hand-off works. Phases are cut so the usable slice lands first; nothing that merely improves the
  page, the breadth, or the recovery ergonomics is allowed inside it.
- **No acceptance criterion that protects a login from being lost or duplicated may be weakened to
  make the slice smaller.** The crash-injection suite over every `ImportStep` and every
  `WslSwitchStep`, the fingerprint verification after every copy, and `duplicates=0` from
  `check-single-holder.sh` are all inside the slice.
- Work happens in a worktree off `main` at `9b1740b`. Never on the main checkout.
- **The operator's live dashboard on the loopback port is not stopped, restarted, reinstalled,
  seeded, or read by any phase before phase 5.** Every phase before it verifies against temp roots,
  temp config (`--config <tmp>`), fake credential pairs, and the fake `claude` script the login
  spike established.
- **No real e-mail address, machine path, or user name in any tracked file.**
  `bash eng/check-no-machine-paths.sh` is the CI gate and scans `src tests README.md docs`. Test
  data uses `example.com` addresses.
- Stage files by name, never `git add -A`. Run `git checkout -- '**/packages.lock.json'` after any
  build that drifts them; `dotnet restore --locked-mode` is what CI runs.
- Core stays BCL-only: no package reference, no project reference, no config binding.
- Analyzer posture: `AnalysisMode=All` with `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild`.
  Every CA and escalated IDE finding is a build error, so the red step is a test written against the
  real signature with a minimal implementation behind it, never `throw new NotImplementedException()`.
- Repo rules: Conventional Commits titles, `.claude/rules/pr-body-contract.md`, and the personal-org
  commit identity this checkout's `includeIf` supplies (verify `git config user.email` before
  committing; it must never report a work address).
- CI runs both legs. Anything the follower does must build and test on the Linux leg, which has no
  Windows file system and no real distro.
- **Every merge is a human gate.** GitHub refuses self-approval, so each phase ends at a PR the
  operator approves. Agents can hold several phases ready at once, but no phase starts on top of an
  unmerged dependency.

### Acceptance criteria

Restated from the user's words as the six binary tests R1 to R6 in `design/design-resolution.md`
section 4. The chain is done when all six hold on **both** fleet machines. The slice (phase 5) proves
R1, R3, R4, R5 and R6 on the laptop for the designated account; R2's 28-day form is a standing
observation, not a phase gate.

1. **R1** every add, alias, browser mapping, login, and switch for either side is a click on the
   Windows loopback page; the WSL side exposes no add, no login, and no roster write (404).
2. **R2** the WSL side has performed **zero** `claude auth login`, and logins per machine per 28 days
   are at most one per account. The zero-logins half is checkable at any moment (`grep -c "auth
   login"` over the follower log is `0`); the per-28-days half is a real-world observation over a
   full window and is tracked, not gated.
3. **R3** switching Windows to B leaves WSL on A and the reverse; three open sessions per side follow
   their own side's switch on their next request.
4. **R4** the `linux-x64` build serves `/api/dashboard` inside the distro.
5. **R5** `check-single-holder.sh` over the four roots (Windows live, store including `.transit/`,
   WSL live including `*.incoming`, WSL app data) prints `duplicates=0` after ten switches on each
   side.
6. **R6** a card for an account held by the other side shows `in use by wsl` / `in use by windows`
   and disables that side's switch button within one dashboard poll (`POLL_MS = 10000`,
   `app.js:4`); the planner refuses `HeldByOtherSide` when the button is bypassed.

### Captured assumptions

- **The fleet is two machines**, the laptop and the desktop. A work machine exists but is outside
  the fleet manifest and gets any setup by hand; no phase here targets it. Any statement of "three
  machines" in an earlier draft is wrong.
- **No account holds two families anywhere today.** Both WSL lanes were logged out by hand (laptop
  2026-09-19, desktop 2026-09-20), which is the work an earlier migration step would have done. There
  is nothing to retire at rollout, no `Retire the WSL login` route is built, and the `foreign family`
  banner moves out of the slice into phase 7 because only the phase-7 escape hatch can recreate the
  condition. Revisit trigger: a second family observed on either machine.
- The 28-day login window is fixed and a refresh does not slide it (measured; design section 2). The
  parent plan's captured assumption to the contrary is false and is corrected by a dated note in
  phase 5. Revisit trigger: a card whose `loginExpiresAt` moves by more than a few seconds across a
  refresh.
- The follower is a **leader-spawned child** (design decision 3). Revisit trigger: a want for the WSL
  side to work with the leader down, which `nat` networking forbids anyway.
- The escape hatch when the distro is off is **allowed**, with the banner and the quarantine-on-return
  rule (design decision 6), and is built in phase 7. Until then the documented answer to "the distro
  is off and I need that account on Windows" is: start the distro, or switch WSL away first.
- DrvFs `fsync` returning 0 is not a durability measurement. This is the chain's top risk; see Risks.
- The probe residue under the Windows temp directory has been deleted, and no repository file was
  written by the probe session.

### Out-of-scope, stated

- On-demand refresh of a WSL-held account; a follower page; a follower-initiated switch; proposals
  per side; headless WSL lanes on a pinned account.
- A fleet-wide (cross-machine) view of who holds what. Recorded in `design/design-resolution.md`
  section 17 and designed nowhere.
- The work machine.
- **Secret delivery.** No phase delivers a secret through dotfiles: the follower's configuration is
  paths and a port, and credentials move only as files inside one machine. The dotfiles `vault-exec`
  work and its vendor-key migration are unrelated and must not be pulled in.
- Editing the parent plan's body. Phase 5 adds only dated notes, the sibling chains' convention.

## Plan

**Standards grounding:** no standards index exists at the repository root, so this follows the repo's
own conventions as the sibling chains `docs/topics/order-by-next-reset/` and `docs/topics/usage-cards/`
established them: `.claude/rules/pr-body-contract.md`; the sync-managed analyzer pair; the Core
csproj's BCL-only layer rule; Core static pure functions with one sibling `<Type>Tests`,
full-sentence test names over Shouldly, `IReadOnlyList` returns, ordinal tie-breaks; and the existing
credential machinery whose shapes every phase extends rather than replaces (`CredentialMutationGate`,
`SwitchJournal`, `AtomicJsonFile`, `OAuthRefreshLock`, `RecoveryFiles`).

**Build technique: tracer bullet.** The design's own phasing was leader-then-follower-then-coordinator
with rollout last, which puts the first usable hand-off at the very end. That order is kept for the
protocol but the *breadth* is pulled out of it: phases 1 to 5 are the thinnest end-to-end path that
can safely move one real pair, and phases 6 to 8 widen it. The design's phases 1 to 4 map to phases 2
to 5 here, minus the presentation and recovery work now in 6 and 7.

Scale: Large. Nine phases including the settled phase 0. Baseline to record before phase 1:
`dotnet test -c Release` total on `9b1740b`; every later count is stated relative to it.

### Sequencing, parallelism, and the earliest usable point

```text
Phase 0 [DONE]
   |
Phase 1  Core side model                 serial gate, ~2-3 h
   |
   +-- Phase 2  Leader: claim and park  -+   PARALLEL, separate worktrees, ~1 day each
   |                                     |
   +-- Phase 3  Follower: staged import -+
                                          |
Phase 4  Coordinator, one switch control, temp-root crash acceptance   serial, ~1-1.5 days
   |
Phase 5  LIVE SLICE on the laptop, one designated account   <=== WSL IS USABLE HERE
   |
   +-- Phase 6  Breadth on the page      -+   PARALLEL after 5, ~1 day each
   |                                      |
   +-- Phase 7  Escape hatches            |
   |                                      |
   +-- Phase 8  Second machine, dotfiles -+
```

- **Strictly serial:** 1 before everything; 4 after both 2 and 3; 5 after 4.
- **Agent-parallelizable:** phases 2 and 3 (their only shared file is the configuration pair, fenced
  by key below); phases 6, 7 and 8 after phase 5. Within a phase, the test suites parallelize across
  agents once the types exist.
- **Real-world waits (not agent work):** phase 4's temp-root acceptance is a 40-switch, 9-kill script
  run that takes roughly an hour of wall clock whoever starts it. Phase 5 needs live Claude Code
  sessions on both sides, minutes not days. The only genuinely multi-day observation in the whole
  chain is R2's per-28-days half and the fixed-window revisit trigger; both are standing checks after
  phase 5, never gates.
- **Human gates:** every phase ends at a PR the operator approves and merges (GitHub refuses
  self-approval). Phase 5 is operator-driven throughout. Phase 7's escape-hatch semantics and phase
  8's desktop rollout each need an explicit go.
- **Realistic wall clock to a usable WSL, with parallel agent implementation and prompt merges: about
  three to four calendar days.** The binding constraints are the five merge gates and the phase-4
  acceptance run, not the typing.

#### Phase file-overlap matrix

| File | P1 | P2 | P3 | P4 | Fence |
|---|---|---|---|---|---|
| `Core/Switching/SideName.cs`, `HolderRecord.cs`, `SlotState.cs`, `SlotStateRule.cs` | create | read | read | read | P1 owns; later phases do not edit |
| `Core/Switching/SwitchRefusal.cs`, `SwitchPlanningInput.cs`, `SwitchPlanner.cs` | add members | use | — | use | P1 adds all three refusals at once, including `SideOffline`, which only P4 raises |
| `App/Configuration/ConfigurationFile.cs`, `ConfigurationValidator.cs` | — | `store.shared` | `role`, `mailbox` | `peers[]` | **the only real overlap between the parallel pair.** Separate keys, separate validator branches, separate test classes; P2 and P3 each add their own and neither reformats the other's |
| `App/Quota/RefreshOutcome.cs`, `QuotaRefresh.cs` | — | `HeldElsewhere` | follower registers no refresh service | — | no shared edit |
| `App/Switching/LiveDirectorySwitch.cs` | — | record write and delete | — | — | P2 only |
| `App/Adapters/FileSystem/StagedImportCredentialPairStore.cs` | — | — | create | — | P3 only |
| `App/wwwroot/app.js`, `index.html` | — | one state word | — | one switch control | P6 owns the chip vocabulary and the layout |

### Phase 0: settle the login-expiry question and clear the probe residue [DONE]

Goal: establish whether the login window slides on refresh, because that is the one fact that could
have flipped the design choice.

Settled 2026-09-19 with no code change and no instrumented build: the window is **fixed** at 28 days
and a refresh moves the recorded expiry by about +1.3 s, not by 28 days. Method, numbers, and
consequences are in `design/design-resolution.md` section 2. The probe residue under the Windows temp
directory has been deleted. The re-read trigger that would have reopened the two-family comparison did
not fire.

Acceptance, met: the measurement is recorded with its method and its discriminator, and no probe
directory remains.

### Phase 1: the side model in Core [DONE] (#66)

Review: architecture

**Goal:** the three types and the three refusals both later phases need, so the leader and follower
can then be built in parallel without touching one another's files.

Work items, in order.

1. `src/ClaudeCodeAccountRotation.Core/Switching/SideName.cs` (new):
   `public readonly record struct SideName(string Value)` with `Windows` and `Wsl` statics, ordinal
   comparison, and a doc comment saying a side is one operating-system lane of **one** machine and
   never a machine.
2. `Core/Switching/HolderRecord.cs` (new):
   `public sealed record HolderRecord(SideName Side, RefreshTokenFingerprint Fingerprint, DateTimeOffset Since);`
   using the existing `Core/Identity/RefreshTokenFingerprint.cs`. Doc comment: the record is an index
   into the store, the file is the truth, and when they disagree the file wins.
3. `Core/Switching/SlotState.cs` (new):
   `public enum SlotState { Parked, HeldHere, HeldElsewhere, NeverLoggedIn, InTransit }`.
4. `Core/Switching/SlotStateRule.cs` (new): one pure static
   `public static SlotState Resolve(bool slotFileExists, HolderRecord? record, SideName thisSide, bool transitFileExists)`
   implementing design section 9.4 exactly. **This is the single derivation**; no caller open-codes
   it. Precedence: transit over everything; then a present slot file means `Parked` and the record is
   stale; then the record's side; then `NeverLoggedIn`.
5. `Core/Switching/SwitchRefusal.cs`: add `HeldByOtherSide`, `SlotInTransit`, `SideOffline` with doc
   comments in the file's house style. All three land here so the enum is not edited twice.
6. `Core/Switching/SwitchPlanningInput.cs`: one trailing parameter
   `SlotState TargetSlot = SlotState.Parked`, defaulted so every existing construction site compiles.
7. `Core/Switching/SwitchPlanner.cs`: refuse `HeldByOtherSide` on `HeldElsewhere` and `SlotInTransit`
   on `InTransit`, after the existing target guards and before the executor's own refusals.
8. `Core.Tests/Switching/SlotStateRuleTests.cs` (new): one fact per row of design section 9.4, plus
   `ASlotHoldingAPairOutranksARecordNamingAnotherSide` and `ATransitFileOutranksEverything`.
9. `Core.Tests/Switching/SwitchPlannerTests.cs`: `ASlotHeldByTheOtherSideIsRefused`,
   `ASlotInTransitIsRefused`, and every existing planner fact still green with the defaulted parameter.

**Sanity Check:**

- `dotnet test -c Release` exit 0, failed 0, total >= baseline + 10.
- `ls src/ClaudeCodeAccountRotation.Core/Switching/` lists the four new files.
- `grep -c "HeldByOtherSide\|SlotInTransit\|SideOffline" src/ClaudeCodeAccountRotation.Core/Switching/SwitchRefusal.cs`
  prints `3`.
- `grep -rn "PackageReference\|ProjectReference" src/ClaudeCodeAccountRotation.Core/ClaudeCodeAccountRotation.Core.csproj | wc -l`
  prints `0`.
- `git diff --stat -- src/ClaudeCodeAccountRotation.App/` prints nothing.
- Build 0 warnings; format, typos, markdownlint, `check-no-machine-paths.sh` clean.

**Verification without risking a live login:** Core is BCL-only, with no file system and no network.
Nothing here can reach a credential file.

**Rollback:** revert the commit. No file format, no configuration key, no on-disk artifact.

**Depends on:** nothing. **Parallelism:** none, it is the gate. **Waits:** none. **Human gate:** the
merge. **Estimate:** 2-3 h of agent work.

### Phase 2: the leader claims and parks, behind `store.shared` [DONE] (#67)

Review: code-design, security

**Goal:** the Windows side writes, reads and reconciles `holder.json`, claims a slot by rename into
the mailbox, parks an incoming export back, and refuses a held or in-transit slot. All of it behind
one flag that is off by default.

Work items, in order.

1. `App/Configuration/`: add `store.shared`, **default `false`**. Every behavior in this phase is
   gated on it, which is what makes the flag the rollback.
2. `App/Adapters/FileSystem/HolderRecordFile.cs` (new): read, write, delete `holder.json` through the
   existing `AtomicJsonFile`, owner-only ACL on Windows as the credential writes already do.
3. `App/Adapters/FileSystem/FileSystemCredentialPairStore.cs`: `ClaimToMailboxAsync` (the L2 rename,
   single-volume, under the existing gate) and `PromoteFromMailboxAsync` (the L4 rename, after a
   fingerprint comparison the caller supplies). No copy path on this side, ever.
4. `App/Switching/LiveDirectorySwitch.cs`: inside the existing journal window and under the existing
   mutation gate, on unpark write `holder.json {windows, fingerprint, since}` into the incoming slot;
   on park delete the outgoing slot's record. No new lock, no new journal step.
5. `App/Dashboard/`: call `SlotStateRule.Resolve` once per account per assemble and apply design
   section 9.4's reconciliation (a slot holding a pair **and** a record deletes the record and logs
   `holder record dropped: slot holds a pair`). Feed `SlotState` to the planner input and emit one
   plain state word per card. **No chip vocabulary and no layout work here; that is phase 6.**
6. `App/Quota/RefreshOutcome.cs` and `QuotaRefresh.cs`: add `HeldElsewhere`; a slot with a record or a
   transit file is recorded with that outcome and **no HTTP call is made for it**.
7. `tests/acceptance/check-single-holder.sh`: accept repeated `--root` arguments and count `.transit/`
   files and `*.incoming` names as holders, keeping the existing positional form working.
8. Tests: `HolderRecordFileTests`; claim and promote against a temp store asserted by fingerprint;
   `LiveDirectorySwitchTests` facts that a switch writes the incoming record and deletes the outgoing
   one and that a crash between them leaves a state section 9.4 rewrites; an API fact that a
   hand-placed `holder.json {"side":"wsl"}` on a temp-root slot returns 409 `HeldByOtherSide`; a
   refresh-pass fact that a held slot yields `HeldElsewhere` with zero requests recorded; a fact that
   with `store.shared: false` none of these behaviors occur.

**Sanity Check:**

- `dotnet test -c Release` exit 0, failed 0, total >= baseline + 22.
- With `store.shared: false` the whole pre-existing suite is green and no test writes a `holder.json`.
- On a temp-root leader with seeded fake pairs: after a switch the outgoing slot holds no
  `holder.json` and the incoming slot holds one whose `side` is `windows` and whose `fingerprint`
  equals the live pair's, both asserted by test.
- A hand-placed `{"side":"wsl"}` record on a parked temp-root slot: switch returns 409
  `HeldByOtherSide`.
- A hand-placed empty file under `<tmp-store>/.transit/wsl/` for a parked account: a full refresh pass
  records `HeldElsewhere` and the recording handler counted zero requests for that account.
- `bash tests/acceptance/check-single-holder.sh` over the temp roots prints `duplicates=0` and counts
  the transit file.
- Build 0 warnings; format, typos, markdownlint, shellcheck on `tests/acceptance/*.sh` and `eng/*.sh`,
  `check-no-machine-paths.sh` clean.

**Verification without risking a live login:** every assertion runs against a temp store, a temp live
dir and fake pairs through the existing `AppFactory` and the recording HTTP handler. The operator's
instance is neither read nor restarted, and `store.shared` defaults to `false`, so a build installed
by accident behaves exactly as today.

**Rollback:** set `store.shared: false`, or revert. `holder.json` is an index, never a token; design
section 9.4 rebuilds or discards it from the files, so a store left with stale records self-heals on
the next dashboard read. No credential file is moved that today's switch does not already move.

**Depends on:** phase 1. **Parallelism:** runs concurrently with phase 3 in a separate worktree; the
configuration pair is the only shared file and each phase adds its own keys. **Waits:** none.
**Human gate:** the merge. **Estimate:** about 1 day of agent work.

### Phase 3: the follower's staged import, and Linux parity [DONE] (#68)

Review: code-design, security. **The riskiest phase; see Risks.**

**Goal:** a process that can take a pair across the volume boundary and give one back, verified by
fingerprint at every step and finished or unwound by a journal after any crash, plus a `linux-x64`
build CI exercises.

Work items, in order.

1. `App/Configuration/`: add `role` (`leader` default, `follower`), `listenPort`, `mailbox`,
   `claudeExecutable`. `ConfigurationValidator`: a follower has no profiles root; its live dir must
   **not** be under `/mnt/`; its app data must be on the live dir's volume; `mailbox` must exist and
   be writable. The leader's validator is unchanged.
2. `App/Adapters/FileSystem/StagedImportCredentialPairStore.cs` (new): design section 9.2, steps F3
   to F6. Every copy is followed by `fsync`, a read-back **through a fresh open**, and a fingerprint
   comparison; a mismatch unwinds and never proceeds. **F4 stops**: the follower exports, answers
   `Exported {fa}`, and refuses to swap until a commit arrives.
3. `App/Switching/ImportJournal.cs` (new): the `ImportStep` journal and `last-import.json`, in the
   existing `SwitchJournal` shape.
4. `App/Switching/ImportReconciler.cs` (new): design section 9.3's follower table, run at start under
   the gate before any request is served, allowing for the live pair having been rotated by the CLI
   while the follower was down.
5. `App/Endpoints/ImportEndpoints.cs` (new): `POST /api/import` (F1 to F4), `POST /api/import/commit`
   (F5 to F8), `POST /api/import/abort` (unwind from `Exported`), and `GET /api/import-status`,
   taking the follower's gate and the WSL live dir's `.oauth_refresh.lock` from F2 on and holding
   both across the two calls with a 120 s idle self-abort. Idempotent by F1; a commit that arrives
   after a self-abort is answered "not imported". **No token appears in any request, response, or
   log line**: identity crosses as a SHA-256 fingerprint only.
   Three rules keep the two-call hold safe, all of them required: the idle window between the
   `Exported` answer and the commit is **20 s**, inside `.oauth_refresh.lock`'s 60 s stale
   threshold (`OAuthRefreshLock.cs:7-24`); the follower **re-reads the live file immediately before
   F5** and aborts if it is no longer the pair it exported, so a stolen lock and a CLI rotation
   cost a refused switch rather than a lost pair; and the follower re-stamps the lock directory's
   mtime every 20 s while it holds it. A commit arriving after a self-abort is answered
   "not imported".

   **No outgoing account is a first-class case.** After both WSL lanes were logged out by hand, an
   empty WSL live dir is exactly the state of the first real hand-off in phase 5. F4 then exports
   nothing and answers `Exported {none}`; the leader skips the gate and the park.

   **Why the commit half exists (the export gate).** It lets the leader read the exported file
   **natively**, on the store's own volume, and compare its fingerprint before the follower performs
   the one step that destroys the outgoing account's last local copy. This closes the chain's only
   lineage-loss window (F4 to F5) at the cost of one extra request per WSL switch. It was briefly
   planned as a deferred mitigation and is now **in this phase**: the follower half (refuse to swap
   without a commit, unwind on abort) ships here, and the leader half (L3b, `ExportVerified`) ships
   with the coordinator in phase 4. The follower must be unable to swap on its own even if a future
   leader forgets to gate it, which is why the refusal lives in the follower and not in the caller.
6. `App/Switching/SwitchOptions.cs`: a test-only `FailAfterStep` hook, the mechanism crash injection
   drives.
7. Publish: a `linux-x64` profile beside the existing `win-x64` one; a POSIX fake `claude` script for
   the Linux CI leg.
8. Tests: the staged store against two temp directories with on-disk state asserted by fingerprint
   after each of F3 to F6; a claimed file rewritten between the request and F3 fails the verify and
   unwinds; an export read-back mismatch unwinds; the export-gate facts above; crash injection at
   **every** `ImportStep` followed
   by a fresh reconciler over the same roots, asserting the section 9.3 outcome and that every
   fingerprint written exists in exactly one non-staging file; validator facts; a fact that the
   follower composition registers no refresh service, no login route and no roster route.

**Sanity Check:**

- `dotnet test -c Release` exit 0, failed 0, total >= baseline + 30, **on both CI legs**.
- The crash-injection suite covers all six `ImportStep` values: a test asserts
  `Enum.GetValues<ImportStep>().Length` equals the number of injection cases, so a new step cannot be
  added without a case. It fires **both** after the journal write and between the disk mutation and
  the journal write, so the torn-F5 case (live already holds B while the journal still reads
  `Exported`) is exercised; reconciliation continues F6 to F8 there rather than unwinding, asserted
  on disk.
- **The lock budget holds.** A commit sent later than the 20 s window finds the follower
  self-aborted with the live pair untouched; a live pair rotated between the `Exported` answer and
  the commit makes F5 refuse rather than swap; the follower re-stamps the lock directory's mtime
  while it holds it, asserted by mtime movement over a hold longer than one interval.
- **No outgoing account works.** A follower whose live dir is empty answers `Exported {none}` and
  the import completes with nothing exported and nothing parked. This is the phase-5 starting
  state, so it is a required fact, not an edge case.
- After every injection case, a scan of the temp roots finds each fingerprint in exactly one
  non-staging file.
- The negative path is asserted, not implied: a mismatched export read-back unwinds and leaves the
  live pair untouched.
- **The export gate holds.** `POST /api/import` alone returns with the journal at `Exported` and the
  WSL live pair **unchanged on disk** (fingerprint still `fa`); the follower answers 409 to a swap
  attempt in any state but `Exported`; `POST /api/import/abort` from `Exported` deletes the export
  and the staging file and leaves the live pair `fa`; a commit arriving after the 120 s idle
  self-abort is answered "not imported" and changes nothing on disk. No code path reaches F5 without
  a commit.
- No token literal in the follower's log fixture, the assertion shape PR #59 established.
- The follower under `--config <tmp>` answers 404 for `POST /api/accounts` and for the login route.
- A follower whose live dir is under `/mnt/`, and one whose `mailbox` does not exist, each fail
  validation with a named reason.
- The `linux-x64` publish answers `GET /api/dashboard` in the Linux CI leg.
- Build, format, typos, markdownlint, shellcheck, `check-no-machine-paths.sh` clean.

**Verification without risking a live login:** the whole phase runs against two temp directories and
fake pairs. Real `EXDEV` over 9P is exercised in phase 4's in-distro temp-root acceptance; no leader
in this phase is ever pointed at a real store, and the follower is not installed anywhere.

**Rollback:** the follower is never deployed and `peers` does not exist yet, so a revert is a code
revert with no operational step. A half-built follower cannot be reached: nothing spawns it.

**Depends on:** phase 1. **Parallelism:** runs concurrently with phase 2; internally, the staged-store
suite and the reconciler suite are separate agent lanes. **Waits:** none. **Human gate:** the merge.
**Estimate:** about 1 to 1.5 days of agent work, the longest of the parallel pair.

### Phase 4: the coordinator, the two roles, and the crash acceptance [DONE] (#69)

Review: architecture, security

**Goal:** a WSL switch completes end to end from the Windows page, and every crash point on either
side resolves to a state the next start finishes or unwinds. Proven on temp roots inside the real
distro, over real 9P.

Work items, in order.

1. `Core/Switching/WslSwitchStep.cs` (new: `Claimed`, `ExportVerified`, `Imported`, `Parked`) and
   the coordinator's journal.
2. `App/Switching/WslSwitch.cs` (new): the L1 to L4 coordinator of design section 9.1 with the
   leader-side crash table of section 9.3, including **L3b, the export gate**: after the follower
   answers `Exported`, the leader reads the exported file **natively** on the store's own volume and
   requires its fingerprint to match before it sends `commit`. A mismatch, a short read or an absent
   file aborts and unclaims, and the switch is refused with nothing swapped. This is the leader half
   of the mitigation phase 3 (#68) builds into the follower; together they close the chain's only
   lineage-loss window. `Cancel` is offered **only** after the follower answers "not imported";
   there is never a blind unclaim.
3. `Core/Peers/IPeerRotationInstance.cs` and `IPeerProcessHost.cs` (new ports);
   `App/Adapters/Peers/HttpPeerRotationInstance.cs` and `WslDistributionPeerHost.cs` (adapters). The
   host spawns `wsl.exe -d <distro> -u <user> --exec ...` and supervises the child. A version
   mismatch refuses the import before anything moves.
4. `App/Configuration/`: `peers[]` with `side`, `baseAddress`, `storePathFromPeer`, `launch`.
5. `App/Endpoints/SideEndpoints.cs` (new): `POST /api/sides/wsl/accounts/{email}/switch` plus a
   minimal `Start WSL side`. **One switch control on the page and one line of side state; the panel,
   the chips and the tee figures are phase 6.**
6. Tests: the coordinator against a fake `IPeerRotationInstance` (claim, export-gate, commit, park;
   `AlreadyImported`; a timeout after `Claimed` leaving the journal open with no unclaim; a
   "not imported" answer unclaiming so the file is back and the record gone; **an export whose bytes
   fail the native gate: abort, unclaim, and both sides' live pairs untouched**; a leader restart at
   each `WslSwitchStep`, `ExportVerified` among them); the
   two-`AppFactory` leader-and-follower test driving a switch through `/api/sides/wsl/...`; a planner
   fact for `SideOffline`.
7. The **temp-root WSL acceptance** under `tests/acceptance/`: a second follower inside the distro
   with `--config <tmp>`, mailbox a temp directory under `/mnt/c` (real DrvFs, real `EXDEV`), live dir
   under `/tmp`, fake pairs and the fake `claude` script; a second leader on Windows with temp roots
   pointed at that same temp store.

**Sanity Check:**

- `dotnet test -c Release` exit 0, failed 0, total >= baseline + 48.
- The temp-root WSL acceptance exits 0 having performed **20 WSL switches and 20 Windows switches**,
  with `kill -9` of the follower at each of the six `ImportStep` values and of the temp leader at
  each of the four `WslSwitchStep` values, each followed by a restart whose outcome matches its
  section 9.3 row, ending with `check-single-holder.sh` over the four temp roots printing
  `duplicates=0` and no fingerprint in two non-staging files.
- At least one acceptance iteration corrupts the exported file between the follower's `Exported`
  answer and the leader's native read: the gate aborts, the switch is refused, and both sides' live
  pairs are unchanged.
- **The leader-crashed-after-commit case resolves forward.** A coordinator fact kills the leader
  after the follower has answered the commit but before `Imported` is journaled, with the
  follower's journal already cleared: the restarted leader asks `ImportStatusAsync`, sees the
  import completed, and resumes at L4. It must not unclaim. A cleared follower journal is never
  read as "nothing happened".
- The first acceptance iteration runs with an **empty** follower live dir, the phase-5 starting
  state: `Exported {none}`, no gate, no park, switch completes.
- Through the two-`AppFactory` test: after a WSL switch the leader reports the account as held by
  `wsl`, within one `POLL_MS` (10 000 ms) interval of completion.
- Stopping the follower flips its side to `offline` within 15 s; the leader restarting it reports
  `online` within 60 s.
- A mutating route sent from Windows with the custom header and no `Origin` is neither 400 nor 403.
- `grep -c "auth login" <follower log fixture>` prints `0`.
- Build, format, typos, markdownlint, shellcheck, `check-no-machine-paths.sh` clean.

**Verification without risking a live login:** entirely temp roots and fake pairs. The operator's real
leader keeps serving on its own port throughout; the temp leader uses a different port and
`--config <tmp>`. The `kill -9` cases are the point of the phase: every irreversible on-disk step is
exercised against fakes here, before phase 5 lets the protocol near a real pair.

**Rollback:** set `peers` to `[]` and the WSL side disappears from the page; the Windows side is
exactly phase 2. Nothing is installed in the distro that a running leader does not spawn.

**Depends on:** phases 2 **and** 3. **Parallelism:** the coordinator suite, the two-factory test and
the acceptance script are three agent lanes once `WslSwitch` compiles. **Waits:** the acceptance run
itself is roughly an hour of wall clock, unattended. **Human gate:** the merge, and a look at the
acceptance output before phase 5 starts. **Estimate:** 1 to 1.5 days of agent work plus the run.

### Phase 5: the live slice on the laptop, one designated account [DONE] (#70)

Review: close-out. **Operator present; the first phase that touches a real credential root, and the
phase that makes WSL usable.**

**Goal:** one real account, chosen by the operator, hands from the Windows store to WSL and back on
the laptop, and the operator uses Claude Code in WSL on it.

Work items, in order.

1. Install the leader build with `store.shared: true` and `peers[]` for the distro. The Windows side
   behaves as today; every slot resolves `Parked`, the live one `HeldHere` with its record written by
   the section 9.4 reconciliation. Confirm the ten cards read exactly as before the install.
2. Install the follower in the distro (by hand for the slice; the dotfiles template is phase 8) and
   start it from the page. No `foreign family` is expected, both WSL lanes having been logged out by
   hand; if one appears, stop and resolve it by hand before switching.
3. Switch WSL to **one designated parked account** from the page: the first staged hand-off on real
   pairs. Zero logins. Run a real Claude Code session in the distro on it.
4. Switch WSL back, so the pair parks into its slot by rename, then switch out and back once more.
5. Ten switches on each side, then `check-single-holder.sh` over the four real roots from inside the
   distro.
6. Dated notes appended, never body edits: the parent plan's Terms posture amendment and its
   alternatives-table row; the correction of its captured assumption that a refresh renews the login;
   `ICredentialPairStore` doc comments; the README posture line. Issue #21 amended in a comment to
   read "one family per account **per machine**; a second family is `foreign family`, always shown,
   never silent".
7. Record the acceptance run in `tests/acceptance/README.md`.

**Sanity Check:**

- R1: the follower answers 404 for every add, login and roster route; every mutation the operator
  performs is a click on the Windows page.
- R2 (checkable half): `grep -c "auth login" <follower log>` prints `0`.
- R3: with three sessions open on each side, a Windows switch to B and a WSL switch to C leave each
  side's three sessions reporting their own side's account on the next message.
- R4: `curl` inside the distro against the follower's port returns a dashboard payload.
- R5: `check-single-holder.sh` over the four real roots prints `duplicates=0` after ten switches on
  each side.
- R6: the designated account's card reports it held by `wsl` and its Windows switch is refused
  (409 `HeldByOtherSide`) within one 10 s poll.
- The operator has completed at least one real WSL Claude Code session on a store account.
- `git log` shows the dated notes and no edit to any parent-plan body paragraph.

**Verification without risking a live login:** it cannot be, entirely. This is where real pairs move,
which is why every protocol step and every crash point was exercised against fakes in phases 3 and 4
first. The residual exposure is bounded to the one account in flight, the operator is present, and
the recovery for a lost lineage is one short Windows login, the same cost as a lapsed login.

**Rollback:** switch WSL away from the held account first, so the pair parks back by rename; then set
`store.shared: false` and `peers: []`, and the machine is exactly where it is today. If the follower
is dead and the account is stranded in the WSL live dir, the honest cost is **one short Windows login
for that account**, plus a stale record the next dashboard read drops.

**Depends on:** phase 4. **Parallelism:** none, it is one operator at one machine. **Waits:** the
session checks are minutes; R2's per-28-days half and the fixed-window revisit trigger begin here as
standing observations and gate nothing. **Human gate:** the whole phase, plus the merge of its doc
commit. **Estimate:** 1 to 2 h with the operator present.

**After phase 5 the operator can use WSL with rotation working**, on the accounts they hand over one
at a time from the page, on the laptop. Phases 6 to 8 are quality of life and breadth.

**Run note 2026-09-21 (#70): the phase ran and passed.** The record is the last row of
`tests/acceptance/README.md`. Six things this section or #70 got wrong, all found by running it:

1. **Nothing created the follower's mailbox.** `ConfigurationValidator` refuses to start a follower
   whose mailbox is absent, saying "the leader creates it in the store under `.transit/`", but the
   only creator was inside the claim — which cannot run until a follower is already answering. A
   fresh install could never start its follower at all. The temp-root acceptance never saw it
   because the script makes the mailbox itself. Fixed in this phase's commit; #82's `EnsureMailbox`
   does not cover it, sitting inside `WslSwitch.ImportAsync`, which has the same prerequisite.
2. **Work item 1 is wrong about the holder record.** It says the live account resolves `HeldHere`
   "with its record written by the section 9.4 reconciliation". The reconciliation writes no record:
   `SharedStoreSlots.ReadAsync` *derives* `HeldHere` from possession, which is what keeps a store
   that predates the flag from reading as never logged in until its next switch. Observed on the
   real store — after installing the leader over ten already-parked accounts, the live one's card
   read `held-here` with no `holder.json` anywhere. A record is written by a *switch*
   (`LiveDirectorySwitch` calls `SharedStoreSlots.TakeAsync` on the incoming slot, which is phase
   2's designed behavior) and by a hand-off to the other side; so a store reads correctly both
   before any record exists and after one does, which is the property that matters.
3. **R2's checkable half names a log the product does not produce.** The follower is leader-spawned
   through `wsl.exe` and its stdout is a discarded pty, so there is no follower log to grep. The
   criterion is restated as the stronger fact: the pair in the distro fingerprints equal to the pair
   that left the store, so it was handed over rather than logged in, beside the 404 on the login
   route and a container with no login runner. A real follower log belongs in phase 8's install, as
   a wrapper or a unit file, not as a restart during the slice.
4. **"Ten switches on each side" could not be done with one account** when this was written: nothing
   parked a held account back except a switch of that side to a *different* one, and R6 forbids
   Windows pulling it back. #82's release route landed mid-phase and made the round trip real, so
   the criterion is met rather than waived. Without it the honest form would have been ten Windows
   switches and one hand-off.
5. **An upgrade needs an external process kill**, because the app maps no shutdown route. Worth
   pricing into phase 8's autostart work.
6. **The product's own credential moves are indistinguishable from exfiltration to a policy
   classifier.** An agent driving this phase was refused at the hand-off itself (`Secret-Store
   Writes`), at ad-hoc credential fingerprint reads (`Credential Materialization`, though
   `check-single-holder.sh` runs fine because the hashing happens inside the sanctioned script), and
   at every process stop (`Interfere With Workloads`). The operator drove the five mutations from
   the page while the agent verified around each. **Any future agent-driven live slice needs the
   operator at the page**; phase 8's desktop rollout should be planned that way rather than
   discovering it again.

### Phase 6: breadth on the page [DONE] (#71)

Review: code-design

**Goal:** all ten accounts read correctly on the page in every state, including the ones WSL holds.

Work: the full chip vocabulary (`live here`, `in use by wsl`, `in transit to wsl`, `parked`) and the
disabled-button rules for every state; the `sides[]` panel; the tee merge for WSL-held accounts
through the follower's dashboard, with `in use by wsl; figures come from wsl sessions` and a
`via snapshot` age or `unknown`; `HeldElsewhere` rendered as card text rather than a bare state word.

**Sanity Check:** a `DashboardAssemblerTests` fact per chip; a fact that a WSL-held account's figures
come from the follower's tee and carry its capture age; a fact that an account with no WSL session
since its last reset renders `unknown` rather than a stale percentage; every existing dashboard fact
still green.

**Verification:** temp roots and the two-`AppFactory` pair, as phase 4. **Rollback:** revert; the
slice's plain state word returns. **Depends on:** phase 5. **Parallelism:** with phases 7 and 8.
**Waits:** none. **Human gate:** the merge. **Estimate:** about 1 day.

### Phase 7: escape hatches and recovery ergonomics [DONE] (#72)

Review: security

**Goal:** the documented answers to "the distro is off", "something is stuck in transit", and "an
account has two families" become buttons instead of hand work.

Work: `Log in again on Windows` for a WSL-held account while the distro is off, with the `superseded`
record, the quarantine of the follower's later export under the app data `quarantine/`, and the
banner; the `foreign family` refusal at L1 and its banner; the 24-hour in-transit banner that is never
auto-cleared; `Cancel` on a stuck coordination, still only after the follower answers "not imported".

**Sanity Check:** a fact per row of design section 11 that the design says is a control; a fact that a
quarantined export is never promoted and never deleted; a fact that no path auto-clears an in-transit
state.

**Verification:** temp roots throughout; the quarantine path is exercised with fake pairs.
**Rollback:** revert; the documented hand procedures return. **Depends on:** phase 5.
**Parallelism:** with phases 6 and 8. **Waits:** none. **Human gate:** the merge, plus a go on the
escape-hatch semantics, since this is the one place the design deliberately allows a second family.
**Estimate:** about 1 day.

### Phase 8: the second machine, dotfiles, and autostart [DONE] (#73)

Review: close-out

**Goal:** the desktop runs the same thing, and neither machine needs a hand-installed follower.

Work: the desktop rollout, the same steps as phase 5 with the operator present; filed in the dotfiles
repository, not here: the follower binary under `~/.local/bin` behind the `isWsl` branch, the follower
`config.json` template with `mailbox` resolved by `wslpath` at apply time, and the leader's `peers[]`
values derived from the fleet manifest. No secret is delivered by any of these.

Amended 2026-09-22. The autostart is **not** a dotfiles shortcut. That repository is user-scope
config and carries no autostart machinery, and its remote-access plan assigns logon and scheduled
tasks to `provisioning`, where the desktop's Remote Control session already starts from one. The
leader's logon task is therefore melodic-software/provisioning#552, with a log redirect and
restart-on-failure. Two findings this phase had priced in are now their own issues rather than work
hidden inside it: #91, a shutdown route, so replacing the binary stops needing an external process
kill; and #92, a real follower log, since `wsl.exe`'s stdout is a discarded pty. #92 settles before
the dotfiles install, which places whichever shape wins.

The install had no asset to fetch: nothing in this repository published an executable, so the
tag-driven release workflow (#89) landed first and `v1.0.0` now carries
`claude-code-account-rotation-linux-x64`, `claude-code-account-rotation-win-x64.exe` and
`SHA256SUMS`. The parent plan's 5.3 scopes release assets to `win-x64` only, which predates this
chain; the WSL side runs the `linux-x64` build, so the asset set is both.

**Sanity Check:** R1 to R6 on the desktop as in phase 5; a fresh `chezmoi apply` on each machine
produces a running follower with no hand-typed path; the leader starts at logon and the page answers
without the operator launching it, which provisioning#552 delivers rather than this repository.

**Verification:** the desktop's own live acceptance, operator present, one designated account first
exactly as the laptop's. **Rollback:** as phase 5, per machine. **Depends on:** phase 5; independent
of 6 and 7. **Parallelism:** the dotfiles work is a separate repository and a separate agent lane;
the desktop rollout is not parallelizable. **Waits:** the desktop must be awake and reachable.
**Human gate:** the desktop rollout, the dotfiles PRs, and the merges. **Estimate:** about 1 day of
agent work plus 1 to 2 h at the desktop.

## Risks and mitigations

**The riskiest phase is phase 3.** It is the only phase in the chain where a credential lineage can be
**lost** rather than stranded. Everywhere else the failure mode is "the side goes offline and nothing
moves"; here F5 replaces the WSL live file, destroying A's last copy on ext4, and from that instant A
exists only as the F4 export on the 9P mailbox.

What would make it fail: the measured DrvFs fact is that `fsync` on `/mnt/c` **returns 0**, not that
the bytes are durable, and the F4 read-back through a fresh open can be served from the mount's
cache (`cache=0x5`). A host power loss in the F4-to-F5 window, with a non-durable export, loses A.
Recovery is one short Windows login for that account.

**Mitigated, by the operator's decision of 2026-09-20: the export gate is in phase 3, not deferred.**
The follower exports and stops; the leader reads the exported file **natively**, on the store's own
volume, and only then sends the commit that lets the follower swap. The follower refuses to swap
without that commit, so the check cannot be skipped by a caller that forgets it. The cost is one
extra request per WSL switch, against a hand-off that already spends tens of milliseconds per 9P
call, and it converts an unmeasured durability assumption into a per-switch verification whose
failure costs a refused switch rather than a login. The residual, now small: a native read proves
the bytes reached the Windows side's own view of the file, not that they survive a power loss in the
milliseconds between the gate and the swap. Recorded as **[A]**; revisit trigger is any observed
export that passes the gate and is later absent or short.

Second: the export-verify is the only thing between a corrupt 9P write and a swap, which is why
phase 3's acceptance names both the follower's own read-back and the gate as their own lines, and
why phase 4's acceptance corrupts an export on purpose at least once.

Third, and much smaller: the cross-side `mkdir` anomaly of design section 3. It costs nothing here
because no lock crosses the boundary, but any future change that introduces one reopens it.

Fourth, on schedule rather than on safety: phases 2 and 3 both touch the configuration pair. If the
two agent lanes reformat one another's file the merge is noisy but never dangerous; the fence is one
key set each and one validator branch each.
