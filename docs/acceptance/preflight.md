# Campaign preflight

What the owner confirmed for the unattended campaign (`D00 T03 §10`), which `pwsh scripts/campaign-preflight.ps1` reads before it clears. The CLI can't report either: extra usage lives in the Claude account's settings, and a reboot can only be watched.

## What the owner confirmed

- Extra usage: off, confirmed by the owner on 2026-11-08 in the Claude account's settings (it was on, and the owner switched it off during the preflight).
- Reboot check: passed on 2026-11-08. The machine booted at 11:38:12 local time; the `Bosun attach D` task ran at 11:38:28 with result 0 and found `R:\Conclave.vhdx` already attached; D: came back as D: (ReFS, 1 TB); the owner signed in at 11:44:45 and the `Bosun heartbeat` task ticked `d:\github\bosun` at 11:49:01 with no error, 4 minutes 16 seconds later; this Claude Code session resumed.

## What changed on this machine, and how to undo it

Applied on 2026-11-08, each saving what it changed first.

- The hold, by `pwsh scripts/campaign-hold.ps1 -Apply`: no sleep, hibernate or display timeout (AC and DC) and no screen saver lock. Prior values are in `%LOCALAPPDATA%\Bosun\campaign-hold.json`. Undo: `pwsh scripts/campaign-hold.ps1 -Restore`.
- Admin setup, by `pwsh scripts/campaign-admin-setup.ps1 -Apply` in an elevated shell: Windows Update installs but never restarts while the owner is signed in (`NoAutoRebootWithLoggedOnUsers` 1, `AUOptions` 4, both absent before), the SYSTEM task `Bosun dotnet final` installs the final .NET 11 SDK into `C:\Program Files\dotnet` once it ships and then removes itself, and the SYSTEM task `Bosun attach D` attaches `R:\Conclave.vhdx` at startup. Prior values are in `%LOCALAPPDATA%\Bosun\admin-setup.json`; the task bodies are in `C:\ProgramData\Bosun\bin`. Undo, in an elevated shell: `pwsh scripts/campaign-admin-setup.ps1 -Restore`.

Each undo keeps its record when anything fails to read back, naming what, so it can be run again.
