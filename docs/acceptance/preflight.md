# Campaign preflight

What the owner confirmed for the unattended campaign (`D00 T03 §10`), which `pwsh scripts/campaign-preflight.ps1` reads before it clears. The CLI can't report either: extra usage lives in the Claude account's settings, and a reboot can only be watched.

## What the owner confirmed

- Extra usage: off, confirmed by the owner on 2026-11-08 in the Claude account's settings (it was on, and the owner switched it off during the preflight).
- Reboot check: passed on 2026-11-08. The machine booted at 11:38:12 local time; the `Bosun attach D` task ran at 11:38:28 with result 0 and found `R:\Conclave.vhdx` already attached; D: came back as D: (ReFS, 1 TB); the owner signed in at 11:44:45 and the `Bosun heartbeat` task ticked `d:\github\bosun` at 11:49:01 with no error, 4 minutes 16 seconds later; this Claude Code session resumed.

Corrected 2026-11-10: the `D:` volume, its `Bosun attach D` task and the `Bosun heartbeat` task named above were retired. The repo is on its original drive, and its own `Bosun run watchdog` task ticks it.

## What changed on this machine, and how to undo it

Applied on 2026-11-08, each saving what it changed first.

- The hold, by `pwsh scripts/campaign-hold.ps1 -Apply`: no sleep, hibernate or display timeout (AC and DC) and no screen saver lock. Prior values are in `%LOCALAPPDATA%\Bosun\campaign-hold.json`. Undo: `pwsh scripts/campaign-hold.ps1 -Restore`.
- Admin setup, by `pwsh scripts/campaign-admin-setup.ps1 -Apply` in an elevated shell: Windows Update installs but does not restart on its own while the owner is signed in (`NoAutoRebootWithLoggedOnUsers` 1, `AUOptions` 4, both absent before); a compliance deadline or a restart the owner schedules still would, so the preflight refuses to start while a deadline is set or a restart is pending, and the SYSTEM task `Bosun dotnet final` installs the final .NET 11 SDK into `C:\Program Files\dotnet` once it ships and then removes itself. Prior values and the targets changed are in `%LOCALAPPDATA%\Bosun\admin-setup.json`, which undo needs; the task bodies are in `C:\ProgramData\Bosun\bin`. Undo, in an elevated shell: `pwsh scripts/campaign-admin-setup.ps1 -Restore`.

- The campaign's Claude sign-in: a claude.ai sign-in ends on the date `claude auth status --json` gives as `loginExpiresAt` (2026-11-18 here), so the preflight's `claude` check wants 30 days left or a long-lived token. Make one with `claude setup-token` in your own terminal and keep it with `pwsh scripts/store-claude-token.ps1`, which sets the user variable `CLAUDE_CODE_OAUTH_TOKEN` that Task Scheduler hands to the watchdog's sessions. A setup token lasts a year from the day it was made, so the script also records that day in `%LOCALAPPDATA%\Bosun\claude-token.json` (never the token) and the check wants 30 days of that year left. The token stored on 2026-11-08 lasts until 2027-11-08. Undo: `pwsh scripts/store-claude-token.ps1 -Remove`.

Each undo keeps its record when anything fails to read back, naming what, so it can be run again.
