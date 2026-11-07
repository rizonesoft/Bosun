---
name: review-todo-section
description: Quality-gate a just-implemented TODO section in Antigravity with self-review, independent review lenses and a bounded fix loop, then write the Verified stamp through the stamp command. Also runs in audit stance on shipped sections. Use after process-todo-section or when asked whether a section is really done.
---

# Review TODO Section

The gate between "code exists" and "the row says `[x]`". Nothing else may flip that row, and the flip happens only through `python scripts/todo-graph.py stamp`, which refuses a section with unticked items, an open prerequisite, a waiting Need, or no review evidence.

One writer and one independent reviewer: Codex uses Argon; Claude, Gemini and Meta/Muse use Astra. Claude is never a reviewer. New cycles run one medium plan review, one medium adversarial review, up to `schedule.max_low_rounds` low reviews (default 8), stopping on clean, then one medium completion review last. Pass `--writer-model <exact-model-version>` and keep one findings file. The dispatcher records its committed policy and actual execution outcomes. Historical receipts and in-flight cycles retain their initiating policy, reviewer and findings. Never resume an operator-paused section without explicit authorization.

## Use this skill when

- A section was just implemented (`process-todo-section` ends by invoking this).
- The user asks "is §N really done?", "re-verify §N", "audit §N": run in audit stance (below).
- Before closing a TODO file with `process-todo-file`.

## Step 0: resolve the argument

Same front door as `process-todo-section`: `python scripts/todo-graph.py resolve "<section-ref>"`. Exit 3 means the row is already `[x]`: that is the signal for audit stance, not an error.

## The three questions

Every review answers these with code and behavior evidence, never assertion:

1. **Does it make sense?** Does the implementation produce the exact outcome the section asked for, without unnecessary scope?
2. **Is it logical?** Do the contracts agree: threading rules, error paths, settings consumers, UI states, failure behavior?
3. **Did it break anything else?** Inspect callers, consumers, tests and adjacent workflows that read what changed.

## Workflow

### 1. Fix the candidate

Name the candidate: the implementation commit, or the commit range (`<BASE_SHA>..<TIP_SHA>`) when the section landed across multiple logical step commits or when fix-loop commits append (commits are never amended once review starts). Record the full SHA(s) in the findings file at `docs/reviews/<NN-domain>/D<NN>-T<NN>-s<N>.md`. For an audit, the candidate is the section's own stamped commit plus every later change to the files it owns, up to today's tip (`<STAMPED_SHA>..HEAD -- <owned paths>`): a regression lives in those later changes. Read the actual diff (`git show` or `git diff <BASE_SHA>..<TIP_SHA>`), not memory of writing it. Map each ticked micro-step checklist item (its `Done when:` and `Cheaper substitute:`) to the code and test that satisfy it; an item with no corresponding change or a partially met `Done when:` (`partial-flip-shipped`) is not done (untick it) or was already true (note where). On a stamped section (audit stance) edit nothing in the section by hand: record the item in the findings file, and when it is a regression, run `unstamp` first; the item is unticked in the reopened section afterwards.

### 2. Self-review

Work the three questions across the diff and its blast radius. Fix what you find, then re-ask them. Look specifically for the failure modes this codebase invites:

- The failure modes the project's `AGENTS.md`, governing plan and SPEC call out for the area you touched: list them in the findings file and check each one.
- An error path that throws, aborts or swallows instead of returning the project's typed error, or a failure reported as success.
- Work on the wrong thread, mutable state shared across threads, or unbounded recursion or allocation on hostile input.
- A file write that is not temp-then-replace, or an original modified in place.
- A hard-coded colour, size or string where design tokens or settings own it, or a control without an accessible name.
- A setting nothing reads, a command nothing reaches, or a link or automation that acts without asking.
- A test that passes without exercising the behavior.

### 3. Independent review rounds (the pinned reviewer)

The dispatcher picks each round's lenses and effort from the pin's schedule (the lens table below is the vocabulary), at least one round by the pinned independent reviewer. Write the prompt to a scratch file from `docs/review-round-prompt.md` (candidate SHA, section contract, governing documents, lenses), then run it, naming your own agent as the writer:

```bash
python3 scripts/review-dispatch.py --writer <claude|codex|gemini|muse|meta> --writer-model <exact-model-version> --prompt <scratch-review-prompt.md> --record docs/reviews/<NN-domain>/D<NN>-T<NN>-s<N>.md
```

The dispatcher appends a `## Round N` section naming the reviewer, its model, effort and lenses, or `none` with the reason when the reviewer returned no review. A round with no review counts for nothing: fix the cause (a CLI not signed in, a sandbox that cannot read the repository, so the diff goes in the prompt) and run the round again. Pass `--surface` on every round of a `Surface: yes` section so the rotation includes the `design` lens. Headings that start `## Round`, `## Completion review` or `## Plan review` are reserved for what `--record` writes: a writer's own note in a findings file uses another heading (for example `## Writer note: ...`), or the file stops being readable and the next review is refused with the offending line named. Complete that round with the candidate SHA, per-lens verdicts (`approve`, `needs-attention` with numbered findings naming `file:line`, or `advisory`), and the quoted gate outputs (the Live proof). On Windows run `python` instead of `python3`. A repository without `scripts/review-dispatch.py` gets it with `forge.py kit . reviewer`.

| Lens | Asks |
| ---- | ---- |
| `plan` | Will the section as written produce a 100% complete feature? (plan review only) |
| `plan-delivery` | Does the build deliver every planned item, with nothing quietly narrowed? |
| `adversarial` | How does this fail on hostile input, races, aborts, automation or links? |
| `consistency` | Does it match the module rules, naming, shared components and settings? |
| `integration` | Do callers and consumers still hold, with every contract kept? |
| `source-defect` | Is the upstream spec or SPEC read accurately with deviations noted? |
| `quality` | Is the code clear, tested where it can fail, and free of dead or duplicated logic? |
| `design` | Does the rendered UI match the design source and its tokens? |
| `optimization` | Is there avoidable work, allocation, latency or step count on a hot path? |
| `record` | Does evidence match what ran, with owned deferrals and earned flip? |

The loop is bounded by the initiating pin: one medium adversarial pass and up to `max_low_rounds` low passes, default 8. Failed attempts do not consume logical rounds. A low pass with P1 uses the low adversarial lens next. Stop on the first explicit `HIGHEST: none` or `HIGHEST: P3` terminal review, resolve real findings, and run final completion after the gates. At exhaustion with P1/P2 remaining, record the round count, unresolved findings, owner and recovery path; do not stamp, and repair or park it as for completion gaps. Continue other authorized ready work. Historical cycles retain their original caps and stop rules. Fixes are new commits and re-run the affected gates. A unit patched three rounds running is rethought. Never weaken checks to earn a stamp.

Architecture check: on sections touching an architectural boundary the governing plan names (process or thread contracts, data ownership, module seams), one findings-file paragraph answers whether the change keeps the governing plan's architecture intact, naming any decision it effectively revises. A revision is recorded as a plan correction, never smuggled in as code.

### 5. Re-run the gates

After the last fix, re-run the section's Test checkpoint and its owed gates (affected test suites, build warnings clean, `validate`, the Freeze check in frozen files, any sanitizer build the section names). Quote the outputs in the findings file. A fix verified by reasoning is not verified. A red gate reopens the candidate: fix, re-run to green, and re-run the affected lens, because earlier verdicts rested on the broken candidate.

### 6. Surface, feature, and frozen checks

Surface and feature completeness: 100% of every control, menu item, dialog and state on the Fidelity counterpart is working (proven on the rendered surface with real data and read-back in this review) or deferred to a named, resolving section, and every required refusal/error/undo path is exercised. Refuse the stamp for an unaccounted control or a `Cheaper substitute` shortcut. Frozen files: every `**Freeze check:**` ran and passed with the result quoted; a frozen behavior that moved without a recorded operator approval means no stamp and a question for the operator.

### Final completion review, after the clean rounds and gates

Use `docs/completion-review-prompt.md` on the final candidate with `--kind completion --candidate <full-SHA> --writer <writer> --writer-model <exact-model-version> --record <same-findings-file>`. This is the last review, after the rounds and the preceding checks. Its feature and excellence coverage remains mandatory. No returned review means no approval; a bounded retry retains the same lineage. `COMPLETION: gaps` leaves the section unstamped with durable findings and a repair path: `python scripts/todo-graph.py repair '<ref>' --reason '<why>'` starts a new review cycle in a new findings file, up to the pin's `max_repair_cycles`, and at that cap `park` sets the section aside so the run carries on. `unpark` belongs to the operator alone. Never relabel gaps as complete. Only a genuine complete verdict may stamp a new-policy cycle. Historical cycles replay their original completion order.

### 7. Stamp

Tick nothing by hand. Confirm every checklist item except `Commit:` is `[x]`, then:

```powershell
python scripts/todo-graph.py stamp 'DNN TNN §N' --sha <full candidate tip SHA> --plan 'docs/reviews/<domain>/D<NN>-T<NN>-s<N>.md' --completion 'docs/reviews/<domain>/D<NN>-T<NN>-s<N>.md:complete' --review '1:docs/reviews/<domain>/D<NN>-T<NN>-s<N>.md:clean' --check '<checkpoint command> -> <quoted result>'
```

One `--plan` (required, naming the section's own findings file; `stamp` refuses without it, with any other file, with a findings file that holds no `## Plan review` returned by another model family and ending `PLAN: complete` or `PLAN: gaps` then its `OVERALL:` line, or with a plan review whose candidate is not the commit that recorded the section's `Started:` line or an earlier one), one `--completion` (required; `stamp` refuses without it, with an unknown verdict, with a findings file that holds no returned `## Completion review`, with a verdict the review's `COMPLETION:` line does not support, or with a `filed` verdict whose `- Filed:` refs are missing or do not resolve), one `--review` per round, one `--check` per gate actually run. `stamp` also replays the pin's `[schedule]` (at the initiating policy commit, recorded in the receipt) through the dispatcher's own scheduler and refuses any pass, the plan review, the completion review or a recorded round (outages included), whose effort or lenses differ from what the schedule gave it, naming the pass, the expected and the recorded values. For a historical cycle only, with a `stopped-at-33` or `stopped-low-severity` verdict add `--cap-outcome '<what was done when the cap was reached>'` (which leftovers were filed where, or why the section stays unstamped); `stamp` refuses a stopped verdict without it. The receipt records the plan review (`plan`: findings file, candidate, reviewer, model, effort, lenses), the pin's schedule (`schedule`), the completion review (`completion`: findings file, verdict, candidate, reviewer, model, filed sections), `rounds_spent` (every round the dispatcher recorded in the findings file, outage rounds included), `round_cap` and `cap_outcome`; `python scripts/todo-graph.py query rounds` lists them across stamped sections so the cap can be tuned. `stamp` writes the receipt `docs/reviews/<ref>.json`, ticks the `Commit:` item and the Implementation Order row, appends the `Verified:` and `Review:` lines, marks the file `done` when its last section stamps, and the CLI syncs the plan. Commit the stamp, receipt, findings file and regenerated plan together as `todo: stamp <ref>`. Add any `Deferred:` lines (each with its `-> XREF:` owner) and `Started:` to `Duration:` notes to the section before stamping, since a stamped section's checklist is never rewritten; a later change is a new section that names the old one.

### 8. Audit stance

On an already-`[x]` section: run the self-review, independent rounds and gates against the section's own candidate and receipt (an audit uses a new plan and final completion in its own findings file; a receipt stamped before completion reviews existed has none and stays valid without one), changing nothing in the section itself until `unstamp` has reopened it. Record the audit's rounds in a findings file of their own, `docs/reviews/<NN-domain>/audit-d<NN>-t<NN>-s<N>.md` (`--record` that path), never in the section's stamped findings file: its receipt names that file's last round, so a round appended there makes the receipt refuse validation and blocks `unstamp`. Confirm the stamp's evidence still holds (re-run the checkpoint) or find the regression. The only permitted row change is `[x]` back to `[ ]`, and only through `python scripts/todo-graph.py unstamp '<ref>' --reason '<what regressed; repair -> XREF: <ref>>'`, which removes the stamp lines, unticks the `Commit:` item and the row, reopens the file, keeps the receipt as `docs/reviews/<ref>.revoked-<date>.json` and adds a dated `Unstamped:` note (a hand edit leaves the graph refusing a checkbox/stamp mismatch). File the repair with `add-todo` first so the reason can name it. A reopened section re-stamps on its repair, never on the evidence that approved the regressed work: once the `unstamp` is committed, its own findings file is `D<NN>-T<NN>-s<N>-cycle<n+1>.md` (n being its `Unstamped:` notes), which takes the repair's plan review, completion review and every round, and its build start is the first commit holding its `Started:` line and every `Unstamped:` note, so the repair writes a fresh `Started:` line after its plan review, as `process-todo-section` step 3a does. Say plainly that downstream sections stamped on this one are suspect until it re-stamps: they keep their stamps (the graph accepts a stamped section whose prerequisite carries an `Unstamped:` note) and `unstamp` prints them. A re-confirmed row keeps its stamp; say so in one line.

## Guardrails

- Do not stamp without a findings file, its one plan review, its one completion review, and at least one independent round.
- Do not stamp an unaccounted control on a surface section, or a moved frozen behavior.
- Do not hand-write `Verified:` lines, hand-flip rows, or edit a receipt. `stamp` owns the record.
- Do not review the working tree when the candidate is a commit or range. Name the hashes.
- Do not transfer evidence across changed candidates: a new tip re-runs its gates.
