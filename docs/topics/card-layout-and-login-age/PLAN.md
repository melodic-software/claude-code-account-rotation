# card-layout-and-login-age

Planned 2026-09-14 for issues #46 (card layout) and #49 (login age and refresh-token expiry), as one
chain and one pull request. Parent topic: `docs/topics/claude-subscription-rotation/PLAN.md`,
Phase 2 item 2.6 (card text) and Phase 3 item 3.2 (the ranking filter). Design:
`design/design-resolution.md` (early-exit-light, Tier B, six threads resolved). Process template:
`docs/topics/order-by-next-reset/PLAN.md`.

## Brief

### TLDR

- The credential expiry #49 asks for is already read from disk (`CredentialPair.LoginExpiresAt`,
  parsed from `refreshTokenExpiresAt`) and already gates the switch planner; the login moment is
  already parsed (`OAuthAccountBlock.ProfileFetchedAt`). Neither reaches the payload.
  `DashboardAssembler` passes five arguments to `AccountStanding` and leaves `LoginExpiresAt` null.
- #46 is page-only: the title is still one `alias (email)` string, the badge row shows some states
  but not "ready", `Remove` sits beside `Switch`, and `Log in again` is offered on every healthy
  card.
- Core changes nothing. Two trailing wire fields on `AccountCardView`, one assembler change, a page
  re-layout, and the close-out.

### The issues, as filed

Issue #46: alias as the heading, address secondary, a clear state chip (live / ready / needs login /
paused / error), the quota block, the browser mapping as small print, then actions with the
destructive one separated and de-emphasised; consistent card height; no `Log in again` as a primary
action on a healthy card. Colour and typography are #50.

Issue #49: a line per card stating when the account will next need a browser login, a visible warning as
it approaches, an expired refresh token presented plainly rather than as switchable, and the
nearest-to-expiry account findable without reading ten cards.

Read against the code at `2b65a31`: the state axis #46 wants is the credential axis, distinct from
the quota axis #47 shipped as `standing`; both stay. "Sort or flag" resolves to flag so #47's total
order is not reopened (design T4).

### Goal

Each card leads with the alias, states in one chip whether the operator can switch to it, keeps
the quota block and the standing line, says how old its login is and when the login expires with a
warning inside seven days and a plain `login expired` once past, and offers `Remove` apart from the
working buttons and `Log in again` only when the card needs it. The two instants travel as two
nullable fields on the existing card record.

### Constraints

- Work happens on `feat-card-layout-and-login-age` in a worktree, off `main` at `2b65a31`. Never
  on the main checkout.
- **No real e-mail address, machine path, or user name in any tracked file.** `bash
  eng/check-no-machine-paths.sh` is the CI gate and scans `src tests README.md docs`; test data
  uses `example.com` addresses.
- Stage files by name, never `git add -A`. Run `git checkout -- '**/packages.lock.json'` after any
  build that drifts them; `dotnet restore --locked-mode` is what CI runs.
- Core stays untouched: no change to `AccountAvailability`, `AccountStanding`, or the comparer.
- Every host-visible instant in App tests comes from `factory.Clock`, never wall clock.
- Analyzer posture: `AnalysisMode=All` with `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild`.
  The red step is a test written against the real signature with a minimal implementation behind
  it, never `throw new NotImplementedException()`.
- No token content on any view type or in any log line: the assembler lifts two instants off the
  pair and discards it (`DashboardView` doc comment: "No token field exists on any of these types").
- Repo rules: Conventional Commits titles, `.claude/rules/pr-body-contract.md`, the `kyle-sexton`
  noreply commit identity (verify `git config user.email` before committing), every commit ending
  `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- The live dashboard on the operator's machine (127.0.0.1:48211) is not stopped, restarted,
  reinstalled, seeded, or written by this work; `GET /api/dashboard` and `/healthz` may be read.
  Tests run against temp directories.
- Every edit is delegated to an `implementation:implementer` worker; an
  `implementation:phase-verifier` checks each phase against the diff before the next begins.

### Acceptance criteria

- Each card in `GET /api/dashboard` carries `loginExpiresAt` and `loggedInAt` (`DateTimeOffset?`).
  A parked account with credentials takes `loginExpiresAt` from its pair's `refreshTokenExpiresAt`
  and `loggedInAt` from its `profile.json`'s `profileFetchedAt`; the live account takes them from
  the live pair and the state file's account block; a roster-only account, a pair without
  `refreshTokenExpiresAt`, or a profile without `profileFetchedAt` yields null for the missing one.
- A parked credential file that is missing, torn, or unreadable yields null for that card's
  `loginExpiresAt` and does not fail the payload: the card still renders, as it does today when
  `HasCredentials` is file existence.
- `AccountStanding.LoginExpiresAt` is filled with the same instant the card carries, so the
  parent's 3.2 filter reads it without a second lookup. Nothing observes this field through the
  payload today (`KeyFor` does not read it), so it is a Read assertion on the `Card()` call, not a
  test.
- The page renders each card, top to bottom: `h2` alias (or the address when there is no alias);
  `p.address` the address, only when an alias exists; one always-present state chip (`live` /
  `paused` / `needs login` / `login expired` / `error` / `ready`, first match in that order) with
  `not on roster` as a secondary badge; the usage block as shipped, plus a `login-expiry` line at
  its foot; the browser mapping as small print; then the trailing group, pushed to the card's
  bottom edge: the action row, `Remove` in its own group (present only when `Remove` is), and the
  Edit panel.
- The `login-expiry` line reads `logged in 12 d ago · login expires in 16 d` (either half omitted
  when its instant is null; the line omitted when both are), takes the `warn` class inside seven
  days, reads `login expired` once `loginExpiresAt <= dashboard.capturedAt`, and carries the
  absolute expiry as its `title` (set only when `loginExpiresAt` is known). Both countdowns
  measure from `dashboard.capturedAt`, never the browser clock.
- On an expired card that is not live, and on a paused card, the standing line is omitted (the
  chip has said it); on an expired card `Switch` is disabled. For a rostered, non-live card,
  `Login` / `Log in again` is in the action row only when `!hasCredentials`, expired, or inside
  the seven-day window; on a healthy rostered card it sits inside the Edit panel. An unrostered
  or live card offers no login button, as today.
- No client sort, no pinning, no DOM regrouping: `dashboard.accounts.forEach` stays; the render
  guard is unchanged; `innerHTML` use stays at its current count of 2.
- `dotnet build -c Release` 0 warnings; `dotnet test -c Release` green with no test lost (baseline
  468 on `2b65a31`, 1 skipped); `dotnet format whitespace --verify-no-changes`, `typos .`,
  markdownlint on changed markdown, `shellcheck eng/*.sh`, and `bash eng/check-no-machine-paths.sh`
  clean.

### Captured assumptions

- `pairs.ReadParkedAsync(folder)` reads a small JSON file and throws `JsonException` or
  `InvalidDataException` on a torn or malformed file (`FileSystemCredentialPairStore.ReadPairAsync`).
  The assembler already calls `ReadLiveAsync` unguarded on the same path; the parked reads this plan
  adds are guarded per account, and the live read is left as it is (a broader error-mapping pass is
  #9's).
- Ten parked reads per ten-second poll is the same order of work the refresh pass and the switch
  already do; no cache is added.
- The seven-day warning window equals `QuotaRefresh._loginRenewalWindow`, so the page's "soon" and
  the pass's "renew now" agree. The page hard-codes the number; threading it through the payload is
  not worth a field for a value the pass also hard-codes.
- `relative()` already yields `in N d` past 48 hours; a mirror `ago()` is added for the login age.
- `SwitchEndpointTests.cs:54` and the two `RosterEndpoints` construction sites compile and pass
  unchanged: both new fields are trailing with null defaults, the `Standing` / `NextResetAt`
  precedent.
- `AppFactory.ParkedProfileAsync(email, token, ct, loginExpiresAt)` already writes a pair with a
  chosen `refreshTokenExpiresAt`, but its `AccountJson` writes no `profileFetchedAt` into
  `profile.json` or the state file; the facts need a stamp derived from `factory.Clock`, never
  `DateTimeOffset.UtcNow`.
- `DashboardAssembler` has no logger and is not `partial`; every logging class in the App is
  `sealed partial` with `[LoggerMessage]` methods (`StateFileWatcher`, `StartupReconciliation`),
  and CA1848 is live, so a plain `LogWarning` call does not build. `AppComposition` registers the
  assembler with `AddSingleton<DashboardAssembler>()`, so a new constructor parameter needs no
  registration edit.
- The parked cards are built inside a synchronous `Select`; an awaited read turns that into a
  `foreach`.

### Out-of-scope, stated

- Any Core change; `Rank` itself (parent 3.2), which this chain only leaves a scope-change note for.
- Colour, typography, the visual language, a paused-group separation (#50); editable alias (#48);
  error mapping for a torn live read (#9); the login-renewal pass.
- Anything that stops, restarts, reinstalls, or writes the operator's live dashboard.

## Plan

**Standards grounding:** no standards index exists (no `.claude/standards.yaml`, no
`docs/standards/`), so resolution falls to the inference rung: `.claude/rules/pr-body-contract.md`
(ambient), the analyzer pair `Directory.Build.props` + `eng/dotnet-analysis/`, the parent design
record (`type-inventory.md` rows for `AccountStanding` and `CredentialPair`,
`capability-matrix.md` C8 "login expiry per card"), and this repository's own conventions as the
two prior chains recorded them (full-sentence test names over Shouldly, long "why" doc comments,
page helpers as small named functions, `element(tag, class, text)` for every node).

Scale: Medium (about ten files across App, tests, the page, and three markdown files). Three
sequential phases; Phases 1 and 2 are file-disjoint, but they share one worktree and one branch,
so they run one after the other.

Baseline to record before Phase 1: `dotnet test -c Release` total on `2b65a31` (expected 468, 1
skipped).

### Phase 1: The assembler fills the instants and the card carries them [TODO]

Review: code

Tests first in `tests/ClaudeCodeAccountRotation.App.Tests/Dashboard/DashboardAssemblerTests.cs`
(the existing `AppFactory` with temp directories and `factory.Clock`), full-sentence fact names,
Shouldly. Red is a fact asserting the field on the payload against the real record; the minimal
implementation behind it is the field with its default, so the fact fails on `null`.

1. `src/ClaudeCodeAccountRotation.App/Dashboard/DashboardViews.cs`: two trailing parameters on
   `AccountCardView`, `DateTimeOffset? LoginExpiresAt = null` and `DateTimeOffset? LoggedInAt =
   null`, and two sentences in its doc comment saying what each is and where it comes from.
2. `src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs`: the class becomes
   `internal sealed partial class` and its primary constructor gains
   `ILogger<DashboardAssembler> logger` (with `using Microsoft.Extensions.Logging;` and
   `using System.Text.Json;`). `Card(...)` gains `DateTimeOffset? loginExpiresAt` and
   `DateTimeOffset? loggedInAt`, passes both to the view and `loginExpiresAt` as
   `AccountStanding`'s sixth argument. `AssembleAsync` supplies them: the live card from
   `livePair?.LoginExpiresAt` and `liveAccount?.ProfileFetchedAt`; each parked card, the `Select`
   becoming a `foreach` so it can await, from a new private `ReadLoginExpiryAsync(profile)` that
   returns null when `!profile.HasCredentials` and otherwise calls
   `pairs.ReadParkedAsync(profile.FolderPath)` inside a catch for `JsonException`,
   `InvalidDataException`, `IOException`, and `UnauthorizedAccessException` (null on any of them,
   one `[LoggerMessage(Level = LogLevel.Warning, Message = "login expiry unreadable for {Folder}:
   {Reason}")]` partial method, naming the folder and never the contents), plus
   `profile.Account?.ProfileFetchedAt`; a roster-only card null for both. A doc comment on the
   helper says why a torn parked file is a blank line and not a failed page (the watcher hotfix
   precedent).
3. `tests/ClaudeCodeAccountRotation.App.Tests/AppFactory.cs`: `AccountJson(email,
   DateTimeOffset? profileFetchedAt = null)` writes `profileFetchedAt` as epoch milliseconds when
   given; `ParkedProfileAsync` and `WriteStateFileAsync` gain the same optional trailing parameter
   and pass it through. Every stamp in a fact derives from `factory.Clock.GetUtcNow()`.
4. Facts, one per criterion, in `DashboardAssemblerTests`: a parked pair written with
   `loginExpiresAt: clock + 16 d` yields that instant on its card and null `loggedInAt` when the
   profile carries no `profileFetchedAt`; a profile stamped `clock - 12 d` yields `loggedInAt`; a
   roster-only account yields null for both; the live card takes its instants from the live pair
   and the state file's block; a parked credential file overwritten with `{` (torn) yields null
   `loginExpiresAt` and the payload still lists that account with `hasCredentials` true.
5. `tests/ClaudeCodeAccountRotation.App.Tests/Endpoints/RosterEndpointTests.cs`: one fact that the
   card `POST /api/accounts` returns carries null for both new fields (the construction sites are
   unchanged).

**Sanity Check:**

- `grep -c "LoginExpiresAt" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardViews.cs` is
  `>= 1`; `grep -c "LoggedInAt" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardViews.cs` is
  `>= 1`.
- `grep -c "ReadParkedAsync" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs`
  prints `1`; `grep -c "LoggerMessage" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs`
  is `>= 1`; `grep -c "sealed partial class DashboardAssembler" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs`
  prints `1`.
- `grep -c "new AccountStanding(" src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs`
  prints `1` and that call passes six arguments (Read assertion).
- `git diff --stat 2b65a31 -- src/ClaudeCodeAccountRotation.Core` prints nothing.
- `! grep -q "UtcNow" tests/ClaudeCodeAccountRotation.App.Tests/Dashboard/DashboardAssemblerTests.cs`
  (every stamp comes from the clock).
- `dotnet test -c Release --filter "FullyQualifiedName~DashboardAssemblerTests|FullyQualifiedName~RosterEndpointTests"`
  green; then the unfiltered `dotnet test -c Release` reports a total `>= 474` with 0 failed;
  `dotnet build -c Release` 0 warnings; `git status --short -- '**/packages.lock.json'` empty after
  the revert.

**Files affected (Phase 1)**

| File | Action | What changes |
|---|---|---|
| `src/ClaudeCodeAccountRotation.App/Dashboard/DashboardViews.cs` | Modify | Two trailing fields on `AccountCardView` |
| `src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs` | Modify | `partial` with a logger; `Card()` takes the two instants; guarded parked read in a `foreach`; sixth `AccountStanding` argument |
| `tests/ClaudeCodeAccountRotation.App.Tests/AppFactory.cs` | Modify | Optional `profileFetchedAt` on the three helpers |
| `tests/ClaudeCodeAccountRotation.App.Tests/Dashboard/DashboardAssemblerTests.cs` | Modify | Five facts |
| `tests/ClaudeCodeAccountRotation.App.Tests/Endpoints/RosterEndpointTests.cs` | Modify | One fact |

### Phase 2: The page leads with the alias, states the chip, and says when the login expires [TODO]

Review: code

No JS test framework exists; the page is pinned by the Sanity Check greps and the phase verifier
reading the diff. Every node goes through `element(tag, className, text)`; no `innerHTML` is added.

1. `src/ClaudeCodeAccountRotation.App/wwwroot/app.js`, the header: `h2` becomes the alias when
   there is one and the address otherwise; when there is an alias, `element("p", "address",
   account.email)` follows it. `word-break: break-all` stays on `.card h2` (the no-alias case still
   puts an address there) and is added to `.address`.
2. `app.js`, the chip: a `stateChip(account, at)` helper returning one of `live`, `paused`,
   `needs login`, `login expired`, `error`, `ready` (first match: `isLive`; `roster.paused`;
   `!hasCredentials`; `loginExpired(account, at)`; `refresh.state === "stranded"`; else `ready`),
   rendered as `span.badge.<class>` with the class the kebab-case word (`login-expired`,
   `needs-login`). The existing conditional `live` / `paused` / `needs-login` badges are replaced
   by this one chip; `not on roster` stays as a second badge. A `loginExpired(account, at)` helper
   (`account.loginExpiresAt && new Date(account.loginExpiresAt).getTime() <= at`) is shared by
   the chip, the `Switch` guard, and the standing line.
3. `app.js`, the expiry line: `loginLine(account, at)` returning `logged in <ago> · login expires
   <relative>` per the acceptance criterion, with `LOGIN_WARN_SECONDS = 7 * 24 * 3600` deciding
   the `warn` class; `ago(instant, from)` mirrors `relative()` with `ago` phrasing. Appended inside
   `usage(...)` after the `next-reset` line as `element("p", "login-expiry" + (warn ? " warn" :
   ""), text)` with `title` set to `new Date(account.loginExpiresAt).toLocaleString()` only when
   `loginExpiresAt` is non-null (`new Date(null)` is the 1970 epoch).
4. `app.js`, the standing line: `nextReset()` returns null when `loginExpired && !account.isLive`
   (the chip has said it; the live account's quota standing is still the operative fact) and when
   `paused` (the chip has said that too).
5. `app.js`, the browser mapping: the `p.muted` line moves from above `usage(...)` to below it.
6. `app.js`, the actions: `Switch` also disabled when `loginExpired`. Inside the existing
   `roster && !account.isLive` guard, the `Login` / `Log in again` button stays in the action row
   only when `!hasCredentials || loginExpired || withinWarn`; otherwise `editPanel(account)` gains
   a `Log in again` secondary button after Save (an unrostered card has no Edit panel and no login
   button, as today). When `Remove` renders (`!account.isLive`), it goes into
   `element("div", "actions danger-zone")` appended after the primary `div.actions`; no empty group
   on the live card.
7. `src/ClaudeCodeAccountRotation.App/wwwroot/app.css`: `.address` (muted, `word-break:
   break-all`), `.badge.login-expired` and `.badge.error` in the error token, `.badge.ready` in
   the ink token (the live token is the live chip's, the accent the off-roster badge's),
   `.login-expiry` beside `.asof`, `.login-expiry.warn` in the warn token, `.cards { grid-auto-rows:
   1fr; }`, `.actions { margin-top: auto; }` (the first element of the trailing group; the
   danger-zone and the Edit panel follow it), `.actions.danger-zone { margin-top: 0; }`. Token
   values are not changed (#50).

**Sanity Check:**

- `grep -c "innerHTML" src/ClaudeCodeAccountRotation.App/wwwroot/app.js` prints `2` (2 today);
  `! grep -q "accounts.sort\|accounts.filter" src/ClaudeCodeAccountRotation.App/wwwroot/app.js`.
- `grep -c "function stateChip\|function loginLine\|function loginExpired\|function ago" src/ClaudeCodeAccountRotation.App/wwwroot/app.js`
  prints `4` (0 today).
- `grep -c '"login expired"' src/ClaudeCodeAccountRotation.App/wwwroot/app.js` is `>= 1` and
  `grep -c '"ready"' src/ClaudeCodeAccountRotation.App/wwwroot/app.js` is `>= 1` (both 0 today).
- `grep -c "danger-zone" src/ClaudeCodeAccountRotation.App/wwwroot/app.js` is `>= 1` and
  `grep -c "danger-zone\|login-expiry\|\.address\|grid-auto-rows" src/ClaudeCodeAccountRotation.App/wwwroot/app.css`
  is `>= 4` (0 today).
- `! grep -q 'roster.alias + " ("' src/ClaudeCodeAccountRotation.App/wwwroot/app.js` (1 today).
- `grep -c "word-break" src/ClaudeCodeAccountRotation.App/wwwroot/app.css` is `>= 2` (the `h2`
  rule and `.address`).
- The render guard is untouched: `git diff 2b65a31 -- src/ClaudeCodeAccountRotation.App/wwwroot/app.js | grep -c "reordered\|:hover\|renderedOrder"`
  prints `0` (no changed line mentions them; `grep -c` exits 1 on zero matches, so read the
  number, not the exit code).
- `dotnet build -c Release` 0 warnings (the page is embedded, so the build proves it still packs).

**Files affected (Phase 2)**

| File | Action | What changes |
|---|---|---|
| `src/ClaudeCodeAccountRotation.App/wwwroot/app.js` | Modify | Header split, chip, expiry line, standing suppression, mapping move, action groups, edit-panel login |
| `src/ClaudeCodeAccountRotation.App/wwwroot/app.css` | Modify | The rules listed in item 7 |

### Phase 3: The close-out [TODO]

Review: pr

1. `CHANGELOG.md` under `[Unreleased]`: an `### Added` entry for the per-card login age and expiry
   line, the warning, and the expired state (#49), and a `### Changed` entry for the card layout
   (#46) naming the two new fields on each dashboard account.
2. `docs/topics/claude-subscription-rotation/PLAN.md`: **a dated scope-change note under Phase 3
   item 3.2 only**, carrying the literal token `scope-change` in its heading, recording that
   `AccountStanding.LoginExpiresAt` is now filled by the assembler and that 3.2's filter must also
   drop rows whose `LoginExpiresAt <= now`, since `Arrange` still orders them (design T2). A second
   note under Phase 2 item 2.6 recording that the "login expires in N days" card text shipped in
   #49 with `logged in N d ago` beside it. No tick, no item edit.
3. PR body drafted **before** `gh pr create` per `.claude/rules/pr-body-contract.md`: title
   `feat: lead each card with the alias, state the chip, and show when the login expires (#46, #49)`;
   body opening `Closes #46` then `Closes #49` on separate lines, with non-empty `## Summary`,
   `## Fix`, `## Verification`, and `## Related` (naming #50, #48, #9, #35 and parent 3.2). Gate
   list: `dotnet build -c Release`, `dotnet test -c Release`, `dotnet format whitespace
   --verify-no-changes`, `typos .`, markdownlint on the changed markdown, `shellcheck eng/*.sh`,
   `bash eng/check-no-machine-paths.sh`, `git status --short -- '**/packages.lock.json'` empty.
4. This topic's phase tags to `[DONE]`; a fresh-context phase verifier on the whole diff before the
   PR.

**Sanity Check:**

- `grep -c "#49" CHANGELOG.md` is `>= 1`; `grep -c "#46" CHANGELOG.md` is `>= 1`.
- `grep -c "scope-change" docs/topics/claude-subscription-rotation/PLAN.md` prints `3` (one from
  #47, two from this chain); `git diff --stat 2b65a31 -- docs/topics/claude-subscription-rotation/PLAN.md`
  shows insertions only.
- `grep -c '^### Phase.*\[DONE\]' docs/topics/card-layout-and-login-age/PLAN.md` prints `3` before
  the PR merges.
- `bash eng/check-no-machine-paths.sh` exits 0; `markdownlint-cli2` clean on the changed markdown.

**Files affected (Phase 3)**

| File | Action | What changes |
|---|---|---|
| `CHANGELOG.md` | Modify | Added / Changed entries for #49 and #46 |
| `docs/topics/claude-subscription-rotation/PLAN.md` | Modify | Two dated scope-change notes (3.2 and 2.6) |
| `docs/topics/card-layout-and-login-age/PLAN.md` | Modify | Phase tags to `[DONE]` |

Zero-match checks throughout this plan are written `! grep -q …` where they gate; a `grep -c`
that should print `0` exits 1, so a runner reads the printed number, not the exit code.

## Alternatives considered

- **An `Expired` group in `AvailabilityStanding`** (Core change; an expired account sorts after
  the paused ones). Rejected: the key orders quota windows and already ignores `HasCredentials`;
  a credential fact in the key would reopen #47's T2. Switch condition: the operator asks for
  expired accounts to sink to the bottom of the grid, at which point it is a one-member enum
  addition plus a `KeyFor` branch.
- **Sort by nearest expiry** (#49's "sort or flag"). Rejected for the same reason; the chip and
  the warn class make the nearest card findable. Switch condition: the operator reports still
  scanning ten cards for the one about to expire.
- **Formatting the expiry phrase server-side.** Rejected: #47's T3 keeps every countdown in
  `app.js` measured from `dashboard.capturedAt`; a server phrase adds a second "now".
- **Caching parked pairs across polls.** Rejected: the read is ten small files per poll and the
  refresh pass rewrites them; a cache would show a stale expiry after a renewal. Switch condition:
  the dashboard's poll visibly slows, measured, with the reads on the profile.
- **Threading the seven-day window through the payload.** Rejected: the pass hard-codes it too;
  one constant on each side is cheaper than a field until a policy binds it (parent 3.1).

## Test strategy

TDD, red then green per fact. Boundaries driven, all existing: `GET /api/dashboard` through the
`AppFactory` (the `DashboardAssemblerTests` seam, temp profiles root, `factory.Clock`), and
`POST /api/accounts` through `RosterEndpointTests`. No new boundary is introduced; the page has no
test seam and is pinned by greps plus the verifier. Regression facts: the torn-parked-file fact
(the watcher hotfix's lesson applied to the new read), and the unchanged-sites fact.

## Risks

- A torn parked credential file during a refresh-pass write-back. Mitigated by the per-account
  catch and the fact that pins it.
- `Log in again` moving into the Edit panel surprises an operator who reaches for it on a healthy
  card. Mitigated by the CHANGELOG entry naming the move; reversible in one line if the operator
  prefers the row.
- `grid-auto-rows: 1fr` makes every row as tall as the tallest card on the page, which can leave
  empty space in a mixed grid. Accepted: #46 asks for aligned rows; #50 owns the visual pass.
- A second `now` on the page. Mitigated: every helper takes `at` from `dashboard.capturedAt`.

## Blast radius

LOW. Additive fields on one internal record with defaults; one new guarded read on the poll path
that the switch and the refresh pass already make; the page re-layout changes what a card shows
and where `Remove` and `Log in again` sit, not what any button does; no Core change, no new route,
no persistence, no migration. The only cross-cutting edge is the parent plan's 3.2 note, which is
additive text.

## Stress-test summary

Formal stress-test skipped: blast radius LOW, no triggers matched. The fresh-context plan
reviewer (Step 3, Opus) returned 0 CRITICAL, 8 IMPORTANT, 10 SUGGESTION; every finding was
verified against the tree and folded in: the Phase 3 fence now includes this `PLAN.md`; the
assembler change spells out `partial` + `ILogger` + `[LoggerMessage]` (CA1848 is live and the
class had no logger); the filtered test check is split from the unfiltered total; `word-break`
stays on the `h2`; the "pinned to the bottom" criterion is reworded to the trailing group; the
vacuous chip grep is replaced; the `AccountStanding.LoginExpiresAt` criterion is marked a Read
assertion; `AppFactory.cs` joins the Phase 1 fence with an optional `profileFetchedAt`; the parked
`Select` becomes a `foreach`; `UnauthorizedAccessException` is caught; the paused half of the
standing suppression is a criterion; suppression is gated on `!isLive`; the login button keeps its
`roster && !isLive` guard; the danger group renders only with `Remove`; `title` is set only with an
expiry; `.badge.ready` avoids the live token; zero-match greps use `! grep -q`; the render-guard
check reads the diff. Verified sound by the reviewer: `ParkedProfile.Account.ProfileFetchedAt` is
populated for parked and live blocks, `ReadPairAsync` throws `JsonException` on `{`, `factory.Clock`
is a settable `TestClock`, `grid-auto-rows: 1fr` composes with the auto-fill grid, the render
guard needs no change, and the PR body contract accepts two `Closes #` lines.

## Execution shape

### Phase file-overlap matrix

| | Phase 1 | Phase 2 | Phase 3 |
|---|---|---|---|
| Phase 1 (App `.cs`, App tests) | | none | none |
| Phase 2 (`app.js`, `app.css`) | none | | none |
| Phase 3 (`CHANGELOG.md`, parent `PLAN.md`, this `PLAN.md`) | none | none | |

### Dependency graph

Phase 2 reads `account.loginExpiresAt` and `account.loggedInAt`, which are null until Phase 1
lands, but its code and its Sanity Check do not depend on Phase 1's build: the fields are read
defensively and every grep is on the page. Phase 3 depends on both. The phases are file-disjoint,
yet every worker commits on this one branch in this one worktree, so two concurrent workers would
collide on the index lock and interleave commits under the phase verifiers' diffs. Sequential is
the shape: Phase 1, verify, Phase 2, verify, Phase 3. Parallel is possible only if each worker
self-provisions its own worktree and the two branches are merged back, which this plan does not
describe.

### Per-phase routing table

| Phase | Surface | Basis |
|---|---|---|
| 1 | `implementation:implementer` worker (Opus), then `implementation:phase-verifier` | Bounded C# change with a test seam |
| 2 | `implementation:implementer` worker (Opus), then `implementation:phase-verifier` | Page-only, file-disjoint from Phase 1, run after it |
| 3 | `implementation:implementer` worker (Sonnet), main session drafts the PR body | Markdown edits |

Cost: three workers one after the other. A parallel Wave A would save one worker's wall time on
about 150 lines and costs a second worktree plus a merge; not taken.

## Decisions made (gate-passed)

| Decision | What it changes in the plan | Basis |
|---|---|---|
| `[EXEC-SHAPE]` The three phases run sequentially in this one worktree | One worker at a time, a verifier between | Zero file overlap between 1 and 2, but one branch and one index; concurrent commits would collide and blur the verifiers' diffs |
| `[EXEC-SHAPE]` The parked read is guarded per account and logs a warning | Phase 1 item 2 | `ReadPairAsync` throws on a torn file; the watcher hotfix (PR #58) showed what an unguarded read on a poll path costs |
| `[EXEC-SHAPE]` The assembler gains an `ILogger` through its primary constructor and a `[LoggerMessage]` method | Phase 1 item 2 | CA1848 is live; `StateFileWatcher` and `StartupReconciliation` are the pattern; DI registration is `AddSingleton<DashboardAssembler>()` |
| `[EXEC-SHAPE]` The test factory's helpers gain an optional `profileFetchedAt` | Phase 1 item 3 | `AccountJson` writes no stamp today; a stamp from `factory.Clock` keeps the wall clock out of the facts |
| `[EXEC-SHAPE]` The seven-day window is a page constant | Phase 2 item 3 | `QuotaRefresh._loginRenewalWindow` is the same hard-coded value; parent 3.1 owns binding |
| `[EXEC-SHAPE]` `Log in again` moves into the Edit panel on a healthy card | Phase 2 item 6 | #46: "It should not be a primary action on a healthy card"; the panel is the existing secondary surface |
| `[FALLBACK — confirm or override]` The standing line is omitted on an expired or paused card | Phase 2 item 4 | Design T1 and T2: the chip already says it, and `usable now` under `login expired` would be false |
| `[FALLBACK — confirm or override]` `grid-auto-rows: 1fr` plus `margin-top: auto` on actions for row alignment | Phase 2 item 7 | #46 asks for consistent card height; the smallest CSS that delivers it without touching tokens |

## Open questions

- Whether the operator wants `Log in again` reachable from the card face on a healthy card (an
  Edit-panel button is the plan's answer; the row is one line away).
- Whether the login-age half of the line earns its space, or the operator wants expiry only.
- The page has no test seam. The only pre-merge visual check would be a screenshot from a second
  instance on another port with temp app data and `example.com` profiles; offered at approval, not
  in scope.

## Handoff to implementation

### User-approval gates

- The two `[FALLBACK]` rows above, and the `Log in again` placement, are surfaced for the operator
  at plan approval; any of them flips in one line.
- Anything that would edit the parent plan beyond the two dated notes, touch Core, or change
  `SwitchEndpointTests.cs`, stops and asks.
- Any need to start, restart, seed, or write the operator's live dashboard stops and asks; nothing
  in this plan requires it.

### Execution shape ([EXEC-SHAPE] tagged)

- `[EXEC-SHAPE]` Sequential: Phase 1, then Phase 2, then Phase 3, each an
  `implementation:implementer` worker in this worktree followed by its own
  `implementation:phase-verifier` before the next worker is dispatched.
- Scope fences. Phase 1 ALLOWED: `src/ClaudeCodeAccountRotation.App/Dashboard/DashboardViews.cs`,
  `src/ClaudeCodeAccountRotation.App/Dashboard/DashboardAssembler.cs`,
  `tests/ClaudeCodeAccountRotation.App.Tests/AppFactory.cs`,
  `tests/ClaudeCodeAccountRotation.App.Tests/Dashboard/DashboardAssemblerTests.cs`,
  `tests/ClaudeCodeAccountRotation.App.Tests/Endpoints/RosterEndpointTests.cs`; FORBIDDEN:
  everything under `wwwroot/`, `src/ClaudeCodeAccountRotation.Core/`, this `PLAN.md`. Phase 2
  ALLOWED: `src/ClaudeCodeAccountRotation.App/wwwroot/app.js`,
  `src/ClaudeCodeAccountRotation.App/wwwroot/app.css`; FORBIDDEN: every `.cs` file, this
  `PLAN.md`. Phase 3 ALLOWED: `CHANGELOG.md`, `docs/topics/claude-subscription-rotation/PLAN.md`,
  `docs/topics/card-layout-and-login-age/PLAN.md` (phase tags only); FORBIDDEN: everything else. A
  worker that needs a file outside its fence stops and reports.
- `[EXEC-SHAPE]` Every worker commits on the branch with a Conventional Commits subject and the
  trailer, stages by name, and reverts lock-file drift before committing.

### Mechanical work

- Commit boundaries: `docs(plan)` for this topic's design record and plan first, then one `feat`
  commit per phase, then `docs` for the close-out. The PR squash-merges.
- After every build: `git checkout -- '**/packages.lock.json'` if it drifted.
- Verification checkpoints: each phase's Sanity Check list; the Phase 3 gate list; a fresh-context
  phase verifier per phase and on the whole diff before the PR.
- Fallback from a worker divergence: finish that phase through a fresh worker with the narrowed
  brief, or in the main session as a last resort.
