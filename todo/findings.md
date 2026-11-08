# Findings

Unscheduled defects noticed while working on another section. Record each entry with `file:line` and strike it with its `DNN TNN §N` reference once scheduled via `add-todo`.

- `scripts/ci_status.py`: an optional attended watch mode with three second polling, like `gh run watch`. The campaign polls every 60 seconds by design, so this waits for someone to want it (D00 T03 §1 completion review, 2026-11-07).
- `scripts/campaign-hold.ps1`: an optional campaign-bound hold that restores itself within one heartbeat of the run ending, with a maximum expiry, as PowerToys Awake does (D00 T03 §10 completion review, 2026-11-08).
- `scripts/campaign-hold.ps1`: one status command showing the hold's age, its settings and the exact restore command, later on the panel's machine page (D00 T03 §10 completion review, 2026-11-08).
- `scripts/campaign-preflight.ps1`: show local failures within five seconds and each slow probe's elapsed time, measured before optimizing (D00 T03 §10 completion review, 2026-11-08).
- `scripts/campaign-preflight.ps1`: end a failing run with one ordered list of fixes, elevated steps and owner confirmations apart, then the command that rechecks them (D00 T03 §10 completion review, 2026-11-08).
