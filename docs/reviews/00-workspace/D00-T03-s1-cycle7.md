## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:14:36Z
- Candidate: 9c93c33c884fb92d905c41fd52061f9b628ad6fc
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 9c93c33c884fb92d905c41fd52061f9b628ad6fc
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 5
- Digest: d3710f4cdee42965

1. **P2 [plan] Lines 78, 80 and 86:** No recovery path establishes CI proof when `build/ci-gate.json` is missing or corrupt and HEAD is a Markdown stamp whose build was skipped. This occurs after a fresh clone or removal of the build directory. The gate eventually returns `unverified`, while §6's `--use-proven` completion check still cannot pass, even when an ancestor successfully tested identical build inputs. Add a bounded recovery command or documented procedure that verifies an eligible ancestor's successful runs or triggers and verifies the missing workflow at HEAD. Test recovery across separate processes, including refusal when intervening code remains untested.

HIGHEST: P2
PLAN: gaps
OVERALL: needs-attention

- Planned: the gate names the gh workflow run command, and a real run of HEAD is the recovery (P2) in the D00 T03 §1 contract, this commit.
