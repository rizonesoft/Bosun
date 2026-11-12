"""Keeps an unattended `process plan` run working inside its own open window until the plan says it is done.

A run starts only when the operator starts it, and it lives in the session's window (the owner's rule of
2026-11-12). The guard keeps that live session working, and nothing more:

- The session ends its turn: the Stop hook sends it back while runnable rows remain.
- The session sits idle (an API error, a yield): a CronCreate tick in that session wakes it.

Nothing restarts or resumes a run. When its session is gone (the window closed, a crash, a reboot) the run has
ended: it is recorded as ended, `status` says so, and no scheduled task, watchdog or launcher brings it back.

The prompt switches the run on, so no one has to remember to. Besides its session going, the operator or the
plan ends it: an operator stop, no runnable rows left, or a recorded halt. Stalling (no commit and no change to
the tree across many attempts) also ends it, so a stuck run cannot spend without limit. Fails open: a broken
guard must never wedge a session.
"""

from __future__ import annotations

import hashlib
import json
import math
import os
import re
import subprocess
import sys
import time
from contextlib import contextmanager
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Callable, Iterator

ROOT = Path(__file__).resolve().parents[2]
STATE = Path(os.environ.get("RUN_GUARD_STATE") or ROOT / ".git" / "run-guard" / "state.json")
CRON = "7,22,37,52 * * * *"

MAX_IDLE_BLOCKS = 5  # sends back in a row with nothing changed, before the turn may end
MAX_YIELDS = 3  # those yields in a row with nothing changed, before the run is called stalled
HOOK_FAILURES = "guard-health.json"
HOOK_FAILURES_KEPT = 10
HEARTBEAT_RENEW_DAYS = 6  # CronCreate jobs expire after 7 days
LOCK_WAIT = 5  # seconds, well under every hook's timeout: slow reads happen before the lock, see snapshot

# Waits for what only time or the owner can fix. The owner chose subscription only, so a limit costs time, never money.
USAGE_WAIT = 30 * 60  # a usage limit that gives no reset time is looked at again after this
SIGN_IN_WAIT = 30 * 60
OVERLOADED_WAIT = 5 * 60
SAVE_TRIES = 3
PENDING = "pending-wait.json"  # a wait the state file couldn't take, kept beside it until the next hook event merges it
START_GRACE = 120  # seconds a run started for a session (`start --session`) may wait for that session to appear
LONGEST_RESET = 8 * 86400  # a weekly limit's reset is the furthest a real one goes; anything past it is misread
# StopFailure's `error` (https://code.claude.com/docs/en/hooks), by what the run does about it.
FAILURE_CLASSES = {
    "rate_limit": "usage-limit",
    "authentication_failed": "auth", "cloud_credential_error": "auth",
    "oauth_org_not_allowed": "auth", "account_on_hold": "auth", "billing_error": "auth",
    "overloaded": "overloaded", "server_error": "overloaded",
}
ACCOUNT_ERRORS = {"oauth_org_not_allowed", "account_on_hold", "billing_error"}

DRAINED = "no runnable rows left; that is not the plan being done, `todo-graph.py query stats` shows what is blocked or unstamped"
GROUNDS = ("data-loss", "credentials", "shared-infrastructure", "wrong-calculation", "operator")

START = re.compile(r"^\s*(please\s+)?/?process[- ](the\s+)?plan\s*[.!]?\s*$", re.IGNORECASE)
STOP = re.compile(
    r"^\s*(please\s+)?(stop|pause|halt)((\s+(the|this|that|it|run|plan|process|work|working|now|everything|cleanly|safely|and|here))*)\s*[.!]?\s*$",
    re.IGNORECASE,
)
MENTIONS_STOP = re.compile(r"\b(stop|pause|halt|cancel|abort|quit)\b", re.IGNORECASE)
USER_EXITS = {"prompt_input_exit", "logout", "clear"}  # the operator's own exits; a closed window reports another reason
GONE = "its window closed, it crashed or the machine restarted"


@dataclass
class Env:
    """What the guard reads from the world, so tests can stand in for it."""

    rows: Callable[[], list[str] | None]
    signature: Callable[[], str]
    # Whether a session is running, from Claude Code's own record of it.
    alive: Callable[[str], bool]
    # What a run with no runnable row does next: {"state": complete | parked | waiting | ci-pending | ci-unavailable
    # | needs-push | needs-ci-repair | unknown, "detail": str, "report": the acceptance text}.
    finish: Callable[[], dict] = lambda: {"state": "drained"}
    # The wakes in todo/wakes.json whose check passes now (read only).
    wakes: Callable[[], list] = lambda: []
    # Write the acceptance file for a run that ends complete or parked, returning its path; OSError when it can't.
    write_acceptance: Callable[[str, str], str] = lambda kind, text: ""
    # Which review is in flight now, or None.
    reviewing: Callable[[], object] = lambda: None
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
    """When the state can't be saved, the wait alone goes to a small file of its own, which the next hook event merges."""
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
    """A wait that has passed gives the run a fresh no-progress budget: the time spent waiting is no sign that the run
    is stuck."""
    for key in ("wait_until", "wait_reason", "wait_detail", "wait_set"):
        state.pop(key, None)
    state.update(idle=0, yields=0, changed_at=now, signature=signature, last_wait_ended=stamp(now))


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


def snapshot(env: Env, state: dict | None = None, now: float | None = None) -> Env:
    """Takes the slow reads now, so the lock is never held across them and a stop can always get in. With nothing to
    run, that includes the finish line and the wakes, which ask git, CI and each wake's check; but not while the run
    waits (`state`), whose wait is the backoff: only a run waiting on its rows checks its wakes then."""
    rows, signature = env.rows(), env.signature()
    env = replace(env, rows=lambda: rows, signature=lambda: signature)
    if rows == []:
        held = state is not None and waiting(state, now)
        found = {"state": "unknown", "detail": "not asked while the run waits"}
        if not held:
            try:
                found = env.finish()
            except Exception as error:
                found = {"state": "unknown", "detail": repr(error)}
        woken = env.wakes() if not held or (state or {}).get("wait_reason") == "rows-waiting" else []
        env = replace(env, finish=lambda: found, wakes=lambda: woken)
    return env


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
    if not state.get("active"):
        state.clear()
        state.update(active=True, status="active", started=stamp(), changed_at=time.time())
    state.pop("started_at", None)  # the session is here, so it is judged like any other from now on
    state["session_id"] = session
    return heartbeat_note()


def heartbeat_current(state: dict, now: float | None = None) -> bool:
    age = (time.time() if now is None else now) - state.get("heartbeat_epoch", 0)
    return age < HEARTBEAT_RENEW_DAYS * 86400


WOKEN = "wake"  # a wake's check passes while its Need is still on the section: applying it is the work
FINISH_STEPS = {
    "needs-push": "push the finished work to origin/main, so CI can prove it",
    "needs-ci-repair": "CI is red on the pushed head: fix it under process-phase's red-first policy",
    WOKEN: "a wake's check passes: run `python scripts/todo-graph.py wake --apply`, commit it, then take the row it frees",
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
    now = time.time()
    # A run waiting on its rows goes on early when a wake's check passes: the session applies the wake.
    woken = waiting(state, now) and state.get("wait_reason") == "rows-waiting" and bool(env.wakes())
    if (waiting(state, now) and not woken) or env.reviewing() or env.ci_waiting():
        # The turn may end, and it isn't idleness: nothing can be done until a wait passes, a review in flight must not
        # have the tree change under it, and a CI `wait` starts no next row. Its notification, or the heartbeat,
        # takes the row up again.
        return None
    if state.get("wait_until") is not None:
        end_wait(state, now, env.signature())
    left = env.rows()
    if left == [] and woken:
        left = [WOKEN]
    elif left == []:
        settled = settle(state, env, now)
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


def start(state: dict, session: str, env: Env, now: float | None = None) -> None:
    """Switch the run on for a session the operator is about to start (scripts/start-run.ps1), or take over a run
    whose own session is gone. The session isn't running yet, so it has START_GRACE to appear before it is judged."""
    owner = state.get("session_id")
    if state.get("active") and owner not in (None, session) and env.alive(owner):
        raise ValueError(f"session {owner} owns the active run and is alive; never run two writers on one tree")
    now = time.time() if now is None else now
    if not state.get("active"):
        state.clear()
        state.update(active=True, status="active", started=stamp(now), changed_at=now)
    state.update(session_id=session, started_at=now)


def on_session_end(state: dict, data: dict) -> None:
    """The run lives in its session: when that ends, for whatever reason, so does the run. Nothing resumes it."""
    if not state.get("active") or state.get("session_id") != data.get("session_id"):
        return
    reason = data.get("reason")
    if reason in USER_EXITS:
        end_run(state, "stopped", f"operator ended the session ({reason})")
    else:
        end_run(state, "ended", f"the session ended ({reason or 'no reason given'}); nothing resumes a run: `process plan` in a new session starts one")


def close_if_gone(state: dict, env: Env, now: float | None = None) -> bool:
    """Record an active run whose session is no longer running as ended, returning whether it was. A session that
    ends with no SessionEnd (a closed window, a crash, a reboot) leaves the state active, and nothing resumes a run,
    so whoever next reads the state (`status`) settles it. `env.alive` raises when it can't tell, which ends nothing:
    not knowing is not the same as gone."""
    owner = state.get("session_id")
    if not state.get("active") or not owner:
        return False
    now = time.time() if now is None else now
    if now - state.get("started_at", 0) < START_GRACE or env.alive(owner):
        return False
    end_run(state, "ended", f"session {owner} is gone ({GONE}); nothing resumes a run: `process plan` in a new session starts one")
    return True


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


WORK_STATES = ("ready", "needs-repair", "needs-filing", "needs-verification", "needs-closeout")
# How long a run with nothing to run waits before it looks again, by why it waits.
LOOK_AGAIN = {"rows-waiting": 30 * 60, "ci-pending": 10 * 60, "ci-unavailable": 30 * 60, "acceptance-unwritable": 10 * 60,
              "finish-unreadable": 10 * 60}
FINISH_WORK = ("needs-push", "needs-ci-repair")  # what the finish line asks of the session itself


def settle(state: dict, env: Env, now: float) -> str:
    """What a run with no runnable row does, from what the graph and CI say is left. Returns "work: <item>" when the
    session has a finishing step to take, "waiting" when only time, a person, a host or CI can move it (looked at
    again when the session's heartbeat next ends a turn after the wait), or the status it ended with."""
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

    A record that cannot be judged raises, so no run is taken over or called ended: not knowing is not the same as dead.
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


def real_alive(session: str) -> bool:
    return any(r.get("sessionId") == session for r in session_records())


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


REAL = Env(rows=real_rows, signature=real_signature, alive=real_alive, finish=real_finish, wakes=real_wakes,
           write_acceptance=write_acceptance, reviewing=review_in_flight, ci_waiting=ci_in_flight)


def health(state: dict, path: Path = STATE, now: float | None = None) -> dict:
    """What `status` adds to the state: is the session's own wake armed, and did a hook fail open?"""
    return {"heartbeat_current": heartbeat_current(state, now), "hook_failures": hook_failures(path)}


def settle_gone(env: Env = REAL, path: Path = STATE, now: float | None = None) -> dict:
    """The state as `status` shows it, after recording a run whose session is gone as ended. A lock or a state that
    can't be written leaves it as read: the next look tries again."""
    peek = load(path)
    now = time.time() if now is None else now
    if not close_if_gone(dict(peek), env, now):  # on a copy: the slow read of every session record is before the lock
        return peek
    with locked(path):
        state = load(path)
        if state.get("session_id") == peek.get("session_id") and close_if_gone(state, replace(env, alive=lambda session: False), now):
            save_retrying(state, path)
            log(f"status: {state.get('status')} {state.get('why', '')}".rstrip(), path)
        return state


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


def hook(raw: str, env: Env = REAL, path: Path = STATE) -> str:
    """One hook event. Returns what the hook prints."""
    data = json.loads(raw or "{}")
    event = data.get("hook_event_name")
    peek = load(path)
    if event == "Stop" and peek.get("active") and peek.get("session_id") == data.get("session_id"):
        env = snapshot(env, state=peek)
    with locked(path):
        return handle_event(event, data, env, path)


def handle_event(event: str | None, data: dict, env: Env, path: Path) -> str:
    state = load(path)
    before = json.dumps(state, sort_keys=True)
    merge_pending_wait(state, path, time.time())
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
                keep_pending_wait(state, path)  # the wait is kept, and the next hook event merges it
            raise
        log(f"{event}: {state.get('status')} {state.get('why', '')}".rstrip(), path)
        if state.get("wait_reason") and state.get("wait_set") != json.loads(before).get("wait_set"):
            log(f"waiting: {state['wait_reason']} until {stamp(state['wait_until'])}: {state.get('wait_detail') or ''}".rstrip(": "), path)
    return output
