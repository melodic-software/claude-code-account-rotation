# Acceptance runbook

The Brief's criteria that need the real CLI, real sessions, and a real browser. Run these by hand on
the desktop with the user present; record each pass in the log at the bottom. Nothing here runs in
CI.

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
