---
name: independent-review
description: Get an independent review of your own work from the reviewer pinned in .conclave/reviewers.toml (a different model family from yours) through scripts/review-dispatch.py, then verify and act on every finding. Use before calling a nontrivial change done, before a commit that ships, or when a TODO section needs its review round.
---

# Independent review

One writer and one independent reviewer: Codex uses Orchestrator (Carbon, `gemini-pro-orchestrator` via `agy`); Claude and Gemini use Astra. Claude is never a reviewer. New cycles run one plan review, one adversarial review, up to `schedule.max_low_rounds` follow-up reviews (32 here), stopping on clean, then one completion review last. The schedule sets no effort overrides: Orchestrator takes none, and Astra uses its pinned medium effort for every pass, including follow-ups formerly run at low effort. Pass `--writer-model <exact-model-version>` and keep one findings file. The dispatcher records its committed policy and actual execution outcomes. Historical receipts and in-flight cycles retain their initiating policy, reviewer and findings. Never resume an operator-paused section without explicit authorization.

Use `docs/plan-review-prompt.md` before the build, `docs/review-round-prompt.md` for rounds and `docs/completion-review-prompt.md` last. Write prompts to scratch. Name exact candidate commits and read their full diff. Reviewers are read-only and receive no secrets.

Run `python scripts/review-dispatch.py --writer <writer> --writer-model <exact-model-version> --prompt <scratch-prompt> --record <findings-file>`. Add `--kind plan --candidate <full-SHA>` for the plan pass and `--kind completion --candidate <full-SHA>` for completion. Pass `--surface` consistently for a surface section.

Verify findings against the code. Fix real defects at their owning layer with regression proof. Commit fixes as new commits, never amend reviewed candidates. Preserve every failed attempt. A failed execution does not consume a logical round and is not approval; use bounded recovery, never another reviewer. A unit patched three rounds running needs rethinking.

Stop rounds on an explicit clean severity verdict. Exhaustion with unresolved findings leaves a durable blocked reason and recovery path in the findings and run checkpoint, with no stamp. Continue other authorized ready work. Completion gaps are not approval: retain the record and resolve the repair through the normal workflow without rewriting a returned pass. Do not force-push or bypass checks.
