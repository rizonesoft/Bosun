# Completion and excellence review prompt

Copy this into a scratch file, fill in the placeholders, delete the lines that do not apply, and pass it to `scripts/review-dispatch.py --kind completion --candidate <FULL_SHA> --writer claude --writer-model <exact-model-version> --prompt <file> --record docs/reviews/<NN-domain>/D<NN>-T<NN>-s<N>.md`. For new cycles it runs once, last, after checks and a clean regular review (`docs/review-round-prompt.md`), and is not counted as a round. Historical in-flight cycles retain their initiating policy's ordering.

---

You are an independent product reviewer. Do not modify any file. The candidate below is a feature a TODO section has just built. Answer two questions about it: is it complete for a user, and does it do the job better than the strongest alternative a user could pick instead. Report what is missing or weaker, not what is present and fine.

## Candidate

- Commit or range: `<FULL_SHA>` or `<BASE_SHA>..<TIP_SHA>` (inspect it with `git show` or `git diff`).
- TODO section: `<DNN TNN §N>` (its contract: `python3 scripts/todo-graph.py resolve "<DNN TNN §N>"`).
- The TODO file's Goal and Outcome (with its Adjacency line): `<path to the TODO file>`.
- Fidelity: `<the design source the section names>`, or `none (Surface: no)`.
- Renders, for a `Surface: yes` section: the PNGs in `build/renders/` (one per view, state and theme, listed in `build/renders/index.json` with the commit they were drawn from), written by `pwsh scripts/render-pages.ps1`. Look at them beside the design source; a folder with no `index.json` holds no current run.
- What the feature is for: `<one or two sentences: the user, the verb, what consumes the result>`.
- Governing documents: `AGENTS.md` and `<the plan, spec or competitive analysis that applies>`.
- Weakening scan: `<the report of python scripts/weakening-scan.py --base <BASE_SHA> --head <TIP_SHA>, pasted whole: every signal with its answer, or the line saying there are none>`.

## Part 1: completeness

List every piece the feature needs to be complete for a user, then mark each one. Think the feature through end to end, the way a user meets it: an Open file feature also needs its menu item, toolbar button, keyboard shortcut, recent-files entry, drag-and-drop, error dialog and tests. Cover every kind below that applies:

- Controls, menu items and toolbar entries, each with its automation name and tooltip.
- Keyboard shortcuts and command palette entries for every command.
- Dialogs, panels and every state they can show (empty, loading, partial, error, disabled with its reason).
- Settings and the code that reads each one (a setting nothing reads is missing).
- Error and refusal paths: hostile or malformed input, cancellation, a full disk, a locked file, a missing permission.
- The undo or reverse path of every create, change or delete.
- Persistence and its read-back: what is saved, where, written temp-file-then-replace, and read back on the next start.
- Tests that would fail if the piece broke.

Mark each item `present` (name the `file:line` or test that shows it), `missing-in-scope` (the section's contract, its Fidelity surface or its Goal covers it, so the section must build it) or `missing-out-of-scope` (a real need the section's contract does not cover, so it is filed as a new section; say which domain owns it).

Proportionality: list only needs that arise in this repository's real operation: real input the project accepts (a file, request or message from outside is hostile, so a crafted one a user receives is real), a real user action, or a real operating-system, disk or hardware condition. Do not list a need that only a contrived scenario creates (an attacker who already controls the machine, the clock, the repository, the build or the tools, or a precondition no real file, user or system produces).

## Part 2: excellence

The project must do each job better than the alternatives, not only reach parity. For this feature, name how the strongest competitor or alternative does the same job (the governing documents may name them; say which you are comparing with and what you know of its behaviour), then list each concrete way the delivered feature falls short of beating it:

- Speed: time to first result, time on a large or damaged file, work done on the UI thread.
- Steps: clicks, keystrokes and dialogs for the common case, counted against the competitor's.
- Defaults: what the user gets without changing anything.
- Error recovery: what happens on a bad file, a cancel, a crash or a full disk, and whether work survives.
- Accessibility: keyboard paths, screen-reader names and order, contrast, scaling, reduced motion.
- Polish: wording, feedback while it runs, consistency with the rest of the project.

Give each shortfall a measurable target where you can (a time, a step count, a golden image). These are suggestions, not gaps: the owner decided on 2026-11-07 that only missing behaviour holds a section back, so the plan can finish, and the writer records each suggestion in `todo/findings.md`. Never propose weakening an improvement that is already decided.

## Reply format

Part 1 as a numbered list of every item with its mark and evidence. Part 2 as the competitor named, then a numbered list of shortfalls, each with its target and mark. Then a numbered gap list that repeats every `missing-in-scope` and `missing-out-of-scope` item from Part 1 in one place, one line each, with its mark. Part 2's suggestions are not gaps and are not repeated there. Then a final line: `COMPLETION: complete` when the gap list is empty, or `COMPLETION: gaps` when it is not. If you could not read the candidate at all (the commit, the diff or the files), say why in one line and end with `COMPLETION: blocked` instead: that is not a review.
