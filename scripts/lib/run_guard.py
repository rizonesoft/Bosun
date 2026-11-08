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
import json
import math
import os
import re
import shutil
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
CHILD_HANG_HOURS = 3  # a resumed session this long without a change is reported, not replaced
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
    live_here: Callable[[], list[str]]
    child_alive: Callable[[dict], bool]
    spawn: Callable[[dict], dict]
    task_installed: Callable[[], bool] = lambda: True
    # The modes `claude --permission-mode` accepts, as its --help lists them.
    modes: Callable[[], set] = lambda: set(KNOWN_MODES)
    # What a run with no runnable row does next: {"state": complete | parked | waiting | ci-pending | ci-unavailable
    # | needs-push | needs-ci-repair | unknown, "detail": str, "report": the acceptance text}.
    finish: Callable[[], dict] = lambda: {"state": "unknown"}
    # The wakes in todo/wakes.json whose check passes now (read only).
    wakes: Callable[[], list] = lambda: []
    # Write the acceptance file for a run that ends complete or parked, returning its path; OSError when it can't.
    write_acceptance: Callable[[str, str], str] = lambda kind, text: ""
    # Which review is in flight now, or None.
    reviewing: Callable[[], object] = lambda: None
    # (ok, reason, detail): whether a launch now would run on the subscription.
    subscription: Callable[[], tuple] = lambda: (True, "", "")


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


def snapshot(env: Env, subscription: bool = False) -> Env:
    """Takes the slow reads now, so the lock is never held across them and a stop can always get in."""
    rows, signature = env.rows(), env.signature()
    env = replace(env, rows=lambda: rows, signature=lambda: signature)
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
        return note + " The watchdog task is not installed, so a crash would end this run: run `bosun-heartbeat add .` where Bosun is installed, or `python scripts/run-guard.py install` first."
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
    holds a run. Ported from Recto (D:\\GitHub\\Recto\\scripts\\campaign_guard.py:780)."""
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


def on_stop(state: dict, data: dict, env: Env) -> str | None:
    if not state.get("active") or state.get("session_id") != data.get("session_id"):
        return None
    if waiting(state) or env.reviewing():
        # The turn may end, and it isn't idleness: nothing can be done until a wait passes, and a review in flight
        # must not have the tree change under it. Its notification, or the heartbeat, resumes the row.
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


def start(state: dict, session: str, mode: str | None, env: Env) -> str | None:
    """Start or take over a run; returns a note for the operator, or None."""
    owner = state.get("session_id")
    if state.get("active") and owner not in (None, session) and env.alive(owner):
        raise ValueError(f"session {owner} owns the active run and is alive; never run two writers on one tree")
    mode, note = check_mode(mode, env)
    if not state.get("active"):
        state.clear()
        state.update(active=True, status="active", started=stamp(), changed_at=time.time())
    state.update(session_id=session, permission_mode=mode)
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
        woken = waiting(state, now) and state.get("wait_reason") == "rows-waiting" and env.wakes()
        if waiting(state, now) and not woken:
            return f"waiting: {state.get('wait_reason')} until {stamp(state['wait_until'])}"
        end_wait(state, now, signature)
    if signature != state.get("restart_signature"):
        state.update(restart_signature=signature, restarts=0, changed_at=now)
    quiet = (now - state.get("changed_at", now)) / 3600
    if state.get("child") and env.child_alive(state["child"]):
        if (now - max(state.get("changed_at", now), state.get("launched_at", 0))) / 3600 >= CHILD_HANG_HOURS:
            end_run(state, "stalled", f"resumed session {state['child'].get('pid')} changed nothing for {CHILD_HANG_HOURS} hours; it was left running")
            return "stalled"
        return "resumed session still running"
    live = env.live_here()
    if live:
        return f"session live here: {live[0]}"
    reviewing = env.reviewing()
    if reviewing:
        # A writer resumed now would change the tree under the reviewer, as a crashed writer's surviving dispatcher
        # would see; the review's end, or the next tick, resumes the run. Neither a restart nor idleness.
        return f"waiting: a review is in flight ({reviewing})"
    if quiet >= MAX_QUIET_HOURS:
        end_run(state, "expired", f"nothing changed for {MAX_QUIET_HOURS} hours, so it was not resumed; `process plan` starts it again")
        return "expired"
    left = env.rows()
    if left == []:
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
    child = env.spawn(state)  # a launch that fails raises and uses no attempt: nothing ran, so nothing was tried
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


def run(*args: str, cwd: Path = ROOT) -> subprocess.CompletedProcess:
    env = {**os.environ, "PYTHONIOENCODING": "utf-8"}
    return subprocess.run(args, cwd=cwd, capture_output=True, env=env, timeout=20, stdin=subprocess.DEVNULL, **hidden())


WORK_STATES = ("ready", "needs-repair", "needs-filing", "needs-verification", "needs-closeout")
# How long a run with nothing to run waits before it looks again, by why it waits.
LOOK_AGAIN = {"rows-waiting": 30 * 60, "ci-pending": 10 * 60, "ci-unavailable": 30 * 60, "acceptance-unwritable": 10 * 60}
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


def process_started(pid: int) -> int | None:
    """When a process started, as a FILETIME, so a reused process id is not taken for the session. None when it is gone."""
    if os.name != "nt":
        try:
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


def real_live_here() -> list[str]:
    return [str(r.get("sessionId")) for r in session_records() if same_folder(r.get("cwd"), ROOT)]


def real_child_alive(child: dict) -> bool:
    started = process_started(int(child.get("pid", 0)))
    return started is not None and started == child.get("started")


def resume_command(state: dict, claude: str) -> list[str]:
    command = [claude, "-p", "process plan"]
    if state.get("session_id"):
        command += ["--resume", state["session_id"]]
    # The operator's own mode for this run and no wider; `default` is Claude Code's own and needs no flag.
    if state.get("permission_mode") and state["permission_mode"] != "default":
        command += ["--permission-mode", state["permission_mode"]]
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
    if data.get("authMethod") != "claude.ai":
        return False, "subscription-unavailable", (f"Claude Code would sign in with {data.get('authMethod')}, which bills an API account: "
                                                   "remove the API key or apiKeyHelper from the environment and settings, then sign in with the subscription")
    return True, "", ""


def real_modes() -> set:
    """The modes `claude --help` lists for --permission-mode, or the known list when it can't be read."""
    claude = shutil.which("claude")
    try:
        done = subprocess.run([claude or "claude", "--help"], capture_output=True, timeout=30, stdin=subprocess.DEVNULL, **hidden())
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
        done = subprocess.run([claude, "auth", "status", "--json"], cwd=ROOT, capture_output=True, env=child_env(os.environ),
                              timeout=AUTH_TIMEOUT, stdin=subprocess.DEVNULL, **hidden())
        data = json.loads(done.stdout.decode("utf-8", "replace"))
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        return False, "sign-in-needed", f"Claude Code's sign-in couldn't be checked ({error}): run `claude auth status` to see why"
    return subscription_verdict(data)


CI_ANSWERS = {0: None, 1: "needs-ci-repair", 4: "ci-pending", 5: "ci-pending"}  # scripts/ci_status.py's exits


def acceptance_report(kind: str, found: dict) -> str:
    """The acceptance file's text: what the owner's acceptance pass starts from."""
    lines = [f"# Acceptance: implementation {'complete' if kind == 'complete' else 'parked'}", "",
             f"Written by the run guard on {stamp()} when the unattended run ended {kind}.", ""]
    parked = sorted(ref for ref, s in found.get("roots", {}).items() if s == "parked")
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
        out = run(sys.executable, str(root / "scripts" / "todo-graph.py"), "query", "finish", cwd=root)
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
    fetched = run("git", "fetch", "-q", "origin", "main", cwd=root)
    head = run("git", "rev-parse", "HEAD", cwd=root).stdout.decode().strip()
    remote = run("git", "rev-parse", "origin/main", cwd=root).stdout.decode().strip()
    if fetched.returncode != 0 or not remote:
        return {"state": "ci-unavailable", "detail": "origin/main couldn't be fetched"}
    if head != remote:
        return {"state": "needs-push", "detail": f"local {head[:7]} isn't origin/main {remote[:7]}"}
    ci = run(sys.executable, str(root / "scripts" / "ci_status.py"), "--sha", head, "--use-proven", cwd=root)
    answer = CI_ANSWERS.get(ci.returncode, "ci-unavailable")
    if answer:
        return {"state": answer, "detail": f"CI on {head[:7]}: {ci.stdout.decode('utf-8', 'replace').strip()[-300:]}"}
    return {"state": kind, "detail": f"CI proven on {head[:7]}", "report": acceptance_report(kind, found)}


def real_wakes(root: Path = ROOT) -> list:
    try:
        out = run(sys.executable, str(root / "scripts" / "todo-graph.py"), "wake", "--check", cwd=root)
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
        child = subprocess.Popen(resume_command(state, claude), cwd=ROOT, stdin=subprocess.DEVNULL, stdout=out, stderr=out,
                                 creationflags=flags, env=child_env(os.environ, lambda line: log(line, STATE)))  # beside the state in use
    return {"pid": child.pid, "started": process_started(child.pid)}


def heartbeat_task_exists() -> bool:
    """Whether the heartbeat's task will really fire: it exists and isn't disabled, as `heartbeat.py status` reads it."""
    if os.name != "nt":
        return False
    done = run("schtasks", "/Query", "/TN", "Bosun heartbeat", "/XML")
    if done.returncode != 0:
        return False
    text = done.stdout.decode("utf-8", "replace")
    enabled = re.search(r"<Settings>.*?<Enabled>(true|false)</Enabled>", text, re.S | re.I)
    if not (enabled is None or enabled.group(1).lower() == "true"):
        return False
    # A task whose program or script is gone fires and does nothing, so it doesn't count as a watchdog.
    command = re.search(r"<Command>(.*?)</Command>", text, re.S)
    if not command or not Path(command.group(1).strip().strip('"')).is_file():
        return False
    arguments = re.search(r"<Arguments>(.*?)</Arguments>", text, re.S)
    script = re.search(r'"([^"]+\.py)"|(\S+\.py)\b', arguments.group(1) if arguments else "")
    return script is None or Path(script.group(1) or script.group(2)).is_file()


def on_heartbeat(root: Path = ROOT) -> bool:
    """Whether Bosun's global heartbeat ticks this repo, which stands in for a task of its own. Being listed isn't
    enough: the heartbeat's own task has to exist, or nothing would tick it."""
    try:
        data = json.loads((Path(os.environ["LOCALAPPDATA"]) / "Bosun" / "heartbeat" / "repos.json").read_text(encoding="utf-8"))
        here = os.path.normcase(str(root.resolve()))
        listed = any(os.path.normcase(str(Path(r["path"]).resolve())) == here for r in data["repos"] if not r.get("distro"))
        return listed and heartbeat_task_exists()
    except (KeyError, OSError, TypeError, ValueError, AttributeError):
        return False  # no heartbeat, or one it can't read: the repo's own task is then what counts


def real_task_installed() -> bool:
    if on_heartbeat():
        return True
    if os.name != "nt":
        return True
    return run("schtasks", "/Query", "/TN", TASK_NAME).returncode == 0


REAL = Env(real_rows, real_signature, real_alive, real_live_here, real_child_alive, real_spawn, real_task_installed,
           real_modes, real_finish, real_wakes, write_acceptance, review_in_flight, real_subscription)


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
    }


def watch_tick(env: Env = REAL, path: Path = STATE, now: float | None = None) -> str:
    """One scheduled tick, with its result kept: slow reads first, then the lock, then the state as it now stands."""
    if not load(path).get("active"):
        return "inactive"
    env = snapshot(env, subscription=True)
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
        save(state, path)
    return result


def hook(raw: str, env: Env = REAL, path: Path = STATE) -> str:
    """One hook event. Returns what the hook prints."""
    data = json.loads(raw or "{}")
    event = data.get("hook_event_name")
    peek = load(path)
    if event == "Stop" and peek.get("active") and peek.get("session_id") == data.get("session_id"):
        env = snapshot(env)
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
