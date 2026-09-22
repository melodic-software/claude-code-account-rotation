# profile-order-and-card-label

Decided 2026-09-11 in a decisions interview after the machine-setup follow-ups closed (discovery:
`.work/machine-setup-followups/browser-profile-numbering/EXPLORE.md` and `RESEARCH.md`, memory
tier). Parent topic: `docs/topics/machine-setup-followups/PLAN.md`.

## Brief

### TLDR

- Order discovered browser profiles the way an operator reads them: `Default` first, then `Profile N`
  by the number, then anything else, within each browser; the browser order stays Chrome, Edge, Brave.
- Show the profile's display name on the roster card beside the raw directory, when the catalog the
  page already holds can resolve the pair; the raw directory alone otherwise.
- Close #43: a `browserProfileDirectory` must be a single path segment, and the read-only login-session
  status route says in code why it sits outside the mutation filter.

### Goal

The dashboard stops echoing Chromium's profile-directory counter as if it were an order. Research
settled that the counter gaps (`Profile 4, 6, 7, 8, 9`) are the browser's designed output, that the
app's only coupling to the directory name is the persisted string it hands to `--profile-directory`,
and that vendors themselves decouple the display name from the directory; so the fix is display and
order in the app, with no browser-side change. The `--profile-directory` operand is also fenced to one
path segment so a roster entry cannot point a login at a directory outside `User Data`.

### Constraints

- One branch in this checkout (no worktree), `feat/profile-order-and-card-label` from `main`.
- No JavaScript test harness; `app.js` is verified by a real-browser drive against an isolated
  instance (temp config, spare port), recorded in the PR body.
- The persisted roster contract is unchanged: `browserProfileDirectory` stays the raw directory
  string, and the launcher still passes it verbatim.
- Repo rules hold: Conventional Commits, `.claude/rules/pr-body-contract.md`, `bash
  eng/check-no-machine-paths.sh` clean, no real address or machine path in tracked files.

### Acceptance criteria

- `ChromiumLocalStateProfileReader.ReadAsync` returns, for a `Local State` whose `info_cache` lists
  `Profile 10, Profile 2, Work, Default, Profile 1` in that document order, the sequence `Default,
  Profile 1, Profile 2, Profile 10, Work`; a reader test pins it.
- `POST /api/accounts` and `PATCH /api/accounts/{email}` return 400 with an explanatory `error` for a
  `browserProfileDirectory` containing `/` or `\`, equal to `.` or `..`, or carrying leading or
  trailing whitespace, and store nothing; an endpoint test pins each shape. A plain `Profile 3` and a
  blank value behave as before.
- `LoginEndpoints.cs` carries a comment beside the `GET /api/login-sessions/{id}` route stating why it
  is mapped on `routes` rather than the mutation group.
- On the roster card, an account mapped to a catalog profile shows `browser / name (directory)`; one
  mapped to a directory the catalog does not carry shows `browser / directory`; read from the DOM of
  an isolated instance seeded with a synthetic `Local State`.
- `dotnet build -c Release` 0 warnings; `dotnet test -c Release` passes with no test lost (baseline
  311 total); `CHANGELOG.md` carries the change under Unreleased.

### Captured assumptions

- Chromium serializes `info_cache` keys in an order the app must not depend on (research left it
  open); the sort makes the picker independent of it either way.
- A directory name outside the `Profile N` / `Default` pattern is rare (Brave names none; Edge and
  Chrome name only those) and sorting it last by ordinal is enough.

### Out-of-scope

- Renaming, renumbering, or recreating browser profile directories on the machine.
- A dependent dropdown, a duplicate-address warning, or an ordering of the roster cards (#47).
- PR #45's findings.

## Plan

### Phase 1: Order, card label, and the #43 fence [DONE]

Done 2026-09-11 on `feat/profile-order-and-card-label`. Test-first for the reader order (one red
fact on a scrambled fixture) and the #43 fence (seven red Theory cases plus one PATCH fact), green in
the same commit; the card label and the changelog followed. `dotnet test -c Release`: total 320,
failed 0, skipped 1 (the pre-existing Unix file-mode skip); `dotnet build -c Release
--no-incremental` 0 warnings. Browser drive: an isolated instance built from the branch (temp config,
port 48222, this machine's real browsers, no synthetic `Local State` because the reader has no path
override and the real files already carry the gaps) returned Chrome `Default, Profile 1, 2, 3` and
Edge `Default, Profile 4, 6, 7, 8, 9` from `/api/browser-profiles`; Playwright read the same order
from the Add form's three `optgroup`s, read `chrome / <display name> (Profile 1)` on a card mapped to
a catalog profile and `chrome / Profile 99` on one mapped to a directory the catalog lacks, and a
POST carrying `../Profile 1` was refused with 400 and produced no card. A fresh-context
`implementation:phase-verifier` returned CONFIRMED on all seven criteria against the working tree
(its two non-blocking notes, the comparer's visibility and the fixture's stated order, are applied in
the closing commit). Review lanes (code, security): no defect; the security lane's P4 on Windows
alias spellings (trailing dot, NTFS stream suffix, reserved device names, control characters,
unbounded length) and its note that the launcher trusted the roster file as written are both taken:
the rule moved into `Core.Accounts.BrowserProfileDirectory`, shared by the endpoints and the
launcher (five more refused shapes in the endpoint Theory, one launcher refusal test), and the
login-session comment's "no more sensitive than the dashboard" claim was reworded to the property
actually defended. A second fresh-context verifier on the final diff passed seven criteria and failed
one sentence of that same comment (it said the page polls the route; the page only posts the code),
which is corrected in the closing commit; the live drive was repeated at the final commit with the
same DOM reads plus the new refused shapes (`Profile 3.`, `CON`, an NTFS stream suffix, and a PATCH to
a trailing dot, all 400).

Work items, in order:

1. Reader test for the natural order (red), then the comparer in `ChromiumLocalStateProfileReader.Profiles` (green).
2. Endpoint Theory for the refused directory shapes (red), then the single-segment check shared by POST and PATCH in `RosterEndpoints` (green); the why-comment on the login-session status route.
3. `app.js`: the card line resolves the pair through `indexOfProfile` and prints `name (directory)` when found.
4. `CHANGELOG.md` entries; browser drive against an isolated instance for the card line; `/verification:confirm`.

Sanity check: `dotnet test -c Release` green with the new tests counted; the DOM read shows both card shapes.
