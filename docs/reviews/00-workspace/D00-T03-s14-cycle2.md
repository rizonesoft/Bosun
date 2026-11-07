## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:45:49Z
- Candidate: 4c9569dcc3ad97697fdd27e1721dc5c548416a8d
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 4c9569dcc3ad97697fdd27e1721dc5c548416a8d
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: e9029ec73854348e

1. **P1, plan, line 458:** The end-to-end filed-stamp test explicitly uses the older policy, while the current-policy test calls `schedule_ok` with mocked policy lookup. Both can pass while the actual current-policy `stamp` path refuses filing. Add a committed current-policy fixture with two repairs, clean third-cycle rounds and completion gaps; invoke `stamp`, then validate in a fresh process. Assert the filed targets survive in the receipt and remain open. Exercise cycle-two refusal through the same path.

2. **P2, plan, line 456:** Runner routing remains contradictory. `process-phase/SKILL.md:52` instructs the per-row loop to handle `ready` and `needs-repair`, then “skip the rest,” which includes `needs-filing`; `process-plan/SKILL.md:24` still routes completion gaps unconditionally to repair or park. The existing tests merely check that `needs-filing` appears somewhere. Require both instructions and all mirrors to route third-cycle completion gaps to filing and stamping, and strengthen verification to reject these conflicting routes.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: a real current-policy filed stamp, and the cycle-two refusal, through stamp itself (P1) in the D00 T03 §14 contract, this commit.
- Planned: both runner skills route third-cycle gaps to filing in every mirror, with the old routes rejected by a test (P2) in the D00 T03 §14 contract, this commit.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:53:30Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 4c9569dcc3ad97697fdd27e1721dc5c548416a8d
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: 9a4a9511f4570d7f

### adversarial: approve

No actionable adversarial findings in the candidate range. Static review only; tests were not run because they create files.

HIGHEST: none
OVERALL: approve

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:55:06Z
- Candidate: c2841e90839e46a7f0d238818dc5e0c7ddcb010c
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 4c9569dcc3ad97697fdd27e1721dc5c548416a8d
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 29
- Digest: c0dc673fd318a61b

Part 1: completeness

Static review of the candidate range. No files changed; tests were inspected, not executed because they write fixtures.

1. **present:** Cycle eligibility and refusal messages. Cycles one and two reject `filed`; cycle three onwards accepts it. `scripts/lib/todo_graph.py:467`; boundary tests at `scripts/tests/test_review_policy.py:114`, `:119`, `:124`, plus actual stamp-path tests at `:273` and `:294`.
2. **present:** Evidence and filing validation. A clean last round and resolving, open targets are required; self-filing is refused. `scripts/lib/todo_graph.py:320`, `:1377`; tests at `scripts/tests/test_review_policy.py:129` and `scripts/tests/test_todo_graph.py:1184`, `:1856`.
3. **present:** Restart routing without another repair cycle. `needs-filing` is derived from durable findings and offered by the guard. `scripts/lib/todo_graph.py:1154`, `scripts/lib/run_guard.py:310`; tests at `scripts/tests/test_todo_graph.py:797` and `scripts/tests/test_run_guard.py:289`.
4. **present:** Runner instructions and policy consistency. The per-row runner handles `needs-filing`; all mirrors and the workflow document have regression checks. `scripts/tests/test_todo_graph.py:994`, `:1015`, `:1025`. Excellence suggestions remain separate from completion gaps, tested at `:1030`.
5. **present:** Persistence and read-back. Writes use temporary files, flush, fsync and replacement. Filed references survive fresh-process validation and subsequent target completion. `scripts/lib/todo_graph.py:95`; tests at `scripts/tests/test_review_policy.py:273` and `scripts/tests/test_todo_graph.py:1856`.
6. **present:** Recovery from interruption between receipt and section writes. Retry preserves the orphan receipt and stamps afresh. `scripts/lib/todo_graph.py:1399`; regression test at `scripts/tests/test_todo_graph.py:1835`.
7. **present:** Cancellation, individual write failures and reversal. Existing tests cover interruption, a failed write followed by successful rollback, and `unstamp` followed by restamping. `scripts/tests/test_todo_graph.py:1729`, `:1758`, `:1780`.
8. **missing-in-scope:** Recovery when disk failure persists through rollback. After receipt and section writes succeed, a full disk can prevent plan regeneration. The exception handler deletes the receipt **before** restoring the section; if that restoration also fails, the section remains verified with no receipt. Subsequent `stamp` starts with graph validation and cannot reach orphan recovery. Evidence: `scripts/lib/todo_graph.py:1427`, `:1433`, `:1436`. The test at `scripts/tests/test_todo_graph.py:1737` fails only one write, so rollback always succeeds. Preserve a recoverable transaction state and add a regression test where projection and rollback writes both fail, then prove recovery after storage becomes available.

Controls, shortcuts, visual states and accessibility styling do not apply to this `Surface: no` internal workflow. No new user setting is introduced.

Part 2: excellence

**Alternative: GitHub Issues with sub-issues**, a strong repository-native option for retaining unfinished work. GitHub supports creating a linked child with one `gh issue create --parent` command and displaying child states and completion progress. [Creation documentation](https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/adding-sub-issues), [progress documentation](https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/browsing-sub-issues).

1. **suggestion:** Filing requires separate section creation, a handwritten `- Filed:` reference and stamping. GitHub creates the parent relationship with the child. **Target:** one validated filing operation that creates or selects the target and records the relationship, while retaining every existing review gate.
2. **suggestion:** Inspecting leftover progress requires following receipt references to individual sections. GitHub exposes child states together. **Target:** one JSON query returning the stamped parent, every filed target, its current state and its blocking reason. This would also give the future panel an owning-tool interface.

No measured speed comparison was performed; superiority over that alternative is not established.

Gap list

1. **missing-in-scope:** `scripts/lib/todo_graph.py:1433`: preserve recoverability when both plan regeneration and rollback fail, and test retry after persistent disk failure clears.

COMPLETION: gaps
