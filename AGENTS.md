# claude-code-account-rotation

## Code Review Rules

Each line names a rule CI does not enforce; the linked file states it in full.

- Org-wide criteria: [`REVIEW.md`](https://github.com/melodic-software/standards/blob/main/REVIEW.md) in `melodic-software/standards`.
- Posture: no model API calls outside the `claude` binary, credentials moved and never copied,
  honest identification, no automatic rotation, bounded usage reads:
  [README](README.md#posture).
- On-disk compatibility: a release reads the files the previous release wrote, with no migrator
  or schema bump: [README](README.md#upgrading-a-release).
- A dated CHANGELOG heading publishes a release on merge:
  [README](README.md#releasing).
