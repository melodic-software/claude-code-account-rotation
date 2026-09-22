# claude-code-account-rotation

Account switching and quota dashboard for Claude Code.

A single local executable that serves a loopback web page showing every one of your Claude Max
accounts with its 5-hour and 7-day headroom, ranks them by earliest weekly reset, and switches the
whole machine to the account you pick, with no browser step. Every open Claude Code session follows
the switch on its next request.

Status: pre-release, under construction. The confirmed Brief and the approved implementation plan
live in `docs/topics/claude-subscription-rotation/PLAN.md`.

## Posture

- Nothing calls the model API outside the unmodified `claude` binary.
- Credential pairs are moved between your own files on your own machine, never copied, so each
  refresh token exists in exactly one place. Handing an account to the other side of the same
  machine (a WSL distribution) crosses a volume, where a rename does not exist: there the pair is
  staged, verified by fingerprint, promoted and deleted under a journal, so at most one *reachable*
  copy exists at every instant. Nothing is ever copied to another machine.
- The tool identifies itself honestly on every request it makes.
- Every switch is a human click; nothing rotates on its own.

## Build

Requires the .NET SDK pinned in `global.json`.

```bash
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

## Upgrading from `account-rotation`

The tool was named `account-rotation` until 2026-09-07. The executable, the User-Agent product
token, and the per-user app data directory carry the new name, and the tool does not migrate the
old directory. Stop the old executable, then move the directory once, contents unchanged:

- Windows: `%LOCALAPPDATA%\account-rotation` to `%LOCALAPPDATA%\claude-code-account-rotation`.
- Linux and macOS: `account-rotation` under the local application data directory (`~/.local/share`
  unless `XDG_DATA_HOME` is set) to `claude-code-account-rotation` beside it.

Parked profiles under `~/.claude-profiles` and Claude Code's own `~/.claude` are not affected.

## Upgrading a release

This is how you move from one tagged release to the next. It is separate from the rename above.
Releases from v1.0.0 on read the files the previous release wrote. There is no migrator and no
schema bump. Do not edit `config.json`, `roster.json`, or `state/live-owner.json`, and do not
copy a template over an existing config.

The app does not call the GitHub API, and the dashboard has no "update available" line. Compare
`claude-code-account-rotation --version` with
<https://github.com/melodic-software/claude-code-account-rotation/releases/latest>.

1. If the logon task from melodic-software/provisioning#552 is installed, disable it first. This
   repository does not name that task.
2. The page must show no blocking banner, so no switch or import is in flight. Look while the
   tool is still running.
3. Stop the leader and the follower. There is no shutdown route in this repository (#91 owns
   that), so stop both externally.
4. Download both release assets, `claude-code-account-rotation-win-x64.exe` and
   `claude-code-account-rotation-linux-x64`, and `SHA256SUMS`. Check each binary against the
   digest that file lists for it. Replace nothing until both match.
5. Replace both binaries, then start them. A version mismatch marks the other side incompatible,
   so both sides have to be the release you just checked.

## Uninstall

There is no `--uninstall` switch, no uninstall script, and no uninstall route. Remove the tool by
hand, in this order.

1. If the logon task from melodic-software/provisioning#552 is installed, disable it. This
   repository does not name that task.
2. Stop the leader and the follower. There is no shutdown route in this repository (#91 owns
   that), so stop both externally.
3. If the page warned that a rotated pair is stranded (a card reads `credentials stranded in
   recovery`), start once so recovery can restore it. Confirm that side's `recovery/` directory
   has no `*.credentials.json` outside `stale/`, then stop again.
4. Read `appDataDirectory`, `profilesRoot`, `liveConfigDirectory`, and `stateFilePath` from
   `config.json`. Read the leader's file, and the follower's when `peers[].launch.configPath`
   names one, and take each side's `appDataDirectory` from its own file.
5. Delete each side's app data directory. Also delete `config.json` when `--config` or
   `peers[].launch.configPath` put that file outside the app data directory.
6. Delete the executables you installed.

`config.json` sits in the app data directory unless `--config` pointed somewhere else. That
directory is `%LOCALAPPDATA%\claude-code-account-rotation` on Windows, and
`claude-code-account-rotation` under the local application data directory (`~/.local/share`
unless `XDG_DATA_HOME` is set) on Linux and macOS. Those four keys name the directories: delete
each `appDataDirectory`, and leave `profilesRoot`, `liveConfigDirectory`, and `stateFilePath`.
A file that moved them is the authority, not these defaults.

Leave the profiles root. That tree holds the parked pairs, `holder.json`, `superseded.json`, and
`.transit`. Leave the live directory and Claude Code's state file. Parked profiles under
`~/.claude-profiles` and Claude Code's own `~/.claude` are the defaults those keys name.

What disappears with the app data directory: the roster alias, notes, pause, and browser
mapping; the quota cache; the computed queue; and quarantined families.

What survives: the live Claude Code login, and the parked pairs.

Remove on a card is the explicit per-account logout. Uninstall never calls it, and never runs
`claude auth logout` against the live directory.

After a later install, parked folders show as not on the roster, and Adopt is offered only for
the live account.

## License

MIT, see `LICENSE`.
