---
schema_version: 1
id: findings-triage
domain: 00-workspace
status: active
title: "TODO-06 -- Findings triage"
depends_on: ["private-overlay"]
---

# TODO-06 -- Findings triage

> **Goal:** Every entry in `todo/findings.md` is either scheduled in an open section, fixed with a test, or struck with its reason, so the plan holds all known work and nothing waits outside it.

> **Current state:** Measured 2026-11-21 at `b8c4e0b3`. `todo/findings.md` holds 207 open entries (lines starting `- ` without `~~`), mostly completion-review suggestions and defects noticed in passing, none of them owned by a section.

## Inputs

- `todo/findings.md` and the header rule it carries: strike an entry with its `DNN TNN §N` once scheduled via `add-todo`.
- The `add-todo` skill, which decides whether a finding becomes a new section or joins an open one.

## Outcome

- `grep -cE '^- [^~]' todo/findings.md` prints 0.
- Every struck entry names the section that owns it, the commit that fixed it, or why it was declined.

**Adjacency:** all=not-applicable (plan maintenance: no product records or user data)

**Adjacency rationale:** Triage moves known work into the plan; the work itself is built and reviewed in the sections it lands in.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Open findings, part 1 of 6 | -- | [ ] |
| 2 | §2 | Open findings, part 2 of 6 | -- | [ ] |
| 3 | §3 | Open findings, part 3 of 6 | -- | [ ] |
| 4 | §4 | Open findings, part 4 of 6 | -- | [ ] |
| 5 | §5 | Open findings, part 5 of 6 | -- | [ ] |
| 6 | §6 | Open findings, part 6 of 6 | -- | [ ] |

## 1. Open findings, part 1 of 6

`todo/findings.md` holds defects and suggestions noticed while building other sections. Unscheduled, they block nothing and are never built, so the plan is not complete while any stays open. This section settles the open findings on lines 5 to 40 (as of `b8c4e0b3`), 35 entries.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Classify every finding in the range against the current code. Done when: each is marked as a defect, a suggestion, already fixed or superseded.
2. Schedule, fix or strike each one. Done when: `grep -nE '^- [^~]' todo/findings.md` lists none of these lines, and `python scripts/todo-graph.py validate` passes.

- [ ] Triage the open findings on lines 5 to 9 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 10 to 14 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 15 to 19 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 20 to 24 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 25 to 29 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 30 to 35 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 36 to 40 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Commit: `todo: settle findings lines 5 to 40 (D00 T06 §1)`

**Test checkpoint:** `python scripts/todo-graph.py validate` exits 0, and no line from 5 to 40 of `todo/findings.md` starts with `- ` without `~~`.

## 2. Open findings, part 2 of 6

`todo/findings.md` holds defects and suggestions noticed while building other sections. Unscheduled, they block nothing and are never built, so the plan is not complete while any stays open. This section settles the open findings on lines 41 to 75 (as of `b8c4e0b3`), 35 entries.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Classify every finding in the range against the current code. Done when: each is marked as a defect, a suggestion, already fixed or superseded.
2. Schedule, fix or strike each one. Done when: `grep -nE '^- [^~]' todo/findings.md` lists none of these lines, and `python scripts/todo-graph.py validate` passes.

- [ ] Triage the open findings on lines 41 to 45 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 46 to 50 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 51 to 55 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 56 to 60 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 61 to 65 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 66 to 70 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 71 to 75 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Commit: `todo: settle findings lines 41 to 75 (D00 T06 §2)`

**Test checkpoint:** `python scripts/todo-graph.py validate` exits 0, and no line from 41 to 75 of `todo/findings.md` starts with `- ` without `~~`.

## 3. Open findings, part 3 of 6

`todo/findings.md` holds defects and suggestions noticed while building other sections. Unscheduled, they block nothing and are never built, so the plan is not complete while any stays open. This section settles the open findings on lines 76 to 111 (as of `b8c4e0b3`), 35 entries.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Classify every finding in the range against the current code. Done when: each is marked as a defect, a suggestion, already fixed or superseded.
2. Schedule, fix or strike each one. Done when: `grep -nE '^- [^~]' todo/findings.md` lists none of these lines, and `python scripts/todo-graph.py validate` passes.

- [ ] Triage the open findings on lines 76 to 80 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 81 to 85 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 86 to 90 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 91 to 95 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 96 to 101 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 102 to 106 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 107 to 111 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Commit: `todo: settle findings lines 76 to 111 (D00 T06 §3)`

**Test checkpoint:** `python scripts/todo-graph.py validate` exits 0, and no line from 76 to 111 of `todo/findings.md` starts with `- ` without `~~`.

## 4. Open findings, part 4 of 6

`todo/findings.md` holds defects and suggestions noticed while building other sections. Unscheduled, they block nothing and are never built, so the plan is not complete while any stays open. This section settles the open findings on lines 112 to 146 (as of `b8c4e0b3`), 35 entries.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Classify every finding in the range against the current code. Done when: each is marked as a defect, a suggestion, already fixed or superseded.
2. Schedule, fix or strike each one. Done when: `grep -nE '^- [^~]' todo/findings.md` lists none of these lines, and `python scripts/todo-graph.py validate` passes.

- [ ] Triage the open findings on lines 112 to 116 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 117 to 121 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 122 to 126 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 127 to 131 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 132 to 136 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 137 to 141 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 142 to 146 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Commit: `todo: settle findings lines 112 to 146 (D00 T06 §4)`

**Test checkpoint:** `python scripts/todo-graph.py validate` exits 0, and no line from 112 to 146 of `todo/findings.md` starts with `- ` without `~~`.

## 5. Open findings, part 5 of 6

`todo/findings.md` holds defects and suggestions noticed while building other sections. Unscheduled, they block nothing and are never built, so the plan is not complete while any stays open. This section settles the open findings on lines 147 to 181 (as of `b8c4e0b3`), 35 entries.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Classify every finding in the range against the current code. Done when: each is marked as a defect, a suggestion, already fixed or superseded.
2. Schedule, fix or strike each one. Done when: `grep -nE '^- [^~]' todo/findings.md` lists none of these lines, and `python scripts/todo-graph.py validate` passes.

- [ ] Triage the open findings on lines 147 to 151 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 152 to 156 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 157 to 161 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 162 to 166 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 167 to 171 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 172 to 176 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 177 to 181 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Commit: `todo: settle findings lines 147 to 181 (D00 T06 §5)`

**Test checkpoint:** `python scripts/todo-graph.py validate` exits 0, and no line from 147 to 181 of `todo/findings.md` starts with `- ` without `~~`.

## 6. Open findings, part 6 of 6

`todo/findings.md` holds defects and suggestions noticed while building other sections. Unscheduled, they block nothing and are never built, so the plan is not complete while any stays open. This section settles the open findings on lines 182 to 214 (as of `b8c4e0b3`), 32 entries.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Classify every finding in the range against the current code. Done when: each is marked as a defect, a suggestion, already fixed or superseded.
2. Schedule, fix or strike each one. Done when: `grep -nE '^- [^~]' todo/findings.md` lists none of these lines, and `python scripts/todo-graph.py validate` passes.

- [ ] Triage the open findings on lines 182 to 186 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 187 to 191 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 192 to 196 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 197 to 201 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 202 to 206 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 207 to 211 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Triage the open findings on lines 213 to 214 of `todo/findings.md` (as of `b8c4e0b3`): check each against the code, then schedule it through `add-todo` into a new or existing open section, fix it in this section with a test when it is a small defect, or strike it with its reason (the commit that already fixed it, the section that superseded it, or a declined suggestion with why). Done when: no finding on those lines is left unstruck, and each struck one names a `DNN TNN §N`, a commit or a reason. Cheaper substitute: striking a finding as declined without checking the code.
- [ ] Commit: `todo: settle findings lines 182 to 214 (D00 T06 §6)`

**Test checkpoint:** `python scripts/todo-graph.py validate` exits 0, and no line from 182 to 214 of `todo/findings.md` starts with `- ` without `~~`.

## Verification

- `grep -cE '^- [^~]' todo/findings.md` prints 0.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
