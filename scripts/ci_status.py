#!/usr/bin/env python3
"""Report the CI state of main or of one pushed commit, and what a run should do next.

    python scripts/ci_status.py                              # build.yml and plan.yml on main
    python scripts/ci_status.py --json                       # the same report as one JSON object
    python scripts/ci_status.py --workflow build.yml --branch main
    python scripts/ci_status.py --sha HEAD --base <commit>   # did this commit pass, or was it rightly not built?
    python scripts/ci_status.py --gate --sha HEAD --base <commit> --wait-minutes 30   # also print one action

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
first red commit, when waiting began, and the last proven commit. That commit is the base when `--base` is omitted, so a
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


def skip_reason(git, workflow, sha, base):
    """Why this push never triggers the workflow, or None unless that is proven."""
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
    if code != 0 or not changed or len(changed) > MAX_DIFF_FILES:
        return None
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


def workflow_status(runner, workflow, branch, sha=None, base=None, git=None):
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
        reason = skip_reason(git, workflow, sha, base)
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


def report(runner, workflows, branch, sha=None, base=None, git=None):
    """The report dict and its exit code. One workflow being unreadable doesn't hide another's red."""
    entries = []
    for workflow in workflows:
        try:
            entries.append(workflow_status(runner, workflow, branch, sha, base, git))
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


def wait_since(path, sha):
    """When waiting on this commit began, or None."""
    waiting = read_gate(path).get("waiting")
    if isinstance(waiting, dict) and waiting.get("sha") == sha and isinstance(waiting.get("since"), (int, float)):
        return waiting["since"]
    return None


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
    """One action for a pushed commit, and any attempt number. A repeat check of the same red commit uses no attempt."""
    state = read_gate(path)
    if code == 0:
        cleared = {"proven": sha, **({"consumed": state["consumed"]} if "consumed" in state else {})}
        if write_gate(path, cleared) or write_gate(path.with_name(CLEARED.name), {"proven": sha, "at": now}):
            return "proceed", None
        print("warning: could not clear the repair count; this commit passed, but carry on as unverified", file=sys.stderr)
        return "unverified", None
    if code != EXIT_RED:
        waiting = state.get("waiting")
        if not isinstance(waiting, dict) or waiting.get("sha") != sha or not isinstance(waiting.get("since"), (int, float)):
            waiting = {"sha": sha, "since": now}
        state["waiting"] = waiting
        if not write_gate(path, state):
            return "unverified", None
        return ("unverified", None) if now - waiting["since"] >= WAIT_LIMIT_SECONDS else ("wait", None)
    state.pop("waiting", None)
    if not isinstance(state.get("first_red"), str) or not isinstance(state.get("seen"), list):
        state.update(first_red=sha, seen=[])
    if sha not in state["seen"]:
        state["seen"].append(sha)
    if not write_gate(path, state):
        # A repair that can't be counted could repeat forever.
        return "unverified", None
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


def resolve_commit(git, ref):
    """The full SHA of a commit, or None."""
    try:
        code, out, _ = git(["rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"])
    except Unavailable:
        return None
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
    if args.base and not args.sha:
        parser.error("--base needs --sha")
    if args.wait_minutes and not args.gate:
        parser.error("--wait-minutes needs --gate")
    if args.wait_minutes < 0:
        parser.error("--wait-minutes can't be negative")
    started = clock()
    deadline = started + args.wait_minutes * 60

    def remaining():
        return max(deadline - clock(), 0)

    def bounded(tool, default=120):
        """A tool call may not outlast the wait, and none starts once it has passed. Without a wait, tools keep their limit."""
        def call(arguments):
            if not args.wait_minutes:
                return tool(arguments, default)
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
        sha = resolve_commit(git_call, args.sha)
        if not sha:
            parser.error(f"--sha is not a commit here: {args.sha}")
    if args.gate:
        since = wait_since(root / REPAIRS, sha)
        if since is not None:
            left = WAIT_LIMIT_SECONDS - (wall() - since)
            deadline = min(deadline, clock() + max(left, 0))
            if left <= 0:  # the limit has passed, so this launches nothing more
                action, _ = gate_action(EXIT_UNAVAILABLE, sha, root / REPAIRS, wall())
                result = {"branch": args.branch, "sha": sha, "base": None, "workflows": [], "exit": EXIT_UNAVAILABLE, "action": action}
                print(json.dumps(result, indent=2) if args.json else f"action: {action}")
                return EXIT_UNAVAILABLE
    if args.base:
        # An unknown base can't prove a skip, but it mustn't stop the commit's own runs being read.
        base = resolve_commit(git_call, args.base)
    elif args.gate:
        proven = read_gate(root / REPAIRS).get("proven")
        base = resolve_commit(git_call, proven) if isinstance(proven, str) else None
    result, code = report(ask, workflows, args.branch, sha, base, git_call)
    if args.gate and code in (EXIT_UNAVAILABLE, EXIT_PENDING, EXIT_NONE):
        # The two hour limit counts from here, and one invocation can't run past it.
        since = begin_wait(root / REPAIRS, sha, wall())
        deadline = min(deadline, clock() + max(WAIT_LIMIT_SECONDS - (wall() - since), 0))
    while args.gate and code in (EXIT_UNAVAILABLE, EXIT_PENDING, EXIT_NONE) and remaining() > 0:
        states = ", ".join(f"{e['workflow']} {e['verdict']}" for e in result["workflows"])
        print(f"waiting: {states}; {int(clock() - started)}s elapsed, {int(remaining())}s left", file=sys.stderr)
        sleep(min(POLL_SECONDS, remaining()))
        result, code = report(ask, workflows, args.branch, sha, base, git_call)
    if args.gate:
        action, attempt = gate_action(code, sha, root / REPAIRS, wall())
        result["action"] = action
        if attempt is not None:
            result["repair_attempt" if action == "repair" else "repairs_tried"] = attempt
    if args.json:
        print(json.dumps(result, indent=2))
    else:
        for entry in result["workflows"]:
            print("\n".join(describe(entry)))
        if args.gate:
            detail = f" (attempt {attempt} of {MAX_REPAIRS})" if result["action"] == "repair" else ""
            print(f"action: {result['action']}{detail}")
    return code


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
