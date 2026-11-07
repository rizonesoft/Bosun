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

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:16:32Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 9c93c33c884fb92d905c41fd52061f9b628ad6fc
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: 439a185ad181610a

## adversarial: needs-attention

1. **P2, `scripts/ci_status.py:560`: The documented polling command suppresses the recovery command when it is needed.** After a fresh clone at a Markdown-only commit with no build run, execute `python scripts/ci_status.py --gate --sha HEAD --wait-minutes 30`. The final sleep reaches the deadline, then `report()` replaces the earlier `none` verdicts with `unavailable` because no tool may start. Recovery generation only considers `none`, so it prints no dispatch command. Repeating the prescribed command eventually reaches `unverified` without recovering. Reproduced with a fake clock and empty run responses: zero wait produces recovery commands; 30 minutes produces exit 3, `wait`, and no recovery. Preserve the missing-run recovery information across deadline exhaustion or emit it before polling.

HIGHEST: P2
OVERALL: needs-attention

## Round 2: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:17:38Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 9c93c33c884fb92d905c41fd52061f9b628ad6fc
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: consistency
- Lines: 6
- Digest: 149706c2f223ea23

## consistency: approve

No consistency findings in the candidate range.

HIGHEST: none
OVERALL: approve

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:19:58Z
- Candidate: 583766f2953f23ae508898c41f53c22b4bb10a2b
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 9c93c33c884fb92d905c41fd52061f9b628ad6fc
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 30
- Digest: 007ca9f413ae14e6

The candidate has gaps in recovery correctness and speed. References below are to `583766f`, not the later working-tree HEAD. No files were modified. I inspected the tests and ran an in-memory reproduction; I did not run the suite that creates temporary files.

**Part 1: completeness**

1. **present:** CLI entry points, help and argument refusal. `scripts/ci_status.py:453` exposes the commands; `:474` restricts gated checks to `HEAD` or a full SHA. Tests: `test_the_gate_takes_only_head_or_a_full_sha`, `test_the_gate_takes_no_workflow_filter`.
2. **present:** Distinct green, red, pending, missing and unavailable results, with deterministic exit precedence. Evidence: `scripts/ci_status.py:217`, `:255`; `Verdicts` and `OverallExit.test_table`.
3. **present:** Commit-specific lookup and newer-run precedence. Evidence: `scripts/ci_status.py:218`; `test_exact_commit_is_read_with_the_commit_filter`, `test_newer_attempts_of_the_same_commit_decide`.
4. **present:** Conservative skip proof covering the full change range, failed intervening code and exact reversts. Evidence: `scripts/ci_status.py:182`; `test_code_commit_before_a_markdown_stamp_is_not_skipped`, `test_markdown_after_a_failed_commit_is_not_a_skip`, `test_an_exact_revert_to_the_proven_commit_is_a_skip`.
5. **present:** One actionable result consumed by the campaign workflow. Evidence: `scripts/ci_status.py:367`; `.codex/skills/process-phase/SKILL.md:56`; `Policy` checks the three mirrored instructions.
6. **present:** Durable repair counts, wait start and proven commit, saved through temporary-file replacement and read after restart. Evidence: `scripts/ci_status.py:280`, `:315`, `:355`; separate-process restart tests at `scripts/tests/test_ci_status.py:446`, `:545`, `:711`.
7. **present:** Storage-error recovery, including locked records, failed writes and incomplete fallback markers. Evidence: `scripts/ci_status.py:315`, `:367`; tests at `scripts/tests/test_ci_status.py:477`, `:484`, `:511`, `:780`. Successful verification clears the repair budget; this internal bookkeeping needs no user-facing undo.
8. **present:** Bounded polling, interruption survival and progress separate from JSON output. Evidence: `scripts/ci_status.py:489`, `:535`, `:554`; tests at `scripts/tests/test_ci_status.py:527`, `:636`, `:809`, `:826`.
9. **present:** Missing-run recovery is emitted in text and JSON and survives polling timeout. Evidence: `scripts/ci_status.py:562`; tests at `scripts/tests/test_ci_status.py:965`, `:974`. Both gating workflows support manual dispatch.
10. **missing-in-scope, G1:** Recovery does not establish that it will run the requested commit. `scripts/ci_status.py:566` always emits `--ref <branch>`, normally `main`, while lookup remains pinned to `sha`. If remote `main` advances after the checked commit, executing this instruction builds another commit and cannot satisfy the original check. GitHub documents `--ref` as a branch or tag selector. The tests at `scripts/tests/test_ci_status.py:990` inject successful results without exercising dispatch-to-commit correspondence. Require a verified matching ref or an explicit mismatch recovery path, plus tests for an advanced remote branch and an older full SHA. [GitHub CLI documentation](https://cli.github.com/manual/gh_workflow_run)

UI controls, shortcuts, dialogs and visual accessibility are not requirements of this `Surface: no` section. The TODO explicitly assigns the operator-facing page to `D04 T03`.

**Part 2: excellence**

The strongest direct alternative is GitHub CLI: dispatch with `gh workflow run`, then monitor with `gh run watch --exit-status`. It provides run progress, a default three-second refresh and failure exit status. It does not replace Bosun’s durable campaign policy. [GitHub CLI watch documentation](https://cli.github.com/manual/gh_run_watch)

1. **in-scope, G2: Recovery is delayed by 30 unnecessary minutes.** The prescribed command enters polling at `scripts/ci_status.py:554` before generating recovery at `:562`. In the fresh-clone case with no runs, my in-memory reproduction performed 60 lookups and consumed 1,800 simulated seconds before returning dispatch instructions. Direct CLI dispatch can start immediately. **Target:** return actionable recovery after the first missing-run assessment, before sleeping, preserving one JSON result and the existing wait bound. Add a regression test asserting zero polling sleeps in this recovery case.
2. **in-scope, G1: Recovery lacks a dependable target.** Compared with explicitly choosing a branch or tag in GitHub CLI, Bosun supplies a command without checking whether that ref still corresponds to its fixed candidate. **Target:** zero unexplained candidate/ref mismatches; test branch movement between assessment and recovery. Do not accept another commit’s green result.

**Consolidated gaps**

1. **missing-in-scope / in-scope, G1:** `scripts/ci_status.py:566`: make recovery correspond to the checked commit, or explain and resolve a ref mismatch; add dispatch-identity regression coverage.
2. **in-scope, G2:** `scripts/ci_status.py:554`: expose actionable missing-run recovery before polling, reducing avoidable delay from 1,800 seconds to zero sleeps.

COMPLETION: gaps

- Filed: D00 T03 §13 (the recovery command names its commit, and is printed at the first look)
