#!/usr/bin/env python3
"""One heartbeat for every repo: a scheduled task ticks each registered repo's run guard.

    bosun-heartbeat add <path> [--distro <name>]   # move a repo on; its own watchdog task is removed
    bosun-heartbeat remove <path>                  # move it off; its own watchdog task comes back
    bosun-heartbeat list [--json]
    bosun-heartbeat status [--json]
    bosun-heartbeat tick                           # one pass, as the `Bosun heartbeat` task runs it

Each repo keeps its run state, rules and hooks. This only owns the schedule, and it can see what a repo can't: memory
across all of them. So one pass resumes at most one repo and carries on from the next one next time, and a pass is held
while memory is short, for at most an hour, because pressure should delay a restart and never block it for good. A tick
never starts a stopped WSL distro. State lives in %LOCALAPPDATA%\\Bosun\\heartbeat, written by temporary file then
replace, so a crash can't lose it, and a handoff a crash interrupted is finished by the next tick. Stdlib only.
"""
import argparse
from datetime import datetime, timezone
import html
import json
import os
import posixpath
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

SCHEMA = 1
TASK = "Bosun heartbeat"
TICK_SECONDS = 120
HOLD_SECONDS = 3600
STALE_SECONDS = 15 * 60
DEFAULT_FLOOR_MB = 2048
LOCK_WAIT_SECONDS = 30
RESUMED = re.compile(r"^resumed, attempt \d+\s*$")
MAX_NOTE = 300


class Refused(Exception):
    """The operator's request can't be done, and nothing changed."""


class Busy(Exception):
    """Another heartbeat process holds the lock."""


def now_iso(seconds):
    return datetime.fromtimestamp(seconds, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def home():
    return Path(os.environ["LOCALAPPDATA"]) / "Bosun"


class World:
    """Everything outside this process, so tests can stand in for it."""

    def now(self):
        return time.time()

    def sleep(self, seconds):
        time.sleep(seconds)

    def available_mb(self):
        import ctypes

        class Status(ctypes.Structure):
            _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong)] + [
                (name, ctypes.c_ulonglong) for name in ("total", "avail", "page_total", "page_avail", "virt_total", "virt_avail", "ext")]
        status = Status()
        status.length = ctypes.sizeof(Status)
        if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            raise OSError("GlobalMemoryStatusEx failed")
        return status.avail // (1024 * 1024)

    def run(self, argv, cwd=None, timeout=TICK_SECONDS):
        """(exit code, output), or (None, why) when it didn't finish. Output goes to a file, not a pipe, because a guard
        that resumes a session leaves a child behind, and a pipe would wait for that child too."""
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        with tempfile.TemporaryFile() as out:
            try:
                child = subprocess.Popen(argv, cwd=cwd, stdin=subprocess.DEVNULL, stdout=out, stderr=subprocess.STDOUT, creationflags=flags)
            except OSError as exc:
                return None, f"could not start {argv[0]}: {exc}"
            try:
                code = child.wait(timeout)
            except subprocess.TimeoutExpired:
                if os.name == "nt":
                    subprocess.run(["taskkill", "/F", "/T", "/PID", str(child.pid)], capture_output=True, creationflags=flags)
                else:
                    child.kill()
                child.wait()
                return None, f"stopped after {timeout} seconds"
            out.seek(0)
            data = out.read()
        # wsl.exe answers in UTF-16, which reads as text with NUL bytes between the letters.
        return code, data.decode("utf-8", "replace").replace("\0", "")

    def python(self):
        """python.exe, even when this runs under pythonw.exe, so a child's output can be read."""
        current = Path(sys.executable)
        beside = current.with_name("python.exe")
        return str(beside) if os.name == "nt" and beside.exists() else str(current)

    def is_dir(self, path):
        return Path(path).is_dir()

    def is_file(self, path):
        return Path(path).is_file()


def write_atomic(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + f".{os.getpid()}.tmp")
    temporary.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


class Lock:
    """Held across a tick or a registry change. The operating system releases it if the process dies."""

    def __init__(self, world, wait=0):
        self.world, self.wait, self.file = world, wait, None

    def try_lock(self):
        if os.name == "nt":
            import msvcrt
            self.file.seek(0)
            msvcrt.locking(self.file.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(self.file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)

    def __enter__(self):
        path = home() / "heartbeat" / "lock"
        path.parent.mkdir(parents=True, exist_ok=True)
        self.file = open(path, "a+b")
        deadline = self.world.now() + self.wait
        while True:
            try:
                self.try_lock()
                return self
            except OSError:
                if self.world.now() >= deadline:
                    self.file.close()
                    raise Busy("another heartbeat process is running") from None
                self.world.sleep(1)

    def __exit__(self, *_):
        self.file.close()  # closing releases it


def repos_path():
    return home() / "heartbeat" / "repos.json"


def status_path():
    return home() / "heartbeat" / "status.json"


def load_repos():
    """The registry. Raises ValueError when it can't be read, because guessing could tick the wrong repos."""
    try:
        data = json.loads(repos_path().read_text(encoding="utf-8"))
    except FileNotFoundError:
        return []
    except OSError as exc:
        raise ValueError(f"repos.json can't be read: {exc}") from None
    except ValueError:
        raise ValueError("repos.json is not valid JSON") from None
    repos = data.get("repos") if isinstance(data, dict) else None
    if not isinstance(repos, list) or not all(isinstance(r, dict) and isinstance(r.get("path"), str) for r in repos):
        raise ValueError("repos.json has the wrong shape")
    return repos


def save_repos(repos):
    write_atomic(repos_path(), {"schema_version": SCHEMA, "repos": repos})


def load_status():
    """(status, problem). No file is a first run, with no problem; one that can't be read or parsed is history that's
    unavailable, which is said, never shown as a first run."""
    try:
        data = json.loads(status_path().read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}, None
    except OSError as exc:
        return {}, f"status.json can't be read: {exc}"
    except ValueError:
        return {}, "status.json is not valid JSON"
    if not isinstance(data, dict):
        return {}, "status.json has the wrong shape"
    return data, None


def save_status(status):
    """Write the status, and say so instead of stopping when the disk refuses: supervision matters more than history."""
    try:
        write_atomic(status_path(), status)
    except OSError as exc:
        print(f"status not saved: {exc}")


def floor_mb():
    """The memory floor from config.json, and a warning if the value there can't be used."""
    path = home() / "config.json"
    try:
        value = json.loads(path.read_text(encoding="utf-8")).get("heartbeat_floor_mb", DEFAULT_FLOOR_MB)
    except FileNotFoundError:
        return DEFAULT_FLOOR_MB, None
    except (OSError, ValueError, AttributeError) as exc:
        return DEFAULT_FLOOR_MB, f"config.json can't be read ({exc}); using {DEFAULT_FLOOR_MB} MB"
    if type(value) is not int or value < 0:
        return DEFAULT_FLOOR_MB, f"heartbeat_floor_mb must be a whole number of MB; using {DEFAULT_FLOOR_MB} MB"
    return value, None


def same_path(a, b):
    return os.path.normcase(os.path.normpath(a)) == os.path.normcase(os.path.normpath(b))


def repo_key(repo):
    """One repo's identity. A WSL repo is its distro and its path, compared as Linux does, case and all; a Windows repo
    is its path, compared as Windows does."""
    if repo.get("distro"):
        return f"wsl:{repo['distro']}:{posixpath.normpath(repo['path'])}"
    return os.path.normcase(os.path.normpath(repo["path"]))


def same_repo(repo, path, distro):
    return repo_key(repo) == repo_key({"path": path, "distro": distro})


def running_distros(world):
    code, out = world.run(["wsl.exe", "--list", "--running", "--quiet"], timeout=30)
    return {line.strip() for line in out.splitlines() if line.strip()} if code == 0 else set()


def own_task(repo):
    return f"{Path(repo['path']).name} run watchdog"


def own_task_owner(world, repo):
    """"ours", "other" (a task of this name that runs another folder: another clone of the same name), "absent", or None
    when Windows wouldn't say. `schtasks` exits 1 for an absent task and for other errors."""
    code, out = world.run(["schtasks", "/Query", "/TN", own_task(repo), "/XML"], timeout=30)
    if code == 0:
        ours = os.path.normcase(os.path.normpath(repo["path"])) in os.path.normcase(out.replace("/", os.sep))
        return "ours" if ours else "other"
    if code is not None and "cannot find" in out.lower():
        return "absent"
    return None


def own_task_exists(world, repo):
    """True or False, and None when Windows wouldn't say. Another clone's task of the same name isn't this repo's."""
    owner = own_task_owner(world, repo)
    return None if owner is None else owner == "ours"


def guard(world, repo, command):
    return world.run([world.python(), str(Path(repo["path"]) / "scripts" / "run-guard.py"), command], cwd=repo["path"])


def remove_own_task(world, repo):
    """None when the repo has no own task afterwards, or why it still does."""
    if repo.get("distro"):
        return None
    exists = own_task_exists(world, repo)
    if exists is False:
        return None
    if exists is None:
        return f"could not check {own_task(repo)}"
    code, out = guard(world, repo, "uninstall")
    return None if code == 0 else f"could not remove {own_task(repo)}: {out.strip()[:MAX_NOTE]}"


def restore_own_task(world, repo):
    """None when the repo has its own task again, or why it doesn't. The guard's installer replaces any task of the
    same name, so one belonging to another clone is left alone and named."""
    if repo.get("distro"):
        return None
    owner = own_task_owner(world, repo)
    if owner == "ours":
        return None
    if owner is None:
        return f"could not check {own_task(repo)}"
    if owner == "other":
        return (f"{own_task(repo)} belongs to another clone with the same folder name, so it wasn't replaced: rename "
                f"this folder, or remove that clone's task, then try again")
    code, out = guard(world, repo, "install")
    return None if code == 0 else f"could not restore {own_task(repo)}: {out.strip()[:MAX_NOTE]}"


def require_heartbeat_task(world):
    """Taking a repo off its own watchdog is only safe when the heartbeat that replaces it will really fire."""
    task = task_health(world)
    if task["state"] != "installed":
        raise Refused(f"the {TASK} task is {task['state']}, so this repo would have no watchdog: run scripts/install.ps1, then add the repo")


def add(world, path, distro=None, allow_no_task=False):
    repo = {"path": path if distro else str(Path(path).resolve()), "state": "adding"}
    if not allow_no_task:
        require_heartbeat_task(world)
    if distro:
        repo["distro"] = distro
        if distro not in running_distros(world):
            raise Refused(f"WSL distro {distro} isn't running, and this won't start it: start it, then add the repo")
        code, _ = world.run(["wsl.exe", "-d", distro, "--cd", path, "--", "test", "-f", "scripts/run-guard.py"], timeout=30)
        if code != 0:
            raise Refused(f"{path} in {distro} has no scripts/run-guard.py: copy the run guard from a repo that has it, such as Steward")
    else:
        if not world.is_dir(repo["path"]):
            raise Refused(f"no such folder: {repo['path']}")
        if not world.is_file(Path(repo["path"]) / "scripts" / "run-guard.py"):
            raise Refused(f"{repo['path']} has no scripts/run-guard.py: copy the run guard from a repo that has it, such as Steward")
    with Lock(world, LOCK_WAIT_SECONDS):
        repos = load_repos()
        if any(same_repo(r, repo["path"], distro) for r in repos):
            raise Refused(f"already on the heartbeat: {repo['path']}")
        save_repos(repos + [repo])
        problem = remove_own_task(world, repo)
        if problem:
            save_repos(repos)  # its own watchdog is still in place, so nothing is lost
            raise Refused(problem)
        repo["state"] = "active"
        save_repos(repos + [repo])
    return repo


def remove(world, path, distro=None):
    with Lock(world, LOCK_WAIT_SECONDS):
        repos = load_repos()
        wanted = path if distro else str(Path(path).resolve())
        matches = [r for r in repos if same_repo(r, wanted, distro)]
        if not matches:
            raise Refused(f"not on the heartbeat: {wanted}")
        repo = matches[0]
        repo["state"] = "removing"
        save_repos(repos)
        problem = restore_own_task(world, repo)
        if problem:
            repo["state"] = "active"
            save_repos(repos)
            raise Refused(problem)
        save_repos([r for r in repos if r is not repo])
    return repo


def reconcile(world, repos):
    """Finish any handoff a crash interrupted, so a repo is never left with no watchdog. Returns notes by path."""
    notes = {}
    for repo in list(repos):
        state = repo.get("state")
        if state == "removing":
            problem = restore_own_task(world, repo)
            if problem:
                notes[repo_key(repo)] = problem
            else:
                repos.remove(repo)
        else:
            exists = own_task_exists(world, repo) if not repo.get("distro") else False
            if exists:
                problem = remove_own_task(world, repo)
                notes[repo_key(repo)] = problem or f"removed {own_task(repo)}, which had come back"
            elif exists is None:
                notes[repo_key(repo)] = f"could not check {own_task(repo)}"
            if state == "adding" and exists is not None and not (exists and problem):
                repo["state"] = "active"
    return notes


def tick_repo(world, repo):
    """(result, error) for one repo. The distro is checked right before its tick, because an earlier repo's guard can
    take minutes, and a stale answer would start a distro that has stopped since."""
    distro = repo.get("distro")
    if distro:
        if distro not in running_distros(world):
            return "WSL not running", None
        code, out = world.run(["wsl.exe", "-d", distro, "--cd", repo["path"], "--", "python3", "scripts/run-guard.py", "watch"])
    else:
        if not world.is_dir(repo["path"]):
            return None, "folder is missing"
        code, out = guard(world, repo, "watch")
    text = out.strip()
    if code == 0:
        return (text.splitlines()[-1] if text else "no answer")[:MAX_NOTE], None
    return None, (f"exit {code}: {text}" if code is not None else text)[:MAX_NOTE]


def tick(world):
    """One pass. Nothing it meets may stop the next one, so every problem is written to the status instead."""
    try:
        with Lock(world):
            return run_pass(world)
    except Busy:
        print("busy: another heartbeat process is running")
        return 0
    except OSError as exc:  # the lock file itself can't be made, so one pass at a time can't be promised
        print(f"nothing ticked: the heartbeat's lock can't be taken: {exc}")
        return 0


def run_pass(world):
    status, history = load_status()
    now = world.now()
    status.update(schema_version=SCHEMA, last_pass=now_iso(now), hold=None, error=None)
    if history:
        print(f"starting the history afresh: {history}")
    try:
        repos = load_repos()
    except ValueError as exc:
        status["error"] = str(exc)
        save_status(status)
        print(f"nothing ticked: {exc}")
        return 0
    before = json.dumps(repos, sort_keys=True)
    notes = reconcile(world, repos)
    if json.dumps(repos, sort_keys=True) != before:  # an unchanged registry needs no write, so a full disk can't stop it
        try:
            save_repos(repos)
        except OSError as exc:
            status["error"] = f"repos.json can't be saved, so the handoffs above are finished again next pass: {exc}"
            print(status["error"])
    results = status.get("repos") if isinstance(status.get("repos"), dict) else {}
    results = {(k if k.startswith("wsl:") else os.path.normcase(os.path.normpath(k))): v for k, v in results.items()}
    active = [r for r in repos if r.get("state") == "active"]
    keys = {repo_key(r) for r in repos}
    results = {k: v for k, v in results.items() if k in keys}
    for key, note in notes.items():
        results.setdefault(key, {})["note"] = note

    floor, warning = floor_mb()
    status["warning"] = warning
    try:
        available = world.available_mb()
    except OSError:
        available = None
    since = status.get("hold_since")
    if available is not None and available < floor:
        since = since if isinstance(since, (int, float)) else now
        if now - since < HOLD_SECONDS:
            status.update(hold_since=since, repos=results,
                          hold=f"holding: {available} MB available, under the {floor} MB floor, for up to {int((since + HOLD_SECONDS - now) // 60)} more minutes")
            save_status(status)
            print(status["hold"])
            return 0
    status["hold_since"] = None

    start = status.get("next")
    order = sorted(range(len(active)), key=lambda i: (i - (start if isinstance(start, int) else 0)) % max(len(active), 1))
    next_up = 0
    for index in order:
        repo = active[index]
        result, error = tick_repo(world, repo)
        entry = results.setdefault(repo_key(repo), {})
        entry.update(at=now_iso(world.now()), result=result, error=error)
        print(f"{repo['path']}: {error or result}")
        if result and RESUMED.match(result):
            next_up = (index + 1) % len(active)
            break
    status.update(next=next_up, repos=results)
    save_status(status)
    return 0


def task_health(world):
    code, out = world.run(["schtasks", "/Query", "/TN", TASK, "/XML"], timeout=30)
    if code != 0:
        if code is not None and "cannot find" in out.lower():
            return {"state": "missing", "enabled": False, "action": None}
        return {"state": "unreadable", "enabled": None, "action": None}
    # Decoded, since Task Scheduler writes `&` in a path as `&amp;`.
    command, arguments = (html.unescape(m.group(1)) if m else None
                          for m in (re.search(rf"<{tag}>(.*?)</{tag}>", out, re.S) for tag in ("Command", "Arguments")))
    enabled = re.search(r"<Settings>.*?<Enabled>(true|false)</Enabled>", out, re.S | re.I)
    action = " ".join(part.strip() for part in (command, arguments) if part)
    is_enabled = enabled is None or enabled.group(1).lower() == "true"
    broken = action_problem(world, command or "", arguments or "")
    state = "broken" if broken else "installed" if is_enabled else "disabled"
    return {"state": state, "enabled": is_enabled, "action": action or None, "problem": broken}


def action_problem(world, command, arguments):
    """Why a task's action can't run, or None: its program and the script it's given both have to be there, or the task
    fires and does nothing."""
    program = command.strip().strip('"')
    if not program:
        return "it has no program to run"
    if not (world.is_file(program) or (not os.path.dirname(program) and shutil.which(program))):
        return f"its program is missing: {program}"
    script = re.search(r'"([^"]+\.py)"|(\S+\.py)\b', arguments)
    if script and not world.is_file(script.group(1) or script.group(2)):
        return f"its script is missing: {script.group(1) or script.group(2)}"
    return None


def report(world):
    """Everything `status` shows, read fresh. Nothing unknown is shown as fine."""
    saved, history = load_status()
    result = {"schema_version": SCHEMA, "repos": [], "error": None, "hold": saved.get("hold"), "warning": saved.get("warning"),
              "last_pass": saved.get("last_pass"), "task": task_health(world), "stale": True, "history": history}
    try:
        repos = load_repos()
    except ValueError as exc:
        repos, result["error"] = [], str(exc)
    result["error"] = result["error"] or saved.get("error")
    last = saved.get("last_pass")
    if isinstance(last, str):
        try:
            then = datetime.strptime(last, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc).timestamp()
            result["stale"] = world.now() - then > STALE_SECONDS
        except ValueError:
            pass
    ticks = saved.get("repos") if isinstance(saved.get("repos"), dict) else {}
    for repo in repos:
        key = repo_key(repo)
        tick_info = next((v for k, v in ticks.items() if k == key or (not repo.get("distro") and not k.startswith("wsl:")
                                                                         and same_path(k, repo["path"]))), {})
        result["repos"].append({"path": repo["path"], "distro": repo.get("distro"), "state": repo.get("state"),
                                "last_tick": tick_info.get("at"), "result": tick_info.get("result"),
                                "error": tick_info.get("error"), "note": tick_info.get("note")})
    return result


def print_status(data):
    task = data["task"]
    print(f"task: {task['state']}" + (f" ({task['action']})" if task["action"] else ""))
    if task.get("problem"):
        print(f"task problem: {task['problem']}")
    if data.get("history"):
        print(f"history unavailable: {data['history']}")
    if data["error"]:
        print(f"error: {data['error']}")
    if data["hold"]:
        print(data["hold"])
    if data["warning"]:
        print(f"warning: {data['warning']}")
    print(f"last pass: {data['last_pass'] or 'never'}" + (" (stale)" if data["stale"] and data["last_pass"] else ""))
    if not data["repos"]:
        print("no repos on the heartbeat")
    for repo in data["repos"]:
        where = f"{repo['path']} (WSL {repo['distro']})" if repo["distro"] else repo["path"]
        state = "" if repo["state"] == "active" else f" [{repo['state']}]"
        outcome = repo["error"] or repo["result"] or "never ticked"
        print(f"{where}{state}: {outcome}" + (f" at {repo['last_tick']}" if repo["last_tick"] else ""))
        if repo["note"]:
            print(f"  note: {repo['note']}")


def main(argv=None, world=None):
    world = world or World()
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("add", "remove"):
        sub = commands.add_parser(name)
        sub.add_argument("path")
        sub.add_argument("--distro")
        if name == "add":
            sub.add_argument("--allow-no-task", action="store_true", help="add even with no heartbeat task, as a test does")
    for name in ("list", "status"):
        commands.add_parser(name).add_argument("--json", action="store_true")
    commands.add_parser("tick")
    args = parser.parse_args(argv)
    if "LOCALAPPDATA" not in os.environ:
        parser.error("LOCALAPPDATA isn't set")
    try:
        if args.command == "tick":
            return tick(world)
        if args.command == "add":
            print(f"on the heartbeat: {add(world, args.path, args.distro, args.allow_no_task)['path']}")
        elif args.command == "remove":
            print(f"off the heartbeat: {remove(world, args.path, args.distro)['path']}")
        elif args.command == "list":
            repos = load_repos()
            if args.json:
                print(json.dumps({"schema_version": SCHEMA, "repos": repos}, indent=2))
            else:
                print("\n".join(f"{r['path']}" + (f" (WSL {r['distro']})" if r.get("distro") else "") for r in repos) or "no repos on the heartbeat")
        else:
            data = report(world)
            if args.json:
                print(json.dumps(data, indent=2))
            else:
                print_status(data)
    except (Refused, Busy, ValueError, OSError) as exc:
        print(f"refused: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        if stream:  # pythonw has none
            stream.reconfigure(encoding="utf-8")
    if sys.stdout is None:
        sys.stdout = sys.stderr = open(os.devnull, "w", encoding="utf-8")
    sys.exit(main())
