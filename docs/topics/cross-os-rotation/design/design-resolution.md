# cross-os-rotation: design resolution

Design chosen 2026-09-19 on the laptop against the repository at `9b1740b`; reconciled
2026-09-20. This file is the tracked record of the shared-store design. The working drafts
(`DESIGN-SHARED-STORE.md`, the rejected `DESIGN.md`, `phase-0.md`) live in an untracked memory
slice, so every fact a later session must act on is carried here instead of cited there.

Tags: **[V]** verified by a command, a `file:line`, or a URL read while designing; **[A]**
assumption with its revisit trigger.

Email placeholders: `<live.com>` the account that was live in WSL on the laptop, `<outlook-claude>`
the Windows live account, `<gmail-main>` the desktop's former WSL account. Accounts A to J are the
laptop's ten.

## 1. The decision

One store per machine on the Windows user profile's volume (today's `~/.claude-profiles`, unchanged
in place) holds the roster, labels, browser mapping, parked pairs, and one `holder.json` per account
naming which side holds its pair. The Windows app is the store's **only writer of slots**; the
browser, the roster, and every login live there.

Each side keeps its own live config dir and its own switch: Windows `~/.claude` (same volume as the
store, every move a rename as today) and WSL `/home/<user>/.claude` (ext4). Selection is per side by
construction. A WSL switch is clicked on the Windows page and driven by the Windows app through the
follower's API, because WSL cannot call Windows under `nat` networking **[V]**.

**One family per account per machine.** Activating an account on a side takes its pair out of the
store into that side's live dir; the slot then holds only `holder.json`, and the other side shows
`in use by wsl` or `in use by windows`. Possession is the truth for tokens; the record is the index
the page can read when the distro is off.

The WSL hand-off is a claim-by-rename inside the store (Windows, native, under the app's one
mutation gate), a staged copy, verify, promote, delete on the WSL side under its own refresh lock
and journal, and a park-by-rename back into the slot (Windows). The one step that destroys a
lineage's last local copy is gated: the follower exports and stops, the leader reads the exported
file natively on the store's own volume, and only then does the follower swap (section 9.2). The
Brief's "moved, never copied" is
amended to "at most one reachable copy at every instant". No cross-side lock exists, because a
cross-side exclusive `mkdir` was measured to double-acquire under contention (section 3).

Logins: zero new logins up front, zero in WSL ever, and the monthly cost stays what it is today, one
short login per account per machine when its 28-day login lapses.

### Why not the two-family alternative

The rejected predecessor gave each account a second token family in WSL ("WSL as a fourth machine").
It is simpler on every axis except the one the user decided on.

| Axis | Two families (rejected) | Shared store (chosen) |
|---|---|---|
| Logins up front, laptop | 10 short (WSL) | 0 |
| Logins per machine per 28 days | 20 | 10, as today |
| Same account live on both sides | allowed | refused (one family) |
| Copy path in credential code | none | one, staged and journaled, follower side only, mailbox only |
| Depends on 9P | no | yes, `/mnt/c` for the mailbox |
| Cross-side lock | none needed | none used; single slot writer plus fingerprint verification |
| Distro off | nothing stranded | WSL-held accounts unavailable on Windows until the escape hatch |
| Held-account usage on the page | one usage read serves both | tee-only for WSL-held accounts |
| Concurrent logins per account per device | 2 | 1 |

The deciding number is the second row, and it rests on section 2: the login window does not slide, so
families are the unit of the monthly chore and two families double it forever.

## 2. The login window is fixed at 28 days, not slid by a refresh

Settled 2026-09-19 on the laptop against the running instance; no instrumented build, no restart, no
credential file read. `refresh_token_expires_in` is present in the token response and counts down to
a **fixed** instant.

Method: `loginExpiresAt` from `GET /api/dashboard` (millisecond precision) minus the credential file
mtime from a directory listing gives the server's value, because the client computes `now + N`
immediately before the write-back (`ClaudeOAuthTokenRefreshClient.cs:103`, `QuotaRefresh.cs:721-723`)
**[V]**. Discriminator: a ~28 d jump means sliding; a sub-second change means present and fixed;
byte-identical means absent.

| Refresh | Write time (UTC) | `loginExpiresAt` written | Implied `refresh_token_expires_in` |
|---|---|---|---|
| 14:46 pass | 2026-09-19 14:46:36.460 | 2026-10-14T22:48:23.458Z | 2 188 907 s (25.33 d) |
| measured pass | 2026-09-19 23:40:19.788 | 2026-10-14T22:48:24.786Z | **2 156 885 s (24.96 d)** |

Difference 32 022 s against 32 023 s elapsed: one fixed instant about 28 days after the login.
`loginExpiresAt` moved +1.328 s, not +28 days.

Corroborating: all ten cards read exactly 28 days (2 419 200 s, the fixture value in
`ClaudeOAuthTokenRefreshClientTests.cs:21`) after an instant between 09-07 and 09-16, and none is 28
days after the day they were all refreshed **[V]**. `loggedInAt` is not the login instant (for two
cards it is later than the expiry-set instant), so the "expiry minus 28 d" value is the only
login-age signal the app has.

Consequences carried forward:

- The parent plan's captured assumption "refreshing a parked pair's token renews its four-week login"
  is **false**. It must be corrected by a dated note, not a silent body edit (phase 5).
- Recorded expiry creeps about +1.3 s forward per refresh, so `loginExpiresAt` is an upper bound.
- The re-read trigger that would have reopened the two-family comparison (`2419200` returned by the
  server) did not fire. The shared-store recommendation stands.
- `refresh.summary` is not rewritten for a single-account refresh (`RefreshRequest.One`); cosmetic,
  unfiled.

## 3. DrvFs and 9P semantics, measured

Measured 2026-09-19 on temp paths (since deleted). These are the facts the protocol rests on; a
newer WSL is the revisit trigger for every **[A]** below.

| Probe | Result | Consequence |
|---|---|---|
| `rename(2)` from `/mnt/c` to ext4 | `errno 18 EXDEV` **[V]** | a cross-volume move is a copy plus a delete, no exception |
| `mv -f` over an existing file on `/mnt/c` from WSL | succeeds; Windows reads the new bytes **[V]** | promote-by-rename inside the store works from either side |
| `fsync` on a `/mnt/c` file from WSL | returns 0 **[V]** | the staged copy can be flushed before the delete. Return 0 is not a durability measurement **[A]**, which is why section 9.2's export gate re-reads the file natively from the Windows side before the follower is allowed to swap |
| `chmod 600` on `/mnt/c` from WSL | no-op, mode stays `777` **[V]** | the store's protection is the NTFS ACL, never the POSIX mode |
| Windows writes, WSL re-reads after a Windows temp-plus-rename | fresh content both times **[V]** | `holder.json` and the claimed file written on Windows are read fresh in WSL |
| directory rename on `/mnt/c` from WSL | succeeds **[V]** | |
| `O_CREAT\|O_EXCL` file create on `/mnt/c` | second create refused, sequentially **[V]** | |
| `mkdir` plus `rmdir` on `/mnt/c` from WSL | 42 ms per pair over 200 pairs **[V]** | a store call from WSL costs tens of ms; the follower makes fewer than ten per switch |

### Cross-side exclusive `mkdir` is not a lock

Native `CreateDirectoryW` from Windows against WSL `mkdir` through `/mnt/c` on the same directory,
tight loops, temp paths.

- Sequential control: a `mkdir` on a directory the other side created a moment earlier is refused
  300 of 300 times in each direction **[V]**.
- Concurrent, with a clock-free detector (the winner renames the directory to a private name at
  once): over 1 498 acquisitions there were **18 rename-after-create failures**, 12 Windows-side and
  6 WSL-side **[V]**. In 17 of them the directory was gone 100 ms later; in one it was not, so an
  ENOENT from the rename is not by itself proof of removal and 18 is an upper bound. The probe
  removes that name only through the other side's rename after its own `mkdir` returned 0, so at
  least most of the 17 are two `mkdir` calls succeeding on one name.
- Mechanism: unknown **[A]**. It does not occur sequentially. Revisit trigger: the same probe prints
  zero failures over 10 000 acquisitions on a newer WSL.
- **Consequence: no design here may rest a single-holder guarantee on a cross-side `mkdir`, and this
  one uses none.** The app's own `.oauth_refresh.lock` is unaffected: it is taken by one side's
  processes on one side's live dir, native on both file systems, which is the case the CLI itself
  relies on.
- A file-rename claim probe (a feeder writes 600 slot files, both sides race to rename each to a
  private name) measured no contention: Windows claimed all 600 with correct content and WSL none,
  because its per-slot poll costs a 9P `stat` of about 20 ms and never arrived first **[V]**. Whether
  a native-versus-9P file rename can double-succeed is untested **[A]**; the protocol never races two
  sides on one name.

## 4. Requirements, restated as tests

| # | The user's words | Binary test |
|---|---|---|
| R1 | one central place, from Windows, for accounts, labels, browser profiles and logins | every add, alias, mapping, login, and switch for either side is a click on the loopback page; the WSL side has no add, no login, no roster write |
| R2 | never log in ten times on Windows and ten more in WSL, never monthly | after rollout the WSL side has performed **zero** `claude auth login`; logins per machine per 28 days are at most one per account |
| R3 | selection per side | switching Windows to B leaves WSL on A and the reverse; three open sessions per side follow their own side's switch on their next request |
| R4 | the app runs on both sides | the `linux-x64` build serves `/api/dashboard` in the distro |
| R5 | one token, one holder (#21) | `check-single-holder.sh` over the four roots (Windows live, store including `.transit/`, WSL live including `*.incoming`, WSL app data) prints `duplicates=0` after ten switches on each side |
| R6 | an account in use on one side is visibly unavailable on the other | the card shows `in use by wsl` / `in use by windows` and its switch button for the other side is disabled within one dashboard poll (`POLL_MS = 10000`, `app.js:4`); the planner refuses `HeldByOtherSide` if the button is bypassed |

## 5. Why the store sits on the Windows volume, and why a record beats bare possession

| Store location | Windows reach | WSL reach | Windows move | WSL move | Available when |
|---|---|---|---|---|---|
| **Windows profile volume `~/.claude-profiles` (chosen)** | native | `/mnt/c`, 9P, 15-42 ms per call **[V]** | rename, as today | staged copy | Windows always; WSL whenever the distro runs |
| ext4 `/home/<user>/.claude-profiles` | `\\wsl.localhost`, 9P, 171 ms per listing, distro must run | native | staged copy | rename | the side that owns the browser and the roster is dead whenever the distro is off |
| Both live dirs on the Windows volume, WSL via `CLAUDE_CONFIG_DIR` under `/mnt/c` | native | 9P for everything the CLI does | rename | rename | no copy path, but the whole WSL config tree (transcripts, a 90 KB state file rewritten per session, plugins, hooks) lands on 9P, `0600` is unenforceable, and the CLI's own `.oauth_refresh.lock` sits on a path where `mkdir` is not exclusive |
| A third volume or a network share | | | copy | copy | two copy paths instead of one; nothing gained |

The Windows volume wins on the one axis that matters: the side that owns the browser and the roster
must reach the store when the other side is off. The "both live dirs on 9P" variant is the tempting
no-copy answer and is rejected for putting the CLI's hot files and its refresh lock on 9P.

**An explicit `holder.json` beats bare possession** for four reasons: when the distro is off Windows
cannot observe the WSL live dir at all, so possession is unobservable exactly when the page needs it;
an empty slot is ambiguous between never-logged-in, removed, and checked-out; the page needs
`in use by wsl since 14:02` as a rendered fact and the planner needs a refusal reason; and crash
recovery needs an expectation to compare the file system against. Possession stays the truth for
tokens: when a slot holds a pair **and** a record naming the other side, reconciliation trusts the
file, rewrites the record, and logs it.

## 6. Store layout

```text
~/.claude-profiles/                       (the store, on the Windows profile volume)
  <folder-per-email>/
    .credentials.json                     present  = parked, free to take
    holder.json                           {"side":"wsl","fingerprint":"<sha256>","since":"..."}
    profile.json                          as today
  .transit/wsl/
    <folder>.credentials.json             claimed for wsl by the leader, not yet imported
    <folder>.credentials.json.incoming    exported by wsl, not yet promoted into its slot
```

`.transit/wsl/` is the follower's mailbox. A file in it means a WSL switch is in flight: the leader's
planner refuses `SlotInTransit` for that account and the leader's refresh pass skips it. Names ending
in `.incoming` in the WSL live dir are staging names; no reader of pairs opens them except
reconciliation.

## 7. New Core types and ports

| Type | Kind | Members | Notes |
|---|---|---|---|
| `SideName` | readonly record struct | `Value` (`windows`, `wsl`) | |
| `HolderRecord` | record | `SideName Side`; `RefreshTokenFingerprint Fingerprint`; `DateTimeOffset Since` | `holder.json`; `RefreshTokenFingerprint` already exists at `Core/Identity/RefreshTokenFingerprint.cs` |
| `SlotState` | enum | `Parked`, `HeldHere`, `HeldElsewhere`, `NeverLoggedIn`, `InTransit` | file present to `Parked`; record for this side to `HeldHere`; record for another to `HeldElsewhere`; a file under `.transit/<side>/` to `InTransit` |
| `SwitchRefusal` | existing enum, extended | `HeldByOtherSide`, `SlotInTransit`, `SideOffline` | `Core/Switching/SwitchRefusal.cs`; `SwitchPlanningInput` gains `SlotState TargetSlot` |
| `RefreshOutcomeKind` | existing enum, extended | `HeldElsewhere` | `App/Quota/RefreshOutcome.cs`, alongside `Skipped` and `NeedsLogin` |
| `WslSwitchStep` | enum | `Claimed`, `ExportVerified`, `Imported`, `Parked` | the leader coordinator's journal. `ExportVerified` is the native read of section 9.2's export gate |
| `ImportStep` | enum | `Planned`, `Staged`, `Exported`, `Swapped`, `Released`, `Patched` | the follower's journal |
| `ImportRequest` / `ImportResult` | records | request: `AccountEmail Email`, `string ClaimedPath`, `RefreshTokenFingerprint Fingerprint`, `OAuthAccountBlock Account`, `string ExportPath`. Result: `AccountEmail? Outgoing`, `RefreshTokenFingerprint? OutgoingFingerprint`, `OAuthAccountBlock? OutgoingAccount`, `bool AlreadyImported` | fingerprints are SHA-256 of tokens, already printed by journals and the acceptance script; **no token crosses the wire** |
| `ICredentialPairStore` | existing port, unchanged signature | | the follower's adapter implements `MoveParkedToLiveAsync` as "stage from the claimed file and swap" and `MoveLiveToParkedAsync` as "export to the mailbox"; the doc comment's "by a rename on one volume" gains "or by the staged hand-off on a follower" |
| `IPeerRotationInstance` | port | `ReadDashboardAsync`; `ImportAsync(ImportRequest)`; `CommitImportAsync(email)`; `AbortImportAsync(email)`; `ImportStatusAsync(email)` | adapter `HttpPeerRotationInstance`. The commit and abort pair is the export gate of section 9.2 |
| `IPeerProcessHost` | port | spawn and supervise the follower | adapter `WslDistributionPeerHost`, `wsl.exe -d <distro> -u <user> --exec` |

## 8. Configuration

Leader (Windows), existing keys unchanged, additions:

```json
{ "role": "leader", "store": { "shared": true },
  "peers": [ { "side": "wsl", "baseAddress": "http://127.0.0.1:48212",
               "storePathFromPeer": "/mnt/c/Users/<user>/.claude-profiles",
               "launch": { "distribution": "Ubuntu-26.04", "user": "<user>",
                           "executablePath": "/home/<user>/.local/bin/claude-code-account-rotation",
                           "port": 48212 } } ] }
```

Follower (WSL), under the platform local app data dir:

```json
{ "role": "follower", "listenPort": 48212,
  "mailbox": "/mnt/c/Users/<user>/.claude-profiles/.transit/wsl",
  "claudeExecutable": "/home/<user>/.local/bin/claude" }
```

`storePathFromPeer` is how the leader spells its own store in the follower's namespace when it sends
an `ImportRequest`; dotfiles derive it with `wslpath` from the Windows user profile at apply time, so
no path is ever typed (Brief "no hard-coded assumptions").

`ConfigurationValidator` amendment: a follower validates its live dir (which must not be under
`/mnt/`), its app data (same volume as the live dir), and that `mailbox` exists and is writable. The
leader's validator is unchanged, its store being same-volume. **The mailbox is the one path in the
product allowed to be on the other volume, and only files under it are ever copied.**

## 9. The hand-off protocol

A WSL switch from A (live in WSL) to B (parked in the store), clicked on the Windows page. Two
processes, two journals, one gate each. Fingerprints: `fa` for A, `fb` for B.

### 9.1 Leader steps (`WslSwitch` coordinator; the leader's mutation gate for L1-L2, and again for L4)

| Step | Action | Store afterwards | Leader journal |
|---|---|---|---|
| L1 | plan: `GET` the follower's dashboard (side online, live account A, `fa`); B's slot must be `Parked` and not stranded in recovery; no login running against B's folder; managed policy allows; A must be `HeldHere` for wsl or absent | | |
| L2 | **claim:** rename `<store>/B/.credentials.json` to `<store>/.transit/wsl/B.credentials.json`; write `<store>/B/holder.json {wsl, fb, since}` | B: mailbox / A: unchanged | `Claimed {A, fa, B, fb}` |
| L3a | release the gate; `POST /api/import` to the follower with `ClaimedPath`, `fb`, B's account block from its `profile.json`, `ExportPath = .transit/wsl/A.credentials.json.incoming`; wait up to 60 s. The follower stops at F4 and answers `Exported {fa}` | A: live + mailbox / B: mailbox + staging | |
| L3b | **the export gate:** read the exported file **natively**, on the store's own volume, and require its fingerprint to equal `fa`. On any mismatch, short read, or absent file, `POST /api/import/abort`, unclaim, and refuse the switch: nothing has been swapped, so the operator loses nothing | | `ExportVerified {fa}` |
| L3c | `POST /api/import/commit`; the follower performs F5 to F8 and answers `ImportResult` | | `Imported` |
| L4 | under the gate: rename the export into `<store>/A/.credentials.json`; write A's `profile.json` from `OutgoingAccount`; delete A's `holder.json` | B: none, live in WSL / A: parked | `Parked`, then cleared |

L2 and every parked-pair refresh unit run under the same in-process `CredentialMutationGate`
(`QuotaRefresh.cs:563-637`), so no token POST can straddle a claim: a refresh that read B before the
claim finds the slot empty at its compare-and-swap **before** it posts (`QuotaRefresh.cs:585-592`
refuses `PairChanged` when the file moved), and a refresh admitted after the claim sees no file and
records `HeldElsewhere`. This is what closes the strand race that an earlier cross-side-lock draft
could not.

### 9.2 Follower steps (two calls, under the follower's gate and the WSL live dir's `.oauth_refresh.lock` from F2 on)

`POST /api/import` runs F1 to F4 and stops. `POST /api/import/commit` runs F5 to F8, and the
follower refuses it unless its own journal reads `Exported`. **The swap that destroys the outgoing
account's last local copy never happens until the leader has read the export natively and said so**
(L3b). `POST /api/import/abort` unwinds from `Exported`. The lock and the gate are held across both
calls, with a 120 s idle timeout after which the follower aborts itself; a commit that arrives late
is answered "not imported".

| Step | Action | On disk afterwards (A / B) | Follower journal |
|---|---|---|---|
| F1 | idempotency: if `last-import.json` or the live pair already answers this request (live fingerprint is `fb` or its rotation and the owner record names B), return the stored result with `AlreadyImported` | | |
| F2 | take the refresh lock; read live A (`fa`, or none) | | `Planned` |
| F3 | **stage B:** copy `ClaimedPath` to `<live>/.credentials.json.incoming`; `fsync`; read back; fingerprint must be `fb` | A: live / B: mailbox + staging | `Staged` |
| F4 | **export A:** copy `<live>/.credentials.json` to `ExportPath`; `fsync`; read back through a fresh open; fingerprint must be `fa`. **Answer `Exported {fa}` and stop**; do not proceed without a commit | A: live + mailbox / B: mailbox + staging | `Exported` |
| F5 | **swap**, only on `POST /api/import/commit`: rename the staging file over `<live>/.credentials.json` (ext4, atomic replace); stamp mtime | A: mailbox / B: mailbox + live | `Swapped` |
| F6 | **release:** delete `ClaimedPath` | A: mailbox / B: live | `Released` |
| F7 | patch the state file's `oauthAccount` with B's block; write the owner record `{fb, B}` | | `Patched` |
| F8 | write `last-import.json {A, fa, A's block}`; clear the journal; release the lock; return `ImportResult` | | cleared |

Two-copy windows: B during F3-F6, A during F4-F5. In every window the second copy is under a name
only this follower and the leader's coordinator open, and the leader's planner and refresh pass
refuse the account while the mailbox holds it. Nobody can refresh a mailbox file: the leader's
refresh reads slots only.

**Why the export gate is worth its round trip.** F5 is the one step in the whole design that
destroys a lineage's last local copy, leaving A alive only as the exported file on the other volume.
The follower's own F4 read-back is a 9P read and can be served from the mount cache (`cache=0x5`),
and section 3 measured that `fsync` over DrvFs *returns 0* without measuring durability; the
Windows-reads-what-WSL-wrote direction is not in the probe table at all. A native read on the
store's own volume is the only check available that does not go through the layer under suspicion,
and it turns an unmeasured assumption into a per-switch verification whose failure costs a refused
switch rather than a login. The price is one extra request per WSL switch, against a hand-off that
already costs tens of milliseconds per 9P call.

### 9.3 Crash matrix

Follower dies mid-import; its reconciliation runs at start, under the gate, before any request. The
CLI may have rotated the live pair while the follower was down (the stale lock is stolen after 60 s,
`OAuthRefreshLock.cs:7-14`), so every rule allows for `fa` having become `fa'`.

| Follower journal at | Files say | Action | Reported to the next `ImportAsync` or `ImportStatusAsync` |
|---|---|---|---|
| `Planned` | nothing moved | clear | not imported; the leader unclaims (L2 reversed) |
| `Staged` | staging holds `fb` | delete staging; clear | not imported; leader unclaims |
| `Exported` | live is `fa` or `fa'`; mailbox holds an export `fa` | delete the export and the staging; clear | not imported; leader unclaims. Unwinding rather than completing keeps the existing rule that before the swap nothing changed for a session, so nothing is completed on its behalf. A commit arriving after this is answered "not imported" |
| `Swapped` | live is `fb` or `fb'`; `ClaimedPath` still holds `fb`; mailbox holds export `fa` | continue F6-F8 | imported; outgoing A |
| `Released` | as above minus the claimed file | continue F7-F8 | imported |
| `Patched` | owner record may be missing | F8 | imported |

Leader dies mid-coordination; its reconciliation at start, under its gate:

| Leader journal at | Files say | Action |
|---|---|---|
| `Claimed`, follower online | | re-issue L3a (idempotent by F1); then L3b, L3c and L4, or unclaim per the follower's answer |
| `ExportVerified`, follower online | export in the mailbox, follower journal at `Exported` | re-issue L3c; the export has already passed the native gate and is not re-read |
| `ExportVerified`, follower offline or journal cleared | export may be gone | treat as not imported: unclaim. Nothing was swapped |
| `Claimed`, follower offline | claimed file in the mailbox, record on the slot | leave both; card shows `in transit to wsl (offline)`; `Cancel` is offered only once the follower answers "not imported"; never a blind unclaim, since the follower may have swapped |
| `Imported`, park not done | export file in the mailbox | L4 by fingerprint |
| `Parked` | | clear |
| no journal, but a file under `.transit/wsl/` | | the same as `Claimed` or `Imported` by which name it carries; a slot without a record but with a claimed file gets its record written, idempotently |

No branch deletes a file whose fingerprint is not the one a journal names, and no branch copies
anything it did not verify by fingerprint after the copy. A pair is lost only if the live file and
every mailbox and staging copy are gone at once, which no step produces: F6 deletes the claimed file
only after F5 put the same lineage live, and L4 renames rather than copies. The one window where a
lineage could be lost rather than stranded, F4 to F5, is closed by the L3b export gate: the leader
reads the export natively before the follower is allowed to swap.

### 9.4 Reconciliation of records against files (leader, at start and on every dashboard read)

| Slot file | `holder.json` | Verdict |
|---|---|---|
| present | absent | `Parked` |
| present | present, any side | record stale: delete it, log `holder record dropped: slot holds a pair`; `Parked` |
| absent | absent, no mailbox file | `NeverLoggedIn` (or removed) |
| absent | `windows`, fingerprint matches the Windows live pair or its rotation (the existing owner-record rule, `LiveDirectorySwitch.cs:642-707`) | `HeldHere` |
| absent | `wsl` | `HeldElsewhere`; the side's liveness is shown separately, as it may be off |
| any | any, plus a file under `.transit/wsl/` naming the account | `InTransit`; older than 24 h with the follower offline is a banner, never auto-cleared |

### 9.5 The Windows side's own switch

Today's two renames, unchanged in mechanism. Additions under the leader's gate: the planner refuses
`HeldByOtherSide` (a `wsl` record on the target) and `SlotInTransit` (a mailbox file for the target
or the outgoing account); on unpark, write `holder.json {windows, fingerprint}` into the incoming
slot; on park, delete the outgoing slot's record. Both are one `AtomicJsonFile` write each inside the
existing journal window, so a crash leaves at worst a record that section 9.4 rewrites from the
files. Because the leader is the only slot writer, the existing `Rename` `FileNotFoundException` path
(`FileSystemCredentialPairStore.cs:149-152`) stays crash-only, as today.

## 10. Locks

- **Per-side live dir:** `.oauth_refresh.lock` as today, native `mkdir` on native file systems, taken
  by the leader around its own switch and by the follower around F2-F8. This is the only lock any
  single-holder argument here rests on, and it is the one the CLI itself uses.
- **Store:** none. The leader's in-process mutation gate is the store lock, because the leader is the
  only slot writer; the follower's mailbox is written by the follower and read by the leader only
  after the follower has answered. The cross-side `mkdir` finding in section 3 therefore costs
  nothing.
- **Claim by rename:** L2's rename is a single-volume move on the store's own volume, the primitive
  the app already trusts (`FileSystemCredentialPairStore.cs:142-165`); it is never raced by another
  process because only the leader renames slots.

## 11. Failure modes

| Situation | User sees | Credentials |
|---|---|---|
| Leader not running | the follower is its child, so down too; WSL sessions continue on their pair; no switch on either side until the leader restarts | nothing moves |
| Distro off or shut down | side `offline`; accounts held by WSL show `in use by wsl (offline)`, their Windows switch disabled; `Start WSL side` button | pairs stay in the WSL live dir on ext4 |
| Distro off and a WSL-held account is needed on Windows now | the card offers `Log in again on Windows`: a fresh family goes into the slot, the `wsl` record is overwritten with `superseded`; when WSL returns, the follower's next export of that account is refused at L4 (slot occupied by a different fingerprint) and the leader quarantines the export under the app data `quarantine/` with a banner. This is the one place two families reappear, by the user's explicit click | one extra short login |
| `/mnt/c` not mounted in the distro | the follower's validator refuses to start: `mailbox unavailable`; the leader shows the side `offline` with that reason | nothing moves |
| Windows user logged out | the 9P server runs as the Windows user, so the mailbox is unreachable; side `offline` | nothing moves |
| Follower or leader dies mid-hand-off | section 9.3 | completed or unwound at the next start |
| Follower unreachable between L2 and L4 | `in transit to wsl`; retried with backoff 5 s to 5 min while the journal is open; `Cancel` only after the follower answers | nothing lost: B sits in the mailbox or in WSL live, A in WSL live or in the mailbox |
| Version mismatch leader/follower | side `incompatible`, no import sent | nothing moves |
| Clock skew between sides | no cross-side instant comparison; `since` is display only; expiry judged per side as today | |
| Same account wanted on both sides | refused, `HeldByOtherSide`; the user picks a side | one family |
| Login expired on a WSL-held pair | the WSL side shows `login expired`; switch WSL away (the pair parks back dead, as a Windows-held expired pair would), then `Log in again` on Windows, as today | one short login, the monthly one |

## 12. Usage and rate-limit data across sides

- The leader reads the usage endpoint for all ten accounts from the store or its own live pair, as
  today. The WSL side makes **no** usage reads and **no** token POSTs (one honest-UA bucket; whether
  it keys per token or per client is still open, spike 02b).
- **Held-by-WSL accounts:** their pair is in the WSL live dir, which the leader does not read. The
  leader's card for that account uses the follower's tee (tier 1, `rate-limit-guard/rate-limits.json`
  inside the WSL live dir, attributed by `account.email`), surfaced through the follower's
  `GET /api/dashboard`; `UsageMerge` merges per bucket. A held account with no WSL session since the
  last reset shows `via snapshot` with its age, or `unknown`, and the card says
  `in use by wsl; figures come from wsl sessions`. On-demand refresh of a WSL-held account is
  deferred: it would spend a read from the shared bucket on the follower.
- **Login expiry:** one family per account, so one `loginExpiresAt` per card, read from the slot
  (parked), from the Windows live pair, or from the follower's dashboard (WSL-held).
- Per-card chips: `live here`, `in use by wsl`, `in transit to wsl`, `parked`, beside the existing
  quota standing. Proposal and queue logic stays Windows-only in V1.

## 13. Install and configuration split

| Piece | Owner | Mechanism |
|---|---|---|
| `store.shared`, `role`, `peers[]`, holder records, `WslSwitch` coordinator, follower `import` route, staged adapter, `linux-x64` publish | this repository | product code; the release workflow uploads `win-x64` and `linux-x64` |
| Leader autostart at logon (missing today) | dotfiles | Startup-folder shortcut, the existing start-menu-shortcut script's precedent |
| Leader `peers[]` values (distro name, WSL user, `storePathFromPeer`) | dotfiles | derived from the fleet manifest and `wslpath`, written by a `modify_` script over `config.json` |
| Follower binary in `~/.local/bin` | dotfiles, `isWsl` branch | the same shape as the existing Claude Code install script; refuses a `/mnt/` install target |
| Follower `config.json` (`mailbox`, `claudeExecutable`) | dotfiles, `isWsl` | template; the Windows profile path resolved with `wslpath` at apply time, never typed |
| Follower process lifecycle | this repository; the leader spawns `wsl.exe -d <distro> -u <user> --exec` | |
| WSL automount and networking mode | provisioning | no change; the follower depends on the automount default |
| `check-single-holder.sh` repeated roots plus `.transit/` and `*.incoming` names | this repository, `tests/acceptance/` | run from inside the distro, where all four roots are readable |

No piece of this delivers a secret. The follower's configuration is paths and a port; credentials
move only as files inside the machine. The dotfiles `vault-exec` work is unrelated to every phase
here.

## 14. Test strategy: no live login is ever at risk

**Unit and integration, both CI legs:**

- `StagedImportCredentialPairStore` against two temp directories: every step's on-disk state asserted
  by fingerprint after each of F3 to F6; a claimed file rewritten between the request and F3 fails
  the verify and unwinds; an export read-back mismatch unwinds; **the follower refuses to swap
  without a commit**, and a corrupted or truncated export makes the leader's native gate abort with
  the live pair untouched.
- Crash injection: a test-only `FailAfterStep` hook (through `SwitchOptions`) aborts the import after
  each `ImportStep`; a fresh executor over the same roots reconciles; assert the section 9.3 outcome
  and that every fingerprint the test's `CredentialFiles` helper wrote exists in exactly one
  non-staging file.
- `WslSwitch` coordinator against a fake `IPeerRotationInstance`: claim, export-gate, commit, park;
  the follower answering `AlreadyImported`; the follower timing out after `Claimed` (journal stays
  open, no unclaim); the follower answering "not imported" (unclaim: file back, record gone); an
  export that fails the native gate (abort, unclaim, live pair untouched on both sides); the leader
  restarting at each `WslSwitchStep`, `ExportVerified` among them.
- Planner: `HeldByOtherSide`, `SlotInTransit`, `SideOffline` over fixtures; the Windows planner
  refuses a slot carrying a `wsl` record.
- Refresh: the leader's pass records `HeldElsewhere` for a slot with a record and sends nothing; a
  refresh unit admitted after a claim refuses `PairChanged` before posting; the follower's pass sends
  zero usage GETs and zero token POSTs.
- Validator: the follower's `mailbox` must exist and be writable; a follower live dir under `/mnt/` is
  refused.
- Leader and follower in one process: two `AppFactory` instances
  (`tests/ClaudeCodeAccountRotation.App.Tests/AppFactory.cs:24`), the follower's mailbox a temp
  directory the leader's store also points at; drive a WSL switch through `/api/sides/wsl/...`; the
  leader's dashboard shows `in use by wsl`; the follower has no `POST /api/accounts` route (404).

**WSL acceptance on temp roots only:** a second follower in the distro with `--config <tmp>` whose
mailbox is a temp directory under `/mnt/c` (real DrvFs, real `EXDEV`) and whose live dir is under
`/tmp`, seeded with fake pairs and a fake `claude` script; a second leader on Windows with temp roots
whose store is that same temp directory. Twenty WSL switches and twenty Windows switches, `kill -9`
of the follower at each `ImportStep` and of the temp leader at each `WslSwitchStep`, each followed by
a restart and the section 9.3 outcome, then `check-single-holder.sh` over the temp roots:
`duplicates=0`. Neither real root is touched, and the operator's real instance keeps running.

**Live acceptance, once per machine, one click:** the migration steps, on the real roots, with the
operator present.

## 15. Amendments this design requires elsewhere

| Where | Today | Amendment |
|---|---|---|
| Brief, Terms posture (parent `PLAN.md`, Constraints) | "pairs are moved, never copied, so one holder exists at any moment" | "pairs are moved by rename on one volume; between the two volumes of one machine they are moved by a staged copy that is verified by fingerprint and finished or unwound by a journal, so that at most one reachable copy exists at any moment" |
| Parent plan alternatives row, "Cross-volume park and unpark by copy, verify, delete", switch condition "Never" | rejected | switch condition met: one family per account across the two volumes of one machine, at the user's request |
| Parent plan captured assumption, "refreshing a parked pair's token renews its four-week login" | assumed true | false; section 2 measured a fixed window |
| `ConfigurationValidator.cs:47-58` | refuses a cross-volume profiles root | unchanged for the leader; a follower has no profiles root and validates a `mailbox` instead |
| `ICredentialPairStore.cs:5-9,16-24` doc comments | "by a rename on one volume" | plus the staged hand-off on a follower |
| Issue #21 | fail on the same `accountUuid` across roots | one family per account **per machine**; a second family is `foreign family`, always shown, never silent |
| Brief Q25 | two accounts live at once is out of scope | one live account **per side** |

## 16. Decisions, resolved

| # | Decision | Resolution |
|---|---|---|
| 1 | Shared store versus two families | **Shared store.** Section 2 measured the fixed window, which was the only fact that could have flipped it |
| 2 | Retire the pre-existing WSL token families | **Retired**, by hand, on both machines: the laptop's WSL lane logged out 2026-09-19, the desktop's 2026-09-20. No account holds two families anywhere today |
| 3 | Follower lifecycle | **Leader-spawned child.** One owner, and the follower is useless without the leader in this design. Revisit trigger: a want for the WSL side to work with the leader down, which NAT forbids anyway |
| 4 | Ports | leader 48211, follower 48212 |
| 5 | Work machine follower | **No.** The work machine is outside the fleet manifest and gets any setup by hand; no phase here targets it |
| 6 | Escape hatch when the distro is off | **Allow** `Log in again on Windows` on a WSL-held account, with the banner and the quarantine-on-return rule of section 11. Revisit trigger: a quarantine actually occurring |

## 17. Out of scope: a fleet-wide view of who holds what

Recorded on request, not designed and not part of any phase.

- **Stores are per machine.** Each machine has its own store and its own roster. Nothing here gives
  two machines a shared store; section 5's reasoning is about one machine's two volumes.
- **Holder records are local.** `holder.json` is written only by that machine's leader and names one
  of that machine's own sides. It carries no machine identity and nothing outside that machine reads
  it.
- **No shared transport exists.** The app listens on loopback and filters `Host`, so nothing off the
  host can read a dashboard; the leader-follower link is localhost forwarding plus `wsl.exe` inside
  one machine.
- **"Which host holds it" is not exclusive across machines.** One family per account is per machine,
  so with the two fleet machines an account can legitimately have two families at once, one per
  machine, and a fleet view would show up to two simultaneous holders rather than one.
- **Login expiry is per family, so per machine:** two independent clocks per account, not one.
