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
