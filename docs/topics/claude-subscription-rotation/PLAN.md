# claude-subscription-rotation

Interview complete 2026-09-03 (three rounds, Q1 to Q28). Brief confirmed by the user 2026-09-03. Spikes 01 to 04 passed (usage endpoint with honest User-Agent; rate bucket measured; credential refresh with write-back; machine-wide swap under live sessions) and spike 05 (login URL routing) is answered by the interactive callback flow; write-ups are in the memory slice `.work/claude-subscription-rotation/spike-0*.md`. Ready for `/planning:plan`. The Plan section stays empty until then.

## Brief

### TLDR

`claude-code-account-rotation`: a single-executable local app with a web page that shows every one of the developer's Claude Max accounts with 5-hour and 7-day headroom, ranks them by earliest weekly reset, and switches the whole machine to the chosen account the way `/login` does today, without a browser step. Accounts are parked credential pairs; the live config dir stays `~/.claude`, so transcripts, settings, plugins, dotfiles, and every open session are untouched. Quota comes from live statusline snapshots plus an on-demand refresh that renews expired tokens itself. Nothing calls the model API outside the unmodified `claude` binary.

### Goal

Uninterrupted Claude Code work across ten personally owned Max 20x subscriptions on any machine the developer sets up, with every account's quota on one screen, a ranked queue of what to use next, one-click machine-wide switching that carries every open session, subagent, and task, and browser logins reduced to roughly once per account per month with the right browser profile opened for you.

### Constraints

- **Terms posture.** Behaviors Anthropic's terms neither prohibit nor address are acceptable when ban risk is judged minimal (Q1). Hard lines from the research: no inference with subscription tokens outside the unmodified `claude` binary; no proxying, pooling, or sharing; no synthetic requests to start or measure a window; no User-Agent imitation (the tool identifies itself honestly on every request it makes); no claiming promotional credits across accounts. Credential handling is limited to the developer's own files on the developer's own machine: pairs are moved, never copied, so one holder exists at any moment; credential refresh writes back only to Claude Code's own credential file and never stores tokens anywhere else.
- **Switch model D** (Q18). The live config dir is the one every session already uses; a switch parks the live pair into the outgoing account's folder and unparks the target pair, patching only the `oauthAccount` block of the state file (`~/.claude.json` at the home root when `CLAUDE_CONFIG_DIR` is unset, `<dir>/.claude.json` when it is set; verified on this machine 2026-09-04). The tool honors Claude Code's token-refresh lock before touching the live pair. Fallback if the spike fails: model C, one `CLAUDE_CONFIG_DIR` per account with junction-shared `projects/` and `plugins/` (Q9).
- **Refresh contract** (Q19). Tier 1: the live session's statusline snapshot. Tier 2: manual "Refresh all", one usage read per account with its existing token; on 401 the tool refreshes that parked pair's token and retries once. Tier 3: automatic refresh of the top three queue candidates only at decision points (active account crosses 80%, or a switch is about to be proposed). No timer polling. Never refresh the live account's pair while a session may be refreshing it. Every card shows "as of <time> via snapshot|refresh" and "login expires in N days."
- **Routing** (Q4, Q11). Eligible = not paused, 7-day used under 100%, 5-hour used under 90%. Order by earliest `seven_day.resets_at`. Propose a switch when the active account trips or crosses 90%. Propose a switch-back when an earlier-deadline account regains 5-hour headroom, at most once per 30 minutes, never mid-turn. Flag accounts whose weekly reset is within 24 hours and still hold headroom. The dashboard shows a ranked queue of the next three. All three numbers are settings.
- **No hard-coded assumptions, cross-platform by design** (Q16, Q22). Every path, port, threshold, and browser mapping lives in one config file with defaults derived from the user profile (never a drive letter): live dir `~/.claude` or `CLAUDE_CONFIG_DIR` when set; profiles root `~/.claude-profiles`; config and roster under the platform's per-user app-data dir. Paths, folder-name sanitization, browser launching, and credential-store access go through platform abstractions so that Windows is the first target, not the only one; macOS Keychain support itself stays deferred (Q26). First run creates the config; the repo carries the template.
- **Quota data model.** Cards are driven by the endpoint's generic `limits[]` array (kind, group, percent, severity, resets_at, scope), so session, weekly all-models, and any weekly scoped bucket such as Fable render without code changes when Anthropic adds or renames buckets; the codenamed top-level fields are never referenced by name. The usage-credits state and `refreshTokenExpiresAt` ("login expires in N days") come from the same responses and the credential store.
- **Profiles and roster** (Q17). Folder name = the account email lowercased and sanitized for the filesystem (any of `< > : " / \ | ? *` and control characters replaced by `_`; trailing dots and spaces trimmed; emails contain none of these in practice). Identity comes from `profile.json` inside the folder and from `claude auth status --json`, never from the folder name, so folders can be renamed. Roster entry: email, optional alias, browser (chrome, edge, brave), browser profile directory, paused flag, notes.
- **Browser-assisted login** (Q20). Login runs `claude auth login --email <email>` against the parked folder as its `CLAUDE_CONFIG_DIR` and opens the sign-in URL in the mapped browser profile. The one-time code remains possible; the tool cannot remove it.
- **Machines** (Q6, Q23). Laptop and desktop in scope. The app must also install and run on the work machine, which has no D drive; whether personal accounts may be switched there is gated by that machine's `/status` setting sources (a device-managed `forceLoginOrgUUID` blocks it) and by the user's choice; the Enterprise seat is never in the rotation. Machines are independent: each logs in its own pairs; refresh gives server truth anywhere; no snapshot sync.
- **Remote Control** (Q13) is off; web and cloud sessions stay per account.
- **Stack** (Q22): .NET single-file executable serving a static page plus JSON endpoints, launched from PATH or a shortcut.
- **Home** (Q15, Q21): `melodic-software/claude-code-account-rotation` (named `account-rotation` on 2026-09-04 via /naming:name-it-better; renamed 2026-09-07 through #5 so the name says which product it rotates; README tagline carries "for Claude Code"), public, MIT, provisioned through `melodic-software/github-iac`. The `rate-limit-guard` tee gains an `account` field from `claude auth status --json`, closing claude-code-plugins #1218, as a separate PR.

### Acceptance criteria

1. **Switch without browser:** with three Claude Code sessions open on account A, clicking Switch to account B makes the next message in every one of the three sessions bill account B (statusline `acct:` badge and `claude auth status --json` both report B) with no browser interaction and no session restarted.
2. **In-flight work survives:** a subagent fan-out started on A before the switch completes on B without error.
3. **Parked pairs are single-holder:** after a switch, account A's folder holds exactly one `.credentials.json` and the live dir holds exactly one; no two files ever contain the same refresh token (test: compare hashes after ten switches).
4. **Quota visibility:** Refresh all populates 5-hour and 7-day percentages and reset times for every non-paused account, including accounts idle for more than eight hours, within 60 seconds, and each card states its source and capture time.
5. **Routing:** given fixture snapshots, the queue lists accounts by earliest weekly reset among eligible ones, excludes paused and exhausted accounts, and the switch-back proposal fires at most once per 30 minutes.
6. **Login assistance:** Login for a parked account opens the sign-in URL in the roster's browser and profile with the email pre-filled, and a completed login leaves a valid pair in that account's folder.
7. **Roster operations:** add creates a folder and offers Login; pause removes an account from the queue but not the page; remove deletes the folder after an optional logout; none requires editing code.
8. **Portability:** a fresh Windows machine with only the executable, the config template, and one login reaches a working dashboard without editing any path in code, and the profiles root can be set to any writable directory.
9. **Refresh lock respected:** a switch attempted while the live dir's refresh lock file exists waits or refuses rather than moving the pair.
10. **Tee attribution:** `~/.claude/rate-limit-guard/rate-limits.json` carries an `account` field after the marketplace change, and the dashboard attributes each snapshot to that account.

### Captured assumptions

- Laptop, desktop, and work machine all run Windows 11; credentials are a file, not a Keychain (revisit trigger: any macOS machine, see Q26).
- Live Claude Code sessions re-read the credential file on their next request after it is replaced. **Verified 2026-09-04 (spike 04):** sessions started on one account reported the other account in `/status` after a park/unpark swap, with no restart; the swap round-tripped and every refresh token existed in exactly one file afterward. Model D is the plan of record; model C is no longer needed as a fallback for this reason.
- Refreshing a parked pair's token renews its four-week login (observed behavior of the credential store; revisit trigger: an account expiring despite weekly refreshes).
- The usage endpoint and the token endpoint accept an honest User-Agent at on-demand call rates. Partially verified 2026-09-03 (spike 01): a single honest-UA GET to `/api/oauth/usage` and `/api/oauth/profile` returned 200 with no rate-limit headers and exposed the Fable bucket via a generic `limits[]` array. Spike 02 (2026-09-03): the honest-UA usage bucket is about 8 reads per rolling 5 minutes per token, then a 300-second lockout, so Refresh all makes one read per account and honors Retry-After. Spike 03 (2026-09-04): the token endpoint accepted an honest-UA refresh on a parked pair (200 in 422 ms, refresh token rotated, atomic write-back, verified by a usage read); the tool must recompute `refreshTokenExpiresAt` from the response's `refresh_token_expires_in`. Remaining unverified: whether the usage bucket is per token or per client across ten accounts (revisit trigger: persistent 429 on honest calls; the answer is to reduce calls, never to imitate the CLI).
- The sign-in URL can be captured and routed to a browser profile. **Verified 2026-09-04:** `claude auth login --email` prints the full URL (with `login_hint` and `code=true`) and then a hidden code-paste prompt that rejected pasted codes on a heavily loaded machine; the interactive `claude` then `/login` flow copies the URL with `c` and completes through a localhost callback from a non-default browser profile with no paste. The Login button uses the callback flow and keeps code-paste as the documented fallback.
- Login under `CLAUDE_CONFIG_DIR=<profile>` leaves a full state tree in the profile folder (a 43 KB `.claude.json`, `settings.json`, empty `projects/` and friends); the tool ignores everything but `.credentials.json` and the `oauthAccount` block, and may prune the rest.

### Out-of-scope

- Any switch without a human action (Q1); timer-based polling (Q19).
- Rotating the Enterprise seat or reading its quota (no `rate_limits` on Team or Enterprise per docs).
- Two accounts live at once on one machine (Q25).
- macOS and Keychain credential storage (Q26).
- Claiming promotional credits across accounts.
- Any proxy, relay, token pool, or use of the model API outside the unmodified binary.

### Deferred questions

- Q25 — Two accounts live at once on one machine (a C-style pinned profile beside model D, for an autonomous loop on one account while working on another). Arbiter: USER-RESERVED; post-V1.
- Q26 — macOS support: credentials live in the Keychain keyed per config dir, so park/unpark needs Keychain handling. Arbiter: USER-RESERVED; post-V1.
- Q27 — A `StopFailure(rate_limit)` hook that pings the dashboard to surface "switch now" when a session hits a wall unattended. Arbiter: /planning:plan; post-V1.
- Q28 — Tray icon and desktop notifications. Arbiter: /planning:plan; post-V1.

## Plan

Draft written 2026-09-04 by `/planning:plan` from the Brief above, the design slice
(`design/design-threads.md`, `design/type-inventory.md`, `design/library-topology.md`,
`design/capability-matrix.md`), spikes 01 to 04, and the org standards. Status: **APPROVED by the
user 2026-09-04** with no revisions; every `[EXEC-SHAPE]` and `[FALLBACK]` decision below stands as
written. Phase tags advance `[TODO]` → `[DOING]` → `[DONE]` during implementation.

### Goal

**What:** build `claude-code-account-rotation`, a .NET 10 single-file executable serving a loopback web page that
shows every parked Claude Max account's quota, ranks them by earliest weekly reset, and switches the
whole machine to a chosen account by moving credential pairs (model D), plus a one-line writer change
in `rate-limit-guard` so statusline snapshots name their account.

**Why:** the developer today rotates ten subscriptions by `/login` in a browser each time a window
trips, loses the outgoing pair when a `/login` overwrites it (it happened again on 2026-09-04), and
has no single screen showing which account has headroom. Spike 04 proved the swap works under live
sessions; this plan turns the proven spikes into the tool.

### Standards grounding

No standards index exists in this working tree (no repository yet: resolution rung 6). The org's
`melodic-software/standards` checkout was read directly as the inference source; Phase 0 materializes
its components into the new repo through the standards sync, after which the index resolves normally.

| Surface | Sections cited | Layer provenance |
|---|---|---|
| engineering / architecture-and-design | Dependency direction; Model expected failures as results, not exceptions; Configurable by default, within reason; Testable by design | team (`standards/conventions/engineering/architecture-and-design.md`) |
| engineering / naming | Default to verbose, behavior-naming identifiers; Name by responsibility, not a vague role-suffix; Disambiguate overloaded terms; Branch names | team (`.../engineering/naming.md`) |
| engineering / simpler-code | Speculative generality; The wrong abstraction | team (`.../engineering/simpler-code.md`) |
| review / error-handling | Timeout-first; Resilience on outbound calls (retry cap, one layer retries); Atomicity: in-place file rewrite, or an atomic-replace idiom missing its durability steps | team (`.../review/error-handling.md`) |
| review / security | Secrets and credentials; Trust boundaries and injection | team (`.../review/security.md`) |
| review / cross-platform | Assumptions; Filesystem and paths; Tools, processes, and committed artifacts | team (`.../review/cross-platform.md`) |
| review / timebombs | Expiry (manual renewal in a rotation path; expiry invisible to monitoring) | team (`.../review/timebombs.md`) |
| review / testing | Coverage and level; Quality over volume; Verification honesty | team (`.../review/testing.md`) |
| review / overlays/dotnet | Captive `HttpClient`; Inject the clock (`TimeProvider`); Types match the contract (`DateTimeOffset`); Test-project placement | team (`.../review/overlays/dotnet.md`) |
| review / concurrency | Shared mutable state without synchronization; Cancellation propagation; Resource exhaustion and primitive disposal (the mutation gate's permit released in `finally`, bounded waits) | team (`.../review/concurrency.md`) |
| review / architecture | Contract evolution (an additive tee key keeps the contract version); Recorded external state (the floor text is amended at its source and its registered copies, never a seventh copy) | team (`.../review/architecture.md`) |
| review / code-design | Speculative generality; Missing abstractions; Singleton as global state (the gate is injected, not static) | team (`.../review/code-design.md`) |
| review / overlays/bash | Failure handling (`pipefail`, masked substitution failures); Cleanup on every exit path; Tests for non-trivial scripts (Phase 6 and the acceptance scripts) | team (`.../review/overlays/bash.md`) |
| components / dotnet-analysis | `Directory.Build.props` strict posture (AnalysisMode All, warnings as errors, lock files); `dotnet.globalconfig` naming and style | team (`standards/components/dotnet-analysis/`) |
| org precedent (not a standard) | medley: xunit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, `global.json` SDK 10.0.400 pinned; github-iac: `GovernedRepositorySpec` shape, `Directory.Build.props` importing `eng/dotnet-analysis` | team repos read this session |

### Build technique

Uncertainty type: integration, not viability. The viability unknowns were resolved upstream by spikes
01 to 04, so the kept slice is a **tracer bullet**: Phase 1 runs the switch end to end through the
real page, real files, and real `claude auth status`, before any quota or ranking code exists. The
one remaining feasibility unknown (the login mechanism, design thread T8) is gated by a throwaway
spike inside Phase 4, not by a phase of its own.

### Phase 0: Repository, toolchain, and first vertical test [DONE]

Review: architecture

Creates `melodic-software/claude-code-account-rotation` and a solution that builds green under the org's strict
analyzer posture, with one real behavior under test so the test lane is proven before Phase 1.

**Human-run steps** (the user's own deploys; the implementer prepares the diffs and stops):

- [x] **0.1** (github-iac#405 merged 2026-09-05 as `7bc660c`; `pulumi up` applied the same day: four
  resources created, three custom-property defaults re-asserted. Deviation
  recorded: `RequiresSecurityReview` and `UsesClaudeReview` are **false** at creation, not true as
  written below, because the synced `components/claude-lanes/` callers name the private fleet's
  runner label and runner-policy refuses them on a public repository; hosted callers arrive by
  repo-local PR and both flags flip in the same change as `RequiresCi`, Phase 5.5.) In `melodic-software/github-iac`, add a `GovernedRepositorySpec` entry
  `new("claude-code-account-rotation", a => { a.Description = "Machine-wide Claude Max account switching and quota dashboard for Claude Code: parked credential pairs, one live config dir, no browser step."; a.Visibility = "public"; a.Topics = new[] { "claude-code", "dotnet", "csharp", "windows", "developer-tools" }; a.AutoInit = true; a.SecurityAndAnalysis = SecretScanning("enabled"); }, RequiresCi: false, RequiresSecurityReview: true, UsesClaudeReview: true, VulnerabilityAlerts: true, DependabotSecurityUpdates: true)`.
  `RequiresCi` stays false until `main` emits the org's `ci-status` context (Phase 5); github-iac#409
  folded the four ci-gate callers into that single context after this item was written.
  Open the PR; the user runs `pulumi preview` and `pulumi up`.
- [x] **0.2** (standards#528 merged 2026-09-05 as `fdac203` after the App grant; the first sync PR,
  claude-code-account-rotation#1, merged the same day as `89afad3` and now owns `eng/dotnet-analysis/`. Deviations
  recorded: the two `claude-*-caller` components are **excluded** (private-only, see 0.1);
  `managed-files-guard-caller` and `typos` are **included** (every public hosted-only target carries
  both); `automerge: false` until `requires-ci` flips, since an armed sync PR would merge ungated.
  Local bootstrap: the checkout was `git init`-ed at the canonical path before the remote existed;
  0.3's clone becomes a `git remote add` plus a rebase onto the AutoInit commit.) In `melodic-software/standards`, add `melodic-software/claude-code-account-rotation` to
  `distribution/sync-manifest.yml` `targets` with the managed set github-iac uses minus
  `claude-settings-github-iac` and `runner-policy` (public repo, hosted CI only):
  `claude-review-caller, claude-security-review-caller, cloud-bootstrap, dotnet-analysis, editorconfig-checker, gitleaks, lefthook-base, lefthook-dotnet, lychee, markdownlint, node-runtime, pr-body-contract-rule, repository-text, review-instructions, shellcheck`.
  The user grants the repository in the `melodic-standards-sync` App installation UI **before** the
  manifest merge (github-iac README "Add an existing organization repository" order), then merges.
- [x] **0.3** (2026-09-05: `git remote add` plus a rebase onto the AutoInit commit `afb674d`, which
  carried only a two-line README; the skeleton landed as claude-code-account-rotation#2, squash `5a2a657`, after
  one Codex finding on the path gate was fixed in the same PR.) Clone the new repo beside the other org checkouts (`<local-repos>/melodic-software/claude-code-account-rotation`)
  and move the contract slice in: copy `<spike-dir>/docs/topics/claude-subscription-rotation/` to
  `docs/topics/claude-subscription-rotation/`, and move `<spike-dir>/.work/` to `<repo>/.work/` (its
  self-ignoring `.gitignore` travels with it; nothing under `.work/` is staged). Before the first
  commit, scrub this file and the design slice of machine paths (drive letters, user names) so the
  public repo carries placeholders only; `eng/check-no-machine-paths.sh` covers `docs/` too.

**Implementer steps** (branch `feat/solution-skeleton`):

- [x] **0.4** (2026-09-05; `Microsoft.Extensions.Http` is not pinned: the framework reference already
  carries the HTTP factory; a repository `.globalconfig` relaxes CA2007 for the Kestrel host; the App
  test project carries a `GET /healthz` round trip because the platform fails a zero-test project.)
  Solution skeleton per `design/library-topology.md`: `ClaudeCodeAccountRotation.slnx`,
  `global.json` (SDK `10.0.400`, `rollForward: disable`, `test.runner: Microsoft.Testing.Platform`),
  `Directory.Build.props` importing `eng/dotnet-analysis/Directory.Build.props` with
  `TargetFramework net10.0`, `Directory.Packages.props` pinning `Microsoft.Extensions.Http`,
  `xunit.v3.mtp-v2`, `Shouldly`, `NSubstitute`, `Microsoft.AspNetCore.Mvc.Testing` at medley's
  current versions, four projects (`src/ClaudeCodeAccountRotation.Core`, `src/ClaudeCodeAccountRotation.App` with
  `<OutputType>Exe</OutputType>` and a framework reference to `Microsoft.AspNetCore.App`,
  `tests/ClaudeCodeAccountRotation.Core.Tests`, `tests/ClaudeCodeAccountRotation.App.Tests`), `LICENSE` (MIT),
  `README.md` (tagline "Account switching and quota dashboard for Claude Code"), `CHANGELOG.md`.
  Until the standards sync PR lands, vendor `eng/dotnet-analysis/` byte-identical from the standards
  checkout so the build posture is strict from the first commit; the sync then owns the files.
- [x] **0.5** (2026-09-05, eight cases green, one rule per red-green cycle.) First red-green vertical: `ProfileFolderName.FromEmail` (Core) with tests for every
  rule in the Brief's sanitizer (forbidden characters, control characters, trailing dots and spaces,
  lowercasing, empty → `unknown`). This proves the analyzer posture, the test runner, and the CI lane
  on real behavior rather than a placeholder.
- [x] **0.6** (2026-09-05; the images are the org's explicit labels `ubuntu-24.04` and `windows-2025`;
  the user-name half of the gate reads `CHECK_NO_MACHINE_PATHS_NAMES` so the name itself never
  enters the public tree.) `.github/workflows/ci.yml`: restore (locked mode), build, test on `windows-latest` and
  `ubuntu-latest`; `eng/check-no-machine-paths.sh` (fails on `[A-Za-z]:\\`, `\\Users\\`, `/home/`,
  or the operator's user name anywhere under `src/`, `tests/`, `README.md`, `config.template.json`).
- [x] **0.7** (2026-09-05) `.gitignore` for .NET plus `.work/` (belt and braces beside the tier's own self-ignore).

**File inventory (Phase 0, new repo):**

| File | Action | Rationale |
|---|---|---|
| [x] `github-iac/GovernedRepositories.cs` | MODIFY | new governed repo entry (0.1) |
| [x] `standards/distribution/sync-manifest.yml` | MODIFY | new sync target (0.2) |
| [x] `ClaudeCodeAccountRotation.slnx` | CREATE | solution |
| [x] `global.json` | CREATE | SDK pin, MTP runner |
| [x] `Directory.Build.props` | CREATE | imports analysis overlay, TFM |
| [x] `Directory.Packages.props` | CREATE | central versions |
| [x] `eng/dotnet-analysis/Directory.Build.props`, `eng/dotnet-analysis/dotnet.globalconfig` | CREATE (vendored, then sync-owned) | strict posture |
| [x] `eng/check-no-machine-paths.sh` | CREATE | AC 8 grep gate |
| [x] `src/ClaudeCodeAccountRotation.Core/ClaudeCodeAccountRotation.Core.csproj` | CREATE | BCL-only project |
| [x] `src/ClaudeCodeAccountRotation.Core/Identity/ProfileFolderName.cs` | CREATE | sanitizer (0.5) |
| [x] `src/ClaudeCodeAccountRotation.App/ClaudeCodeAccountRotation.App.csproj`, `Program.cs` | CREATE | exe stub that starts Kestrel on loopback and serves `GET /healthz` |
| [x] `tests/ClaudeCodeAccountRotation.Core.Tests/*.csproj`, `Identity/ProfileFolderNameTests.cs` | CREATE | first tests |
| [x] `tests/ClaudeCodeAccountRotation.App.Tests/*.csproj` | CREATE | empty project compiles (carries the healthz test, see 0.4) |
| [x] `.github/workflows/ci.yml` | CREATE | build, test, path gate |
| [x] `LICENSE`, `README.md`, `CHANGELOG.md`, `.gitignore` | CREATE | repo hygiene |
| [x] `docs/topics/claude-subscription-rotation/**` | MOVE | contract slice from the spike dir |

**Sanity Check:**

- `gh repo view melodic-software/claude-code-account-rotation --json visibility,licenseInfo -q '.visibility + " " + .licenseInfo.key'` prints `PUBLIC mit`.
- In the checkout: `dotnet build -c Release` exit 0; `dotnet test` exit 0 and reports ≥ 6 passed tests (the sanitizer cases).
- `test -f eng/dotnet-analysis/dotnet.globalconfig && grep -q "TreatWarningsAsErrors>true" eng/dotnet-analysis/Directory.Build.props` exit 0.
- `bash eng/check-no-machine-paths.sh` exit 0.
- `git -C <repo> status --porcelain | grep -c "^?? .work"` prints `0` (memory tier never staged).
- `gh pr list -R melodic-software/claude-code-account-rotation --state merged --search "chore: sync standards"` shows ≥ 1 merged sync PR (may land after 0.4; not a blocker for Phase 1).
- Run 2026-09-05 after #1 and #2 merged: all six pass (`PUBLIC mit`; 9 tests on the skeleton and 115
  on the Phase 1 branch; `eng/dotnet-analysis/` blobs on `main` identical to the sync's; `.work`
  never staged; one merged sync PR).

### Phase 1: Tracer bullet, machine-wide switch through the page [DONE]

Review: security
Review: concurrency

Closed 2026-09-06 as claude-code-account-rotation#3: the 1.5a probe and the live acceptance ran on the desktop
with three sessions open (log in `tests/acceptance/README.md`); the security and concurrency
reviews' Critical and Important findings are fixed in the same PR, the Suggestions are filed as
issues #7 to #12, and a fresh-context verifier confirmed nineteen criteria at the merge head. The
sanity check below holds with the test names as built (`ACrashBetweenUnparkAndPatchIsReconciledAtStartup`,
`ConcurrentSwitchesSerializeAndLeaveOneHolderPerLineage`, `ConfigurationTests`); the
switch-and-refresh serialization case waits for Phase 2's refresh client, as 1.5b records.

The end-to-end slice: open the page, see the live account and every parked profile, click Switch,
and every open Claude Code session bills the new account on its next request (AC 1, 2, 3, 9).
Behavioral reference: `spike-04-swap.py` (memory slice), guard for guard.

- [x] **1.1** (2026-09-05) Core identity types (`design/type-inventory.md` "Identity and files"): `AccountEmail`,
  `OAuthAccountBlock` (raw `JsonObject` preserved), `CredentialPair` (no `ToString` over tokens),
  `RefreshTokenFingerprint` (SHA-256 hex), `ParkedProfile`, `LiveAccountState`, and `Result<TValue, TError>`.
- [x] **1.2** (2026-09-05; deviations: the planner takes one `SwitchPlanningInput` record carrying
  the inventory's parameters plus the profiles root and the live lineage's recorded owner;
  `LiveIdentityUnverified` is checked **first**, since an unreconciled switch invalidates every
  other answer including `AlreadyOnTarget`; a tenth refusal `MutationInProgress` is the executor's
  answer to a second concurrent switch, the plan's 409. Review on #3, 2026-09-05: a live pair whose
  account the state file does not name is refused `LiveIdentityUnverified` rather than planned with
  `Outgoing = null`, because the unpark would fail onto the live file with the journal open.) `SwitchPlanner.Plan` (pure) with the `SwitchRefusal` cases in spike 04's order plus
  three the review added: `TargetLoginExpired` (parked `refreshTokenExpiresAt` in the past),
  `SwitchingBlockedByManagedPolicy` (a device-managed `forceLoginOrgUUID` is in effect), and
  `LiveIdentityUnverified` (an open switch journal, or a state-file account that does not match the
  live fingerprint's known owner). Tests first: one test per refusal plus the happy path, plus
  "outgoing has no credentials" (post-`/login` overwrite case) yields a plan with `Outgoing = null`.
  `AccountEmail.Parse` rules stated and tested: exactly one `@`, no whitespace, no path separators,
  no control characters, 3 to 254 characters, lowercased.
- [x] **1.3** (2026-09-05; the `ManagedLoginPolicy` record lives in Core so the planner can consult
  it, its reader `ManagedLoginPolicyReader` in the App; the reader ranks sources as the current
  managed-settings docs do: HKLM `Settings` value, then `%ProgramFiles%\ClaudeCode\managed-settings.json`
  on Windows, then HKCU, reading `forceLoginOrgUUID` from the highest-ranked present source only.)
  Ports `ICredentialPairStore` and `IClaudeCliAuthStatus` (returns
  `Result<ClaudeAuthStatus, string>` carrying the failure detail, never null); `ManagedLoginPolicy`
  (reads the platform's managed settings file and, on Windows, the HKLM policy key; exposes
  `ForceLoginOrgUuid`).
- [x] **1.4** (2026-09-05; `AtomicBytesFile` is the byte-level half the state-file splice uses, the
  JSON writer serializes through it; the live-dir cross-check against `projectsDirectory` is not
  wired yet, it joins the dashboard warnings when the CLI status is read at startup in Phase 2.
  Review on #3, 2026-09-05: the state-file patch stamps the file (existence, length, last-write
  time) before the read and after the splice and writes only when the stamps agree, five attempts,
  else it fails with the journal open for startup to complete; the volume check resolves the owning
  mount point outside Windows, where a `DriveInfo` built from a path names the path itself and had
  every configuration refused on the Ubuntu lane.)
  App concrete file classes: `AtomicJsonFile` (temp file in the target directory,
  created with `UnixCreateMode` 0600 on non-Windows, `Flush(true)`, then `File.Replace` when the
  target exists or `File.Move` when it does not; retry a sharing violation with jittered backoff from
  50 ms up to 2 s total, then fail), `ClaudeStateFile` (re-read immediately before patch; locate the
  `oauthAccount` value span with `Utf8JsonReader` and splice only that span, so every other byte is
  preserved), `ProfileFolderStore` (deletes only folders it discovered through `ListAsync`, never a
  path built from a request), `FileSystemCredentialPairStore` (`File.Move` for park and unpark, same
  volume only; no copy path exists), `ClaudeCliProcessAuthStatus` (30-second timeout, arguments as
  an array, executable resolved by `ClaudeExecutableLocator`: the `claudeExecutable` config key, else
  PATH preferring a native `claude.exe` or `claude` (this desktop has the native binary), else an npm
  `claude.cmd` shim run through `cmd.exe /c` with an argument list as the one documented exception
  to Phase 4's grep; refuse with a clear message when nothing resolves). Configuration validation
  refuses a profiles root on a different volume than the live dir (`DriveInfo` mount-point
  comparison, both paths named) and a profiles root that equals, contains, or sits inside the live
  dir or the home root; it warns when the live dir the tool computed differs from the parent of the
  `projectsDirectory` that `claude auth status --json` reports (a shortcut launch without the shell's
  `CLAUDE_CONFIG_DIR`). It also refuses a profiles root under a known sync root (`%OneDrive%`,
  `%OneDriveCommercial%`, `%OneDriveConsumer%`, a `Dropbox` or `Google Drive` folder under the user
  profile): Files-On-Demand dehydrates a credential file into a placeholder, and a synced folder
  uploads refresh tokens, which is a second holder by another name.
- [x] **1.5** (2026-09-05; `LibraryImport` of `CreateDirectoryW` and libc `mkdir`; the held lock's
  mtime is not refreshed, a hold lasts milliseconds.) `OAuthRefreshLock`: the tool **participates in Claude Code's own refresh mutex**
  instead of sampling it. Verified in the installed binary (2.1.261): the CLI takes
  `<live dir>/.oauth_refresh.lock` through `proper-lockfile` (`stale: 60000`, `update: 5000`), a
  directory created with an exclusive `mkdir`, and a contending session gets the retryable
  "another process is refreshing" error, never a logout. The tool acquires the same directory with
  an atomic-exclusive create (P/Invoke `CreateDirectoryW` on Windows and libc `mkdir` on Unix;
  `Directory.CreateDirectory` is idempotent and useless here), treats an existing directory whose
  mtime is older than 60 s as stale and removes it, otherwise polls every 250 ms up to
  `RefreshLockWaitBound` (default 10 s) and then refuses with `RefreshLockPresent`; it holds the lock
  across park → unpark → patch (milliseconds) and removes the directory in `finally`. The wildcard
  `*.lock` file scan stays only as a secondary guard. D2 is closed by this item.
- [x] **1.5a** (2026-09-06, this desktop, CLI 2.1.263: **keep the patch.** One switch with
  `patchStateFile: false` and three sessions open. The credential move worked and every session
  billed the incoming account on its next request (the statusline tee's buckets went from 28 and 68
  percent to 6 and 3 percent), but nine minutes and many requests later `oauthAccount` still named
  the outgoing account with `profileFetchedAt` untouched and `claude auth status --json` reported
  the outgoing account: the binary does not re-stamp on an ordinary request. Two consequences.
  First, the page derives liveness from the state file, so it showed the incoming account as
  "needs login", and the planner would have refused the next switch (`AlreadyOnTarget` or
  `TargetHasNoCredentials`); the `patchStateFile` knob was therefore removed the same day, the patch
  is unconditional, and the risk table no longer claims a dashboard re-patch guard. Second, the first request after
  the unpark refreshed the expired access token and rotated the refresh token, so `live-owner.json`
  no longer matched the live fingerprint and the owner guard silently stopped applying; both are
  fixed in #3. A third surfaced in the post-fix live check the same evening: eight minutes after
  a switch a running session wrote its in-memory block back over the patch, naming the outgoing
  account with a `profileFetchedAt` from before the switch, so the risk table's re-patch guard is
  needed after all and is built (a state-file watcher plus every dashboard read, judged by the
  owner record's timestamp). Recovery from the probe ran through the tool's own path: a journal at step
  `Unparked` carrying the current fingerprint, then startup reconciliation patched the 90 KB state
  file in one attempt and `claude auth status` reported the incoming account.) **Probe (this
  desktop, before the first real switch):** perform one swap **without**
  patching the state file. The binary re-derives and writes `oauthAccount` itself after its next
  profile fetch (`stampAuthenticatedAccount` re-stamps on an email or uuid mismatch), so the patch
  may be redundant and it races the CLI's own write of an 88 KB file. If `claude auth status --json`
  and three open sessions report the incoming account after one message, **drop the patch** (and
  the re-patch guard) from the design and record it here; if they do not, keep the patch, performed
  under the acquired lock immediately after the credential move, and treat the CLI's later rewrite
  of the block as expected. Either outcome is recorded as a dated note under this item.
- [x] **1.5b** (2026-09-05; the parked-pair refresh before unparking waits for Phase 2's token
  refresh client; a crash after the park and before the unpark is **unwound** (the outgoing pair
  restored) rather than completed, the least surprising outcome for the user; the live lineage's
  owner is recorded in `<appdata>/state/live-owner.json`; the mutation gate wait for a switch is
  zero, so a second switch is the 409 immediately. Review on #3, 2026-09-05: the unwind acquires the
  refresh lock like every other credential move and reports a block, moving nothing, while a session
  holds it; the instance lock opens `instance.lock` with `FileShare.None` and publishes the URL in a
  sibling `instance.url`, delete-on-close on Windows only, because .NET on Unix unlinks the path at
  open and a second instance was starting on the Ubuntu lane.) `LiveDirectorySwitch` under the `CredentialMutationGate` (one `SemaphoreSlim(1, 1)`
  every credential-touching operation acquires with a timeout and releases in `finally`; a second
  concurrent switch gets 409) and the `InstanceLock` (an owner-only lock file under app data; a
  second instance refuses to start and prints the running instance's URL): snapshot live state →
  `SwitchPlanner.Plan` → acquire `OAuthRefreshLock` → write the `SwitchJournal` (intent, outgoing
  and incoming fingerprints, step reached) → if the incoming pair's access token has expired,
  refresh it while still parked (safe by construction, spike 03; it also stops N sessions racing to
  refresh at once) → park → unpark → set the unparked file's mtime to now (the CLI reloads on an
  mtime *change*, so this is belt and braces) → patch the state file if 1.5a kept it → clear the
  journal → release the lock → `claude auth status --json` → `SwitchOutcome` (a mismatch between the
  reported email and the incoming account is surfaced as a warning, never hidden). At startup and
  before every plan, an open journal is reconciled from the fingerprints on disk (each lineage sits
  in exactly one place, so the step reached is derivable), and every `.credentials.json` under the
  live dir and the profiles root is fingerprinted: a duplicate lineage is quarantined by moving the
  extra copy to `<appdata>/quarantine/` with a blocking banner, never silently deleted; until both
  reconcile, switching refuses with `LiveIdentityUnverified`. One structured log event per switch
  and per refusal; never a token.
- [x] **1.6** (2026-09-05; unknown command-line arguments pass through to the host rather than
  failing, because `WebApplication.CreateBuilder` reads `--key value` pairs and the test host passes
  its runner's own arguments to the entry point; the shipped template is the first-run file itself,
  written with the defaults resolved.) Configuration: `ClaudeCodeAccountRotationConfiguration`, `ConfigurationDefaults.ForCurrentUser()`
  (live dir from `CLAUDE_CONFIG_DIR` else `~/.claude`; state file `~/.claude.json` when unset,
  `<dir>/.claude.json` when set; profiles root `~/.claude-profiles`; app data
  `%LOCALAPPDATA%\claude-code-account-rotation` or XDG), `ConfigurationFile` (load; first run writes
  `config.template.json` content with defaults resolved at runtime), `--config`, `--port`, `--version`.
- [x] **1.7** (2026-09-05; plus a loopback-Host middleware on every route; the bundled CA2025
  analyzer crashes on the minimal-API lambdas and is off in `.globalconfig`.) Endpoints `GET /api/dashboard` (identity only in this phase: live account, parked
  profiles with `HasCredentials`, roster entries) and `POST /api/accounts/{email}/switch` (200
  `SwitchOutcome`, 409 `SwitchRefusal`), `SameOriginMutationFilter` on every mutating route.
- [x] **1.8** (2026-09-05) Page: `wwwroot/index.html`, `app.js`, `app.css` embedded; cards with email, live badge,
  "needs login" badge, Switch button; result toast; polls `GET /api/dashboard` every 10 s.
- [x] **1.9** (2026-09-05; the runbook also carries the 1.5a probe steps as its first section.) `tests/acceptance/check-single-holder.sh`: SHA-256 of `claudeAiOauth.refreshToken` in the
  live file and every `~/.claude-profiles/*/.credentials.json`; prints `files=N distinct=N duplicates=0`
  and exits 1 on any duplicate (the spike 04 hash check generalized to ten). `tests/acceptance/README.md`
  lists the human steps for AC 1, 2, 9.

**File inventory (Phase 1):**

| File | Action | Rationale |
|---|---|---|
| [x] `src/ClaudeCodeAccountRotation.Core/Result.cs` | CREATE | result type |
| [x] `src/ClaudeCodeAccountRotation.Core/Identity/{AccountEmail,OAuthAccountBlock,CredentialPair,RefreshTokenFingerprint,ParkedProfile,LiveAccountState}.cs` | CREATE | 1.1 |
| [x] `src/ClaudeCodeAccountRotation.Core/Switching/{SwitchPlanner,SwitchPlan,SwitchRefusal,SwitchOutcome}.cs` (plus `SwitchPlanningInput`, `ManagedLoginPolicy`) | CREATE | 1.2 |
| [x] `src/ClaudeCodeAccountRotation.Core/Ports/{ICredentialPairStore,IClaudeCliAuthStatus}.cs` | CREATE | 1.3 |
| [x] `src/ClaudeCodeAccountRotation.Core/Configuration/ClaudeCodeAccountRotationConfiguration.cs` | CREATE | 1.6 |
| [x] `src/ClaudeCodeAccountRotation.App/Adapters/FileSystem/{AtomicJsonFile,ClaudeStateFile,ProfileFolderStore,FileSystemCredentialPairStore}.cs` (plus `AtomicBytesFile`) | CREATE | 1.4 |
| [x] `src/ClaudeCodeAccountRotation.App/Adapters/Process/ClaudeCliProcessAuthStatus.cs` | CREATE | 1.4 |
| [x] `src/ClaudeCodeAccountRotation.App/Adapters/FileSystem/OAuthRefreshLock.cs` (P/Invoke exclusive mkdir, stale steal at 60 s) | CREATE | 1.5 |
| [x] `src/ClaudeCodeAccountRotation.App/Switching/{LiveDirectorySwitch,SwitchJournal,CredentialMutationGate}.cs` (plus `SwitchOptions`) | CREATE | 1.5b |
| [x] `src/ClaudeCodeAccountRotation.App/{Switching/InstanceLock,ManagedLoginPolicyReader}.cs`, `Adapters/Process/ClaudeExecutableLocator.cs` | CREATE | 1.3, 1.4, 1.5 |
| [x] `src/ClaudeCodeAccountRotation.Core/Identity/AccountEmail.cs` (Parse rules), `Switching/SwitchRefusal.cs` (ten cases) | MODIFY | 1.2 |
| [x] `src/ClaudeCodeAccountRotation.App/Configuration/{ConfigurationDefaults,ConfigurationFile,ConfigurationValidator,StartupArguments}.cs` (the first-run file is the template) | CREATE | 1.6 |
| [x] `src/ClaudeCodeAccountRotation.App/Endpoints/{DashboardEndpoints,SwitchEndpoints}.cs`, `Dashboard/{DashboardAssembler,DashboardViews}.cs`, `Security/{SameOriginMutationFilter,LoopbackHostMiddleware}.cs`, `Hosting/{AppComposition,StartupReconciliation,InstanceLockHolder,EmbeddedPage}.cs` | CREATE | 1.7 |
| [x] `src/ClaudeCodeAccountRotation.App/Program.cs` | MODIFY | composition root, explicit service types |
| [x] `src/ClaudeCodeAccountRotation.App/wwwroot/{index.html,app.js,app.css}` | CREATE | 1.8 |
| [x] `tests/ClaudeCodeAccountRotation.Core.Tests/Switching/SwitchPlannerTests.cs`, `Identity/*Tests.cs` | CREATE | output-based tests |
| [x] `tests/ClaudeCodeAccountRotation.App.Tests/Adapters/{AtomicJsonFileTests,ClaudeStateFileTests,FileSystemCredentialPairStoreTests,ProfileFolderStoreTests,OAuthRefreshLockTests,ClaudeExecutableLocatorTests,ClaudeCliProcessAuthStatusTests}.cs`, `Switching/{LiveDirectorySwitchTests,CoordinationTests}.cs`, `Configuration/ConfigurationTests.cs`, `ManagedLoginPolicyReaderTests.cs` | CREATE | state-based over temp dirs |
| [x] `tests/ClaudeCodeAccountRotation.App.Tests/Endpoints/SwitchEndpointTests.cs` (with `AppFactory`) | CREATE | `WebApplicationFactory`, doubles for the two ports |
| [x] `tests/acceptance/check-single-holder.sh` (takes `<profiles-root> <live-dir>` as arguments), `tests/acceptance/check-live-identity.sh`, `tests/acceptance/README.md` | CREATE | 1.9 |

**Sanity Check:**

- `dotnet test` exit 0; `grep -c "SwitchRefusal\." tests/ClaudeCodeAccountRotation.Core.Tests/Switching/SwitchPlannerTests.cs` ≥ 9; `AccountEmailTests` has one test per Parse rule (≥ 6).
- `FileSystemCredentialPairStoreTests.TenSwitchesLeaveEachRefreshTokenInExactlyOneFile` passes (asserts `distinct == files` and no duplicate fingerprints after 10 alternating switches across 3 temp profiles).
- `ClaudeStateFileTests.PatchChangesOnlyTheAccountSpan` passes (fixture is a ≥ 90 KB state file with hundreds of project entries and non-ASCII strings; every byte outside the `oauthAccount` value span is identical before and after; no `*.tmp` remains) and `PatchSurvivesAnExternalRewriteBetweenReadAndReplace` passes (a simulated CLI rewrite of an unrelated key between the tool's read and replace is not lost: the tool re-reads under the lock and re-applies). Both are moot if 1.5a drops the patch; record which.
- `OAuthRefreshLockTests`: acquire creates `<live>/.oauth_refresh.lock` as a directory and removes it on dispose; a fresh directory held by a simulated CLI (mtime now) makes the switch wait then return `RefreshLockPresent` after the bound; a directory with mtime 61 s ago is stolen; two tool-side acquisitions serialize.
- `LiveDirectorySwitchTests.StartupQuarantinesADuplicateLineage`: two files with the same refresh-token fingerprint → one moved to quarantine, banner set, switching refuses until cleared.
- `LiveDirectorySwitchTests.CrashBetweenUnparkAndPatchIsReconciledAtStartup`: a journal left at step `unparked` with the state file still naming the outgoing account is reconciled (state file patched to the incoming account, journal cleared); a plan requested before reconciliation returns `LiveIdentityUnverified`.
- `LiveDirectorySwitchTests.ConcurrentSwitchAndRefreshSerialize`: a switch and a parked-pair refresh started together complete one after the other with `duplicates=0` on the fingerprints.
- `ConfigurationFileTests`: profiles root on another volume → refused naming both paths; profiles root inside the live dir → refused; a second instance against the same app data → refused with the running URL.
- `SwitchEndpointTests`: `POST /api/accounts/{email}/switch` without `X-Claude-Code-Account-Rotation` → 403; with a cross-site `Origin` → 403; with a non-loopback `Host` → 400; with a fresh `.oauth_refresh.lock` directory in the temp live dir → 409 `RefreshLockPresent`; with a parked pair whose login expired → 409 `TargetLoginExpired`; two concurrent switch requests → one 200 and one 409.
- Live acceptance on this desktop (human observes, recorded in `tests/acceptance/README.md` log): with **three** sessions open, click Switch; `/status` in all three shows the new email after one message; `claude auth status --json | jq -r .email` matches; `bash tests/acceptance/check-single-holder.sh <profiles-root> <live-dir>` prints `duplicates=0` and exits 0; a subagent fan-out started before the switch completes without error (AC 2); then run several turns in an old session and start and stop a fresh session, and `jq -r .oauthAccount.emailAddress <live-state-file>` still prints the incoming email (state-file drift probe); once per acceptance run, `bash tests/acceptance/check-live-identity.sh` makes a single honest-UA `GET /api/oauth/profile` with the live access token and prints the billed email, which must match.

### Phase 2: Quota reads and parked-pair credential refresh [DOING]

Review: security

Cards show 5-hour, 7-day, and any scoped bucket (Fable) for every account, with source and capture
time, plus login expiry (AC 4). Behavioral references: `spike-usage-probe.py`,
`spike-02-rate-bucket-probe.py`, `spike-03-credential-refresh.py`.

- [ ] **2.0 Spike 02b (this desktop, before 2.5 locks the pacing):** run
  `spike-02-rate-bucket-probe.py` against two tokens in one window (the live pair and the parked
  gmail pair; refresh the parked pair first through the spike 03 path if its access token has
  expired). Record in `.work/claude-subscription-rotation/spike-02b-bucket-keying.md` whether the
  8-per-5-minute bucket is keyed per token or shared per client. **Per token:** AC 4's 60-second
  bound stands for ten accounts. **Shared:** AC 4 cannot hold for more than about eight idle
  accounts in one window; route the re-scoping ("populated across successive windows, paced, with
  lockouts shown per card") through `/planning:plan review` because it changes a locked criterion.
- [x] **2.1** Core quota types and `UsageResponseParser` (reads `limits[]` and `extra_usage` only).
  Fixture `usage-response-spike01.json` reproduces spike 01's shape including the codenamed null
  buckets. Tests: three limits parsed; `weekly_scoped` carries `ScopeDisplayName == "Fable"`; an
  unknown `kind` still parses as `LimitKind.Unknown`; a body with no `limits` → failure result.
- [x] **2.2** `StatuslineSnapshot` + `RateLimitGuardTeeFileReader` (tolerates a missing file, torn
  JSON, missing `rate_limits`, and reads `account.email` when present).
- [x] **2.3** `RefreshBudget` (`TimeProvider`-driven sliding window: 6 reads per account per 5 min,
  60-second minimum gap, lockout until `Retry-After`). The first 401 on a reservation refunds it and
  does not start the gap clock, so the single retry after a credential refresh rides the original
  reservation; a second 401 on the same reservation is not refunded (PR #15 security review).
- [x] **2.4** Ports `IUsageEndpointClient`, `ITokenRefreshClient`; adapters
  `AnthropicUsageEndpointClient` and `ClaudeOAuthTokenRefreshClient` via `IHttpClientFactory` typed
  clients, 20-second timeout, `User-Agent: claude-code-account-rotation/<version> (+https://github.com/melodic-software/claude-code-account-rotation)`,
  `anthropic-beta: oauth-2025-04-20` on the usage read, Claude Code's public client id on the refresh
  as spike 03 did. Errors map to `UsageReadFailure`; `Retry-After` parsed from the 429.
- [x] **2.5** `QuotaRefresh` under the `CredentialMutationGate`: Refresh all iterates non-paused
  accounts with one-second spacing, plus any paused account whose login expires within 7 days (a
  paused pair's 28-day login must never lapse silently); on 401 for a **parked** pair, refresh once
  and write back (rotated `refreshToken`, `accessToken`, `expiresAt`, and `refreshTokenExpiresAt`
  recomputed from `refresh_token_expires_in`) through `ICredentialPairStore.WriteParkedAsync` as a
  compare-and-swap on the parked file's fingerprint (abort loudly if the file moved or changed since
  it was read), then retry the read once. Because the old refresh token is consumed the moment the
  token endpoint answers 200, a failed write-back never discards the rotated pair: retry with
  jittered backoff up to 5 s, then persist it to an owner-only `<appdata>/recovery/<folder>.credentials.json`,
  raise a blocking card error, and restore it at the next start (or on demand) once the folder is
  writable. The live pair is read with its current access token and never refreshed here (a 401 on
  the live pair shows "session will refresh" on the card). Per-account results including lockouts.
  `UsageSnapshotCache` persists the latest snapshot per account.
- [x] **2.6** Endpoints `POST /api/refresh`, `POST /api/accounts/{email}/refresh`; dashboard cards
  gain bars per `UsageLimit`, "as of <time> via snapshot|refresh|cached", "login expires in N days"
  (from `LoginExpiresAt`), "rate limited, retry in N s" state, and the usage-credits state line.

Landed (2026-09-12): 2.1 to 2.4 shipped together in PR #15 and were never ticked; 2.5 and 2.6
shipped in #52 (`docs/topics/usage-cards/PLAN.md`, its `DEVIATIONS.md` for every departure from
the text above). What moved: "login expires in N days" is #49's; the pass runs in a hosted
background worker started by the routes rather than inside the request; the recovery file is
restored at start and on a per-card refresh, and a stranded folder refuses a switch server-side.
The phase stays `[DOING]` for 2.0 alone.

**Sanity Check:**

- `.work/claude-subscription-rotation/spike-02b-bucket-keying.md` exists and its first line matches `^# Spike 02b .* (PER-TOKEN|SHARED)$`. Scope-change note 2026-09-12 (#52): still open; #52 shipped without it. The pass is safe under both answers (stop on the first 429, honour `Retry-After` for every read, keep unread accounts first in line, skip the doomed read when the access token is already expired) and the pass log records reads in the window, so the keying can be settled from the log as well as from the spike.
- `dotnet test` exit 0; `grep -rn "seven_day_opus\|seven_day_sonnet\|tangelo\|nimbus_quill\|cinder_cove" src/ | wc -l` prints `0`.
- `AnthropicUsageEndpointClientTests`: 200 → success; 429 with `Retry-After: 300` → `RateLimited` with `RetryAfter == 300 s` and exactly one request sent.
- `QuotaRefreshTests.ParkedPairUnauthorizedRefreshesOnceThenRetriesOnce`: fake handler records exactly one token POST and exactly two usage GETs; written-back file has a new `refreshTokenExpiresAt` equal to `now + refresh_token_expires_in` and untouched sibling keys.
- `QuotaRefreshTests.LivePairIsNeverRefreshed`: 401 on the live pair produces zero token POSTs.
- `RefreshBudgetTests`: seventh reserve within 5 minutes is denied; a reserve within 60 s of the last successful read is denied; `UnauthorizedResponseDoesNotStartTheGapClock` allows the retry 2 s after a 401.
- `QuotaRefreshTests.WriteBackFailureParksTheRotatedPairInRecovery`: with the parked file made read-only after the token endpoint answers, the recovery file exists with the new fingerprint, the card reports a blocking error, and the next start restores the pair and deletes the recovery file.
- `AtomicJsonFileTests.CreatesOwnerOnlyFilesOnUnix` (Linux CI leg): target mode `600`, no temp residue; `ReplacesAbsentTargetByMove` on both legs.
- `QuotaRefreshTests.PausedPairNearLoginExpiryIsRefreshed`: a paused pair 5 days from login expiry gets one token POST; one 20 days out gets none.
- Live acceptance (human): with ≥ 2 parked accounts, one idle > 8 h, click Refresh all; every card populated within 60 s (wall clock) with source, capture time, and "login expires in N days"; compare one card to that account's claude.ai Settings > Usage. Scope-change note 2026-09-12 (#52): "login expires in N days" moved to #49; the 60-second bound is not promised while the bucket keying is unknown, since under a shared bucket a pass populates about eight cards and the rest report "rate limited, retry in N s" and lead the next pass (converging in three passes over ten accounts). Running spike 02b per-token restores the bound as written. The runbook step is in `tests/acceptance/README.md`.

### Phase 3: Ranking, queue, and switch proposals [TODO]

The ranked queue of three, the switch and switch-back proposals, the 24-hour urgency flag, and the
decision-point auto-refresh (AC 5). Pure functions over `AccountStanding` rows.

- [ ] **3.1** `RoutingPolicy` bound from `config.json` with the Brief's defaults (90, 90, 100, 30 min,
  24 h, 3, 80).
- [ ] **3.2** `AccountRanking.Rank`: tests for paused excluded, 7-day at 100 excluded, 5-hour at 90
  excluded, live account included when eligible, order by earliest `WeeklyResetsAt` (accounts with
  no snapshot sort last and are flagged "no data"), truncation to `QueueLength`, urgency flag when
  the reset is within 24 h and headroom remains.
- [ ] **3.3** `SwitchAdvisor.Evaluate`: proposal `ActiveNearLimit` at ≥ 90, `ActiveTripped` at 100
  or when `~/.claude/rate-limit-guard/stop-events.jsonl` (the reader contract's documented reactive
  file, read by `RateLimitGuardStopEventsReader`) holds a `StopFailure` record newer than the latest
  snapshot, `SwitchBackAvailable` when an earlier-deadline account regained 5-hour headroom and the
  last such proposal is older than the cooldown; none when the queue is empty. The cooldown clock and
  the last-seen trigger state persist in `<appdata>/state/advisor-state.json` (`AdvisorState`, via
  `AtomicJsonFile`) so a restart does not reset them.
- [ ] **3.4** Tier-3 auto-refresh in `QuotaRefresh` is **edge-triggered**: it fires only on the
  active account's transition into `AutoRefreshTriggerPercent` (last-seen state in `AdvisorState`)
  or when a proposal first appears or changes kind; it refreshes the top three candidates, skipping
  any read within 60 s, under `RefreshBudget`. A dashboard read with unchanged state sends nothing,
  so the page's 10-second poll can never turn into endpoint polling.
- [ ] **3.5** Dashboard: queue panel (three rows with deadline, headroom, urgency), proposal banner
  with a Switch button (advice only; the click is the human action).

**Sanity Check:**

- `dotnet test` exit 0; `AccountRankingTests` and `SwitchAdvisorTests` cover every bullet above (grep the test names: `Paused`, `SevenDayExhausted`, `FiveHourAtThreshold`, `OrdersByEarliestWeeklyReset`, `TruncatesToQueueLength`, `FlagsUrgentWeeklyReset`, `SwitchBackRespectsCooldown`, `NoProposalWhenQueueEmpty` each match ≥ 1 test method).
- `QuotaRefreshTests.DecisionPointRefreshesTopThreeOnly`: with five eligible accounts and the active crossing to 81 percent, exactly three usage GETs are sent, and none for an account read 30 s earlier.
- `QuotaRefreshTests.UnchangedStateSendsNoUsageReads`: 360 dashboard reads over a simulated hour with the active account steady at 85 percent send zero usage GETs.
- `SwitchAdvisorTests.CooldownSurvivesRestart`: a switch-back proposal at T, a new advisor built from the persisted state at T + 10 min, no second proposal.
- `grep -rn "System.Threading.Timer\|PeriodicTimer\|Task.Delay" src/ClaudeCodeAccountRotation.App/Quota/ | wc -l` prints `0` (no timer path in the refresh code). Scope-change note 2026-09-12 (#52): `RefreshBudget` refuses and never waits; the one-second spacing is an injected delay delegate on `QuotaRefresh`, registered in `AppComposition`, so tests pace through a recorder and the grep stays green.

### Phase 4: Roster operations and browser-assisted login [TODO]

Review: security

Add, pause, remove, adopt-live, alias and browser mapping, and the Login button (AC 6, 7). The first
work item settles the login mechanism (design thread T8).

- [ ] **4.1 Spike 05b** (throwaway, this machine, recorded in
  `.work/claude-subscription-rotation/spike-05b-piped-login.md`): run
  `CLAUDE_CONFIG_DIR=<empty temp folder> claude auth login --email <parked email>` with stdin and
  stdout piped from a small script; capture the printed URL; complete the browser step in the mapped
  profile; write the displayed code to the child's stdin followed by a newline. Record: did the URL
  print, did the prompt read a piped line, was the code accepted, did `.credentials.json` appear.
  **PASS** → mechanism (a) piped login is primary. **FAIL** → mechanism (b): the tool opens a
  terminal (`wt` or the platform default) running `claude` under the profile's `CLAUDE_CONFIG_DIR`
  with on-page instructions (`/login`, `c` to copy, paste the URL into the dashboard, which appends
  `login_hint=<email>` (the parameter the `--email` flag sets) and opens it in the mapped profile);
  the localhost callback completes it. If `login_hint` is not honored on that URL, AC 6 is met
  except for the pre-fill under (b), and the approval record says so.
- [ ] **4.2** `ILoginSessionRunner` + `ClaudeCliLoginSessionRunner` per the 05b verdict; in-memory
  login sessions with a 10-minute expiry; the process is killed on expiry or cancel. On completion,
  rewrite `profile.json` from the folder's freshly written `.claude.json` `oauthAccount` block before
  pruning the login residue, so a stale block never wins over the new login.
- [ ] **4.3** `IBrowserLauncher` + `ChromiumFamilyBrowserLauncher`: resolves the executable from
  `config.json` overrides, then platform-known install paths for Chrome, Edge, Brave; arguments
  `--profile-directory=<dir>` and the URL, passed as an argument array (never a shell string).
- [ ] **4.4** Roster: `RosterEntry`, `Roster`, `RosterFile` (atomic write); add and adopt-live admit
  only Max accounts (`subscriptionType == "max"` from `claude auth status --json` run under the
  folder, or an `organizationRateLimitTier` containing `claude_max` in the account block); a Team or
  Enterprise account is refused with the reason, since the Enterprise seat is never in the rotation
  and exposes no `rate_limits`. Endpoints
  `POST /api/accounts` (creates the folder, returns the card with "needs login"),
  `PATCH /api/accounts/{email}` (pause, alias, browser, notes), `DELETE /api/accounts/{email}?logout=`
  (optional `claude auth logout` run with `CLAUDE_CONFIG_DIR=<folder>`, then delete the folder;
  refused for the live account), `POST /api/accounts/{email}/adopt-live` (adds the live state
  file's account to the roster; the dashboard offers it whenever the live email is not on the roster).
- [ ] **4.5** Login endpoints per `design/type-inventory.md` "Routes"; page gains Add, Pause, Remove,
  Login, Adopt buttons and the login code field (mechanism a) or the URL paste field (mechanism b).

**Sanity Check:**

- `.work/claude-subscription-rotation/spike-05b-piped-login.md` exists and its first line matches `^# Spike 05b .* (PASS|FAIL)$`.
- `dotnet test` exit 0; `RosterEndpointTests`: add → `Directory.Exists(<profiles>/<sanitized>)`; pause → `RosterFile` round-trips `Paused == true` and the card renders the paused state (exclusion from the ranked queue is asserted by Phase 3's `AccountRankingTests` `Paused` case, since the queue lands at 3.5); remove → folder gone; remove on the live email → 409; adopt-live → roster contains the live email.
- `ChromiumFamilyBrowserLauncherTests`: recorded arguments equal `["--profile-directory=Profile 3", "<url>"]` for Chrome with profile `Profile 3`.
- `grep -rn "UseShellExecute = true" src/ClaudeCodeAccountRotation.App/Adapters/` prints nothing, and
  `grep -rn "cmd.exe" src/ClaudeCodeAccountRotation.App/Adapters/` names `cmd.exe` in code on one
  line only, `ClaudeExecutableLocator.cs`'s `Path.Combine(directory, "cmd.exe")` (the documented
  npm-shim exception); every other hit is a comment pointing at that exception. No other adapter
  shells out.
- Live acceptance (human): Login for a parked account opens the roster's browser and profile with the email pre-filled; after completion `CLAUDE_CONFIG_DIR=<folder> claude auth status --json | jq -r .email` prints that email and the card leaves "needs login" (the "login expires in N days" card text is 2.6 and is asserted in Phase 2's live acceptance).

### Phase 5: Packaging, portability, and release [TODO]

One executable, a config template, one login, working dashboard on a fresh Windows machine (AC 8).

- [ ] **5.1** Publish profile: `dotnet publish src/ClaudeCodeAccountRotation.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64`; also build (not publish) for `linux-x64` and `osx-arm64` in CI to keep the code portable.
- [ ] **5.2** First-run behavior: missing `config.json` → written from the embedded template with
  runtime-resolved defaults, message printed with the path; `--help` documents every flag; the
  process prints the dashboard URL and opens nothing automatically.
- [ ] **5.3** `.github/workflows/release.yml`: on tag `v*`, publish and upload
  `claude-code-account-rotation-win-x64.exe` plus `config.template.json` as release assets; `README.md` install
  section (download, place on PATH or make a shortcut, run once, add accounts) and a posture
  section that states plainly: every switch is a human click; the tool never calls the model API;
  it reads the undocumented usage endpoint with its own User-Agent, which the research rates GRAY
  (no Anthropic statement either way), not ALLOWED; the parked-pair refresh presents Claude Code's
  public OAuth `client_id`; running the loop lanes (`work-loop`, `babysit-loop`, `attend-queue`)
  while rotating accounts is outside V1 because reader-side invalidation of a latched window is
  still #1218's open half.
- [ ] **5.4** Flip `RequiresCi: true` in github-iac once `main` emits the org's `ci-status` context
  (github-iac#409 folded the four ci-gate callers into that single context; the pr-contract
  composite runs as its steps) (human-run `pulumi up`).
- [ ] **5.5** Laptop install (human): follow the README on the laptop; reach a working dashboard
  with one login and no code edit; set `profilesRoot` to a non-default writable directory and confirm
  it is honored.

**Sanity Check:**

- Publish exit 0; `ls publish/win-x64/*.exe | wc -l` prints `1`.
- `publish/win-x64/claude-code-account-rotation.exe --version` prints the assembly version; `claude-code-account-rotation.exe --config <temp>/config.json --port 0` creates the file (`test -f`) and `curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:<printed port>/api/dashboard` prints `200`.
- `bash eng/check-no-machine-paths.sh` exit 0; `grep -c '"profilesRoot"' src/ClaudeCodeAccountRotation.App/config.template.json` prints `1` and `grep -c ':\\\\' src/ClaudeCodeAccountRotation.App/config.template.json` prints `0`.
- `gh release view <tag> -R melodic-software/claude-code-account-rotation --json assets -q '.assets[].name'` lists `claude-code-account-rotation-win-x64.exe` and `config.template.json`.
- Laptop (human): dashboard reachable; `profilesRoot` override honored; recorded in `tests/acceptance/README.md`.

### Phase 6: `rate-limit-guard` tee `account` field (separate repository and PR) [DONE]

Review: security

Writer-side identity in the tee snapshot so the dashboard attributes each statusline snapshot to an
account (AC 10; design thread T14). Repository `melodic-software/claude-code-plugins`, branch
`feat/rate-limit-guard-tee-account-field`, one PR referencing #1218. Fully independent of Phases 1
to 5 except that Phase 2's `RateLimitGuardTeeFileReader` already parses the key when present.

- [x] **6.1 Pre-flight consumer check (first):** (2026-09-05, worker W6; the six registered carriers
  matched the floor before and after the change, and the full reader list is in the PR body.) `bash scripts/check-loop-lane-floor-drift.sh --check`
  prints the registered carriers (three lane `SKILL.md` bodies, the `extract-ssot` orchestrated mode,
  two launch-prompt templates) and `grep -rn "rate-limits.json" plugins/ docs/ prompts/ scripts/`
  lists every other reader. Record them in the PR body. The "Operable floor" staleness bullet states
  "the file carries **no account-identifier field**"; this change makes that false, so the bullet is
  amended in the source and in every registered carrier in the same PR (the drift gate is what proves
  the six copies moved together). Measured on this desktop 2026-09-04 for the mechanism choice: state
  file 88 KB; bash `$(<file)` plus parameter-expansion extraction 3.6 to 4.0 s (unusable); `jq -r`
  over stdin 35 ms; `claude auth status --json` 175 ms.
- [x] **6.2** (2026-09-05, worker W6; deviations: the batch jq pass emits a fifth line as well, whether
  the chosen body already carries an account-shaped key, decided with `keys_unsorted` rather than a
  substring test over the body; and every jq token line is CR-stripped in `_rlg_absorb_jq_lines`
  because a native Windows jq terminates lines with CRLF and only the last line was ever clean, a
  latent defect the two new lines exposed, fixed with the payload line left byte-for-byte intact;
  the non-spool paths, `RLG_TEE_ASYNC=1` and bash below 4.2, write no `account` key since they have
  no spool file to date the state file against. Codex review on claude-code-plugins#3778: the
  staleness test now requires the spool record to be strictly newer than the state file, so equal
  timestamps omit on coarse-mtime filesystems, and the value is validated on codepoints inside jq
  before bash command substitution can strip a NUL or a trailing newline, with the bash checks kept
  as a second layer.) `plugins/rate-limit-guard/scripts/statusline-tee.sh`, **drain path only** (the render
  path stays fork-free): after the batch jq chooses the record, resolve the state file
  (`$CLAUDE_CONFIG_DIR/.claude.json` when set, else `$HOME/.claude.json`) and read the identity with
  one `jq -r '.oauthAccount.emailAddress // empty' < "$state_file"` (bash opens the file, so the
  Windows MSYS-path limitation does not apply; one extra process per 30-second drain, measured at
  35 ms). Validate the value (`@` present, no `"` or control characters, 3 to 254 characters). Emit
  `account: { "email": "<value>" }` at the top level only when (i) the chosen record's stdin payload
  carried no `account*` key (a stdin key always wins; the writer injects only when absent) and (ii)
  the state file is not newer than the chosen record's spool file (`[[ "$state_file" -nt "$spool/$shard.json" ]]`,
  a builtin test): a state file modified after the observation means a switch may have happened
  since, so the identity is omitted rather than mislabeled. To make (ii) possible the batch jq pass
  also emits the chosen shard name as a fourth line. Absent, malformed, or stale → no `account` key,
  statusline output unchanged. Header comments updated to match.
- [x] **6.3** (2026-09-05, worker W6; 138 assertions pass on this desktop, twelve name `account.email`,
  two guard the CRLF strip; control characters and the 254-character bound are covered by code, not
  by a fixture.) `scripts/statusline-tee.test.sh` cases (`set -o pipefail`, temp cleanup on every exit
  path per the bash overlay): state file present → `account.email` equals it; absent → key absent;
  `CLAUDE_CONFIG_DIR` set → `<dir>/.claude.json` read; malformed value → key absent and wrapped
  statusline stdout byte-identical; stdin payload carrying `account_id` → passthrough wins and no
  injected `account`; state file touched after the spool record → key absent; the existing zero-fork
  render trace case still passes.
- [x] **6.4** (2026-09-05, worker W6; version `0.7.33` → `0.8.0`; the §6 paragraph changed is the
  account-identity one, which precedes the guard-mode telemetry paragraph that actually closes §6.
  Two deviations forced by that repository's gates: its changelog-parity gate requires a bump and an
  entry for every plugin whose files change, so the three carrier plugins moved too (docs-hygiene
  `0.21.38`, source-control `0.55.55`, work-items `0.39.64`, one bullet each), and the plugin README
  is a purged surface, so its new sentences carry no em dashes. One deviation from the wording below:
  the `TODO(#1218)` pointers were replaced with present-tense statements of what is not built,
  because #1218 is closed as not planned and claude-code-plugins#3770 had removed that pointer for
  exactly that reason; the PR body cites the issue once as lineage.) Docs in the same PR: `reference/reader-contract.md` amends the floor's staleness bullet
  ("the file carries an `account.email` field when the writer could attribute the observation; a
  write is still the signal that the windows changed under you") and documents the key, its source,
  the staleness guard, and the recheck trigger (`oauthAccount.emailAddress` is internal CLI state)
  under "Tee file shape" with the untrusted-value rule intact; every registered carrier's inlined
  floor block updated identically; `docs/conventions/loop-lane/README.md` §6 final paragraph
  updated from "designed elsewhere" to "writer-side identity landed; reader-side invalidation and the
  lane-floor re-audit remain #1218 follow-up"; plugin `README.md` multi-account paragraph updated;
  `CHANGELOG.md` entry; plugin version bump per the marketplace convention.
- [x] **6.5** (2026-09-05: claude-code-plugins#3778, opened by the main session after a fresh-context
  verifier passed all eleven criteria; #1218 is closed as not planned, so the body cites it as `Refs`
  with a `No related issue:` line rather than a closing keyword. Two Codex findings fixed and every
  CI lane green; squash-merged 2026-09-06 as claude-code-plugins#3778, rate-limit-guard `0.8.0` is
  live on the marketplace `main`. Phase 6 stayed DOING for 6.6, which landed 2026-09-10 with Phase 2 here; the heading is `[DONE]`.) Open the PR with `/source-control:pull-request`; body references #1218, lists the
  consumers from 6.1, carries the timing numbers, and names the reader-side follow-up as out of scope.
- [x] **6.6** (2026-09-10: landed with Phase 2's `DashboardAssembler`;
  `DashboardAssemblerTests.PreSwitchWindowsAreUnattributed` and
  `RateLimitGuardTeeFileReaderTests.ParsesAccountEmailWhenPresent` pass on `main`; the same day the
  work machine's card showed the snapshot attributed to the tee's `account.email` after
  rate-limit-guard 0.8.9's drain.) In this repo, `DashboardAssembler` treats a tee snapshot for the live account as
  unattributed ("pre-switch windows") when its `resets_at` values equal the outgoing account's last
  known values after a switch the tool performed, until the first snapshot whose values differ.

**Sanity Check:**

- `bash plugins/rate-limit-guard/scripts/statusline-tee.test.sh` exit 0 and `grep -c "account.email" plugins/rate-limit-guard/scripts/statusline-tee.test.sh` ≥ 5.
- `bash scripts/check-loop-lane-floor-drift.sh --check` exit 0 with the amended bullet (all registered carriers compared and equal).
- `git diff main -- plugins/rate-limit-guard/reference/reader-contract.md | grep -c "no account-identifier field"` prints `1` (the old sentence removed exactly once) and `grep -c 'account.email' plugins/rate-limit-guard/reference/reader-contract.md` ≥ 2.
- `grep -c "designed elsewhere" docs/conventions/loop-lane/README.md` prints `0`.
- `gh pr view <n> -R melodic-software/claude-code-plugins --json body -q .body | grep -c "#1218"` ≥ 1.
- In this repo: `RateLimitGuardTeeFileReaderTests.ParsesAccountEmailWhenPresent` and `DashboardAssemblerTests.PreSwitchWindowsAreUnattributed` pass, and the live dashboard card for the live account shows "via snapshot" attributed to the tee's `account.email` (AC 10).

### Alternatives considered

| Alternative | Why rejected | Switch condition (what would make it the better choice) |
|---|---|---|
| Model C: one `CLAUDE_CONFIG_DIR` per account with junction-shared `projects/` and `plugins/` | Model D reproduces the "Login tab" behavior every open session follows, needs no junctions or settings duplication, and spike 04 proved live sessions follow the credential file | Spike-04 behavior regresses in a CLI release (sessions pin credentials in memory), or Q25 (two live accounts) is promoted |
| Ship the four Python spike scripts as the product | Brief Q22 chose a .NET single-file executable with a web page; the scripts have no UI, roster, or ranking, and Python is not on the work machine | The user reverses Q22 |
| One project instead of Core plus App | Core's pure functions (planner, parser, ranking) are the tested heart; a project boundary makes the dependency direction compiler-checked at zero runtime cost | Core stays under about 400 lines after Phase 3 |
| Login by driving the interactive `claude` TUI through a pseudo-terminal | Fragile on Windows ConPTY; mechanism (a) needs no terminal and (b) needs no automation | Both (a) and (b) fail on a calm machine |
| Tee reads `claude auth status --json` at drain time | One node process every 30-second drain on a machine the tee authors kept fork-free; the state-file read is zero-fork and the same source the swap patches | `oauthAccount.emailAddress` disappears from the state file |
| Native AOT publish | Adds trimming and reflection constraints for no V1 benefit; single-file self-contained is enough for "one executable" | Startup time or size becomes a complaint |
| Timer-based background refresh | Out of scope by Brief (Q19) and would spend the honest-UA bucket | Never (Brief decision) |
| Copy credential blobs between slots (community switchers) | Single-use refresh-token rotation logs out the second holder; moving is the whole trick | Never (terms and mechanics) |
| Interfaces for every file class ("ports everywhere") | One implementation each and the file system is a managed dependency; Khorikov and the org's code-design bar both call it speculative generality | A real second implementation appears (the Brief's Keychain requirement, Q26, is why `ICredentialPairStore` alone stays a port) |
| Tee identity by bash parameter expansion over `$(<state file>)` (fork-free) | Measured 2026-09-04 on this desktop: 3.6 to 4.0 s for an 88 KB state file; `jq` over stdin is 35 ms | Never; the measurement settles it |
| Cross-volume park and unpark by copy, verify, delete | Contradicts the Brief's "moved, never copied, so one holder exists at any moment"; a failed delete leaves two holders | Never; configuration refuses a profiles root on another volume instead |
| Reuse medley's `Platform.Core.Results.Result<TValue>` | Core takes no package or project reference outside the BCL, and the tool wants a typed error (`SwitchRefusal`, `UsageReadFailure`) rather than a message hierarchy | A NuGet-published org result type with a typed-error shape appears |

### Test strategy

Test-first, Red-Green-Refactor, in the vertical order the phases list. Test types per
`/testing:plan`'s classification (unit for Core, integration for adapters and endpoints, scripted
acceptance for the criteria that need the real CLI). Principles applied from `/tdd:principles`:
output-based tests for Core, state-based tests against real temp directories for the file classes (a
managed dependency the app owns, so no doubles), communication-based tests only for the out-of-process
ports (Anthropic endpoints, the `claude` process, the browser).

**Test boundaries (approval settles these; anything else chosen during implementation is a deviation
logged in `DEVIATIONS.md`):**

| Boundary | Status | Driven by |
|---|---|---|
| `ProfileFolderName.FromEmail`, `AccountEmail.Parse`, `UsageResponseParser`, `SwitchPlanner.Plan`, `AccountRanking.Rank`, `SwitchAdvisor.Evaluate`, `RefreshBudget` | introduced (Core public functions) | unit tests, fixtures, `FakeTimeProvider` |
| `AtomicJsonFile`, `ClaudeStateFile` (textual splice; bytes outside the `oauthAccount` span asserted identical), `ProfileFolderStore`, `RosterFile`, `UsageSnapshotCache`, `AdvisorState`, `SwitchJournal`, `RateLimitGuardTeeFileReader`, `RateLimitGuardStopEventsReader` | introduced (concrete file classes rooted at a path) | integration tests over temp directories |
| `ICredentialPairStore` | introduced port (the Brief's platform abstraction for the deferred Keychain store); its test double is the file adapter itself over a temp directory | integration tests |
| `IUsageEndpointClient`, `ITokenRefreshClient` | introduced ports | fake `HttpMessageHandler` |
| `IClaudeCliAuthStatus`, `ILoginSessionRunner`, `IBrowserLauncher` | introduced ports | canned, scripted, and recording doubles |
| HTTP routes (`/api/*`) | introduced | `WebApplicationFactory` with the ports doubled and file classes on a temp root |
| `claude auth status --json`, `/status`, the tee file, claude.ai Settings > Usage | existing external surfaces | scripted acceptance (`tests/acceptance/`), human-observed |
| `statusline-tee.sh` stdin/stdout/tee-file contract | existing (rate-limit-guard) | `statusline-tee.test.sh` black-box cases |

**Edge cases named up front:** post-`/login` live dir whose account is on no roster; a profile folder
with `profile.json` but no pair; a pair whose refresh token equals the live one; a fresh `*.lock`;
cross-volume profiles root; a 40 KB state file with hundreds of project entries; `limits[]` with an
unknown `kind`; `Retry-After` absent on a 429; `refresh_token_expires_in` absent on a refresh
response (keep the old value and flag the card); tee file torn or absent; email with uppercase and
a plus tag.

**No measurable-improvement claim:** AC 4's 60-second bound is a target on a new capability, not an
improvement over a baseline; it is checked by wall clock in the Phase 2 acceptance step.

### Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| A switch races a session's own token refresh: sampling the lock leaves a window in which a session acquires `.oauth_refresh.lock`, refreshes, and writes the outgoing account's rotated pair over the just-unparked one | Low per switch, certain over months | High (two holders, sessions logged out) | The tool acquires the CLI's own `.oauth_refresh.lock` directory (exclusive mkdir, 60 s stale steal) and holds it across the move; a contending session gets the CLI's retryable error, never a logout; D2 closed from the binary |
| The tool's state-file patch interleaves with the CLI's own `oauthAccount` rewrite | Medium | Medium (lost unrelated key, transient wrong identity) | Settled by probe 1.5a (2026-09-06): the CLI never re-stamps on an ordinary request, so the patch is unconditional and runs under the acquired lock right after the move |
| Claude Code rewrites `~/.claude.json` between the tool's read and replace, losing an unrelated key | Low | Medium | The patched bytes are staged in a flushed temp file, the state file is re-read and compared byte for byte (a length-and-mtime stamp misses same-length rewrites inside one tick), then renamed into place; only the rename is unguarded; five attempts, then the journal completes it at the next start |
| Cross-volume profiles root would make the move a copy-then-delete | Low (default is same volume) | High (two holders) | Configuration refuses a profiles root on another volume, naming both paths; no copy path exists in the code |
| A running session writes a stale in-memory `oauthAccount` back to the state file after a switch, so identity and billing disagree | High (observed 2026-09-06 eight minutes after a switch, after an eight-hour window earlier that day had shown none) | Medium | The owner record binds the live pair to its account: re-bound when the CLI rotates the token while the state file still names the owner, released when the CLI stamped a later login, kept when the state file carries a block older than the record. A stale block is repaired at once: a watcher on the state file (edge-triggered, debounced, one pass at startup) and every dashboard read patch the owner's block back in from its profile folder, and the planner refuses until they agree |
| A token refresh succeeds server-side but the write-back fails, discarding the only valid pair | Low | High (account needs a browser login) | Jittered retry, then an owner-only recovery file and a blocking card error; restored at next start |
| Two credential mutations overlap (switch during Refresh all, two clicks, a second instance) | Medium without a gate | High (two holders, sessions logged out) | One in-process mutation gate, an instance lock, compare-and-swap write-back, and a switch journal reconciled at startup |
| Usage bucket is per client, not per token (D1), so ten accounts hit 429 together | Medium | Medium | One-second spacing, budget of 6 per account, honor `Retry-After`, cards show lockouts; spike 02b once two pairs are parked |
| Anthropic changes `limits[]`, `oauthAccount`, or `.credentials.json` shapes | Medium over a year | Medium | Raw JSON preserved and round-tripped; parser reads only named keys with defensive defaults; recheck triggers recorded in the design threads |
| `claude auth login` output or prompt changes; piped code path (05b) fails | Medium | Medium | Mechanism (b) documented and implemented as fallback behind the same port |
| Terms posture: automation perceived as circumvention | Low | High | Every switch is a human click; own User-Agent on every request; no model-API call; no timer; posture documented in the README |
| `.claude.json` grows to megabytes | Low | Low | `JsonNode` streaming parse; patch cost is a rewrite the CLI already performs |
| Standards sync lands after the skeleton, causing an analyzer-posture diff | Medium | Low | Vendor the two analysis files byte-identical first; the sync PR then shows no diff |

## Blast radius

**HIGH.** Security-sensitive (moves the developer's credential files and patches the state file every
Claude Code session on the machine reads); cross-repository contract change (the tee file six loop-lane
consumers read, additive but reviewed); a new public repository and toolchain; 3+ steps touching
undocumented CLI behavior (state-file layout, lock file, usage endpoint). Reversible in git and by
swapping back, but a failed move touches live credentials.

**Stress-test needed: Yes.** Step 3 plan-reviewer sub-agent (mandatory) and Step 4 `/planning:devils-advocate`
via a fresh-context sub-agent.

## Stress-test summary

Two fresh-context passes on 2026-09-04, both verified against files, the installed binary, and one
measurement before anything was applied.

- **Plan reviewer (Step 3):** 2 critical, 14 important, 16 suggestions. Applied 31; overrode one
  (dropping `ICredentialPairStore`: the Brief's Keychain abstraction requirement keeps it, its
  double is recorded). Largest changes: a process-wide credential mutation gate and instance lock; a
  switch journal reconciled at startup; compare-and-swap write-back with a recovery file so a
  rotated pair is never discarded; three new refusals (`TargetLoginExpired`,
  `SwitchingBlockedByManagedPolicy`, `LiveIdentityUnverified`); edge-triggered tier-3 refresh; the
  refusal of a cross-volume profiles root instead of a copy path; containment and Max-only checks;
  the tee identity read by `jq` at drain (bash extraction measured at 3.7 s) with the floor bullet
  amended across all registered carriers.
- **Devil's advocate (Step 4):** 1 critical, 4 high, 4 medium, 2 low. All confirmed in the binary or
  on disk. Critical: the switch sampled the refresh lock instead of acquiring it (TOCTOU); the real
  lock is `<live dir>/.oauth_refresh.lock`, a `proper-lockfile` directory with a 60 s stale
  threshold, so the tool now acquires it exclusively and holds it across the move (D2 closed). High:
  the state-file patch may be redundant and races the CLI's own rewrite (Phase 1.5a probe decides);
  concurrent mutations (already gated); AC 4's 60-second bound depends on the unresolved bucket
  keying (spike 02b moved before Phase 2, with a `/planning:plan review` route if the bucket is
  shared); the wildcard file guard was the wrong shape (replaced). Medium and low: sync-root refusal,
  startup duplicate-lineage quarantine, the loop-lane residual and the GRAY usage-endpoint posture
  named in the README, a ≥ 90 KB fixture with an external-writer test, an mtime bump after unpark.
- **Confirmed to hold, on binary evidence:** sessions reload credentials on an mtime change
  (`lastCredentialsMtimeMs`, inequality compare), the state-file path, same-volume `File.Move`
  atomicity, and the login-residue set.
- **Research-iterate:** no loop needed; every finding resolved on evidence already at hand. One
  open item is routed, not resolved: the AC 4 re-scope if spike 02b returns "shared".

## Execution shape

### Phase file-overlap matrix

| Phase | Files | Overlaps with |
|---|---|---|
| 0 | repo skeleton, github-iac, standards manifest | 1 (Program.cs, csproj) |
| 1 | Core Identity, Switching, Ports; App file adapters, LiveDirectorySwitch, Program.cs, Endpoints, Dashboard, wwwroot | 2, 3, 4 (Program.cs, Dashboard, wwwroot, Endpoints) |
| 2 | Core Quota, two HTTP ports; App QuotaRefresh, HTTP adapters, tee reader, cache; Dashboard, wwwroot | 1, 3, 4 |
| 3 | Core Routing; App QuotaRefresh (tier 3), Dashboard, wwwroot | 2, 4 |
| 4 | Core Roster, two process ports, browser port; App Process and Browser adapters, RosterFile, Endpoints, Dashboard, wwwroot | 1, 2, 3 |
| 5 | publish props, workflows, README, ConfigurationFile | 1 (ConfigurationFile) |
| 6 | `claude-code-plugins/plugins/rate-limit-guard/**` | none (other repository) |

### Dependency graph

- 0 → 1 (repo and skeleton must exist). 1 → 2 (ports and file classes). 2 → 3 (snapshot types).
  1 → 4 (folders, endpoints, page); 4 consumes nothing from 2 or 3, so it can run before either.
  3 → 5 and 4 → 5 (release packages the whole). 6 depends on
  nothing here; 2's tee reader parses `account.email` when present, so 6 may land before or after 2.
- Integration-first: Phase 1 is the tracer bullet and its sanity check is the live two-session probe.

### Recommended shape

> Wave A (after Phase 0): **Phase 1 in the main session** and **Phase 6 as one sub-agent worker**
> in a worktree of `claude-code-plugins` (file-disjoint: another repository; about 150 LOC of shell
> and docs). Wave B (sequential in the main session): **2-core (2.1 to 2.4, plus 6.6 and #10,
> PR #15) → 4 → 2-remainder (2.0, 2.5, 2.6) together with 3 → 5**.
> Reordered 2026-09-07 (approved by the operator): the session limit hit with only two of the ten
> accounts present on this machine, so nothing could roll; Phase 4 (roster add and remove,
> browser-assisted login) depends only on Phase 1 and moves ahead of the rest of 2 and of 3.
> Cost note: one extra agent for Phase 6 versus fully sequential; everything else shares
> `Program.cs`, the dashboard, and the page, so parallel work there would race.

### Scope-fencing table (Wave A)

| Agent | Phase | ALLOWED files | LOC |
|---|---|---|---|
| main | 1 | `claude-code-account-rotation/**` (except `docs/topics/**/PLAN.md` status edits, which are main-only anyway) | ~900 |
| W6 | 6 | `claude-code-plugins/plugins/rate-limit-guard/scripts/statusline-tee.sh`, `scripts/statusline-tee.test.sh`, `reference/reader-contract.md`, `README.md`, `CHANGELOG.md`, `.claude-plugin/plugin.json` (version); the floor block only in every carrier `scripts/check-loop-lane-floor-drift.sh --check` lists (the three lane `SKILL.md` bodies, the `extract-ssot` orchestrated mode, the two `prompts/loops/` templates); `docs/conventions/loop-lane/README.md` §6 final paragraph only | ~250 |

**W6 FORBIDDEN:** any file outside the list above; any line of a carrier outside its inlined floor
block; the drift gate's registry (`scripts/check-loop-lane-floor-drift.sh`) unless the gate itself
demands an entry; any file in `claude-code-account-rotation`; staging or committing outside the consuming repo's
commit convention (`/source-control:commit`).

**W6 reports at end:** work items completed, per-criterion Sanity Check verdict (the four commands
under Phase 6), actual LOC delta, PR URL.

```text
DIVERGENCE ESCALATION (mandatory): if reality diverges from this brief —
a precondition fails, a file/symbol named here is absent or different than
described, scope is blocked, or a design question arises mid-task — STOP.
Do not improvise, fix forward, or expand scope. Report to the orchestrator:
what you found, what the brief expected, and the exact state of your work
(files touched, edits applied / not applied). Await a revised brief.
```

### Sequential fallback

> If W6 reports cannot-complete, a scope-fence violation, or the floor-drift check fails, abort W6 and
> run Phase 6 in the main session after Phase 5. Phases 1 to 5 are unaffected.

### Per-phase routing table

| Phase | Surface | Basis |
|---|---|---|
| 0 | main session + human | Pulumi apply, App installation grant, and the repo move are the user's own actions |
| 1 | main session | judgment-heavy, touches live credentials, needs the live acceptance probe |
| 2 | main session | shares Program.cs, dashboard, page with 1 |
| 3 | main session | pure logic but edits the same dashboard and refresh class as 2 |
| 4 | main session + human (05b, browser login) | spike verdict and browser steps need the operator |
| 5 | main session + human (laptop) | release and fresh-machine install |
| 6 | sub-agent worker (worktree in `claude-code-plugins`) | mechanical, file-disjoint, other repository |

## Open questions

- D1 (spike 02b): usage bucket keyed per token or per client. Phase 2.0, before pacing locks; a
  "shared" result re-scopes AC 4 through `/planning:plan review`.
- D2: **closed 2026-09-04** from the binary: `<live dir>/.oauth_refresh.lock`, directory lock, stale
  at 60 s, refreshed every 5 s. Recheck trigger: the name or library changes in a CLI release.
- D3 (spike 05b): piped login code acceptance. Decides Phase 4's mechanism.
- Whether the state-file patch is needed at all (Phase 1.5a probe).
- Whether `claude auth logout` under a profile folder revokes server-side (research unknown 6); the
  optional logout on Remove is offered either way.
- Whether a running session ever writes a stale `oauthAccount` back to the state file after a
  switch (answered 2026-09-06: not seen over eight hours; the owner record's stale-block refusal
  is the guard if it ever happens, since the dashboard does not re-patch).
- Whether `login_hint` is honored on the interactive `/login` URL (only matters under mechanism b).

## Handoff to implementation

### User-approval gates

- Phase 0.1, 0.2, 0.3, 5.4, 5.5: the user runs `pulumi up`, grants the App installation, moves the
  contract slice, flips `RequiresCi`, installs on the laptop. Implementation prepares diffs and stops
  at each.
- Phase 1 and Phase 2 live acceptance steps touch the real live dir on this desktop: confirm before
  the first real switch and before the first real Refresh all.
- Phase 4.1 spike 05b needs a browser login the user performs.
- Every `[FALLBACK — confirm or override]` item in the Decisions table below, if not overridden at
  approval, is taken as approved.
- Any mid-flight pivot that changes an acceptance criterion routes back to `/planning:plan review`.

### Execution shape ([EXEC-SHAPE] tagged)

- [EXEC-SHAPE] Tracer-bullet ordering: switch first (Phase 1), quota core second, roster and login
  third (Phase 4), the quota remainder together with ranking fourth, packaging fifth; the tee PR
  runs in parallel as W6.
- [EXEC-SHAPE] Two source projects plus two test projects (design T1).
- [EXEC-SHAPE] Sub-topic promotion declined although Phases 1 to 4 each exceed 300 LOC: one operator,
  one repository, sequential commits on one branch per phase; six PLAN.md files would fragment one
  contract. Revisit if a phase grows its own research need.
- [EXEC-SHAPE] The sanitizer is Phase 0's first vertical test (real behavior proves the lane).
- [EXEC-SHAPE] Sanity-check commands as written per phase; the acceptance runbook lives in
  `tests/acceptance/` with one script (`check-single-holder.sh`) and one human checklist.
- [FALLBACK — confirm or override] The tool acquires Claude Code's own `.oauth_refresh.lock`
  directory (exclusive mkdir through P/Invoke, 60 s stale steal) and holds it for the milliseconds
  of a switch; a profiles root under OneDrive, Dropbox, or Google Drive is refused; duplicate
  lineages found at startup are quarantined under app data with a banner; the state-file patch is
  kept or dropped by the Phase 1.5a probe's outcome without a second approval round.
- [FALLBACK — confirm or override] Default listen port `48211`; lock wait bound 10 s; refresh budget
  6 per 5 min with a 60 s minimum gap (a 401 exempt); login-session expiry 10 min; a profiles root
  on another volume is refused (no copy path); per-account snapshot cache, advisor state, switch
  journal, and recovery files on disk under app data; same-origin plus custom-header filter and
  loopback `Host` filtering on the API; mechanism (a) piped login gated by spike 05b with (b) as
  fallback (`login_hint` appended under b); tee identity read with one `jq` over stdin at drain time
  with the mtime staleness guard, and the floor bullet amended across all registered carriers in the
  same PR; tier-3 refresh edge-triggered; a paused pair refreshed within 7 days of login expiry;
  a switch refuses under a device-managed `forceLoginOrgUUID` and roster add admits Max accounts
  only; single-file self-contained without AOT; release asset for `win-x64` only in V1.
- [FALLBACK — confirm or override] Terms-posture transparency: the parked-pair refresh presents
  Claude Code's public OAuth `client_id` (as spike 03 did). It is not User-Agent imitation, but it is
  the one request where the tool presents the CLI's client identity; the README posture paragraph
  and the Brief's captured assumptions should name it. Approval of this plan is taken as accepting
  that posture unless overridden.

### Decisions made (gate-passed)

Every choice below is `/planning:plan`'s, not the Brief's. Each passed the confidence gate on
evidence captured this session; the last row is below the bar and is flagged for override.

| Decision | What it changes in the plan | Basis (evidence) |
|---|---|---|
| Tracer-bullet ordering: the switch ships first | Phase 1 is the end-to-end switch; quota (2), ranking (3), roster and login (4), packaging (5) follow | Spikes 01 to 04 settled viability; integration is the remaining risk |
| Two source projects and two test projects | Phase 0 and 1 file inventories; Core carries no package reference | Org `architecture-and-design.md` dependency direction; functional core per `/tdd:principles` |
| No sub-topic promotion despite phase size | One PLAN.md, seven phases, one branch per phase | One operator, one repository, sequential commits |
| The sanitizer is Phase 0's first test | Phase 0.5 | Proves the lane on real behavior, not a placeholder (`testing.md` test theater) |
| Phase 6 runs as a parallel sub-agent worker | Execution shape Wave A; W6 scope fence | File-disjoint: another repository |
| Acquire the CLI's `.oauth_refresh.lock` rather than sample it | Phase 1.5; D2 closed; `OAuthRefreshLock` type | Installed binary: `proper-lockfile`, `stale: 60000`, exclusive mkdir |
| Keep or drop the state-file patch by a probe | Phase 1.5a | Installed binary: `stampAuthenticatedAccount` re-stamps `oauthAccount` on mismatch |
| Interfaces only for out-of-process dependencies plus the credential store | Six ports in the type inventory; file classes concrete | `/tdd:principles` (managed vs unmanaged); Brief Q26 abstraction requirement |
| Tee identity read by `jq` over stdin at drain time, mtime staleness guard, floor bullet amended across all carriers | Phase 6.2 to 6.4; W6 scope | Measured 3.7 s (bash) vs 35 ms (jq) vs 175 ms (CLI); the floor block's own text |
| Cross-volume and sync-root profiles roots are refused | Phase 1.4 configuration validation; no copy path | Brief "moved, never copied"; handoff constraint on OneDrive |
| Tier-3 refresh is edge-triggered | Phase 3.4 | Brief Q19 (no timer); a level trigger under a 10-second page poll is polling |
| Failed write-back parks the rotated pair in a recovery file | Phase 2.5 | Spike 03: the old refresh token is consumed on 200 |
| Max-only roster admission; refusal under a managed `forceLoginOrgUUID` | Phases 1.2, 1.3, 4.4 | Brief Q6, Q23 |
| Login mechanism (a) piped, gated by spike 05b, (b) fallback with `login_hint` | Phase 4.1 | Spike 03 crumbs; reopens the handoff's (b)-primary note because (a) removes the manual step |
| Spike 02b before Phase 2; AC 4 re-scope routed to `/planning:plan review` if the bucket is shared | Phase 2.0 | Spike 02 left keying unknown; two tokens are available now |
| Single-file self-contained, no AOT; `win-x64` asset only | Phase 5 | Brief Q22; org precedent keeps AOT off |
| README posture names the GRAY usage endpoint, the `client_id`, and the loop-lane residual | Phase 5.3 | Research root index §1; spike 03; reader contract |
| Wave B reordered so Phase 4 runs before the rest of Phase 2 and before Phase 3 (2026-09-07, approved by the operator) | Supersedes the phase order in the first row: Wave B becomes 2-core (2.1 to 2.4, plus 6.6 and #10, PR #15) → 4 → 2-remainder (2.0, 2.5, 2.6) with 3 → 5 | The session limit hit with only two of ten accounts on this machine, so nothing could roll; the dependency graph puts Phase 4 on Phase 1 alone |
| **Below bar, flagged:** default port `48211`, lock wait 10 s, budget 6 per 5 min with a 60 s gap, login-session expiry 10 min, 7-day paused-refresh window, quarantine on duplicate | Configuration defaults and small policies | Judgment calls; any value can be changed at approval or in `config.json` |

### Mechanical work

- One branch per phase (`feat/<phase-slug>`), squash-merged through PRs on the new repo; Phase 6 on
  its own branch in `claude-code-plugins`. Commit at every green checkpoint via `/source-control:commit`.
- Verification checkpoints: each phase's Sanity Check block, then `/verification:confirm` before the
  phase PR.
- Phase tags in this file advance `[TODO]` → `[DOING]` → `[DONE]`; edits to this file are
  main-session only.
- Sequential fallback for W6 as stated above.
- Close-out at the final PR: `/planning:plan close-out` (publish PLAN.md to the PR, graduate durable
  outcomes, prune the contract slice).
