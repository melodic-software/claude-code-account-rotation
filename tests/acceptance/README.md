# Acceptance runbook

The Brief's criteria that need the real CLI, real sessions, and a real browser. Run these by hand on
the desktop with the user present; record each pass in the log at the bottom. Nothing here runs in
CI.

`check-single-holder.sh` takes either form. `<profiles-root> <live-dir> [app-data-dir]` is the
single-side check every step below uses. `--root <dir>`, repeatable, sweeps whole trees instead and
is the form a machine whose two sides share one store needs: it counts a mailbox claim under
`.transit/` and a `*.incoming` staging name as holders like any other file, so a hand-off in flight
is checked rather than passed over.

## The temp-root WSL acceptance (`wsl-switch-acceptance.sh`, issue #69)

The one script here that touches no real root at all, and the only one that can run unattended. It
drives a real leader on Windows and a real follower inside the distro over the real 9P mount, on
temp roots and fake pairs, with a `claudeExecutable` that does not exist so no CLI is ever started.
Roughly forty minutes.

```sh
dotnet publish src/ClaudeCodeAccountRotation.App -c Release -r win-x64 --self-contained false -o <win>
dotnet publish src/ClaudeCodeAccountRotation.App -c Release -r linux-x64 --self-contained true -o <linux>
# copy <linux> into the distro, off the mount. Publish writes
# claude-code-account-rotation-follower-log beside the binary; the script
# copies it from this tree if it is missing and marks both executable.
bash tests/acceptance/wsl-switch-acceptance.sh --leader <win> --follower <linux inside the distro>
```

It runs on the **Windows** side and reaches into the distro with `wsl.exe`. That is forced, not
preferred: the leader binds loopback only and under WSL's default NAT networking a process inside
the distro cannot reach the Windows loopback at all. The sweep that needs all four roots at once
still runs inside the distro, where they are all readable.

The follower build is self-contained because the distro's runtime need not match the SDK the leader
was built with; the framework-dependent `linux-x64` leg is what `eng/smoke-linux-publish.sh` and CI
cover. Both binaries must be published from the same commit: the leader refuses a side whose
reported version is not its own.

Two environment variables drive the injections, both read only from the environment and never from
the configuration file: `CCAR_FAIL_AFTER_STEP` names one `ImportStep` or `WslSwitchStep` and kills
the process at it (`<step>:before-journal` for the torn case), and
`CCAR_CORRUPT_EXPORT_BEFORE_GATE` truncates the exported pair between the follower's `Exported`
answer and the leader's native read, which is the one window no external script can hit
deterministically.

`--keep` leaves the temp roots and every process log behind, which is what a failed run is for.

## The state-file patch probe (plan item 1.5a, run 2026-09-06)

Run once, before the first real switch, with the patch disabled: three sessions open, one switch to a
parked account, one message in each session, then `claude auth status --json | jq -r .email` and
`/status` everywhere. Every session billed the incoming account on its next request, but the CLI
never re-stamped `oauthAccount`, so identity stayed on the outgoing account. The patch is therefore
unconditional and the `patchStateFile` key no longer exists; the outcome is recorded under 1.5a in
`PLAN.md` and in the log below. The probe is not repeated.

## AC 1, switch without a browser, and AC 9, refresh lock respected

1. Three sessions open on account A; note the statusline `acct:` badge in each.
2. On the page, click Switch to account B. Expect the toast `Switched to B; parked A`.
3. Send one message in each of the three sessions; every `/status` shows B, and
   `claude auth status --json | jq -r .email` prints B. No browser opened, no session restarted.
4. `bash tests/acceptance/check-single-holder.sh ~/.claude-profiles ~/.claude` prints `duplicates=0`.
5. Create the lock directory `~/.claude/.oauth_refresh.lock`, click Switch, and expect the 409 toast
   `A session is refreshing its token right now`; remove the directory.

## AC 2, in-flight work survives

1. In one session, start a subagent fan-out (three parallel subagents) on account A.
2. While it runs, click Switch to B.
3. The fan-out completes without an error, and the parent's next message bills B.

## State-file drift probe

After a switch, run several turns in a session opened before the switch, then start and stop a fresh
session. `jq -r .oauthAccount.emailAddress ~/.claude.json` still prints the incoming account.

## Live identity check (once per acceptance pass)

`bash tests/acceptance/check-live-identity.sh` prints `billed_email=<incoming account>`. It makes one
honest-User-Agent read of the OAuth profile with the live access token and prints nothing else.

## Usage cards and Refresh all (issue #52)

Run after installing a build that carries the usage cards, with every account logged in.

1. Open the dashboard and click `Refresh all`. The line under the header reads
   `refreshing all accounts...` while the pass runs and then `last pass: ...` with the counts.
2. Every card the rate window allowed shows three rows, the 5-hour window, the 7-day window, and the
   scoped one, each with a percentage and its reset time, and one `as of <time> via refresh` line
   naming where the figures came from and when they were taken. A card the window refused reads
   `rate limited, retry in N s` and is first in line for the next pass; a card nothing has read shows
   `unknown` on every row rather than zero. Wait out the lockout and click `Refresh all` again for
   the cards the first pass did not reach.
3. Pick one card and compare it against that account's `Settings > Usage` on claude.ai, signed in as
   that account. Record which account, both readings, and the card's capture time: a difference is
   only a finding once it is larger than what the capture time explains.
4. `bash tests/acceptance/check-single-holder.sh ~/.claude-profiles ~/.claude` prints `duplicates=0`
   and `recovery=0`. A non-zero `recovery` names each stranded file: restart the tool, or click that
   account's own `Refresh`, and run the check again.

## Log

| date | machine | criteria | outcome | notes |
|---|---|---|---|---|
| 2026-09-06 | desktop, Windows 11, CLI 2.1.263 | 1.5a probe | keep the patch | the pair moved and billing followed on the next request; `oauthAccount` and `claude auth status` kept the outgoing account after nine minutes; the next refresh rotated the token so `live-owner.json` went stale; recovered through journal reconciliation with the patch on; `duplicates=0` before and after |
| 2026-09-06 | desktop, Windows 11, CLI 2.1.263 | AC 9 | pass | with `.oauth_refresh.lock` held by hand the switch waited 10 s and refused 409 `RefreshLockPresent`; nothing moved; a second click during the wait got 409 `MutationInProgress` at once (the page could disable the button while a switch is in flight) |
| 2026-09-06 | desktop, Windows 11, CLI 2.1.263 | AC 2 | pass | three parallel subagents started in one session; the switch ran 3.6 s later (200, CLI verified the incoming account, no mismatch); all three finished their reads and reported without an error; `duplicates=0`; the live refresh token rotated again within seconds of the unpark, so `live-owner.json` was stale at once |
| 2026-09-06 | desktop, Windows 11, CLI 2.1.263 | AC 1 | pass | three sessions open; switch through the page's API returned the toast payload in 3.6 s; `/status` in every session showed the incoming account without a restart or a browser, even before the next message (it reads the patched state file); `claude auth status --json` and `.oauthAccount.emailAddress` both named the incoming account five minutes later; `duplicates=0` |
| 2026-09-06 | desktop, Windows 11, CLI 2.1.263 | live identity | pass | `check-live-identity.sh` printed `billed_email=<incoming account>` after the AC 1 switch |
| 2026-09-06 | desktop, Windows 11, CLI 2.1.263 | state-file drift | fail, then fixed | eight hours after the probe's recovery patch and five minutes after the AC 1 switch the state file still named the incoming account, but in the post-fix live check a running session wrote its in-memory block back eight minutes after the switch (`profileFetchedAt` from before the switch); the owner guard refused the next switch, and the repair guard (state-file watcher plus every dashboard read) was built the same evening and re-checked live: see the next rows |
| 2026-09-06 | desktop, Windows 11, CLI 2.1.263 | post-fix live check | pass, with the drift finding | on the review-fixed build: switch to the parked account (200, pair moved, owner record re-bound on the way in, `duplicates=0`); the CLI verification and `/status` named the outgoing account because a session's write-back landed minutes later; the next switch refused `LiveIdentityUnverified` as designed |
| 2026-09-07 | desktop, Windows 11, CLI 2.1.263 | identity repair guard | pass | started the guarded build on the stale state: the startup pass logged the disagreement, re-patched the owner's block, and re-bound the record to the rotated pair; `claude auth status` named the live pair's owner within seconds; then a switch back to the main account: 200, no mismatch, `duplicates=0`, record re-bound after the CLI's rotation |
| 2026-09-21 | laptop, Windows 11 + Ubuntu-26.04 (WSL2, NAT), CLI 2.1.278 | phase 5 live slice, R1 R3 R4 R5 R6 and R2's checkable half (#70) | pass | The first run on real credential pairs: one designated account, chosen for the longest login window of the ten, handed from the Windows store to WSL and back, operator at the page for every mutation. Leader and follower published from the same commit, both reporting it. **R1**: from inside the distro, `POST /api/accounts`, `POST /api/accounts/<email>/login`, `GET /api/roster`, `POST /api/roster/<email>`, `POST /api/accounts/<email>/switch` and `POST /api/refresh` all answer **404** at the follower's router. **R2**: proven by fingerprint rather than by the log grep the criterion names (see the correction below) — the pair that arrived in the distro was byte-identical to the one that left the store, so it was handed over and not logged in; the follower's container registers no login runner and the login route is a 404. **R3**: Windows switched to another account while WSL held the designated one, and each side kept its own. **R4**: `GET /api/dashboard` on the follower's port answers **200** from inside the distro. **R5**: `duplicates=0` on every sweep — before the install, after it, before the hand-off, while held, and after the release. **R6**: the held account's card read `in use by wsl` with its Windows switch disabled, and its slot held `holder.json` (144 B) and no credential. **The rotation-during-hold case, on real credentials**: the live WSL session rotated the token while the account was held, and the release brought the *rotated* lineage home with no stale copy anywhere — 9 distinct holders while held, 10 after the release, `duplicates=0` at both, and the returned fingerprint deliberately not the one that went out. That is the case a copy-based switcher loses a login to. Also observed: a release leaves the follower's `oauthAccount` as `{}` rather than removing the key, and the real CLI was content with it — no login prompt, no error, the account read as absent |
| 2026-09-22 | desktop (melo-desk-001), Windows 11 + Ubuntu-26.04 (WSL2), CLI 2.1.280, build `1.0.0+2a30095` | phase 8 desktop rollout, R1 R2 R3 R4 R5 R6 (#73) | pass | The second machine, on the released build rather than a local publish: leader and follower both installed from the `v1.0.0` assets by `chezmoi apply`, no hand-staged binary, and both reporting the same version string. **R1**: at the follower's router `POST /api/accounts`, `POST /api/accounts/<email>/login`, `DELETE /api/accounts/<email>`, `POST /api/accounts/<email>/switch`, `POST /api/refresh` and `POST /api/accounts/<email>/adopt-live` all answer **404**. **R2**: the distro performed zero logins — the designated account arrived entirely through the hand-off, and a real interactive `claude` session ran on it as Claude Max with no login prompt. The log grep the criterion names is still unavailable on this build (the follower's stdout is a discarded pty; a wrapper log arrives with #113), so the evidence is the 404 login route plus the session itself. **R3**: three `claude` sessions per side, then a Windows switch to `ksextontech@live.com` while WSL held the designated account; the Windows three reported the new account and the WSL three kept theirs. **R4**: `GET /api/dashboard` on the follower's port answers **200** from inside the distro with `"role":"follower"`. **R5**: `duplicates=0` on every sweep over the four roots — after the install, after the hand-off, after a real WSL session, and after twenty switches, ten per side. **R6**: the held account's card read `slot: held-elsewhere`, `chip: in use by wsl`, `canSwitchHere: false`, `heldAway: true`, and bypassing the disabled button by posting the switch directly answered **409 `HeldByOtherSide`** with the one-pair-per-machine message, so the refusal lives in the planner rather than only in the page. **The rotation-during-hold case again, on this machine**: the token rotated inside the distro during the live session (the credential file grew 524 → 917 bytes) and the invariant stayed at `duplicates=0`, reproducing the laptop's result on different hardware. **One trap found**: a distro that has never run `claude` interactively shows the first-run onboarding wizard, whose first question is `Select login method`, even though the handed-over credentials are valid and both CLI versions authenticate headlessly. An operator who answers it would create the second token family the design forbids; `hasCompletedOnboarding` was stamped in `~/.claude.json` instead |
