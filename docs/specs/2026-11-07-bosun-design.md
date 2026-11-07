# Bosun design

Goal: run at least 5 Windows repos and 5 WSL repos at once on a 32 GB machine without freezes, crashes or WSL hangs.

## Problem

A snapshot on 2026-11-07 showed Windows using about 16.3 GB of RAM with only 3 CLI sessions open, beside a WSL cap of 14 GB. That leaves about 1.5 GB of margin. Open sessions are cheap (0.5 to 0.8 GB each), so the likely cause of failures is several builds or test runs peaking together. That cause is still a hypothesis, and the logger exists to test it.

## Phase 1 (this build)

### Build queue

`bosun run -- <command>` runs a heavy command when a slot is free. It works from Windows and WSL against the same slots.

- Slots are Windows named mutexes in the session namespace. The kernel releases a mutex when its owner dies, so a crashed job cannot block the queue and no daemon is needed.
- Slot 0 always admits. Later slots admit only when Windows has at least `--min-avail-mb` available. A lone job can always start, so the gate cannot deadlock.
- After `--max-wait` seconds the job runs anyway with a warning. A queue that can stall work forever is worse than brief memory pressure.
- A nested `bosun run` sees `BOSUN_SLOT` and runs straight through, so a script that calls itself cannot deadlock on its own slot.
- On Windows the child runs in a kill-on-close job object, so the slot is never released while an orphaned build keeps running.
- In WSL, a shim holds a slot through a background `bosun.exe acquire` process on a pipe. When the shim dies for any reason, the pipe closes, the Windows process exits and the slot frees. The shim also waits for guest memory, because the VM has its own ceiling that Windows cannot see.
- In WSL, the command runs with a parent-death signal, so it stops if the shim dies. Linux applies that only to the direct child, so a descendant that detaches itself can outlive the slot. This is an accepted limit.
- A batch file argument containing a line break is refused, because cmd.exe cannot safely receive one.
- Waiters poll, so the queue is not first-in, first-out. That is acceptable for a handful of jobs.

Defaults: 2 slots, 6144 MB minimum available, 900 s maximum wait. Each can be set by flag or by `BOSUN_SLOTS`, `BOSUN_MIN_AVAIL_MB` and `BOSUN_MAX_WAIT`.

### Memory logger

Two small samplers write the same CSV format to `%LOCALAPPDATA%\Bosun\logs` every 30 s, and files older than 14 days are pruned. `bosun report` joins them by minute.

Columns: `ts,side,kind,name,count,private_mb,rss_mb`. `kind` is `sys`, `repo` or `proc`. Timestamps and file dates are UTC on both sides. Repos are named by full path, so same-named repos stay separate.

Repo attribution: a process belongs to the git repo containing its working directory, otherwise to its parent's repo. On Linux the directory comes from `/proc/<pid>/cwd`. Windows has no public API for it, so the logger reads the process parameters from the PEB, read-only, and only for processes it is allowed to open.

## Phase 2 (after real data)

These wait for logged peaks because their thresholds depend on them.

- Pressure monitor: lower the live `user.slice` `MemoryHigh` when Windows runs short, which could replace the fixed WSL cap. Unproven until we measure how quickly WSL returns memory to Windows.
- Background apps: lower memory priority through `SetProcessInformation` so idle apps are trimmed first. Nothing gets killed.
- Parking: one command that stops an idle repo's dev servers and databases.

## Rejected

- Working-set or standby-list cleaners: they turn free cache into page faults and slow the machine down.
- Hard per-repo memory caps: they kill work instead of delaying it.
- Dev Drive as a memory fix: ReFS uses slightly more memory than NTFS and WSL gains nothing. It remains a possible later speedup for Windows builds only.
