"""Keeps an unattended `process plan` run going until the plan says it is done.

A run can end three ways, and each has its own catch:

- The session ends its turn: the Stop hook sends it back while runnable rows remain.
- The session sits idle (an API error, a yield): a CronCreate tick in that session wakes it.
- The session dies (a crash, a reboot): the scheduled `watch` resumes it, never beside a live session.

The prompt switches the run on, so no one has to remember to. Only the operator or the plan ends it: an
operator stop, a normal exit, no runnable rows left, or a recorded halt. Stalling (no commit and no change to
the tree across many attempts) also ends it, so a stuck run cannot spend without limit. Fails open: a broken
guard must never wedge a session.
"""

from __future__ import annotations

import calendar
import hashlib
import html
import json
import math
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from contextlib import contextmanager
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Callable, Iterator

ROOT = Path(__file__).resolve().parents[2]
STATE = Path(os.environ.get("RUN_GUARD_STATE") or ROOT / ".git" / "run-guard" / "state.json")
TASK_NAME = f"{ROOT.name} run watchdog"
WATCH_MINUTES = 10
CRON = "7,22,37,52 * * * *"

MAX_IDLE_BLOCKS = 5  # sends back in a row with nothing changed, before the turn may end
MAX_YIELDS = 3  # those yields in a row with nothing changed, before the run is called stalled
MAX_RESTARTS = 3  # resumes of a dead session with nothing changed, before the run is called stalled
MAX_QUIET_HOURS = 72  # a run this long without a change has been walked away from; it never resumes itself
CHILD_HANG_HOURS = 3  # a resumed session this long without a change is stopped and replaced, unless it's in the build queue
MAX_FRESH_STARTS = 1  # a run whose session lost its transcript starts afresh once; a second loss ends it
TRANSCRIPT_GONE = re.compile(r"No conversation found with session ID", re.I)
CHILD_OUTPUT_LIMIT = 64 * 1024
HOOK_FAILURES = "guard-health.json"
HOOK_FAILURES_KEPT = 10
HEARTBEAT_RENEW_DAYS = 6  # CronCreate jobs expire after 7 days
LOCK_WAIT = 5  # seconds, well under every hook's timeout: slow reads happen before the lock, see snapshot

# Waits for what only time or the owner can fix. The owner chose subscription only, so a limit costs time, never money.
USAGE_WAIT = 30 * 60  # a usage limit that gives no reset time is looked at again after this
SIGN_IN_WAIT = 30 * 60
OVERLOADED_WAIT = 5 * 60
AUTH_TIMEOUT = 30  # seconds for `claude auth status`
SAVE_TRIES = 3
PENDING = "pending-wait.json"  # a wait the state file couldn't take, kept beside it until the next tick merges it
LONGEST_RESET = 8 * 86400  # a weekly limit's reset is the furthest a real one goes; anything past it is misread
# StopFailure's `error` (https://code.claude.com/docs/en/hooks), by what the run does about it.
FAILURE_CLASSES = {
    "rate_limit": "usage-limit",
    "authentication_failed": "auth", "cloud_credential_error": "auth",
    "oauth_org_not_allowed": "auth", "account_on_hold": "auth", "billing_error": "auth",
    "overloaded": "overloaded", "server_error": "overloaded",
}
ACCOUNT_ERRORS = {"oauth_org_not_allowed", "account_on_hold", "billing_error"}
# What a resumed session must never see, so it can only run on the subscription. The sign-in check below covers the
# sources an environment can't, such as an apiKeyHelper or a key in a settings file.
SCRUBBED = ("ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY",
            "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY")

SUBSCRIPTION_SIGN_INS = ("claude.ai", "oauth_token")  # `claude auth status --json` authMethod values, 2026-11-08
KNOWN_MODES = ("acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan")  # claude --help, 2026-11-08
CAMPAIGN_MODE = "bypassPermissions"  # the owner's choice: a hidden session can't answer a prompt

DRAINED = "no runnable rows left; that is not the plan being done, `todo-graph.py query stats` shows what is blocked or unstamped"
GROUNDS = ("data-loss", "credentials", "shared-infrastructure", "wrong-calculation", "operator")

START = re.compile(r"^\s*(please\s+)?/?process[- ](the\s+)?plan\s*[.!]?\s*$", re.IGNORECASE)
STOP = re.compile(
    r"^\s*(please\s+)?(stop|pause|halt)((\s+(the|this|that|it|run|plan|process|work|working|now|everything|cleanly|safely|and|here))*)\s*[.!]?\s*$",
    re.IGNORECASE,
)
MENTIONS_STOP = re.compile(r"\b(stop|pause|halt|cancel|abort|quit)\b", re.IGNORECASE)
USER_EXITS = {"prompt_input_exit", "logout", "clear"}  # a crash or a killed process reports none of these


@dataclass
class Env:
    """What the guard reads from the world, so tests can stand in for it."""

    rows: Callable[[], list[str] | None]
    signature: Callable[[], str]
    alive: Callable[[str], bool]
    # The sessions in this checkout, each {"id", "status"} from Claude Code's own record (a bare id: status unknown).
    live_here: Callable[[], list]
    child_alive: Callable[[dict], bool]
    spawn: Callable[[dict], dict]
    task_installed: Callable[[], bool] = lambda: True
    # The modes `claude --permission-mode` accepts, as its --help lists them.
    modes: Callable[[], set] = lambda: set(KNOWN_MODES)
    # What a run with no runnable row does next: {"state": complete | parked | waiting | ci-pending | ci-unavailable
    # | needs-push | needs-ci-repair | unknown, "detail": str, "report": the acceptance text}.
    finish: Callable[[], dict] = lambda: {"state": "drained"}
    # The wakes in todo/wakes.json whose check passes now (read only).
    wakes: Callable[[], list] = lambda: []
    # Write the acceptance file for a run that ends complete or parked, returning its path; OSError when it can't.
    write_acceptance: Callable[[str, str], str] = lambda kind, text: ""
    # Which review is in flight now, or None.
    reviewing: Callable[[], object] = lambda: None
    # (ok, reason, detail): whether a launch now would run on the subscription.
    subscription: Callable[[], tuple] = lambda: (True, "", "")
    # The child's process tree, each {"pid", "started", "name", "command"}, the child first.
    tree: Callable[[dict], list] = lambda child: []
    # Stop the child's tree, returning the pids of the listed processes still running afterwards.
    stop_tree: Callable[[dict, list], list] = lambda child, procs: []
    # What the child wrote since it was launched.
    child_output: Callable[[dict], str] = lambda child: ""
    # A hung child's stop, already tried before the lock was taken (see `stop_attempt`), or None.
    stopped: Callable[[dict], dict | None] = lambda child: None
    # Which commit a CI gate is polling now, or None (`ci_in_flight`).
    ci_waiting: Callable[[], object] = lambda: None


def stamp(now: float | None = None) -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(now if now is not None else time.time()))


def load(path: Path = STATE) -> dict:
    """The run's state. A file that cannot be read raises: unreadable is not the same as no run."""
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError:
        return {}
    state = json.loads(text)
    if not isinstance(state, dict):
        raise ValueError(f"{path} does not hold a run state")
    return state


@contextmanager
def locked(path: Path = STATE, wait: float = LOCK_WAIT) -> Iterator[None]:
    """Every change to the state goes through here, held across load to save, so a tick cannot undo a stop."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.with_name(path.name + ".lock").open("a+b") as handle:
        deadline = time.monotonic() + wait
        while True:
            try:
                if os.name == "nt":
                    import msvcrt

                    handle.seek(0)
                    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl

                    fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise TimeoutError(f"{path.name} stayed locked for {wait} seconds") from None
                time.sleep(0.1)
        try:
            yield
        finally:
            if os.name == "nt":
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)


def save(state: dict, path: Path = STATE) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    tmp.write_text(json.dumps(state, indent=2), encoding="utf-8")
    os.replace(tmp, path)


def save_retrying(state: dict, path: Path = STATE, pause: float = 0.5) -> None:
    """A save that a moment's lock (an antivirus scan, a backup) doesn't defeat."""
    for attempt in range(SAVE_TRIES):
        try:
            save(state, path)
            return
        except OSError:
            if attempt == SAVE_TRIES - 1:
                raise
            time.sleep(pause * (attempt + 1))


def keep_pending_wait(state: dict, path: Path) -> None:
    """When the state can't be saved, the wait alone goes to a small file of its own, which the next tick merges."""
    pending = {k: state[k] for k in ("session_id", "wait_until", "wait_reason", "wait_detail", "wait_set") if k in state}
    target = path.with_name(PENDING)
    for attempt in range(SAVE_TRIES):
        try:
            tmp = target.with_name(f"{target.name}.{os.getpid()}.tmp")
            tmp.write_text(json.dumps(pending), encoding="utf-8")
            os.replace(tmp, target)
            return
        except OSError:
            if attempt == SAVE_TRIES - 1:
                raise
            time.sleep(0.5 * (attempt + 1))


def merge_pending_wait(state: dict, path: Path, now: float) -> None:
    """Take up a wait the hook couldn't save into the state, if it is this run's and still ahead."""
    target = path.with_name(PENDING)
    try:
        pending = json.loads(target.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return
    except (OSError, ValueError):
        pending = None
    if isinstance(pending, dict) and state.get("active") and pending.get("session_id") == state.get("session_id") \
            and isinstance(pending.get("wait_until"), (int, float)) and pending["wait_until"] > now \
            and pending["wait_until"] > (state.get("wait_until") or 0):
        state.update({k: v for k, v in pending.items() if k.startswith("wait_")})
    target.unlink(missing_ok=True)


def log(line: str, path: Path = STATE) -> None:
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.with_name("run-guard.log").open("a", encoding="utf-8") as out:
            out.write(f"{stamp()} {line}\n")
    except OSError:
        pass


def note(state: dict, line: str) -> None:
    """A line for the guard log, written by the tick that saves this state."""
    state.setdefault("log_lines", []).append(line)


def holding_sessions(live: list, owner: str | None) -> list[str]:
    """The live sessions that hold this tree: the run's own, whatever it is doing, and any other not `idle` (a status
    that can't be read counts as busy, since not knowing is not the same as idle)."""
    found = []
    for session in live:
        sid, status = (session.get("id"), session.get("status")) if isinstance(session, dict) else (session, None)
        if sid == owner:
            found.append(str(sid))
        elif status != "idle":
            found.append(f"{sid} ({status or 'status unknown'})")
    return found


def in_build_queue(proc: dict) -> bool:
    """A `bosun run` process holds a build slot or waits for one. On Windows the snapshot has no command line, and
    nothing else in a session's tree runs bosun.exe for hours, so its image name is enough."""
    name = str(proc.get("name") or "").lower()
    return name in ("bosun.exe", "bosun") or bool(re.search(r"\bbosun(\.exe)?\s+run\b", str(proc.get("command") or "")))


def hung(state: dict, signature: str, now: float) -> bool:
    """A child that has changed nothing for CHILD_HANG_HOURS since the later of its launch and the last change."""
    if signature != state.get("restart_signature"):
        return False
    return (now - max(state.get("changed_at", now), state.get("launched_at", 0))) / 3600 >= CHILD_HANG_HOURS


def stop_attempt(child: dict, env: Env) -> dict:
    """Try to stop a hung child's tree: {"queued": a bosun pid, or "gone": True, or "survivors" and "count"}. Slow
    (a stop can take a minute), so the watchdog does it before taking the lock, where a `stop` prompt can't wait."""
    procs = env.tree(child)
    queued = [p for p in procs if in_build_queue(p)]
    if queued:
        return {"child": child, "queued": queued[0].get("pid")}
    if not env.child_alive(child):  # it ended, or its pid now belongs to a stranger, who is never stopped
        return {"child": child, "gone": True}
    return {"child": child, "survivors": env.stop_tree(child, procs), "count": len(procs)}


def stop_hung(state: dict, child: dict, env: Env) -> str | None:
    """Stop a hung child, or say why not. None when it is gone and the run can resume; otherwise the tick's result."""
    pid = child.get("pid")
    outcome = env.stopped(child) or stop_attempt(child, env)
    if outcome.get("queued") is not None:
        return f"resumed session {pid} is in the build queue (bosun {outcome['queued']}); left running"
    if outcome.get("gone"):
        state.pop("child", None)
        note(state, f"resumed session {pid} was gone when it came to be stopped")
        return None
    survivors, procs = outcome.get("survivors") or [], range(outcome.get("count", 0))
    if survivors:
        end_run(state, "stalled", f"resumed session {pid} changed nothing for {CHILD_HANG_HOURS} hours and couldn't be "
                                  f"stopped; still running: {', '.join(str(s) for s in survivors)}")
        return "stalled"
    note(state, f"stopped resumed session {pid} and its {len(procs)} processes: nothing changed for {CHILD_HANG_HOURS} hours")
    state.pop("child", None)
    return None


def end_run(state: dict, status: str, why: str) -> None:
    state.update(active=False, status=status, why=why, ended=stamp())


def waiting(state: dict, now: float | None = None) -> bool:
    """True while the run waits for something only time can bring back, such as an unavailable reviewer."""
    until = state.get("wait_until")
    return isinstance(until, (int, float)) and (time.time() if now is None else now) < until


def set_wait(state: dict, reason: str, until: float, detail: str | None = None) -> None:
    if not state.get("active"):
        raise ValueError("no active run, so nothing waits")
    if not reason.strip():
        raise ValueError("a wait needs a reason")
    state.update(wait_until=until, wait_reason=reason.strip(), wait_detail=(detail or "").strip() or None, wait_set=stamp())


def end_wait(state: dict, now: float, signature: str) -> None:
    """A wait that has passed gives the run a fresh no-progress budget: neither the time spent waiting nor the resume
    that follows it is a sign that the run is stuck."""
    for key in ("wait_until", "wait_reason", "wait_detail", "wait_set"):
        state.pop(key, None)
    state.update(restarts=0, idle=0, yields=0, changed_at=now, restart_signature=signature, signature=signature,
                 last_wait_ended=stamp(now))


def is_stop(prompt: str) -> bool:
    """A plain order to stop the run. Anything less plain goes to the model to judge: see on_prompt."""
    return bool(STOP.match(prompt))


def heartbeat_note() -> str:
    return (
        "Unattended run guard is active. If this session is interactive and CronList shows no heartbeat, create one "
        f"now with CronCreate, cron \"{CRON}\", prompt: \"Run heartbeat: run `python scripts/run-guard.py status`. If it "
        "prints active and no turn is in progress, continue process-plan at the next ready row. If it prints waiting, end "
        "the turn and keep this job. If it prints anything else, CronDelete this job and end the turn.\" Never ask the "
        "operator; record decisions in the run file. If the "
        "operator asks to stop or pause, run `python scripts/run-guard.py halt --ground operator --reason <their words>`."
    )


def snapshot(env: Env, subscription: bool = False, state: dict | None = None, now: float | None = None,
             stop_hung_child: bool = False) -> Env:
    """Takes the slow reads now, so the lock is never held across them and a stop can always get in. With nothing to
    run, that includes the finish line and the wakes, which ask git, CI and each wake's check; but not while the run
    waits (`state`), whose wait is the backoff: only a run waiting on its rows checks its wakes then. For the
    watchdog, a hung child is stopped here too, and the tick under the lock reads what happened."""
    rows, signature = env.rows(), env.signature()
    env = replace(env, rows=lambda: rows, signature=lambda: signature)
    child = (state or {}).get("child")
    if stop_hung_child and child and not waiting(state, now) and hung(state, signature, time.time() if now is None else now) \
            and env.child_alive(child):
        outcome = stop_attempt(child, env)
        env = replace(env, stopped=lambda c: outcome if c == child else None)
    if rows == []:
        held = bool(state) and waiting(state, now)
        found = {"state": "unknown", "detail": "not asked while the run waits"}
        if not held:
            try:
                found = env.finish()
            except Exception as error:
                found = {"state": "unknown", "detail": repr(error)}
        woken = env.wakes() if not held or state.get("wait_reason") == "rows-waiting" else []
        env = replace(env, finish=lambda: found, wakes=lambda: woken)
    if subscription:
        answer = env.subscription()
        env = replace(env, subscription=lambda: answer)
    return env


def check_mode(mode: object, env: Env) -> tuple:
    """(the mode to keep, a note for the operator or None). A missing mode is `default`, which the resume leaves off its
    command line. Raises ValueError for a mode Claude Code wouldn't accept, so no run starts that couldn't resume."""
    mode = str(mode) if mode else "default"
    if mode != "default" and mode not in env.modes():
        raise ValueError(f"permission mode {mode!r} isn't one claude accepts ({', '.join(sorted(env.modes()))}), so this run "
                         "couldn't be resumed: start it again in bypassPermissions")
    note = None if mode == CAMPAIGN_MODE else (
        f"This run started in {mode} mode, so a resumed session would meet prompts no one is there to answer and stop at "
        "each one. For an unattended run, start it with `claude --permission-mode bypassPermissions`.")
    return mode, note


def on_prompt(state: dict, data: dict, env: Env) -> str | None:
    prompt = str(data.get("prompt") or "")
    session = data.get("session_id")
    if state.get("active") and is_stop(prompt):
        end_run(state, "stopped", f"operator: {prompt.strip()}")
        return "The run guard stopped holding this run because the operator said to stop. `process plan` starts it again."
    if not START.match(prompt):
        # Background-task notifications and the heartbeat arrive as prompts too; neither is the operator speaking.
        machine = "<task-notification>" in prompt or prompt.startswith("Run heartbeat:")
        if state.get("active") and state.get("session_id") == session and not machine and MENTIONS_STOP.search(prompt):
            return (
                "The unattended run guard is active and this message mentions stopping. If the operator is asking to stop or "
                "pause the run, now or after the current section, run `python scripts/run-guard.py halt --ground operator "
                "--reason <their words>` when that point comes, or the Stop hook keeps sending the turn back. If it is about "
                "something else, carry on."
            )
        return None
    if state.get("active") and state.get("session_id") not in (None, session) and env.alive(state["session_id"]):
        return f"Another session ({state['session_id']}) owns the active run. Never run two writers on one tree: stop and say so."
    try:
        mode, mode_note = check_mode(data.get("permission_mode"), env)
    except ValueError as error:
        return f"The unattended run guard did not start this run: {error}."
    if not state.get("active"):
        state.clear()
        state.update(active=True, status="active", started=stamp(), changed_at=time.time())
    state.update(session_id=session, permission_mode=mode)
    note = heartbeat_note() + (" " + mode_note if mode_note else "")
    if not env.task_installed():
        return note + " The watchdog task is not installed, so a crash would end this run: run `python scripts/run-guard.py install` first."
    return note


def heartbeat_current(state: dict, now: float | None = None) -> bool:
    age = (time.time() if now is None else now) - state.get("heartbeat_epoch", 0)
    return age < HEARTBEAT_RENEW_DAYS * 86400


FINISH_STEPS = {
    "needs-push": "push the finished work to origin/main, so CI can prove it",
    "needs-ci-repair": "CI is red on the pushed head: fix it under process-phase's red-first policy",
}


def send_back(left: list[str] | None, state: dict) -> str:
    rows = "the next ready row" if not left else f"{len(left)} runnable rows, first {left[0]}"
    if left and left[0] in FINISH_STEPS:
        rows = f"one finishing step: {FINISH_STEPS[left[0]]}"
    heartbeat = "" if heartbeat_current(state) else "The heartbeat is not armed or is about to expire: create it again with CronCreate if this session is interactive, deleting any old one. "
    return heartbeat + (
        f"Unattended run is active: {rows}. Do not stop or report: continue process-plan now. A section that cannot be "
        "stamped is recorded and passed over, not a reason to end the run. Halting needs data loss, credentials, shared "
        "infrastructure or a calculation that looks wrong: `python scripts/run-guard.py halt --ground <ground> --reason <why>`."
    )


def pid_alive(pid: int) -> bool:
    return process_started(pid) is not None


def review_in_flight(root: Path = ROOT) -> str | None:
    """A reviewer scripts/review-dispatch.py is running now, from its marks under build/review-in-flight/, or None. A
    mark counts only while its process lives and its deadline hasn't passed, so a crashed dispatcher's leftover never
    holds a run. Ported from Recto (its `scripts/campaign_guard.py`)."""
    folder = Path(root) / "build" / "review-in-flight"
    try:
        names = sorted(os.listdir(folder))
    except OSError:
        return None
    for name in names:
        try:
            doc = json.loads((folder / name).read_text(encoding="utf-8"))
            pid, deadline = int(doc["pid"]), float(doc["deadline"])
        except (OSError, ValueError, KeyError, TypeError):
            continue
        if pid > 0 and time.time() <= deadline and pid_alive(pid):
            return f"{doc.get('reviewer', 'reviewer')}, dispatcher pid {pid}"
    return None


CI_WAIT_LIMIT = 2 * 60 * 60  # scripts/ci_status.py WAIT_LIMIT_SECONDS: past it the gate answers `unverified`


def ci_in_flight(root: Path = ROOT) -> str | None:
    """The commit a CI gate (`scripts/ci_status.py --gate`) is polling now, from its wait in build/ci-gate.json, or None.
    process-phase holds the next row on a `wait`, so this does too. It counts only while the gate that wrote it lives
    and within the gate's own limit, so a killed gate's record never holds a run."""
    try:
        waiting = json.loads((Path(root) / "build" / "ci-gate.json").read_text(encoding="utf-8")).get("waiting")
        sha, since, pid = str(waiting["sha"]), float(waiting["since"]), int(waiting["pid"])
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        return None  # no wait, or one from a gate too old to name its pid: nothing is polling
    if pid > 0 and time.time() - since < CI_WAIT_LIMIT and pid_alive(pid):
        return f"{sha[:12]}, gate pid {pid}"
    return None


def on_stop(state: dict, data: dict, env: Env) -> str | None:
    if not state.get("active") or state.get("session_id") != data.get("session_id"):
        return None
    if waiting(state) or env.reviewing() or env.ci_waiting():
        # The turn may end, and it isn't idleness: nothing can be done until a wait passes, a review in flight must not
        # have the tree change under it, and a CI `wait` starts no next row. Its notification, or the heartbeat,
        # resumes the row.
        return None
    left = env.rows()
    if left == []:
        settled = settle(state, env, time.time())
        if not settled.startswith("work: "):
            return None  # it waits, and the turn may end, or it ended
        left = [settled[len("work: "):]]
    signature = env.signature()
    if signature != state.get("signature"):
        state.update(signature=signature, idle=0, yields=0, changed_at=time.time())
    else:
        state["idle"] = state.get("idle", 0) + 1
    if state.get("idle", 0) >= MAX_IDLE_BLOCKS:
        state["idle"] = 0
        state["yields"] = state.get("yields", 0) + 1
        if state["yields"] >= MAX_YIELDS:
            end_run(state, "stalled", "no commit or change to the tree across repeated attempts")
        return None
    state["last_block"] = stamp()
    return send_back(left, state)


def on_session_start(state: dict, data: dict) -> str | None:
    """Only the run's own session continues it: a new session opened in this repository is unrelated work."""
    if not state.get("active") or state.get("session_id") != data.get("session_id"):
        return None
    return heartbeat_note() + " Continue process-plan now."


def on_cron_created(state: dict, data: dict) -> None:
    if state.get("active") and state.get("session_id") == data.get("session_id"):
        state.update(heartbeat_at=stamp(), heartbeat_epoch=time.time())


def failure_class(data: dict) -> str:
    return FAILURE_CLASSES.get(str(data.get("error") or ""), "other")


def reset_time(text: str, now: float) -> float | None:
    """When a usage limit resets, from the words that report it, or None when they don't say, or say something no real
    reset could be (not a number, already past, or more than 8 days off): the 30 minute wait then looks again."""
    try:
        until = _reset_time(text, now)
    except (ValueError, OverflowError, OSError):
        return None
    if until is None or not math.isfinite(until) or until <= now or until > now + LONGEST_RESET:
        return None
    return until


def _reset_time(text: str, now: float) -> float | None:
    found = re.search(r"\|(\d{10})\b", text)
    if found:
        return float(found.group(1))
    found = re.search(r"(?i)\bresets?\b[^.\n]{0,40}?\bin (\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m)\b", text)
    if found:
        return now + float(found.group(1)) * (3600 if found.group(2).lower().startswith("h") else 60)
    found = re.search(r"(?i)\bresets?\b[^.\n]{0,40}?\bat (\d{1,2})(?::(\d{2}))?\s*(am|pm)\b", text)
    if found:
        hour = int(found.group(1)) % 12 + (12 if found.group(3).lower() == "pm" else 0)
        day = time.localtime(now)
        target = time.mktime((day.tm_year, day.tm_mon, day.tm_mday, hour, int(found.group(2) or 0), 0, 0, 0, -1))
        return target if target > now else target + 86400  # the next time that clock time comes round
    return None


def on_stop_failure(state: dict, data: dict, now: float | None = None) -> None:
    """An API error ends the turn with no Stop to send back. A usage limit, a sign-in problem or an overloaded API puts
    the run in a wait that spends no stall budget; anything else is only recorded, and `status` shows it."""
    if not (state.get("active") and state.get("session_id") == data.get("session_id")):
        return
    now = time.time() if now is None else now
    error = str(data.get("error") or "unknown")
    state["last_api_failure"] = f"{stamp(now)} {error}"
    text = f"{data.get('error_details') or ''}\n{data.get('last_assistant_message') or ''}"
    kind = failure_class(data)
    if kind == "usage-limit":
        until = reset_time(text, now) or now + USAGE_WAIT
        set_wait(state, "usage-limit", until, f"the subscription's usage limit is reached; the run resumes at {stamp(until)}")
    elif kind == "auth":
        detail = (f"the account can't be used ({error}): that is the owner's to sort out with Anthropic, and the run resumes once Claude Code can sign in"
                  if error in ACCOUNT_ERRORS else
                  f"Claude Code's sign-in has expired or was refused ({error}): sign in again with `claude` and /login, and the run resumes")
        set_wait(state, "sign-in-needed", now + SIGN_IN_WAIT, detail)
    elif kind == "overloaded":
        set_wait(state, "overloaded", now + OVERLOADED_WAIT, f"Anthropic's API reported {error}; the run looks again in 5 minutes")


def start(state: dict, session: str, mode: str | None, env: Env, model: str | None = None) -> str | None:
    """Start or take over a run; returns a note for the operator, or None.

    `model` is what the operator started the session with (`claude --model`): an alias such as `opus`, which Claude
    Code resolves to the newest Opus, or an exact id. It is kept so every resumed or fresh session runs the same one.
    """
    owner = state.get("session_id")
    if state.get("active") and owner not in (None, session) and env.alive(owner):
        raise ValueError(f"session {owner} owns the active run and is alive; never run two writers on one tree")
    mode, note = check_mode(mode, env)
    if model is not None and not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._\[\]-]*", model):
        raise ValueError(f"`{model}` is not a model name `claude --model` takes (an alias such as opus, or an exact id)")
    if not state.get("active"):
        state.clear()
        state.update(active=True, status="active", started=stamp(), changed_at=time.time())
    state.update(session_id=session, permission_mode=mode)
    if model:
        state["model"] = model
    return note


def on_session_end(state: dict, data: dict) -> None:
    if state.get("active") and state.get("session_id") == data.get("session_id") and data.get("reason") in USER_EXITS:
        end_run(state, "stopped", f"operator ended the session ({data.get('reason')})")


def watch(state: dict, env: Env, now: float | None = None) -> str:
    """One scheduled tick. A live session in this repository always wins: two writers on one tree is the worse failure."""
    if not state.get("active"):
        return "inactive"
    now = time.time() if now is None else now
    signature = env.signature()
    if state.get("wait_until") is not None:
        # A run waiting on its rows wakes early when a wake's check passes: the resumed session clears the Need.
        woken = bool(waiting(state, now) and state.get("wait_reason") == "rows-waiting" and env.wakes())
        if waiting(state, now) and not woken:
            return f"waiting: {state.get('wait_reason')} until {stamp(state['wait_until'])}"
        end_wait(state, now, signature)
        state["woken"] = woken  # its Need is still on the section until the resumed session applies the wake
    if signature != state.get("restart_signature"):
        state.update(restart_signature=signature, restarts=0, changed_at=now)
    quiet = (now - state.get("changed_at", now)) / 3600
    child = state.get("child")
    if child and (env.stopped(child) or env.child_alive(child)):  # one stopped before the lock is alive no longer
        if not hung(state, signature, now):
            return "resumed session still running"
        stopped = stop_hung(state, child, env)
        if stopped is not None:
            return stopped
    elif child and TRANSCRIPT_GONE.search(env.child_output(child) or ""):  # a fresh child saying so too ends it
        # `--resume` found no transcript: the session can't come back, so the run starts a fresh `process plan`.
        if state.get("fresh_starts", 0) >= MAX_FRESH_STARTS:
            end_run(state, "stalled", f"session {state.get('session_id')}'s transcript is gone, and the run already started "
                                      f"afresh once; `process plan` starts it again")
            return "stalled"
        state.pop("child", None)
        state["fresh_next"] = True
        note(state, f"session {state.get('session_id')}'s transcript is gone; the next launch starts a fresh session")
    live = holding_sessions(env.live_here(), state.get("session_id"))
    if live:
        return f"session live here: {live[0]}"
    reviewing = env.reviewing()
    if reviewing:
        # A writer resumed now would change the tree under the reviewer, as a crashed writer's surviving dispatcher
        # would see; the review's end, or the next tick, resumes the run. Neither a restart nor idleness.
        return f"waiting: a review is in flight ({reviewing})"
    checking = env.ci_waiting()
    if checking:
        # The gate's own result reaches the session if it is still running, and otherwise the next tick after it ends
        # resumes the run, which runs the gate again. Neither a restart nor idleness.
        return f"waiting: CI is being checked ({checking})"
    if quiet >= MAX_QUIET_HOURS:
        end_run(state, "expired", f"nothing changed for {MAX_QUIET_HOURS} hours, so it was not resumed; `process plan` starts it again")
        return "expired"
    left = env.rows()
    if left == [] and state.pop("woken", False):
        pass  # a passing wake is the work: the resumed session runs `wake --apply` first
    elif left == []:
        settled = settle(state, env, now)
        if settled == "waiting":
            return f"waiting: {state.get('wait_reason')} until {stamp(state['wait_until'])}"
        if not settled.startswith("work: "):
            return settled
    if state.get("restarts", 0) >= MAX_RESTARTS:
        end_run(state, "stalled", f"{MAX_RESTARTS} resumes changed nothing")
        return "stalled"
    # Only ever on the subscription: a launch that would bill an account, or can't sign in, waits instead, spending no
    # restart, and looks again when the wait passes.
    ok, reason, detail = env.subscription()
    if not ok:
        set_wait(state, reason or "sign-in-needed", now + SIGN_IN_WAIT, detail)
        return f"waiting: {state['wait_reason']} until {stamp(state['wait_until'])}"
    fresh, spent = bool(state.get("fresh_next")), state.get("fresh_starts", 0)
    if fresh and spent >= MAX_FRESH_STARTS:
        # The budget is already spent, so the last fresh launch happened and its record was never saved. If that
        # session is running, its own `process plan` prompt takes the run back; nothing else is launched beside it.
        state.pop("fresh_next", None)
        end_run(state, "stalled", "a fresh session was launched but its record wasn't saved; if it is running, its own "
                                  "`process plan` takes the run back, and otherwise `process plan` starts it again")
        return "stalled"
    if fresh:
        # Spent before the launch, in the state the watchdog saves just before launching, so a save that fails after
        # it can never let a second fresh start through.
        state["fresh_starts"] = spent + 1
    try:
        child = env.spawn(state)  # a launch that fails raises and uses no attempt: nothing ran, so nothing was tried
    except BaseException:
        if fresh:
            state["fresh_starts"] = spent
        raise
    if state.pop("fresh_next", False):
        child = {**child, "fresh": True}
        note(state, f"started a fresh `process plan` session (pid {child.get('pid')}), as {state.get('permission_mode') or 'default'}")
    state.update(child=child, restarts=state.get("restarts", 0) + 1, launched_at=now, last_restart=stamp())
    return f"resumed, attempt {state['restarts']}"


def halt(state: dict, ground: str, reason: str) -> None:
    if ground not in GROUNDS:
        raise ValueError(f"ground must be one of: {', '.join(GROUNDS)}")
    if not reason.strip():
        raise ValueError("a halt needs a reason")
    end_run(state, "halted", f"{ground}: {reason.strip()}")


# The world, on this machine.


def hidden() -> dict:
    return {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}


def run(*args: str, cwd: Path = ROOT, timeout: float = 20) -> subprocess.CompletedProcess:
    env = {**os.environ, "PYTHONIOENCODING": "utf-8"}
    return subprocess.run(args, cwd=cwd, capture_output=True, env=env, timeout=timeout, stdin=subprocess.DEVNULL, **hidden())


def run_tree(args: list, timeout: float, cwd: Path | None = None, env: dict | None = None) -> subprocess.CompletedProcess:
    """Run a command whose children must stop with it. On a timeout the whole tree is stopped and TimeoutExpired is
    raised. `subprocess.run(timeout=...)` stops only the direct child, and on Windows `claude` is a shim whose own child
    keeps the pipes open, so a hung sign-in check held the watchdog's tick for as long as that child lived."""
    flags = subprocess.CREATE_NO_WINDOW | subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
    proc = subprocess.Popen(args, cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            creationflags=flags, start_new_session=os.name != "nt")
    try:
        out, err = proc.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        try:
            if os.name == "nt":
                subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True, timeout=60, **hidden())
            else:
                os.killpg(proc.pid, signal.SIGKILL)
        except (OSError, subprocess.SubprocessError):
            pass  # the direct child is stopped below whatever happened to the rest
        proc.kill()
        try:
            proc.communicate(timeout=10)
        except subprocess.TimeoutExpired:
            pass  # a survivor still holds the pipes: the caller gets its timeout now, not when that process ends
        raise
    return subprocess.CompletedProcess(args, proc.returncode, out, err)


WORK_STATES = ("ready", "needs-repair", "needs-filing", "needs-verification", "needs-closeout")
# How long a run with nothing to run waits before it looks again, by why it waits.
LOOK_AGAIN = {"rows-waiting": 30 * 60, "ci-pending": 10 * 60, "ci-unavailable": 30 * 60, "acceptance-unwritable": 10 * 60,
              "finish-unreadable": 10 * 60}
FINISH_WORK = ("needs-push", "needs-ci-repair")  # what the finish line asks of the session itself


def settle(state: dict, env: Env, now: float) -> str:
    """What a run with no runnable row does, from what the graph and CI say is left. Returns "work: <item>" when the
    session has a finishing step to take, "waiting" when only time, a person, a host or CI can move it (rechecked by
    the watchdog, its quiet time and restarts suspended), or the status it ended with."""
    try:
        found = env.finish() or {}
    except Exception as error:  # an unreadable finish line never ends a run; the next look tries again
        found = {"state": "unknown", "detail": repr(error)}
    kind, detail = found.get("state"), found.get("detail") or ""
    if kind not in FINISH_WORK + ("waiting", "ci-pending", "ci-unavailable", "complete", "parked", "drained"):
        # A check that failed this once (a slow fetch, CI that timed out) is no answer, and never an end.
        set_wait(state, "finish-unreadable", now + LOOK_AGAIN["finish-unreadable"],
                 f"what is left couldn't be read ({detail or kind}); the run looks again")
        return "waiting"
    if kind in FINISH_WORK:
        return f"work: {kind}"
    if kind in ("waiting", "ci-pending", "ci-unavailable"):
        reason = "rows-waiting" if kind == "waiting" else kind
        set_wait(state, reason, now + LOOK_AGAIN[reason], detail)
        return "waiting"
    if kind in ("complete", "parked"):
        try:
            path = env.write_acceptance(kind, found.get("report") or "")
        except OSError as error:
            set_wait(state, "acceptance-unwritable", now + LOOK_AGAIN["acceptance-unwritable"],
                     f"the acceptance file couldn't be written ({error}); the run tries again")
            return "waiting"
        end_run(state, kind, f"{detail}; acceptance: {path}" if detail else f"acceptance: {path}")
        return kind
    end_run(state, "drained", DRAINED)
    return "drained"


def runnable(rows: list[dict]) -> list[str]:
    """The rows this run can take, from `query disposition`, which owns what can run. A needs-repair row is work: the
    skills repair or park it, and the state already checked that this session can."""
    return [r["ref"] for r in rows if r.get("state") in WORK_STATES]


def real_rows() -> list[str] | None:
    """The rows this run can still take. None when that cannot be told, which never ends a run."""
    try:
        out = run(sys.executable, str(ROOT / "scripts" / "todo-graph.py"), "query", "disposition")
        if out.returncode != 0:
            return None
        return runnable(json.loads(out.stdout.decode("utf-8", "replace")))
    except (OSError, ValueError, TypeError, AttributeError, subprocess.SubprocessError):
        return None


def real_signature(root: Path = ROOT) -> str:
    head = run("git", "rev-parse", "HEAD", cwd=root).stdout
    tree = run("git", "status", "--porcelain", cwd=root).stdout + run("git", "diff", "HEAD", cwd=root).stdout
    # The diff leaves out untracked files, and status can collapse a new folder to one line: edits there are work too.
    for name in run("git", "ls-files", "--others", "--exclude-standard", "-z", cwd=root).stdout.decode("utf-8", "replace").split("\0"):
        try:
            seen = (root / name).stat() if name else None
        except OSError:
            continue  # removed while this looked; the next signature differs anyway
        if seen:
            tree += f"{name}:{seen.st_size}:{seen.st_mtime_ns}\n".encode()
    return f"{head.decode().strip()}:{hashlib.sha256(tree).hexdigest()}"


def linux_started(pid: int, proc: Path = Path("/proc")) -> int | None:
    """A Linux process's start, in clock ticks after boot (field 22 of /proc/<pid>/stat), so a reused pid reads
    differently; None when it is gone or a zombie."""
    try:
        stat = (proc / str(pid) / "stat").read_text()
    except (FileNotFoundError, ProcessLookupError):
        return None
    fields = stat[stat.rindex(")") + 2:].split()  # after the name, which can hold spaces and brackets: field 3 onwards
    return None if fields[0] == "Z" else int(fields[19])


def process_started(pid: int) -> int | None:
    """When a process started, as a FILETIME, so a reused process id is not taken for the session. None when it is gone."""
    if os.name != "nt":
        if Path("/proc/self/stat").is_file():
            return linux_started(pid)
        try:  # no /proc (macOS): alive or not is all there is
            os.kill(pid, 0)
            return 0
        except OSError:
            return None
    import ctypes
    from ctypes import wintypes

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    kernel.GetProcessTimes.argtypes = (wintypes.HANDLE, *[ctypes.POINTER(wintypes.FILETIME)] * 4)
    kernel.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
    kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    handle = kernel.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        if ctypes.get_last_error() == 87:  # ERROR_INVALID_PARAMETER: no such process
            return None
        raise OSError(f"cannot tell whether process {pid} is running")
    try:
        code = wintypes.DWORD()
        if not kernel.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value != 259:  # STILL_ACTIVE
            return None
        times = [wintypes.FILETIME() for _ in range(4)]
        if not kernel.GetProcessTimes(handle, *[ctypes.byref(t) for t in times]):
            return None
        return (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
    finally:
        kernel.CloseHandle(handle)


def session_records() -> list[dict]:
    """Claude Code's own record of each session it runs; one left by a crash names a process that has gone.

    A record that cannot be judged raises, so `watch` starts nothing: not knowing is not the same as dead.
    """
    records = []
    for path in (Path.home() / ".claude" / "sessions").glob("*.json"):
        try:
            record = json.loads(path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            continue  # the session exited while this looked
        started = process_started(int(record["pid"]))
        if started is not None and (os.name != "nt" or str(started) == str(record.get("procStart"))):
            records.append(record)
    return records


def same_folder(a: str | None, b: Path) -> bool:
    return bool(a) and os.path.normcase(os.path.realpath(a)) == os.path.normcase(os.path.realpath(str(b)))


def real_alive(session: str) -> bool:
    return any(r.get("sessionId") == session for r in session_records())


def real_live_here() -> list[dict]:
    return [{"id": str(r.get("sessionId")), "status": r.get("status")} for r in session_records() if same_folder(r.get("cwd"), ROOT)]


def real_child_alive(child: dict) -> bool:
    started = process_started(int(child.get("pid", 0)))
    return started is not None and started == child.get("started")


def resume_command(state: dict, claude: str) -> list[str]:
    command = [claude, "-p", "process plan"]
    if state.get("session_id") and not state.get("fresh_next"):  # a lost transcript starts afresh
        command += ["--resume", state["session_id"]]
    # The operator's own mode for this run and no wider; `default` is Claude Code's own and needs no flag.
    if state.get("permission_mode") and state["permission_mode"] != "default":
        command += ["--permission-mode", state["permission_mode"]]
    # The model the run was started with, so a resume or a fresh start never drops to the account's default.
    if state.get("model"):
        command += ["--model", state["model"]]
    return command


def child_env(environ: dict, note: Callable[[str], None] = lambda line: None) -> dict:
    """The environment a resumed session gets: the run's own, less anything that would let it bill an account."""
    env = dict(environ)
    for name in SCRUBBED:
        if name in env:
            del env[name]
            note(f"removed {name} from the resumed session's environment, so it runs on the subscription")
    return env


def subscription_verdict(data: object) -> tuple:
    """(ok, reason, detail) from `claude auth status --json`: ok only when it signs in with the subscription."""
    if not isinstance(data, dict) or not data.get("loggedIn"):
        return False, "sign-in-needed", "Claude Code isn't signed in: run `claude` and sign in with the subscription (/login)"
    if data.get("apiProvider") != "firstParty":
        return False, "subscription-unavailable", (f"Claude Code is set to use {data.get('apiProvider')}, which bills a cloud account: "
                                                   "unset CLAUDE_CODE_USE_* and any provider in its settings so the subscription is used")
    # claude.ai is the stored sign-in; oauth_token is `claude setup-token`'s long-lived token in CLAUDE_CODE_OAUTH_TOKEN,
    # which only a subscription can make. Anything else (api_key, an apiKeyHelper) bills an API account.
    if data.get("authMethod") not in SUBSCRIPTION_SIGN_INS:
        return False, "subscription-unavailable", (f"Claude Code would sign in with {data.get('authMethod')}, which bills an API account: "
                                                   "remove the API key or apiKeyHelper from the environment and settings, then sign in with the subscription")
    return True, "", ""


def real_modes() -> set:
    """The modes `claude --help` lists for --permission-mode, or the known list when it can't be read."""
    claude = shutil.which("claude")
    try:
        done = run_tree([claude or "claude", "--help"], timeout=30)
        text = done.stdout.decode("utf-8", "replace")
        found = re.search(r"--permission-mode.*?choices:(.*?)\)", text, re.S)
        modes = set(re.findall(r'"([A-Za-z]+)"', found.group(1))) if found else set()
    except (OSError, subprocess.SubprocessError):
        modes = set()
    return modes or set(KNOWN_MODES)


def real_subscription() -> tuple:
    claude = shutil.which("claude")
    if not claude:
        return False, "sign-in-needed", "claude is not on PATH, so its sign-in can't be checked"
    try:
        done = run_tree([claude, "auth", "status", "--json"], timeout=AUTH_TIMEOUT, cwd=ROOT, env=child_env(os.environ))
        data = json.loads(done.stdout.decode("utf-8", "replace"))
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        return False, "sign-in-needed", f"Claude Code's sign-in couldn't be checked ({error}): run `claude auth status` to see why"
    return subscription_verdict(data)


CI_ANSWERS = {0: None, 1: "needs-ci-repair", 4: "ci-pending", 5: "ci-pending"}  # scripts/ci_status.py's exits


def acceptance_report(kind: str, found: dict) -> str:
    """The acceptance file's text: what the owner's acceptance pass starts from."""
    lines = [f"# Acceptance: implementation {'complete' if kind == 'complete' else 'parked'}", "",
             f"Written by the run guard on {stamp()} when the unattended run ended {kind}.", ""]
    parked = sorted(found.get("parked") or [ref for ref, s in found.get("roots", {}).items() if s == "parked"])
    if parked:
        lines += ["## Parked sections", "", *(f"- {ref}" for ref in parked), ""]
        lines += ["## Blocked by them", ""]
        lines += [f"- {ref}: held by {', '.join(roots)}" for ref, roots in sorted(found.get("blocked", {}).items())
                  if ref not in parked]
        lines += [""]
    lines += ["## Acceptance rows (phase 99)", "", "Run each with the owner: `process-phase 99`.", ""]
    lines += [f"- {r['ref']}: {r['title']}" + (f" (needs {', '.join(r['needs'])})" if r.get("needs") else "")
              for r in found.get("acceptance", [])] or ["- none"]
    lines += ["", "## Files that close in acceptance", ""]
    lines += [f"- {path}" for path in found.get("files_waiting_on_acceptance", [])] or ["- none"]
    return "\n".join(lines) + "\n"


def real_finish(root: Path = ROOT) -> dict:
    """What a run with nothing to run does next, from `query finish`, git and CI."""
    try:
        out = run(sys.executable, str(root / "scripts" / "todo-graph.py"), "query", "finish", cwd=root, timeout=120)
        found = json.loads(out.stdout.decode("utf-8", "replace")) if out.returncode == 0 else None
    except (OSError, ValueError, subprocess.SubprocessError):
        found = None
    if not isinstance(found, dict):
        return {"state": "unknown", "detail": "query finish couldn't be read"}
    if not found["complete"]:
        roots = found.get("roots", {})
        if roots and all(s == "parked" for s in roots.values()):
            kind = "parked"
        else:
            waits = sorted(f"{ref} ({s})" for ref, s in roots.items() if s != "parked")
            return {"state": "waiting", "detail": f"waiting on {', '.join(waits) or 'its rows'}"}
    else:
        kind = "complete"
    try:
        fetched = run("git", "fetch", "-q", "origin", "main", cwd=root, timeout=60)
    except (OSError, subprocess.SubprocessError):
        return {"state": "ci-unavailable", "detail": "origin/main couldn't be fetched in time"}
    head = run("git", "rev-parse", "HEAD", cwd=root).stdout.decode().strip()
    remote = run("git", "rev-parse", "origin/main", cwd=root).stdout.decode().strip()
    if fetched.returncode != 0 or not remote:
        return {"state": "ci-unavailable", "detail": "origin/main couldn't be fetched"}
    if head != remote:
        return {"state": "needs-push", "detail": f"local {head[:7]} isn't origin/main {remote[:7]}"}
    try:
        ci = run(sys.executable, str(root / "scripts" / "ci_status.py"), "--sha", head, "--use-proven", cwd=root, timeout=120)
    except (OSError, subprocess.SubprocessError):
        return {"state": "ci-unavailable", "detail": "CI couldn't be asked in time"}
    answer = CI_ANSWERS.get(ci.returncode, "ci-unavailable")
    if answer:
        return {"state": answer, "detail": f"CI on {head[:7]}: {ci.stdout.decode('utf-8', 'replace').strip()[-300:]}"}
    return {"state": kind, "detail": f"CI proven on {head[:7]}", "report": acceptance_report(kind, found)}


WAKE_SECONDS = 60  # each check's own budget, as todo_graph.WAKE_SECONDS gives it


def real_wakes(root: Path = ROOT) -> list:
    try:
        count = len(json.loads((root / "todo" / "wakes.json").read_text(encoding="utf-8")))
    except (OSError, ValueError, TypeError):
        count = 1
    try:
        out = run(sys.executable, str(root / "scripts" / "todo-graph.py"), "wake", "--check", cwd=root,
                  timeout=WAKE_SECONDS * max(count, 1) + 30)
        return json.loads(out.stdout.decode("utf-8", "replace")) if out.returncode == 0 else []
    except (OSError, ValueError, subprocess.SubprocessError):
        return []


def write_acceptance(kind: str, text: str, root: Path = ROOT, today: str | None = None) -> str:
    """docs/acceptance/<date>.md, atomically and read back; never over an earlier one, which a second the same day
    keeps as <date>-2.md. Raises OSError when it can't be written or doesn't read back."""
    folder = root / "docs" / "acceptance"
    folder.mkdir(parents=True, exist_ok=True)
    day = today or time.strftime("%Y-%m-%d")
    path, n = folder / f"{day}.md", 2
    while path.exists():
        path, n = folder / f"{day}-{n}.md", n + 1
    tmp = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    tmp.write_text(text, encoding="utf-8")
    os.replace(tmp, path)
    if path.read_text(encoding="utf-8") != text:
        raise OSError(f"{path} didn't read back as written")
    return path.relative_to(root).as_posix()


def real_spawn(state: dict) -> dict:
    claude = shutil.which("claude")
    if not claude:
        raise OSError("claude is not on PATH")
    output = STATE.with_name("resumed-session.log")
    flags = subprocess.CREATE_NO_WINDOW | subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
    with output.open("ab") as out:
        offset = out.tell()  # where this child's own output starts, so an old child's error is never read as its own
        child = subprocess.Popen(resume_command(state, claude), cwd=ROOT, stdin=subprocess.DEVNULL, stdout=out, stderr=out,
                                 creationflags=flags, env=child_env(os.environ, lambda line: log(line, STATE)))  # beside the state in use
    return {"pid": child.pid, "started": process_started(child.pid), "log_offset": offset}


def real_child_output(child: dict, path: Path | None = None) -> str:
    path = path or STATE.with_name("resumed-session.log")
    try:
        with path.open("rb") as handle:
            handle.seek(int(child.get("log_offset") or 0))
            return handle.read(CHILD_OUTPUT_LIMIT).decode("utf-8", "replace")
    except (OSError, ValueError):
        return ""


def process_table() -> list[dict]:
    """Every process: {"pid", "ppid", "name", "command"}. On Windows from a toolhelp snapshot, on Linux from /proc."""
    if os.name != "nt":
        table = []
        for entry in Path("/proc").iterdir():
            if not entry.name.isdigit():
                continue
            try:
                stat = (entry / "stat").read_text()
                ppid = int(stat[stat.rindex(")") + 2:].split()[1])
                command = (entry / "cmdline").read_bytes().replace(b"\0", b" ").decode("utf-8", "replace").strip()
                table.append({"pid": int(entry.name), "ppid": ppid, "name": (entry / "comm").read_text().strip(), "command": command})
            except (OSError, ValueError, IndexError):
                continue
        return table
    import ctypes
    from ctypes import wintypes

    class Entry(ctypes.Structure):
        _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD), ("th32ProcessID", wintypes.DWORD),
                    ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                    ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", wintypes.LONG), ("dwFlags", wintypes.DWORD),
                    ("szExeFile", wintypes.WCHAR * 260)]
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.CreateToolhelp32Snapshot.argtypes = (wintypes.DWORD, wintypes.DWORD)
    kernel.Process32FirstW.argtypes = kernel.Process32NextW.argtypes = (wintypes.HANDLE, ctypes.POINTER(Entry))
    kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    snap = kernel.CreateToolhelp32Snapshot(0x2, 0)  # TH32CS_SNAPPROCESS
    if snap in (None, wintypes.HANDLE(-1).value):
        raise OSError("the process list can't be read")
    table = []
    try:
        entry = Entry()
        entry.dwSize = ctypes.sizeof(Entry)
        more = kernel.Process32FirstW(snap, ctypes.byref(entry))
        while more:
            table.append({"pid": entry.th32ProcessID, "ppid": entry.th32ParentProcessID, "name": entry.szExeFile, "command": ""})
            more = kernel.Process32NextW(snap, ctypes.byref(entry))
    finally:
        kernel.CloseHandle(snap)
    return table


def descendants(root_pid: int, root_started, table: list[dict], started: Callable[[int], object]) -> list[dict]:
    """The process tree under root_pid, the root first. A process counts as a child only if it started after its
    parent: a parent pid outlives its process and can be reused, and a stranger must never be swept in."""
    tree = [{"pid": root_pid, "started": root_started, "name": next((p["name"] for p in table if p["pid"] == root_pid), ""),
             "command": next((p["command"] for p in table if p["pid"] == root_pid), "")}]
    seen = {root_pid}
    frontier = [(root_pid, root_started)]
    while frontier:
        parent, parent_started = frontier.pop()
        for proc in table:
            if proc["ppid"] != parent or proc["pid"] in seen:
                continue
            when = started(proc["pid"])
            if when is None or (isinstance(when, int) and isinstance(parent_started, int) and when < parent_started):
                continue
            seen.add(proc["pid"])
            tree.append({**proc, "started": when})
            frontier.append((proc["pid"], when))
    return tree


def real_tree(child: dict) -> list[dict]:
    return descendants(int(child.get("pid", 0)), child.get("started"), process_table(), process_started)


def survivors(procs: list[dict], started: Callable[[int], object] = None) -> list[int]:
    started = started or process_started
    left = []
    for proc in procs:
        try:
            if started(proc["pid"]) == proc["started"] and proc["started"] is not None:
                left.append(proc["pid"])
        except OSError:
            left.append(proc["pid"])  # can't tell: not proved stopped
    return left


def kill_each(procs: list[dict], started: Callable[[int], object], kill: Callable[[int], None]) -> list[int]:
    """Kill each listed process, deepest first so nothing is orphaned past the sweep, and only one still the process
    that was listed: a pid reused since the listing is a stranger's. Returns the pids killed."""
    killed = []
    for proc in reversed(procs):
        try:
            if proc.get("started") is None or started(proc["pid"]) != proc["started"]:
                continue
            kill(proc["pid"])
            killed.append(proc["pid"])
        except OSError:
            pass  # gone already, or refused: `survivors` says which
    return killed


def real_stop_tree(child: dict, procs: list[dict]) -> list[int]:
    if os.name == "nt":
        try:
            run("taskkill", "/T", "/F", "/PID", str(child["pid"]), timeout=60)
        except (OSError, subprocess.SubprocessError):
            pass  # what survived is what counts, read below
    else:
        import signal
        kill_each(procs, process_started, lambda pid: os.kill(pid, signal.SIGKILL))
    deadline = time.time() + 10
    left = survivors(procs)
    while left and time.time() < deadline:
        time.sleep(0.5)
        left = survivors(procs)
    return left


def real_task_installed() -> bool:
    if os.name != "nt":
        return True
    return run("schtasks", "/Query", "/TN", TASK_NAME).returncode == 0


REAL = Env(real_rows, real_signature, real_alive, real_live_here, real_child_alive, real_spawn, real_task_installed,
           real_modes, real_finish, real_wakes, write_acceptance, review_in_flight, real_subscription, real_tree,
           real_stop_tree, real_child_output, ci_waiting=ci_in_flight)


def record_tick(result: str, ok: bool, path: Path = STATE) -> None:
    """Kept apart from the state, so a tick that could not read or lock the state still says so."""
    tick = path.with_name("last-tick.json")
    tmp = tick.with_name(f"{tick.name}.{os.getpid()}.tmp")
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp.write_text(json.dumps({"at": stamp(), "ok": ok, "result": result}), encoding="utf-8")
    os.replace(tmp, tick)


def health(state: dict, path: Path = STATE, env: Env = REAL, now: float | None = None) -> dict:
    """What `status` adds to the state: can each catch actually catch?"""
    now = time.time() if now is None else now
    try:
        tick = json.loads(path.with_name("last-tick.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        tick = None
    return {
        "watchdog_installed": env.task_installed(),
        "last_tick": tick,
        "last_tick_stale": tick is None or not tick.get("ok") or now - calendar.timegm(time.strptime(tick["at"], "%Y-%m-%dT%H:%M:%SZ")) > (WATCH_MINUTES * 3) * 60,
        "heartbeat_current": heartbeat_current(state, now),
        "hook_failures": hook_failures(path),
    }


def hook_failures(path: Path = STATE) -> list:
    try:
        data = json.loads(path.with_name(HOOK_FAILURES).read_text(encoding="utf-8"))
        return data.get("failures", []) if isinstance(data, dict) else []
    except FileNotFoundError:
        return []
    except (OSError, ValueError) as error:
        return [{"at": None, "error": f"{HOOK_FAILURES} can't be read: {error!r}"}]


def record_hook_failure(error: str, path: Path = STATE) -> None:
    """Keep a hook that failed open where `status` shows it. Never raises: a full disk mustn't block the session."""
    try:
        failures = [f for f in hook_failures(path) if f.get("at")][-(HOOK_FAILURES_KEPT - 1):]
        failures.append({"at": stamp(), "error": error[:500]})
        target = path.with_name(HOOK_FAILURES)
        target.parent.mkdir(parents=True, exist_ok=True)
        tmp = target.with_name(f"{target.name}.{os.getpid()}.tmp")
        tmp.write_text(json.dumps({"failures": failures}), encoding="utf-8")
        os.replace(tmp, target)
    except Exception:  # noqa: BLE001 - the hook is already failing open; this record is best effort by design
        pass


def clear_hook_failures(path: Path = STATE) -> None:
    """A hook that ran clean: the guard works again, so the old failures no longer describe it."""
    try:
        path.with_name(HOOK_FAILURES).unlink(missing_ok=True)
    except OSError:
        pass


def parse_stamp(text) -> float | None:
    try:
        return float(calendar.timegm(time.strptime(str(text), "%Y-%m-%dT%H:%M:%SZ")))
    except (TypeError, ValueError):
        return None


def user_ids() -> set:
    """This user's names as a task's principal may give them: the name, DOMAIN\\name, and the SID."""
    name = os.environ.get("USERNAME") or os.environ.get("USER") or ""
    ids = {name.lower(), f"{os.environ.get('USERDOMAIN', '')}\\{name}".lower()}
    if os.name == "nt":
        try:
            done = run("whoami", "/user", "/fo", "csv", "/nh")
            ids.add(done.stdout.decode("utf-8", "replace").strip().split(",")[-1].strip('" ').lower())
        except (OSError, subprocess.SubprocessError, IndexError):
            pass
    return {i for i in ids if i and i != "\\"}


def read_task_xml(name: str) -> tuple:
    """(code, text) of `schtasks /Query /XML` for one task; code None when schtasks can't be run."""
    try:
        done = run("schtasks", "/Query", "/TN", name, "/XML", timeout=30)
    except (OSError, subprocess.SubprocessError) as error:
        return None, repr(error)
    return done.returncode, (done.stdout or done.stderr).decode("utf-8", "replace")


def read_json_file(path: Path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except FileNotFoundError:
        return None
    except (OSError, ValueError) as error:
        return {"__unreadable__": repr(error)}


def xml_field(xml: str, tag: str) -> str | None:
    """One element's text from a task's XML, decoded: Task Scheduler writes `&` in a path as `&amp;`."""
    found = re.search(rf"<{tag}>(.*?)</{tag}>", xml, re.S)
    return html.unescape(found.group(1)) if found else None


def task_problems(xml: str, script_ok: Callable[[str], bool], word: str, ids: set, is_file: Callable[[str], bool]) -> list[str]:
    problems = []
    program = (xml_field(xml, "Command") or "").strip().strip('"')
    args = xml_field(xml, "Arguments") or ""
    if not program:
        problems.append("its action has no program")
    elif not is_file(program):
        problems.append(f"its program is missing: {program}")
    script = re.search(r'"([^"]+\.py)"|(\S+\.py)\b', args)
    script = (script.group(1) or script.group(2)) if script else ""
    if not script:
        problems.append("its action runs no script")
    elif not is_file(script):
        problems.append(f"its script is missing: {script}")
    elif not script_ok(script):
        problems.append(f"its action runs another script: {script}")
    if word not in re.findall(r"[\w-]+", args.replace(script, " ")):
        problems.append(f"its action doesn't run `{word}`: {args.strip()}")
    logon, user = (xml_field(xml, "LogonType") or "").strip(), (xml_field(xml, "UserId") or "").strip()
    if logon != "InteractiveToken":
        problems.append(f"it doesn't run in this user's session (logon {logon or 'not given'})")
    if user.lower() not in ids:
        problems.append(f"it runs as another user: {user or 'not given'}")
    enabled = re.search(r"<Settings>.*?<Enabled>(true|false)</Enabled>", xml, re.S | re.I)
    if enabled and enabled.group(1).lower() == "false":
        problems.append("it is disabled")
    return problems


def verify(root: Path = ROOT, path: Path = STATE, now: float | None = None, task_xml: Callable = read_task_xml,
           read_json: Callable = read_json_file, is_file: Callable = os.path.isfile, ids: set | None = None) -> dict:
    """Whether the watchdog will really tick this repo: its own task runs this checkout's guard as this user, is
    enabled, and has ticked lately. Each problem has a reason."""
    now = time.time() if now is None else now
    ids = user_ids() if ids is None else ids
    own = os.path.normcase(os.path.normpath(str(root / "scripts" / "run-guard.py")))
    problems = []
    code, xml = task_xml(TASK_NAME)
    if code is None:
        problems.append(f"the task {TASK_NAME} can't be read: {xml}")
    elif code != 0:
        problems.append(f"the task {TASK_NAME} is missing" if "cannot find" in xml.lower() else f"the task {TASK_NAME} can't be read: {xml.strip()[:200]}")
    else:
        problems += [f"{TASK_NAME}: {p}" for p in
                     task_problems(xml, lambda s: os.path.normcase(os.path.normpath(s)) == own, "watch", ids, is_file)]
    tick = read_json(path.with_name("last-tick.json"))
    at = parse_stamp(tick.get("at")) if isinstance(tick, dict) else None
    if at is None:
        problems.append("the watchdog hasn't ticked yet")
    elif now - at > 2 * WATCH_MINUTES * 60:
        problems.append(f"the watchdog hasn't ticked in {2 * WATCH_MINUTES} minutes (last {tick.get('at')})")
    elif not tick.get("ok"):
        problems.append(f"the watchdog's last tick failed: {tick.get('result')}")
    return {"healthy": not problems, "watchdog": TASK_NAME, "problems": problems}


def watch_tick(env: Env = REAL, path: Path = STATE, now: float | None = None) -> str:
    """One scheduled tick, with its result kept: slow reads first, then the lock, then the state as it now stands."""
    peek = load(path)
    if not peek.get("active"):
        return "inactive"
    env = snapshot(env, subscription=True, state=peek, now=now, stop_hung_child=True)
    launch = env.spawn

    def spawn(state: dict) -> dict:
        save_retrying(state, path)  # what this launch spends is saved before it can run; unsaved, nothing launches
        return launch(state)
    env = replace(env, spawn=spawn)
    with locked(path):
        state = load(path)
        merge_pending_wait(state, path, time.time() if now is None else now)
        # Saved before anything can launch: a state that can't be written would forget a resume, or a wait, and the
        # next tick would launch again, so an unwritable state launches nothing.
        state["last_tick_at"] = stamp(now)
        save_retrying(state, path)
        try:
            result = watch(state, env, now)
        except Exception as error:  # kept where `status` shows it; nothing started and the next tick tries again
            state["last_error"] = f"{stamp()} {error!r}"
            save(state, path)
            raise
        if result.startswith("resumed,"):
            state.pop("last_error", None)
        lines = state.pop("log_lines", [])
        save(state, path)
    for line in lines:
        log(line, path)
    return result


def hook(raw: str, env: Env = REAL, path: Path = STATE) -> str:
    """One hook event. Returns what the hook prints."""
    data = json.loads(raw or "{}")
    event = data.get("hook_event_name")
    peek = load(path)
    if event == "Stop" and peek.get("active") and peek.get("session_id") == data.get("session_id"):
        env = snapshot(env, state=peek)
    elif event == "UserPromptSubmit" and START.match(str(data.get("prompt") or "")):
        installed, modes = env.task_installed(), env.modes()
        env = replace(env, task_installed=lambda: installed, modes=lambda: modes)
    with locked(path):
        return handle_event(event, data, env, path)


def handle_event(event: str | None, data: dict, env: Env, path: Path) -> str:
    state = load(path)
    before = json.dumps(state, sort_keys=True)
    output = ""
    if event == "UserPromptSubmit":
        note = on_prompt(state, data, env)
        if note:
            output = json.dumps({"hookSpecificOutput": {"hookEventName": event, "additionalContext": note}})
    elif event == "SessionStart":
        note = on_session_start(state, data)
        if note:
            output = json.dumps({"hookSpecificOutput": {"hookEventName": event, "additionalContext": note}})
    elif event == "Stop":
        reason = on_stop(state, data, env)
        if reason:
            output = json.dumps({"decision": "block", "reason": reason})
        elif state.get("status") in ("drained", "stalled") and json.dumps(state, sort_keys=True) != before:
            output = json.dumps({"systemMessage": f"Run guard: run {state['status']}, {state.get('why')}."})
    elif event == "SessionEnd":
        on_session_end(state, data)
    elif event == "PostToolUse":
        on_cron_created(state, data)
    elif event == "StopFailure":
        on_stop_failure(state, data)
    if json.dumps(state, sort_keys=True) != before:
        try:
            save_retrying(state, path)
        except OSError:
            if event == "StopFailure" and state.get("wait_until"):
                keep_pending_wait(state, path)  # the wait is kept, and the next tick merges it
            raise
        log(f"{event}: {state.get('status')} {state.get('why', '')}".rstrip(), path)
        if state.get("wait_reason") and state.get("wait_set") != json.loads(before).get("wait_set"):
            log(f"waiting: {state['wait_reason']} until {stamp(state['wait_until'])}: {state.get('wait_detail') or ''}".rstrip(": "), path)
    return output
