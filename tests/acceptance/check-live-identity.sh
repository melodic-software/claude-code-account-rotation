#!/usr/bin/env bash
# One honest-User-Agent read of the OAuth profile with the live access token,
# printing the e-mail the live pair actually bills. Run once per acceptance
# pass to confirm the account the page reports is the account the server sees.
# The token is read into a variable and sent as a bearer header only; it is
# never printed and never placed on a command line.
#
# Usage: check-live-identity.sh [<live-dir>]   (default: $CLAUDE_CONFIG_DIR or ~/.claude)
set -euo pipefail

live_dir="${1:-${CLAUDE_CONFIG_DIR:-$HOME/.claude}}"
credential_file="$live_dir/.credentials.json"
profile_url="https://api.anthropic.com/api/oauth/profile"
user_agent="claude-code-account-rotation-acceptance/1 (+https://github.com/melodic-software/claude-code-account-rotation)"

[[ -f "$credential_file" ]] || {
  echo "no live credential file at $credential_file" >&2
  exit 2
}

access_token="$(jq -r '.claudeAiOauth.accessToken // empty' -- "$credential_file")"
[[ -n "$access_token" ]] || {
  echo "no access token in $credential_file" >&2
  exit 2
}

# The bearer header travels on curl's stdin as a config line, never as an argument:
# an argument would sit in the process list and /proc/<pid>/cmdline for the life of
# the request.
response="$(printf 'header = "Authorization: Bearer %s"\n' "$access_token" | curl --silent --show-error --fail \
  --config - \
  --header "anthropic-beta: oauth-2025-04-20" \
  --header "Accept: application/json" \
  --user-agent "$user_agent" \
  "$profile_url")"
unset access_token

email="$(printf '%s' "$response" | jq -r '.account.email // .email // empty')"
if [[ -z "$email" ]]; then
  echo "the profile response carried no e-mail; keys: $(printf '%s' "$response" | jq -c 'keys')" >&2
  exit 1
fi

echo "billed_email=$email"
