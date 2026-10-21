# claude-code-account-rotation

Account switching and quota dashboard for Claude Code.

A single local executable that serves a loopback web page showing every one of your Claude Max
accounts with its 5-hour and 7-day headroom, ranks them by earliest weekly reset, and switches the
whole machine to the account you pick, with no browser step. Every open Claude Code session follows
the switch on its next request.

Status: a tagged release exists.

## Posture

- Nothing calls the model API outside the unmodified `claude` binary.
- Credential pairs are moved between your own files on your own machine, never copied, so each
  refresh token exists in exactly one place. Handing an account to the other side of the same
  machine (a WSL distribution) crosses a volume, where a rename does not exist: there the pair is
  staged, verified by fingerprint, promoted and deleted under a journal, so at most one *reachable*
  copy exists at every instant. Nothing is ever copied to another machine.
- CI tokens never leave memory. An account's Console API key is the exception: you paste it once
  per machine, and it is kept only in the OS store (Windows Credential Manager, persisted for this
  computer only, or the Secret Service through libsecret on Linux). The tool never writes it to a
  file of its own, a log or a page, and never copies it to another machine. The key belongs to your
  user on this machine, so setting or removing it needs your interactive sign-in: an SSH or service
  session has no credential store (Windows error 1312). With no usable store, setting a key is
  refused with the cause. A store that does not answer within 15 s is reported as a failure, but
  the write or delete may still complete.
- Removing an account, or its key, deletes the stored key. If the roster records a key and the
  store cannot be reached or refuses, the removal is refused with the cause and the account stays
  on the roster with its key record (a removed account's login and folder are already handled);
  `?keepStoredKey=true` goes ahead and names the key left behind and the command that removes it
  (`cmdkey /delete:<target>` on Windows; on Linux, unlock the keyring first, since `secret-tool
  clear` removes only unlocked items).
- The tool identifies itself honestly on every request it makes.
- Every switch is a human click; nothing rotates on its own.
- The tool never calls the model API.
- It reads the undocumented usage endpoint with its own User-Agent, which the research rates GRAY
  (no Anthropic statement either way), not ALLOWED. It reads only when the dashboard is opened,
  Refresh is pressed, or the rate-limit stop hook fires, at most once a minute per account, never
  on a timer. It reads GitHub only through `gh api`, with gh's own User-Agent and sign-in, and
  only when you click Check roles, Generate CI token or Push API key: those read secret names,
  dates and visibility, never a value. The first rate-limit answer stops what was clicked, and
  nothing retries it. Writes go one at a time, at least a second apart. It changes a lane kill
  switch's value only when you confirm its toggle, and never creates one.
- The parked-pair refresh presents Claude Code's public OAuth `client_id`.
- An optional Admin key, stored per account like the Console key, lets Check API key read the
  organization's key list from the Admin API (`GET /v1/organizations/api_keys`) and say whether
  the stored Console key is archived, inactive, expired or near its expiry. This is not a model API
  call: it reads key metadata, only on that click, with the tool's own User-Agent, and the first
  rate-limit answer ends it. It finds the key by the list's partial hint and says "cannot tell"
  when the hint is missing or fits more than one key. Individual accounts have no Admin API.
- Running the loop lanes (`work-loop`, `babysit-loop`, `attend-queue`) while rotating accounts is
  outside V1 because reader-side invalidation of a latched window is still an open problem.

### Terms of service and compliance

Last reviewed: 2026-10-05.

This is the maintainer's reading of public sources, not legal advice. Anthropic "reserves the right
to take measures to enforce these restrictions and may do so without prior notice"
([legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)), and it can change
its terms at any time.

#### Sources

| Source | URL | Effective or checked |
| --- | --- | --- |
| Consumer Terms | <https://www.anthropic.com/legal/consumer-terms> | effective 2025-10-08, checked 2026-10-05 |
| Commercial Terms | <https://www.anthropic.com/legal/commercial-terms> | effective 2025-06-17, checked 2026-10-05 |
| Usage Policy | <https://www.anthropic.com/legal/aup> | effective 2025-09-15, checked 2026-10-05 |
| Claude Code legal and compliance | <https://code.claude.com/docs/en/legal-and-compliance> | checked 2026-10-05 |
| Claude Code authentication | <https://code.claude.com/docs/en/authentication> | checked 2026-10-05 |
| Claude Code GitHub Actions | <https://code.claude.com/docs/en/github-actions> | checked 2026-10-05 |
| Claude Code desktop app | <https://code.claude.com/docs/en/desktop> | checked 2026-10-06 |
| Log in to your Claude account | <https://support.claude.com/en/articles/13189465-log-in-to-your-claude-account> | checked 2026-10-06 |
| Supported countries | <https://www.anthropic.com/supported-countries> | checked 2026-10-05 |
| Safeguards warnings and appeals | <https://support.claude.com/en/articles/8241253-safeguards-warnings-and-appeals> | checked 2026-10-05 |

#### What the terms say

- Consumer Terms §2: "You may not share your Account login information, Anthropic API key, or
  Account credentials with anyone else." and "You also may not make your Account available to
  anyone else."
- Consumer Terms §3 forbids access "through automated or non-human means, whether through a bot,
  script, or otherwise", "Except when you are accessing our Services via an Anthropic API Key or
  where we otherwise explicitly permit it".
- Legal and compliance: "Advertised usage limits for Pro and Max plans assume ordinary, individual
  usage of Claude Code and the Agent SDK."
- Legal and compliance: "developers may not collect, store, or intermediate Claude.ai credentials
  or session tokens — sign-in to a Claude account must complete through Anthropic's own flow".
- Legal and compliance: the restriction does not "prevent an end user from signing in to the
  unmodified Claude Code binary with their own Claude subscription".
- Usage Policy: no coordinating "malicious activity across multiple accounts to avoid detection or
  circumvent product guardrails", and no circumventing "a ban through the use of a different
  account".
- Authentication docs: "To stay signed in to multiple accounts at once, such as work and personal
  accounts, give each account its own configuration directory."
- GitHub Actions docs: a `CLAUDE_CODE_OAUTH_TOKEN` generated with `claude setup-token` works on
  Pro, Max, Team and Enterprise plans. For a secret shared across repositories the docs advise an
  API key instead, "since an OAuth token is tied to the subscription of the person who ran
  `claude setup-token`."
- No term in these sources limits how many subscriptions one person may hold, and none names
  usage-limit or rate-limit circumvention.

#### Operator checklist

- [ ] Every account in the roster belongs to you and is paid by you. Never share, lend or pool
  accounts or credentials: [Consumer Terms §2](https://www.anthropic.com/legal/consumer-terms).
- [ ] Keep Team and Enterprise seats out of the roster; the tool refuses them
  ([`MaxTierAdmission.cs`](src/ClaudeCodeAccountRotation.Core/Accounts/MaxTierAdmission.cs)), and
  they fall under the [Commercial Terms](https://www.anthropic.com/legal/commercial-terms).
- [ ] Use only the unmodified `claude` binary for model requests, with no proxy and no third-party
  harness on subscription credentials: the
  [legal page](https://code.claude.com/docs/en/legal-and-compliance) allows signing in to "the
  unmodified Claude Code binary".
- [ ] Sign in through Claude Code's own login flow (`claude auth login`), never by pasting tokens
  taken from somewhere else: sign-in "must complete through Anthropic's own flow"
  ([legal page](https://code.claude.com/docs/en/legal-and-compliance)).
- [ ] Switch by hand. Do not add automatic rotation at usage limits: limits "assume ordinary,
  individual usage" ([legal page](https://code.claude.com/docs/en/legal-and-compliance)); see the
  human-click bullet under [Posture](#posture).
- [ ] Keep the tool's own User-Agent and never imitate Claude Code's: Anthropic has banned tools
  that spoofed the Claude Code client ([public report](https://x.com/trq212/status/2009689809875591565)).
- [ ] Read usage only when asked, inside the existing budget: the usage endpoint is undocumented;
  see the read-budget bullet under [Posture](#posture).
- [ ] CI: role secrets are organization secrets visible to all of the organization's repositories,
  on purpose: every lane in the organization reads its role's pair. Each account you own makes one
  `claude setup-token` token, which goes into every role that account backs, and the lane launcher
  spends the role's API key first and falls back to that token
  ([run-claude-cli](https://github.com/melodic-software/ci-workflows/blob/main/.github/actions/run-claude-cli/action.yml)).
  Paste each account's Console API key once per machine; it stays in the OS store. Re-read the
  [GitHub Actions docs](https://code.claude.com/docs/en/github-actions), which advise an API key for
  a secret shared across repositories: the token "is tied to the subscription of the person who ran
  `claude setup-token`".
- [ ] Use Claude only from a [supported country](https://www.anthropic.com/supported-countries),
  and avoid VPN exits that change your region: account creation from an unsupported location is a
  listed [suspension reason](https://support.claude.com/en/articles/8241253-safeguards-warnings-and-appeals).
- [ ] Re-check the sources table whenever Anthropic announces a terms, limits or enforcement
  change, and at least once a quarter, then update the "Last reviewed" date above.

#### How this tool compares

| Behavior | Does it? | Where |
| --- | --- | --- |
| Model inference outside `claude` | No | [Posture](#posture), first bullet |
| Proxy between `claude` and Anthropic | No | [Posture](#posture), first bullet |
| Background timer for reads, refreshes or switches | No | [Posture](#posture), switch and read bullets; [`QuotaRefresh.cs`](src/ClaudeCodeAccountRotation.App/Quota/QuotaRefresh.cs) |
| Refreshes a parked token | Only during a requested read: when its recorded expiry has passed, or once when the usage endpoint answers 401, followed by one retried read. Done under the tool's own write gate; a switch holds Claude Code's own refresh lock | [`QuotaRefresh.cs`](src/ClaudeCodeAccountRotation.App/Quota/QuotaRefresh.cs), [`OAuthRefreshLock.cs`](src/ClaudeCodeAccountRotation.App/Adapters/FileSystem/OAuthRefreshLock.cs) |
| Usage reads | Bounded: at most once a minute per account, only on request | [Posture](#posture), read bullet; [`RefreshBudget.cs`](src/ClaudeCodeAccountRotation.Core/Quota/RefreshBudget.cs) |
| User-Agent | Its own on every Anthropic request; GitHub reads and writes go through `gh`, with gh's own | [`AnthropicEndpoints.cs`](src/ClaudeCodeAccountRotation.App/Adapters/Http/AnthropicEndpoints.cs), [`GhProcess.cs`](src/ClaudeCodeAccountRotation.App/Adapters/Process/GhProcess.cs) |
| Admin API reads | Only on Check API key, with a stored Admin key: the organization's key list, metadata only, never a model call | [Posture](#posture); [`AnthropicAdminKeysClient.cs`](src/ClaudeCodeAccountRotation.App/Adapters/Http/AnthropicAdminKeysClient.cs) |
| OAuth `client_id` for parked refreshes | Claude Code's public one | [Posture](#posture); [`AnthropicEndpoints.cs`](src/ClaudeCodeAccountRotation.App/Adapters/Http/AnthropicEndpoints.cs) |
| CI tokens | Only on a click: the unmodified `claude setup-token` makes the token, you sign in on Anthropic's page and paste the code, and the token goes from the CLI's output to `gh secret set` in memory, never to disk, a log, or the page | [Assigning accounts to CI roles](#assigning-accounts-to-ci-roles); [`ClaudeCliSetupTokenRunner.cs`](src/ClaudeCodeAccountRotation.App/Adapters/Process/ClaudeCliSetupTokenRunner.cs) |
| Console API keys | Kept only in the OS store, per machine; read only to hand to `gh secret set` on its standard input | [Posture](#posture); [`ApiKeyEndpoints.cs`](src/ClaudeCodeAccountRotation.App/Endpoints/ApiKeyEndpoints.cs) |
| GitHub reads | Only on a click, through `gh api`: secret names, dates and visibility, never a value | [Posture](#posture); [`GhApiReader.cs`](src/ClaudeCodeAccountRotation.App/Adapters/Process/GhApiReader.cs) |

Two points are gray:

- The usage endpoint is undocumented. No Anthropic statement allows or forbids reading it.
- The [legal page](https://code.claude.com/docs/en/legal-and-compliance) says "developers may not
  collect, store, or intermediate Claude.ai credentials or session tokens". The tool stores and
  moves your own tokens between your own folders, and refreshes parked ones with Claude Code's
  `client_id`. That sentence is addressed to "developers" building products that offer Claude
  sign-in to their users, which this tool does not do, but the page does not address a person
  managing their own credentials.

#### Known enforcement (public reports)

- Anthropic confirmed in January 2026 that it banned use of third-party tools that spoofed the
  Claude Code client: <https://x.com/trq212/status/2009689809875591565>.
- Account creation from an unsupported location is a listed reason for suspension:
  [safeguards warnings and appeals](https://support.claude.com/en/articles/8241253-safeguards-warnings-and-appeals).
- Unconfirmed anecdote, not confirmed by Anthropic: community reports link bans to automatic
  multi-account rotation that refreshes tokens in the background and polls usage for idle
  accounts: <https://www.reddit.com/r/ClaudeCode/comments/1uouvo8/> (2026-07-06).

#### Claude Desktop is out of scope

Last checked: 2026-10-06.

The tool switches the Claude Code CLI only. A switch leaves the Claude Desktop app, including its
Code tab, on whatever account it last signed in to. To change Desktop's account, sign out and back
in from Desktop itself.

What Desktop does:

- **It has its own login, not the CLI's.** The authentication docs say "Claude Desktop and cloud
  sessions do not call `apiKeyHelper` or read these environment variables: they use OAuth"
  ([authentication](https://code.claude.com/docs/en/authentication)). Desktop and the CLI share
  settings, MCP servers and `CLAUDE.md`, and the docs list no shared login
  ([desktop](https://code.claude.com/docs/en/desktop#coming-from-the-cli)). A request to share the
  CLI's login with Desktop was closed as not planned
  ([anthropics/claude-code#62206](https://github.com/anthropics/claude-code/issues/62206)).
- **Its Code tab counts against Desktop's account.** Desktop's usage ring shows "your plan usage
  for the period", and "plan usage is shared across all your Claude Code surfaces"
  ([desktop](https://code.claude.com/docs/en/desktop#check-usage)).
- **Anthropic documents one account switch only.** An individual account and a Team or Enterprise
  account on the same email can be switched from the initials menu
  ([log in to your Claude account](https://support.claude.com/en/articles/13189465-log-in-to-your-claude-account)).
  Desktop has no documented counterpart to the CLI's per-account configuration directory.

Community tools take two approaches:

- **Swapping Desktop's files.** These tools save and restore Desktop's stored login while it is
  closed: its cookie file, which holds the claude.ai web session, and the sign-in token entries in
  its `config.json`. Some also decrypt those tokens to read usage on a timer, or switch accounts
  automatically.
- **One data folder per account.** These tools start the official app with Chromium's
  `--user-data-dir` launch option, so each account signs in and refreshes inside its own folder and
  the tool never touches a credential. Anthropic does not document the option for Desktop. An open
  report in its tracker says a sign-in started in a second instance completes in the first one
  ([anthropics/claude-code#98549](https://github.com/anthropics/claude-code/issues/98549)).

Why the tool does neither:

- **Swapping files parks more than the CLI does.** Moving the CLI's credential files is already
  gray (see [How this tool compares](#how-this-tool-compares)). Desktop's cookie is a full claude.ai
  web session, the "Claude.ai credentials or session tokens" the
  [legal page](https://code.claude.com/docs/en/legal-and-compliance) names, and its storage layout
  is undocumented and can change with any Desktop update.
- **A data folder per account rests on an undocumented launch option.** It keeps credentials inside
  the official app, so it is the lower-risk route, but it has no Anthropic statement behind it and
  has open sign-in bugs. It stays out of scope until Anthropic documents it.
- **No public report ties a ban to either approach, and no Anthropic source allows either.** The
  absence of reports is weak evidence: these tools have few users.

## Install

Download the release from
<https://github.com/melodic-software/claude-code-account-rotation/releases/latest>.
The assets are `claude-code-account-rotation-win-x64.exe` (the Windows leader),
`claude-code-account-rotation-linux-x64` (the follower the WSL side runs),
`claude-code-account-rotation-follower-log` (the wrapper the leader starts in its
place), `SHA256SUMS`, and `config.template.json` (the template the executable embeds).

Check each binary against the digest `SHA256SUMS` lists for it. A release download does not keep
the executable bit, so on Linux run `chmod +x` on `claude-code-account-rotation-linux-x64` and
`claude-code-account-rotation-follower-log` before the first run. Place the executable on `PATH`,
or make a shortcut to it, and run it once.

A missing `config.json` is written on that first run from the embedded template. The written file
fills its paths from the user profile. It sits in the app data directory unless `--config` pointed
somewhere else. That directory is `%LOCALAPPDATA%\claude-code-account-rotation` on Windows, and
`claude-code-account-rotation` under the local application data directory (`~/.local/share` unless
`XDG_DATA_HOME` is set) on Linux and macOS.

The process prints `Dashboard:` and the bound URL. The default port is 48211. `--port 0` asks the
operating system for a free port for that launch. Without `--open` no browser opens: open the
printed URL, and paste line 2 of `instance.url` when the page asks for the instance token.

With `--open`, the dashboard opens in the default browser already signed in. If an instance is
already running, `--open` opens that instance's dashboard and exits. Otherwise, on Windows,
`--open` starts the leader as a separate process with no window, waits up to 30 seconds for it
to listen, opens its dashboard, and exits. If it does not come up, `--open` exits non-zero and
says why; run the same command without `--open` to watch it start in the console. On Linux and
macOS, `--open` with no instance running starts the process in the foreground as usual and opens
its own dashboard once it is listening.

The leader `--open` starts has its own hidden console, so closing an ordinary terminal window
does not stop it. A launcher running inside a job object that kills its processes on close still
takes the leader down with it; some IDE terminals and Win32-OpenSSH sessions do that. The
Start-menu shortcut and the logon task are not affected.

Stop the tool with the Stop button in the dashboard header. It stops the WSL follower first and
then the leader, and lets a rotation in flight finish. While a switch or import is in progress on
either side, it refuses, says why, and both keep running. That refusal is the only thing that
blocks the stop: any other follower answer, or none within 10 s, lets the leader stop, and the
page shows what the follower said. With more than one side in `peers[]` it refuses before
stopping anything; stop each process with `POST /api/shutdown` instead. The page cannot start
the tool again: run `claude-code-account-rotation --open`, or on Windows use the Start-menu
shortcut.

When the leader starts, it brings up every side that has `peers[].launch` set. It leaves a side
that is on its own version alone. It starts a side that is not running. A side on another
version is stopped and started again. It moves no account. If a side cannot be brought up, the
leader logs why and keeps running, and the side reads offline or incompatible. Start WSL side
on the page tries again. [Upgrading a release](#upgrading-a-release) says how this lets either
side be installed first.

Scripts use routes. `POST /api/shutdown` stops only the process it is sent to, on either side,
which is what an upgrade that replaces one side's binary wants. `POST /api/stop` on the leader
is the Stop button: send it in place of `/api/shutdown` below to stop both sides. Both answer 409
while a switch or import is in progress:

```powershell
$instance = Get-Content "$env:LOCALAPPDATA\claude-code-account-rotation\instance.url"
Invoke-RestMethod -Method Post -Uri "$($instance[0])/api/shutdown" `
  -Headers @{ Authorization = "Bearer $($instance[1])"; 'X-Claude-Code-Account-Rotation' = '1' }
```

`Stop-Process -Name claude-code-account-rotation` or Task Manager is a last resort: a hard kill
skips that drain, and a token refresh cut off mid-write can leave an account's rotated
credentials stranded.

If a card says it is not on the roster, click Adopt. Adopt is only offered for a Max account
that is already logged in on this machine. Otherwise use Add an account, then Login on the card
that needs a login. `profilesRoot` is the only hand edit, and the tool has to be restarted after
it.

## Rate-limit stop hook

When a session stops on a usage limit, Claude Code waits for the reset. The optional hook
`hooks/rate-limit-stop.sh` tells the running dashboard, which reads the live account's usage once
(at most once a minute per account). When a side's account is at 100% of its 5-hour or 7-day
window, or of a model's weekly window such as Fable's, the dashboard shows a "switch now" prompt
naming the first usable account in its order whose usage was read in the last 30 minutes. An
account at its Fable weekly limit is never named, because the tool cannot know which model a
session will run; you can still switch to it by hand. When every usable account's figures are
older than that, the prompt names none: it says which account is unverified and how old its
figures are, and offers Refresh usage. Nothing switches until you click it.

Install it on the side the leader runs on. The follower has no hook route, and the hook reads only
the account the leader's side is logged in as. For an account the other side holds, Refresh usage
asks that side to read it with its own access token, and its card also shows that side's
statusline figures.

1. Save `hooks/rate-limit-stop.sh` from the release tag you run, and on Linux run `chmod +x` on it.
   It needs `bash` and `curl`. On Windows, Git Bash is required: without it Claude Code runs
   hooks in PowerShell, where `bash` can resolve to WSL's `bash.exe` and the hook does nothing.
2. Add the hook to `~/.claude/settings.json`, with the path where you saved the script
   (Claude Code 2.1.78 or later):

   ```json
   {
     "hooks": {
       "StopFailure": [
         {
           "matcher": "rate_limit",
           "hooks": [{ "type": "command", "command": "bash /path/to/rate-limit-stop.sh", "timeout": 10 }]
         }
       ]
     }
   }
   ```

The script reads `instance.url` from the default app data directory and exits quietly when the
app is not running. It posts to `POST /api/hooks/rate-limit` with the headers `POST /api/shutdown`
takes. It answers 202 when a read started, or 409 with one of these `refusal` values:
`NoLiveAccount` (no account is logged in on this side), `RateLimited` (the account was read inside
the last minute, or the usage endpoint's lockout is still running), or `RefreshInProgress` (a
refresh is already running).

## Remote Control after a switch

A Remote Control session belongs to the account signed in when it started, and Claude Code stops
it once that account changes; nothing restarts it
([docs](https://code.claude.com/docs/en/errors#remote-control-stopped-because-the-signed-in-account-changed)).
So when you click Switch for the Windows side and the scheduled task named by `remoteControlTask`
(default `claude-remote-control`) is running, the dashboard first asks:
"Switch to X? Remote Control restarts under X; live Remote Control sessions on this machine drop
and reopen in X's Claude app." After the switch, and once `claude auth status` reports the new
account, the tool restarts the task:

1. It records the task's process tree: the process whose command line carries the task action's
   arguments, and everything under it (`conhost.exe`, `pwsh.exe`, then the `claude.exe` server).
   A `claude remote-control` you started in a terminal is not under that process and is never
   touched.
2. It ends the task. That ends only the root process, so it then ends the recorded processes
   still running, `claude.exe` first.
3. It waits up to 15 seconds for the task to stop, then runs it again.

- A task that was not running is left alone, and the switch asks nothing. When the page cannot
  read the task's state, it still asks, as a condition. The state comes from
  `Get-ScheduledTask`, whose state is a number, so it reads the same in every display language.
- A task whose state could not be read before the switch is restarted only if its processes are
  found; one found stopped is not started, and the result says so.
- A second switch sent while the first one's restart is running is refused (`MutationInProgress`).
- A switch of the WSL side, and any switch on Linux, restarts nothing.
- A restart that fails never fails the switch. A process that cannot be ended stops the restart
  before the task runs again, so two servers never run. A running task whose processes cannot be
  identified is neither ended nor run. The result says why and what to run by
  hand: `Stop-ScheduledTask claude-remote-control`, end the `claude.exe` running `remote-control`
  that it leaves behind, then `Start-ScheduledTask claude-remote-control`.
- `remoteControlTask` names a task in the Task Scheduler root folder; a path or a wildcard
  (`\ / * ? [ ]`) is refused at startup.
- Set `remoteControlTask` to `null` or `""` in `config.json` to turn this off. A file without the
  key uses the default.

## Reading the dashboard from scripts

`GET /api/dashboard`, sent with `Authorization: Bearer` and the token on the second line of
`instance.url`, returns more than the page needs. Only these fields are supported for scripts;
a change to any of them is a breaking change in the CHANGELOG. Everything else may change
without notice.

- `accounts[]`, in the dashboard's order: grouped `usable`, then `model-limited`, then `limited`,
  then `exhausted`, then `unread`, then `paused`. Within a group, accounts sort by `nextResetAt` (the weekly reset for a
  usable account), earliest first, with an undated one after every dated one, then by e-mail.
- `accounts[].email`.
- `accounts[].standing`: `usable`, `model-limited` (a model's weekly window, such as Fable's, is at
  100% and the 5-hour and 7-day are not; `nextResetAt` is that window's reset and
  `accounts[].limitedModel` names it), `limited` (the 5-hour window is at 100% and the 7-day is not;
  `nextResetAt` is the 5-hour reset), `exhausted` (the 7-day window is at 100%), `unread`, or
  `paused`.
- `accounts[].nextResetAt`: when the account frees up (ISO 8601), or null when there is no wait
  or none that can be dated.
- `accounts[].usage.limits[]` where `kind` is `session` (5-hour) or `weekly_all` (7-day):
  `percent` (null when unknown or the window has reset since the read) and `resetsAt`.
- `accounts[].isLive`: true for the account this side is logged in as.
- `accounts[].slot`: where the account's pair is when the store is shared with another side:
  `held-here`, `held-elsewhere`, `parked`, `in-transit`, or `never-logged-in`; null when the store
  is not shared.

Which side holds a `held-elsewhere` account comes from `GET /api/sides`, sent the same way. It
returns one entry per configured side (an empty list when none is). Supported fields:

- `[].side`: the side's name, for example `wsl`.
- `[].online`: whether that side answered.
- `[].liveAccount`: the account that side is logged in as, or null when it holds none. It is also
  null when the side did not answer, and that side may then still hold an account.

## Assigning accounts to CI roles

GitHub Actions lanes run Claude Code on a funding role's secrets. A role is a prefix plus its
organization (or repository): its token secret is `<PREFIX>_CLAUDE_CODE_OAUTH_TOKEN` and its key
secret `<PREFIX>_CLAUDE_API_KEY`. The bare `CLAUDE_CODE_OAUTH_TOKEN` is the
`legacy (review lanes)` role, with a token secret only, and any other name the roster kept from an
earlier release is a role with a token secret only. The lane launcher spends a role's API key
first and falls back to its token. Role secrets are organization secrets visible to all
repositories, on purpose.

One account backs each role, and an account can back several. Both halves of a role come from the
same account: the token from that account's `claude setup-token`, the key from that account's
Console API key, which you paste once per machine with Set API key and which stays in the OS store
(see [Posture](#posture)). A card shows one badge per role it backs, such as
`automations · token+key` (hover it for the secret names and dates). A token badge warns in the
last 30 days of the token's one year and says when it has expired, and the card lists, per expiry
date, which roles fall back to their API key only and which stop. CI draws on that account's
5-hour and 7-day limits alongside your interactive sessions on it.

Prerequisite: the GitHub CLI (`gh`) on PATH, signed in (`gh auth login`) as someone who may write
those secrets (`admin:org` for organization secrets). The tool checks this before a token is made.

1. Pick the card with the most 7-day headroom. If any role you will pick has a key half, store the
   account's Console API key first (··· menu, Set API key).
2. Open its ··· menu, then Generate CI token. Tick the roles it is to back. The list holds every
   role an account backs and, after Check roles, every role the organization's secrets name. To
   create a role, type its prefix and organization; the page asks before it creates the two
   secrets. Choose Start sign-in.
3. Anthropic's sign-in page opens in the account's mapped browser profile. Sign in as that
   account, then paste the code it shows into the card and choose Submit code.
4. The tool sets each role's token secret from the new token, then its key secret from the stored
   key, one at a time and at least a second apart. Before any token is made it reads who may see
   each existing secret and passes that back (`--visibility all`, `private`, or `--repos` with the
   selected repositories), since `gh secret set` otherwise makes a secret private; a new secret is
   visible to all repositories. A role the account takes is taken off the account that backed it.

The token and key never leave memory: they are not shown, logged, written to disk, or passed as an
argument. A failure before any secret is set (a rejected code, `gh` refusing every write, or a CLI
release whose screen the tool cannot read) ends the session without storing anything, and the
message gives the manual commands: `claude setup-token`, then `gh secret set <name> --repo
<owner/name>`, or `--org <org>` with the secret's current visibility (`--visibility all`,
`--visibility private`, or `--repos a,b` for selected repositories), since gh otherwise makes it
private. When some halves are set and others are not, the roster records the halves that were
set and the message names each one that was not. A role whose token was set but whose key was not
is `split` (token from one account, key from another): the account that set the half that failed
keeps it, and both cards say `split` and name both accounts until that half is re-sent.
Re-sending one half of a role an account already backs moves only that half. Push API
key, in the ··· menu, sends the stored key to the key secret of every role the account backs,
with no sign-in.
Use it after replacing a Console key too. The first rate-limit answer from GitHub stops every
write after it, and nothing retries it.

The same start is `POST /api/accounts/{email}/ci-token` with `{"roles":[{"prefix":"AUTOMATIONS",
"organization":"octo"}]}` (`tokenSecret` in place of `prefix` for a token-only role, and
`repository` in place of `organization`). The 2.1.0 body, one `secretName` with its repository or
organization, `visibility` and `repositories`, still works: it writes the token half only, to
that one secret. With an organization and no `visibility`, it keeps the visibility the secret has;
a secret that does not exist yet is created visible to all repositories when its name ends in
`CLAUDE_CODE_OAUTH_TOKEN` (a role's), and private otherwise, as gh's default made it.
Push is `POST /api/accounts/{email}/api-key/push`.

`claude setup-token` draws its screen only when its input is a terminal, so the tool runs it
under one: a pseudoconsole (ConPTY, Windows 10 1809 or later) on Windows, and a POSIX
pseudo-terminal on Linux. On macOS the action refuses and you run the commands by hand. The
terminal is 1,000 columns wide, so nothing on it wraps. The tool reads only two things from that
screen: the sign-in URL, which must be whole (with its `code_challenge` and `state`), and the
token, matched by its `sk-ant-oat01-` prefix. Exactly one token must appear, standing alone on
its row. CI runs the real CLI under that host on both Windows and Linux, so a release that
changes either one fails a check.

Check roles, in the header, lists the Actions secrets of every organization a role on the roster
names, and of one you type, with `gh api` (the `admin:org` scope reads them). It lists each role
the secret names imply, with the account that backs it, and judges each half the roster records:
`ok`; `changed outside the tool` when GitHub's `updated_at` is more than 2 minutes after the
tool's write, or, for a half recorded with a date only, on a later day in this machine's time
zone, where the tool records those dates; `unknown` for such a half updated that same day, or
one GitHub lists as older than the tool's write or with no readable time; and `missing` when the
secret is gone. A role whose secrets exist but that no account backs is `unclaimed`. It reads
only on that click, and the first rate-limit answer ends the check with no retry.

The same click reads the lane kill switches: the organization variables `CLAUDE_LANES_DISABLED`,
`CLAUDE_REVIEW_DISABLED` and `CLAUDE_SECURITY_REVIEW_DISABLED` of each organization it read, and
`CLAUDE_LANES_REPO_DISABLED` of each repository listed in `laneRepositories` in `config.json`
(`["owner/name", ...]`; absent means none). `CLAUDE_LANES_DISABLED` and
`CLAUDE_LANES_REPO_DISABLED` show running only when exactly `false`, and stopped on any other
value or when the variable does not exist. The two review switches show stopped only when exactly
`true`. Values compare exactly, so `False` or ` false` is not `false`. Each organization switch
has a Stop or Start button that asks first. It then reads the variable's visibility (and, when it
is `selected`, the ids of its repositories), runs `gh api --method PATCH
orgs/<org>/actions/variables/<name> -f value=<true|false> -f visibility=<as read>` (plus
`-F selected_repository_ids[]=<id>` per id), and reads the switch back. GitHub's documentation
marks visibility optional but does not promise an omitted one is kept, so the tool sends back the
visibility it just read. If that read fails, nothing is written. A variable GitHub answers 404 for is
reported as not set or not visible to this sign-in, and never created. The repository switch is read-only, and no repository or environment
variable is ever written. The route is `POST /api/kill-switches` with `{"org", "name", "value"}`;
any other name, or a value other than `true` or `false`, is refused before `gh` runs.

To mark an account by hand instead (for a token made outside the tool, or when the tool set the
secret but could not update the roster), open Edit and set "CI token generated on". Optionally
also set "CI secret name" and "CI secret in" (`owner/repository`, or an organization), then
Save; that adds the secret's role with its token set on that date. Untick a role in Edit to take
it off the account. The same writes are `PATCH /api/accounts/{email}`, sent with the headers
`POST /api/shutdown` takes, with
`{"ciTokenGeneratedOn":"yyyy-MM-dd","ciTokenSecret":{"name":"...","repository":"owner/name"}}`
(or `"organization"` in place of `"repository"`) to add one role, or with `"ciRoles"`, the
account's whole list: each item names `prefix` (or `tokenSecret` for a token-only role), then
`repository` or `organization`, and the halves set from this account, `tokenSetOn` and
`keySetOn` (`yyyy-MM-dd`). A token half needs its date; a key half alone needs no token date.
Naming a role takes it off the account that backed it before. A marker set in Edit that names no
secret is a hand marker: hand markers keep replacing each other, so at most one account carries
one, and a role claim never clears one. Clearing the date clears every token half. On an
account that backs a token half, the date is always that of its newest token half, whatever the
request sends, so taking off the newest role lowers it and taking off the last one clears it.

Every change of which account backs a role half is appended to `lane-history.jsonl` under the app
data directory: one JSON line with the time, the role, `token` or `key`, the account and the
previous account (or null), and the route (`assign`, `push`, `edit` or `delete`). It holds names
and dates only, never a secret value, and a line that does not parse is skipped.
`GET /api/lane-history?since=<date or ISO time>` returns the entries, and the page's History
disclosure reads the last 30 days when you open it.

## Build

Requires the .NET SDK pinned in `global.json`.

```bash
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

## Releasing

A fix pull request that should ship dates its CHANGELOG heading: `## [x.y.z] - YYYY-MM-DD`.
Ask the operator before dating a heading. When that change reaches `main`, the release workflow
tags the merge commit `vx.y.z` and publishes the release in the same run. A push to `main` whose
top dated heading already has a published release publishes nothing. Pushing a `v*` tag by hand
still publishes from that tag.

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

The app calls the GitHub API only through `gh`, for Check roles, CI secret writes, kill-switch
toggles and the secret metadata it reads on those clicks, and never
to look for releases; the dashboard has no "update available" line. Compare
`claude-code-account-rotation --version` with
<https://github.com/melodic-software/claude-code-account-rotation/releases/latest>.

Install the two sides in either order. Neither install has to wait for the other, and neither
has to stop the other side.

1. Download `claude-code-account-rotation-win-x64.exe`,
   `claude-code-account-rotation-linux-x64`, `claude-code-account-rotation-follower-log`,
   and `SHA256SUMS`. Check each binary and the wrapper against the digest that
   file lists for it. Replace nothing until they match. Place
   `claude-code-account-rotation-follower-log` in the same directory as the Linux binary.
2. **Windows side.** Stop the leader with `POST /api/shutdown`, replace the `.exe`, and start the
   leader. Send `Authorization: Bearer` with the token on the second line of the leader's
   `instance.url`, and the same `X-Claude-Code-Account-Rotation: 1` header as the other
   dashboard writes. A 409 means a switch or import is in flight, so the leader was left
   running: try again once it finishes. If the logon task from
   melodic-software/provisioning#552 is installed, disable it before the stop and enable it after
   the start. This repository does not name that task.
3. **WSL side.** Replace the Linux binary and the wrapper while the follower runs. Rename a new
   file over each one, as chezmoi does. Do not copy over the running binary: Linux refuses that
   with "Text file busy". Do not stop the follower.

What happens after each order:

- **Windows first.** The new leader finds the follower on the old version. It stops the follower
  with that side's own `/api/shutdown` and starts it again. The Linux binary installed at that
  moment is still the old one, so the old build starts again, but now it knows the new leader's
  version. When the WSL install lands, the follower sees the new build is the leader's version.
  Within about 15 seconds, once no import is in flight, it stops with exit code 75, and the
  leader starts the new build. Hand-offs are refused between the two installs, because the
  versions differ.
- **WSL first.** The follower keeps running the old build, which still matches the old leader,
  so hand-offs keep working. It knows the leader runs the old version, so it does not stop for
  the new build. When the new leader starts, it finds the follower on the old version, stops it,
  and starts the new build.

Either way, a side is stopped only through its own `/api/shutdown`, which refuses while a switch
or import is in flight. A refused stop leaves that side running on its old version, and the
page shows it as incompatible. Start WSL side, or `POST /api/sides/wsl/start` on the leader,
tries again: it stops and starts a side on another version, and starts one that is not running.
The leader runs one of these passes at a time per side, so its startup, the button, and a
follower's exit cannot overlap. Only exit code 75 makes the leader start the side again. A
crash, or a stop you sent, is left for you.

Upgrading onto this release from an earlier one, Windows first: the old follower does not stop
for a new build by itself, so click Start WSL side once after both installs. Installing WSL
first needs no click.

When both sides are done, `claude-code-account-rotation --version` prints the same version on
each, and the page shows the WSL side online.

## Uninstall

There is no `--uninstall` switch, no uninstall script, and no uninstall route. Remove the tool by
hand, in this order.

1. If the logon task from melodic-software/provisioning#552 is installed, disable it. This
   repository does not name that task.
2. Start the leader and the follower if either is not running. Startup finishes or unwinds an
   open switch or import journal, and it restores a recovery file. Do this even when you already
   stopped the processes: a journal left behind is what the next start uses to finish a move, and
   deleting it first leaves a parked pair with nothing to put it back.
3. While they are running, the page must show no blocking banner, the same rule as an upgrade.
   No switch or import may be in flight.
4. On each side, look in `recovery/`. A `*.credentials.json` outside `stale/` can be the only
   working lineage: the token endpoint has already invalidated the refresh token in the profile
   folder. Restart that side, or refresh the card, so the file is restored. Do not continue while
   such a file remains, even when no card says `credentials stranded in recovery`.
5. Stop the leader and the follower with `POST /api/shutdown`. Send
   `Authorization: Bearer` with the token on the second line of that side's `instance.url`,
   and the same `X-Claude-Code-Account-Rotation: 1` header as the other dashboard writes.
   A 409 means a switch or import is still in flight, so the process was left running.
6. Read `appDataDirectory`, `profilesRoot`, `liveConfigDirectory`, and `stateFilePath` from
   `config.json`. Read the leader's file, and the follower's when `peers[].launch.configPath`
   names one, and take each side's `appDataDirectory` from its own file.
7. Delete each side's app data directory. Also delete `config.json` when `--config` or
   `peers[].launch.configPath` put that file outside the app data directory.
8. Delete the executables you installed.

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
