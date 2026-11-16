#!/usr/bin/env python3
"""WSL memory sampler. Writes the same CSV format as the Windows logger.

usage: bosun_log.py <log_dir> [--interval 30] [--once]
       bosun_log.py instances [--json]
"""
import datetime
import json
import os
import re
import shlex
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


# --- Instances (D03 T02 §1) -------------------------------------------------------------------------------------------
# This side's agents, editors and jobs, by the rules of src/core.hpp (dm::instance_kind, instance_roots, owned_memory,
# instance_state), written in the same document as `bosun.exe instances --json`. The queue's slots are Windows', so a
# job here has no slot of its own: the panel joins it to its holder by the shim's pid (`guest_pid`).


def instance_kind(name):
    n = name.lower()
    if len(n) > 4 and n.endswith(".exe"):
        n = n[:-4]
    if n in ("claude", "codex", "agy", "code", "antigravity"):
        return n
    return ""


def read_proc(proc, pid):
    """(name, ppid, private_kb, start_ticks) from /proc/<pid>, or None for a kernel thread or a process that went."""
    status = {}
    try:
        with open(f"{proc}/{pid}/status", encoding="utf-8", errors="replace") as f:
            for line in f:
                key, _, value = line.partition(":")
                status[key] = value.strip()
        with open(f"{proc}/{pid}/stat", encoding="utf-8", errors="replace") as f:
            stat = f.read()
    except OSError:
        return None
    if "VmRSS" not in status:
        return None
    try:
        start = int(stat.rpartition(")")[2].split()[19])  # field 22: the start, in clock ticks after boot
    except (IndexError, ValueError):
        start = None
    return (status.get("Name", "?"), int(status.get("PPid", "0")), int(status.get("RssAnon", "0 kB").split()[0]), start)


def parents_of(procs):
    """Each process's parent, or 0: a parent that started after its child is a reused id, not the parent."""
    parent = {}
    for pid, (_, ppid, _, start) in procs.items():
        up = procs.get(ppid)
        real = up is not None and ppid != pid and not (start is not None and up[3] is not None and up[3] > start)
        parent[pid] = ppid if real else 0
    return parent


def first_root(pid, parent, roots):
    for _ in range(len(parent) + 1):
        if not pid:
            return 0
        if pid in roots:
            return pid
        pid = parent.get(pid, 0)
    return 0


def instance_roots(procs, parent):
    """Each process of a kind with no ancestor of the same kind (a walk back to itself is no ancestor)."""
    kind = {pid: instance_kind(info[0]) for pid, info in procs.items()}
    roots = set()
    for pid, k in kind.items():
        if not k:
            continue
        up, nested = pid, False
        for _ in range(len(parent) + 1):
            up = parent.get(up, 0)
            if not up or up == pid:
                break
            if kind.get(up) == k:
                nested = True
                break
        if not nested:
            roots.add(pid)
    return roots


def owned_memory(procs, parent, roots):
    """Each root's private memory in MB with that of every descendant whose nearest root it is, or None."""
    kb = {r: None for r in roots}
    for pid, info in procs.items():
        owner = first_root(pid, parent, roots)
        if owner:
            kb[owner] = (kb[owner] or 0) + info[2]
    return {r: (None if v is None else v // 1024) for r, v in kb.items()}


def parse_session(text):
    """(pid, proc_start, status) from a Claude Code session record, or None when it isn't one."""
    try:
        doc = json.loads(text)
    except ValueError:
        return None
    if not isinstance(doc, dict):
        return None
    pid, start, status = doc.get("pid"), doc.get("procStart"), doc.get("status")
    if not isinstance(pid, int) or isinstance(pid, bool) or pid <= 0:
        return None
    if isinstance(start, int) and not isinstance(start, bool):
        start = str(start)
    if not isinstance(start, str):
        return None
    return pid, start, status if isinstance(status, str) else None


def instance_state(mark, session, own_start):
    """(state, evidence), in the order of dm::instance_state: a live review, then the session record; else unknown."""
    if mark and mark["live"]:
        return "reviewing", f"runs under {mark['reviewer']}'s review (dispatcher pid {mark['pid']})"
    if session:
        pid, start, status = session
        if own_start is None or start != str(own_start):
            return "unknown", f"the session record for pid {pid} names a process that started at another time"
        state = {"busy": "working", "idle": "idle"}.get(status, "unknown")
        return state, f"Claude Code says {status or 'nothing'} (session record)"
    return "unknown", None


def shim_command(argv):
    """The command a `bosun run` shim runs, as its argument list, or None when the process is not one."""
    for i in range(min(2, len(argv) - 1)):
        if os.path.basename(argv[i]) == "bosun" and argv[i + 1] == "run":
            rest = argv[i + 2:]
            return rest[rest.index("--") + 1:] if "--" in rest else []
    return None


def read_json_files(folder, source, problems, parse):
    """Each parsed file in `folder`; a missing folder is none, one that can't be listed or a file that isn't the thing
    is a problem, and a file that went while it was read is skipped."""
    try:
        names = sorted(n for n in os.listdir(folder) if n.endswith(".json"))
    except FileNotFoundError:
        return []
    except OSError as e:
        problems.append({"source": source, "why": f"{folder} can't be read ({e.strerror})"})
        return []
    found = []
    for name in names:
        path = os.path.join(folder, name)
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                text = f.read(64 * 1024 + 1)
        except FileNotFoundError:
            continue
        except OSError as e:
            problems.append({"source": source, "why": f"{path} can't be read ({e.strerror})"})
            continue
        value = parse(text) if len(text) <= 64 * 1024 else None
        if value is None:
            problems.append({"source": source, "why": f"{path} is not a {'session record' if source == 'sessions' else 'review mark'}"})
        else:
            found.append(value)
    return found


def parse_mark(text):
    try:
        doc = json.loads(text)
    except ValueError:
        return None
    if not isinstance(doc, dict):
        return None
    pid, deadline = doc.get("pid"), doc.get("deadline")
    if not isinstance(pid, int) or isinstance(pid, bool) or pid < 0 or not isinstance(deadline, (int, float)) or isinstance(deadline, bool):
        return None
    # A number past a float's range is no deadline any dispatcher wrote (round 1: it had stopped the whole scan).
    try:
        deadline = float(deadline)
    except OverflowError:
        return None
    if deadline != deadline or deadline in (float("inf"), float("-inf")):
        return None
    reviewer = doc.get("reviewer")
    return {"pid": pid, "deadline": deadline, "reviewer": reviewer if isinstance(reviewer, str) else "a reviewer"}


def boot_time(proc):
    try:
        with open(f"{proc}/stat", encoding="ascii") as f:
            for line in f:
                if line.startswith("btime "):
                    return int(line.split()[1])
    except (OSError, ValueError):
        pass
    return None


def instances(proc="/proc", home=None, now=None, distro=None, ticks=None):
    """This side's instances document (the shape of dm::instances_json)."""
    home = home if home is not None else os.path.expanduser("~")
    now = time.time() if now is None else now
    ticks = ticks or os.sysconf("SC_CLK_TCK")
    procs = {}
    for entry in os.listdir(proc):
        if entry.isdigit():
            info = read_proc(proc, int(entry))
            if info is not None:
                procs[int(entry)] = info
    parent = parents_of(procs)
    agents = instance_roots(procs, parent)
    shims = {}
    for pid in procs:
        try:
            with open(f"{proc}/{pid}/cmdline", "rb") as f:
                argv = [a.decode("utf-8", "surrogateescape") for a in f.read().split(b"\0") if a]
        except OSError:
            continue
        command = shim_command(argv)
        if command is not None:
            shims[pid] = command
    # A shim inside another job's slot passes straight through: the outer one is the job.
    jobs = {pid for pid in shims if not first_root(parent.get(pid, 0), parent, set(shims))}
    memory = owned_memory(procs, parent, agents | jobs)
    btime = boot_time(proc)

    def started(pid):
        start = procs[pid][3]
        if start is None or btime is None:
            return None
        return datetime.datetime.fromtimestamp(btime + start // ticks, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    problems = []
    sessions = {s[0]: s for s in read_json_files(os.path.join(home, ".claude", "sessions"), "sessions", problems, parse_session)}
    cache, marks_by_repo, unreadable, rows = {}, {}, 0, []

    def where(pid):
        try:
            cwd = os.readlink(f"{proc}/{pid}/cwd")
        except OSError:
            return None, None
        repo = repo_for_dir(cwd, lambda d: os.path.exists(d + "/.git"), cache) if cwd != "/" else ""
        return utf8_text(cwd), (utf8_text(repo) if repo else None)

    for pid in sorted(agents):
        cwd, repo = where(pid)
        if cwd is None:
            unreadable += 1
        mark = None
        if repo:
            if repo not in marks_by_repo:
                marks = read_json_files(os.path.join(repo, "build", "review-in-flight"), "review-marks", problems, parse_mark)
                for m in marks:
                    m["live"] = m["deadline"] >= now and os.path.isdir(f"{proc}/{m['pid']}")
                marks_by_repo[repo] = marks
            marks = marks_by_repo[repo]
            found = first_root(pid, parent, {m["pid"] for m in marks})
            for m in marks:
                if found and m["pid"] == found and (mark is None or (m["live"] and not mark["live"])):
                    mark = m
        state, evidence = instance_state(mark, sessions.get(pid), procs[pid][3])
        rows.append({"side": "wsl", "kind": instance_kind(procs[pid][0]), "name": utf8_text(procs[pid][0]), "pid": pid, "cwd": cwd,
                     "repo": repo, "private_mb": memory[pid], "started_at": started(pid), "state": state, "evidence": evidence,
                     "slot": None, "command": None, "guest_pid": None, "guest_distro": None})
    for pid in sorted(jobs):
        cwd, repo = where(pid)
        command = shims[pid]
        rows.append({"side": "wsl", "kind": "job", "name": utf8_text(os.path.basename(command[0]) if command else "bosun"), "pid": pid,
                     "cwd": cwd, "repo": repo, "private_mb": memory[pid], "started_at": started(pid), "state": "unknown", "evidence": None,
                     "slot": None, "command": utf8_text(shlex.join(command)), "guest_pid": None, "guest_distro": None})
    rows.sort(key=lambda r: (r["repo"] is not None, r["repo"] or "", r["kind"], r["pid"]))
    return {"schema_version": 1, "side": "wsl", "distro": distro if distro is not None else os.environ.get("WSL_DISTRO_NAME"),
            "at": datetime.datetime.fromtimestamp(now, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "instances": rows, "unreadable": unreadable, "problems": problems}


def instances_text(doc):
    lines = [] if doc["instances"] else ["no agents, editors or jobs running"]
    for i in doc["instances"]:
        line = f"{i['kind']} {i['pid']} {i['repo'] or '(no repo)'} {i['state']}"
        if i["private_mb"] is not None:
            line += f" {i['private_mb']} MB"
        lines.append(line)
    lines += [f"can't read {p['source']}: {p['why']}" for p in doc["problems"]]
    if doc["unreadable"]:
        lines.append(f"{doc['unreadable']} processes couldn't be read")
    return "\n".join(lines) + "\n"


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


def write_snapshot(log_dir, text, replace=None, attempts=20, name="current-wsl.json"):
    """Replaces current-wsl.json (or `name`) through a temporary file in the same folder, so a reader sees the old one or the
    new one, never part of either. The replace is retried for up to a second on PermissionError (a Windows reader
    holding the file on /mnt/c); one that still fails keeps the previous snapshot, removes the temporary file, says so
    on stderr and returns, so the logger carries on and the next sample tries again. Returns True once it landed."""
    replace = replace or os.replace
    path = os.path.join(log_dir, name)
    temp = None
    try:
        # Inside the handled path (round 1): a full disk or no inodes left can refuse the temporary file itself.
        fd, temp = tempfile.mkstemp(prefix="." + name.rsplit(".", 1)[0] + ".", suffix=".tmp", dir=log_dir)
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
    write_instances(log_dir)


def instances_file(distro):
    """The file a distro's instances go to in the Windows log folder: one per distro, so two never write over each other.
    A name wsl.exe wouldn't take (or none, outside WSL) is "default"."""
    return f"instances-wsl-{distro if distro and re.fullmatch(r'[A-Za-z0-9._-]{1,64}', distro) else 'default'}.json"


def write_instances(log_dir, **scan):
    """This distro's instances, beside its snapshot, every sample (D03 T02 §1, round 1): the panel reads this file rather
    than asking wsl.exe, which would start a distro the person had stopped. Replaced whole, as the snapshot is."""
    doc = instances(**scan)
    return write_snapshot(log_dir, json.dumps(doc, separators=(",", ":"), ensure_ascii=False) + "\n", name=instances_file(doc["distro"]))


def main(argv):
    if len(argv) >= 2 and argv[1] == "instances":
        doc = instances()
        sys.stdout.write(json.dumps(doc, separators=(",", ":"), ensure_ascii=False) + "\n" if "--json" in argv else instances_text(doc))
        return 0
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
