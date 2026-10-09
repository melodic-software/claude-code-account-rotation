# claude-code-account-rotation

## Org architecture

Org architecture (cross-repo decisions, glossary, why each trust link exists): private repo `melodic-software/architecture`. Repo map: `gh api orgs/melodic-software/properties/values` (system and role per repo). Read it with `gh api repos/melodic-software/architecture/contents/<path>`; on Claude Code on the web, attach it at session start; in CI, check it out with a read-only App token. If access is denied, stop and tell the user.

## Code Review Rules

Each line names a rule CI does not enforce; the linked file states it in full.

- Org-wide criteria: [`REVIEW.md`](REVIEW.md), synced from `melodic-software/standards`.
- Posture: no model API calls outside the `claude` binary, credentials moved and never copied,
  honest identification, no automatic rotation, bounded usage reads:
  [README](README.md#posture).
- On-disk compatibility: a release reads the files the previous release wrote, with no migrator
  or schema bump: [README](README.md#upgrading-a-release).
- A dated CHANGELOG heading publishes a release on merge; ask the operator before dating one:
  [README](README.md#releasing).
