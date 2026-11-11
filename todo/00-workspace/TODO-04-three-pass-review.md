---
schema_version: 1
id: three-pass-review
domain: 00-workspace
status: active
title: "TODO-04 -- Three-pass review from the Conclave-SDK kits"
---

# TODO-04 -- Three-pass review from the Conclave-SDK kits

> **Goal:** Every new section here is reviewed three-pass, as the Conclave-SDK reviewer and todo kits do it: one plan review, one round carrying every defect lens, one completion review, and reconciliations only after gaps, with only P1 and P2 holding the stamp. Bosun keeps everything its own reviewer does that the kit lacks, every shipped section still validates under the pin it was reviewed with, and nothing links back to the SDK.

> **Current state:** Measured 2026-11-12 with `python forge.py diff D:\Bosun reviewer` and `todo` from Conclave-SDK at `2d0c332`. Bosun reviews on a rotation pin (plan, adversarial, up to 32 low rotation rounds, completion; `max_repair_cycles = 33`). Done the same day: Gemini 4 Carbon reviews Codex work (`[reviewers.carbon]`, `gemini-4-carbon-low` and `-medium`), the `muse` and `meta` writers are gone, and a committed pin replayed for a shipped section drops routes for retired writers (`load_pin` in `scripts/review-dispatch.py`). The kit's loader refuses Bosun's pin (`models_by_effort`, `completion_order`, `max_low_rounds`, `max_repair_cycles`). A three-way merge of the kit into Bosun from their closest common versions gives 34 conflict hunks over 1768 lines in `scripts/review-dispatch.py`, 33 in its tests, 1 in `scripts/lib/todo_graph.py`, 1 in `scripts/todo-graph.py` and 1 to 8 in each skill and prompt. Bosun-only reviewer features the kit lacks: review-in-flight marks (`D00 T01 §1`), settled items in prompts (`§2`), attempt limits and diagnostics (`D00 T03 §4`), the usage-limit wait (`§5`), repair cycles and parking (`§3`), and the required `--writer-model`. Not taken, by the owner's choice: the kit's `campaign_guard.py` and its Codex and Antigravity hook files (Bosun's `scripts/run-guard.py` owns the run), and `merge_prs.py` (work lands on `main`).

## Inputs

- Conclave-SDK `kits/reviewer/` and `kits/todo/` at a named commit (`2d0c332` or later), read from its checkout (`D:\Conclave-SDK` on Windows): `scripts/review_pin.py`, `scripts/review-prompt.py`, `scripts/review-dispatch.py`, `scripts/lib/todo_graph.py`, `docs/reconciliation-review-prompt.md`, the review prompts, the `independent-review`, `review-todo-section`, `process-todo-section`, `process-phase` and `process-plan` skills, and their tests (`test_review_pin.py`, `test_review_prompt.py`, `test_three_pass.py`).
- Conclave-SDK `todo/00-reviewer/TODO-01-three-pass-review.md`: the eight sections that built three-pass there, item by item, as the checklist of behaviour to carry over.
- Files are copied and merged into this repo, never linked: no symlink, hard link, junction or path back to the SDK.
- -> XREF: D00 T01 §1 -- review-in-flight marks kept through the merge
- -> XREF: D00 T03 §4 -- attempt limits kept through the merge

## Outcome

- `python scripts/review-dispatch.py --check-pin` prints `ok` on a three-pass pin routing claude and gemini to Astra and codex to Carbon.
- A whole three-pass chain (plan, round, completion with gaps, reconciliation) dispatches, records and stamps here, and every section stamped under the rotation pin still validates.
- Every Bosun-only reviewer feature listed in Current state still has its passing tests.

**Adjacency:** all=not-applicable (developer tooling with no records a user lists, exports, is notified of or reverses)

**Adjacency rationale:** The reverse of each section is restoring the pin committed before it; shipped receipts keep their own pins either way.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Pin loader module with the three-pass keys | -- | [ ] |
| 2 | §2 | Three-pass dispatcher keeping Bosun's reviewer features | §1 | [ ] |
| 3 | §3 | Three-pass stamp rules | §2 | [ ] |
| 4 | §4 | Three-pass pin, prompts and skills | §3 | [ ] |
| 5 | §5 | Kit install record with no link to the SDK | §4 | [ ] |

## 1. Pin loader module with the three-pass keys

The kit moved pin handling into `review_pin.py`; Bosun's lives inside `review-dispatch.py`. This brings the module over and extends it to read every pin Bosun has committed.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Copy the module. Done when: `scripts/review_pin.py` loads the kit's three-pass template pin.
2. Keep Bosun's history loading. Done when: every pin committed in this repo's history loads.

- [ ] Copy Conclave-SDK `kits/reviewer/templates/scripts/review_pin.py` and `tests/test_review_pin.py` to `scripts/review_pin.py` and `scripts/tests/test_review_pin.py`, naming the SDK commit in the module docstring. Done when: `python scripts/run-tests.py test_review_pin` passes.
- [ ] Accept Bosun's rotation keys (`completion_order`, `max_low_rounds`, `max_repair_cycles`) and `models_by_effort` on an Antigravity reviewer in `scripts/review_pin.py`, for a rotation schedule only. Done when: a test loads each pin `git log -p .conclave/reviewers.toml` shows, and a three-pass schedule with `max_low_rounds` is refused naming it.
- [ ] Drop routes for writers no longer in `WRITERS` when loading a committed pin, refusing them in the live pin, as `load_pin` in `scripts/review-dispatch.py` does since 2026-11-12. Done when: `test_a_committed_pin_drops_a_retired_writer_and_the_live_pin_refuses_it` passes against `review_pin.py`.
- [ ] Commit: `review: pin loader module with the three-pass keys`

**Test checkpoint:** `python scripts/run-tests.py test_review_pin test_review_dispatch` passes; removing the rotation-key allowance fails the history test.

## 2. Three-pass dispatcher keeping Bosun's reviewer features

The merge itself: the kit's three-pass kinds, order, caps, lens verdicts, reconciliation and prompt filler go into Bosun's `scripts/review-dispatch.py`, and none of Bosun's own features is lost.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Merge. Done when: `git merge-file` of Bosun's dispatcher, its closest kit ancestor (`db20677`) and the kit's current file has every conflict resolved.
2. Prove both feature sets. Done when: the checkpoint passes.

- [ ] Resolve the three-way merge of `scripts/review-dispatch.py` (ancestor: Conclave-SDK `db20677:kits/reviewer/templates/scripts/review-dispatch.py`), loading the pin through `scripts/review_pin.py`. Done when: no conflict marker remains and `python scripts/review-dispatch.py --check-pin` prints `ok`.
- [ ] Copy `scripts/review-prompt.py` and `scripts/tests/test_review_prompt.py` from the kit. Done when: `python scripts/run-tests.py test_review_prompt` passes.
- [ ] Keep review-in-flight marks, settled items, attempt limits and diagnostics, the usage-limit wait, repair cycles and `--writer-model` working under three-pass in `scripts/review-dispatch.py`. Done when: their existing test classes (`InFlightMarks`, `Settled`, `Attempts`, `RepairCycles`, `HarnessTimeout`) pass unchanged.
- [ ] Resolve the three-way merge of `scripts/tests/test_review_dispatch.py` and add the kit's `scripts/tests/test_three_pass.py`. Done when: both pass and a whole three-pass chain runs through the command line in a test.
- [ ] Pass `--effort` to Antigravity only when its reviewer has no `models_by_effort`, so Carbon keeps its per-effort model ids. Done when: a test argv for Carbon has the scheduled model and no `--effort`.
- [ ] Commit: `review: three-pass dispatcher keeping Bosun's reviewer features`

**Test checkpoint:** `python scripts/run-tests.py test_review_dispatch test_review_policy test_three_pass test_review_prompt` passes; deleting the reconciliation kind fails `test_three_pass`.

## 3. Three-pass stamp rules

The stamp in `scripts/lib/todo_graph.py` learns the three-pass chain (receipts with `schedule_sha`, `passes_spent` and `pass_cap`, the `reconciled` and `filed` verdicts) while every rotation receipt stays valid.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Merge. Done when: the one conflict in `scripts/lib/todo_graph.py` and the one in `scripts/todo-graph.py` (ancestors `161fa4f` and `db20677`) are resolved.
2. Prove both receipt shapes. Done when: the checkpoint passes.

- [ ] Resolve the three-way merges of `scripts/lib/todo_graph.py` and `scripts/todo-graph.py`. Done when: no conflict marker remains and `python scripts/todo-graph.py validate` passes on this repo.
- [ ] Bring the kit's three-pass stamp tests into `scripts/tests/test_todo_graph.py`. Done when: a three-pass chain stamps, and `filed` under `blocking = ["P1", "P2"]` refuses a P2 and stamps a P3.
- [ ] Keep every rotation receipt valid. Done when: the existing `Stamp` and `Dispositions` tests pass with no edit to their expectations.
- [ ] Commit: `todo: stamp the three-pass chain against the section's own pin`

**Test checkpoint:** `python scripts/run-tests.py test_todo_graph` passes and `python scripts/todo-graph.py validate` passes on this repo.

## 4. Three-pass pin, prompts and skills

The switch: the repo pin moves to three-pass, the prompts and skills in all three agent folders describe it, and `AGENTS.md` says so.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Switch the pin. Done when: `--check-pin` prints `ok` on the three-pass pin.
2. Switch the words. Done when: no skill, prompt or doc describes the 33-round rotation as current.

- [ ] Rewrite `.conclave/reviewers.toml` as the kit's three-pass pin (`pass_cap = 33`, `blocking = ["P1", "P2"]`) routing claude and gemini to Astra and codex to Carbon. Done when: `python scripts/review-dispatch.py --check-pin` prints `ok`.
- [ ] Copy `docs/reconciliation-review-prompt.md` and merge `docs/plan-review-prompt.md`, `docs/review-round-prompt.md` and `docs/completion-review-prompt.md` from the kit. Done when: each names the lens verdict lines and the `HIGHEST:` line the dispatcher asks for.
- [ ] Merge the kit's `independent-review`, `review-todo-section`, `process-todo-section`, `process-phase` and `process-plan` skills into `.claude/skills/`, `.codex/skills/` and `.agents/skills/`, keeping Bosun's run guard, waits and `--writer-model` steps. Done when: the three copies of each skill state the same rules and name `--kind reconciliation`.
- [ ] Update `AGENTS.md`, `docs/todo-workflow.md` and the `test_review_policy.py` live-pin tests for three-pass. Done when: `Select-String` finds no current-tense mention of 32 low rounds, and the tests pass.
- [ ] Commit: `review: this repo reviews three-pass`

**Test checkpoint:** `python scripts/review-dispatch.py --check-pin` prints `ok`, and `python scripts/run-tests.py` passes in full.

## 5. Kit install record with no link to the SDK

So a later `forge.py diff` or `update` sees the merge as deliberate instead of drift.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Record. Done when: `python <SDK>/forge.py diff D:\Bosun reviewer` and `todo` report the merged files as recorded.

- [ ] Run `python forge.py record D:\Bosun reviewer --merged <each merged path>` and the same for `todo` from the Conclave-SDK checkout, writing `.conclave/kits.json`. Done when: `forge.py diff` reports no `differs` file without a merged note.
- [ ] Prove nothing links to the SDK. Done when: `Get-ChildItem -Recurse -Attributes ReparsePoint` under this repo finds nothing, no file has a hard link count above 1 (`fsutil hardlink list`), and `.conclave/kits.json` holds no path.
- [ ] Commit: `kits: record the three-pass merge of the reviewer and todo kits`

**Test checkpoint:** `python D:\Conclave-SDK\forge.py diff D:\Bosun reviewer` exits 0 with every merged file recorded.

## Verification

- `python scripts/review-dispatch.py --check-pin` prints `ok` on the three-pass pin.
- `python scripts/run-tests.py` passes in full, and `python scripts/todo-graph.py validate` passes.
- One real section is reviewed three-pass end to end and stamped.
