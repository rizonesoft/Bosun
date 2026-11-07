## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:55:19Z
- Candidate: b8f26c936b6ce5711c2cb97fc21d091abc4d79f4
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: b8f26c936b6ce5711c2cb97fc21d091abc4d79f4
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: c22f8cf70eef984d

1. **P1, plan, line 96:** “Offering only `ready` rows” contradicts the already-shipped §3 and would remove repairable work from the guard. Correct this to offer both `ready` and `needs-repair`, preserving the repair-or-park route. Require a regression test showing that a run containing only repairable work resumes rather than ends.

2. **P2, plan, lines 97-98:** The skill acceptance check can pass without making disposition authoritative for phase selection. At this commit, `process-plan` still instructs initial and subsequent phase selection through `query ready`, which deliberately includes host-blocked rows. Require every phase-selection step to use fresh disposition results and test a host-blocked earlier phase followed by a runnable later phase. Merely mentioning `query disposition` and removing host lists does not prove that the runner advances correctly.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: the guard offers needs-repair rows too, already tested in test_a_run_with_only_repairable_work_left_goes_on (P1) in the D00 T03 §2 contract.
- Planned: query next makes phase selection testable, and the skills use it at every step (P2) in the D00 T03 §2 contract.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T19:58:39Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: b8f26c936b6ce5711c2cb97fc21d091abc4d79f4
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: 2f6106de4302e98e

## adversarial: approve

No actionable adversarial findings in the candidate range. Checked failure-path handling and ran read-only CLI and in-memory selector probes. Full test suite was not run.

HIGHEST: none
OVERALL: approve

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:00:57Z
- Candidate: af417fb700ba9d5d19a9d383976597f07c3ec789
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: b8f26c936b6ce5711c2cb97fc21d091abc4d79f4
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 35
- Digest: ae4ba240e97e4e03

Reviewed `71e40eec..af417fb7`. **Not complete for users:** application detection can misclassify availability, and the new CLI lacks regression coverage. Thirteen read-only tests passed; the full checkpoint was not run. No files were modified.

**Part 1: completeness**

1. **present:** One disposition authority covers dependencies, people, host requirements, parking and repair. Evidence: `scripts/lib/todo_graph.py:1105`; tests `test_every_need_in_the_closed_list`, `test_a_parked_sections_dependants_wait` and `test_completion_gaps_need_repair`.
2. **present:** Desktop eligibility requires an active, unlocked session and fails closed. Evidence: `scripts/lib/host_caps.py:18`, `:34`, `:54`; `scripts/tests/test_host_caps.py:15`. The live desktop probe returned true.
3. **missing-in-scope:** Application availability is inferred from an HKLM registry key existing, without reading or checking its executable. An incomplete installation or stale registration therefore reports available. An in-memory probe confirmed an empty registration returns true. Evidence: `scripts/lib/host_caps.py:64`. Resolve and verify the registered executable; test absent, malformed, missing-target and valid registrations.
4. **missing-in-scope:** Application discovery ignores per-user App Paths registrations. A valid installation outside PATH can consequently remain `waiting-host`. Evidence: `scripts/lib/host_caps.py:68`. Windows supports both HKCU and HKLM registrations. Add discovery and regression coverage for both. [Microsoft application registration](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration)
5. **present:** Phase selection skips blocked work, includes repairable work and returns null when nothing is runnable. Evidence: `scripts/lib/todo_graph.py:1137`; `scripts/tests/test_todo_graph.py:826`, `:834`.
6. **present:** Plan entry and continuation use the selector; phase execution rereads disposition. Evidence: `.codex/skills/process-plan/SKILL.md:16`, `:70`; `.codex/skills/process-phase/SKILL.md:39`. Mirror checks passed.
7. **present:** The guard consumes disposition and preserves uncertainty instead of treating unreadable output as completion. Evidence: `scripts/lib/run_guard.py:312`; tests `test_real_rows_reads_query_disposition`, `test_an_unreadable_answer_never_ends_a_run` and `test_a_run_with_only_repairable_work_left_goes_on`.
8. **present:** Unreadable findings or policy block the affected row with a reason. Evidence: `scripts/lib/todo_graph.py:1124`; tests at `scripts/tests/test_todo_graph.py:575`, `:642`, `:661`.
9. **present:** JSON commands, refusal exits and legacy selection behavior remain available. Evidence: `scripts/todo-graph.py:124`, `:164`, `:202`; `test_a_stamped_row_is_verified_and_old_answers_are_unchanged`. Live `query next` returned valid JSON.
10. **missing-in-scope:** No regression test invokes `query next`. Existing tests exercise `next_work` and search skill text, so removing CLI registration or dispatch would escape them. Evidence: `scripts/tests/test_todo_graph.py:826`, `:850`, `:862`. Test actual CLI output and exit status for selected work and null.

GUI controls, shortcuts, visual accessibility, persisted selection, undo and disk-write recovery do not apply to this read-only, `Surface: no` selector. Campaign lifecycle and panel presentation have separate owners.

**Part 2: excellence**

**Alternative:** An attended operator using the existing graph commands and Windows application resolution. This is the strongest directly compatible fallback established here, rather than an unrelated scheduler. Windows supports per-user application registration; the operator can also verify that an application actually opens.

1. **in-scope:** Defaults and recovery fall short of that fallback when registration exists but its executable does not. Target: zero runnable classifications across missing-target registration fixtures, with eligibility restored on the next query after repair. Evidence: `scripts/lib/host_caps.py:64`.
2. **in-scope:** Per-user installations require avoidable operator diagnosis or configuration changes. Target: zero manual configuration steps for a valid HKCU-registered application outside PATH. Evidence: `scripts/lib/host_caps.py:68`.

No measured speed deficit or applicable visual-accessibility shortfall was established.

**Consolidated gap list**

1. **missing-in-scope:** Validate registered executable availability instead of accepting registry-key existence.
2. **missing-in-scope:** Detect valid per-user App Paths registrations.
3. **missing-in-scope:** Add CLI regression coverage for `query next`, including JSON null and exit status.
4. **in-scope:** Match direct availability verification: zero false-ready results for missing executable targets.
5. **in-scope:** Beat manual setup: zero configuration steps for supported per-user registrations.

COMPLETION: gaps
