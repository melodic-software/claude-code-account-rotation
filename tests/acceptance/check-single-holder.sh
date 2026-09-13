#!/usr/bin/env bash
# Proves the single-holder invariant on a real machine: every refresh token
# across the live credential file, every parked profile, and every dotfile
# temp beside them (a crash between a write and its rename, including a
# state-file write stranded at its own config root) exists in exactly one file.
# Prints `files=N distinct=N duplicates=0 unreadable=0` and exits 0; any
# duplicate lineage is listed and the exit code is 1, and a temp too damaged
# to parse is counted as unreadable rather than silently skipped. Reads the
# files, prints only SHA-256 fingerprints, never a token.
#
# A second `recovery=N` line counts the rotated pairs held under the app data
# directory's `recovery/` and `recovery/stale/`, each of which is a second
# on-disk holder of a lineage for as long as it stands. A strand is a state to
# clear rather than a duplicate to fail on, so those files are named and counted
# and the exit code is left to the duplicate scan.
#
# Usage: check-single-holder.sh <profiles-root> <live-dir> [app-data-dir]
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
  echo "usage: $0 <profiles-root> <live-dir> [app-data-dir]" >&2
  exit 2
fi

product="claude-code-account-rotation"
profiles_root="$1"
live_dir="$2"
# The same per-user location the app composes: the platform's local application
# data directory (which the .NET runtime resolves to %LOCALAPPDATA% on Windows
# and to $XDG_DATA_HOME or ~/.local/share elsewhere) plus the product name. The
# Windows value arrives with backslashes under Git Bash.
if [[ -n "${3:-}" ]]; then
  app_data="$3"
elif [[ -n "${LOCALAPPDATA:-}" ]]; then
  app_data="${LOCALAPPDATA//\\//}/$product"
else
  app_data="${XDG_DATA_HOME:-${HOME:-}/.local/share}/$product"
fi
credential_file=".credentials.json"

fingerprint() {
  # jq -j prints the raw token without a trailing newline; only its hash
  # leaves this function. A parse failure on truncated or invalid JSON
  # propagates as a non-zero exit through pipefail, which the caller treats
  # as unreadable rather than as an empty token.
  jq -j '.claudeAiOauth.refreshToken // empty' -- "$1" 2>/dev/null | sha256sum | cut -c1-64
}

# Any dotfile ending in .tmp beside a directory's credential or state file is
# a write a crash stopped before its rename landed; it can hold a full
# credential pair and is fingerprinted exactly like a settled file so a
# duplicate cannot hide behind a temp name. The name pattern defaults to every
# temp in a Claude-owned directory; the home root also holds unrelated tools'
# temps, so there the pattern is narrowed to ones naming the state file.
# Collects into "files" unless a third argument names another array, which the
# recovery sweep below uses: a recovery temp holds the same rotated pair as the
# settled file beside it once the write lands, so counting it as a second holder
# would report a duplicate for a directory whose whole purpose is to hold one.
collect_temps() {
  local dir="$1" pattern="${2:-.*.tmp}" entry
  local -n target="${3:-files}"
  [[ -d "$dir" ]] || return 0
  while IFS= read -r -d '' entry; do
    target+=("$entry")
  done < <(find "$dir" -maxdepth 1 -name "$pattern" -type f -print0 | sort -z)
}

declare -a files=()
[[ -f "$live_dir/$credential_file" ]] && files+=("$live_dir/$credential_file")
collect_temps "$live_dir"

# The state file is "$HOME/.claude.json" when CLAUDE_CONFIG_DIR is unset and
# "$CLAUDE_CONFIG_DIR/.claude.json" when it is set, so its own crash temp lands
# in whichever of those roots is live. Follow that same rule rather than always
# scanning the home root: under a custom root, a temp left at "$HOME" by an old
# default-root installation is stale residue this run must not fail on. When the
# state root is the live directory it was already scanned above.
#
# A state temp holds no refresh token, so it fingerprints empty and only ever
# adds a "no refresh token" line below, never a duplicate. Only state-file-named
# temps are collected here: a state root that is the home directory is not
# Claude-owned, and an unrelated tool's stray "*.tmp" must not fail this run.
state_root="${CLAUDE_CONFIG_DIR:-${HOME:-}}"
if [[ -n "$state_root" ]] && ! [[ "$state_root" -ef "$live_dir" ]]; then
  collect_temps "$state_root" '.*claude*.tmp'
fi

if [[ -d "$profiles_root" ]]; then
  while IFS= read -r -d '' file; do
    files+=("$file")
  done < <(find "$profiles_root" -mindepth 2 -maxdepth 2 -name "$credential_file" -type f -print0 | sort -z)
  while IFS= read -r -d '' dir; do
    collect_temps "$dir"
  done < <(find "$profiles_root" -mindepth 1 -maxdepth 1 -type d -print0 | sort -z)
fi

declare -A holders=()
duplicates=0
unreadable=0
for file in "${files[@]}"; do
  # A failing fingerprint is the unreadable case counted just below, not a
  # reason to stop, so the set -e suppression this condition causes is intended.
  # shellcheck disable=SC2310
  if ! hash="$(fingerprint "$file")"; then
    echo "unreadable $file" >&2
    unreadable=$((unreadable + 1))
    continue
  fi
  if [[ -z "$hash" || "$hash" == "$(printf '' | sha256sum | cut -c1-64)" ]]; then
    echo "no refresh token in $file" >&2
    continue
  fi
  if [[ -n "${holders[$hash]:-}" ]]; then
    duplicates=$((duplicates + 1))
    echo "duplicate lineage ${hash:0:12}: ${holders[$hash]} and $file" >&2
  else
    holders[$hash]="$file"
  fi
done

echo "files=${#files[@]} distinct=${#holders[@]} duplicates=$duplicates unreadable=$unreadable"

# The recovery envelope nests the pair one level down, so these files are not
# fingerprinted beside the settled ones; each is named by its file so the
# operator can see which account is stranded, and the count is the number the
# runbook expects to be zero.
declare -a recovery_files=()
for directory in "$app_data/recovery" "$app_data/recovery/stale"; do
  [[ -d "$directory" ]] || continue
  while IFS= read -r -d '' entry; do
    recovery_files+=("$entry")
  done < <(find "$directory" -maxdepth 1 -name "*$credential_file" -type f -print0 | sort -z)
  # A crash between an envelope's write and its rename leaves a temp holding a
  # whole rotated pair, exactly as a settled envelope does, so the count the
  # runbook expects to be zero has to include it.
  collect_temps "$directory" '.*.tmp' recovery_files
done
for entry in "${recovery_files[@]}"; do
  echo "recovery file $(basename "$entry") in $(basename "$(dirname "$entry")")" >&2
done
echo "recovery=${#recovery_files[@]}"

[[ "$duplicates" -eq 0 && "$unreadable" -eq 0 ]]
