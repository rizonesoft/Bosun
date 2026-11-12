"""A disposable campaign: a real `process plan` on a clone of this repo, its CI and reviewer faked (D00 T03 §10).

It starts real Claude sessions for hours, which spend subscription usage, so it runs only with BOSUN_E2E=1:

    BOSUN_E2E=1 python -m unittest scripts.tests.test_campaign_e2e

The clone, in scratch, keeps only five trivial sections, and its run state stays in the clone (`RUN_GUARD_STATE`). A
faked `gh` answers CI for each commit by the newest e2e section stamped in it, so one section meets each gate action:

    §1  pending for 35 minutes            -> `wait`, then `proceed`: no next row may start before it clears
    §2  red until e2e/repaired-2.txt says fixed -> `repair`: a repair commit, then a recheck that passes
    §3  red on every commit               -> `repair` three times, then `exhausted`: the fault is filed, the run goes on
    §4  no run at all, its wait moved back two hours -> `unverified`: the fault is filed, the run goes on
    §5  green                             -> the next row each of those carries on to

A faked `codex` returns clean reviews. Both fakes sit first on a PATH every process the test starts inherits, and the
session's own shells see it (checked on 2026-11-08).

Nothing in the repo restarts or resumes a run (the owner's rule of 2026-11-12), and a `claude -p` session ends when the
Stop hook lets its turn end, where an interactive one would wait in its window for its own heartbeat. So the test plays
the operator: when a session has ended and work is left, and no wait is in force, it starts the next one itself. Right
after §2's Started commit it kills the live session's whole tree once: `run-guard.py status` must then read `ended`,
nothing may start by itself, and the next session the test starts must commit the next step of §2. Every process tree
it started is stopped and the clone removed at the end, pass or fail; set BOSUN_E2E_KEEP=1 to keep the clone for a look.
"""
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "lib"))
import todo_graph as tg  # noqa: E402
REF ="D00 T01"  # the clone's only TODO file is 00-workspace/TODO-01
SECTIONS = ["one", "two", "three", "four", "five"]
PENDING_SECONDS = 35 * 60  # longer than the gate's own 30 minute wait, so one call ends in `wait`
HOURS = float(os.environ.get("BOSUN_E2E_HOURS", "8"))
LOOK_EVERY = 300  # between the test's own looks at a run whose session has ended
RUN_OVER = ("drained", "complete", "parked", "stalled", "stopped", "halted")  # the guard's own endings: nothing is left to start
SCRATCH = Path(os.environ.get("BOSUN_E2E_SCRATCH") or os.environ.get("BOSUN_SCRATCH") or tempfile.gettempdir())

FAKE_GH = r'''"""The e2e campaign's GitHub CLI: CI answers scripted by the newest e2e section stamped in each commit."""
import json, os, re, subprocess, sys, time
from pathlib import Path

args = sys.argv[1:]
state_dir = Path(os.environ["E2E_FAKE_STATE"])
repo = os.environ["E2E_REPO"]
PENDING_SECONDS = int(os.environ.get("E2E_PENDING_SECONDS", "2100"))
WAIT_LIMIT = 2 * 60 * 60


def log(**fields):
    # One file per call: on Windows two processes appending to one file can write at the same offset.
    folder = state_dir / "gh-calls"
    folder.mkdir(exist_ok=True)
    name = f"{time.time_ns()}-{os.getpid()}"
    tmp = folder / f"{name}.tmp"
    tmp.write_text(json.dumps({"at": time.time(), "args": args, **fields}), encoding="utf-8")
    os.replace(tmp, folder / f"{name}.json")


def git(*a):
    done = subprocess.run(["git", "-C", repo, *a], capture_output=True, text=True)
    return done.returncode, done.stdout


def load():
    try:
        return json.loads((state_dir / "gh-state.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {"first_seen": {}, "runs": {}}


def save(state):
    tmp = state_dir / f"gh-state.json.{os.getpid()}.tmp"
    tmp.write_text(json.dumps(state), encoding="utf-8")
    os.replace(tmp, state_dir / "gh-state.json")


sys.path.insert(0, str(Path(repo) / "scripts" / "lib"))
from todo_graph import receipt_for  # the name `stamp` writes, so the two can't drift


def section_of(sha):
    """The newest of the five e2e sections with a receipt in this commit, or None."""
    code, out = git("ls-tree", "-r", "--name-only", sha, "docs/reviews")
    present = set(out.splitlines()) if code == 0 else set()
    found = [n for n in range(1, 6) if receipt_for(f"D00 T01 §{n}") in present]
    return max(found) if found else None


def verdict(sha, state):
    """(status, conclusion) for this commit, or None for no run at all."""
    section = section_of(sha)
    now = time.time()
    first = state["first_seen"].setdefault(sha, now)
    if section == 1:
        return ("in_progress", None) if now - first < PENDING_SECONDS else ("completed", "success")
    if section == 2:
        code, text = git("show", f"{sha}:e2e/repaired-2.txt")
        return ("completed", "success") if code == 0 and "fixed" in text else ("completed", "failure")
    if section == 3:
        return ("completed", "failure")
    if section == 4:
        # Stands in for two hours passing: the gate's wait on this commit is moved back past its limit.
        gate = Path(repo) / "build" / "ci-gate.json"
        try:
            record = json.loads(gate.read_text(encoding="utf-8"))
            waiting = record.get("waiting")
            if isinstance(waiting, dict) and waiting.get("sha") == sha and waiting.get("since", 0) > now - WAIT_LIMIT:
                waiting["since"] = now - WAIT_LIMIT - 60
                tmp = gate.with_name(f"ci-gate.json.{os.getpid()}.tmp")
                tmp.write_text(json.dumps(record), encoding="utf-8")
                os.replace(tmp, gate)
                log(moved_wait_back=sha)
        except (OSError, ValueError):
            pass
        return None
    return ("completed", "success")


def option(name):
    return args[args.index(name) + 1] if name in args else None


state = load()
if args[:2] == ["auth", "status"]:
    print("github.com\n  Logged in to github.com account e2e (keyring)")
    log(answer="signed in")
    sys.exit(0)
if args[:2] == ["workflow", "run"]:
    log(answer="started")
    sys.exit(0)
if args[:2] == ["run", "list"] and option("--commit"):
    sha, workflow = option("--commit"), option("--workflow")
    answer = verdict(sha, state)
    runs = []
    if answer:
        key = f"{sha}:{workflow}"
        run_id = state["runs"].setdefault(key, 9_000_000 + len(state["runs"]))
        status, conclusion = answer
        runs.append({"databaseId": run_id, "headSha": sha, "status": status, "conclusion": conclusion, "event": "push",
                     "url": f"https://example.invalid/e2e/runs/{run_id}", "createdAt": "2026-11-08T00:00:00Z"})
    save(state)
    log(sha=sha, workflow=workflow, section=section_of(sha), answer=answer and answer[1] or (answer and answer[0]) or "none")
    print(json.dumps(runs))
    sys.exit(0)
if args[:2] == ["run", "view"] and len(args) > 2:
    run_id = int(args[2])
    sha = next((k.split(":")[0] for k, v in state["runs"].items() if v == run_id), None)
    section = section_of(sha) if sha else None
    why = {2: "test_two_repaired FAILED: e2e/repaired-2.txt must exist and hold the single line `fixed`",
           3: "test_three FAILED: e2e/attempt-3.txt must hold the next attempt number"}.get(section, "a step failed")
    log(run=run_id, section=section, answer="view")
    if "--json" in args:
        print(json.dumps({"jobs": [{"name": "Build and test on Windows", "status": "completed", "conclusion": "failure",
                                    "url": f"https://example.invalid/e2e/runs/{run_id}/job/1"}]}))
    else:
        print(f"X Build and test on Windows\n  {why}")
    sys.exit(0)
log(answer="unsupported")
print(f"fake gh (e2e campaign): '{' '.join(args)}' is not part of this test", file=sys.stderr)
sys.exit(1)
'''

FAKE_CODEX = r'''"""The e2e campaign's reviewer: a clean reply of whichever kind the prompt asks for."""
import json, os, sys, time
from pathlib import Path

args = sys.argv[1:]
state_dir = Path(os.environ["E2E_FAKE_STATE"])
if args[:2] == ["login", "status"]:
    print("Logged in using ChatGPT")
    sys.exit(0)
prompt = sys.stdin.read() if "-" in args else (args[-1] if args else "")
if "PLAN: complete" in prompt:
    kind, reply = "plan", "No gaps: the section is a complete feature.\n\nHIGHEST: none\nPLAN: complete\nOVERALL: approve"
elif "COMPLETION: complete" in prompt:
    kind, reply = "completion", ("Part 1\n1. Every item: delivered, in the commit.\n\nPart 2\nNo competitor applies to a fixture.\n\n"
                                 "Gaps: none.\n\nCOMPLETION: complete")
else:
    kind, reply = "round", "1. adversarial: approve, no findings.\n\nHIGHEST: none\nOVERALL: approve"
out = args[args.index("-o") + 1] if "-o" in args else None
if out:
    Path(out).write_text(reply + "\n", encoding="utf-8")
else:
    print(reply)
folder = state_dir / "codex-calls"  # one file per call, as the fake gh writes
folder.mkdir(exist_ok=True)
name = f"{time.time_ns()}-{os.getpid()}"
(folder / f"{name}.tmp").write_text(json.dumps({"at": time.time(), "kind": kind, "args": args}), encoding="utf-8")
os.replace(folder / f"{name}.tmp", folder / f"{name}.json")
'''

ADJACENCY = ("**Adjacency:** list=not-applicable (a fixture file); document=not-applicable (a fixture file); "
             "settings=not-applicable (a fixture file); reporting=not-applicable (a fixture file); "
             "notifications=not-applicable (a fixture file); permissions=not-applicable (a fixture file); "
             "audit=not-applicable (a fixture file); exchange=not-applicable (a fixture file); "
             "reverse=not-applicable (a fixture file)")


def e2e_todo():
    """The clone's only TODO file: five sections, each writing one file, in a chain."""
    rows = "\n".join(f"| {n} | §{n} | Write e2e/{name}.txt | {'--' if n == 1 else f'§{n - 1}'} | [ ] |"
                     for n, name in enumerate(SECTIONS, 1))
    bodies = "\n".join(
        f"## {n}. Write e2e/{name}.txt\n\n**Phase:** 0\n**Needs:** none\n**Surface:** no\n\n"
        f"A fixture for the disposable campaign: one file with one line.\n\n**Build order:**\n\n"
        f"1. Write the file. Done when: the checkpoint passes.\n\n"
        f"- [ ] Create `e2e/{name}.txt` holding the single line `{name}`. Done when: the checkpoint below exits 0.\n"
        f"- [ ] Commit: `e2e: write {name}`\n\n"
        f"**Test checkpoint:** `python -c \"import pathlib; assert pathlib.Path('e2e/{name}.txt').read_text().strip() == '{name}'\"` exits 0.\n"
        for n, name in enumerate(SECTIONS, 1))
    return ("---\nschema_version: 1\nid: e2e-fixture\ndomain: 00-workspace\nstatus: active\n"
            'title: "TODO-01 -- Disposable campaign fixture"\n---\n\n# TODO-01 -- Disposable campaign fixture\n\n'
            "> **Goal:** five trivial sections for the disposable campaign in `scripts/tests/test_campaign_e2e.py`.\n\n"
            "> **Current state:** none of the files exist.\n\n## Inputs\n\n- None: each section writes one new file.\n\n"
            "## Outcome\n\n- `e2e/one.txt` to `e2e/five.txt`, each holding its own name.\n\n" + ADJACENCY + "\n\n"
            "## Implementation Order\n\n| Order | Section | Deliverable | Depends On | Status |\n"
            "| ----- | ------- | ----------- | ---------- | ------ |\n" + rows + "\n\n" + bodies +
            "\n## Verification\n\n- Every file holds its name.\n")


def run(args, cwd, env=None, timeout=300, check=True):
    done = subprocess.run(args, cwd=cwd, env=env, capture_output=True, text=True, timeout=timeout,
                          stdin=subprocess.DEVNULL, encoding="utf-8", errors="replace")
    if check and done.returncode != 0:
        raise AssertionError(f"{args} exited {done.returncode}: {(done.stdout + done.stderr)[-2000:]}")
    return done


def write_launcher(folder, name, script):
    (folder / f"fake_{name}.py").write_text(script, encoding="utf-8")
    if os.name == "nt":
        (folder / f"{name}.cmd").write_text(f'@"{sys.executable}" "{folder / f"fake_{name}.py"}" %*\r\n', encoding="utf-8")
    # Git Bash finds an extensionless script too.
    posix_python = Path(sys.executable).as_posix()
    (folder / name).write_text(f'#!/bin/sh\nexec "{posix_python}" "{(folder / f"fake_{name}.py").as_posix()}" "$@"\n',
                               encoding="utf-8", newline="\n")
    if os.name != "nt":
        (folder / name).chmod(0o755)


def seed(work: Path) -> dict:
    """The clone, its bare origin, the fakes and the environment, ready for a session. Spends no Claude usage."""
    clone, origin, fakes = work / "clone", work / "origin.git", work / "fakes"
    run(["git", "clone", "-q", "--no-hardlinks", str(ROOT), str(clone)], cwd=work)
    for args in (["config", "user.name", "E2E Campaign"], ["config", "user.email", "e2e@example.invalid"],
                 ["config", "core.hooksPath", "scripts/hooks"]):
        run(["git", *args], cwd=clone)
    todo = clone / "todo"
    for child in todo.iterdir():
        if child.is_dir() and re.match(r"\d\d-", child.name):
            shutil.rmtree(child)
    for receipt in (clone / "docs" / "reviews").rglob("*"):
        if receipt.is_file():
            receipt.unlink()
    (todo / "00-workspace").mkdir()
    (todo / "00-workspace" / "TODO-01-e2e.md").write_text(e2e_todo(), encoding="utf-8", newline="\n")
    (todo / "00-workspace" / "INDEX.md").write_text(
        "# 00-workspace\n\nThe disposable campaign's fixture.\n\n| File | Scope | Status |\n| ---- | ----- | ------ |\n"
        "| TODO-01-e2e.md | Five trivial sections | active |\n", encoding="utf-8", newline="\n")
    (todo / "TODO-00-INDEX.md").write_text(
        "# TODO-00 -- Root Index\n\nGenerated plan: [implementation-plan.md](./implementation-plan.md).\n\n## Active TODOs\n\n"
        "| Domain | File | Scope |\n| ------ | ---- | ----- |\n| 00 | 00-workspace/TODO-01-e2e.md | Five trivial sections |\n\n"
        "## Completed TODOs\n", encoding="utf-8", newline="\n")
    # The real sections are gone, so a wake naming one of them would point at nothing (found by a run's own audit).
    (todo / "wakes.json").write_text("[]\n", encoding="utf-8", newline="\n")
    run([sys.executable, "scripts/todo-graph.py", "plan", "--sync"], cwd=clone)
    run([sys.executable, "scripts/todo-graph.py", "validate"], cwd=clone)
    run(["git", "add", "-A"], cwd=clone)
    # Empty when ROOT is itself a seeded clone, as when a campaign's sessions run this suite, and it must still land.
    run(["git", "commit", "-q", "--allow-empty", "-m", "e2e: the disposable campaign's fixture"], cwd=clone)
    run(["git", "clone", "-q", "--bare", str(clone), str(origin)], cwd=work)
    run(["git", "remote", "set-url", "origin", str(origin)], cwd=clone)
    run(["git", "fetch", "-q", "origin"], cwd=clone)
    run(["git", "branch", "-q", "--set-upstream-to", "origin/main", "main"], cwd=clone)
    fakes.mkdir()
    write_launcher(fakes, "gh", FAKE_GH)
    write_launcher(fakes, "codex", FAKE_CODEX)
    state = clone / ".git" / "run-guard" / "state.json"
    # Without BOSUN_E2E*, so the clone's own test suite, which the sessions run, can't start a campaign inside this one.
    env = {k: v for k, v in os.environ.items() if k != "BOSUN_SLOT" and not k.startswith("BOSUN_E2E")}
    env.update(PATH=f"{fakes}{os.pathsep}{os.environ['PATH']}", RUN_GUARD_STATE=str(state), E2E_FAKE_STATE=str(fakes),
               E2E_REPO=str(clone), E2E_PENDING_SECONDS=str(PENDING_SECONDS))
    return {"clone": clone, "origin": origin, "fakes": fakes, "state": state, "env": env}


def remove_tree(path):
    """Remove a work folder, git's read-only object files included, raising if anything is left: `ignore_errors` hid
    that on Windows, and a folder stayed behind after every run."""
    def writable(func, target, _exc):
        os.chmod(target, stat.S_IWRITE)
        func(target)
    shutil.rmtree(path, onexc=writable)


def stop_tree(pid):
    if not pid:
        return
    if os.name == "nt":
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)], capture_output=True, timeout=60)
    else:
        try:
            os.killpg(pid, 9)
        except OSError:
            pass


def alive(pid):
    if not pid:
        return False
    if os.name == "nt":
        out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True, timeout=60).stdout
        return str(pid) in out
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def read_calls(folder):
    """Every call a fake recorded, oldest first. A record that does not read is an error, never skipped."""
    if not folder.exists():
        return []
    return sorted((json.loads(p.read_text(encoding="utf-8")) for p in folder.glob("*.json")), key=lambda c: c["at"])


class ScriptedCi(unittest.TestCase):
    """The fixture alone, no Claude: the real gate reads each section's scripted CI as the action it is there to show."""

    @classmethod
    def setUpClass(cls):
        cls.work = Path(tempfile.mkdtemp(prefix="bosun-campaign-fixture-", dir=str(SCRATCH) if SCRATCH.is_dir() else None))
        # Registered before seeding: a failed setUpClass skips tearDownClass, which left a folder behind each time.
        cls.addClassCleanup(remove_tree, cls.work)
        cls.seeded = seed(cls.work)
        cls.clone = cls.seeded["clone"]

    def commit(self, path, text):
        target = self.clone / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
        run(["git", "add", path], cwd=self.clone)
        run(["git", "commit", "-q", "-m", f"fixture: {path}"], cwd=self.clone)
        return run(["git", "rev-parse", "HEAD"], cwd=self.clone).stdout.strip()

    def gate(self, sha):
        done = run([sys.executable, "scripts/ci_status.py", "--gate", "--sha", sha, "--json"], cwd=self.clone,
                   env=self.seeded["env"], check=False)
        return json.loads(done.stdout)["action"]

    def test_each_section_meets_its_gate_action(self):
        # Receipts at the path `stamp` writes: a first run used a spelling of its own, so every commit read green.
        def receipt(n):
            return tg.receipt_for(f"{REF} §{n}")
        self.assertEqual(receipt(1), "docs/reviews/d00-t01-s1.json")
        # In order, as the run meets them: the gate's record carries from one to the next.
        self.assertEqual(self.gate(self.commit(receipt(1), "{}")), "wait")
        self.assertEqual(self.gate(self.commit(receipt(2), "{}")), "repair")
        self.assertEqual(self.gate(self.commit("e2e/repaired-2.txt", "fixed\n")), "proceed")
        red = [self.commit(receipt(3), "{}")] + [self.commit("e2e/attempt-3.txt", f"{n}\n") for n in (1, 2, 3)]
        self.assertEqual([self.gate(sha) for sha in red], ["repair", "repair", "repair", "exhausted"])
        self.assertEqual(self.gate(self.commit(receipt(4), "{}")), "unverified")
        self.assertEqual(self.gate(self.commit(receipt(5), "{}")), "proceed")
        calls = read_calls(self.seeded["fakes"] / "gh-calls")
        self.assertTrue(any(c.get("moved_wait_back") for c in calls), "§4's wait was never moved back")

    def test_calls_made_at_once_are_all_recorded_whole(self):
        # Run 3 lost a record to two appends at the same offset; each call now writes its own file.
        gh = shutil.which("gh", path=self.seeded["env"]["PATH"])
        folder = self.seeded["fakes"] / "gh-calls"
        before = len(read_calls(folder))
        procs = [subprocess.Popen([gh, "auth", "status"], cwd=self.clone, env=self.seeded["env"], stdout=subprocess.DEVNULL,
                                  stderr=subprocess.DEVNULL) for _ in range(16)]
        for p in procs:
            self.assertEqual(p.wait(timeout=120), 0)
        self.assertEqual(len(read_calls(folder)) - before, 16, "a call made alongside others was lost or torn")

    def test_the_clone_keeps_no_wake_for_a_section_it_removed(self):
        wakes = json.loads((self.clone / "todo" / "wakes.json").read_text(encoding="utf-8"))
        self.assertEqual(wakes, [], "a wake names a section the fixture deleted")

    def test_seeding_a_seeded_clone_still_works(self):
        # The campaign's own sessions run this suite inside the clone, where ROOT is already the fixture.
        work = Path(tempfile.mkdtemp(prefix="bosun-campaign-fixture-", dir=str(SCRATCH) if SCRATCH.is_dir() else None))
        self.addCleanup(remove_tree, work)
        # A copy at the fixture commit itself, so re-seeding it changes nothing and the commit is empty.
        source = work / "seeded"
        run(["git", "clone", "-q", "--no-hardlinks", str(self.clone), str(source)], cwd=work)
        fixture = run(["git", "log", "--format=%H", "-1", "--grep", "disposable campaign's fixture"], cwd=source).stdout.strip()
        run(["git", "reset", "-q", "--hard", fixture], cwd=source)
        global ROOT
        saved, ROOT = ROOT, source
        try:
            inner = seed(work)
        finally:
            ROOT = saved
        self.assertIn("disposable campaign's fixture", run(["git", "log", "-1", "--format=%s"], cwd=inner["clone"]).stdout)

    def test_the_clone_cannot_start_a_campaign_of_its_own(self):
        self.assertFalse([k for k in self.seeded["env"] if k.startswith("BOSUN_E2E")],
                         "the sessions would inherit BOSUN_E2E, and their test suite would start a nested campaign")

    def test_the_fakes_come_first_and_an_unscripted_gh_call_fails(self):
        for name in ("gh", "codex"):
            found = shutil.which(name, path=self.seeded["env"]["PATH"])
            self.assertTrue(found and Path(found).parent == self.seeded["fakes"], f"the fake {name} isn't first on PATH: {found}")
        unknown = run([shutil.which("gh", path=self.seeded["env"]["PATH"]), "pr", "list"], cwd=self.clone,
                      env=self.seeded["env"], check=False)
        self.assertEqual(unknown.returncode, 1, "a gh call the test doesn't script must fail loudly")


@unittest.skipUnless(os.environ.get("BOSUN_E2E") == "1", "runs real Claude sessions for hours, which spend subscription usage: set BOSUN_E2E=1")
class DisposableCampaign(unittest.TestCase):
    def git(self, *args):
        return run(["git", *args], cwd=self.clone).stdout

    def guard_state(self):
        try:
            return json.loads(self.state.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}

    def live_pid(self):
        """The session the test started last, while it runs. The guard starts none, so there is no other."""
        return self.sessions[-1].pid if self.sessions and self.sessions[-1].poll() is None else None

    def start_session(self):
        """The operator's part, which nothing else plays: start a `process plan` session in the clone."""
        log = open(self.work / f"session-{len(self.sessions) + 1}.log", "w", encoding="utf-8")
        self.addCleanup(log.close)
        flags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
        session = subprocess.Popen([self.claude, "-p", "process plan", "--permission-mode", "bypassPermissions"], cwd=self.clone,
                                   env=self.env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                                   creationflags=flags, start_new_session=os.name != "nt")
        self.sessions.append(session)
        print(f"[{time.strftime('%H:%M:%S')}] started session {len(self.sessions)} (pid {session.pid})", flush=True)
        return session

    def status(self):
        done = run([sys.executable, "scripts/run-guard.py", "status"], cwd=self.clone, env=self.env, timeout=600, check=False)
        line = (done.stdout.strip().splitlines() or [""])[0]
        self.looks.append((time.time(), line))
        print(f"[{time.strftime('%H:%M:%S')}] status: {line}", flush=True)
        return line

    def section_text(self, ref):
        """The body of one section, from its TODO file in the clone."""
        domain, file, number = re.fullmatch(r"D(\d\d) T(\d\d) §(\d+)", ref).groups()
        for path in (self.clone / "todo").glob(f"{domain}-*/TODO-{file}-*.md"):
            found = re.search(rf"^## {number}\. .*?(?=^## |\Z)", path.read_text(encoding="utf-8"), re.M | re.S)
            if found:
                return found.group(0)
        return ""

    def commits(self):
        """(unix time, sha, subject, files) for each commit after the fixture, oldest first."""
        out = self.git("log", "--reverse", "--format=@@%ct %H %s", "--name-only", f"{self.base}..HEAD")
        found = []
        for block in out.split("@@")[1:]:
            lines = [x for x in block.splitlines() if x.strip()]
            stamp, sha, subject = lines[0].split(" ", 2)
            found.append((int(stamp), sha, subject, lines[1:]))
        return found

    def test_a_disposable_campaign_meets_every_gate_action_and_a_killed_session_stays_dead(self):
        self.claude = shutil.which("claude")
        self.assertIsNotNone(self.claude, "claude is not on PATH")
        SCRATCH.mkdir(parents=True, exist_ok=True)
        self.work = Path(tempfile.mkdtemp(prefix="bosun-campaign-e2e-", dir=str(SCRATCH)))
        if os.environ.get("BOSUN_E2E_KEEP") != "1":
            self.addCleanup(remove_tree, self.work)
        seeded = seed(self.work)
        self.clone, self.env, self.state, fakes = seeded["clone"], seeded["env"], seeded["state"], seeded["fakes"]
        self.base = self.git("rev-parse", "HEAD").strip()
        self.looks, self.sessions = [], []
        self.addCleanup(lambda: [stop_tree(s.pid) for s in self.sessions if s.poll() is None])
        self.start_session()
        deadline, next_look, killed_at = time.time() + HOURS * 3600, time.time() + LOOK_EVERY, None
        while time.time() < deadline:
            time.sleep(30)
            subjects = [c[2] for c in self.commits()]
            if killed_at is None and any(re.search(rf"{REF} §2\b", s) for s in subjects):
                pid = self.live_pid()
                self.assertIsNotNone(pid, "no live session to kill after §2 started")
                stop_tree(pid)
                self.sessions[-1].wait(timeout=120)
                killed_at = time.time()
                print(f"[{time.strftime('%H:%M:%S')}] killed the session tree {pid} after §2 started", flush=True)
                # The run ended with its session, and nothing brings it back: no session starts by itself.
                self.assertEqual(self.status(), "ended", "a run whose session was killed isn't recorded as ended")
                owner = self.guard_state().get("session_id")
                time.sleep(LOOK_EVERY)
                self.assertEqual(self.status(), "ended", "the ended run changed by itself")
                self.assertEqual(self.guard_state().get("session_id"), owner, "a session took the ended run over by itself")
                self.start_session()  # the operator starts the run again
                next_look = time.time() + LOOK_EVERY
                continue
            if time.time() >= next_look and self.live_pid() is None:
                next_look = time.time() + LOOK_EVERY
                state = self.guard_state()
                waits = state.get("active") and isinstance(state.get("wait_until"), (int, float)) and time.time() < state["wait_until"]
                # Settled: nothing left that a run can do, including rows that now wait on a person, as the filed faults do.
                if state.get("status") in RUN_OVER or (waits and state.get("wait_reason") == "rows-waiting"):
                    self.status()
                    break
                if not waits:  # a wait is honoured as an interactive session's heartbeat would honour it
                    self.start_session()
        else:
            self.fail(f"the campaign didn't finish within {HOURS} hours; state: {self.guard_state()}; looks: {self.looks[-5:]}")
        self.check(fakes, killed_at)

    def check(self, fakes, killed_at):
        commits = self.commits()
        gh = [c for c in read_calls(fakes / "gh-calls") if c.get("sha")]
        subjects = [c[2] for c in commits]
        print("commits:\n  " + "\n  ".join(subjects))

        def first_commit(pattern, after=0):
            return next((c for c in commits if c[0] >= after and re.search(pattern, c[2])), None)

        def started(n):
            return first_commit(rf"{REF} §{n}\b")

        # Every e2e section was stamped.
        for n in range(1, 6):
            self.assertIsNotNone(first_commit(rf"stamp {REF} §{n}\b"), f"§{n} was never stamped")
        # wait: §1's commit read pending, the gate waited, and §2 began only once it was green.
        one = [c for c in gh if c["section"] == 1]
        self.assertTrue(any(c["answer"] == "in_progress" for c in one), "CI on §1 never read pending")
        green_one = min(c["at"] for c in one if c["answer"] == "success")
        self.assertGreaterEqual(green_one - min(c["at"] for c in one), 30 * 60, "the gate didn't wait out its 30 minutes on §1")
        # Git times are whole seconds, so §2's start may share the second §1 went green, and no earlier.
        self.assertGreaterEqual(started(2)[0], int(green_one), "§2 started before §1's CI cleared")
        # repair: §2's stamp read red, a commit repaired it, and the recheck read green.
        two = [c for c in gh if c["section"] == 2]
        self.assertTrue(any(c["answer"] == "failure" for c in two), "CI on §2 never read red")
        repair = next((c for c in commits if "e2e/repaired-2.txt" in c[3]), None)
        self.assertIsNotNone(repair, "no commit repaired §2's red CI")
        self.assertTrue(any(c["sha"] == repair[1] and c["answer"] == "success" for c in two), "the repair was never rechecked")
        green_two = min(c["at"] for c in two if c["sha"] == repair[1] and c["answer"] == "success")
        self.assertGreaterEqual(started(3)[0], int(green_two), "§3 started before the repair of §2's CI read green")
        # exhausted: §3 stayed red through three repairs, the fault was filed as a section, and §4 started after it.
        three = {c["sha"] for c in gh if c["section"] == 3 and c["answer"] == "failure"}
        self.assertGreaterEqual(len(three), 4, f"§3 met {len(three)} red commits, not the first and three repairs")
        last_red_three = max(c["at"] for c in gh if c["section"] == 3)
        filed = [c for c in commits if any(f.startswith("todo/") and f.endswith(".md") for f in c[3])
                 and c[0] >= last_red_three - 60 and not re.search(rf"{REF} §[1-5]\b", c[2])]
        self.assertTrue(filed, "the exhausted fault on §3 was never filed as a section")
        self.assertGreater(started(4)[0], filed[0][0] - 1, "§4 started before §3's fault was filed")
        # unverified: §4 never got a run, its fault was filed, and §5 still ran.
        four = [c for c in gh if c["section"] == 4]
        self.assertTrue(four and all(c["answer"] == "none" for c in four), "CI on §4 wasn't the no-run case")
        last_four = max(c["at"] for c in four)
        filed_four = [c for c in commits if c[0] >= int(last_four) and any(f.startswith("todo/") for f in c[3])
                      and not re.search(rf"{REF} §[1-5]\b", c[2])]
        self.assertTrue(filed_four, "the unverified fault on §4 was never filed")
        self.assertGreaterEqual(started(5)[0], filed_four[0][0], "§5 started before §4's unverified fault was filed")
        # The filed faults are open sections the plan still shows, not lost.
        disposition = json.loads(run([sys.executable, "scripts/todo-graph.py", "query", "disposition"], cwd=self.clone).stdout)
        print("open after the run: " + ", ".join(f"{d['ref']} {d['state']}" for d in disposition))
        filed_text = [self.section_text(d["ref"]) for d in disposition if not re.fullmatch(rf"{REF} §[1-5]", d["ref"])]
        self.assertTrue(any(re.search(r"test_three|attempt-3|exhausted", t, re.I) for t in filed_text),
                        "no open section records §3's exhausted CI fault")
        self.assertTrue(any(re.search(r"unverified|no run|no answer", t, re.I) for t in filed_text),
                        "no open section records §4's unverified CI fault")
        # the kill: the guard recorded the run as ended, and the session the operator then started took §2's next step.
        self.assertIsNotNone(killed_at, "the session was never killed")
        guard_log = (self.state.parent / "run-guard.log").read_text(encoding="utf-8", errors="replace")
        self.assertRegex(guard_log, r"status: ended session \S+ is gone", "the guard log doesn't show the run ending with its session")
        after_kill = next((c for c in commits if c[0] >= killed_at), None)
        self.assertIsNotNone(after_kill, "nothing was committed after the kill")
        self.assertTrue(re.search(rf"{REF} §2\b|e2e: write two", after_kill[2]) or "e2e/two.txt" in after_kill[3],
                        f"the next session's first commit wasn't §2's next step: {after_kill[2]}")
        self.assertEqual(self.git("status", "--porcelain").strip(), "", "the run left something uncommitted")
        print(f"reviews: {len(read_calls(fakes / 'codex-calls'))}; sessions: {len(self.sessions)}; looks: {len(self.looks)}")


if __name__ == "__main__":
    unittest.main()
