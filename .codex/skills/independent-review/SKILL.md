---
name: independent-review
description: Get an independent review of your own work from the reviewer pinned in .conclave/reviewers.toml (a different model family from yours) through scripts/review-dispatch.py, then verify and act on every finding. Use before calling a nontrivial change done, before a commit that ships, or when a TODO section needs its review round.
---

# Independent review

Claude and Antigravity are the only writers; Codex is reviewer only (owner, 2026-11-14). Both writers use Astra (`gpt-6.2-astra` through `codex`) via `scripts/review-dispatch.py` and `.conclave/reviewers.toml`. Antigravity uses Orchestrator (Carbon) only as a writer. Reviews use medium effort for plan, initial adversarial and completion passes, and low effort for follow-ups. The existing lenses, 32 follow-up limit and 33 repair-cycle limit are unchanged. Historical receipts remain readable; retired reviewers cannot launch new passes. An empty response, timeout or malformed verdict is never approval. Pass `--writer-model <exact-model-version>` and keep one findings file. Never resume an operator-paused section without explicit authorization.

Use `docs/plan-review-prompt.md` before the build, `docs/review-round-prompt.md` for rounds and `docs/completion-review-prompt.md` last. Write prompts to scratch. Name exact candidate commits and read their full diff. Reviewers are read-only and receive no secrets.

Run `python scripts/review-dispatch.py --writer <writer> --writer-model <exact-model-version> --prompt <scratch-prompt> --record <findings-file>`. Add `--kind plan --candidate <full-SHA>` for the plan pass and `--kind completion --candidate <full-SHA>` for completion. Pass `--surface` consistently for a surface section.

Verify findings against the code. Fix real defects at their owning layer with regression proof. Commit fixes as new commits, never amend reviewed candidates. Preserve every failed attempt. A failed execution does not consume a logical round and is not approval; use bounded recovery, never another reviewer. A unit patched three rounds running needs rethinking.

Stop rounds on an explicit clean severity verdict. Exhaustion with unresolved findings leaves a durable blocked reason and recovery path in the findings and run checkpoint, with no stamp. Continue other authorized ready work. Completion gaps are not approval: retain the record and resolve the repair through the normal workflow without rewriting a returned pass. Do not force-push or bypass checks.
