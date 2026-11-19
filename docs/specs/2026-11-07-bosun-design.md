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
- In WSL, the command runs with a parent-death signal, so it stops if the shim dies, and in its own transient systemd scope, so anything it left running is stopped when it ends. Only a descendant of a shim that was itself killed can outlive the slot, because then nothing is left to stop the scope. This is an accepted limit.
- A job and everything it starts may write at most `--max-write-gb` (100 GB by default, 0 for none). On Windows the job object's I/O accounting is checked every second; in WSL the scope's `io.stat`, which needs the `io` controller delegated to user sessions by `wsl/enable-write-budget.sh`. A job over the budget is stopped whole and exits 74. Two limits are accepted, because the platforms keep no finer count: on Windows the count includes bytes written to pipes, and in WSL it excludes writes to Windows drives through `/mnt`, which travel over the host file share. In WSL every path is contained, a nested run and one without a slot included, because the Windows job cannot see what Linux processes write. A command that writes without end would otherwise fill the drive, and a full system drive takes Windows and the WSL disk image on it down with it.
- A batch file argument containing a line break is refused, because cmd.exe cannot safely receive one.
- Waiters poll, so the queue is not first-in, first-out. That is acceptable for a handful of jobs.

Defaults: 2 slots, 6144 MB minimum available, 900 s maximum wait. Each can be set by flag or by `BOSUN_SLOTS`, `BOSUN_MIN_AVAIL_MB` and `BOSUN_MAX_WAIT`.

### Memory logger

Two small samplers write the same CSV format to `%LOCALAPPDATA%\Bosun\logs` every 30 s, and files older than 14 days are pruned. `bosun report` joins them by minute.

Columns: `ts,side,kind,name,count,private_mb,rss_mb`. `kind` is `sys`, `repo` or `proc`. Timestamps and file dates are UTC on both sides. Repos are named by full path, so same-named repos stay separate.

Repo attribution: a process belongs to the git repo containing its working directory, otherwise to its parent's repo. On Linux the directory comes from `/proc/<pid>/cwd`. Windows has no public API for it, so the logger reads the process parameters from the PEB, read-only, and only for processes it is allowed to open.

## Phase 2 (after real data)

These wait for logged peaks because their thresholds depend on them.

- Pressure monitor: when Windows' available memory falls under the panel's floor, the WSL logger lowers the live `MemoryHigh` of `bosun-jobs.slice`, which holds only the jobs `bosun run` starts in WSL, and restores it once Windows is more than 1,024 MB over the floor. It only ever lowers a limit, and nothing acts until the panel's switch is on (off by default). It throttles the work that makes the peaks and does not replace the fixed WSL cap. Ownership (review round 9): `bosun-jobs.slice` is Bosun's own level and an operator's own cap goes on its parent, `bosun.slice`, which Bosun never writes; a job is held by the tighter of the two limits (cgroup v2), so restoring Bosun's level can never remove the operator's cap. A read then a write on one slice cannot be made safe against another writer of that slice (two `systemctl` calls), so the design keeps the operator off it, and a foreign value found on `bosun-jobs.slice` is still held, not overwritten, as a safety net.

  Measured 2026-11-19 from the history `bosun report` reads: 6,674 minutes joined from 2026-11-12 to 2026-11-19 (`windows-*.csv` row `vmmemWSL` private MB, against `wsl-*.csv` `mem_total_mb` minus `mem_available_mb`; no WSL file exists for 2026-11-16, so that day has no joined minutes). `.wslconfig` sets `autoMemoryReclaim=dropcache` (it is not absent; WSL's default is `gradual`).

  WSL gives memory back slowly and unreliably. Across 38 times the guest's used memory fell by 1,500 MB or more within three minutes (1,500 to 5,464 MB, median about 1,600), the VM's private memory fell by half of that drop within two hours in only 14. Where it did, the median was 13.5 minutes after the drop (range 1 to 65). Across all 38 the median change in the VM's private memory was -59 MB at 5 minutes, -102 MB at 15, -176 MB at 30 and +5 MB at 60, and it fell by 1,000 MB or more in 1, 6, 4 and 5 of them at those times. The clearest returns were 2026-11-18 01:37, 02:10 and 04:13 (UTC), where about 4,000 MB freed in the guest brought the VM down 2,200 to 3,600 MB within 15 to 30 minutes. The VM's private memory stayed 0.6 to 11 GB above the guest's used memory (median 7.9 GB) because the guest's page cache is not counted as used.

  So throttling jobs lowers what the guest uses within seconds, but the Windows side sees the memory only if and when WSL returns it, usually after more than an hour or not at all. The monitor is therefore about stopping new growth under pressure, not about freeing memory for Windows quickly.
- Background apps: lower memory priority through `SetProcessInformation` so idle apps are trimmed first. Nothing gets killed.
- Parking: one command that stops an idle repo's dev servers and databases.

## Rejected

- Working-set or standby-list cleaners: they turn free cache into page faults and slow the machine down.
- Hard per-repo memory caps: they kill work instead of delaying it.
- Dev Drive as a memory fix: ReFS uses slightly more memory than NTFS and WSL gains nothing. It remains a possible later speedup for Windows builds only.
