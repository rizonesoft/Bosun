---
schema_version: 1
id: review-pages
domain: 04-workflow
status: active
title: "TODO-02 -- Review pages"
---

# TODO-02 -- Review pages

> **Goal:** The state of every review is visible as it happens, using the dispatcher's own records.

> **Current state:** Measured 2026-11-07. `review-dispatch.py --check-pin --json` and `--list-rounds --json` exist. Marks arrive with `D00 T01 §1`.

## Inputs

- `design/SPEC.md` §7.
- `scripts/review-dispatch.py`, `docs/reviews/`.
- -> XREF: D04 T01 §1 -- consumed by this file
- -> XREF: D00 T01 §2 -- consumed by this file
- -> XREF: D02 T03 §4 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file
- -> XREF: D02 T02 §11 -- consumed by this file
- -> XREF: D07 T01 §4 -- consumes this file
- -> XREF: D06 T02 §8 -- consumes this file

## Outcome

- Pin problems show the dispatcher's refusal in full.
- A review in flight appears with its elapsed time and deadline.

**Adjacency:** list=applicable @ D04 T02 §2; document=not-applicable (findings files are the document); settings=not-applicable (the pin is committed); reporting=applicable @ D04 T02 §2; notifications=not-applicable (run page); permissions=not-applicable (read-only); audit=applicable @ D04 T02 §2; exchange=not-applicable (none); reverse=not-applicable (read-only)

**Adjacency rationale:** This page starts no reviews. A review run outside the skills could not count anyway, and the pin stays a committed file.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Pin and live review | D04 T01 §1, D00 T01 §2, D02 T02 §11, D02 T03 §4, D02 T03 §7 | [ ] |
| 2 | §2 | Rounds and receipts | §1 | [ ] |

## 1. Pin and live review

If the reviewer can't run, a run will stall. The pin card shows that before it happens.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §7.
**Job:** know the reviewer can run, and that a review isn't hung.
**Treatment:** two cards. Cheaper substitute that fails: checking `codex --version` and calling it fine.
**Chrome:** the shell's page frame.

**Build order:**

1. Add `--check-pin --json` to `WorkflowClient`. Done when: routes, schedule and CLI presence are typed.
2. Read `build/review-in-flight`. Done when: elapsed and deadline show.

- [ ] Build the pin card per `design/SPEC.md` §7. Done when: a broken pin fixture shows the refusal in full. Cheaper substitute: a green tick that hides the reason.
- [ ] Show marks, ignoring dead ones with the guard's rule. Done when: a dead pid's mark isn't shown as running.
- [ ] Read the pin and review records for the selected repo, running inside its distro for a WSL repo through `WorkflowClient`. Done when: a WSL fixture returns data, and a stopped distro shows "WSL not running" with no `wsl.exe` command run. Cheaper substitute: Windows repos only.
- [ ] Commit: `reviews: pin and in-flight`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~ReviewsPin` exits 0, and fails if the change is reverted.

## 2. Rounds and receipts

Evidence is what makes a stamp mean something. This makes it readable.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §7.
**Job:** see why a section passed, or why it keeps going round.
**Treatment:** tables with a detail pane. Cheaper substitute that fails: opening JSON receipts by hand.
**Chrome:** the Reviews page.

**Build order:**

1. Add `--list-rounds --json` and `query rounds`. Done when: tables fill from fixtures.
2. Show findings on select. Done when: the text of a round appears.

- [ ] List rounds per section, with verdict words, execution outcome and effort. Done when: a `timeout` outcome reads as no review, never as approval. Cheaper substitute: counting an outage as a pass.
- [ ] Show receipts with rounds spent and completion. Done when: fields match a fixture receipt.
- [ ] Show findings text from the file named by the record. Done when: an unreadable record is listed as unreadable.
- [ ] Commit: `reviews: rounds and receipts`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~ReviewsRounds` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- `python scripts/review-dispatch.py --check-pin --json` matches the page in a live check.
