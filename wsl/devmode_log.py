#!/usr/bin/env python3
"""WSL memory sampler. Writes the same CSV format as the Windows logger.

usage: devmode_log.py <log_dir> [--interval 30] [--once]
"""
import datetime
import os
import re
import sys
import time

PAGE_KB = os.sysconf("SC_PAGESIZE") // 1024


def repo_for_dir(path, has_git, cache):
    """Returns the path of the nearest folder holding .git, or empty.

    The full path keeps two repos that share a folder name apart.
    """
    if path in cache:
        return cache[path]
    cur = path.rstrip("/")
    repo = ""
    while cur:
        if has_git(cur):
            repo = cur
            break
        cur = cur.rpartition("/")[0]
    cache[path] = repo
    return repo


def csv_name(name):
    """Percent-encodes characters that would break a CSV row, so distinct names stay distinct."""
    return re.sub(r'[%,"\r\n]', lambda m: f"%{ord(m.group()):02X}", name)


def read_status(pid):
    """Returns (name, ppid, private_kb, rss_kb), or None for kernel threads and exited processes."""
    fields = {}
    try:
        with open(f"/proc/{pid}/status", encoding="utf-8", errors="replace") as f:
            for line in f:
                key, _, value = line.partition(":")
                fields[key] = value.strip()
    except OSError:
        return None
    if "VmRSS" not in fields:
        return None
    return (
        fields.get("Name", "?"),
        int(fields.get("PPid", "0")),
        int(fields.get("RssAnon", "0 kB").split()[0]),
        int(fields["VmRSS"].split()[0]),
    )


def attribute(procs, own_repo):
    """Maps pid to repo: a process's own repo, otherwise the nearest ancestor's."""
    result = {}
    for pid in procs:
        cur = pid
        repo = ""
        for _ in range(32):
            repo = own_repo.get(cur, "")
            if repo or cur not in procs:
                break
            cur = procs[cur][1]
        result[pid] = repo
    return result


def meminfo():
    out = {}
    with open("/proc/meminfo", encoding="ascii") as f:
        for line in f:
            key, _, value = line.partition(":")
            out[key] = int(value.split()[0])
    return out


def psi_full_avg10_x100():
    try:
        with open("/proc/pressure/memory", encoding="ascii") as f:
            for line in f:
                if line.startswith("full"):
                    return int(float(line.split()[1].split("=")[1]) * 100)
    except OSError:
        pass  # PSI is absent on kernels built without it; the column stays 0.
    return 0


def oom_kills():
    with open("/proc/vmstat", encoding="ascii") as f:
        for line in f:
            if line.startswith("oom_kill "):
                return int(line.split()[1])
    return 0


def sample(log_dir, cache):
    procs = {}
    own_repo = {}
    has_git = lambda d: os.path.exists(d + "/.git")
    for entry in os.listdir("/proc"):
        if not entry.isdigit():
            continue
        pid = int(entry)
        status = read_status(pid)
        if status is None:
            continue
        procs[pid] = status
        try:
            cwd = os.readlink(f"/proc/{pid}/cwd")
        except OSError:
            continue  # Another user's process, or it exited.
        if cwd != "/":
            own_repo[pid] = repo_for_dir(cwd, has_git, cache)
    owners = attribute(procs, own_repo)

    repos = {}
    names = {}
    for pid, (name, _, private_kb, rss_kb) in procs.items():
        for table, key in ((repos, owners[pid]), (names, name)):
            if not key:
                continue
            row = table.setdefault(key, [0, 0, 0])
            row[0] += 1
            row[1] += private_kb
            row[2] += rss_kb

    # UTC on both sides, so samples still join if Windows and WSL disagree on the time zone.
    now = datetime.datetime.now(datetime.timezone.utc)
    ts = now.strftime("%Y-%m-%dT%H:%M:%S")
    mem = meminfo()
    lines = [
        f"{ts},wsl,sys,mem_available_mb,0,{mem['MemAvailable'] // 1024},0",
        f"{ts},wsl,sys,mem_total_mb,0,{mem['MemTotal'] // 1024},0",
        f"{ts},wsl,sys,swap_used_mb,0,{(mem['SwapTotal'] - mem['SwapFree']) // 1024},0",
        f"{ts},wsl,sys,psi_full_avg10_x100,0,{psi_full_avg10_x100()},0",
        f"{ts},wsl,sys,oom_kills,0,{oom_kills()},0",
    ]
    for key, (count, private_kb, rss_kb) in sorted(repos.items()):
        lines.append(f"{ts},wsl,repo,{csv_name(key)},{count},{private_kb // 1024},{rss_kb // 1024}")
    top = sorted(names.items(), key=lambda kv: kv[1][1], reverse=True)[:15]
    for key, (count, private_kb, rss_kb) in top:
        lines.append(f"{ts},wsl,proc,{csv_name(key)},{count},{private_kb // 1024},{rss_kb // 1024}")

    path = os.path.join(log_dir, f"wsl-{now:%Y-%m-%d}.csv")
    fresh = not os.path.exists(path)
    with open(path, "a", encoding="utf-8", newline="\n") as f:
        if fresh:
            f.write("ts,side,kind,name,count,private_mb,rss_mb\n")
        f.write("\n".join(lines) + "\n")


def main(argv):
    if len(argv) < 2:
        print(__doc__, file=sys.stderr)
        return 64
    log_dir = argv[1]
    interval = int(argv[argv.index("--interval") + 1]) if "--interval" in argv else 30
    once = "--once" in argv
    os.makedirs(log_dir, exist_ok=True)
    cache = {}
    last_clear = time.monotonic()
    while True:
        sample(log_dir, cache)
        if once:
            return 0
        if time.monotonic() - last_clear > 3600:
            cache.clear()  # Repos get created and deleted during long runs.
            last_clear = time.monotonic()
        time.sleep(interval)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
