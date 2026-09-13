# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- The dashboard payload carries usage where it carried quota (#52). Each card's `quota` and
  `quotaNote` are replaced by `usage` (the source the newest figures came from, when they were
  taken, one row per bucket, and the usage-credits block) and `usageNote`, and each card gains a
  `refresh` object holding how its last turn in a pass went; the dashboard itself gains a `refresh`
  object with whether a pass is running, the lockout that is still standing, and one line on what
  the last pass came to. A card is merged per bucket across every source that has numbers for that
  account rather than letting the newest whole reading win, so a live session's statusline write can
  no longer blank a scoped row an on-demand read found. Nothing else in the repository read the old
  fields.
- Discovered browser profiles are listed in the order an operator reads them: `Default` first, then
  `Profile N` by the number, then any other directory name, within each browser. The browser's own
  `Local State` lists them in whatever order it wrote them, which is a deletion history (the directory
  counter never goes down), so the picker used to show `Profile 10` before `Profile 2` and every gap
  as if it meant something. Nothing about the persisted mapping changes: the roster still stores the
  directory name and the launcher still passes it verbatim.
- The roster card names the mapped browser profile the way the browser does (`edge / Personal
  (Profile 4)`) when the discovered catalog can resolve the pair, and shows the bare directory as
  before when it cannot.
- The read-only `GET /api/login-sessions/{id}` route records in code why it sits outside the
  same-origin mutation filter, beside the other reads (#43). No behaviour change.
- `GET /healthz` is served by the framework's own health-check middleware rather than a hand-rolled
  route, with no checks registered: it is a liveness probe and nothing more. The body is now the
  status word `Healthy` as `text/plain` instead of a small JSON object, and the response carries the
  `Cache-Control: no-store, no-cache` headers the middleware writes, so an intermediary never answers
  the probe from a cache. Nothing in the repository parsed the old body.
- Renamed the project to `claude-code-account-rotation` (#5): the repository, the executable, the
  User-Agent product token, the per-user app data directory, the mutation header
  (`X-Claude-Code-Account-Rotation`), the solution and project names, and the
  `ClaudeCodeAccountRotation.*` namespaces. An existing app data directory named `account-rotation`
  is moved by hand once; see the README's upgrading note.

### Added

- Every card says what its account has left (#52). The five-hour window, the seven-day window, and
  every scoped window the endpoint reports each get a row with its percentage, its reset time, and a
  line naming the source the figures came from and the time they were captured. A bucket no source
  has ever carried says `unknown` rather than showing a zero it does not know, and a bucket whose
  window has reset since it was captured says so instead of showing a figure that now measures
  nothing: a card that silently shows a stale number is worse than one that admits it is stale.
  `Refresh all` in the header reads every account that has credentials (a paused one is only
  renewed, never read), one read each, the one read longest ago first; a card's own `Refresh` reads
  that one account. Both are routes in the same-origin mutation group, `POST /api/refresh` and
  `POST /api/accounts/{email}/refresh`, and both answer at once and leave the pass to a hosted
  background worker, because a browser navigating away must never interrupt a credential operation;
  the page's ten-second poll is what shows each card land. Reads are paced and budgeted, and a 429
  from either host ends the pass and refuses every later read until its `Retry-After` has expired,
  which each waiting card reports as `rate limited, retry in N s` while keeping its place at the
  head of the next pass.
- A parked account's login is refreshed as a matter of course while its usage is read (#52). Its
  access token has almost always expired by the time a pass reaches it, so refreshing it is the
  normal path rather than the exception: one token request, and the rotated pair written back under
  the credential mutation gate by compare-and-swap against the fingerprint the folder still holds,
  with every sibling key left as it was. The live account is never refreshed by this tool, because
  the running session owns that lineage and renews it itself. A write-back that cannot land after
  its retries parks the rotated pair in an owner-only file under the app data directory's
  `recovery/`, since the old refresh token dies the moment the token endpoint answers and that file
  is then the only copy of a working login: the card reads `credentials stranded in recovery`, a
  switch to that folder is refused by the server rather than only greyed out on the page, removing
  the account is refused until the file is resolved, and the next start and the next per-card
  refresh both try the restore through the same compare-and-swap. A recovery file whose folder was
  legitimately rewritten since is moved to `recovery/stale/` and named in a warning instead of
  overwriting the newer pair. `tests/acceptance/check-single-holder.sh` counts and names what those
  two directories hold, on a `recovery=` line of its own, because a recovery file is a second
  on-disk holder of a lineage for as long as it stands.
- The figures survive a restart, and a paused account's login no longer lapses unnoticed (#52).
  Every successful read is kept in `state/usage-cache.json` under the app data directory and loaded
  back at start, so installing a build costs no reads and each card renders `via cached` with the
  time it was originally captured rather than falling back to `unknown`. A cached row whose window
  has reset since then still blanks through the same rule, which is what keeps `via cached` from
  meaning "a number from nowhere". The file holds percentages, reset times, bucket names, and
  account addresses, and no token of any kind; a missing or unreadable one loads nothing rather than
  delaying the start. A paused account is read by no pass, which is what used to let its login run
  out unnoticed until a switch to it failed, so `Refresh all` now renews the login of a paused
  account that is within a week of expiring — one token request and the same write-back, no usage
  read, nothing taken from the read budget, and not again for a day.
- A repo-level `nuget.config` that clears every inherited package source and names nuget.org as the
  only one, for restore, audit, and package-source mapping alike. `dotnet restore --locked-mode`
  then resolves the same way on a developer machine whose user-level NuGet configuration enables
  other feeds as it does on CI, instead of failing with NU1507 because more than one source could
  serve a package. The committed lock files record package identities, not sources, so they are
  unchanged.
- The browser profiles this machine already has are discovered rather than typed. Each
  Chromium-family browser publishes its own profiles in its `Local State` file: the profile
  directory, the name the browser shows for it, and usually the address signed into it. A new port
  and adapter read that for Chrome, Edge and Brave, `GET /api/browser-profiles` returns it, and the
  roster's browser-profile field is a grouped select instead of a free-text box asking for a
  directory name nobody knows. Typing an account's e-mail into the Add form pre-selects the profile
  already signed into that address, and its browser with it, which is the mapping for almost every
  account an operator adds; the selection stays overridable, and a profile the enumeration missed
  is still typeable. Each option names the display name, the signed-in address, and the directory,
  because the first two are what the operator recognizes and only the last one starts a browser:
  Edge writes a profile whose directory is `Default` and whose display name is `Profile 1`. The
  read is read-only and forgiving in every direction: an absent file, an unreadable one, malformed
  JSON, and a file with no profile cache all mean "no profiles for that browser", so a browser the
  operator never installed cannot take down the page for the ones they did.
- Browser-assisted login, so a parked account is logged in from the page instead of from a hand-run
  `/login` in a terminal. Login runs the unmodified CLI under that account's own folder as its
  `CLAUDE_CONFIG_DIR`, captures the sign-in URL it prints, and opens that URL in the browser profile
  the roster maps to the account, with the address already filled in. The operator pastes the
  one-time code into the card; a rejected code is a retry inside the session's ten-minute expiry
  rather than a dead session, and the child process is killed on expiry, on cancel, and at shutdown.
  A login the browser could not be opened for still hands back the URL to open by hand.
- Completing a login rewrites the folder's `profile.json` from the state file that login just wrote,
  and only then prunes the residue, so a folder logged in a second time as a different account can
  never keep the first account's name. The rewrite happens once the CLI has ended, not while it is
  running, and a login whose state file names no account leaves the folder exactly as it is rather
  than pruning away the only copy of its identity.
- The account roster and its page controls: Add, Pause, Remove, and Adopt, so getting the other
  accounts onto a machine never means editing a file or reaching for curl. Add creates the profile
  folder and the card says "needs login"; Pause takes an account out of the ranked queue without
  taking it off the page; Adopt puts the account the machine is already logged in as on the roster;
  Edit remaps an account's alias, browser, and browser profile directory. Only Max accounts join: a
  Team or Enterprise seat is refused with its reason, because that seat exposes no usage buckets and
  is never rotated. An account with no login to read yet joins as "needs login" rather than being
  refused. The roster lives in `roster.json` under app data and is written through the same atomic
  path as every other file the tool owns.
- Removing an account revokes its login by default (`DELETE /api/accounts/{email}?logout=`). A
  deleted folder leaves recoverable bytes holding a refresh token valid for the rest of its 28 days,
  so revocation is what removal means: a failed logout refuses the delete rather than stranding a
  live token, and `logout=false` is the operator's deliberate override, with the response saying
  plainly that the token was not revoked. A folder holding no credential pair skips the logout,
  since there is nothing to revoke. Removing the live account is refused.
- A browser launcher for the Chromium family: the executable resolves from the new
  `browserExecutables` configuration overrides first, then from the platform's known install
  locations for Chrome, Edge, and Brave, and the profile directory and the URL are passed as an
  argument array rather than a command line, so ten accounts get ten browser profiles and no URL is
  ever parsed by a shell.
- Quota reads: the usage response's generic `limits[]` array and `extra_usage` block are parsed into
  card-ready types, so the five-hour, weekly all-models, and any weekly scoped bucket render without
  code changes when Anthropic adds or renames one.
- Live-session quota from the `rate-limit-guard` tee file, the free tier of the refresh contract. A
  card shows a snapshot only when the snapshot names that card's account, so the outgoing account's
  windows are never read as the incoming account's after a switch. The tee path is derived from the
  live config directory, so it follows wherever that is set.
- A per-account refresh budget: six usage reads per five minutes with a sixty-second minimum gap and
  a lockout honored from the endpoint's own `Retry-After`, driven by an injectable clock.
- The two outbound adapters, for the usage endpoint and the OAuth token endpoint, each sending an
  honest User-Agent naming the tool, its version, and its home, with a twenty-second timeout and
  typed failures. The token refresh returns the rotated tokens to its caller and writes nothing.
- Solution skeleton: `ClaudeCodeAccountRotation.Core` (BCL only), `ClaudeCodeAccountRotation.App` (Kestrel on loopback),
  and their test projects under the org's strict analyzer posture.
- Machine-wide account switch: the loopback page lists the live account and every parked profile,
  and Switch parks the live credential pair, unparks the chosen one under Claude Code's own refresh
  lock, patches only the state file's account block, and verifies with `claude auth status`. Every
  open Claude Code session follows on its next request.
- Startup reconciliation: duplicate credential lineages are quarantined under app data, and a switch
  a crash left half done is finished or unwound from the journal.
- Configuration under the per-user app data directory with every path derived at runtime, refusing
  a profiles root on another volume, inside the live directory, or under a sync folder.
- Acceptance runbook and scripts under `tests/acceptance/` for the criteria that need real sessions.

### Fixed

- A swept temporary file is classified by the name it was going to take rather than by parsing it, so
  a write a crash truncated is quarantined rather than deleted: those bytes can be the only copy of a
  rotated refresh token. A 401 refund is remembered for the rest of the window, so a rejected token
  cannot loop against the endpoint without bound. A `Retry-After` is clamped to an hour.
- Files this tool creates are readable by their owner alone on Windows as well as on Unix, and a
  temporary file a crash left behind is swept at startup: one holding a credential pair moves to
  quarantine, where the lineage scan and the operator can both see it, and any other is deleted
  (#10).
- `check-single-holder.sh` fingerprints every dotfile temp beside a credential or state file, not
  just the settled `.credentials.json`, so a crash temp holding a duplicate refresh token can no
  longer hide behind its filename and print a false `duplicates=0`; a temp too damaged to parse is
  counted as `unreadable` and fails the run instead of being silently skipped (#20). The state-temp
  scan follows `CLAUDE_CONFIG_DIR` the same way the app does, so a stale temp left at the home root
  by an old default-root install no longer fails a clean run under a custom config root.
- Two accounts can no longer normalise onto one profile folder. `a<b@x.com` and `a>b@x.com` both
  became `a_b@x.com`, and `user@example.com.` was trimmed onto `user@example.com`, so the second
  account's login overwrote the first account's pair and removing either revoked the other's token.
  The e-mail rule refuses every character that collapsed, and a dot at either end.
- A removal takes the one credential mutation gate, re-reads the live account under it rather than
  before it, and runs the logout and the delete under a cancellation-proof token, so it can no
  longer land inside a switch and leave the pair in neither place. It also refuses while a login is
  running against that folder, which used to leave the "removed" account reappearing with a fresh,
  unrevoked token as soon as the operator pasted the code.
- Two logins can no longer start against one profile folder: the check and the registration are one
  step under the gate rather than a scan followed by a spawn. A second code pasted while the first
  is still being checked is refused instead of overwriting the first caller's completion signal and
  leaving it to wait out its whole reply budget.
- A switch refuses, rather than races, while a login is running against either folder it would
  move. The login's live-account check is also re-read under the gate at the moment the child is
  spawned, so a switch completing in between can no longer leave one account holding two valid
  credential pairs.
- A failing `claude auth status` or `claude auth logout` reports its exit code to the caller and
  sends what the child printed to the log, instead of returning that output in the message the page
  renders.

### Security

- `POST /api/accounts` and `PATCH /api/accounts/{email}` refuse a `browserProfileDirectory` that is
  not a single plain directory name (a path separator or another character no directory name can
  hold, `.` or `..` or a trailing dot, a control character, a reserved device name, leading or
  trailing whitespace, or more than 255 characters) with a 400 and store nothing, and the browser
  launcher holds the same rule for a name the roster file already carries, refusing the launch with
  the reason instead of emitting the switch (#43). The value becomes `--profile-directory=<value>`
  verbatim, which the browser appends to its own user-data directory without normalising it, so a
  dot-segment walks elsewhere and a trailing dot or an NTFS stream suffix aliases a sibling profile on
  Windows.
- A Team or Enterprise seat could reach the rotation through the login. The tier was judged only when
  an account was added or adopted, and an account with no folder yet has nothing to judge, so it
  joined as "needs login"; whichever account was then picked at the browser step was accepted with no
  second look. One address can carry both an Enterprise seat and a personal Max account, and the
  browser offers both, so a single misclick put the seat that is never rotated into the rotation. The
  tier is now judged again once a login has written its credentials and its `profile.json`, under
  that account's own folder and against the same admission the roster admits by. Anything but Max is
  refused, including a tier that could not be read: the login is revoked with `claude auth logout`
  under that folder, the pair it wrote is deleted (a switch admits any folder holding one, whatever
  its token is worth), and the session's message names the subscription the CLI reported and says the
  credentials were revoked. The roster entry stays, so the account can be logged in again and the Max
  account picked instead. A revocation that fails still deletes the pair and says so. The judgement
  takes no identity the folder records, because a folder logged in before carries the earlier
  login's `profile.json` and a login whose tidy-up could not run leaves its state file behind, so
  nothing on disk proves which login wrote a block naming a Max tier. And the guard discards only a
  pair the login is proven to have written: the credential file is read before the CLI starts and
  again when it ends, the same bytes are the earlier login left untouched (the session ends the way
  a login that wrote nothing does), and a pair that was rewritten but whose tier cannot be read is
  kept, with the message saying to remove the account before switching to it, rather than deleted;
  that kept folder is left exactly as the CLI left it, with no `profile.json` rewritten and no
  residue pruned, and a fault while judging is settled the same way rather than left pending.
- Command injection through the `cmd.exe` shim used for an npm-installed CLI. Arguments were joined
  with a space and no quoting, so an account e-mail carrying `&` was read as a command separator and
  the rest of it ran as a second command. Every argument is now one quoted operand at the shared
  construction point, and the two characters quoting cannot neutralise, a quote and a percent sign,
  are refused there. The account e-mail rule is an allowlist rather than a blocklist as well:
  letters, digits, and `. _ - + @`, with no dot at either end.
- The same-origin check on every mutating route compares the `Origin` header against this instance's
  configured listen address rather than against the request's own `Host`, which the same request
  supplies. `AllowedHosts` is set to the three loopback names, so the framework's host filter refuses
  a rebound name before the pipeline reaches a route.
