## Plan review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:36:30Z
- Candidate: 317b159f55ca30473bb8733e0b356a143cf417dd
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 317b159f55ca30473bb8733e0b356a143cf417dd
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: plan
- Lines: 5
- Digest: 775b8c8d3e1576cd

1. **P1 [plan]** `todo/00-workspace/TODO-03-campaign-readiness.md:73`: The policy checkpoint proves only that three skill copies contain identical text and action names. It passes even if the campaign advances on `wait`, skips repairs, or never files an exhausted fault. Add a disposable campaign scenario exercising the gate’s actual consumer: `wait` prevents advancement, `repair` triggers repair and rechecking, and `exhausted` or `unverified` creates the fault before continuing. Assert those effects, including recovery after restarting the session.

HIGHEST: P1
PLAN: gaps
OVERALL: needs-attention

- Filed: D00 T03 §10 (a disposable campaign in a real session proves the skills act on the gate, since a skill is instructions a session follows and a unit test cannot run one)

## Round 1: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T20:59:12Z
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 317b159f55ca30473bb8733e0b356a143cf417dd
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: adversarial
- Lines: 6
- Digest: fd38b20221ac1fe0

### adversarial: needs-attention

1. **P2** `scripts/ci_status.py:457`: The two-hour deadline still excludes the initial Git call. Without `--wait-minutes`, the deadline starts at infinity, and `resolve_commit()` runs before the persisted limit is applied at lines 493-494. Reproduce by invoking `--gate --sha <commit>` with one second remaining in its recorded wait while Git stalls on disk I/O. An in-memory slow-tool reproduction received a 120-second timeout and consumed 120 seconds. An already-expired wait likewise still launches Git. Read and enforce the recorded deadline before this call. The new test misses this because `scripts/tests/test_ci_status.py:863` explicitly makes commit resolution instantaneous.

HIGHEST: P2
OVERALL: needs-attention

## Round 2: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:00:47Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 317b159f55ca30473bb8733e0b356a143cf417dd
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: consistency
- Lines: 6
- Digest: a09cfe082dda9ba1

### consistency: needs-attention

1. **P2** `scripts/ci_status.py:476`: The new five-second resolution limit still violates the module’s shared deadline rule and leaves Round 1’s finding unresolved. The persisted two-hour deadline is applied only after `resolve_commit()`. Reproduce with a recorded wait that has one second remaining, then run `python scripts/ci_status.py --gate --sha <recorded-commit>` while Git stalls on disk I/O: resolution receives five seconds, exceeding the remaining budget. An expired wait still launches Git. Enforce the recorded deadline before resolution rather than substituting a separate timeout.

HIGHEST: P2
OVERALL: needs-attention

## Round 3: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:01:58Z
- Effort: low
- Writer model: claude-opus-5-5
- Policy: 317b159f55ca30473bb8733e0b356a143cf417dd
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lenses: quality
- Lines: 6
- Digest: 7bc742c4602f0c6c

### quality: needs-attention

1. **P3** `scripts/tests/test_ci_status.py:890`: The test claiming to bound the entire call makes Git resolution instantaneous and advances the clock only for subsequent tools. Its one-second assertion therefore cannot detect time spent resolving `HEAD`. Reproduce by making this Git stub consume its supplied timeout and advancing the wall clock alongside the fake monotonic clock. Add that scenario so the test exercises the complete invocation, including slow Git resolution.

HIGHEST: P3
OVERALL: needs-attention

## Completion review: astra (`gpt-6-astra` via `codex`)

- Writer: claude
- Run: 2026-11-07T21:03:54Z
- Candidate: d94aa5d5fe5206efa244a751a08bb4826889e4eb
- Effort: medium
- Writer model: claude-opus-5-5
- Policy: 317b159f55ca30473bb8733e0b356a143cf417dd
- Execution: returned
- Attempt reviewer: astra
- Attempt model: gpt-6-astra
- Attempt cli: codex
- Lines: 36
- Digest: d9c0ff744341b9bc

The feature is not yet complete, and it does not beat GitHub CLI on feedback speed. I inspected the candidate range and reproduced the deadline gap using an in-memory harness. No files were modified; the file-writing test suite was not run.

Part 1: completeness

1. **present:** CLI entry points, help and invalid-combination refusals. `scripts/ci_status.py:429` defines the commands; tests cover invalid commits and forbidden gate filters at `scripts/tests/test_ci_status.py:187`, `:376`, `:521`. GUI controls, tooltips and keyboard shortcuts do not apply to this section’s `Surface: no` contract.

2. **present:** Exact-commit lookup, pending/empty/unavailable states, verdict precedence and text/JSON output. Evidence: `scripts/ci_status.py:218`, `:256`, `:403`; regression coverage at `scripts/tests/test_ci_status.py:113`, `:157`, `:180`, `:321`, `:337`.

3. **present:** Skip evidence across the full comparison range, including exact reverts, with real runs taking precedence. Evidence: `scripts/ci_status.py:183`, `:232`; tests at `scripts/tests/test_ci_status.py:204`, `:239`, `:250`, `:743`, `:768`.

4. **present:** Repair counting, exhaustion, green reset and restart persistence. Evidence: `scripts/ci_status.py:368`; tests at `scripts/tests/test_ci_status.py:434`, `:446`, `:467`. Green reset supplies the reverse path for this internal bookkeeping.

5. **present:** Atomic state replacement, corrupt-state warnings, locked-file fallback and explicit refusal when state cannot be saved. Evidence: `scripts/ci_status.py:281`, `:316`, `:374`; tests at `scripts/tests/test_ci_status.py:471`, `:477`, `:484`, `:511`, `:780`, `:790`. These cover the common write-failure handling used for permissions and exhausted storage.

6. **present:** Read-only proof lookup, bounded polling, progress on stderr and reasons for `unverified`. Evidence: `scripts/ci_status.py:333`, `:517`; tests at `scripts/tests/test_ci_status.py:554`, `:611`, `:809`, `:939`, `:946`.

7. **missing-in-scope:** The saved two-hour deadline does not cover the documented `--sha HEAD` command. `scripts/ci_status.py:480` resolves the name before applying the persisted deadline at `:500`. With one second remaining, my harness observed a five-second Git call; with the deadline already expired, it still observed that call. This contradicts `todo/00-workspace/TODO-03-campaign-readiness.md:89`. The test at `scripts/tests/test_ci_status.py:883` makes Git resolution instantaneous and therefore misses it. Require the complete invocation to respect the remaining second, and an expired invocation to launch no tool.

8. **missing-out-of-scope:** Actual campaign consumption remains unverified: preventing advancement on `wait`, performing repairs, filing exhausted/unverified faults and preserving those effects after restart. `scripts/tests/test_ci_status.py:1005` checks mirrored instructions, not execution. Already assigned to **00-workspace, D00 T03 §10**, at `todo/00-workspace/TODO-03-campaign-readiness.md:338`; no duplicate section is needed.

Part 2: excellence

The strongest practical alternative is **GitHub CLI**, using `gh run list --commit <sha>` followed by `gh run watch <run-id> --exit-status`. Its documented watch refreshes every three seconds and displays step progress, including a compact relevant/failed-step view. [Run list documentation](https://cli.github.com/manual/gh_run_list), [Run watch documentation](https://cli.github.com/manual/gh_run_watch).

1. **out-of-scope:** Completion feedback is slower. Bosun polls every 60 seconds (`scripts/ci_status.py:65`, `:520`), so a completed run can remain reported as pending for nearly another minute, excluding request time. Target: an attended monitoring mode with completion detection within three seconds plus request latency. The section explicitly specifies 60-second polling, so this needs a design decision and a **00-workspace** follow-up, not an unrequested change to its default.

2. **out-of-scope:** Waiting feedback lacks the alternative’s job/step detail. `scripts/ci_status.py:518` reports workflow verdicts and time only; the operator must open another command or page to distinguish an advancing build from a stalled step. Target: expose the current job/step and run URL in the same monitoring session, requiring zero additional commands for that diagnosis. Assign to **04-workflow, D04 T03**, with structured data supplied by the owning tool.

Gap list

1. **missing-in-scope:** Enforce the persisted deadline before `HEAD` resolution and add a slow-resolution regression test (`scripts/ci_status.py:480`; `scripts/tests/test_ci_status.py:883`).
2. **missing-out-of-scope:** Complete the already-filed real campaign consumer/restart verification, **D00 T03 §10** (`todo/00-workspace/TODO-03-campaign-readiness.md:338`).
3. **out-of-scope:** Design attended completion feedback within three seconds plus request latency, **00-workspace** (`scripts/ci_status.py:65`).
4. **out-of-scope:** Provide current job/step and run-link feedback without another command, **D04 T03** (`scripts/ci_status.py:518`).

COMPLETION: gaps
