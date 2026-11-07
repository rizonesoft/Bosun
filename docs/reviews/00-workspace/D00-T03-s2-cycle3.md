## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:02:25Z
- Candidate: e598a1b4cb4a1be32bdff02262d3ec68ddc49351
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: e598a1b4cb4a1be32bdff02262d3ec68ddc49351
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: 13037728835620c6

1. **P1, plan, lines 97 and 99:** The skill acceptance checks do not prove that section execution follows the shared disposition. At the candidate, `process-phase` Step 3 still selects rows using `resolve` exit 0, which intentionally permits host-blocked rows; `process-plan` also still says completion-gap sections are not runnable. The specified text checks can pass with these contradictory instructions present. Require every execution decision to re-read disposition, remove conflicting routes in all mirrors, and test that a host-blocked row is skipped while a repairable row is routed to repair.

2. **P2, plan, lines 94 and 105:** The desktop tests can pass with broken native probes. Existing locked/disconnected tests replace `session_active` and `input_desktop` with lambdas, while the real-probe test only checks that the result is Boolean. Require tests exercising the native-call wrappers with simulated WTS and desktop responses, including failure, disconnection and a locked desktop. Otherwise an always-active or always-Default wrapper can pass the checkpoint and incorrectly admit desktop work.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: execution re-reads disposition, the conflicting routes go, in all mirrors (P1) in the D00 T03 §2 contract, this commit.
- Planned: the native desktop probes are tested through simulated Windows calls (P2) in the D00 T03 §2 contract, this commit.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:05:53Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: e598a1b4cb4a1be32bdff02262d3ec68ddc49351
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: fee3bda462adaea5

## adversarial: approve

No actionable adversarial defects found in the candidate range. Review was read-only; tests were not run.

HIGHEST: none
OVERALL: approve

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:07:35Z
- Candidate: 9092587489aa58689a4d5ef03775694a18e923ec
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: e598a1b4cb4a1be32bdff02262d3ec68ddc49351
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 26
- Digest: b59c1ab299896011

Part 1: completeness

This section supplies the operator’s unattended runner with one answer about which TODO section can run here. The guard and runner skills consume that answer. The candidate is complete for that scoped job.

1. **present:** One disposition with a reason, covering ready, host/person/dependency waits, repair, parking and verification. Evidence: `scripts/lib/todo_graph.py:1105`; `test_every_need_in_the_closed_list`, `test_a_person_need_beats_a_ready_host`, and `test_a_parked_section_is_parked`.
2. **present:** Fresh host capability detection, including installed applications rather than stale registry entries, per-user and per-machine registration, quoted paths and non-Windows behaviour. Evidence: `scripts/lib/host_caps.py:65`; `Registered` and `Capabilities` tests in `scripts/tests/test_host_caps.py:104`.
3. **present:** Desktop eligibility checks the process’s active session and input desktop. Locked, disconnected and failed probes refuse desktop work. Native wrappers release allocated memory and desktop handles. Evidence: `scripts/lib/host_caps.py:19`; `NativeCalls` tests at `scripts/tests/test_host_caps.py:36`. The live probe returned `display-session: true`.
4. **present:** Dependency and suspect-ancestor checks remain authoritative; attended work does not become runnable merely because a desktop exists. Evidence: `scripts/lib/todo_graph.py:1112`; closed-list and dependency tests in `scripts/tests/test_todo_graph.py`.
5. **present:** Review failures distinguish repairable dead ends from a clean last round awaiting completion. Unreadable findings or recorded policy produce a wait with a reason. Evidence: `scripts/lib/todo_graph.py:987`; `test_a_findings_file_that_cant_be_read_waits_and_recovers`, `test_a_cycle_whose_policy_cant_be_read_waits_and_spends_nothing`, and `test_a_clean_last_round_at_the_cap_is_waiting_for_completion_not_stuck`.
6. **present:** Command entry points return machine-readable dispositions and the next eligible row, or JSON `null`. Ordering skips host-blocked earlier work and includes repairable work. Evidence: `scripts/todo-graph.py:164`; `QueryNext` tests at `scripts/tests/test_todo_graph.py:850` and ordering tests at line 826.
7. **present:** Guard and skills consume the shared decision. Skills re-read it before each row and route repairable work to repair or parking. Evidence: `scripts/lib/run_guard.py:313`, `.codex/skills/process-phase/SKILL.md:51`, and `SkillsDefer` tests covering all three mirrors.
8. **present:** Existing `resolve`, `classify` and plan semantics remain separate and unchanged. Evidence: `scripts/lib/todo_graph.py:959`, `scripts/todo-graph.py:124`, and `test_a_stamped_row_is_verified_and_old_answers_are_unchanged`.
9. **present:** Failure recovery does not require saved capability state: queries recompute from current inputs, while failed subprocess execution or unreadable JSON returns an unknown result to the guard. Evidence: `scripts/lib/run_guard.py:313`; `test_an_unreadable_answer_never_ends_a_run`.
10. **present:** The section introduces no persistent writes or destructive commands requiring undo, atomic replacement or full-disk recovery. Repair and parking mutations belong to §3; this section reads their results. GUI controls, shortcuts, dialogs and visual accessibility are not applicable to its explicitly declared `Surface: no`.

Verification: all 17 host-capability tests passed. Graph loading and next-row selection took 0.199 seconds for 123 sections in one live measurement. Other cited tests were inspected, not executed, because their fixtures write files.

Part 2: excellence

**Alternative:** An attended operator manually checks the generated plan, dependencies, installed applications, desktop session and latest review findings before selecting work. This is the strongest directly assessable fallback for this repository’s particular local workflow; no governing document identifies a competing product implementing the same contract.

No concrete shortfall against that alternative was established within this section’s scope. Shared automatic selection removes repeated manual reconciliation. The measurement above supports responsiveness on this repository, but does not establish performance on substantially larger repositories or superiority over every external scheduler.

Gap list: empty. No files changed.

COMPLETION: complete
