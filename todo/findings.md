# Findings

Unscheduled defects noticed while working on another section. Record each entry with `file:line` and strike it with its `DNN TNN §N` reference once scheduled via `add-todo`.

- `scripts/ci_status.py`: an optional attended watch mode with three second polling, like `gh run watch`. The campaign polls every 60 seconds by design, so this waits for someone to want it (D00 T03 §1 completion review, 2026-11-07).
