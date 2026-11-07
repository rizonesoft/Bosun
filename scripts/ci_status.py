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
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
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
WAIT_LIMIT_SECONDS = 2 * 60 * 60
REPAIRS = Path("build") / "ci-gate.json"
CLEARED = Path("build") / "ci-gate.cleared"
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
        state = {}
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
        if not write_gate(path, state):
            return state
    try:
        marker.unlink()
    except OSError as exc:
        print(f"warning: could not remove {marker} ({exc}); it has been honoured and won't be again", file=sys.stderr)
    return state


def write_gate(path, state):
    """True when saved, by temporary file then replace."""
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
    state = read_gate(path)
    waiting = state.get("waiting")
    if isinstance(waiting, dict) and waiting.get("sha") == sha and isinstance(waiting.get("since"), (int, float)):
        return waiting["since"]
    state["waiting"] = {"sha": sha, "since": now}
    write_gate(path, state)
    return now


def gate_action(code, sha, path, now):
    """One action for a pushed commit, and any attempt number, or why it is unverified. A repeat check of the same red
    commit uses no attempt."""
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
    while args.gate and args.wait_minutes and code in (EXIT_UNAVAILABLE, EXIT_PENDING, EXIT_NONE) and remaining() > 0:
        states = ", ".join(f"{e['workflow']} {e['verdict']}" for e in result["workflows"])
        print(f"waiting: {states}; {int(clock() - started)}s elapsed, {int(remaining())}s left", file=sys.stderr)
        sleep(min(POLL_SECONDS, remaining()))
        result, code = report(ask, workflows, args.branch, sha, base, git_call, base_proven)
    if args.gate:
        missing = [e["workflow"] for e in result["workflows"] if e["verdict"] == "none"]
        if missing:
            # With no run and no proof, as after a fresh clone, a real run of this commit is the way to find out.
            result["recovery"] = "; ".join(f"gh workflow run {name} --ref {args.branch}" for name in missing)
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
        if args.gate:
            if result.get("recovery"):
                print(f"to get a real run: {result['recovery']}")
            if result.get("reason"):
                print(f"reason: {result['reason']}")
            detail = f" (attempt {attempt} of {MAX_REPAIRS})" if result["action"] == "repair" else ""
            print(f"action: {result['action']}{detail}")
    return code


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
