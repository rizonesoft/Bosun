#!/usr/bin/env python3
"""WSL memory sampler. Writes the same CSV format as the Windows logger.

usage: bosun_log.py <log_dir> [--interval 30] [--once]
"""
import datetime
import json
import os
import re
import sys
import tempfile
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


def utf8_text(name):
    """A name as text both the CSV and the snapshot's JSON can hold: a Linux path need not be UTF-8, and Python keeps
    its stray bytes as surrogates, which a UTF-8 write refuses. Each such byte becomes U+FFFD (D01 T01 §4, round 1)."""
    return name.encode("utf-8", "surrogateescape").decode("utf-8", "replace")


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
    """The memory stall figure, or None on a kernel built without PSI: not measured is not 0 (D01 T01 §4)."""
    try:
        with open("/proc/pressure/memory", encoding="ascii") as f:
            for line in f:
                if line.startswith("full"):
                    return int(float(line.split()[1].split("=")[1]) * 100)
    except OSError:
        pass
    return None


def snapshot_json(at, interval, memory, repos, processes, history_written=True):
    """current-wsl.json: the schema bosun.exe writes for Windows (dm::snapshot_json), side wsl and no VM view."""
    group = lambda label, rows: [
        {label: key, "processes": count, "private_mb": private_kb // 1024, "rss_mb": rss_kb // 1024}
        for key, (count, private_kb, rss_kb) in rows
    ]
    doc = {
        "schema_version": 1,
        "side": "wsl",
        "at": at,
        "interval_s": interval,
        "memory": memory,
        "wsl_vm": None,
        "repos": group("repo", repos),
        "processes": group("name", processes),
        "history_written": history_written,
    }
    return json.dumps(doc, ensure_ascii=False, separators=(",", ":"))


def write_snapshot(log_dir, text, replace=None, attempts=20):
    """Replaces current-wsl.json through a temporary file in the same folder, so a reader sees the old snapshot or the
    new one, never part of either. The replace is retried for up to a second on PermissionError (a Windows reader
    holding the file on /mnt/c); one that still fails keeps the previous snapshot, removes the temporary file, says so
    on stderr and returns, so the logger carries on and the next sample tries again. Returns True once it landed."""
    replace = replace or os.replace
    path = os.path.join(log_dir, "current-wsl.json")
    temp = None
    try:
        # Inside the handled path (round 1): a full disk or no inodes left can refuse the temporary file itself.
        fd, temp = tempfile.mkstemp(prefix=".current-wsl.", suffix=".tmp", dir=log_dir)
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
            f.flush()
            os.fsync(f.fileno())
        for attempt in range(attempts + 1):
            try:
                replace(temp, path)
                return True
            except PermissionError:
                if attempt == attempts:
                    raise
                time.sleep(0.05)
    except OSError as e:
        print(f"[bosun] can't replace the live snapshot {path} ({e}): the previous one stays, and the next sample tries again", file=sys.stderr)
        if temp:
            try:
                os.unlink(temp)
            except OSError:
                pass
        return False
    return False


def oom_kills():
    with open("/proc/vmstat", encoding="ascii") as f:
        for line in f:
            if line.startswith("oom_kill "):
                return int(line.split()[1])
    return 0


def sample(log_dir, cache, interval=30):
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
            own_repo[pid] = utf8_text(repo_for_dir(cwd, has_git, cache))  # the raw path finds .git; the text is what's written
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
    # The live snapshot (D01 T01 §4) holds the same figures as these rows; a figure not measured has no row and is null.
    memory = {
        "available_mb": mem["MemAvailable"] // 1024,
        "total_mb": mem["MemTotal"] // 1024,
        "swap_used_mb": (mem["SwapTotal"] - mem["SwapFree"]) // 1024,
        "psi_full_avg10_x100": psi_full_avg10_x100(),
        "oom_kills": oom_kills(),
    }
    rows = (("mem_available_mb", "available_mb"), ("mem_total_mb", "total_mb"), ("swap_used_mb", "swap_used_mb"),
            ("psi_full_avg10_x100", "psi_full_avg10_x100"), ("oom_kills", "oom_kills"))
    lines = [f"{ts},wsl,sys,{name},0,{memory[key]},0" for name, key in rows if memory[key] is not None]
    repo_rows = sorted(repos.items())
    for key, (count, private_kb, rss_kb) in repo_rows:
        lines.append(f"{ts},wsl,repo,{csv_name(key)},{count},{private_kb // 1024},{rss_kb // 1024}")
    top = sorted(names.items(), key=lambda kv: kv[1][1], reverse=True)[:15]
    for key, (count, private_kb, rss_kb) in top:
        lines.append(f"{ts},wsl,proc,{csv_name(key)},{count},{private_kb // 1024},{rss_kb // 1024}")

    path = os.path.join(log_dir, f"wsl-{now:%Y-%m-%d}.csv")
    fresh = not os.path.exists(path)
    # A sample that can't go into the history (a full disk, a denied or held file) is said and shown in the snapshot,
    # and sampling carries on: the snapshot is still written, and the next sample tries the CSV again (completion
    # review of D01 T01 §4).
    history_written = True
    try:
        with open(path, "a", encoding="utf-8", newline="\n") as f:
            if fresh:
                f.write("ts,side,kind,name,count,private_mb,rss_mb\n")
            f.write("\n".join(lines) + "\n")
    except OSError as e:
        history_written = False
        print(f"[bosun] can't add this sample to {path} ({e}): history is being lost, though sampling goes on", file=sys.stderr)
    write_snapshot(log_dir, snapshot_json(ts + "Z", interval, memory, repo_rows, top, history_written))


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
        sample(log_dir, cache, interval)
        if once:
            return 0
        if time.monotonic() - last_clear > 3600:
            cache.clear()  # Repos get created and deleted during long runs.
            last_clear = time.monotonic()
        time.sleep(interval)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
