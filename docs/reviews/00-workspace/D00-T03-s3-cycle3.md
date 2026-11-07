## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:33:44Z
- Candidate: 43e733660d586a3aea0472eb1c3ae93fa1029d1f
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 43e733660d586a3aea0472eb1c3ae93fa1029d1f
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 5
- Digest: c0f3e6cc988af5d5

1. **P2, plan, line 127:** Require `review_dead_end` to use the cycle’s initiating committed review policy when detecting exhausted rounds. At this commit, it uses the current pin through `round_cap`, while the dispatcher preserves the initiating policy. Changing `max_low_rounds` during an unfinished cycle can therefore leave the guard offering work the dispatcher refuses, or trigger repair prematurely. Add tests changing the pin in both directions after a cycle starts and proving disposition and dispatcher agree.

HIGHEST: P2
PLAN: gaps
OVERALL: needs-attention

- Planned: exhausted rounds judged by the initiating policy (P2) in the D00 T03 §3 contract, this commit.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:39:24Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 43e733660d586a3aea0472eb1c3ae93fa1029d1f
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: dffa44808b251ba0

### adversarial: needs-attention

1. **P2, `scripts/lib/todo_graph.py:1008`: Policy lookup failures silently substitute the current review budget.** A shallow clone can contain a cycle’s findings without its initiating policy commit. `record_policy` then refuses, but this handler uses `round_cap(root)` and can declare exhaustion prematurely, allowing automation to spend a repair cycle. Reproduce with an unfinished cycle started under `max_low_rounds = 8`, three returned rounds with findings, and a current pin set to 2; clone without the initiating commit and request disposition. It reports `needs-repair` although the original budget is not exhausted. An in-memory probe confirmed that a policy-read refusal becomes an exhaustion result. Propagate an unavailable-policy state, block repair, and leave unrelated rows runnable.

HIGHEST: P2
OVERALL: needs-attention

## Round 2: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:44:15Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 43e733660d586a3aea0472eb1c3ae93fa1029d1f
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: consistency
- Lines: 6
- Digest: a00cfed37c6a0b1f

### consistency: approve

No actionable consistency findings in the candidate range.

HIGHEST: none
OVERALL: approve

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:46:21Z
- Candidate: 2798c310344e29289c2888a774f2e82932265dac
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 43e733660d586a3aea0472eb1c3ae93fa1029d1f
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 32
- Digest: 21964bbaa8fb67b6

The feature has one confirmed recovery gap, so it is not complete. Its superiority over the strongest practical alternative is also not established.

Reviewed the requested range ending at `2798c310344e29289c2888a774f2e82932265dac`. References below describe that candidate. No files were modified. Tests were inspected, not rerun; the reported failure was reproduced using candidate code loaded into memory.

**Part 1: completeness**

This feature lets the unattended runner recover from failed reviews within a bounded budget, preserve evidence, and park exhausted sections for the operator. It has no UI surface; menus, tooltips, shortcuts, dialogs and visual accessibility are outside this section’s contract.

1. **present:** Discoverable `repair`, `park` and `unpark` commands with required reasons and outcome messages. Evidence: `scripts/todo-graph.py:90`.
2. **present:** Repair budget setting consumed and validated, including malformed, unreadable and incorrectly shaped configuration. Evidence: `scripts/lib/todo_graph.py:1018`; `test_a_bad_repair_cap_refuses_repair_but_still_answers` and `test_an_unusable_pin_refuses_rather_than_assuming_a_cap`.
3. **present:** Completion gaps and exhausted rounds select repair, using the initiating policy rather than a subsequently changed budget. Missing recorded policy blocks the affected row. Evidence: `scripts/lib/todo_graph.py:987`; tests at `scripts/tests/test_todo_graph.py:608` and `:642`.
4. **present:** Dependencies, person requirements and host availability take precedence over repair readiness. Evidence: `scripts/lib/todo_graph.py:1103`; `test_a_dead_end_this_session_cannot_repair_reads_what_it_waits_for`.
5. **present:** The runner continues when only repairable work remains; skills route exhaustion to parking. Evidence: `scripts/lib/run_guard.py:308`, `scripts/tests/test_run_guard.py:289`, `.codex/skills/process-phase/SKILL.md:39`; mirror checks at `scripts/tests/test_todo_graph.py:825`.
6. **present:** Repairs preserve earlier findings, select a fresh cycle and refuse duplicate attempts, stamped sections, invalid arguments and excess repairs. Evidence: `scripts/lib/todo_graph.py:1063`; tests at `scripts/tests/test_todo_graph.py:523`, `:533`, `:543` and `:723`.
7. **present:** Parking prevents repair and stamping, blocks dependants, and has an operator-directed reverse path granting one additional repair. Evidence: `scripts/lib/todo_graph.py:1087` and `:1305`; tests at `scripts/tests/test_todo_graph.py:732`, `:755` and `:1617`.
8. **present:** Persistent section markers, fresh-read cycle selection, temp-file-then-replace writes, rollback and explicit recovery instructions after rollback failure. Evidence: `scripts/lib/todo_graph.py:95`, `:384` and `:1039`; failure tests at `scripts/tests/test_todo_graph.py:672`, `:681` and `:697`.
9. **present:** New-cycle review isolation, renewed build-start ordering, and the complete exhaustion-to-successful-stamp path. Evidence: `scripts/tests/test_review_dispatch.py:1222` and `:1234`; `scripts/tests/test_todo_graph.py:1625` and `:1648`.
10. **missing-in-scope, G1:** Unreadable findings are silently classified as runnable. `scripts/lib/todo_graph.py:997` catches `OSError` and returns `None`, which becomes `ready` at line 1132. Injecting a sharing-violation `PermissionError` produced `('ready', 'can run now')`; `repair` then refused with “no dead end.” A real file lock or permission failure therefore hides the blocker and lets the guard select unusable work. Report a blocked state naming the file and error, preserve findings and budget, and add regression coverage proving unrelated rows continue and unlocking restores the correct disposition.

**Part 2: excellence**

The comparison is **GitHub Actions with GitHub CLI**, a strong practical alternative for bounded job recovery and attempt inspection. It supports rerunning failed jobs with one command and inspecting previous attempts. It does not replace Bosun’s review-approval semantics. [Rerun commands](https://cli.github.com/manual/gh_run_rerun), [attempt history](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs?tool=cli).

1. **in-scope, G1:** Error recovery and diagnostic wording fall short: an evidence-read failure appears as readiness, followed by an unrelated “no dead end” refusal. **Target:** one disposition query identifies the affected file and I/O cause; zero repair cycles or reviewer attempts are spent while unreadable; unrelated rows remain selectable; the first query after access returns reports the actual review state. This addresses the same gap as Part 1, item 10.

No measured speed or interaction benchmark supports a broader claim of superiority. No additional accessibility or visual-polish shortfall applies to this nonvisual section.

**Consolidated gaps**

1. **G1, missing-in-scope / in-scope:** `scripts/lib/todo_graph.py:997`: replace false readiness on findings I/O failure with an actionable blocked state and regression tests covering lock, permission failure, unaffected rows and recovery.

COMPLETION: gaps
