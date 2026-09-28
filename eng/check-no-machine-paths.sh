#!/usr/bin/env bash
# Fails when a machine-specific path leaks into the surfaces the public
# repository ships: src/, tests/, README.md, config.template.json, and the
# contract slice under docs/. The tool derives every path from the user
# profile at runtime, so a literal drive root or home directory in any of
# these files is a portability defect (Brief acceptance criterion 8).
#
# Patterns:
#   - a drive-root path, X:\ or X:/, where X is not the tail of a URL scheme;
#   - a per-user home path: \Users\<name>, /Users/<name>, /home/<name>;
#   - any extra literal listed in CHECK_NO_MACHINE_PATHS_NAMES (comma-separated),
#     which an operator sets locally to their own user name; each name is
#     matched as a fixed string, so regex metacharacters in it stay literal.
set -euo pipefail

repository_root="$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse --show-toplevel)"
cd "$repository_root"

surfaces=(src tests README.md docs)
[[ -f config.template.json ]] && surfaces+=(config.template.json)

pattern='(^|[^A-Za-z])[A-Za-z]:[\\/]|[\\/]Users[\\/][A-Za-z0-9._-]+|/home/[A-Za-z0-9._-]+'
# Operator names are searched as fixed strings (grep -F), never spliced into the
# ERE above: a name such as "a.b" or "[" would otherwise widen the pattern or
# make grep fail, and a failed grep inside the if would read as "no match".
name_args=()
if [[ -n "${CHECK_NO_MACHINE_PATHS_NAMES:-}" ]]; then
  IFS=',' read -r -a extra_names <<< "$CHECK_NO_MACHINE_PATHS_NAMES"
  for name in "${extra_names[@]}"; do
    [[ -n "$name" ]] && name_args+=(-e "$name")
  done
fi

findings=0
# -I skips binary files: compressed bytes in a font can spell "c:/" by chance,
# and no binary here carries a path the tool reads.
while IFS= read -r -d '' file; do
  if grep -InE "$pattern" -- "$file"; then
    findings=1
  fi
  if (( ${#name_args[@]} > 0 )) && grep -InF "${name_args[@]}" -- "$file"; then
    findings=1
  fi
done < <(git ls-files -z -- "${surfaces[@]}")

if [[ "$findings" -ne 0 ]]; then
  echo "check-no-machine-paths: machine-specific path or name found in the files above." >&2
  exit 1
fi

echo "check-no-machine-paths: clean."
