#!/usr/bin/env bash
# Publishes the linux-x64 build and proves the follower it produces serves
# GET /api/dashboard (anchor issue #65, R4). Everything it touches is a temp
# directory: no store, no live directory, no login, and a fake `claude` on the
# path the follower is configured with, so the runner's own CLI is never found.
set -euo pipefail

repository_root="$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse --show-toplevel)"
cd "$repository_root"

work="$(mktemp -d)"
follower_pid=""
cleanup() {
  [[ -n "$follower_pid" ]] && kill "$follower_pid" 2>/dev/null
  rm -rf "$work"
}
trap cleanup EXIT

dotnet publish src/ClaudeCodeAccountRotation.App \
  -c Release -r linux-x64 --self-contained false \
  -o "$work/publish"

mkdir -p "$work/live" "$work/appdata" "$work/store/.transit/wsl"

# A follower's container registers no CLI adapter, no login runner and no
# refresh engine, so nothing here can resolve the runner's own `claude`.
cat > "$work/appdata/config.json" <<JSON
{
  "role": "follower",
  "liveConfigDirectory": "$work/live",
  "stateFilePath": "$work/.claude.json",
  "appDataDirectory": "$work/appdata",
  "profilesRoot": "$work/profiles",
  "mailbox": "$work/store/.transit/wsl"
}
JSON

port=48299
"$work/publish/claude-code-account-rotation" --config "$work/appdata/config.json" --port "$port" \
  > "$work/follower.log" 2>&1 &
follower_pid=$!

for _ in $(seq 1 60); do
  if curl -fsS -o /dev/null "http://127.0.0.1:$port/healthz"; then
    break
  fi
  if ! kill -0 "$follower_pid" 2>/dev/null; then
    echo "smoke-linux-publish: the follower exited before it served:" >&2
    cat "$work/follower.log" >&2
    exit 1
  fi
  sleep 1
done

# Line 2 is the instance token. It is sent on curl's stdin and is not printed,
# including when this curl fails. /healthz above stays without it.
token="$(sed -n '2p' "$work/appdata/instance.url" | tr -d '\r\n')"
status="$(printf 'header = "Authorization: Bearer %s"\n' "$token" | curl --config - -s -o "$work/dashboard.json" -w '%{http_code}' "http://127.0.0.1:$port/api/dashboard")"
if [[ "$status" != "200" ]]; then
  echo "smoke-linux-publish: /api/dashboard answered $status" >&2
  cat "$work/follower.log" >&2
  exit 1
fi

if ! grep -q '"role":"follower"' "$work/dashboard.json"; then
  echo "smoke-linux-publish: the dashboard is not a follower's:" >&2
  cat "$work/dashboard.json" >&2
  exit 1
fi

echo "smoke-linux-publish: the linux-x64 publish answered /api/dashboard with 200."
