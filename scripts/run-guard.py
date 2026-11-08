#!/usr/bin/env python3
"""Keeps an unattended `process plan` run going; see scripts/lib/run_guard.py for the rules.

  hook                       one Claude Code hook event on stdin (.claude/settings.json)
  watch                      one scheduled tick: resume a dead run, never beside a live session
  status                     prints "active" or why not, then the detail
  start --session <id>       switch the run on for a session already running
  halt --ground G --reason R record a halt (data-loss, credentials, shared-infrastructure, wrong-calculation, operator)
  install | uninstall        add or remove the Windows scheduled task for `watch`
"""

from __future__ import annotations

import argparse
import calendar
import time
import json
import os
import subprocess
import sys
from pathlib import Path

sys.dont_write_bytecode = True  # the guard's own cache files must not read as progress in the tree it watches
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))

import run_guard as guard  # noqa: E402


def say(text: str) -> None:
    if sys.stdout is not None:  # pythonw, as the scheduled task runs it, has no stdout
        print(text)


def install() -> int:
    python = Path(sys.executable)
    windowless = python.with_name("pythonw.exe")
    if not windowless.exists():  # a console every few minutes would interrupt the desktop, so nothing is scheduled
        say(f"pythonw.exe isn't next to {python}, so the watchdog would open a console each time: install Python from "
            f"python.org, which includes it, then run this again")
        return 1
    action = f'"{windowless}" "{Path(__file__).resolve()}" watch'
    done = subprocess.run(["schtasks", "/Create", "/F", "/TN", guard.TASK_NAME, "/SC", "MINUTE", "/MO", str(guard.WATCH_MINUTES), "/IT", "/TR", action], capture_output=True, text=True)
    say((done.stdout or done.stderr).strip())
    return done.returncode


def uninstall() -> int:
    done = subprocess.run(["schtasks", "/Delete", "/F", "/TN", guard.TASK_NAME], capture_output=True, text=True)
    say((done.stdout or done.stderr).strip())
    return done.returncode


def watch() -> int:
    try:
        result = guard.watch_tick()
    except Exception as error:
        guard.log(f"watch failed: {error!r}")
        guard.record_tick(repr(error), False)
        return 1
    guard.log(f"watch: {result}")
    guard.record_tick(result, True)
    say(result)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("hook", "watch", "install", "uninstall"):
        commands.add_parser(name)
    commands.add_parser("status").add_argument("--json", action="store_true", help="print only the JSON")
    wait = commands.add_parser("wait", help="hold the active run until a time, as review-dispatch.py does for a reviewer")
    wait.add_argument("--reason", required=True)
    wait.add_argument("--until", help="when the wait ends, as 2026-11-08T12:00:00Z")
    wait.add_argument("--minutes", type=float, help="or how long it lasts")
    wait.add_argument("--detail", help="what is wrong and how to recover, shown by status")
    start = commands.add_parser("start")
    start.add_argument("--session", required=True)
    start.add_argument("--permission-mode")
    stop = commands.add_parser("halt")
    stop.add_argument("--ground", required=True, choices=guard.GROUNDS)
    stop.add_argument("--reason", required=True)
    args = parser.parse_args()

    if args.command == "hook":
        try:
            output = guard.hook(sys.stdin.buffer.read().decode("utf-8", "replace"))
        except Exception as error:  # fail open, and say so: a broken guard must never wedge a session or pass unseen
            guard.log(f"hook failed open: {error!r}")
            say(json.dumps({"systemMessage": f"Run guard failed and is not holding this run: {error}"}))
            return 0
        if output:
            say(output)
        return 0
    if args.command == "install":
        return install()
    if args.command == "uninstall":
        return uninstall()

    if args.command == "status":
        state = guard.load()
        word = ("waiting" if guard.waiting(state) else "active") if state.get("active") else (state.get("status") or "inactive")
        detail = {k: state.get(k) for k in ("session_id", "why", "started", "ended", "restarts", "yields", "last_restart", "last_error", "heartbeat_at", "last_api_failure", "wait_reason", "wait_detail") if state.get(k) is not None}
        if guard.waiting(state):
            detail["wait_until"] = guard.stamp(state["wait_until"])
        data = {"state": word, **detail, **guard.health(state)}
        if args.json:
            say(json.dumps(data))
            return 0
        say(word)
        if word == "waiting":
            say(f"waiting for {state['wait_reason']} until {detail['wait_until']}" + (f": {state['wait_detail']}" if state.get("wait_detail") else ""))
        say(json.dumps(data))
        return 0
    if args.command == "wait":
        if (args.until is None) == (args.minutes is None):
            say("give --until or --minutes")
            return 2
        try:
            until = calendar.timegm(time.strptime(args.until, "%Y-%m-%dT%H:%M:%SZ")) if args.until else time.time() + args.minutes * 60
        except ValueError:
            say(f"--until must look like 2026-11-08T12:00:00Z, not {args.until}")
            return 2
        try:
            with guard.locked():
                state = guard.load()
                if not state.get("active"):
                    say("no active run, so nothing waits")
                    return 4  # review-dispatch.py's GUARD_NO_RUN: nothing to wait, which isn't a failure
                try:
                    guard.set_wait(state, args.reason, until, args.detail)
                except ValueError as error:
                    say(str(error))
                    return 2
                guard.save(state)
        except (OSError, ValueError) as error:  # a lock that timed out, or a state that can't be read or saved
            say(f"the wait couldn't be saved: {error}")
            return 1
        guard.log(f"wait: {args.reason} until {guard.stamp(until)}")
        say(f"waiting until {guard.stamp(until)}")
        return 0
    if args.command == "watch":
        return watch()
    with guard.locked():
        state = guard.load()
        if args.command == "start":
            try:
                note = guard.start(state, args.session, args.permission_mode, guard.REAL)
            except ValueError as error:
                say(str(error))
                return 1
            if note:
                say(note)
        else:
            guard.halt(state, args.ground, args.reason)
        guard.save(state)
    say(state.get("status", ""))
    child = state.get("child")
    if args.command == "halt" and child and guard.real_child_alive(child):
        say(f"Resumed session {child['pid']} is still working and will stop at the end of its turn. To end it now: taskkill /PID {child['pid']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
