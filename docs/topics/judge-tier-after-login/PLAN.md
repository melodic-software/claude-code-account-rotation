# judge-tier-after-login

Planned 2026-09-11 for the two Codex findings left open on PR #45 (`fix/judge-tier-after-login`),
both confirmed by the PR's own stopping comment. Parent topic:
`docs/topics/claude-subscription-rotation/PLAN.md` (the Enterprise seat is never in the rotation).

## Brief

### TLDR

- PR #45 judges the tier of the account that signed in at the end of a login and refuses anything
  but Max by revoking and deleting the pair. Two findings keep it unmerged: a stale recorded
  identity can vouch for an Enterprise seat (P1), and an abandoned re-login on a working account
  can delete that account's still-valid pair (P2).
- Fix both in one commit on the rebased branch, red-first tests for each and for their
  interaction, then push, answer and resolve the two threads, and squash-merge on green.

### Goal

A login that ends can only ever discard a credential pair that the login itself is proven to have
written, and the tier judgment on that pair takes no recorded identity at all, since nothing in
the folder proves which login wrote one. The guard PR #45 adds then closes the gap it was opened
for without being able to destroy a working login.

### Constraints

- The branch is `fix/judge-tier-after-login`, rebased onto `main` at `d8c7aeb` first; only
  `CHANGELOG.md` conflicts. Work happens in this checkout, no worktree, as the previous chain did.
- The live dashboard on 48211 and everything under the app-data directory are the user's live tool
  with ten real accounts: nothing here stops, restarts, reinstalls, seeds, or patches it. Tests run
  only against temp directories and the scripted CLI. No real login is started for a spike.
- `MaxTierAdmission.Evaluate` and the roster call site (`RosterEndpoints.JudgeAsync`) are not
  changed; the fix is in the post-login path.
- Repo rules hold: Conventional Commits, `.claude/rules/pr-body-contract.md`, `bash
  eng/check-no-machine-paths.sh` clean, no real address, machine path, or user name in any tracked
  file, no `git add -A`, `git checkout -- '**/packages.lock.json'` after any build that drifts them.
- Push with `--force-with-lease` (the branch is rebased); thread replies and resolution through the
  GraphQL `resolveReviewThread` mutation; squash-merge on green. Each outward action is reported.

### Acceptance criteria

- P1, recorded in `profile.json`: with `profile.json` carrying `organizationRateLimitTier`
  containing `claude_max`, a login whose scripted CLI writes the pair and a `.claude.json` with no
  `oauthAccount` block, and a status read reporting `enterprise`, ends `Failed` with a message naming
  the `"enterprise"` subscription, `auth logout` run under that folder, no `.credentials.json` left.
- P1, recorded in `.claude.json`: with a `.claude.json` already in the folder whose `oauthAccount`
  block carries `claude_max` (a previous residue-kept login), a scripted CLI that writes only the
  pair, and a status read reporting `enterprise`, the same refusal.
- P2, abandoned: with a pair already in the folder before the login starts, a scripted CLI that
  exits without writing, and a status read that fails, the session ends `Failed`, no logout runs,
  and the pair's bytes are unchanged; the message says the earlier login was left as it was.
- P2, expired: same seed, clock advanced past ten minutes, the pump's finish awaited through the
  runner's internal completion hook: the session stays `Expired`, no logout, bytes unchanged.
- Pre-existing pair rewritten but unjudgeable: a pair in the folder before the start, a scripted
  CLI that overwrites it and exits, a status read that fails: `Failed`, no logout, the folder and
  its pair left in place, the message says the tier could not be checked and the account must be
  removed from the roster before any switch.
- A re-login that completes as Max over an existing pair still ends `Completed` with the new pair
  in place (characterization, expected green before the change); the four refusal tests PR #45 added
  still pass.
- `dotnet test -c Release` green with no test lost against the rebased baseline; `dotnet build -c
  Release` 0 warnings; `dotnet format whitespace --verify-no-changes`, `typos .`, markdownlint, and
  `eng/check-no-machine-paths.sh` clean.
- Both Codex threads carry a reply and are resolved; `mergeStateStatus` is no longer `DIRTY` or
  `BLOCKED`; the PR is squash-merged.

### Captured assumptions

- `claude auth login` neither rewrites nor deletes an existing `.credentials.json` unless the
  login completes. The spike that settled the login mechanism ran on an empty folder, so re-login
  over an existing pair is unverified against the real CLI; a spike would need a real account's
  pair in a throwaway folder, which is a second holder and is not done here. If a future CLI
  refreshed the parked pair's token mid-login, the bytes would change, the pair would be judged, and
  a failing status read would land on the keep-and-tell branch rather than a deletion.
- Nothing in-process writes a parked folder's pair between a login's start and its finish today
  (`WriteParkedAsync` has no production caller). When the parked-pair refresh write-back lands
  (parent plan 2.5), it must check `IsRunningAgainst(folder)` under the gate the way switch and
  remove do, or it will read as the login's own write.
- A pair that exists at start but cannot be read is rare (the shared reader grants write and delete
  sharing); refusing to start the login is the safe answer and is not pinned by a test on Linux.

### Out-of-scope

- `POST /switch` tier guarding (PR #45 Related, first bullet).
- The mutation-gate timeout leaving a pair switchable, and the `wwwroot` code-field change after a
  `Failed` session (PR #45 Related).
- The ownership window between a session settling `Expired` and its pump taking the gate, during
  which the folder is no longer "running against" and a switch may move a pair the login wrote;
  pre-existing, recorded in the PR's Related section.
- Logging which refusal branch produced a 409 on the login-code route (carried as a follow-up).

## Plan

Standards grounding: no standards index exists at `docs/standards/` and no `.claude/standards.yaml`;
the repo's only rule file is the ambient `pr-body-contract.md`. Scale is Small (three source files,
two test files, changelog), so ecosystem defaults apply and nothing further was pulled.

### Phase 1: The credential-file digest and the identity-free judgment [DONE]

Work items, in order:

1. `git switch -c fix/judge-tier-after-login origin/fix/judge-tier-after-login`, `git rebase main`,
   resolve `CHANGELOG.md` only (both new bullets stay under the one existing `### Security` heading,
   MD024 is siblings-only), commit this topic on the branch, run `dotnet test -c Release` once for
   the rebased baseline count.
2. Red tests in `tests/.../Endpoints/LoginEndpointTests.cs`, driven through the existing
   `POST /api/accounts/{email}/login` and `POST /api/login-sessions/{id}/code` routes with the
   scripted child and `AppFactory.CannedCli`. One new boundary: an `internal Task
   FinishedAsync(LoginSessionId)` on the runner returning the session's pump task, reached from the
   test through the factory's service provider (`InternalsVisibleTo` already covers the test
   project); the expiry test cannot observe the finish any other way, because `EnforceExpiry`
   settles synchronously in the request and the pump finishes afterwards.
   - `AStaleProfileCannotVouchForASeatWhoseAdoptionFailed` (P1): hand-build `profile.json` from
     `AppFactory.AccountJson` plus `organizationRateLimitTier: "default_claude_max"`; `OnCode`
     writes the pair and a state file with no `oauthAccount`; `Cli.SubscriptionType = "enterprise"`.
     Expect `Failed`, the enterprise message, `LogoutCalls == [folder]`, pair gone. Red today: the
     block wins in `MaxTierAdmission.Evaluate`.
   - `AStaleStateFileCannotVouchForTheSeatThatSignedIn` (P1): seed `.claude.json` with an
     `oauthAccount` carrying `claude_max`; `OnCode` writes only the pair; enterprise status. Expect
     the same refusal. Red today: adoption rewrites `profile.json` from the stale block and it wins.
   - `AnAbandonedReLoginLeavesTheWorkingPairUntouched` (P2, the interaction): seed a pair with
     `CredentialFiles.WriteAsync(folder, "refresh-old")` before the start; `OnCode` calls
     `child.Exit()` and writes nothing; `Cli.ReadError` set. Expect `Failed`, the left-as-it-was
     message, `LogoutCalls` empty, pair bytes equal to the seed. Red today: the old pair is judged,
     `Unknown`, revoked and deleted.
   - `AReLoginThatExpiresLeavesTheWorkingPairUntouched` (P2): same seed, `Cli.ReadError` set,
     advance the clock ten minutes, GET the session, then await `FinishedAsync`. Expect `Expired`
     still, no logout, bytes unchanged. Red today only with the hook: the finish deletes the pair
     after the GET returns.
   - `AnUnjudgeableRewriteOfAnExistingPairIsKeptAndNamed`: seed a pair; `OnCode` overwrites the
     pair with `CredentialFiles.WriteAsync(folder, "refresh-new")` and exits; `Cli.ReadError` set.
     Expect `Failed`, no logout, the file present with the new bytes, the message saying the tier
     could not be checked and to remove the account before switching. Red today: deleted.
   - `AReLoginThatCompletesAsMaxReplacesTheOldPair` (characterization, green today): seed a pair,
     `OnCode = CompleteLoginAsync`. Expect `Completed`, no logout, the pair holding the new token.
3. Green, in `ClaudeCliLoginSessionRunner.cs`:
   - `StartAsync` records, inside the gate and before the child spawns, the SHA-256 hex of
     `.credentials.json` through `SharedFileReader` (null when absent) as
     `Session.CredentialFileDigestAtStart`; a file that exists but cannot be read
     (`IOException`, `UnauthorizedAccessException`) refuses the start with a `Failure` like the
     other refusals there. A comment beside the digest states the write-back rule from Captured
     assumptions.
   - `FinishAsync`: absent now routes into the existing pending-only `Expired`/`Failed` branch with
     the existing messages (keyed on the finish state, so a pair that existed at start and is gone
     is not called untouched). Present now takes the gate, re-reads the digest under it, and:
     equal to the start digest routes into the same pending-only branch with a message that names
     the untouched earlier login; different, or no pair at start, goes to `AdmitAsync`. A read
     failure under the gate lands on the keep-and-tell branch (`Failed`, folder untouched), inside
     the existing catch structure so nothing escapes the pump's `finally`.
   - `AdmitAsync` adopts as before (the adoption outcome only selects the success message) and
     judges with `ParkedFolderAdmission.JudgeFreshLoginAsync(folder, cli, ct)`, which runs
     `auth status --json` under the folder and evaluates it with no account block. `Refused`
     discards. `Unknown` discards only when no pair existed at start (the pair is then proven to be
     the login's own); with a pre-existing pair it keeps the folder and settles `Failed` with the
     remove-before-switching message, the disposition the gate-timeout branch already uses. Its doc
     comment says why the recorded identity is never consulted here.
   - `ParkedFolderAdmission`: `JudgeFreshLoginAsync(folder, cli, ct)` beside the roster's
     `JudgeAsync`; the class doc names both askers and why the login-time one takes no identity.
4. `CHANGELOG.md`: extend the PR's Unreleased Security bullet with the two behaviors (the guard
   discards only a pair the login is proven to have written; the tier judgment after a login
   takes no recorded identity); no Fixed entry, since nothing here was released.
5. Gates: `dotnet build -c Release` (0 warnings), `dotnet test -c Release`, `dotnet format
   whitespace --verify-no-changes`, `typos .`, markdownlint on the changed markdown, `bash
   eng/check-no-machine-paths.sh`, `git status --short -- '**/packages.lock.json'` empty. Then a
   fresh-context `implementation:phase-verifier` on the diff and the code and security review lanes.
6. Commit (one commit for the two fixes and their tests; P1 alone makes P2 worse, so they do not
   land apart), push with `--force-with-lease`, append a dated Verification subsection to the PR
   body, reply on threads `PRRT_kwDOUPhjfc6gHe90` and `PRRT_kwDOUPhjfc6gHe96`, resolve both, wait
   for the five checks and Codex's summary comment, squash-merge.

**Sanity Check:**

- `dotnet test -c Release` exit 0 with total ≥ baseline + 6 and failed 0.
- `grep -c "CredentialFileDigestAtStart" src/ClaudeCodeAccountRotation.App/Adapters/Process/ClaudeCliLoginSessionRunner.cs` ≥ 2 (recorded at start, compared at finish).
- `grep -c "JudgeFreshLoginAsync" src/ClaudeCodeAccountRotation.App/Accounts/ParkedFolderAdmission.cs src/ClaudeCodeAccountRotation.App/Adapters/Process/ClaudeCliLoginSessionRunner.cs` prints at least 1 for each file.
- `git diff main -- src/ClaudeCodeAccountRotation.Core/Accounts/MaxTierAdmission.cs` is empty.
- `gh api graphql` on PR #45 `reviewThreads` shows `isResolved: true` for both thread ids; `gh pr view 45 --json state` prints `MERGED`.

**Done:** the two fixes and six tests landed in `5be58d3` (five red first, one characterization);
`dotnet test -c Release` went from 333 on the rebased baseline to 339. A fresh-context phase
verifier confirmed every due criterion; the code and security lanes found no blocker and five small
follow-ups, folded in as `c6f3b34` with four more tests (three red first, one pin): the judgment
runs before adoption and a kept-but-unjudged folder is left whole, the finish takes the gate without
a pre-gate existence check, a fault while finishing settles the session with a fixed message, the
finish hook throws on an unknown id and a session is published with its pump, and the expiry message
the pump writes unwatched is pinned. 343 tests, 0 failed, 1 skipped; build with 0 warnings; format,
typos, markdownlint, and the machine-path check clean. Both `[FALLBACK]` dispositions stand as
written. The thread replies, resolves, PR body update, and merge are the remaining items of step 6.

## Blast radius

MEDIUM. Seven files, two of them tests and one the changelog; the production change is confined to
the login-finish path, which is the security-sensitive surface (credential deletion and revocation),
so the stress-test trigger for security-sensitive changes matches even though the diff is small and
`git revert` undoes it cleanly.

## Stress-test summary

Two fresh-context reviews ran on the first draft: a plan reviewer (0 critical, 4 important, 5
suggestions) and `/planning:devils-advocate` (0 critical, 2 high, 4 medium, 4 low). Every finding
was checked against the runner, the store, and the tests before it changed the plan:

- Confirmed and applied: `adopted == true` proves only that a state file with an account block
  existed, not that this login wrote it (a residue-kept login leaves one behind), so "identity only
  when adopted" was replaced by "no identity in the post-login judgment", with a second P1 test for
  the state-file case. The expiry test was vacuous as first written because `EnforceExpiry` settles
  in the request and the pump finishes later; the internal `FinishedAsync` hook is the named
  boundary. The finish-time digest read moved under the gate with its own catch. The pending-only
  message is keyed on the finish state. The pre-existing-pair plus `Unknown` case is decided as
  keep-and-tell, pinned by a test. `PairFingerprint` was renamed to say what it hashes. The
  characterization test is labeled as such and the PR will not claim it as red.
- Confirmed and recorded, not fixed: the real-CLI re-login spike (needs a real pair in a throwaway
  folder, a second holder), the write-back drift rule, and the expired-but-unfinished ownership
  window are Captured assumptions or Out-of-scope with their reasons.
- Checked negative by the reviewer: parking a pair into the login's folder between start and finish
  is unreachable (the switch refuses both folders while pending, and after expiry the unparked pair
  equals the start digest).

## Execution shape

Single phase, all main-session; no parallelism axis.

## Open questions

- None blocking.

## Handoff to implementation

### User-approval gates

- The squash-merge is the one outward, hard-to-reverse action; it is authorized by the PR's own
  stopping comment ("3. Merge.") and the user's "after the PR lands", and is reported when done.
- `[FALLBACK — confirm or override]` A pair that exists at start but cannot be read refuses the
  login start rather than proceeding unprovable.
- `[FALLBACK — confirm or override]` A pre-existing pair that was rewritten during the login and
  whose tier cannot be read is kept, and the operator is told to remove the account before
  switching, rather than deleted; the same disposition the gate-timeout branch already takes.

### Execution shape ([EXEC-SHAPE] tagged)

- `[EXEC-SHAPE]` The post-login judgment takes no recorded identity at all (the Codex thread's
  first option) rather than a `profileFetchedAt` stamp check: it removes the vector instead of
  trusting a CLI stamp, and the PR already fails closed on a tierless status.
- `[EXEC-SHAPE]` The digest is a SHA-256 of the file bytes rather than of the parsed refresh token:
  it needs no parse, an unparsable file is still comparable, and a token refresh rotates the token
  too, so neither survives one.
- `[EXEC-SHAPE]` One commit for both fixes and their tests, on the rebased branch, the topic
  directory committed first in its own `docs(plan)` commit.
- `[EXEC-SHAPE]` Tests live in `LoginEndpointTests` beside PR #45's four, over the real runner and
  scripted child; the one internal hook is the only new seam.

### Mechanical work

- Verification checkpoints: the Phase 1 sanity list; a fresh-context `implementation:phase-verifier`
  on the diff before the push; code and security review lanes on the diff (the surface is
  credential deletion).
- Sequential; nothing to fall back from.
