#!/usr/bin/env python3
"""Keeps an unattended `process plan` run working inside its own open window; see scripts/lib/run_guard.py for the rules.

Nothing here restarts or resumes a run: a run starts only when the operator starts it, and ends with its session.

  hook                       one Claude Code hook event on stdin (.claude/settings.json)
  hook --runner antigravity  one Antigravity Stop hook event on stdin (.agents/hooks.json), for the run whose id the
                             launcher put in BOSUN_RUN_SESSION
  status                     prints "active" or why not, then the detail; a run whose session is gone is recorded as ended
  start --session <id>       switch the run on for a session the operator is about to start (scripts/start-run.ps1);
                             --runner antigravity --owner-pid <pid> for one that lives while that process does
  end --session <id>         record that session's run ended, as the launcher does once the CLI it started returns
  wait --reason R            hold the active run until a time, as review-dispatch.py does for a reviewer
  halt --ground G --reason R record a halt (data-loss, credentials, shared-infrastructure, wrong-calculation, operator)
  uninstall                  remove the scheduled task an older version installed ("<folder> run watchdog")
"""

from __future__ import annotations

import argparse
import calendar
import time
import json
import subprocess
import sys
from pathlib import Path

sys.dont_write_bytecode = True  # the guard's own cache files must not read as progress in the tree it watches
sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))

import run_guard as guard  # noqa: E402


def say(text: str) -> None:
    if sys.stdout is not None:  # a console-less parent gives no stdout
        print(text)


def uninstall() -> int:
    """The watchdog task was retired on 2026-11-12 (nothing resumes a run), and nothing installs it any more: this
    removes one an older version left behind."""
    name = f"{guard.ROOT.name} run watchdog"
    done = subprocess.run(["schtasks", "/Delete", "/F", "/TN", name], capture_output=True, text=True, **guard.hidden())
    say((done.stdout or done.stderr).strip())
    return done.returncode


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    hook = commands.add_parser("hook")
    hook.add_argument("--runner", choices=guard.RUNNERS, default="claude")
    commands.add_parser("uninstall")
    status = commands.add_parser("status")
    status.add_argument("--json", action="store_true", help="print only the JSON")
    wait = commands.add_parser("wait", help="hold the active run until a time, as review-dispatch.py does for a reviewer")
    wait.add_argument("--reason", required=True)
    wait.add_argument("--until", help="when the wait ends, as 2026-11-08T12:00:00Z")
    wait.add_argument("--minutes", type=float, help="or how long it lasts")
    wait.add_argument("--detail", help="what is wrong and how to recover, shown by status")
    start = commands.add_parser("start")
    start.add_argument("--session", required=True)
    start.add_argument("--runner", choices=guard.RUNNERS, default="claude")
    start.add_argument("--owner-pid", type=int, help="for --runner antigravity: the process the run lives as long as")
    end = commands.add_parser("end")
    end.add_argument("--session", required=True)
    end.add_argument("--reason", default="")
    stop = commands.add_parser("halt")
    stop.add_argument("--ground", required=True, choices=guard.GROUNDS)
    stop.add_argument("--reason", required=True)
    args = parser.parse_args()

    if args.command == "hook" and args.runner == "antigravity":
        import os

        try:
            output = guard.hook_antigravity(sys.stdin.buffer.read().decode("utf-8", "replace"), os.environ.get("BOSUN_RUN_SESSION", ""))
        except Exception as error:  # fail open and print nothing: Antigravity reads only a decision, and none is given
            guard.log(f"antigravity hook failed open: {error!r}")
            guard.record_hook_failure(f"antigravity: {error!r}")
            return 0
        guard.clear_hook_failures()
        if output:
            say(output)
        return 0
    if args.command == "hook":
        try:
            output = guard.hook(sys.stdin.buffer.read().decode("utf-8", "replace"))
        except Exception as error:  # fail open, and say so: a broken guard must never wedge a session or pass unseen
            guard.log(f"hook failed open: {error!r}")
            guard.record_hook_failure(repr(error))  # where `status` shows it; never raises
            say(json.dumps({"systemMessage": f"Run guard failed and is not holding this run: {error}"}))
            return 0
        guard.clear_hook_failures()
        if output:
            say(output)
        return 0
    if args.command == "uninstall":
        return uninstall()

    if args.command == "status":
        unreadable = None
        try:
            state = guard.settle_gone()  # a run whose session is gone has ended, and is recorded so here
        except Exception as error:  # a corrupt or locked state is shown, never raised: health still has to show
            unreadable = repr(error)
            try:
                state = guard.load()  # as it stands, when it is the sessions or the lock that couldn't be read
                unreadable = None
            except (OSError, ValueError):
                state = {}
            if unreadable is None:
                state["last_error"] = f"{guard.stamp()} whether the session is still running couldn't be told: {error!r}"
        word, data, lines = guard.status_view(state, guard.REAL, unreadable)
        if args.json:
            say(json.dumps(data))
            return 0
        say(word)
        for line in lines:
            say(line)
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
    if args.command == "end":
        with guard.locked():
            state = guard.load()
            if state.get("session_id") == args.session:
                guard.claim_hold(state, guard.read_hold(guard.REAL))  # the run's own hold, while it is still active
            ended = guard.end(state, args.session, args.reason)
            if ended:
                guard.log(f"end: {state.get('why')}")
            tried = state.get("hold_restore")
            changed = guard.release_hold(state, guard.REAL)  # the launcher's end is the run's end: no hook may follow it
            if state.get("hold_restore") is not tried:
                guard.log(f"end: hold restore exit {state['hold_restore'].get('exit')}")
            if ended or changed:
                guard.save(state)
        say(state.get("status") or ("active" if state.get("active") else "inactive"))
        return 0
    with guard.locked():
        state = guard.load()
        if args.command == "start":
            started = None
            if args.owner_pid:
                try:
                    started = guard.process_started(args.owner_pid)
                except OSError as error:
                    say(str(error))
                    return 1
            try:
                guard.start(state, args.session, guard.REAL, runner=args.runner, owner_pid=args.owner_pid, owner_started=started)
            except (ValueError, OSError) as error:
                say(str(error))
                return 1
        else:
            guard.claim_hold(state, guard.read_hold(guard.REAL))  # the run's own hold, while it is still active
            guard.halt(state, args.ground, args.reason)
            guard.release_hold(state, guard.REAL)  # a halt ends the run: its hold goes back now, not at a later hook
        guard.save(state)
    say(state.get("status", ""))
    if args.command == "halt":
        say("The session finishes its turn and is not sent back. To end it now, close its window.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
