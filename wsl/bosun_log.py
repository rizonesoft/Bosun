#!/usr/bin/env python3
"""WSL memory sampler. Writes the same CSV format as the Windows logger.

usage: bosun_log.py <log_dir> [--interval 30] [--once]
       --once samples and reports only: it never runs the pressure throttle, which only the running service does, so
       one process owns the slice and a one-shot run can't lower it and leave the service blind to it.
       bosun_log.py instances [--json]
"""
import datetime
import json
import os
import fcntl
import re
import shlex
import signal
import stat
import subprocess
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
# shim here is only a job's Linux half: whether it holds a slot, and when its command started, are its holder's record's
# to say (round 5). The panel lists it once joined to that holder by the shim's pid and distro, and never alone.


def instance_kind(name):
    n = name.lower()
    if len(n) > 4 and n.endswith(".exe"):
        n = n[:-4]
    if n in ("claude", "codex", "agy", "code", "antigravity"):
        return n
    return ""


class Unreadable(Exception):
    """A process whose /proc files exist but can't be read: denied, or an I/O fault. Not one that went."""


def read_proc(proc, pid):
    """(name, ppid, private_kb, start_ticks) from /proc/<pid>, or None for a kernel thread or a process that went. A
    process that is there and can't be read raises Unreadable (completion review: a denied /proc read as an empty scan)."""
    status = {}
    try:
        with open(f"{proc}/{pid}/status", encoding="utf-8", errors="replace") as f:
            for line in f:
                key, _, value = line.partition(":")
                status[key] = value.strip()
        with open(f"{proc}/{pid}/stat", encoding="utf-8", errors="replace") as f:
            stat = f.read()
    except (FileNotFoundError, ProcessLookupError):
        return None  # it exited while this looked
    except OSError as e:
        raise Unreadable(e.strerror or str(e)) from e
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


def unescape_mount(text):
    """A mountinfo field as the path it names: the kernel writes a space, tab, newline and backslash as \\040, \\011,
    \\012 and \\134."""
    return re.sub(r"\\([0-7]{3})", lambda m: chr(int(m.group(1), 8)), text)


def drive_mounts(mountinfo="/proc/self/mountinfo"):
    """The Windows folders this distro has mounted now (D03 T02 §6), from its own mount table, never from /etc/wsl.conf,
    which says what the next start will mount: each 9p or drvfs mount whose options name aname=drvfs and path=<X>:\\, as
    {"mount": "/mnt/c", "windows": "C:\\\\"}, its root field kept, so a bind of a drive's subfolder (/mnt/c/src at /work)
    is {"mount": "/work", "windows": "C:\\\\src"} (round 1); and each other mount inside one of them, as
    {"mount": ..., "windows": None}, since a guest file system mounted over part of a drive hides it there (round 1).
    None with why when the table can't be read. A drive mounted by hand is here as WSL's own are, and one the
    configuration names but nothing mounted is not."""
    try:
        with open(mountinfo, encoding="utf-8", errors="replace") as f:
            lines = f.read(4 * 1024 * 1024).splitlines()
    except OSError as e:
        return None, f"{mountinfo} can't be read ({e.strerror or e}), so which guest folders are Windows drives is not known"
    drives, others = [], []
    for line in lines:
        if " - " not in line:
            continue
        before, after = line.split(" - ", 1)
        fields, rest = before.split(" "), after.split(" ")
        if len(fields) < 5 or len(rest) < 3:
            continue
        root, mount = unescape_mount(fields[3]), unescape_mount(fields[4])
        options = rest[2]
        path = re.search(r"(?:^|[;,])path=([A-Za-z]):\\(?:[;,]|$)", options)
        if rest[0] in ("9p", "drvfs") and path and ("aname=drvfs" in options or rest[0] == "drvfs"):
            inside = root.strip("/").replace("/", "\\")
            drives.append({"mount": mount, "windows": path.group(1).upper() + ":\\" + inside})
        else:
            others.append(mount)
    def under(path, top):
        return top != "/" and path.startswith(top.rstrip("/") + "/")
    hidden = [{"mount": m, "windows": None} for m in others if any(under(m, d["mount"]) for d in drives)]
    found = drives + hidden
    found.sort(key=lambda d: d["mount"])
    return found, None


def windows_of(linux_path, drives):
    """A guest path's Windows folder, by the panel's own rule (DriveMounts.WindowsPath): the longest mount it is at or
    under; None when that mount is a guest one, or none holds it."""
    best = None
    for d in drives or []:
        mount = d["mount"].rstrip("/") if len(d["mount"]) > 1 else d["mount"]
        if (linux_path == mount or linux_path.startswith("/" if mount == "/" else mount + "/")) and (best is None or len(mount) > len(best[0])):
            best = (mount, d["windows"])
    if best is None or best[1] is None:
        return None
    tail = linux_path[len(best[0]):].strip("/").replace("/", "\\")
    return best[1] if not tail else best[1].rstrip("\\") + "\\" + tail


def guest_path(windows_path, drives):
    """A Windows folder's path in this distro, through the drive mounts (D03 T02 §6: the logger finds its log folder this
    way on each write, so moved mounts are followed): a mount of the folder or of any folder holding it (on whole
    components, as Windows compares them), joined with the rest, taken only when the panel's own rule maps it back to
    this same folder, so a guest file system mounted over it is never written into (round 2). None when none does."""
    m = re.fullmatch(r"([A-Za-z]):[\\/]?(.*)", windows_path or "")
    if not m or drives is None:
        return None
    wanted = m.group(1).upper() + ":\\" + m.group(2).replace("/", "\\").strip("\\")
    def parts(path):
        return [p for p in path.rstrip("\\").lower().split("\\") if p]
    candidates = []
    for d in drives:
        if d["windows"] is None:
            continue
        held = parts(d["windows"])
        if parts(wanted)[:len(held)] == held:
            rest = "/".join(wanted.rstrip("\\").split("\\")[len(held):])
            base = d["mount"].rstrip("/") if len(d["mount"]) > 1 else ""
            candidates.append((len(held), (base + "/" + rest) if rest else (d["mount"] if d["mount"] else "/")))
    for _, guest in sorted(candidates, key=lambda c: -c[0]):
        back = windows_of(guest, drives)
        if back is not None and parts(back) == parts(wanted):
            return guest
    return None


def log_folder(given, mountinfo="/proc/self/mountinfo"):
    """The folder the reports go to: a guest path as given (an older install), or the Windows folder (C:\\...) the
    install now gives, mapped through the drive mounts now. (None, why) when it can't be found."""
    if not re.match(r"^[A-Za-z]:[\\/]", given or ""):
        return given, None
    drives, why = drive_mounts(mountinfo)
    if drives is None:
        return None, why
    found = guest_path(given, drives)
    return (found, None) if found else (None, f"no mount holds {given} as Windows has it (none of its drive, or a guest file system over it), so the reports can't reach Windows")


def instances(proc="/proc", home=None, now=None, distro=None, ticks=None, mountinfo="/proc/self/mountinfo"):
    """This side's instances document (the shape of dm::instances_json)."""
    home = home if home is not None else os.path.expanduser("~")
    now = time.time() if now is None else now
    ticks = ticks or os.sysconf("SC_CLK_TCK")
    procs, denied, why = {}, 0, None
    for entry in os.listdir(proc):
        if entry.isdigit():
            try:
                info = read_proc(proc, int(entry))
            except Unreadable as e:
                denied, why = denied + 1, str(e)
                continue
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
    if denied:
        # Said, so a scan that read nothing is never taken for one that found nothing; an agent among them, or memory a
        # listed one's denied children hold, is missing from this document.
        problems.append({"source": "processes", "why": f"{denied} of the processes in {proc} can't be read ({why}), so instances and memory may be missing"})
    sessions = {s[0]: s for s in read_json_files(os.path.join(home, ".claude", "sessions"), "sessions", problems, parse_session)}
    cache, marks_by_repo, unreadable, rows = {}, {}, denied, []

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
        # No start: the shim's own is not its command's, which waits for a slot and for guest memory first (round 5).
        rows.append({"side": "wsl", "kind": "job", "name": utf8_text(os.path.basename(command[0]) if command else "bosun"), "pid": pid,
                     "cwd": cwd, "repo": repo, "private_mb": memory[pid], "started_at": None, "state": "unknown", "evidence": None,
                     "slot": None, "command": utf8_text(shlex.join(command)), "guest_pid": None, "guest_distro": None})
    rows.sort(key=lambda r: (r["repo"] is not None, r["repo"] or "", r["kind"], r["pid"]))
    drives, drives_why = drive_mounts(mountinfo)
    if drives_why:
        problems.append({"source": "automount", "why": drives_why})
    return {"schema_version": 1, "side": "wsl", "distro": distro if distro is not None else os.environ.get("WSL_DISTRO_NAME"),
            "at": datetime.datetime.fromtimestamp(now, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "instances": rows, "unreadable": unreadable, "problems": problems, "drives": drives}


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
    return f"instances-wsl-{safe_distro(distro)}.json"


def safe_distro(distro):
    """A distro name fit for a file name, or "default" (a name wsl.exe wouldn't take, or none outside WSL)."""
    return distro if distro and re.fullmatch(r'[A-Za-z0-9._-]{1,64}', distro) else "default"


def write_instances(log_dir, **scan):
    """This distro's instances, beside its snapshot, every sample (D03 T02 §1, round 1): the panel reads this file rather
    than asking wsl.exe, which would start a distro the person had stopped. Replaced whole, as the snapshot is."""
    doc = instances(**scan)
    return write_snapshot(log_dir, json.dumps(doc, separators=(",", ":"), ensure_ascii=False) + "\n", name=instances_file(doc["distro"]))


# --- Pressure throttle (D05 T02 §5) -----------------------------------------------------------------------------------
# When Windows' available memory (from current-windows.json, never the guest's) falls under the panel's floor, this lowers
# bosun-jobs.slice's MemoryHigh, the slice `bosun run` puts every queued job in and nothing else, so only Bosun's own
# builds are throttled; it restores the limit once Windows is more than the recovery band over the floor, as the panel's
# PressureWatch ends an episode. The panel's settings reach it as pressure-policy.json (src/Bosun.Core/Memory/
# PressurePolicy.cs); no file, or the switch off, is no action. A limit is only ever lowered, the original is kept in
# pressure-state-<distro>-u<uid>.json from before the first write until a read-back shows it restored, and what the throttle did or
# why it could not is in pressure-status-<distro>-u<uid>.json: each user manager of each distro has its own slice (round 8), so each has
# its own files in the shared Windows logs folder. Nothing here stops sampling.
#
# Ownership (round 9): bosun-jobs.slice is Bosun's own level, and an operator's own cap goes on its parent, bosun.slice,
# which Bosun never writes. cgroup v2 applies the tighter of a cgroup's limit and its ancestors', so a job is held by the
# operator's cap whatever Bosun does at its own level, and restoring bosun-jobs.slice can never remove it. A read followed
# by a write on one slice can't be made safe against someone writing the same slice (two systemctl calls), so the design
# keeps the operator off it; the checks below that hold a foreign value on bosun-jobs.slice are a safety net for an
# operator who writes there anyway, and the restore race on that slice is out of an operator's way by design.

POLICY_FILE = "pressure-policy.json"
HOST_FUTURE_S = 60  # the most a Windows sample may be dated ahead of this clock (skew), past which it is invalid


def state_file(distro, uid):
    return f"pressure-state-{safe_distro(distro)}-u{uid}.json"


def status_file(distro, uid):
    return f"pressure-status-{safe_distro(distro)}-u{uid}.json"
JOBS_SLICE = "bosun-jobs.slice"
PARENT_SLICE = "bosun.slice"  # the operator's level: never written here
HOST_STALE_S = 90  # three samples, as MemoryClient.StaleAfter
MIN_JOB_LIMIT_MB = 1024
MB = 1024 * 1024
INFINITY = "infinity"


class Failure(Exception):
    """A thing the throttle could not do, with why: said in pressure-status-<distro>-u<uid>.json, and sampling carries on."""


def read_small_json(path, limit=64 * 1024):
    """(document, None) for a file that parses, (None, None) for one that is not there, (None, why) for one that is not usable."""
    # Opened without blocking and checked on the descriptor (round 9): a FIFO at the path, with no writer, would block open()
    # for ever and stop the whole logger. Only a regular file is read, so a FIFO, a device or a directory is refused.
    try:
        fd = os.open(path, os.O_RDONLY | os.O_NONBLOCK | getattr(os, "O_NOFOLLOW", 0))
    except FileNotFoundError:
        return None, None
    except OSError as e:
        return None, f"{path} can't be read ({e.strerror})"
    try:
        if not stat.S_ISREG(os.fstat(fd).st_mode):
            return None, f"{path} is not a regular file"
        with os.fdopen(fd, encoding="utf-8", errors="replace", closefd=False) as f:
            text = f.read(limit + 1)
    except OSError as e:
        return None, f"{path} can't be read ({e.strerror})"
    finally:
        os.close(fd)
    if len(text) > limit:
        return None, f"{path} is larger than {limit // 1024} KB"
    try:
        doc = json.loads(text)
        # A file that is there and says null is a file that is not usable, never a file that is not there (round 12: a null
        # policy read as no policy and ended an operator's hold).
        return (doc, None) if doc is not None else (None, f"{path} holds null")
    except (ValueError, RecursionError):  # deeply nested brackets raise RecursionError, which is no JSON either
        return None, f"{path} is not JSON"


def whole_number(value):
    return isinstance(value, int) and not isinstance(value, bool)


def read_policy(log_dir):
    """(policy, why): the panel's pressure-policy.json as a dict (enabled, floor_mb, recovery_band_mb, job_limit_mb), or None
    with why when there is none to act on. A missing file is (None, None): no policy, no action. A file that is not a usable
    policy is also no action, and says why."""
    doc, why = read_small_json(os.path.join(log_dir, POLICY_FILE))
    if doc is None:
        return None, why
    if not isinstance(doc, dict):
        return None, f"{POLICY_FILE} is not a JSON object"
    if not whole_number(doc.get("schema_version")) or doc["schema_version"] != 1:
        return None, f"{POLICY_FILE} is of another version"
    if not isinstance(doc.get("enabled"), bool):
        return None, f"{POLICY_FILE}: enabled is not true or false"
    for key in ("floor_mb", "recovery_band_mb", "job_limit_mb"):
        if not whole_number(doc.get(key)) or doc[key] < 0:
            return None, f"{POLICY_FILE}: {key} is not a whole number of MB"
    if doc["floor_mb"] == 0:
        return None, f"{POLICY_FILE}: floor_mb is zero"
    if doc["job_limit_mb"] < MIN_JOB_LIMIT_MB:
        return None, f"{POLICY_FILE}: job_limit_mb is under the least allowed, {MIN_JOB_LIMIT_MB} MB"
    return {k: doc[k] for k in ("enabled", "floor_mb", "recovery_band_mb", "job_limit_mb")}, None


def read_host(log_dir, now):
    """(available_mb, why): Windows' available memory from current-windows.json. None, with why, when the sample is missing,
    unreadable, not a Windows snapshot, stale or impossible: such a sample decides nothing."""
    doc, why = read_small_json(os.path.join(log_dir, "current-windows.json"), 4 * 1024 * 1024)
    if doc is None:
        return None, why or "there is no Windows snapshot yet"
    # A whole number, never JSON true, which Python counts equal to 1 (round 2), as the policy reader checks.
    if not isinstance(doc, dict) or not whole_number(doc.get("schema_version")) or doc["schema_version"] != 1 or doc.get("side") != "windows":
        return None, "the Windows snapshot is not one this logger reads"
    try:
        at = datetime.datetime.strptime(doc.get("at"), "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc)
    except (TypeError, ValueError):
        return None, "the Windows snapshot has no valid time"
    memory = doc.get("memory")
    if not isinstance(memory, dict):
        return None, "the Windows snapshot has no memory figures"
    available, total = memory.get("available_mb"), memory.get("total_mb")
    if not whole_number(available) or not whole_number(total) or available < 0 or total <= 0 or available > total:
        return None, "the Windows snapshot's memory figures are not possible"
    age = (now - at).total_seconds()
    if age < -HOST_FUTURE_S:
        return None, "the Windows snapshot is dated in the future"
    if age > HOST_STALE_S:
        return None, "the Windows snapshot is stale"
    return available, None


def run_systemctl(args, timeout=15):
    """(return code, text) of `systemctl --user <args>`; no return code when it could not be run or timed out."""
    try:
        done = subprocess.run(["systemctl", "--user", *args], capture_output=True, text=True, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as e:
        return None, str(e)
    return done.returncode, (done.stdout if done.returncode == 0 else (done.stderr or done.stdout)).strip()


def limit_text(value):
    if value == INFINITY:
        return INFINITY
    return f"{value // MB} MB" if value % MB == 0 else f"{value} bytes"


# The throttle locks this process holds, by path: ownership is per process (a flock is per open file, so a second open in
# the same process would refuse itself), and a process that owns the throttle owns it for every Throttle it makes.
_OWNED = {}

# Where systemd keeps each user manager's runtime folder, /run/user/<uid>. A module setting, so a test can give its
# throttles a root of their own and never take the running service's lock.
RUN_USER = "/run/user"

# Where the kernel keeps its control groups. A module setting too, so a test can point a throttle at a folder of its own.
CGROUP_ROOT = "/sys/fs/cgroup"


class Throttle:
    """Lowers `unit`'s MemoryHigh under Windows pressure and restores it. `systemctl` is the hands on the slice, apart so a
    test can stand in for it; `unit` is bosun-jobs.slice, or a disposable slice in the integration test."""

    def __init__(self, log_dir, systemctl=run_systemctl, unit=JOBS_SLICE, distro=None, uid=None, lock_dir=None, cgroup_root=None):
        self.log_dir, self.systemctl, self.unit = log_dir, systemctl, unit
        self.cgroup_root = cgroup_root  # None: CGROUP_ROOT, read when used
        self.restore_why = None  # why the last restore_now returned "failed"
        # One owner per user manager and slice (round 10: two loggers of one user raced on one record and lost the
        # original). The owner holds an exclusive lock for its life, in the user's own runtime folder (local, never the
        # Windows log folder, where locks don't hold); a logger without it samples but never throttles or reports.
        # From the uid, never the environment (round 11: an unset XDG_RUNTIME_DIR sent a second logger of the same manager
        # to /tmp, a different lock): systemd keeps each user manager's runtime folder at /run/user/<uid>. Without it there
        # is no manager to own, and own() declines.
        self.lock_dir = lock_dir  # None: the manager's own folder under RUN_USER, from the uid, decided in own()
        self.lock_fd = None
        self.lock_fault = None       # why the lock could not be had at all, as opposed to another logger holding it
        self.last_lock_fault = None  # the last fault said, so the journal says each once
        # `systemctl --user` controls the slice of the user manager of this uid, so the record is this uid's too (round 8).
        self.uid = os.getuid() if uid is None else uid
        # The identity comes from the installer (BOSUN_DISTRO in the unit), then from WSL. With neither, or a name that is
        # not a safe file name, this logger has no identity and never throttles: a shared "default" record could be taken
        # for another distro's (D05 T02 §5, round 4).
        named = distro if distro is not None else (os.environ.get("BOSUN_DISTRO") or os.environ.get("WSL_DISTRO_NAME"))
        self.distro = named if named and re.fullmatch(r'[A-Za-z0-9._-]{1,64}', named) else None
        self.identity_fault = None if self.distro else (
            f"this logger has no distro identity ({'BOSUN_DISTRO or WSL_DISTRO_NAME is not a usable name' if named else 'neither BOSUN_DISTRO nor WSL_DISTRO_NAME is set'}), "
            "so the pressure throttle changes nothing: reinstall with wsl/install.sh")
        self.state_name, self.status_name = state_file(self.distro, self.uid), status_file(self.distro, self.uid)  # the status of a nameless logger is the "default" one
        if self.distro:
            self.state, self.operator, self.state_fault = self.safe_load_state()
        else:
            self.state, self.operator, self.state_fault = None, None, None
        self.last = None  # the last status, without its time
        self.delivered = None  # the last status whose file write landed: a failed write is retried next sample
        self.restore_written = False  # Bosun just put the original back and could not yet drop its record

    # -- the slice ---------------------------------------------------------------------------------------------------
    def read_limit(self):
        code, out = self.systemctl(["show", "-p", "MemoryHigh", "--value", self.unit])
        if code != 0:
            raise Failure(f"can't read {self.unit}'s MemoryHigh ({out or 'systemctl failed'}): no systemd user session, or its memory is not delegated")
        if out == INFINITY:
            return INFINITY
        if not out.isdigit():
            raise Failure(f"{self.unit}'s MemoryHigh reads {out!r}, which is not bytes or infinity")
        return int(out)

    def set_limit(self, value):
        code, out = self.systemctl(["set-property", "--runtime", self.unit, f"MemoryHigh={value}"])
        if code != 0:
            raise Failure(f"systemctl refused MemoryHigh={value} on {self.unit} ({out or 'no reason given'})")

    def cgroup_dir(self):
        """The kernel's control group of the slice under this user's manager: bosun-jobs.slice is bosun.slice/bosun-jobs.slice."""
        stem = self.unit[:-len(".slice")] if self.unit.endswith(".slice") else self.unit
        parts = stem.split("-")
        chain = ["-".join(parts[:i + 1]) + ".slice" for i in range(len(parts))]
        return os.path.join(self.cgroup_root or CGROUP_ROOT, "user.slice", f"user-{self.uid}.slice", f"user@{self.uid}.service", *chain)

    def enforcement(self, applied):
        """(kind, why): "enforced" when the slice's cgroup memory.high holds `applied`; "next" when no cgroup exists yet (no job
        has run: prepared for the next job, not verified on a running one); "not" with why when the kernel does not enforce it."""
        folder = self.cgroup_dir()
        if not os.path.isdir(folder):
            return "next", None
        path = os.path.join(folder, "memory.high")
        try:
            with open(path, encoding="ascii") as f:
                kernel = f.read().strip()
        except FileNotFoundError:
            return "not", f"{folder} has no memory.high, so the memory controller is not enabled for it (a parent slice does not delegate it)"
        except (OSError, UnicodeDecodeError) as e:
            return "not", f"{path} can't be read ({getattr(e, 'strerror', None) or e})"
        if kernel != str(applied):
            return "not", f"the kernel's memory.high reads {kernel}, not {applied}"
        return "enforced", None

    def enforced_report(self, applied, enforced_text, prepared_text):
        """The status of a slice Bosun holds at `applied`: lowered only when the kernel is shown to enforce it."""
        kind, why = self.enforcement(applied)
        if kind == "enforced":
            return self.report("lowered", enforced_text)
        if kind == "next":
            return self.report("lowered", prepared_text)
        return self.report("not_enforced", f"{self.unit} at {limit_text(applied)}, set for future jobs but not enforced by the kernel now: {why}")

    # -- the record of what was changed ------------------------------------------------------------------------------
    def safe_load_state(self):
        try:
            return self.load_state()
        except Exception as e:  # nothing about the record may stop the service before it samples
            return None, None, f"{self.state_name} can't be read ({e})"

    def load_state(self):
        """(lowered, operator, fault): this distro's record. One that is not this logger's (another unit, distro or user) is a
        fault, never acted on."""
        doc, why = read_small_json(os.path.join(self.log_dir, self.state_name))
        if why:
            return None, None, why
        if doc is None:
            return None, None, None
        mine = isinstance(doc, dict) and doc.get("schema_version") == 1 and doc.get("distro") == self.distro and doc.get("uid") == self.uid
        lowered = doc.get("lowered") if mine else False
        operator = doc.get("operator") if mine else False
        if lowered is None and operator is None:
            return None, None, None
        # The writer's own invariants (round 7): a limit Bosun can set (at least MIN_JOB_LIMIT_MB) lowered from an original
        # above it (or infinity). A record that breaks them was never Bosun's and authorises no write.
        lowered_ok = lowered is None or (isinstance(lowered, dict) and lowered.get("unit") == self.unit and whole_number(lowered.get("applied"))
                                         and lowered["applied"] >= MIN_JOB_LIMIT_MB * MB
                                         and (lowered.get("original") == INFINITY
                                              or (whole_number(lowered.get("original")) and lowered["original"] > lowered["applied"]))
                                         and isinstance(lowered.get("unverified", False), bool)
                                         and (lowered.get("previous") is None
                                              or (whole_number(lowered["previous"]) and lowered["previous"] > lowered["applied"]
                                                  and (lowered["original"] == INFINITY or lowered["original"] > lowered["previous"]))))
        operator_ok = operator is None or (isinstance(operator, dict) and operator.get("unit") == self.unit
                                           and (operator.get("seen") == INFINITY or whole_number(operator.get("seen"))))
        if not (mine and lowered_ok and operator_ok):
            return None, None, f"{self.state_name} is not a record this logger wrote"
        return lowered, operator, None

    def save_state(self, lowered, operator=None):
        text = json.dumps({"schema_version": 1, "distro": self.distro, "uid": self.uid, "lowered": lowered, "operator": operator}, separators=(",", ":")) + "\n"
        if not write_snapshot(self.log_dir, text, name=self.state_name):
            raise Failure(f"can't write {self.state_name}, so no limit is changed")
        self.state, self.operator = lowered, operator

    # -- the decision ------------------------------------------------------------------------------------------------
    def restore_now(self):
        """Puts the original back and clears the record, but only after a read shows the slice at Bosun's own value (round 8:
        a write before knowing who owns the current value could take an operator's cap away). "done" once a read shows the
        original; "failed" when it can't be read or put back yet (the record stays); "held" when the slice reads something
        that is neither, which is the operator's: the record becomes a hold and nothing is written."""
        original, applied = self.state["original"], self.state["applied"]
        ours = (applied, self.state.get("previous"))  # a tightening not yet confirmed may have left either
        self.restore_why = None
        try:
            current = self.read_limit()
        except Failure as e:
            self.restore_why = str(e)
            return "failed"
        if current != original:
            if current not in ours:
                self.save_state(None, {"unit": self.unit, "seen": current})
                return "held"
            refused = None
            try:
                self.set_limit(original)
            except Failure as e:
                refused = str(e)  # the read below says whether anything changed
            try:
                back = self.read_limit()
            except Failure as e:
                self.restore_why = refused or str(e)
                return "failed"
            if back != original:
                self.restore_why = refused or f"{self.unit} reads {limit_text(back)} after the write, not {limit_text(original)}"
                return "failed"
        try:
            self.save_state(None)
        except Failure:
            self.restore_written = True  # the limit is back; the stale record is dropped when the next sample reads the original
        return "done"

    def held_report(self):
        seen = limit_text(self.operator["seen"])
        return self.report("left_alone", f"{self.unit} reads {seen}, neither what Bosun set nor the original: someone changed it, so Bosun left it and will not lower it again until this pressure episode ends")

    def restore(self, why):
        original = self.state["original"]
        done = self.restore_now()
        if done == "done":
            return self.report("restored", f"{self.unit} is back at {limit_text(original)}: {why}")
        if done == "held":
            return self.held_report()
        return self.report("error", f"{self.unit} could not be put back at {limit_text(original)}: {self.restore_why or 'the slice could not be read'} ({why}); the original is kept and each sample tries again")

    def lower(self, policy, total_mb):
        limit = policy["job_limit_mb"] * MB
        if total_mb is None:
            return self.report("error", "the guest's total memory can't be read, so the job limit can't be checked against it and nothing is lowered; the next sample tries again")
        if policy["job_limit_mb"] > total_mb:
            return self.report("error", f"the job limit {policy['job_limit_mb']} MB is over the guest's total memory, {total_mb} MB, so nothing is lowered")
        current = self.read_limit()
        if current != INFINITY and current <= limit:
            return self.report("idle", f"{self.unit} is already at or under the policy's limit, so it is left alone")
        # The original is on disk before the first write, so a crash between the two can't lose it. Unverified until the
        # read-back confirms the write (round 13): a logger that dies before writing leaves a record whose slice still reads
        # the original, which the next start takes as a write that did not happen, never as an operator's change.
        self.save_state({"unit": self.unit, "original": current, "applied": limit, "unverified": True})
        # Read again just before the write (round 7: an operator's limit set while the record was being saved was written
        # over, and lost on recovery). A change since the first read is the operator's: held, never written over. systemd
        # has no compare-and-set for a property, so this narrows the window to the write itself, the least it can be.
        try:
            again = self.read_limit()
        except Failure as e:
            self.save_state(None)
            return self.report("error", f"{self.unit} could not be read again before the write ({e}), so nothing is lowered")
        if again != current:
            self.save_state(None, {"unit": self.unit, "seen": again})
            return self.report("left_alone", f"{self.unit} changed to {limit_text(again)} while Bosun was about to lower it: someone else set it, so Bosun left it and will not lower it again until this pressure episode ends")
        try:
            self.set_limit(limit)
        except Failure as e:
            return self.abort_lowering(str(e))
        try:
            got = self.read_limit()
        except Failure as e:
            # Who owns the value now is not known (round 8): nothing is written. The record stays, unverified, and the next
            # sample reads the slice first.
            try:
                self.save_state(dict(self.state, unverified=True))
            except Failure:
                pass
            return self.report("error", f"{e}; the lowering to {limit_text(limit)} is unverified, so nothing is written until the next sample reads the slice")
        if got != limit and got != current:
            # Neither what Bosun just wrote nor the original: someone else set it between the write and the read, so it is the
            # operator's. Not rolled back; held off for the rest of this episode, as decide_lowered does for a third value.
            self.save_state(None, {"unit": self.unit, "seen": got})
            return self.report("left_alone", f"{self.unit} reads {limit_text(got)} after Bosun wrote {limit_text(limit)}: someone else changed it, so Bosun left it and will not lower it again until this pressure episode ends")
        if got != limit:
            return self.abort_lowering(f"{self.unit} reads {limit_text(got)} after the write, not {limit_text(limit)}")
        try:
            self.save_state(dict(self.state, unverified=False))
        except Failure as e:
            # The slice is lowered and read back; only the mark could not be saved. The record still holds the original, and
            # the next sample reads the slice at Bosun's value and confirms it.
            return self.report("error", f"{self.unit} lowered to {limit_text(limit)} and read back, but its record could not be marked verified ({e}); the next sample confirms it")
        tail = f"Windows is under its floor (your own cap belongs on {PARENT_SLICE}, which Bosun never writes)"
        return self.enforced_report(limit, f"{self.unit} lowered from {limit_text(current)} to {limit_text(limit)}: {tail}",
                                    f"{self.unit} set to {limit_text(limit)} for the next job (none is running in it yet, so the kernel's limit is not yet verified): {tail}")

    def abort_lowering(self, why):
        done = self.restore_now()
        if done == "done":
            return self.report("error", f"{why}; {self.unit} is as it was")
        if done == "held":
            return self.held_report()
        # Not verified back: kept as ours, so the next sample reads the slice first; any other value is an operator's.
        try:
            self.save_state(dict(self.state, unverified=True))
        except Failure:
            pass
        return self.report("error", f"{why}; the original could not be put back yet and is kept, and each sample tries again")

    def step(self, policy, policy_why, host_mb, host_why, total_mb):
        """One sample's decision; always returns (and writes) the status, whatever fails."""
        try:
            return self.decide(policy, policy_why, host_mb, host_why, total_mb)
        except Failure as e:
            return self.report("error", str(e))

    def decide(self, policy, policy_why, host_mb, host_why, total_mb):
        if self.identity_fault:
            return self.report("error", self.identity_fault)
        # The record is re-read from disk for every decision, never trusted from memory: another process may have written it
        # (or dropped it), and a fault is retried each sample too, as a record unreadable once is not unreadable for good.
        self.state, self.operator, self.state_fault = self.safe_load_state()
        if self.state_fault:
            return self.report("error", f"{self.state_fault}, so nothing is changed until it is fixed or removed")
        on = policy is not None and policy["enabled"]
        if self.operator is not None:
            return self.decide_operator(policy, policy_why, on, host_mb)
        if self.state is not None:
            return self.decide_lowered(policy, policy_why, on, host_mb, host_why, total_mb)
        if not on:
            # A policy file that is there and unusable is a fault, said even before any status file exists; an absent or
            # disabled one stays quiet.
            return self.report("off", policy_why or ("automatic action is off" if policy is not None else "there is no policy file"), fault=policy_why is not None)
        if host_mb is None:
            return self.report("idle", host_why)
        if host_mb < policy["floor_mb"]:
            return self.lower(policy, total_mb)
        return self.report("idle", "Windows has enough memory")

    def stop(self):
        """The service is stopping: a limit Bosun lowered, and still holds as it set it, is put back now (round 6: a stopped
        service left a running job throttled until another logger started). A limit an operator took over, or none, is
        left as it is."""
        if self.identity_fault or self.lock_fd is None:
            return None  # a logger that never owned the throttle restores nothing
        self.state, self.operator, self.state_fault = self.safe_load_state()
        if self.state_fault or self.state is None:
            return None
        try:
            current = self.read_limit()
        except Failure as e:
            return self.report("error", f"the logger is stopping and could not read {self.unit}: {e}; the original is kept for the next start")
        if current not in (self.state["applied"], self.state.get("previous")):
            return None  # not Bosun's value any more: the next start decides, as for any sample
        return self.restore("the logger is stopping")

    def decide_lowered(self, policy, policy_why, on, host_mb, host_why, total_mb=None):
        state = self.state
        current = self.read_limit()
        if state.get("previous") is not None and current == state["previous"]:
            # A tightening that did not take (the write failed, or its read-back did): the slice is at the earlier value of
            # Bosun's, so the record goes back to it and the tightening is tried again below.
            self.save_state(self.settled(state, state["previous"]))
            state = self.state
        if current == state["original"] and state.get("unverified"):
            # The unverified lowering did not take: nothing of Bosun's is on the slice, and the next sample decides afresh.
            self.save_state(None)
            return self.report("idle", f"{self.unit} reads {limit_text(current)}: the earlier lowering did not take, so its record is dropped")
        if current == state["original"]:
            episode = on and not (host_mb is not None and host_mb > policy["floor_mb"] + policy["recovery_band_mb"])
            if episode and not self.restore_written:
                # The original read back while Windows is still under pressure and Bosun did not just write it: the operator
                # put it back (or WSL did), and it is theirs for the rest of this episode, as for a third value (round 4).
                self.save_state(None, {"unit": self.unit, "seen": current})
                return self.report("left_alone", f"{self.unit} reads {limit_text(current)}, the original, though Bosun had lowered it: someone put it back, so Bosun left it and will not lower it again until this pressure episode ends")
            # Bosun's own restore whose record was not dropped, or the episode is over: nothing to restore, record dropped.
            self.restore_written = False
            self.save_state(None)
            return self.report("restored", f"{self.unit} is already back at {limit_text(current)}")
        if current != state["applied"]:
            # The slice reads neither Bosun's value nor the original: an operator took it over, and it is theirs for the rest of this pressure episode.
            self.save_state(None, {"unit": self.unit, "seen": current})
            return self.report("left_alone", f"{self.unit} reads {limit_text(current)}, neither what Bosun set ({limit_text(state['applied'])}) nor the original ({limit_text(state['original'])}): someone changed it, so Bosun left it and will not lower it again until this pressure episode ends")
        if state.get("unverified"):
            # It reads Bosun's own value: the lowering took. Carry on as for any lowered slice.
            self.save_state(self.settled(state, state["applied"]))
            state = self.state
        if not on:
            return self.restore(policy_why or ("automatic action was turned off" if policy is not None else "the policy file is gone"))
        if host_mb is not None and host_mb > policy["floor_mb"] + policy["recovery_band_mb"]:
            return self.restore("Windows has recovered")
        if host_mb is not None and policy["job_limit_mb"] * MB < state["applied"]:
            return self.tighten(policy, total_mb)
        text = f"{self.unit} stays at {limit_text(state['applied'])}" + ("" if host_mb is not None else f": {host_why}") + f" (your own cap belongs on {PARENT_SLICE})"
        return self.enforced_report(state["applied"], text, text + ": no job is running in it yet, so the kernel's limit is not yet verified")

    @staticmethod
    def settled(state, applied):
        """The record as verified at `applied`, with no tightening pending."""
        return {k: v for k, v in dict(state, applied=applied, unverified=False).items() if k != "previous"}

    def tighten(self, policy, total_mb):
        """The policy's job limit is now lower than the one Bosun holds, in the same episode: the tighter limit is applied (only
        ever lowering) and read back, and the record keeps the ORIGINAL for the restore."""
        state = self.state
        limit, old = policy["job_limit_mb"] * MB, state["applied"]
        if total_mb is None:
            return self.report("error", f"the guest's total memory can't be read, so the tighter job limit can't be checked against it and {self.unit} stays at {limit_text(old)}; the next sample tries again")
        if policy["job_limit_mb"] > total_mb:
            return self.report("error", f"the job limit {policy['job_limit_mb']} MB is over the guest's total memory, {total_mb} MB, so {self.unit} stays at {limit_text(old)}")
        # On disk first, with the earlier value beside it, so a crash after the write is still Bosun's own value to read back.
        self.save_state(dict(state, applied=limit, unverified=True, previous=old))
        why = None
        try:
            self.set_limit(limit)
        except Failure as e:
            # Refused: nothing was written, so the slice is still at the earlier value.
            self.save_state(self.settled(self.state, old))
            return self.report("error", f"{e}; {self.unit} stays at {limit_text(old)} and the next sample tries again")
        try:
            got = self.read_limit()
        except Failure as e:
            got, why = None, str(e)
        if got == limit:
            self.save_state(self.settled(self.state, limit))
            tail = f"Windows is still under its floor and the policy's job limit is lower (your own cap belongs on {PARENT_SLICE})"
            return self.enforced_report(limit, f"{self.unit} tightened from {limit_text(old)} to {limit_text(limit)}: {tail}",
                                        f"{self.unit} tightened to {limit_text(limit)} for the next job (none is running in it yet, so the kernel's limit is not yet verified): {tail}")
        if got is not None and got != old:
            self.save_state(None, {"unit": self.unit, "seen": got})
            return self.report("left_alone", f"{self.unit} reads {limit_text(got)} after Bosun wrote {limit_text(limit)}: someone else changed it, so Bosun left it and will not lower it again until this pressure episode ends")
        # Failed, or read back at the earlier value: the earlier value stays Bosun's. (A failed read-back leaves the record
        # unverified with `previous`, and the next sample reads the slice and settles it.)
        if got == old:
            self.save_state(self.settled(self.state, old))
        return self.report("error", f"{why or f'{self.unit} reads {limit_text(got)} after the write, not {limit_text(limit)}'}; {self.unit} stays at {limit_text(old)} and the next sample tries again")

    def decide_operator(self, policy, policy_why, on, host_mb):
        """An operator took the slice over: nothing is touched until the episode ends (Windows over the floor plus the band) or
        the switch goes off, and then the take-over is forgotten so Bosun may act again, only ever lowering."""
        seen = limit_text(self.operator["seen"])
        if policy is None and policy_why is not None:
            # The policy could not be read this sample (round 11): that ends nothing. The hold stays until a policy read off,
            # the file gone, or Windows recovered.
            return self.report("left_alone", f"{self.unit} was set to {seen} by someone else and stays left alone; the policy could not be read ({policy_why})")
        if not on:
            self.save_state(None)
            return self.report("off", policy_why or ("automatic action is off" if policy is not None else "there is no policy file"), fault=policy_why is not None)
        if host_mb is not None and host_mb > policy["floor_mb"] + policy["recovery_band_mb"]:
            self.save_state(None)
            return self.report("idle", f"Windows has recovered; the operator's {seen} on {self.unit} is no longer held off")
        return self.report("left_alone", f"{self.unit} was set to {seen} by someone else: left alone for the rest of this pressure episode")

    # -- what it says ------------------------------------------------------------------------------------------------
    def report(self, state, detail, fault=False):
        doc = {"schema_version": 1, "state": state, "detail": detail, "unit": self.unit, "distro": self.distro, "uid": self.uid,
               "original": limit_text(self.state["original"]) if self.state else None,
               "applied": limit_text(self.state["applied"]) if self.state else None}
        if doc != self.last and (self.last is None or doc["state"] != self.last["state"] or state == "error"):
            print(f"[bosun] pressure throttle {state}: {detail}", file=sys.stderr)
        quiet = state == "off" and not fault and not os.path.exists(os.path.join(self.log_dir, self.status_name))  # a machine that never opted in gets no file
        if quiet:
            self.delivered = doc
        elif doc != self.delivered or state in ("lowered", "error"):
            stamped = dict(doc, at=datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))
            if write_snapshot(self.log_dir, json.dumps(stamped, separators=(",", ":")) + "\n", name=self.status_name):
                self.delivered = doc  # only a write that landed counts as said
        self.last = doc
        return doc

    def own(self):
        """True while this logger holds the throttle's lock for its user and slice, taken on first use and kept."""
        if self.lock_fd is not None:
            return True
        if self.lock_dir is None:
            self.lock_dir = os.path.join(RUN_USER, str(self.uid))
        path = os.path.join(self.lock_dir, f"bosun-throttle-u{self.uid}-{self.unit}.lock")
        if path in _OWNED:
            self.lock_fd = _OWNED[path]
            return True
        self.lock_fault = None
        try:
            fd = os.open(path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        except OSError as e:
            # Not contention: the lock could not be had at all (no space, no folder, no access). Said, never taken for another
            # logger owning it (round 12).
            self.lock_fault = f"the throttle's lock {path} can't be opened ({e.strerror}), so the pressure throttle is off"
            return False
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            os.close(fd)
            return False  # another logger of this user holds it: it owns the throttle
        except OSError as e:
            os.close(fd)
            self.lock_fault = f"the throttle's lock {path} can't be taken ({e.strerror}), so the pressure throttle is off"
            return False
        self.lock_fd = _OWNED[path] = fd
        return True

    def run_once(self, now=None):
        if not self.own():
            if self.lock_fault:
                # No logger could hold a lock that can't be opened, so no owner's status is overwritten by saying so.
                if self.lock_fault != self.last_lock_fault:
                    print(f"[bosun] {self.lock_fault}", file=sys.stderr)
                    self.last_lock_fault = self.lock_fault
                return self.report("error", self.lock_fault)
            # Another logger of this user owns the throttle: this one changes nothing and writes no status, so the owner's
            # record and status are never touched by a second writer.
            return {"state": "not_owner", "detail": "another Bosun logger of this user owns the pressure throttle"}
        now = now or datetime.datetime.now(datetime.timezone.utc)
        policy, policy_why = read_policy(self.log_dir)
        host_mb, host_why = read_host(self.log_dir, now)
        try:
            total_mb = meminfo()["MemTotal"] // 1024
        except (OSError, KeyError, ValueError):
            total_mb = None
        return self.step(policy, policy_why, host_mb, host_why, total_mb)


def main(argv):
    if len(argv) >= 2 and argv[1] == "instances":
        doc = instances()
        sys.stdout.write(json.dumps(doc, separators=(",", ":"), ensure_ascii=False) + "\n" if "--json" in argv else instances_text(doc))
        return 0
    if len(argv) < 2:
        print(__doc__, file=sys.stderr)
        return 64
    given = argv[1]
    interval = int(argv[argv.index("--interval") + 1]) if "--interval" in argv else 30
    once = "--once" in argv
    # The Windows log folder is found through the drive mounts on each sample, so moving them can't strand the reports
    # (D03 T02 §6); a guest path, as an older install gives, is used as it is.
    # --mountinfo names another mount table, for tests.
    mountinfo = argv[argv.index("--mountinfo") + 1] if "--mountinfo" in argv else "/proc/self/mountinfo"
    log_dir, why = log_folder(given, mountinfo)
    while log_dir is None:
        print(f"[bosun] {why}", file=sys.stderr)
        if once:
            return 1
        time.sleep(interval)
        log_dir, why = log_folder(given, mountinfo)
    os.makedirs(log_dir, exist_ok=True)
    cache = {}
    last_clear = time.monotonic()
    throttle = Throttle(log_dir)
    if not once:
        # systemd stops the service with SIGTERM: it ends the loop through SystemExit, so the finally below restores.
        signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    try:
        while True:
            found, why = log_folder(given, mountinfo)
            if found is None:
                # The folder can't be found now: nothing is written, and the throttle reads nothing, until it can be,
                # never to the last folder found, which may now be another drive's (round 1).
                print(f"[bosun] {why}; this sample is skipped", file=sys.stderr)
                if once:
                    return 1
                time.sleep(interval)
                continue
            if found != log_dir:
                log_dir = throttle.log_dir = found
                os.makedirs(log_dir, exist_ok=True)
            sample(log_dir, cache, interval)
            if not once:  # a one-shot run never throttles: only the service owns the slice and its record
                try:
                    throttle.run_once()
                except Exception as e:  # nothing the throttle does may stop the sampling
                    print(f"[bosun] pressure throttle failed: {e}", file=sys.stderr)
            if once:
                return 0
            if time.monotonic() - last_clear > 3600:
                cache.clear()  # Repos get created and deleted during long runs.
                last_clear = time.monotonic()
            time.sleep(interval)
    finally:
        if not once:
            try:
                throttle.stop()
            except Exception as e:  # the stop's own failure is said; the original stays recorded for the next start
                print(f"[bosun] pressure throttle could not restore on stop: {e}", file=sys.stderr)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
