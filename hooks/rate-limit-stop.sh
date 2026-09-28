#!/usr/bin/env bash
# Claude Code StopFailure hook, matcher rate_limit: asks the running dashboard to
# read this side's live account once, so its card and "switch now" prompt show
# the limit. Silent, and exits 0 whatever happens: Claude Code ignores a
# StopFailure hook's output and exit code, and a stopped app is not an error.
set -u

if [[ -n "${LOCALAPPDATA:-}" ]]; then
  instance="$LOCALAPPDATA/claude-code-account-rotation/instance.url"
else
  instance="${XDG_DATA_HOME:-$HOME/.local/share}/claude-code-account-rotation/instance.url"
fi

[[ -r "$instance" ]] || exit 0
{ read -r url && read -r token; } < "$instance" || exit 0

# The token goes in on stdin rather than argv, where other processes can read it.
printf 'Authorization: Bearer %s\n' "$token" | curl -fsS --max-time 5 -X POST \
  -H @- \
  -H "X-Claude-Code-Account-Rotation: 1" \
  "$url/api/hooks/rate-limit" > /dev/null 2>&1
exit 0
