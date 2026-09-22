#!/usr/bin/env bash
# The temp-root WSL acceptance (issue #69). Drives a real leader on Windows and
# a real follower inside the distro over the real 9P mount.
#
# It runs on the WINDOWS side, in the shell the repository's other scripts run
# in, and reaches into the distro with `wsl.exe` for everything that belongs
# there. That is not a preference: the leader binds loopback only and refuses
# any Origin but its own, and under WSL's default NAT networking a process
# inside the distro cannot reach the Windows loopback at all. The traffic that
# has to cross does cross the way the product's own does — Windows to the
# follower's forwarded port — and the checks that need all four roots at once
# run inside the distro, where they are all readable.
#
# Everything it touches is a temp root. The store, the Windows live directory
# and the Windows app data sit under the Windows temp directory; the follower's
# live directory and app data sit under /tmp inside the distro. No real profile
# store, no real live directory, no login, and a `claudeExecutable` that does
# not exist, so no CLI is ever started. The operator's own instance keeps
# serving on its own port throughout: this run binds ports of its own, and
# every process it kills is one whose PID it started.
#
# What it proves, in order:
#   1. A first WSL switch with an EMPTY follower live directory: Exported
#      {none}, no gate, no park, the switch completes. The phase-5 state.
#   2. One iteration that corrupts the export between the follower's Exported
#      answer and the leader's native read: the gate refuses, the switch is
#      refused, and both sides' live pairs are unchanged.
#   3. kill -9 of the follower at each of the six ImportStep values and of the
#      leader at each of the four WslSwitchStep values, before and after each
#      journal write, every one followed by a restart whose outcome is checked.
#   4. N WSL switches and N Windows switches.
#   5. The side goes offline when the follower stops and online again when the
#      leader starts it.
#   6. A mutating route with the custom header and no Origin is neither 400
#      nor 403.
#   7. No `auth login` anywhere in the follower's logs.
#   8. check-single-holder.sh over the roots prints duplicates=0.
#
# Usage:
#   wsl-switch-acceptance.sh --leader <dir> --follower <linux dir> [options]
#     --leader    the published win-x64 directory (a path this shell can see)
#     --follower  the published directory INSIDE the distro, a Linux path
#     --distro    the distribution (default: WSL's own default)
#     --switches  switches per side (default 20)
#     --keep      leave the temp roots and logs behind for a post-mortem
#
# Every step decides for itself what a failure means — an unreachable side is a
# finding to record and go on from, not a reason to stop — so the helpers are
# called in conditions throughout and the set -e suppression that causes is
# intended everywhere it happens.
# shellcheck disable=SC2310
set -euo pipefail

leader_publish=""
follower_publish=""
distro=""
switches=20
keep_roots="no"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --leader) leader_publish="${2:?--leader needs a directory}"; shift 2 ;;
    --follower) follower_publish="${2:?--follower needs a directory}"; shift 2 ;;
    --distro) distro="${2:?--distro needs a name}"; shift 2 ;;
    --switches) switches="${2:?--switches needs a number}"; shift 2 ;;
    --keep) keep_roots="yes"; shift ;;
    *) echo "usage: $0 --leader <dir> --follower <linux dir> [--distro D] [--switches N] [--keep]" >&2; exit 2 ;;
  esac
done
[[ -n "$leader_publish" && -n "$follower_publish" ]] || { echo "usage: $0 --leader <dir> --follower <linux dir> [--distro D] [--switches N] [--keep]" >&2; exit 2; }

leader_exe="$leader_publish/claude-code-account-rotation.exe"
[[ -f "$leader_exe" ]] || { echo "no leader binary at $leader_exe" >&2; exit 2; }

# The machine's own names are derived, never typed, which is what keeps
# eng/check-no-machine-paths.sh true of this file.
[[ -n "$distro" ]] || distro="$(wsl.exe --list --quiet 2>/dev/null | tr -d '\000\r' | head -1)"
[[ -n "$distro" ]] || { echo "no WSL distribution found" >&2; exit 2; }
wsl_user="$(wsl.exe -d "$distro" -e bash -c 'whoami' 2>/dev/null | tr -d '\000\r')"
[[ -n "$wsl_user" ]] || { echo "could not read the distro's user" >&2; exit 2; }

# A login shell, because check-single-holder.sh needs the distro's own jq and a
# non-login `bash -c` gets none of the operator's PATH.
wsl_run() { wsl.exe -d "$distro" -u "$wsl_user" -e bash -lc "$1" 2>&1 | tr -d '\000\r'; }

# Three spellings of the one Windows temp root: as .NET on Windows will open it
# (forward slashes, so the JSON below needs no escaping), as this shell sees it,
# and as the distro sees it through the mount.
windows_profile="${USERPROFILE:-}"
[[ -n "$windows_profile" ]] || { echo "USERPROFILE is not set" >&2; exit 2; }
windows_profile="${windows_profile//\\//}"
run_id="ccar-accept-$$"
win_root_native="$windows_profile/AppData/Local/Temp/$run_id"
drive="$(printf '%s' "${win_root_native:0:1}" | tr '[:upper:]' '[:lower:]')"
win_root_here="/$drive${win_root_native:2}"
win_root_wsl="/mnt/$drive${win_root_native:2}"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
here_drive="$(printf '%s' "${here:1:1}" | tr '[:upper:]' '[:lower:]')"
holder_script_wsl="/mnt/$here_drive${here:2}/check-single-holder.sh"

store_here="$win_root_here/store"
store_native="$win_root_native/store"
store_wsl="$win_root_wsl/store"
win_live_here="$win_root_here/live"
win_appdata_here="$win_root_here/appdata"
mailbox_here="$store_here/.transit/wsl"
mailbox_wsl="$store_wsl/.transit/wsl"
wsl_root="/tmp/$run_id"
wsl_live="$wsl_root/live"
wsl_appdata="$wsl_root/appdata"
follower_exe="$follower_publish/claude-code-account-rotation"
follower_wrapper="$follower_publish/claude-code-account-rotation-follower-log"
repo="$(cd "$here/../.." && pwd)"
repo_drive="$(printf '%s' "${repo:1:1}" | tr '[:upper:]' '[:lower:]')"
wrapper_src_wsl="/mnt/$repo_drive${repo:2}/src/ClaudeCodeAccountRotation.App/follower-log"
# Publish places the wrapper beside the binary. A directory copied into the
# distro before that, or a copy that dropped the executable bit, is repaired
# from this tree: the page's Start execs the wrapper, not the binary.
wsl_run "test -f '$follower_wrapper' || cp -f '$wrapper_src_wsl' '$follower_wrapper'; chmod +x '$follower_wrapper' '$follower_exe'; test -x '$follower_wrapper'" >/dev/null \
  || { echo "the follower wrapper is not executable at $follower_wrapper" >&2; exit 2; }

leader_port=48411
follower_port=48412
leader_url="http://127.0.0.1:$leader_port"
follower_url="http://127.0.0.1:$follower_port"
header="X-Claude-Code-Account-Rotation: 1"

log_dir="$win_root_here/logs"
leader_pid=""
follower_wrapper_pid=""
failures=0

say() { printf '%s %s\n' "$(date -u +%H:%M:%S)" "$*"; }
fail() { failures=$((failures + 1)); printf '%s FAIL %s\n' "$(date -u +%H:%M:%S)" "$*"; }

# --- processes ---------------------------------------------------------------
#
# Nothing here is ever killed by name. The operator's own instance runs under
# the same executable name on this machine, and a pkill would take it down.

stop_leader() {
  [[ -n "$leader_pid" ]] || return 0
  kill -9 "$leader_pid" 2>/dev/null || true
  wait "$leader_pid" 2>/dev/null || true
  leader_pid=""
}

stop_follower() {
  # The Linux process first, by the PID it recorded for itself, then the
  # wsl.exe wrapper that was holding the session open for it.
  wsl_run "[ -f '$wsl_root/follower.pid' ] && kill -9 \$(cat '$wsl_root/follower.pid') 2>/dev/null; rm -f '$wsl_root/follower.pid'; true" >/dev/null 2>&1 || true
  if [[ -n "$follower_wrapper_pid" ]]; then
    kill -9 "$follower_wrapper_pid" 2>/dev/null || true
    wait "$follower_wrapper_pid" 2>/dev/null || true
    follower_wrapper_pid=""
  fi
}

# Whatever holds this run's follower port inside the distro, by the PID that
# holds it and never by name. The liveness step has the leader start a follower
# of its own, whose PID this script never learns; left behind, it would squat
# the port and the next run would talk to it instead of its own follower, which
# is a confusing failure a long way from its cause.
# Invoked by cleanup above, which shellcheck does not follow through the trap.
# shellcheck disable=SC2329
free_follower_port() {
  wsl_run "for pid in \$(ss -ltnp 2>/dev/null | sed -n 's/.*:$follower_port .*pid=\([0-9]*\).*/\1/p' | sort -u); do kill -9 \"\$pid\" 2>/dev/null; done; true" >/dev/null 2>&1 || true
}

# Invoked by the EXIT trap below, which shellcheck does not follow.
# shellcheck disable=SC2329
cleanup() {
  stop_leader || true
  stop_follower || true
  free_follower_port || true
  if [[ "$keep_roots" == "yes" ]]; then
    printf 'kept: %s (and %s inside the distro)\n' "$win_root_here" "$wsl_root"
    return
  fi

  rm -rf "$win_root_here" 2>/dev/null || true
  wsl_run "rm -rf '$wsl_root'" >/dev/null 2>&1 || true
}
trap cleanup EXIT

# Before anything binds. A port already in use means some earlier run left a
# process behind, and a run that talked to it instead of its own follower would
# report nonsense with the cause hours upstream.
for port_in_use in "$leader_port" "$follower_port"; do
  if curl -fsS --max-time 2 "http://127.0.0.1:$port_in_use/healthz" >/dev/null 2>&1 \
    || curl -fsS --max-time 2 "http://127.0.0.1:$port_in_use/api/dashboard" >/dev/null 2>&1; then
    echo "something is already answering on port $port_in_use; stop it before running this" >&2
    exit 2
  fi
done

mkdir -p "$store_here" "$win_live_here" "$win_appdata_here" "$mailbox_here" "$log_dir"
wsl_run "mkdir -p '$wsl_live' '$wsl_appdata'" >/dev/null

# --- seeding -----------------------------------------------------------------

# A credential pair in the shape the product reads, with a refresh token that
# is a label and never a secret. Only SHA-256 fingerprints of these are ever
# compared or printed, which is what check-single-holder.sh does too.
write_pair() {
  local path="$1" token="$2" now
  now="$(date +%s)"
  mkdir -p "$(dirname "$path")"
  printf '{"claudeAiOauth":{"accessToken":"access-%s","refreshToken":"%s","expiresAt":%s,"refreshTokenExpiresAt":%s,"scopes":["user:inference"]}}\n' \
    "$token" "$token" "$(( (now + 8 * 3600) * 1000 ))" "$(( (now + 28 * 24 * 3600) * 1000 ))" > "$path"
}

# profile.json holds the oauthAccount block itself, not a document wrapping
# it: the product writes account.Raw straight out, and a wrapped one reads as a
# folder with no account at all.
write_profile() {
  local folder="$1" email="$2"
  mkdir -p "$folder"
  printf '{"accountUuid":"uuid-%s","emailAddress":"%s","organizationRateLimitTier":"default_claude_max_20x"}\n' \
    "$email" "$email" > "$folder/profile.json"
}

accounts=()
for index in 1 2 3 4 5 6; do
  email="acct$index@example.com"
  accounts+=("$email")
  write_profile "$store_here/$email" "$email"
  write_pair "$store_here/$email/.credentials.json" "refresh-$email-$run_id"
done
printf '{"numStartups":7}\n' > "$win_root_here/.claude.json"

# --- configuration -----------------------------------------------------------
#
# Both files are written on this side. The follower's lives under the mount and
# is handed to it as a /mnt path: only its live directory and app data are
# required to be off the mount, and its own configuration is neither.

cat > "$win_root_here/leader-config.json" <<JSON
{
  "liveConfigDirectory": "$win_root_native/live",
  "stateFilePath": "$win_root_native/.claude.json",
  "profilesRoot": "$store_native",
  "appDataDirectory": "$win_root_native/appdata",
  "listenPort": $leader_port,
  "claudeExecutable": "$win_root_native/no-claude-here.cmd",
  "store": { "shared": true },
  "role": "leader",
  "peers": [
    {
      "side": "wsl",
      "baseAddress": "$follower_url",
      "storePathFromPeer": "$store_wsl",
      "launch": {
        "distribution": "$distro",
        "user": "$wsl_user",
        "executablePath": "$follower_exe",
        "port": $follower_port,
        "configPath": "$win_root_wsl/follower-config.json"
      }
    }
  ]
}
JSON

cat > "$win_root_here/follower-config.json" <<JSON
{
  "role": "follower",
  "liveConfigDirectory": "$wsl_live",
  "stateFilePath": "$wsl_root/.claude.json",
  "appDataDirectory": "$wsl_appdata",
  "profilesRoot": "$wsl_root/profiles",
  "listenPort": $follower_port,
  "mailbox": "$mailbox_wsl"
}
JSON

# --- lifecycle ---------------------------------------------------------------

wait_for() {
  local url="$1" attempts="${2:-120}" index
  for index in $(seq 1 "$attempts"); do
    if curl -fsS --max-time 2 "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 0.5
  done
  return 1
}

start_follower() {
  local fail_after="${1:-}" log
  log="$log_dir/follower-$(date -u +%H%M%S)-$RANDOM.log"
  # The wsl.exe child is what holds the distro session open: a process started
  # and abandoned by a one-shot `wsl.exe -e` is reaped with that session.
  # Into its own root first. `wsl.exe` inherits this shell's working directory,
  # which is a Windows path under the mount, and a host started there has its
  # content root there.
  wsl.exe -d "$distro" -u "$wsl_user" -e bash -c \
    "cd '$wsl_root' && echo \$\$ > follower.pid; exec env CCAR_FAIL_AFTER_STEP='$fail_after' '$follower_exe' --config '$win_root_wsl/follower-config.json'" \
    > "$log" 2>&1 &
  follower_wrapper_pid=$!
  cp -f "$log" "$log_dir/follower-latest.log" 2>/dev/null || true
  wait_for "$follower_url/api/dashboard" || { fail "the follower did not come up; see $log"; return 1; }
}

start_leader() {
  local fail_after="${1:-}" corrupt="${2:-}" log
  log="$log_dir/leader-$(date -u +%H%M%S)-$RANDOM.log"
  if [[ -n "$corrupt" ]]; then
    CCAR_FAIL_AFTER_STEP="$fail_after" CCAR_CORRUPT_EXPORT_BEFORE_GATE=1 \
      "$leader_exe" --config "$win_root_here/leader-config.json" > "$log" 2>&1 &
  else
    CCAR_FAIL_AFTER_STEP="$fail_after" \
      "$leader_exe" --config "$win_root_here/leader-config.json" > "$log" 2>&1 &
  fi
  leader_pid=$!
  leader_log="$log"
  wait_for "$leader_url/healthz" || { fail "the leader did not come up; see $log"; return 1; }
}

# --- the routes --------------------------------------------------------------

# The last three characters, because curl writes one code per connection it
# made and a leader killed mid-request leaves it having made two.
wsl_switch() {
  local code
  code="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 180 -X POST -H "$header" \
    "$leader_url/api/sides/wsl/accounts/$1/switch" 2>/dev/null || echo "000")"
  printf '%s' "${code: -3}"
}

windows_switch() {
  curl -sS -o /dev/null -w '%{http_code}' --max-time 120 -X POST -H "$header" \
    "$leader_url/api/accounts/$1/switch" 2>/dev/null || echo "000"
}

wsl_release() {
  local code query=""
  [[ "${1:-}" == "quarantine" ]] && query="?quarantineForeignFamily=true"
  code="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 180 -X POST -H "$header" \
    "$leader_url/api/sides/wsl/release$query" 2>/dev/null || echo "000")"
  printf '%s' "${code: -3}"
}

side_online() {
  curl -fsS --max-time 5 "$leader_url/api/sides/wsl" 2>/dev/null \
    | sed -n 's/.*"online":\([a-z]*\).*/\1/p' | head -1
}

# One poll of the leader's dashboard, which is where its own crash table runs.
poll_leader() { curl -fsS --max-time 60 "$leader_url/api/dashboard" >/dev/null 2>&1 || true; }

follower_live_account() {
  local body
  body="$(curl -fsS --max-time 5 "$follower_url/api/dashboard" 2>/dev/null || true)"
  [[ -n "$body" ]] || { echo "unreachable"; return; }
  printf '%s' "$body" | sed -n 's/.*"liveAccount":"\([^"]*\)".*/\1/p' | head -1 | grep . || echo "none"
}

# --- assertions --------------------------------------------------------------

fingerprint_of() {
  [[ -f "$1" ]] || { echo "absent"; return; }
  sed -n 's/.*"refreshToken":"\([^"]*\)".*/\1/p' "$1" | head -1 | tr -d '\n' | sha256sum | cut -c1-12
}

mailbox_file_count() { find "$store_here/.transit" -type f 2>/dev/null | wc -l | tr -d ' '; }

journals_are_clear() {
  [[ -f "$win_appdata_here/state/wsl-switch-journal.json" ]] && return 1
  local open
  open="$(wsl_run "[ -f '$wsl_appdata/state/import-journal.json' ] && echo open || echo clear")"
  [[ "$open" == "clear" ]]
}

# The leader's crash table runs on a dashboard poll, so settling is polling
# until the journal and the mailbox are both clear rather than sleeping.
settle() {
  local attempts="${1:-60}" index
  for index in $(seq 1 "$attempts"); do
    poll_leader
    if journals_are_clear && [[ "$(mailbox_file_count)" == "0" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

expect() {
  local what="$1" want="$2" got="$3"
  if [[ "$want" == "$got" ]]; then
    say "ok: $what"
  else
    fail "$what: wanted '$want', got '$got'"
  fi
}

# The next account this side could take: parked in the store and not the one
# the side already holds.
next_parked() {
  local held="$1" candidate
  for candidate in "${accounts[@]}"; do
    if [[ "$candidate" != "$held" && -f "$store_here/$candidate/.credentials.json" ]]; then
      printf '%s' "$candidate"
      return 0
    fi
  done
  return 1
}

# Every crash row ends in one of exactly two places, and both are states the
# next start finished or unwound: the import completed and the incoming
# account is live on that side, or it unwound and the account is parked again.
# What is never allowed is a lineage in two files, which the final sweep checks.
check_row() {
  local label="$1" victim="$2" incoming="$3" after
  after="$(follower_live_account)"
  if [[ "$after" == "$incoming" ]]; then
    say "  ok: $label resolved forward; $incoming is live on the follower"
  elif [[ "$after" == "$victim" ]]; then
    expect "  $label unwound and $incoming is parked again" "present" \
      "$([[ -f "$store_here/$incoming/.credentials.json" ]] && echo present || echo absent)"
  else
    fail "$label left the follower holding '$after', which is neither side of the hand-off"
  fi
}

present_or_absent() { [[ -f "$1" ]] && echo present || echo absent; }

# A release ends in one of exactly two places, and both are states the next
# start finished or unwound: the pair came back, so the follower holds nothing
# and the slot holds the pair, or it did not, so the follower still holds it and
# the slot still carries the record that says so. What is never allowed is the
# account in neither place, or in both, which the final sweep also checks.
check_release_row() {
  local label="$1" held="$2" after
  after="$(follower_live_account)"
  if [[ "$after" == "none" ]]; then
    expect "  $label resolved forward; $held is parked in its slot" "present" \
      "$(present_or_absent "$store_here/$held/.credentials.json")"
    expect "  $label left no holder record over it" "absent" "$(present_or_absent "$store_here/$held/holder.json")"
  elif [[ "$after" == "$held" ]]; then
    expect "  $label unwound; $held is still with that side" "absent" \
      "$(present_or_absent "$store_here/$held/.credentials.json")"
    expect "  $label kept the holder record that says where it is" "present" \
      "$(present_or_absent "$store_here/$held/holder.json")"
  else
    fail "$label left the follower holding '$after', which is neither side of the release"
  fi
}

# --- the run -----------------------------------------------------------------

# Set by start_leader; the gate assertion greps the current leader's log.
leader_log=""
say "distro=$distro store=$store_here follower-live=$wsl_live"
start_follower || exit 1
start_leader || exit 1

# 1. The empty-follower iteration: Exported {none}, no gate, no park.
say "iteration 1: an empty follower live directory"
first="${accounts[0]}"
expect "the first WSL switch returns 200" "200" "$(wsl_switch "$first")"
expect "the follower holds the first account" "$first" "$(follower_live_account)"
expect "nothing was left in a mailbox" "0" "$(mailbox_file_count)"
journals_are_clear || fail "a journal was left open after the first switch"

# 2. The export gate, run early so a failure here is not buried at the end of
#    an hour. The leader is restarted with the export-gate injection, which
#    truncates the export between the follower's answer and the native read.
say "the export-gate corruption iteration"
before_wsl="$(follower_live_account)"
before_win_live="$(fingerprint_of "$win_live_here/.credentials.json")"
target="$(next_parked "$before_wsl")"
stop_leader
start_leader "" "1" || exit 1
expect "a corrupted export is refused with 409" "409" "$(wsl_switch "$target")"
expect "the follower's live pair is unchanged" "$before_wsl" "$(follower_live_account)"
expect "the Windows live pair is unchanged" "$before_win_live" "$(fingerprint_of "$win_live_here/.credentials.json")"
expect "the refused target is back in its slot" "present" "$([[ -f "$store_here/$target/.credentials.json" ]] && echo present || echo absent)"
expect "the refused target has no holder record" "absent" "$([[ -f "$store_here/$target/holder.json" ]] && echo present || echo absent)"
expect "the mailbox is empty after the gate refused" "0" "$(mailbox_file_count)"
if grep -q "export gate refused" "$leader_log" 2>/dev/null; then
  say "ok: the leader logged the gate refusal"
else
  fail "the leader log carries no gate refusal"
fi
stop_leader
start_leader || exit 1

# 3a. The follower's six ImportStep crash rows, before and after each journal
#     write. Every kill is the follower killing itself with Process.Kill at the
#     named step: a SIGKILL with no unwinding, no finally, and no flush.
say "the follower crash rows"
for step in Planned Staged Exported Swapped Released Patched; do
  for timing in "$step" "$step:before-journal"; do
    victim="$(follower_live_account)"
    if ! incoming="$(next_parked "$victim")"; then
      fail "no parked account left for $timing"
      break
    fi

    stop_follower
    start_follower "$timing" || { fail "the follower would not start for $timing"; continue; }
    say "  $timing: the switch answered $(wsl_switch "$incoming") with the follower killed mid-import"
    stop_follower
    start_follower || { fail "the follower would not restart after $timing"; continue; }
    if settle 90; then
      check_row "$timing" "$victim" "$incoming"
    else
      fail "$timing did not settle: a journal or the mailbox still holds something"
    fi
  done
done

# 3b. The leader's four WslSwitchStep crash rows.
say "the leader crash rows"
for step in Claimed ExportVerified Imported Parked; do
  for timing in "$step" "$step:before-journal"; do
    victim="$(follower_live_account)"
    if ! incoming="$(next_parked "$victim")"; then
      fail "no parked account left for $timing"
      break
    fi

    stop_leader
    start_leader "$timing" || { fail "the leader would not start for $timing"; continue; }
    say "  $timing: the switch answered $(wsl_switch "$incoming") with the leader killed mid-hand-off"
    stop_leader
    start_leader || { fail "the leader would not restart after $timing"; continue; }
    if settle 90; then
      check_row "$timing" "$victim" "$incoming"
    else
      fail "$timing did not settle: a journal or the mailbox still holds something"
    fi
  done
done

# 3c. The park-back, which is the one hand-off a switch cannot express: the
#     side hands its account back and takes nothing in its place, which is the
#     only way out for a fleet where WSL holds exactly one account.
say "the release rows"
held="$(follower_live_account)"
if [[ "$held" == "none" || "$held" == "unreachable" ]]; then
  fail "the follower holds nothing, so the release rows have nothing to hand back"
else
  expect "a release returns 200" "200" "$(wsl_release)"
  expect "the follower holds nothing afterwards" "none" "$(follower_live_account)"
  expect "the released account is back in its slot" "present" "$(present_or_absent "$store_here/$held/.credentials.json")"
  expect "the released account has no holder record" "absent" "$(present_or_absent "$store_here/$held/holder.json")"
  expect "nothing was left in a mailbox" "0" "$(mailbox_file_count)"
  journals_are_clear || fail "a journal was left open after the release"
  # The refusal the page's own control is hidden behind, when it is bypassed.
  expect "a second release with nothing to hand back is refused" "409" "$(wsl_release)"

  # The export gate in the release direction. The leader is restarted with the
  # injection that truncates the export between the follower's answer and the
  # native read, which is a window no external script can hit.
  say "the release export-gate corruption iteration"
  incoming="$(next_parked none)"
  expect "a switch back to $incoming returns 200" "200" "$(wsl_switch "$incoming")"
  settle 90 || fail "the switch before the release gate iteration did not settle"
  before_win_live="$(fingerprint_of "$win_live_here/.credentials.json")"
  stop_leader
  start_leader "" "1" || exit 1
  expect "a corrupted release export is refused with 409" "409" "$(wsl_release)"
  expect "the follower still holds its account" "$incoming" "$(follower_live_account)"
  expect "the Windows live pair is unchanged" "$before_win_live" "$(fingerprint_of "$win_live_here/.credentials.json")"
  expect "the refused account has no pair in its slot" "absent" "$(present_or_absent "$store_here/$incoming/.credentials.json")"
  # The asymmetry: a refused release keeps the record, because that side still
  # holds the pair and nothing else in the store says where it is.
  expect "the refused release kept the holder record" "present" "$(present_or_absent "$store_here/$incoming/holder.json")"
  expect "the mailbox is empty after the gate refused" "0" "$(mailbox_file_count)"
  if grep -q "export gate refused" "$leader_log" 2>/dev/null; then
    say "ok: the leader logged the release gate refusal"
  else
    fail "the leader log carries no gate refusal for the release"
  fi
  stop_leader
  start_leader || exit 1

  # The crash rows. The follower's torn F5 is the one that matters: the live
  # pair is gone and the journal still reads Exported, and an unwind there
  # would delete the export of a lineage that is live nowhere else.
  say "the release crash rows"
  for timing in Exported:before-journal Swapped:before-journal Patched; do
    held="$(follower_live_account)"
    if [[ "$held" == "none" ]]; then
      if ! held="$(next_parked none)"; then
        fail "no parked account left for the release row $timing"
        break
      fi
      wsl_switch "$held" >/dev/null
      settle 90 || fail "the switch before the release row $timing did not settle"
    fi

    stop_follower
    start_follower "$timing" || { fail "the follower would not start for the release row $timing"; continue; }
    say "  $timing: the release answered $(wsl_release) with the follower killed mid-release"
    stop_follower
    start_follower || { fail "the follower would not restart after the release row $timing"; continue; }
    if settle 90; then
      check_release_row "$timing" "$held"
    else
      fail "the release row $timing did not settle: a journal or the mailbox still holds something"
    fi
  done

  # And the leader's own, which is the row where the commit landed and its
  # journal write did not: it must resolve forward and never take a claim back.
  say "the leader's release crash row"
  held="$(follower_live_account)"
  if [[ "$held" == "none" ]]; then
    held="$(next_parked none)" && wsl_switch "$held" >/dev/null && { settle 90 || fail "the switch before the leader release row did not settle"; }
  fi
  stop_leader
  start_leader "Imported:before-journal" || exit 1
  say "  Imported:before-journal: the release answered $(wsl_release) with the leader killed mid-hand-off"
  stop_leader
  start_leader || exit 1
  if settle 90; then
    check_release_row "Imported:before-journal" "$held"
  else
    fail "the leader's release row did not settle"
  fi
fi

# 4. The volume of switches the criterion asks for, on both sides.
say "$switches WSL switches"
for index in $(seq 1 "$switches"); do
  held="$(follower_live_account)"
  if ! incoming="$(next_parked "$held")"; then
    fail "no parked account left at WSL switch $index"
    break
  fi

  code="$(wsl_switch "$incoming")"
  [[ "$code" == "200" ]] || fail "WSL switch $index to $incoming answered $code"
  [[ "$(follower_live_account)" == "$incoming" ]] || fail "WSL switch $index to $incoming did not land"
done

say "$switches Windows switches"
for index in $(seq 1 "$switches"); do
  held="$(sed -n 's/.*"emailAddress":"\([^"]*\)".*/\1/p' "$win_root_here/.claude.json" 2>/dev/null | head -1)"
  if ! incoming="$(next_parked "${held:-none}")"; then
    fail "no parked account left at Windows switch $index"
    break
  fi

  code="$(windows_switch "$incoming")"
  [[ "$code" == "200" ]] || fail "Windows switch $index to $incoming answered $code"
done

# 5. Liveness: the side goes offline when the follower stops, and the leader
#    starts it again through the page's own route.
say "side liveness"
stop_follower
offline="no"
for index in $(seq 1 30); do
  if [[ "$(side_online)" == "false" ]]; then
    offline="yes"
    break
  fi
  sleep 0.5
done
expect "the side goes offline within 15 s of the follower stopping" "yes" "$offline"

curl -sS -o /dev/null --max-time 30 -X POST -H "$header" "$leader_url/api/sides/wsl/start" 2>/dev/null || true
online="no"
for index in $(seq 1 120); do
  if [[ "$(side_online)" == "true" ]]; then
    online="yes"
    break
  fi
  sleep 0.5
done
expect "the leader restarting the side reports online within 60 s" "yes" "$online"

# 6. A mutating route with the custom header and no Origin is neither 400 nor
#    403: this is exactly the shape the leader sends the follower.
status="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 10 -X POST -H "$header" \
  -H 'Content-Type: application/json' -d '{"email":"nobody@example.com"}' \
  "$follower_url/api/import/abort" 2>/dev/null || echo "000")"
if [[ "$status" == "400" || "$status" == "403" ]]; then
  fail "a mutating route with the header and no Origin answered $status"
else
  say "ok: a mutating route with the header and no Origin answered $status"
fi

# 7. No login was ever performed inside the distro.
expect "the follower logs carry no auth login" "0" \
  "$(cat "$log_dir"/follower-*.log 2>/dev/null | grep -c "auth login" || true)"

# 8. The invariant, over every root this run touched. Run inside the distro,
#    which is the one place all of them are readable at once.
say "the single-holder sweep"
sweep="$(wsl_run "bash '$holder_script_wsl' --root '$store_wsl' --root '$win_root_wsl/live' --root '$win_root_wsl/appdata' --root '$wsl_live' --root '$wsl_appdata'" || true)"
printf '%s\n' "$sweep"
if printf '%s' "$sweep" | grep -q "duplicates=0"; then
  say "ok: duplicates=0 over the temp roots"
else
  fail "the sweep did not print duplicates=0"
fi

if [[ "$failures" -eq 0 ]]; then
  say "acceptance passed"
  exit 0
fi

say "acceptance failed with $failures finding(s)"
exit 1
