## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:18:41Z
- Candidate: 555dd884430d781bf4d182109504328ec9e77cc3
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 555dd884430d781bf4d182109504328ec9e77cc3
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 7
- Digest: 1cf733edef32bdeb

1. **P1, lines 68-74 and 79:** A proven path-filter skip does not prove inherited code passed. After B fails or becomes unverified, a Markdown-only C can be `skipped` against B, return `proceed`, and clear the repair budget despite retaining B’s broken code. Require successful evidence for the unchanged workflow inputs before treating a skip as gate success. Add cross-process tests for failed and unverified B followed by Markdown-only C, asserting neither clears the unresolved failure.

2. **P1, lines 73, 77 and 79:** The previously gated commit is not necessarily the previous remote head. A crash after pushing B but before gating it leaves A recorded; a later push C that reverts B and edits Markdown can incorrectly appear Markdown-only against A. The proposed test gates B, so it misses this crash window. Persist the actual push boundary before pushing, or recover authoritative push evidence; otherwise refuse to infer a skip. Test the push-before-gate crash across separate processes and reconcile the conflicting base instructions.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Planned: a skip now means inputs unchanged since the last proven commit, so a failed or unverified commit can never be skipped over (P1) in the D00 T03 §1 contract, this commit.
- Planned: no push boundary is needed: a crash between push and gate can't hide code, because the diff from the proven commit still shows it (P1) in the D00 T03 §1 contract, this commit.

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:21:50Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 555dd884430d781bf4d182109504328ec9e77cc3
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 8
- Digest: b12cfa665f42f42d

### adversarial: needs-attention

1. **P1, `scripts/ci_status.py:462-467`: Explicit `--base` bypasses proven-commit protection.** Reproduce with A green, B introducing failing code, then C changing only `README.md`. Run `--gate --sha C --base B --json`, as the documented CLI permits. With no runs for C, both workflows become `skipped`, the gate returns `proceed`, and B’s repair history is cleared despite its broken code remaining. Using the repository’s workflow filters, an in-memory reproduction returned exit 0 with `--base B`, versus exit 5 without it. Gate skip evidence must use a proven base even when `--base` is supplied.

2. **P2, `scripts/ci_status.py:446-451`: A commit-resolution timeout produces a usage error instead of a gate action.** Run `--gate --sha HEAD --wait-minutes 0.1 --json` when disk contention or process startup makes Git exceed six seconds. `resolve_commit` converts `Unavailable` into `None`, and `parser.error` exits 2, incorrectly claiming HEAD is invalid and emitting no JSON or action. Reproduced by injecting the timeout exception raised by the tool wrapper. Distinguish unavailable resolution from an invalid commit and return the gate’s unavailable result.

HIGHEST: P1
OVERALL: needs-attention

## Round 2: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:23:25Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 555dd884430d781bf4d182109504328ec9e77cc3
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: consistency
- Lines: 6
- Digest: f56d0be41347428c

### consistency: needs-attention

1. **P3, `todo/00-workspace/TODO-03-campaign-readiness.md:73`: The checked policy still instructs passing the previous remote head to `--gate`.** The candidate now rejects `--gate --base`, while the process-phase skill correctly omits it. Reproduce by following that checklist instruction: `python scripts/ci_status.py --gate --sha HEAD --base HEAD^ --json` exits 2 without a gate action. Update the checklist to require the persisted proven commit and remove the previous-remote-head instruction.

HIGHEST: P3
OVERALL: needs-attention

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:25:55Z
- Candidate: 8d4fd2bdfb52143448f53eb0d88cc1ed1a34d389
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 555dd884430d781bf4d182109504328ec9e77cc3
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 47
- Digest: 00c4a94df7b6752a

The candidate is **not complete**, and it does **not yet beat GitHub CLI for reliable CI monitoring**. Two read-only, in-memory reproductions exposed gaps in proof isolation and restart recovery. No files were modified; the full suite was not rerun because its fixtures write files.

**Part 1: completeness**

1. **present:** CLI entry point, help, workflow selection, commit selection, text and JSON output. Evidence: `scripts/ci_status.py:397`. This is `Surface: no`; menus, toolbar controls, automation names, command palettes and visual accessibility states do not apply.

2. **present:** Exact-commit lookup and pending, missing, failed and unavailable states, with red-first exit precedence. Evidence: `scripts/ci_status.py:210`, `scripts/ci_status.py:246`; tests at `scripts/tests/test_ci_status.py:113`, `:157`, `:180`, `:300`.

3. **present:** Conservative skip inference from committed workflow filters and the cumulative diff against recorded proof, including refusal when evidence is unreadable. Evidence: `scripts/ci_status.py:178`; tests at `scripts/tests/test_ci_status.py:223`, `:605`, `:615`, `:630`.

4. **missing-in-scope, P1:** Proof must belong to the workflows actually verified. `--workflow build.yml --gate` writes the same global `proven` value and clears the same repair history as a complete gate. A later Markdown-only commit can therefore report both workflows `skipped` without any successful plan run. Reproduction returned `proceed` for the build-only check, then `proceed` with `["skipped", "skipped"]` for the default gate. Evidence: `scripts/ci_status.py:347`, `:403`, `:469`. Isolate proof and repair state by workflow, or prevent partial checks from updating complete-gate authority. Add a regression covering a failed or unverified omitted workflow.

5. **present:** Durable repair counting, three-repair exhaustion, green reset and next-process read-back. Evidence: `scripts/ci_status.py:343`; tests at `scripts/tests/test_ci_status.py:412`, `:424`, `:462`.

6. **present:** Atomic state and fallback-marker replacement, corrupt-record handling, and refusal when persistence fails. These cover disk-full, permission and lock failures through the `OSError` path. Evidence: `scripts/ci_status.py:271`, `:306`, `:348`; tests at `scripts/tests/test_ci_status.py:449`, `:455`, `:489`, `:642`. The reverse path is a verified green resetting repair state; no user-content undo is needed.

7. **present:** Fractional invocation deadlines shared by Git, GitHub CLI and sleeps. Evidence: `scripts/ci_status.py:426`; tests at `scripts/tests/test_ci_status.py:392`, `:682`, `:688`.

8. **missing-in-scope, P2:** The durable two-hour budget must start before the initial lookup can be interrupted. Currently `report()` runs before `begin_wait()`. An interruption inside the first GitHub lookup left no waiting record; simulated initial queries taking 240 seconds recorded the start at 1240 instead of 1000. Evidence: `scripts/ci_status.py:476`. The existing cancellation test at `scripts/tests/test_ci_status.py:529` interrupts sleep only, after persistence. Add initial-lookup interruption and slow-first-lookup regression tests.

9. **present:** Progress on stderr, one JSON result on stdout, failure-job links and caller instructions for all actions. Evidence: `scripts/ci_status.py:374`, `:481`; `scripts/tests/test_ci_status.py:671`, `:722`, `:797`; `.codex/skills/process-phase/SKILL.md:56`.

10. **missing-in-scope, P2:** Expiry must explain why the gate stopped. The already-expired path emits `unverified`, exit 3 and an empty workflow list, but no reason, elapsed time or limit. The caller is instructed to file the reason and cannot obtain it from that result. Evidence: `scripts/ci_status.py:464`; `.codex/skills/process-phase/SKILL.md:61`. Add explicit expiry diagnostics in text and JSON, with output tests.

**Part 2: excellence**

The strongest direct alternative is **GitHub CLI**, using `gh run list --commit` to locate runs and `gh run watch --exit-status` to monitor them. It provides commit filtering, step progress, failure exits and a default three-second refresh interval. This comparison uses documented behavior, not a live performance benchmark. [Run list documentation](https://cli.github.com/manual/gh_run_list), [Run watch documentation](https://cli.github.com/manual/gh_run_watch).

1. **in-scope:** Trustworthiness falls short because a partial check can authorize workflows it never verified, as Part 1 item 4 demonstrates. **Target:** zero cross-workflow proof reuse or repair-budget resets in subset-to-default gate regression tests.

2. **in-scope:** Recovery loses time before the first response, as Part 1 item 8 demonstrates. **Target:** retain the original waiting start across interruption during initial lookup; reach `unverified` within the aggregate 7,200-second budget.

3. **in-scope:** Terminal feedback is less actionable than the alternative’s run progress and outcome display. **Target:** every `unverified` result carries a specific reason in text and JSON; expiry includes elapsed time and the applicable limit, requiring no extra diagnostic command.

4. **out-of-scope:** Completion detection can lag almost 60 seconds, versus GitHub CLI’s documented three-second refresh interval. Evidence: `scripts/ci_status.py:59`, `:484`. The section explicitly requires 60-second polling, so changing it needs a design decision owned by **00-workspace**. **Target:** a measured detection budget below three seconds, with an explicit request-cost budget. This is a proposed follow-up, not filed, because this review prohibits file changes.

**Gap list**

1. **missing-in-scope:** Isolate persisted proof and repair history by verified workflow coverage.
2. **missing-in-scope:** Persist the waiting start before initial lookup and preserve it through interruption.
3. **missing-in-scope:** Explain expired `unverified` results in text and JSON.
4. **in-scope:** Prevent partial checks from authorizing unverified workflows.
5. **in-scope:** Enforce the aggregate 7,200-second budget including initial lookup and restart.
6. **in-scope:** Make every `unverified` outcome diagnosable without another command.
7. **out-of-scope:** Revisit the mandated 60-second polling interval against GitHub CLI responsiveness; owner: 00-workspace.

COMPLETION: gaps
