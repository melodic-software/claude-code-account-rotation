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

## License

MIT, see `LICENSE`.
