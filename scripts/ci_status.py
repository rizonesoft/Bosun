#!/usr/bin/env python3
"""Report the CI state of main or of one pushed commit, and what a run should do next.

    python scripts/ci_status.py                              # build.yml and plan.yml on main
    python scripts/ci_status.py --json                       # the same report as one JSON object
    python scripts/ci_status.py --workflow build.yml --branch main
    python scripts/ci_status.py --sha HEAD --base <commit>   # did this commit pass, or was it rightly not built?
    python scripts/ci_status.py --gate --sha HEAD --wait-minutes 30   # also print one action
    python scripts/ci_status.py --sha HEAD --use-proven      # read only: is this commit proven, by a run or the record?

Each workflow (by default `build.yml` and `plan.yml`, the ones in `.github/workflows/` whose results gate main) gets one
verdict from the newest run that judged the commit or branch; a cancelled run judges nothing:

    green       the newest run succeeded
    red         the newest run failed (its failed jobs are named)
    pending     a newer run is queued or in progress, so nothing older can answer
    none        no run says anything, and a skip can't be proven
    skipped     --base was given, and the workflow's own path filters prove this push never triggers it

`--sha` reads that commit's runs with `gh run list --commit`, so an older green can't satisfy it. `--base` is the remote
head before the push: GitHub filters paths against the whole push, so a code commit followed by a Markdown stamp is not
skipped. A skip needs proof, so a missing base, an unreadable workflow, a failed diff or a diff over GitHub's 300 file
limit reads `none`.

The overall exit takes the first that applies across the workflows: 1 any red; 3 any unavailable (`gh` missing, not
authenticated or failed, which is never green); 4 any pending; 5 any none; 0 only when each is green or skipped. Exit 2
is bad usage. No run and an unknown state are never success.

`--gate` needs `--sha` and adds one action: `proceed` on exit 0, `wait` on exit 3, 4 or 5, `repair` on red with attempts
left, `exhausted` after three repairs have failed, and `unverified` after two hours of waiting on one commit or when its
state can't be saved. The last two carry on with local gates, because the run must not block itself, and the guard
refuses `complete` while the head has no green. State lives in `build/ci-gate.json`: the repair attempts, keyed by the
first red commit, when waiting began, and the last proven commit. Waiting is limited across every call, and no tool starts
after the limit: HEAD is read from the `.git` files with no process, and any other name is looked up within what remains. That commit is the base when `--base` is omitted, so a
skip means the workflow's inputs haven't changed since something passed, and a restart loses nothing. A failed commit
before it can't slip through, because its code still shows in the diff. A green clears the attempts, and if that file is
locked it leaves `build/ci-gate.cleared`, which the next read honours once. The policy that consumes this is in the `process-phase` skill. Stdlib only; the `gh` and
`git` runners, the clocks and the sleep are injectable for tests.

A workflow that reads `none` for a commit (no run, and no proof of a skip) needs a real run, and `gh workflow run <file>
--ref <branch>` runs whatever the branch holds when it is run. So the branch's remote head is read first (`gh api`):

    it is the commit      `--gate` starts the workflow itself, once per commit and workflow (recorded under `started`
                          in `build/ci-gate.json`), and says so on stderr and in `started`: but only once the workflow
                          has had no run for START_GRACE_SECONDS since this gate's first look, looking again every
                          GRACE_POLL_SECONDS meanwhile. GitHub lists a push's own run a few seconds after the push,
                          and a gate called at once started a second one, which the workflow's concurrency group
                          queued behind the first, doubling the wait (D00 T03 §22). When the wait it was given leaves
                          no room for that (no --wait-minutes, a short one, or the two hour limit nearly spent), it
                          starts the workflow at once, as the only chance it has. While a start is held the
                          `waiting:` line says so, and the final JSON carries `start_held`. A read-only look starts
                          nothing and prints the command (`recovery`).
    it is another commit  nothing is started or offered; `moved_to` names the head, which is the commit to gate.
    it is behind it       the commit isn't on the remote yet, so there is nothing to run: `not_pushed` names the head.
    it can't be read      nothing is started or offered; `recovery_error` says why.

Two gates may look at one commit at once (a restart while the first still polls). Every change to the record is made
under `build/ci-gate.lock`, and the one function that saves it refuses without that lock, so both cannot start the same
workflow, and a start is recorded before it is made. A gate that can't get the lock in ten seconds saves nothing on
that look, which reads as a record that couldn't be saved: nothing is started, and a red or silent CI is `unverified`. Only a
refusal GitHub itself answered with (an HTTP 4xx) removes that record; a start that failed some other way may have been
accepted, so it is kept and not tried again for the commit, and what was said is kept with it: every later look, and a
restart, still report that the start was never confirmed, until the run appears. A record that can't be read may have
held a start, so from then until the next green nothing is started, and the command to run by hand is named.

None of these changes a verdict, the exit code or the action. A run answers for a commit only through its own `headSha`
(`gh run list --commit`), so one started just as the branch moved runs the newer commit and never turns this one green.
"""
import argparse
import contextlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ("build.yml", "plan.yml")
FIELDS = "databaseId,headSha,status,conclusion,url,createdAt,event"
LIMIT = 10
GREEN = {"success", "neutral", "skipped"}
# A run that was stopped before it judged the commit: no verdict either way.
NO_VERDICT = {"cancelled", "stale"}
MAX_JOBS = 20
MAX_MESSAGE = 300
MAX_DIFF_FILES = 300  # GitHub always runs a workflow when a push changes more files than its filters will read
MAX_REPAIRS = 3
CANDIDATE = re.compile(r"[0-9a-f]{40}")
RESOLVE_SECONDS = 5  # a name other than HEAD still needs git, and that is a quick local look
POLL_SECONDS = 60
# How long a gate gives a push's own run to be listed before it starts the workflow itself, and how often it looks
# while it waits for that (D00 T03 §22).
START_GRACE_SECONDS = 90
GRACE_POLL_SECONDS = 10
WAIT_LIMIT_SECONDS = 2 * 60 * 60
REPAIRS = Path("build") / "ci-gate.json"
CLEARED = Path("build") / "ci-gate.cleared"
LOCK = Path("build") / "ci-gate.lock"
HISTORY_LOST = "history_lost"  # in the record: it could not be read at some point, so what it held is not known
LOCK_WAIT_SECONDS = 10  # how long one gate waits for another to finish a change to the record
LOCK_STALE_SECONDS = 120  # a lock older than this was left by a process that died holding it
EXIT_RED, EXIT_UNAVAILABLE, EXIT_PENDING, EXIT_NONE = 1, 3, 4, 5


class Unavailable(Exception):
    """gh is missing, unauthenticated or failed: the CI state is unknown (exit 3)."""


def run_tool(name, args, cwd=ROOT, timeout=120):
    """Run a tool; returns (exit code, stdout, stderr). Raises Unavailable when it can't start."""
    # An explicit path: on Windows a bare which() also searches the working directory, where a planted tool could sit.
    executable = shutil.which(name, path=os.environ.get("PATH", ""))
    if executable is None:
        raise Unavailable(f"{name} is not installed or not on PATH")
    try:
        done = subprocess.run([executable, *args], cwd=cwd, capture_output=True, text=True, encoding="utf-8",
                              errors="replace", timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise Unavailable(f"{name} could not run: {exc}") from None
    return done.returncode, done.stdout, done.stderr


def run_gh(args, timeout=120):
    return run_tool("gh", args, timeout=timeout)


def gh_json(runner, args):
    try:
        code, out, err = runner(args)
    except FileNotFoundError:
        raise Unavailable("gh is not installed or not on PATH") from None
    if code != 0:
        reason = (err or out or "").strip().splitlines()
        raise Unavailable(f"gh {' '.join(args[:2])} exited {code}: {(reason[0] if reason else 'no output')[:MAX_MESSAGE]}")
    try:
        return json.loads(out)
    except ValueError:
        raise Unavailable(f"gh {' '.join(args[:2])} did not return JSON") from None


def verdict_of(run):
    if run is None:
        return "none"
    conclusion = (run.get("conclusion") or "").lower()
    if conclusion in GREEN:
        return "green"
    if conclusion in NO_VERDICT:
        return "cancelled"
    return "red"


def brief(run):
    return None if run is None else {k: run.get(k) for k in ("databaseId", "headSha", "status", "conclusion", "url",
                                                              "createdAt", "event")}


def path_rule(pattern):
    """A GitHub path filter as a regex: ** crosses folders, * does not. None for a form this can't judge."""
    if not pattern or pattern.startswith("!") or any(c in pattern for c in "[]{}+"):
        return None
    out = ""
    i = 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            out += "(?:.*/)?"
            i += 3
        elif pattern.startswith("**", i):
            out += ".*"
            i += 2
        elif pattern[i] == "*":
            out += "[^/]*"
            i += 1
        elif pattern[i] == "?":
            out += "[^/]"
            i += 1
        else:
            out += re.escape(pattern[i])
            i += 1
    return re.compile(out + r"\Z")


def push_filters(workflow_text):
    """The (paths, paths-ignore) lists under on.push, or None when they can't be read with confidence."""
    lines = [line.rstrip() for line in workflow_text.splitlines() if line.strip() and not line.lstrip().startswith("#")]
    index = next((i for i, line in enumerate(lines) if re.fullmatch(r"['\"]?on['\"]?:", line)), None)
    if index is None:
        return None
    filters = {"paths": None, "paths-ignore": None}
    in_push, push_indent, key, key_indent = False, 0, None, 0
    for line in lines[index + 1:]:
        indent = len(line) - len(line.lstrip())
        if indent == 0:
            break
        text = line.strip()
        if not in_push:
            if text == "push:":
                in_push, push_indent = True, indent
            continue
        if indent <= push_indent:
            break
        if key and indent > key_indent and text.startswith("- "):
            filters[key].append(text[2:].strip().strip("'\""))
            continue
        name = text.rstrip(":")
        if text.endswith(":") and name in filters:
            key, key_indent = name, indent
            filters[key] = []
        elif text.endswith(":") or ":" in text:
            key = None
        else:
            return None
    if not in_push:
        return None
    return filters["paths"], filters["paths-ignore"]


def skip_reason(git, workflow, sha, base, base_proven=False):
    """Why this push never triggers the workflow, or None unless that is proven. No change at all proves nothing from an
    arbitrary base, but from a proven one it means an exact revert, whose inputs are what passed."""
    if not base:
        return None
    try:
        code, text, _ = git(["show", f"{sha}:.github/workflows/{workflow}"])
        if code != 0:
            return None
        filters = push_filters(text)
        if filters is None or (filters[0] is None and filters[1] is None) or None not in filters:
            return None
        code, _, _ = git(["merge-base", "--is-ancestor", base, sha])
        if code != 0:
            return None
        code, out, _ = git(["diff", "--name-only", "-z", "--no-renames", base, sha])
    except Unavailable:
        return None
    changed = [name for name in out.split("\0") if name]  # NUL separated, because Git quotes non-ASCII names otherwise
    if code != 0 or len(changed) > MAX_DIFF_FILES:
        return None
    if not changed:
        return "no file differs from the last proven commit" if base_proven else None
    paths, ignore = filters
    rules = [path_rule(p) for p in (paths if paths is not None else ignore)]
    if not rules or None in rules:
        return None
    matched = [any(rule.match(name) for rule in rules) for name in changed]
    if paths is not None and not any(matched):
        return f"none of the {len(changed)} changed files match its paths filter"
    if ignore is not None and all(matched):
        return f"all {len(changed)} changed files match its paths-ignore filter"
    return None


def workflow_status(runner, workflow, branch, sha=None, base=None, git=None, base_proven=False):
    scope = ["--commit", sha] if sha else ["--branch", branch]
    runs = gh_json(runner, ["run", "list", *scope, "--workflow", workflow, "--limit", str(LIMIT), "--json", FIELDS])
    if not isinstance(runs, list) or not all(isinstance(r, dict) for r in runs):
        raise Unavailable(f"gh run list for {workflow} did not return a list of runs")
    runs = sorted(runs, key=lambda r: str(r.get("createdAt") or ""), reverse=True)
    completed = [r for r in runs if r.get("status") == "completed"]
    latest = completed[0] if completed else None
    decisive = next((r for r in completed if verdict_of(r) != "cancelled"), None)
    newer = [r for r in runs if r.get("status") != "completed"
             and (decisive is None or str(r.get("createdAt") or "") > str(decisive.get("createdAt") or ""))]
    verdict = "pending" if newer else verdict_of(decisive)
    entry = {"workflow": workflow, "verdict": verdict, "latest_completed": brief(latest),
             "latest_completed_state": verdict_of(latest), "verdict_run": brief(decisive), "in_progress": len(newer)}
    if verdict == "none" and sha and git:
        # A commit already recorded as proven stays so, unless a real run has since said otherwise, as above.
        same = base_proven and sha == base
        reason = "this commit is already recorded as proven" if same else skip_reason(git, workflow, sha, base, base_proven)
        if reason:
            entry.update(verdict="skipped", reason=reason)
    if verdict == "red":
        entry["failed_jobs"] = failed_jobs(runner, decisive.get("databaseId"))
    return entry


def failed_jobs(runner, run_id):
    """The failed jobs of one run, or a one-line note when they cannot be read (the run is still red)."""
    try:
        view = gh_json(runner, ["run", "view", str(run_id), "--json", "jobs"])
    except Unavailable as exc:
        return [{"name": f"jobs unavailable: {exc}", "conclusion": None, "url": None}]
    jobs = view.get("jobs") if isinstance(view, dict) else None
    if not isinstance(jobs, list):
        return [{"name": "jobs unavailable: gh run view returned no job list", "conclusion": None, "url": None}]
    failed = [j for j in jobs if isinstance(j, dict) and verdict_of(j) == "red" and j.get("status") == "completed"]
    return [{"name": j.get("name"), "conclusion": j.get("conclusion"), "url": j.get("url")} for j in failed[:MAX_JOBS]]


def overall_exit(verdicts):
    """Red first, then unavailable, pending and none; 0 only when every workflow is green or skipped."""
    for verdict, code in (("red", EXIT_RED), ("unavailable", EXIT_UNAVAILABLE), ("pending", EXIT_PENDING),
                          ("none", EXIT_NONE)):
        if verdict in verdicts:
            return code
    return 0 if verdicts else EXIT_NONE


def report(runner, workflows, branch, sha=None, base=None, git=None, base_proven=False):
    """The report dict and its exit code. One workflow being unreadable doesn't hide another's red."""
    entries = []
    for workflow in workflows:
        try:
            entries.append(workflow_status(runner, workflow, branch, sha, base, git, base_proven))
        except Unavailable as exc:
            entries.append({"workflow": workflow, "verdict": "unavailable", "error": str(exc)})
    code = overall_exit([e["verdict"] for e in entries])
    result = {"branch": branch, "sha": sha, "base": base, "workflows": entries, "exit": code}
    errors = [e["error"] for e in entries if e.get("error")]
    if errors:
        result["error"] = errors[0]
    return result, code


_HELD = threading.local()


def lock_held(path):
    """Whether this thread holds the record's lock, which `write_gate` requires before it changes the record."""
    return str(path) in getattr(_HELD, "paths", ())


@contextlib.contextmanager
def gate_lock(path, wait=None):
    """Hold the record's lock file while changing the record, so two gates can't both read it and then both write.
    Yields True when held. After `wait` seconds without it, yields False: nothing can be saved then, because
    `write_gate` refuses the record without the lock, and every caller already handles a record that can't be saved.
    A lock left by a process that died is taken over once it is older than LOCK_STALE_SECONDS. Taking it again in the
    thread that holds it is allowed, so a locked change may call another."""
    if lock_held(path):
        yield True
        return
    lock = path.with_name(LOCK.name)
    deadline = time.monotonic() + (LOCK_WAIT_SECONDS if wait is None else wait)
    held = False
    while not held:
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            os.close(os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY))
            held = True
        except FileExistsError:
            try:
                if time.time() - lock.stat().st_mtime > LOCK_STALE_SECONDS:
                    lock.unlink()
                    continue
            except OSError:
                pass  # it went away or can't be read: try again
            if time.monotonic() >= deadline:
                break
            time.sleep(0.05)
        except OSError:
            break  # the folder can't be written, so there is nothing to lock and nothing to save either
    if held:
        _HELD.paths = {*getattr(_HELD, "paths", ()), str(path)}
    try:
        yield held
    finally:
        if held:
            _HELD.paths = {p for p in _HELD.paths if p != str(path)}
            try:
                lock.unlink()
            except OSError:
                pass


def confirmed_refusal(code, out, err):
    """True when GitHub itself answered the start with a refusal (an HTTP 4xx), so it is certain nothing was started."""
    return code != 0 and re.search(r"\bHTTP 4\d\d\b", f"{err}\n{out}") is not None


def remote_head(runner, branch):
    """The commit the branch points at on the remote. Unavailable when gh can't say, or says something that isn't a SHA."""
    try:
        code, out, err = runner(["api", f"repos/{{owner}}/{{repo}}/branches/{branch}", "--jq", ".commit.sha"])
    except FileNotFoundError:
        raise Unavailable("gh is not installed or not on PATH") from None
    if code != 0:
        reason = (err or out or "").strip().splitlines()
        raise Unavailable(f"gh api exited {code}: {(reason[0] if reason else 'no output')[:MAX_MESSAGE]}")
    head = out.strip()
    if not CANDIDATE.fullmatch(head):
        raise Unavailable("gh api did not return a commit for the branch")
    return head


def started_record(state, sha):
    """The workflows already started for this commit, from the gate's record, and under `unanswered` the ones whose
    start got no clear answer, each with what was said."""
    record = state.get("started")
    if isinstance(record, dict) and record.get("sha") == sha and isinstance(record.get("workflows"), list):
        unanswered = record.get("unanswered")
        return {"sha": sha, "workflows": list(record["workflows"]),
                "unanswered": dict(unanswered) if isinstance(unanswered, dict) else {}}
    return {"sha": sha, "workflows": [], "unanswered": {}}


def unanswered_text(name, why, branch):
    return (f"the start of {name} got no clear answer ({why}), so it may be running and won't be started again for "
            f"this commit; if no run appears: gh workflow run {name} --ref {branch}")


def recover(runner, result, branch, sha, gate, path, expired=lambda: False, git=None, hold=lambda: False):
    """What can be done about workflows with no run and no proof, added to the report. With `gate` it starts each one
    once, but only while the branch's remote head is this commit; while `hold()` says so it starts none yet and names
    them under `holding`, since the push's own run may be about to be listed. `hold` is asked after the remote head has
    been read, not before: that lookup can be slow, and a hold decided before it could outlast the grace (round 1). Verdicts, the exit code and the action are not its
    to change: a started run answers on a later look, through its own headSha."""
    missing = [e["workflow"] for e in result["workflows"] if e["verdict"] == "none"]
    for key in ("recovery", "started", "moved_to", "not_pushed", "recovery_error", "holding"):
        result.pop(key, None)
    if not missing:
        return
    if not sha:
        # Judging the branch itself, so whatever it holds is what should run.
        result["recovery"] = "; ".join(f"gh workflow run {name} --ref {branch}" for name in missing)
        return
    if expired():
        result["recovery_error"] = f"the wait ran out before the remote head of {branch} was read, so no run was started"
        return
    try:
        head = remote_head(runner, branch)
    except Unavailable as exc:
        result["recovery_error"] = f"the remote head of {branch} couldn't be read, so no run was started: {exc}"
        return
    if head != sha:
        # A remote head this commit descends from means the commit was never pushed, which is not the branch moving.
        behind = False
        if git:
            try:
                behind = git(["merge-base", "--is-ancestor", head, sha])[0] == 0
            except Unavailable:
                behind = False
        result["not_pushed" if behind else "moved_to"] = head
        return
    if not gate:
        result["recovery"] = "; ".join(f"gh workflow run {name} --ref {branch}" for name in missing)
        return
    if hold():
        result["holding"] = missing
        return
    errors = []
    for name in missing:
        if expired():
            errors.append(f"the wait ran out before {name} could be started")
            break
        # Reserved under the lock and before the start: two gates can't both find it unstarted, and a start that can't
        # be remembered would be repeated on every look.
        with gate_lock(path) as held:
            if not held:
                errors.append(f"another gate holds {LOCK.name}, so {name} was not started on this look")
                break
            state = read_gate(path)
            if state.get(HISTORY_LOST):
                # The record could not be read at some point, so whether this was already started is not known.
                errors.append(f"{path.name} could not be read since the last green, so what was already started for "
                              f"this commit is unknown and {name} was not started; if no run appears: "
                              f"gh workflow run {name} --ref {branch}")
                continue
            record = started_record(state, sha)
            if name in record["workflows"]:
                continue
            record["workflows"].append(name)
            state["started"] = record
            if not write_gate(path, state):
                errors.append(f"{path.name} couldn't be saved, so {name} was not started")
                break
        try:
            code, out, err = runner(["workflow", "run", name, "--ref", branch])
        except (Unavailable, FileNotFoundError) as exc:
            code, out, err = None, "", str(exc)
        if code == 0:
            print(f"recovery: started {name} on {branch} at {sha}", file=sys.stderr)
            continue
        reason = (err or out or "").strip().splitlines()
        why = (reason[0] if reason else "no output")[:MAX_MESSAGE]
        if code is not None and confirmed_refusal(code, out, err):
            # GitHub said no, so nothing runs: the reservation goes, and a later look may try again.
            with gate_lock(path) as held:
                state = read_gate(path)
                record = started_record(state, sha)
                record["workflows"] = [w for w in record["workflows"] if w != name]
                state["started"] = record
                write_gate(path, state)
            errors.append(f"{name} could not be started: gh workflow run exited {code}: {why}")
        else:
            # No answer either way: GitHub may have accepted it. Starting again could run it twice, so it stays recorded,
            # with what was said, so every later look and a restart still report that it was never confirmed.
            unsaved = True
            with gate_lock(path) as held:
                state = read_gate(path)
                record = started_record(state, sha)
                record["unanswered"][name] = why
                state["started"] = record
                unsaved = not write_gate(path, state)
            if unsaved:
                errors.append(unanswered_text(name, why, branch))
    record = started_record(read_gate(path), sha)
    # Reported for as long as the workflow still has no run, on this look and every later one.
    errors.extend(unanswered_text(name, why, branch) for name, why in record["unanswered"].items()
                  if name in missing and unanswered_text(name, why, branch) not in errors)
    if record["workflows"]:
        result["started"] = [name for name in record["workflows"] if name in missing]
    if errors:
        result["recovery_error"] = "; ".join(errors)


def read_gate(path):
    """The gate's record; an unreadable one reads as nothing known, with one line saying so. A cleared marker means a
    green that couldn't rewrite this file, so its attempts are over."""
    state = {}
    try:
        state = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(state, dict):
            raise ValueError("wrong shape")
    except FileNotFoundError:
        state = {}
    except (OSError, ValueError) as exc:
        print(f"warning: {path} is unreadable ({exc}); counting no repairs used", file=sys.stderr)
        # Nothing known is the right reading for repairs, but not for starts: a workflow may already have been started
        # for this commit. The flag travels with every save until a green replaces the record, and holds starts back.
        state = {HISTORY_LOST: True}
    marker = path.with_name(CLEARED.name)
    try:
        record = json.loads(marker.read_text(encoding="utf-8"))
        content = f"{record['proven']} {record['at']}"
    except FileNotFoundError:
        return state
    except OSError:
        return state
    except (ValueError, KeyError, TypeError) as exc:
        print(f"warning: ignoring an incomplete {marker} ({exc!r})", file=sys.stderr)
        return state
    if state.get("consumed") != content:  # honoured once, even if the marker can't be deleted
        state = {"proven": record["proven"], "consumed": content}
        # Taking the marker up changes the record, so it waits for a reader that holds the lock; until then every
        # reader sees what the marker says.
        if not lock_held(path) or not write_gate(path, state):
            return state
    elif not lock_held(path):
        return state
    try:
        marker.unlink()
    except OSError as exc:
        print(f"warning: could not remove {marker} ({exc}); it has been honoured and won't be again", file=sys.stderr)
    return state


def write_gate(path, state):
    """True when saved, by temporary file then replace. The record itself is saved only by the thread holding its
    lock: a change made without it could put back a copy that lacks what another gate has just written."""
    if path.name == REPAIRS.name and not lock_held(path):
        print(f"warning: {path.name} was not changed, because {LOCK.name} is held by another gate", file=sys.stderr)
        return False
    temporary = path.with_name(path.name + f".{os.getpid()}.tmp")
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary.write_text(json.dumps(state), encoding="utf-8")
        os.replace(temporary, path)
        return True
    except OSError as exc:
        print(f"warning: could not save {path} ({exc})", file=sys.stderr)
        try:
            temporary.unlink()
        except OSError:
            pass
        return False


def peek_proven(path):
    """The last proven commit, read without changing anything, honouring a cleared marker the gate hasn't taken up yet."""
    state, problem = {}, None
    try:
        state = json.loads(path.read_text(encoding="utf-8"))
        state = state if isinstance(state, dict) else {}
    except FileNotFoundError:
        pass
    except (OSError, ValueError) as exc:
        problem = exc  # the marker exists for just this case, so look there before giving up
    try:
        marker = json.loads(path.with_name(CLEARED.name).read_text(encoding="utf-8"))
        if isinstance(marker, dict) and isinstance(marker.get("proven"), str) and "at" in marker \
                and state.get("consumed") != f"{marker['proven']} {marker['at']}":
            return marker["proven"]
    except (OSError, ValueError):
        pass  # no usable marker, so the record decides
    if problem:
        print(f"warning: {path} can't be read ({problem}), so nothing counts as proven", file=sys.stderr)
    proven = state.get("proven")
    return proven if isinstance(proven, str) else None


def begin_wait(path, sha, now):
    """Record when waiting on this commit began, before any polling, so a killed process loses none of it. Returns that
    time, or now when it can't be saved."""
    with gate_lock(path):
        return _begin_wait(path, sha, now)


def _begin_wait(path, sha, now):
    state = read_gate(path)
    waiting = state.get("waiting")
    # The pid says which gate is polling now: scripts/lib/run_guard.py holds the run's next row only while it lives.
    if isinstance(waiting, dict) and waiting.get("sha") == sha and isinstance(waiting.get("since"), (int, float)):
        if waiting.get("pid") != os.getpid():
            waiting["pid"] = os.getpid()
            write_gate(path, state)
        return waiting["since"]
    state["waiting"] = {"sha": sha, "since": now, "pid": os.getpid()}
    write_gate(path, state)
    return now


def gate_action(code, sha, path, now):
    """One action for a pushed commit, and any attempt number, or why it is unverified. A repeat check of the same red
    commit uses no attempt."""
    with gate_lock(path):
        return _gate_action(code, sha, path, now)


def _gate_action(code, sha, path, now):
    state = read_gate(path)
    if code == 0:
        cleared = {"proven": sha, **({"consumed": state["consumed"]} if "consumed" in state else {})}
        if write_gate(path, cleared) or write_gate(path.with_name(CLEARED.name), {"proven": sha, "at": now}):
            return "proceed", None
        print("warning: could not clear the repair count; this commit passed, but carry on as unverified", file=sys.stderr)
        return "unverified", f"this commit passed, but neither {path.name} nor {CLEARED.name} could be saved"
    if code != EXIT_RED:
        waiting = state.get("waiting")
        if not isinstance(waiting, dict) or waiting.get("sha") != sha or not isinstance(waiting.get("since"), (int, float)):
            waiting = {"sha": sha, "since": now}
        state["waiting"] = waiting
        if not write_gate(path, state):
            return "unverified", f"no answer from CI, and {path.name} couldn't be saved to count the wait"
        waited = now - waiting["since"]
        if waited >= WAIT_LIMIT_SECONDS:
            return "unverified", f"no answer from CI after {int(waited)} seconds, the limit being {WAIT_LIMIT_SECONDS}"
        return "wait", None
    state.pop("waiting", None)
    if not isinstance(state.get("first_red"), str) or not isinstance(state.get("seen"), list):
        state.update(first_red=sha, seen=[])
    if sha not in state["seen"]:
        state["seen"].append(sha)
    if not write_gate(path, state):
        # A repair that can't be counted could repeat forever.
        return "unverified", f"CI is red, and {path.name} couldn't be saved to count repairs"
    repairs_tried = len(state["seen"]) - 1
    if repairs_tried >= MAX_REPAIRS:
        return "exhausted", repairs_tried
    return "repair", repairs_tried + 1


def describe(entry):
    if entry["verdict"] == "unavailable":
        return [f"{entry['workflow']}: unavailable: {entry['error']}"]
    if entry["verdict"] == "skipped":
        return [f"{entry['workflow']}: skipped: {entry['reason']}"]
    run = entry["verdict_run"]
    where = f" at {run['headSha'][:12]} ({run['url']})" if run and run.get("headSha") and run.get("url") else ""
    lines = [f"{entry['workflow']}: {entry['verdict']}{where}"]
    if entry["latest_completed_state"] == "cancelled":
        lines.append(f"  latest completed run was cancelled: {entry['latest_completed']['url']}")
    if entry["in_progress"]:
        lines.append(f"  {entry['in_progress']} newer run(s) still queued or in progress")
    for job in entry.get("failed_jobs", []):
        lines.append(f"  failed job: {job['name']} ({job['conclusion']}) {job['url'] or ''}".rstrip())
    return lines


def read_head(root):
    """The commit HEAD names, read from the `.git` files with no process, or None when that can't be told here."""
    try:
        git_dir = Path(root) / ".git"
        if git_dir.is_file():  # a linked worktree points at its own folder
            git_dir = (Path(root) / git_dir.read_text(encoding="utf-8").split("gitdir:", 1)[1].strip()).resolve()
        common = git_dir
        if (git_dir / "commondir").is_file():
            common = (git_dir / (git_dir / "commondir").read_text(encoding="utf-8").strip()).resolve()
        head = (git_dir / "HEAD").read_text(encoding="utf-8").strip()
        if CANDIDATE.fullmatch(head):
            return head
        if not head.startswith("ref: "):
            return None
        ref = head[5:].strip()
        for folder in (git_dir, common):
            if (folder / ref).is_file():
                value = (folder / ref).read_text(encoding="utf-8").strip()
                return value if CANDIDATE.fullmatch(value) else None
        for line in (common / "packed-refs").read_text(encoding="utf-8").splitlines():
            parts = line.split()
            if len(parts) == 2 and parts[1] == ref and CANDIDATE.fullmatch(parts[0]):
                return parts[0]
    except (OSError, IndexError, ValueError):
        pass  # the caller falls back to asking git
    return None


def resolve_commit(git, ref):
    """The full SHA of a commit, or None when it isn't one. Unavailable means git couldn't say, which is different."""
    code, out, _ = git(["rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"])
    return out.strip() if code == 0 and out.strip() else None


def main(argv=None, runner=run_gh, root=ROOT, git=None, clock=time.monotonic, sleep=time.sleep, wall=time.time):
    root = Path(root)
    git_default = git is None
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--json", action="store_true", help="print the report as one JSON object")
    parser.add_argument("--branch", default="main", help="the branch whose runs are read (default main)")
    parser.add_argument("--workflow", action="append", dest="workflows",
                        help="a workflow file in .github/workflows/ (repeatable; default build.yml and plan.yml)")
    parser.add_argument("--sha", help="judge this commit's own runs rather than the branch")
    parser.add_argument("--base", help="the remote head before the push, needed to prove a workflow was skipped")
    parser.add_argument("--use-proven", action="store_true", help="read only: with --sha, prove a skip against the recorded proven commit")
    parser.add_argument("--gate", action="store_true", help="also print one action: proceed, wait, repair, exhausted or unverified")
    parser.add_argument("--wait-minutes", type=float, default=0, help="with --gate, poll this long while the action is wait")
    args = parser.parse_args(argv)
    directory = root / ".github" / "workflows"
    workflows = args.workflows or [w for w in WORKFLOWS if (directory / w).is_file()]
    missing = [w for w in workflows if "/" in w or "\\" in w or not (directory / w).is_file()]
    if missing or not workflows or not args.branch.strip():
        parser.error(f"no such workflow in .github/workflows/: {', '.join(missing)}" if missing
                     else "no workflow to watch, or an empty --branch")
    if args.gate and not args.sha:
        parser.error("--gate needs --sha")
    if args.gate and args.sha != "HEAD" and not CANDIDATE.fullmatch(args.sha):
        parser.error("--gate takes --sha HEAD or a full SHA, because a wait is recorded by commit")
    if args.gate and args.workflows:
        parser.error("--gate judges every gating workflow, so a pass means that, and it takes no --workflow")
    if args.base and not args.sha:
        parser.error("--base needs --sha")
    if args.use_proven and (not args.sha or args.gate or args.base):
        parser.error("--use-proven needs --sha, and takes no --gate or --base")
    if args.base and args.gate:
        parser.error("--gate proves a skip only against the last proven commit, so it takes no --base")
    if args.wait_minutes and not args.gate:
        parser.error("--wait-minutes needs --gate")
    if args.wait_minutes < 0:
        parser.error("--wait-minutes can't be negative")
    started = clock()
    deadline = started + args.wait_minutes * 60 if args.wait_minutes else float("inf")  # the two hour limit may lower it

    def remaining():
        return max(deadline - clock(), 0)

    def bounded(tool, default=120):
        """A tool call may not outlast the deadline, and none starts once it has passed."""
        def call(arguments):
            left = remaining()
            if left <= 0:
                raise Unavailable("the wait ran out")
            return tool(arguments, min(left, default))
        return call

    ask = bounded(run_gh) if runner is run_gh else runner
    # The git runner stays read-only here, and test runners take only arguments.
    git_call = bounded(lambda arguments, timeout: run_tool("git", arguments, cwd=root, timeout=timeout)) if git_default else git
    sha = base = None
    if args.sha:
        name = bounded(lambda arguments, timeout: run_tool("git", arguments, cwd=root, timeout=timeout), RESOLVE_SECONDS) if git_default else git
        recorded = read_gate(root / REPAIRS).get("waiting") if args.gate else None
        if isinstance(recorded, dict) and isinstance(recorded.get("since"), (int, float)):
            # Which wait applies isn't known yet, so any recorded one bounds this look.
            deadline = min(deadline, clock() + max(WAIT_LIMIT_SECONDS - (wall() - recorded["since"]), 0))
        try:
            # A full SHA needs no looking up, so a new commit always gets its own wait.
            direct = args.sha if CANDIDATE.fullmatch(args.sha) else read_head(root) if git_default and args.sha == "HEAD" else None
            sha = direct or resolve_commit(name, args.sha)
        except Unavailable as exc:
            result = {"branch": args.branch, "sha": None, "base": None, "workflows": [], "exit": EXIT_UNAVAILABLE, "error": str(exc)}
            if args.gate:
                # HEAD couldn't be read at all, so a wait already recorded carries on rather than restarting. With none,
                # it is keyed by what was asked for, so the two hour limit still applies if git stays unavailable.
                key = recorded["sha"] if isinstance(recorded, dict) and isinstance(recorded.get("sha"), str) else args.sha
                begin_wait(root / REPAIRS, key, wall())
                result["action"], why = gate_action(EXIT_UNAVAILABLE, key, root / REPAIRS, wall())
                if why:
                    result["reason"] = why
            print(json.dumps(result, indent=2) if args.json else f"unavailable: {exc}" + (f"\nreason: {result['reason']}" if result.get("reason") else "") + (f"\naction: {result['action']}" if args.gate else ""))
            return EXIT_UNAVAILABLE
        if not sha:
            parser.error(f"--sha is not a commit here: {args.sha}")
    if args.gate:
        # Recorded before the first lookup, so a process killed during it keeps the time. The two hour limit counts from
        # here, and one invocation can't run past it. A verdict clears it.
        left = WAIT_LIMIT_SECONDS - (wall() - begin_wait(root / REPAIRS, sha, wall()))
        # A new commit's wait starts fresh, so an older commit's limit no longer applies once the commit is known.
        deadline = (started + args.wait_minutes * 60 if args.wait_minutes else float("inf"))
        deadline = min(deadline, clock() + max(left, 0))
        if left <= 0:  # the limit has passed, so this launches nothing more
            action, why = gate_action(EXIT_UNAVAILABLE, sha, root / REPAIRS, wall())
            result = {"branch": args.branch, "sha": sha, "base": None, "workflows": [], "exit": EXIT_UNAVAILABLE, "action": action, "reason": why}
            print(json.dumps(result, indent=2) if args.json else f"reason: {why}\naction: {action}")
            return EXIT_UNAVAILABLE
    proven_commit = peek_proven(root / REPAIRS) if args.use_proven else (read_gate(root / REPAIRS).get("proven") if args.gate else None)
    wanted = args.base or proven_commit
    if isinstance(wanted, str):
        # An unknown base can't prove a skip, but it mustn't stop the commit's own runs being read.
        try:
            base = resolve_commit(git_call, wanted)
        except Unavailable:
            base = None
    base_proven = bool(base) and not args.base
    result, code = report(ask, workflows, args.branch, sha, base, git_call, base_proven)

    def hold_left():
        """Seconds a missing workflow's start is still held, giving the push's own run time to be listed; 0 once the
        grace has passed, and 0 from the start when the wait leaves no room for it: with no wait there is no later
        look, and a wait no longer than the grace (and one look) would run out with nothing started."""
        left = started + START_GRACE_SECONDS - clock()
        if not (args.gate and args.wait_minutes) or left <= 0 or remaining() <= left + GRACE_POLL_SECONDS:
            return 0
        return left

    held, held_for, held_since = [], 0.0, None

    def look_after():
        """A missing run is started, held or explained on this look, and what was held is remembered for the report:
        the time between a look that held and the next look, added up, so a hold that a failed look interrupts and a
        later one resumes counts for all of it (completion review)."""
        nonlocal held_for, held_since
        if held_since is not None:
            held_for += clock() - held_since
        recover(ask, result, args.branch, sha, args.gate, root / REPAIRS, lambda: remaining() <= 0, git_call, hold=lambda: hold_left() > 0)
        for name in result.get("holding", []):
            if name not in held:
                held.append(name)
        held_since = clock() if result.get("holding") else None

    # At the first look: a missing run is held for the grace or started now, or the reason it can't be is known now.
    look_after()
    # A commit the branch has left, with no run that can still answer for it (its runs cancelled by a newer push, or
    # none), stays unanswered however long this waits: say where the branch went now, the commit to gate instead.
    while (args.gate and args.wait_minutes and code in (EXIT_UNAVAILABLE, EXIT_PENDING, EXIT_NONE) and remaining() > 0
           and not result.get("moved_to")):
        states = ", ".join(f"{e['workflow']} {e['verdict']}" for e in result["workflows"])
        holding = result.get("holding")
        note = (f"; holding the start of {', '.join(holding)} for {int(hold_left())}s more in case the push's own run is "
                "about to be listed") if holding else ""
        print(f"waiting: {states}; {int(clock() - started)}s elapsed, {int(remaining())}s left{note}", file=sys.stderr)
        # While a start is held the gate looks often, so the push's run is seen as soon as it is listed.
        # A held start that has come due meanwhile is looked at again at once, never after a whole poll.
        sleep(min(min(GRACE_POLL_SECONDS, hold_left()) if holding else POLL_SECONDS, remaining()))
        if remaining() <= 0:  # no tool may start now, so the last real answer stands
            break
        result, code = report(ask, workflows, args.branch, sha, base, git_call, base_proven)
        look_after()
    if held_since is not None:  # the wait ran out while a start was still held
        held_for += clock() - held_since
    result.pop("holding", None)
    if held:
        result["start_held"] = {"seconds": int(held_for), "workflows": sorted(held)}
    if args.gate:
        action, attempt = gate_action(code, sha, root / REPAIRS, wall())
        result["action"] = action
        if action == "unverified":
            result["reason"] = attempt
        elif attempt is not None:
            result["repair_attempt" if action == "repair" else "repairs_tried"] = attempt
    if args.json:
        print(json.dumps(result, indent=2))
    else:
        for entry in result["workflows"]:
            print("\n".join(describe(entry)))
        if result.get("recovery"):
            # The preflight reads this line and passes it on as the fix.
            print(f"to get a real run: {result['recovery']}")
        if result.get("started"):
            print(f"started for this commit: {', '.join(result['started'])}")
        if result.get("moved_to"):
            print(f"{args.branch} has moved on to {result['moved_to']}: gate that commit (--sha {result['moved_to']})")
        if result.get("not_pushed"):
            print(f"this commit is not on the remote yet ({args.branch} there is at {result['not_pushed']}): push it, then gate it")
        if result.get("recovery_error"):
            print(f"no run was started: {result['recovery_error']}")
        if args.gate:
            if result.get("reason"):
                print(f"reason: {result['reason']}")
            detail = f" (attempt {attempt} of {MAX_REPAIRS})" if result["action"] == "repair" else ""
            print(f"action: {result['action']}{detail}")
    return code


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
