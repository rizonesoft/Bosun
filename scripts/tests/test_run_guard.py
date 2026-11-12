from __future__ import annotations

import json
import subprocess
import os
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))

import run_guard as guard  # noqa: E402


class World:
    """Stands in for the machine: rows, the tree, sessions and what gets spawned."""

    def __init__(self, rows=("D01 T02 §4",), signature="one", alive=(), live=(), child=False):
        self.rows, self.sig, self.live, self.child = rows, signature, list(live), child
        self.alive = set(alive)
        self.spawned = []

    def env(self):
        return guard.Env(
            rows=lambda: None if self.rows is None else list(self.rows),
            signature=lambda: self.sig,
            alive=lambda s: s in self.alive,
            live_here=lambda: self.live,
            child_alive=lambda c: self.child,
            spawn=lambda s: self.spawned.append(dict(s)) or {"pid": 1, "started": 2},
        )


def active(**extra):
    return {"active": True, "status": "active", "session_id": "me", **extra}


class Prompts(unittest.TestCase):
    def test_process_plan_switches_the_run_on_in_each_spelling(self):
        for prompt in ("process plan", "/process-plan", "Process the plan", "process plan."):
            state = {}
            note = guard.on_prompt(state, {"prompt": prompt, "session_id": "me", "permission_mode": "acceptEdits"}, World().env())
            self.assertTrue(state.get("active"), prompt)
            self.assertEqual(state["permission_mode"], "acceptEdits")
            self.assertIn("CronCreate", note)

    def test_please_also_starts_it(self):
        state = {}
        guard.on_prompt(state, {"prompt": "please process the plan", "session_id": "me"}, World().env())
        self.assertTrue(state["active"])

    def test_a_missing_watchdog_is_said_when_the_run_starts(self):
        env = World().env()
        env.task_installed = lambda: False
        self.assertIn("run-guard.py install", guard.on_prompt({}, {"prompt": "process plan", "session_id": "me"}, env))

    def test_an_audit_a_named_phase_or_talk_about_the_plan_does_not(self):
        for prompt in ("process plan --audit", "process-plan 2", "why did process plan stop", "I see we stopped"):
            state = {}
            self.assertIsNone(guard.on_prompt(state, {"prompt": prompt, "session_id": "me"}, World().env()))
            self.assertFalse(state.get("active"), prompt)

    def test_an_operator_stop_ends_the_run(self):
        for prompt in ("stop", "Stop cleanly and safely", "pause the run", "please halt", "stop.", "stop the process plan now"):
            state = active()
            guard.on_prompt(state, {"prompt": prompt, "session_id": "me"}, World().env())
            self.assertFalse(state["active"], prompt)
            self.assertEqual(state["status"], "stopped")

    def test_words_about_stopping_do_not_end_the_run(self):
        for prompt in ("I see we stopped", "stop hooks for this repo need work", "The other Windows repo also stopped.", "stop using subagents"):
            state = active()
            guard.on_prompt(state, {"prompt": prompt, "session_id": "me"}, World().env())
            self.assertTrue(state["active"], prompt)

    def test_a_less_plain_stop_goes_to_the_model_with_the_way_to_halt(self):
        for prompt in ("Please stop the run after the current section", "can you cancel this for now"):
            state = active()
            note = guard.on_prompt(state, {"prompt": prompt, "session_id": "me"}, World().env())
            self.assertIn("halt --ground operator", note, prompt)
            self.assertTrue(state["active"])
        self.assertIsNone(guard.on_prompt(active(), {"prompt": "what is next", "session_id": "me"}, World().env()))
        for machine in ("<task-notification>the agent can stop</task-notification>", "Run heartbeat: if it prints anything else stop"):
            self.assertIsNone(guard.on_prompt(active(), {"prompt": machine, "session_id": "me"}, World().env()))
        self.assertIsNone(guard.on_prompt(active(), {"prompt": "stop the build", "session_id": "someone else"}, World().env()))

    def test_a_second_session_cannot_take_a_live_run(self):
        state = active()
        note = guard.on_prompt(state, {"prompt": "process plan", "session_id": "other"}, World(alive=["me"]).env())
        self.assertIn("two writers", note)
        self.assertEqual(state["session_id"], "me")

    def test_a_session_can_take_a_run_whose_owner_died_and_keeps_its_counters(self):
        state = active(restarts=2)
        guard.on_prompt(state, {"prompt": "process plan", "session_id": "new"}, World().env())
        self.assertEqual(state["session_id"], "new")
        self.assertEqual(state["restarts"], 2)


class Stops(unittest.TestCase):
    def test_nothing_is_held_when_no_run_is_active(self):
        self.assertIsNone(guard.on_stop({}, {"session_id": "me"}, World().env()))
        self.assertIsNone(guard.on_stop({"active": False, "session_id": "me"}, {"session_id": "me"}, World().env()))

    def test_another_session_is_never_held(self):
        self.assertIsNone(guard.on_stop(active(), {"session_id": "other"}, World().env()))

    def test_runnable_rows_send_the_turn_back_naming_the_next(self):
        reason = guard.on_stop(active(), {"session_id": "me", "stop_hook_active": True}, World().env())
        self.assertIn("D01 T02 §4", reason)
        self.assertIn("Do not stop", reason)

    def test_no_runnable_rows_finishes_the_run(self):
        state = active()
        self.assertIsNone(guard.on_stop(state, {"session_id": "me"}, World(rows=()).env()))
        self.assertEqual((state["active"], state["status"]), (False, "drained"))
        self.assertIn("not the plan being done", state["why"])

    def test_rows_that_cannot_be_read_never_end_a_run(self):
        state = active()
        self.assertIsNotNone(guard.on_stop(state, {"session_id": "me"}, World(rows=None).env()))
        self.assertTrue(state["active"])

    def test_no_change_yields_after_the_ceiling_then_stalls(self):
        state, world, data = active(), World(), {"session_id": "me"}
        allowed = 0
        for _ in range(200):
            if guard.on_stop(state, data, world.env()) is None:
                allowed += 1
            if not state["active"]:
                break
        self.assertEqual(allowed, guard.MAX_YIELDS)
        self.assertEqual(state["status"], "stalled")

    def test_progress_resets_the_ceiling(self):
        state, world, data = active(), World(), {"session_id": "me"}
        for turn in range(100):
            world.sig = f"commit {turn}"
            self.assertIsNotNone(guard.on_stop(state, data, world.env()))
        self.assertTrue(state["active"])


class SessionEvents(unittest.TestCase):
    def test_a_normal_exit_ends_the_run_and_a_crash_reason_does_not(self):
        for reason in sorted(guard.USER_EXITS):
            state = active()
            guard.on_session_end(state, {"session_id": "me", "reason": reason})
            self.assertFalse(state["active"], reason)
        for reason in ("other", None):
            state = active()
            guard.on_session_end(state, {"session_id": "me", "reason": reason})
            self.assertTrue(state["active"], reason)

    def test_another_session_exiting_does_not_end_the_run(self):
        state = active()
        guard.on_session_end(state, {"session_id": "other", "reason": "prompt_input_exit"})
        self.assertTrue(state["active"])

    def test_only_the_runs_own_session_is_told_to_continue(self):
        self.assertIn("Continue process-plan", guard.on_session_start(active(), {"session_id": "me"}))
        state = active()
        self.assertIsNone(guard.on_session_start(state, {"session_id": "new"}))
        self.assertEqual(state["session_id"], "me")

    def test_a_heartbeat_about_to_expire_is_said(self):
        state = active(heartbeat_epoch=1000.0)
        self.assertTrue(guard.heartbeat_current(state, now=1000.0 + 5 * 86400))
        self.assertFalse(guard.heartbeat_current(state, now=1000.0 + guard.HEARTBEAT_RENEW_DAYS * 86400))

    def test_an_api_failure_is_kept_for_status(self):
        state = active()
        guard.on_stop_failure(state, {"session_id": "me", "error": "rate_limit"})
        self.assertIn("rate_limit", state["last_api_failure"])
        other = active()
        guard.on_stop_failure(other, {"session_id": "other", "error": "rate_limit"})
        self.assertNotIn("last_api_failure", other)

    def test_start_will_not_take_a_live_run(self):
        state = active()
        with self.assertRaises(ValueError):
            guard.start(state, "other", None, World(alive=["me"]).env())
        self.assertEqual(state["session_id"], "me")
        guard.start(state, "other", "acceptEdits", World().env())
        self.assertEqual((state["session_id"], state["permission_mode"]), ("other", "acceptEdits"))

    def test_the_heartbeat_is_recorded_and_its_absence_is_said(self):
        state = active()
        self.assertIn("not armed", guard.on_stop(state, {"session_id": "me"}, World().env()))
        guard.on_cron_created(state, {"session_id": "other"})
        self.assertNotIn("heartbeat_at", state)
        guard.on_cron_created(state, {"session_id": "me"})
        self.assertNotIn("not armed", guard.on_stop(state, {"session_id": "me"}, World(signature="two").env()))


class Watch(unittest.TestCase):
    def test_nothing_starts_without_an_active_run(self):
        world = World()
        self.assertEqual(guard.watch({}, world.env()), "inactive")
        self.assertEqual(world.spawned, [])

    def test_never_beside_a_live_session_even_an_unrelated_one(self):
        world = World(live=["someone"])
        self.assertIn("someone", guard.watch(active(), world.env()))
        self.assertEqual(world.spawned, [])

    def test_never_beside_the_session_it_already_resumed(self):
        world = World(child=True)
        guard.watch(active(child={"pid": 1, "started": 2}), world.env())
        self.assertEqual(world.spawned, [])

    def test_a_dead_session_resumes_once_a_tick(self):
        world, state = World(), active()
        self.assertIn("attempt 1", guard.watch(state, world.env()))
        self.assertEqual(len(world.spawned), 1)
        self.assertEqual(state["child"], {"pid": 1, "started": 2})

    def test_resumes_that_change_nothing_stall_and_progress_resets_them(self):
        world, state = World(), active()
        for _ in range(guard.MAX_RESTARTS):
            guard.watch(state, world.env())
        self.assertEqual(guard.watch(state, world.env()), "stalled")
        self.assertEqual(len(world.spawned), guard.MAX_RESTARTS)
        self.assertFalse(state["active"])
        world, state = World(), active()
        for turn in range(guard.MAX_RESTARTS * 3):
            world.sig = str(turn)
            guard.watch(state, world.env())
        self.assertTrue(state["active"])

    def test_a_plan_with_nothing_left_finishes_instead_of_resuming(self):
        world, state = World(rows=()), active()
        self.assertEqual(guard.watch(state, world.env()), "drained")
        self.assertEqual(world.spawned, [])

    def test_a_run_walked_away_from_expires_instead_of_resuming(self):
        world, state = World(), active(restart_signature="one", changed_at=1000.0)
        self.assertEqual(guard.watch(state, world.env(), now=1000.0 + guard.MAX_QUIET_HOURS * 3600), "expired")
        self.assertEqual(world.spawned, [])
        self.assertFalse(state["active"])

    def test_a_change_keeps_a_run_from_expiring(self):
        world, state = World(signature="two"), active(restart_signature="one", changed_at=1000.0)
        self.assertIn("attempt 1", guard.watch(state, world.env(), now=1000.0 + guard.MAX_QUIET_HOURS * 3600))

    def test_a_hung_resumed_session_is_stopped_and_replaced(self):
        world, state = World(child=True), active(child={"pid": 7, "started": 2}, restart_signature="one", changed_at=1000.0)
        stopped = []
        env = guard.replace(world.env(), tree=lambda c: [{"pid": 7, "started": 2, "name": "claude.exe", "command": ""}],
                            stop_tree=lambda c, procs: stopped.append([p["pid"] for p in procs]) or [])
        self.assertEqual(guard.watch(state, env, now=1000.0 + guard.CHILD_HANG_HOURS * 3600), "resumed, attempt 1")
        self.assertEqual(stopped, [[7]])
        self.assertEqual(len(world.spawned), 1)
        self.assertIn("stopped resumed session 7", " ".join(state["log_lines"]))

    def test_a_quiet_session_that_crashed_does_not_condemn_its_replacement(self):
        world, state = World(), active(restart_signature="one", changed_at=1000.0)
        launched = 1000.0 + guard.CHILD_HANG_HOURS * 3600 + 60
        self.assertIn("attempt 1", guard.watch(state, world.env(), now=launched))
        world.child = True
        self.assertEqual(guard.watch(state, world.env(), now=launched + 600), "resumed session still running")
        self.assertTrue(state["active"])
        # Its hang is measured from its own launch, not from the crash's quiet before it: only now is it stopped.
        self.assertEqual(guard.watch(state, world.env(), now=launched + guard.CHILD_HANG_HOURS * 3600 - 60),
                         "resumed session still running")
        self.assertIn("attempt 2", guard.watch(state, world.env(), now=launched + guard.CHILD_HANG_HOURS * 3600))

    def test_not_knowing_who_is_alive_starts_nothing(self):
        world, state = World(), active()
        env = world.env()

        def unknown():
            raise OSError("cannot tell")

        env.live_here = unknown
        with self.assertRaises(OSError):
            guard.watch(state, env)
        self.assertEqual(world.spawned, [])

    def test_the_resume_uses_the_operators_own_mode_and_no_wider(self):
        command = guard.resume_command(active(permission_mode="acceptEdits"), "claude")
        self.assertEqual(command, ["claude", "-p", "process plan", "--resume", "me", "--permission-mode", "acceptEdits"])
        self.assertNotIn("--permission-mode", guard.resume_command(active(), "claude"))
        self.assertNotIn("--dangerously-skip-permissions", " ".join(command))


class Rows(unittest.TestCase):
    def test_only_ready_rows_are_offered(self):
        rows = [{"ref": f"D01 T02 §{n}", "state": state} for n, state in enumerate(
            ("ready", "waiting-host", "waiting-person", "waiting-dependency", "parked", "verified"), 1)]
        self.assertEqual(guard.runnable(rows), ["D01 T02 §1"])

    def test_a_row_needing_filing_is_work(self):
        self.assertEqual(guard.runnable([{"ref": "D01 T02 §1", "state": "needs-filing"}]), ["D01 T02 §1"])

    def test_a_run_with_only_repairable_work_left_goes_on(self):
        rows = [{"ref": "D01 T02 §1", "state": "needs-repair"}, {"ref": "D01 T02 §2", "state": "parked"}]
        self.assertEqual(guard.runnable(rows), ["D01 T02 §1"])
        reason = guard.on_stop(active(), {"session_id": "me", "stop_hook_active": True}, World(rows=guard.runnable(rows)).env())
        self.assertIn("D01 T02 §1", reason)

    def test_a_display_session_row_runs_with_a_desktop_and_waits_without_one(self):
        with_desktop = [{"ref": "D01 T03 §1", "needs": ["display-session"], "state": "ready"}]
        without = [{"ref": "D01 T03 §1", "needs": ["display-session"], "state": "waiting-host"}]
        self.assertEqual(guard.runnable(with_desktop), ["D01 T03 §1"])
        self.assertEqual(guard.runnable(without), [])

    def answers(self, code, stdout):
        return mock.patch.object(guard, "run", return_value=subprocess.CompletedProcess([], code, stdout, b""))

    def test_real_rows_reads_query_disposition(self):
        rows = [{"ref": "D01 T03 §1", "state": "ready"}, {"ref": "D01 T03 §2", "state": "parked"}]
        with self.answers(0, json.dumps(rows).encode()) as run:
            self.assertEqual(guard.real_rows(), ["D01 T03 §1"])
        self.assertEqual(run.call_args.args[-2:], ("query", "disposition"))

    def test_an_unreadable_answer_never_ends_a_run(self):
        for code, stdout in ((1, b""), (0, b"not json"), (0, b"[1, 2]"), (0, b"{}")):
            with self.subTest(stdout), self.answers(code, stdout):
                self.assertIn(guard.real_rows(), (None, []) if stdout == b"{}" else (None,))
        with mock.patch.object(guard, "run", side_effect=OSError("gone")):
            self.assertIsNone(guard.real_rows())


class OwnWatchdog(unittest.TestCase):
    """The repo's own task is its only watchdog: nothing outside the repo stands in for it (the global heartbeat was
    retired on 2026-11-10, after a moved repo was left with no watchdog at all)."""

    def test_a_leftover_heartbeat_registry_listing_this_repo_is_no_watchdog(self):
        with tempfile.TemporaryDirectory() as folder, mock.patch.dict(os.environ, {"LOCALAPPDATA": folder}):
            beat = Path(folder) / "Bosun" / "heartbeat"
            beat.mkdir(parents=True)
            (beat / "repos.json").write_text(json.dumps({"repos": [{"path": str(guard.ROOT), "state": "active"}]}), encoding="utf-8")
            asked = []

            def run(*args, **kwargs):
                asked.append(args)
                return subprocess.CompletedProcess(args, 1, b"", b"ERROR: The system cannot find the file specified.")
            with mock.patch.object(guard, "run", side_effect=run), mock.patch.object(guard.os, "name", "nt"):
                self.assertFalse(guard.real_task_installed())
            self.assertEqual(asked, [("schtasks", "/Query", "/TN", guard.TASK_NAME)])

    def test_the_prompt_names_the_repo_s_own_installer_and_nothing_else(self):
        env = World().env()
        env.task_installed = lambda: False
        note = guard.on_prompt({}, {"prompt": "process plan", "session_id": "me"}, env)
        self.assertIn("python scripts/run-guard.py install", note)
        self.assertNotIn("bosun", note.lower())


class Halts(unittest.TestCase):
    def test_a_halt_needs_a_named_ground_and_a_reason(self):
        with self.assertRaises(ValueError):
            guard.halt(active(), "too many gaps", "because")
        with self.assertRaises(ValueError):
            guard.halt(active(), "credentials", "  ")
        state = active()
        guard.halt(state, "credentials", "token rejected")
        self.assertEqual((state["active"], state["status"]), (False, "halted"))
        self.assertIn("token rejected", state["why"])


class HookEntry(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "state.json"

    def event(self, world=None, **data):
        return guard.hook(json.dumps(data), (world or World()).env(), self.path)

    def test_a_whole_run_from_the_prompt_to_a_block_to_the_finish(self):
        started = json.loads(self.event(hook_event_name="UserPromptSubmit", prompt="process plan", session_id="me"))
        self.assertIn("CronCreate", started["hookSpecificOutput"]["additionalContext"])
        blocked = json.loads(self.event(hook_event_name="Stop", session_id="me"))
        self.assertEqual(blocked["decision"], "block")
        finished = json.loads(self.event(World(rows=()), hook_event_name="Stop", session_id="me"))
        self.assertIn("drained", finished["systemMessage"])
        self.assertFalse(guard.load(self.path)["active"])

    def test_an_ordinary_stop_prints_nothing_and_writes_nothing(self):
        self.assertEqual(self.event(hook_event_name="Stop", session_id="me"), "")
        self.assertFalse(self.path.exists())

    def test_a_corrupt_state_file_is_not_taken_for_no_run(self):
        self.path.write_text("{not json", encoding="utf-8")
        with self.assertRaises(ValueError):
            self.event(hook_event_name="Stop", session_id="me")

    def test_a_held_lock_refuses_rather_than_overwrites(self):
        code = (
            "import sys,time; sys.path.insert(0, sys.argv[1]); import run_guard as g, pathlib; "
            "p=pathlib.Path(sys.argv[2]); l=g.locked(p); l.__enter__(); print('held', flush=True); time.sleep(20)"
        )
        holder = subprocess.Popen([sys.executable, "-c", code, str(Path(guard.__file__).parent), str(self.path)], stdout=subprocess.PIPE)
        self.addCleanup(holder.stdout.close)
        self.addCleanup(holder.wait)
        self.addCleanup(holder.kill)
        self.assertIn(b"held", holder.stdout.readline())
        with self.assertRaises(TimeoutError):
            with guard.locked(self.path, wait=0.2):
                self.fail("two holders of the state lock")
        holder.kill()
        holder.wait()
        with guard.locked(self.path, wait=5):
            pass

    def test_status_says_when_a_tick_failed_or_has_gone_quiet(self):
        env = World().env()
        unseen = guard.health(active(), self.path, env, now=1000.0)
        self.assertTrue(unseen["last_tick_stale"])
        guard.record_tick("OSError('no')", False, self.path)
        failed = guard.health(active(), self.path, env)
        self.assertFalse(failed["last_tick"]["ok"])
        self.assertTrue(failed["last_tick_stale"])
        guard.record_tick("inactive", True, self.path)
        self.assertFalse(guard.health(active(), self.path, env)["last_tick_stale"])
        self.assertTrue(guard.health(active(), self.path, env, now=time.time() + guard.WATCH_MINUTES * 60 * 4)["last_tick_stale"])

    def test_the_cron_tool_records_the_heartbeat(self):
        self.path.write_text(json.dumps(active()), encoding="utf-8")
        self.event(hook_event_name="PostToolUse", tool_name="CronCreate", session_id="me")
        self.assertIn("heartbeat_at", guard.load(self.path))

    def test_a_stop_gets_in_while_a_tick_does_its_slow_reads(self):
        self.path.write_text(json.dumps(active()), encoding="utf-8")
        reading, release = threading.Event(), threading.Event()
        world = World()
        env = world.env()

        def slow_rows():
            reading.set()
            release.wait(10)
            return ["D01 T02 §4"]

        env.rows = slow_rows
        results = []
        tick = threading.Thread(target=lambda: results.append(guard.watch_tick(env, self.path)))
        tick.start()
        self.assertTrue(reading.wait(5))
        self.event(hook_event_name="UserPromptSubmit", prompt="stop", session_id="me")
        release.set()
        tick.join(10)
        self.assertEqual(results, ["inactive"])
        self.assertEqual(world.spawned, [])
        self.assertEqual(guard.load(self.path)["status"], "stopped")

    def test_a_failed_launch_uses_no_attempt_and_its_error_stays_until_one_works(self):
        self.path.write_text(json.dumps(active()), encoding="utf-8")
        world = World()
        env = world.env()

        def fails(_state):
            raise OSError("claude is not on PATH")

        broken = guard.replace(env, spawn=fails)
        for _ in range(guard.MAX_RESTARTS + 2):
            with self.assertRaises(OSError):
                guard.watch_tick(broken, self.path)
        state = guard.load(self.path)
        self.assertTrue(state["active"])
        self.assertEqual(state.get("restarts", 0), 0)
        self.assertIn("not on PATH", state["last_error"])
        self.assertIn("attempt 1", guard.watch_tick(env, self.path))
        self.assertNotIn("last_error", guard.load(self.path))


class Signature(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for command in (["init", "-q"], ["-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "start"]):
            subprocess.run(["git", *command], cwd=self.root, check=True, capture_output=True)

    def test_work_in_an_untracked_file_or_a_new_folder_changes_it(self):
        first = guard.real_signature(self.root)
        (self.root / "new").mkdir()
        (self.root / "new" / "a.txt").write_text("one", encoding="utf-8")
        second = guard.real_signature(self.root)
        self.assertNotEqual(first, second)
        (self.root / "new" / "a.txt").write_text("one and two", encoding="utf-8")
        third = guard.real_signature(self.root)
        self.assertNotEqual(second, third)
        (self.root / "new" / "b.txt").write_text("more", encoding="utf-8")
        self.assertNotEqual(third, guard.real_signature(self.root))

    def test_nothing_changing_leaves_it_alone(self):
        (self.root / "a.txt").write_text("one", encoding="utf-8")
        self.assertEqual(guard.real_signature(self.root), guard.real_signature(self.root))

    def test_a_review_attempt_record_in_build_is_not_progress(self):
        (self.root / ".gitignore").write_text((guard.ROOT / ".gitignore").read_text(encoding="utf-8"), encoding="utf-8")
        subprocess.run(["git", "add", "-A"], cwd=self.root, check=True, capture_output=True)
        subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "ignore"],
                       cwd=self.root, check=True, capture_output=True)
        before = guard.real_signature(self.root)
        (self.root / "build" / "review-failures").mkdir(parents=True)
        (self.root / "build" / "review-attempts.json").write_text('{"passes": {}}', encoding="utf-8")
        (self.root / "build" / "review-failures" / "x.txt").write_text("failed", encoding="utf-8")
        self.assertEqual(guard.real_signature(self.root), before)
        world, state = World(signature=before), active(signature=before, restart_signature=before, restarts=1, idle=2)
        guard.on_stop(state, {"session_id": "me"}, world.env())
        guard.watch(state, world.env())
        self.assertEqual((state["idle"], state["restarts"]), (3, 2), "an attempt record reset idle or restarts")


class ReviewInFlight(unittest.TestCase):
    """A review that is running lets the turn end, and only while its dispatcher lives and its deadline holds."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.marks = self.root / "build" / "review-in-flight"
        self.marks.mkdir(parents=True)

    def mark(self, pid, deadline, name="astra"):
        (self.marks / f"{pid}-{name}.json").write_text(json.dumps({"pid": pid, "reviewer": name, "deadline": deadline}), encoding="utf-8")

    def env(self):
        return guard.replace(World().env(), reviewing=lambda: guard.review_in_flight(self.root))

    def dead_pid(self):
        done = subprocess.run([sys.executable, "-c", "import os; print(os.getpid())"], capture_output=True, text=True)
        return int(done.stdout)

    def test_live_dead_and_expired_marks(self):
        self.assertIsNone(guard.review_in_flight(self.root))
        self.mark(self.dead_pid(), time.time() + 600, "dead")
        self.mark(os.getpid(), time.time() - 1, "expired")
        (self.marks / "garbage.json").write_text("{bad", encoding="utf-8")
        self.assertIsNone(guard.review_in_flight(self.root), "a dead or expired mark held the run")
        self.assertIsNotNone(guard.on_stop(active(), {"session_id": "me"}, self.env()), "the turn wasn't sent back")
        self.mark(os.getpid(), time.time() + 600, "live")
        self.assertIn("live", guard.review_in_flight(self.root))
        self.assertIsNone(guard.on_stop(active(), {"session_id": "me"}, self.env()))

    def test_the_watchdog_resumes_no_writer_while_a_review_runs(self):
        world = World()
        state = active(restarts=1, restart_signature=world.sig)
        self.mark(os.getpid(), time.time() + 600)
        env = guard.replace(world.env(), reviewing=lambda: guard.review_in_flight(self.root))
        self.assertTrue(guard.watch(state, env).startswith("waiting: a review is in flight"))
        self.assertEqual((world.spawned, state["restarts"]), ([], 1))
        for path in self.marks.iterdir():
            path.unlink()
        self.assertIn("attempt 2", guard.watch(state, env))

    def test_repeated_stops_during_a_review_keep_the_idle_budget(self):
        world = World()
        state = active(signature=world.sig, idle=1, yields=1)
        self.mark(os.getpid(), time.time() + 600)
        env = self.env()
        for _ in range(guard.MAX_IDLE_BLOCKS * guard.MAX_YIELDS * 2):
            self.assertIsNone(guard.on_stop(state, {"session_id": "me"}, env))
        self.assertEqual((state["idle"], state["yields"], state["active"]), (1, 1, True))
        for path in self.marks.iterdir():
            path.unlink()
        self.assertIn("Unattended run is active", guard.on_stop(state, {"session_id": "me"}, env))


class CiInFlight(unittest.TestCase):
    """A CI gate that is polling holds the next row, as process-phase says a `wait` does, and only while the gate lives
    and its two hours hold. Found by the disposable campaign on 2026-11-08: a gate polling in the background and the
    Stop hook's "continue" let §2 start before §1's CI cleared."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "build").mkdir()

    def record(self, pid, since):
        (self.root / "build" / "ci-gate.json").write_text(json.dumps({"waiting": {"sha": "a" * 40, "since": since, "pid": pid}}),
                                                          encoding="utf-8")

    def env(self, world=None):
        return guard.replace((world or World()).env(), ci_waiting=lambda: guard.ci_in_flight(self.root))

    def test_only_a_live_gate_inside_its_limit_holds(self):
        self.assertIsNone(guard.ci_in_flight(self.root))
        dead = int(subprocess.run([sys.executable, "-c", "import os; print(os.getpid())"], capture_output=True, text=True).stdout)
        self.record(dead, time.time())
        self.assertIsNone(guard.ci_in_flight(self.root), "a dead gate's record held the run")
        self.record(os.getpid(), time.time() - guard.CI_WAIT_LIMIT - 1)
        self.assertIsNone(guard.ci_in_flight(self.root), "a wait past its limit held the run")
        (self.root / "build" / "ci-gate.json").write_text("{bad", encoding="utf-8")
        self.assertIsNone(guard.ci_in_flight(self.root))
        self.record(os.getpid(), time.time())
        self.assertIn("a" * 12, guard.ci_in_flight(self.root))

    def test_the_turn_may_end_and_no_writer_is_resumed_while_the_gate_polls(self):
        world = World()
        state = active(restarts=1, restart_signature=world.sig, signature=world.sig, idle=1, yields=1)
        self.record(os.getpid(), time.time())
        env = self.env(world)
        for _ in range(guard.MAX_IDLE_BLOCKS * guard.MAX_YIELDS * 2):
            self.assertIsNone(guard.on_stop(state, {"session_id": "me"}, env), "the Stop hook sent the turn to the next row")
        self.assertTrue(guard.watch(state, env).startswith("waiting: CI is being checked"))
        self.assertEqual((world.spawned, state["restarts"], state["idle"], state["active"]), ([], 1, 1, True))
        (self.root / "build" / "ci-gate.json").write_text(json.dumps({"proven": "a" * 40}), encoding="utf-8")
        self.assertIn("Unattended run is active", guard.on_stop(state, {"session_id": "me"}, env))
        self.assertIn("attempt 2", guard.watch(state, env))

    def test_the_limit_is_the_gates_own(self):
        sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
        import ci_status
        self.assertEqual(guard.CI_WAIT_LIMIT, ci_status.WAIT_LIMIT_SECONDS)


class Modes(unittest.TestCase):
    """A run's permission mode is one Claude Code accepts, checked the same way however the run starts."""

    def test_the_mode_check(self):
        env = World().env()
        with self.assertRaisesRegex(ValueError, "'sudo-mode'"):
            guard.check_mode("sudo-mode", env)
        self.assertEqual(guard.check_mode("bypassPermissions", env), ("bypassPermissions", None))
        for mode in (None, "", "default", "acceptEdits"):
            with self.subTest(mode):
                kept, note = guard.check_mode(mode, env)
                self.assertEqual(kept, mode or "default")
                self.assertIn("bypassPermissions", note)

    def test_both_entry_points_refuse_an_unknown_mode_before_changing_anything(self):
        env = World().env()
        state = {}
        message = guard.on_prompt(state, {"prompt": "process plan", "session_id": "me", "permission_mode": "sudo-mode"}, env)
        self.assertIn("did not start", message)
        self.assertEqual(state, {})
        with self.assertRaisesRegex(ValueError, "sudo-mode"):
            guard.start(state, "me", "sudo-mode", env)
        self.assertEqual(state, {})

    def test_both_entry_points_keep_a_known_mode_and_say_when_it_isnt_the_campaign_s(self):
        env = World().env()
        state = {}
        note = guard.on_prompt(state, {"prompt": "process plan", "session_id": "me", "permission_mode": "acceptEdits"}, env)
        self.assertEqual(state["permission_mode"], "acceptEdits")
        self.assertIn("start it with `claude --permission-mode bypassPermissions`", note)
        state = {}
        self.assertIsNone(guard.start(state, "me", "bypassPermissions", env))
        self.assertEqual(state["permission_mode"], "bypassPermissions")
        state = {}
        guard.start(state, "me", None, env)
        self.assertEqual(state["permission_mode"], "default")
        self.assertNotIn("--permission-mode", guard.resume_command(state, "claude"))

    def test_a_fresh_process_reads_the_saved_mode_back_into_the_resume(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.json"
            env = {**os.environ, "RUN_GUARD_STATE": str(path)}
            script = [sys.executable, str(guard.ROOT / "scripts" / "run-guard.py")]
            done = subprocess.run([*script, "start", "--session", "sess-9", "--permission-mode", "bypassPermissions"],
                                  env=env, capture_output=True, text=True, timeout=120)
            self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
            refused = subprocess.run([*script, "start", "--session", "sess-9", "--permission-mode", "sudo-mode"],
                                     env=env, capture_output=True, text=True, timeout=120)
            self.assertEqual(refused.returncode, 1)
            self.assertIn("sudo-mode", refused.stdout)
            state = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(guard.resume_command(state, "claude"),
                             ["claude", "-p", "process plan", "--resume", "sess-9", "--permission-mode", "bypassPermissions"])

    def test_the_model_the_run_started_with_is_kept_for_every_resume(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.json"
            env = {**os.environ, "RUN_GUARD_STATE": str(path)}
            script = [sys.executable, str(guard.ROOT / "scripts" / "run-guard.py")]
            done = subprocess.run([*script, "start", "--session", "sess-9", "--permission-mode", "bypassPermissions",
                                   "--model", "opus"], env=env, capture_output=True, text=True, timeout=120)
            self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
            refused = subprocess.run([*script, "start", "--session", "sess-9", "--model", "opus; rm"],
                                     env=env, capture_output=True, text=True, timeout=120)
            self.assertEqual(refused.returncode, 1)
            self.assertIn("is not a model name", refused.stdout)
            state = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(guard.resume_command(state, "claude"),
                             ["claude", "-p", "process plan", "--resume", "sess-9", "--permission-mode", "bypassPermissions",
                              "--model", "opus"])
            # A lost transcript starts afresh, on the same model.
            self.assertEqual(guard.resume_command({**state, "fresh_next": True}, "claude")[-2:], ["--model", "opus"])
        self.assertNotIn("--model", guard.resume_command(active(), "claude"))

    def test_the_modes_come_from_claude_s_own_help(self):
        text = ('  --permission-mode <mode>   Permission mode to use for the session\n'
                '                             (choices: "alpha", "betaMode",\n'
                '                             "gamma")\n  --other  something (choices: "nope")\n')
        done = subprocess.CompletedProcess([], 0, text.encode(), b"")
        with mock.patch.object(guard, "run_tree", return_value=done):
            self.assertEqual(guard.real_modes(), {"alpha", "betaMode", "gamma"})

    def test_help_that_cant_be_read_falls_back_to_the_known_modes(self):
        for failure in (FileNotFoundError("no claude"), subprocess.TimeoutExpired("claude", 30)):
            with self.subTest(type(failure).__name__), mock.patch.object(guard, "run_tree", side_effect=failure):
                self.assertEqual(guard.real_modes(), set(guard.KNOWN_MODES))
        with mock.patch.object(guard, "run_tree", return_value=subprocess.CompletedProcess([], 0, b"no options here", b"")):
            self.assertEqual(guard.real_modes(), set(guard.KNOWN_MODES))

    def test_this_machine_s_claude_lists_bypass_mode(self):
        self.assertIn("bypassPermissions", guard.real_modes())


class UsageLimits(unittest.TestCase):
    """A limit, a sign-in problem or an overloaded API waits without spending the stall budget, on the subscription only."""

    def failure(self, error, text="", session="me"):
        return {"hook_event_name": "StopFailure", "session_id": session, "error": error, "error_details": text,
                "last_assistant_message": f"API Error: {text}"}

    def test_each_error_value_has_its_class(self):
        expected = {"rate_limit": "usage-limit", "authentication_failed": "auth", "cloud_credential_error": "auth",
                    "oauth_org_not_allowed": "auth", "account_on_hold": "auth", "billing_error": "auth",
                    "overloaded": "overloaded", "server_error": "overloaded", "invalid_request": "other",
                    "model_not_found": "other", "max_output_tokens": "other", "unknown": "other", None: "other"}
        for error, kind in expected.items():
            with self.subTest(error):
                self.assertEqual(guard.failure_class({"error": error}), kind)

    def test_each_reset_form(self):
        now = 1_762_590_000.0  # ten thousand seconds before the epoch reset below
        self.assertEqual(guard.reset_time("Claude AI usage limit reached|1000900", now), None)  # not ten digits
        self.assertEqual(guard.reset_time("Claude AI usage limit reached|1762600000", now), 1762600000.0)
        self.assertEqual(guard.reset_time("Your limit resets in 3 hours.", now), now + 3 * 3600)
        self.assertEqual(guard.reset_time("Limit will reset in 45 minutes", now), now + 45 * 60)
        at = guard.reset_time("Your limit will reset at 5pm (Africa/Johannesburg).", now)
        clock = time.localtime(at)
        self.assertEqual((clock.tm_hour, clock.tm_min), (17, 0))
        self.assertTrue(now < at <= now + 86400)
        self.assertIsNone(guard.reset_time("429 Too Many Requests", now))
        for wild in ("resets in 100000000 hours", "resets in " + "9" * 400 + " hours", "usage limit|0000000001",
                     "resets in 9 days... resets in 300 hours"):
            with self.subTest(wild[:30]):
                self.assertIsNone(guard.reset_time(wild, now))

    def test_a_wild_reset_still_saves_a_30_minute_wait_through_the_hook(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.json"
            guard.save(active(), path)
            guard.hook(json.dumps(self.failure("rate_limit", "resets in 100000000 hours")), World().env(), path)
            state = guard.load(path)
            self.assertEqual(state["wait_reason"], "usage-limit")
            self.assertLess(state["wait_until"] - time.time(), 1801)

    def test_a_usage_limit_without_a_reset_waits_30_minutes(self):
        state = active()
        guard.on_stop_failure(state, self.failure("rate_limit", "429 Too Many Requests"), now=1000)
        self.assertEqual((state["wait_reason"], state["wait_until"]), ("usage-limit", 1000 + 1800))

    def test_a_5_hour_limit_ends_in_a_resume_not_stalled(self):
        world, state = World(), active(restarts=2, changed_at=0)
        start = 1_000_000.0
        guard.on_stop_failure(state, self.failure("rate_limit", "limit reached, resets in 5 hours"), now=start)
        now = start
        while now < start + 5 * 3600:
            now += 300
            self.assertTrue(guard.watch(state, world.env(), now=now).startswith(("waiting", "resumed")))
            if now < start + 5 * 3600:
                self.assertEqual((world.spawned, state["restarts"]), ([], 2))
        self.assertEqual(len(world.spawned), 1)
        self.assertTrue(state["active"])
        self.assertEqual(state["restarts"], 1)  # the resume, on a fresh budget

    def test_overloaded_waits_5_minutes_and_other_only_records(self):
        state = active()
        guard.on_stop_failure(state, self.failure("overloaded"), now=1000)
        self.assertEqual((state["wait_reason"], state["wait_until"]), ("overloaded", 1300))
        state = active()
        guard.on_stop_failure(state, self.failure("max_output_tokens"), now=1000)
        self.assertNotIn("wait_until", state)
        self.assertIn("max_output_tokens", state["last_api_failure"])

    def test_sign_in_tells_an_expired_login_from_an_account_problem(self):
        expired, account = active(), active()
        guard.on_stop_failure(expired, self.failure("authentication_failed"), now=1000)
        guard.on_stop_failure(account, self.failure("billing_error"), now=1000)
        self.assertEqual((expired["wait_reason"], account["wait_reason"]), ("sign-in-needed", "sign-in-needed"))
        self.assertIn("/login", expired["wait_detail"])
        self.assertIn("owner's to sort out", account["wait_detail"])

    def test_the_launch_environment_has_no_paid_keys_and_says_which_went(self):
        environ = {name: "a-secret-value-1234" for name in guard.SCRUBBED}
        environ["PATH"] = "keep"
        lines = []
        env = guard.child_env(environ, lines.append)
        self.assertEqual(env, {"PATH": "keep"})
        self.assertEqual(len(lines), len(guard.SCRUBBED))
        for name, line in zip(guard.SCRUBBED, lines):
            self.assertIn(name, line)
            self.assertNotIn("a-secret-value", line)

    def test_only_a_subscription_sign_in_may_launch(self):
        cases = [({"loggedIn": True, "authMethod": "claude.ai", "apiProvider": "firstParty"}, (True, "")),
                 # `claude setup-token`'s long-lived token, which needs the subscription; `auth status` as it read on 2026-11-08
                 ({"loggedIn": True, "authMethod": "oauth_token", "apiProvider": "firstParty"}, (True, "")),
                 ({"loggedIn": True, "authMethod": "oauth_token", "apiProvider": "vertex"}, (False, "subscription-unavailable")),
                 ({"loggedIn": False}, (False, "sign-in-needed")),
                 ("not json", (False, "sign-in-needed")),
                 ({"loggedIn": True, "authMethod": "claude.ai", "apiProvider": "bedrock"}, (False, "subscription-unavailable")),
                 ({"loggedIn": True, "authMethod": "api_key", "apiProvider": "firstParty"}, (False, "subscription-unavailable"))]
        for data, expected in cases:
            with self.subTest(data):
                ok, reason, detail = guard.subscription_verdict(data)
                self.assertEqual((ok, reason), expected)
                self.assertTrue(ok or detail, "a refusal says what to do")

    def test_a_launch_that_would_bill_an_account_waits_instead(self):
        world, state = World(), active(restarts=1, restart_signature="one")
        env = guard.replace(world.env(), subscription=lambda: (False, "subscription-unavailable", "would sign in with api_key"))
        self.assertTrue(guard.watch(state, env, now=1000).startswith("waiting: subscription-unavailable"))
        self.assertEqual((world.spawned, state["restarts"]), ([], 1))
        self.assertIn("api_key", state["wait_detail"])

    def test_sign_in_failing_again_and_again_spends_nothing_and_resumes_when_it_returns(self):
        world, state = World(), active(restarts=0, changed_at=0)
        guard.on_stop_failure(state, self.failure("authentication_failed"), now=0)
        signed_in = [False]
        env = guard.replace(world.env(), subscription=lambda: (True, "", "") if signed_in[0] else (False, "sign-in-needed", "not signed in"))
        now = 0.0
        for _ in range(guard.MAX_QUIET_HOURS * 2):  # far past every stall and quiet limit, still signed out
            now += 1800 + 1
            self.assertTrue(guard.watch(state, env, now=now).startswith("waiting: sign-in-needed"))
        self.assertEqual((world.spawned, state["restarts"]), ([], 0))
        self.assertTrue(state["active"])
        signed_in[0] = True
        now += 1800 + 1
        self.assertIn("attempt 1", guard.watch(state, env, now=now))

    def test_the_hook_saves_the_wait_and_the_watchdog_honours_it_from_a_fresh_read(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.json"
            guard.save(active(), path)
            world = World()
            reset = int(time.time()) + 900
            raw = json.dumps(self.failure("rate_limit", f"usage limit reached|{reset}"))
            guard.hook(raw, world.env(), path)
            fresh = guard.load(path)
            self.assertEqual((fresh["wait_reason"], fresh["wait_until"]), ("usage-limit", float(reset)))
            self.assertIn("waiting: usage-limit", (Path(folder) / "run-guard.log").read_text(encoding="utf-8"))
            self.assertTrue(guard.watch_tick(world.env(), path, now=reset - 60).startswith("waiting"))
            self.assertEqual(world.spawned, [])
            self.assertIn("attempt 1", guard.watch_tick(world.env(), path, now=reset + 60))
            # Another session's failure sets nothing.
            guard.save(active(), path)
            guard.hook(json.dumps(self.failure("rate_limit", "x", session="someone-else")), world.env(), path)
            self.assertNotIn("wait_until", guard.load(path))
            # An operator halt during a wait is never resumed.
            guard.hook(raw, world.env(), path)
            with guard.locked(path):
                state = guard.load(path)
                guard.halt(state, "operator", "stop for now")
                guard.save(state, path)
            spawned = len(world.spawned)
            self.assertEqual(guard.watch_tick(world.env(), path, now=reset + 3600), "inactive")
            self.assertEqual(len(world.spawned), spawned)

    def test_status_shows_a_sign_in_wait_and_what_to_do(self):
        with tempfile.TemporaryDirectory() as folder:
            state = active()
            guard.on_stop_failure(state, self.failure("authentication_failed"))
            (Path(folder) / "state.json").write_text(json.dumps(state), encoding="utf-8")
            env = {**os.environ, "RUN_GUARD_STATE": str(Path(folder) / "state.json")}
            script = [sys.executable, str(guard.ROOT / "scripts" / "run-guard.py")]
            text = subprocess.run([*script, "status"], env=env, capture_output=True, text=True).stdout
            self.assertEqual(text.splitlines()[0], "waiting")
            self.assertIn("/login", text)
            data = json.loads(subprocess.run([*script, "status", "--json"], env=env, capture_output=True, text=True).stdout)
            self.assertEqual(data["wait_reason"], "sign-in-needed")
            self.assertIn("/login", data["wait_detail"])


FAKE_CLAUDE = """import json, os, sys, time
mode = os.environ.get("FAKE_CLAUDE_MODE", "ok")
with open(os.environ["FAKE_CLAUDE_OUT"], "a") as out:
    out.write(json.dumps({"argv": sys.argv[1:], "env": sorted(k for k in os.environ if k in %r)}) + "\\n")
if sys.argv[1:4] == ["auth", "status", "--json"]:
    if mode == "sleep":
        time.sleep(30)
    print({"ok": json.dumps({"loggedIn": True, "authMethod": "claude.ai", "apiProvider": "firstParty"}),
           "out": json.dumps({"loggedIn": False}), "garbage": "not json at all"}[mode])
"""


class LaunchBoundary(unittest.TestCase):
    """The real `claude auth status` and the real launch, through a fake claude on PATH."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        folder = Path(self.temp.name)
        script = folder / "fake_claude.py"
        script.write_text(FAKE_CLAUDE % (guard.SCRUBBED,), encoding="utf-8")
        if os.name == "nt":
            (folder / "claude.cmd").write_text(f'@"{sys.executable}" "{script}" %*\r\n', encoding="utf-8")
        else:
            (folder / "claude").write_text(f'#!/bin/sh\nexec "{sys.executable}" "{script}" "$@"\n', encoding="utf-8")
            (folder / "claude").chmod(0o755)
        self.out = folder / "calls.jsonl"
        secrets = {name: "a-secret-value-1234" for name in guard.SCRUBBED}
        patch = mock.patch.dict(os.environ, {"PATH": f"{folder}{os.pathsep}{os.environ['PATH']}", "FAKE_CLAUDE_OUT": str(self.out), **secrets})
        patch.start()
        self.addCleanup(patch.stop)

    def calls(self):
        return [json.loads(line) for line in self.out.read_text().splitlines()] if self.out.exists() else []

    @staticmethod
    def stop_tree(pid):
        """Stop a launched child and everything it started, and wait until it has gone."""
        if os.name == "nt":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)], capture_output=True, timeout=60)
        else:
            try:
                os.killpg(pid, 9)
            except OSError:
                pass
        deadline = time.time() + 30
        while guard.pid_alive(pid) and time.time() < deadline:
            time.sleep(0.1)

    def test_the_sign_in_check_runs_without_paid_keys_and_reads_each_answer(self):
        for mode, expected in (("ok", True), ("out", False), ("garbage", False)):
            with self.subTest(mode), mock.patch.dict(os.environ, {"FAKE_CLAUDE_MODE": mode}):
                ok, reason, detail = guard.real_subscription()
                self.assertEqual(ok, expected)
                self.assertTrue(ok or "sign-in-needed" == reason)
        self.assertTrue(all(call["env"] == [] for call in self.calls()), "a paid key reached claude auth status")

    def test_a_sign_in_check_that_hangs_is_a_wait_not_a_launch(self):
        started = time.time()
        with mock.patch.dict(os.environ, {"FAKE_CLAUDE_MODE": "sleep"}), mock.patch.object(guard, "AUTH_TIMEOUT", 1):
            ok, reason, detail = guard.real_subscription()
        self.assertEqual((ok, reason), (False, "sign-in-needed"))
        self.assertIn("couldn't be checked", detail)
        # The fake is a shim whose child sleeps 30 seconds holding the pipes: the check must end at its own limit, with
        # that child stopped, not when the child finishes (it took the full 30 seconds until 2026-11-10).
        self.assertLess(time.time() - started, 15, "the hung check outlived its timeout")

    def test_the_launch_runs_the_resume_command_without_paid_keys(self):
        state_path = Path(self.temp.name) / "state.json"
        with mock.patch.object(guard, "STATE", state_path):
            child = guard.real_spawn(active(session_id="sess-1", permission_mode="bypassPermissions"))
        # Its whole tree stops before the folder goes: on Windows the cmd wrapper can still hold resumed-session.log
        # open after the fake has written its call, and the folder then can't be removed (seen on CI, 2026-11-08).
        self.addCleanup(self.stop_tree, child["pid"])
        deadline = time.time() + 30
        while not self.calls() and time.time() < deadline:
            time.sleep(0.1)
        (call,) = self.calls()
        self.assertEqual(call["argv"], ["-p", "process plan", "--resume", "sess-1", "--permission-mode", "bypassPermissions"])
        self.assertEqual(call["env"], [], "a paid key reached the resumed session")
        self.assertTrue(child["pid"])
        log = (Path(self.temp.name) / "run-guard.log").read_text(encoding="utf-8")
        self.assertIn("removed ANTHROPIC_API_KEY", log)
        self.assertNotIn("a-secret-value", log)


class SavingAWait(unittest.TestCase):
    """A wait the disk won't take at once is kept, and an unwritable state never launches anything."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "state.json"
        guard.save(active(), self.path)
        self.real_save = guard.save
        self.payload = json.dumps({"hook_event_name": "StopFailure", "session_id": "me", "error": "rate_limit",
                                   "error_details": f"usage limit|{int(time.time()) + 900}"})

    def failing(self, times):
        left = [times]

        def save(state, path=guard.STATE):
            if Path(path) == self.path and left[0] > 0:
                left[0] -= 1
                raise PermissionError("locked by another process")
            return self.real_save(state, path)
        return save

    def test_a_moment_s_lock_is_retried_and_the_wait_saved(self):
        with mock.patch.object(guard, "save", self.failing(2)), mock.patch.object(guard.time, "sleep"):
            guard.hook(self.payload, World().env(), self.path)
        self.assertEqual(guard.load(self.path)["wait_reason"], "usage-limit")

    def test_a_wait_the_state_wont_take_is_kept_and_merged_by_the_next_tick(self):
        with mock.patch.object(guard, "save", self.failing(99)), mock.patch.object(guard.time, "sleep"):
            with self.assertRaises(OSError):
                guard.hook(self.payload, World().env(), self.path)
        self.assertNotIn("wait_until", guard.load(self.path))
        self.assertTrue((Path(self.temp.name) / guard.PENDING).exists())
        world = World()
        self.assertTrue(guard.watch_tick(world.env(), self.path).startswith("waiting: usage-limit"))
        self.assertEqual(world.spawned, [])
        self.assertEqual(guard.load(self.path)["wait_reason"], "usage-limit")
        self.assertFalse((Path(self.temp.name) / guard.PENDING).exists())

    def test_an_unwritable_state_launches_nothing(self):
        world = World()
        with mock.patch.object(guard, "save", self.failing(99)), mock.patch.object(guard.time, "sleep"):
            with self.assertRaises(OSError):
                guard.watch_tick(world.env(), self.path)
        self.assertEqual(world.spawned, [])


class EndStates(unittest.TestCase):
    """No runnable row is not one state: complete, parked, waiting and a finishing step each lead somewhere else."""

    def env(self, world, finish, wakes=(), writes=None):
        written = writes if writes is not None else []

        def write(kind, text):
            if isinstance(written, Exception):
                raise written
            written.append((kind, text))
            return f"docs/acceptance/2026-11-08{'-2' if len(written) > 1 else ''}.md"
        found = finish if callable(finish) else (lambda: dict(finish))
        return guard.replace(world.env(), finish=found, wakes=lambda: list(wakes), write_acceptance=write)

    def test_complete_and_parked_write_the_acceptance_file_and_end(self):
        for kind in ("complete", "parked"):
            with self.subTest(kind):
                world, state, writes = World(rows=()), active(), []
                env = self.env(world, {"state": kind, "detail": "CI proven", "report": "# Acceptance"}, writes=writes)
                self.assertEqual(guard.watch(state, env, now=1000), kind)
                self.assertEqual((state["active"], state["status"]), (False, kind))
                self.assertEqual(writes, [(kind, "# Acceptance")])
                self.assertIn("docs/acceptance/", state["why"])

    def test_parked_is_never_reported_complete(self):
        world, state = World(rows=()), active()
        guard.watch(state, self.env(world, {"state": "parked", "report": "x"}), now=1000)
        self.assertNotEqual(state["status"], "complete")

    def test_a_failed_acceptance_write_keeps_the_run_and_tries_again(self):
        world, state = World(rows=()), active()
        env = self.env(world, {"state": "complete", "report": "x"}, writes=OSError("disk full"))
        self.assertTrue(guard.watch(state, env, now=1000).startswith("waiting: acceptance-unwritable"))
        self.assertTrue(state["active"])
        writes = []
        self.assertEqual(guard.watch(state, self.env(world, {"state": "complete", "report": "x"}, writes=writes), now=1000 + 601), "complete")
        self.assertEqual(len(writes), 1)

    def test_waiting_rows_keep_the_run_past_the_quiet_limit_and_resume_when_one_clears(self):
        world, state = World(rows=()), active(changed_at=0, restarts=2, restart_signature="one")
        env = self.env(world, {"state": "waiting", "detail": "waiting on D00 T02 §3 (waiting-person)"})
        now = 0.0
        while now < (guard.MAX_QUIET_HOURS + 10) * 3600:
            now += 300
            self.assertTrue(guard.watch(state, env, now=now).startswith("waiting: rows-waiting"))
        self.assertTrue(state["active"])
        self.assertEqual(world.spawned, [])
        world.rows = ("D00 T02 §3",)  # the person answered: the row is ready
        now += 1801
        self.assertIn("attempt 1", guard.watch(state, env, now=now))

    def test_a_passing_wake_resumes_a_waiting_run_before_its_deadline(self):
        world, state = World(rows=()), active()
        guard.watch(state, self.env(world, {"state": "waiting"}), now=1000)
        self.assertEqual(state["wait_reason"], "rows-waiting")
        # The Need is still on the section, so nothing is runnable yet: the wake itself is the reason to resume.
        env = self.env(world, {"state": "waiting"}, wakes=[{"section": "D00 T02 §3", "need": "third-party"}])
        self.assertIn("attempt 1", guard.watch(state, env, now=1060))
        self.assertEqual(len(world.spawned), 1)
        self.assertNotIn("woken", state)

    def test_each_ci_answer_has_its_next_step(self):
        for kind, expect in (("ci-pending", 600), ("ci-unavailable", 1800)):
            with self.subTest(kind):
                world, state = World(rows=()), active()
                self.assertTrue(guard.watch(state, self.env(world, {"state": kind}), now=1000).startswith(f"waiting: {kind}"))
                self.assertEqual(state["wait_until"], 1000 + expect)
        for kind, words in (("needs-push", "push the finished work"), ("needs-ci-repair", "red-first policy")):
            with self.subTest(kind):
                world, state = World(rows=()), active()
                reason = guard.on_stop(state, {"session_id": "me"}, self.env(world, {"state": kind}))
                self.assertIn(words, reason)
                self.assertIn("attempt 1", guard.watch(state, self.env(world, {"state": kind}), now=1000))

    def test_recovery_to_green_ends_complete(self):
        world, state = World(rows=()), active()
        answers = iter([{"state": "needs-ci-repair"}, {"state": "ci-pending"}, {"state": "complete", "report": "x"}])
        env = self.env(world, lambda: next(answers))
        self.assertIn("red-first policy", guard.on_stop(state, {"session_id": "me"}, env))
        self.assertTrue(guard.watch(state, env, now=1000).startswith("waiting: ci-pending"))
        self.assertEqual(guard.watch(state, env, now=1000 + 601), "complete")

    def test_a_finish_check_that_fails_once_waits_and_looks_again(self):
        def boom():
            raise subprocess.TimeoutExpired("git fetch", 20)
        for finish in ({"state": "unknown"}, boom):
            with self.subTest(finish=finish):
                world, state = World(rows=()), active()
                self.assertTrue(guard.watch(state, self.env(world, finish), now=1000).startswith("waiting: finish-unreadable"))
                self.assertTrue(state["active"])
        world, state = World(rows=()), active()
        self.assertIsNone(guard.on_stop(state, {"session_id": "me"}, self.env(world, boom)))
        self.assertTrue(state["active"])

    def test_an_explicit_drained_still_ends_as_before(self):
        world, state = World(rows=()), active()
        self.assertEqual(guard.watch(state, self.env(world, {"state": "drained"}), now=1000), "drained")


class SlowReads(unittest.TestCase):
    def test_the_wake_check_gets_a_minute_a_wake(self):
        seen = {}

        def run(*args, cwd=None, timeout=20):
            seen["timeout"] = timeout
            return subprocess.CompletedProcess(args, 0, b"[]", b"")
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "todo").mkdir()
            (Path(folder) / "todo" / "wakes.json").write_text(json.dumps([{}, {}]), encoding="utf-8")
            with mock.patch.object(guard, "run", side_effect=run):
                guard.real_wakes(Path(folder))
        self.assertEqual(seen["timeout"], 2 * guard.WAKE_SECONDS + 30)

    def test_the_finish_line_and_wakes_are_read_before_the_lock_and_only_with_nothing_to_run(self):
        calls = []
        world = World(rows=())
        env = guard.replace(world.env(), finish=lambda: calls.append("finish") or {"state": "waiting"},
                            wakes=lambda: calls.append("wakes") or [])
        snap = guard.snapshot(env)
        self.assertEqual(calls, ["finish", "wakes"])
        snap.finish()
        snap.wakes()
        self.assertEqual(calls, ["finish", "wakes"], "the lock-time reads repeated the slow ones")
        calls.clear()
        guard.snapshot(guard.replace(env, rows=lambda: ["D00 T01 §1"]))
        self.assertEqual(calls, [], "with work to do, the finish line isn't asked")

    def test_a_waiting_run_asks_nothing_slow_until_its_wait_passes(self):
        calls = []
        env = guard.replace(World(rows=()).env(), finish=lambda: calls.append("finish") or {"state": "ci-unavailable"},
                            wakes=lambda: calls.append("wakes") or [])
        waiting_on_ci = active(wait_until=2000.0, wait_reason="ci-unavailable")
        guard.snapshot(env, state=waiting_on_ci, now=1000)
        self.assertEqual(calls, [], "a wait's backoff was skipped")
        waiting_on_rows = active(wait_until=2000.0, wait_reason="rows-waiting")
        guard.snapshot(env, state=waiting_on_rows, now=1000)
        self.assertEqual(calls, ["wakes"], "a run waiting on its rows checks only its wakes")
        calls.clear()
        guard.snapshot(env, state=waiting_on_ci, now=2001)
        self.assertEqual(calls, ["finish", "wakes"], "once the wait passed, the finish line is asked again")

    def test_a_slow_fetch_or_ci_is_unavailable_not_an_end(self):
        def run(*args, cwd=None, timeout=20):
            if "query" in args:
                return subprocess.CompletedProcess(args, 0, json.dumps({"complete": True, "roots": {}}).encode(), b"")
            raise subprocess.TimeoutExpired(args, timeout)
        with mock.patch.object(guard, "run", side_effect=run):
            self.assertEqual(guard.real_finish()["state"], "ci-unavailable")


class Acceptance(unittest.TestCase):
    def test_the_file_is_written_atomically_read_back_and_never_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            first = guard.write_acceptance("complete", "# one\n", root, "2026-11-08")
            second = guard.write_acceptance("parked", "# two\n", root, "2026-11-08")
            self.assertEqual((first, second), ("docs/acceptance/2026-11-08.md", "docs/acceptance/2026-11-08-2.md"))
            self.assertEqual((root / first).read_text(encoding="utf-8"), "# one\n")
            self.assertEqual(list((root / "docs" / "acceptance").glob("*.tmp")), [])

    def test_a_write_the_disk_refuses_raises_for_the_guard_to_retry(self):
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "docs").mkdir()
            (Path(folder) / "docs" / "acceptance").write_text("a file where the folder would go", encoding="utf-8")
            with self.assertRaises(OSError):
                guard.write_acceptance("complete", "# x\n", Path(folder), "2026-11-08")

    def test_the_report_names_parked_blocked_acceptance_rows_and_files(self):
        found = {"roots": {"D01 T01 §2": "parked"}, "blocked": {"D01 T01 §3": ["D01 T01 §2"], "D01 T01 §2": ["D01 T01 §2"]},
                 "acceptance": [{"ref": "D99 T01 §1", "title": "Acceptance", "needs": ["attended"]}],
                 "files_waiting_on_acceptance": ["todo/99-x/TODO-01.md"]}
        text = guard.acceptance_report("parked", found)
        for words in ("implementation parked", "- D01 T01 §2", "D01 T01 §3: held by D01 T01 §2", "D99 T01 §1", "needs attended",
                      "todo/99-x/TODO-01.md"):
            self.assertIn(words, text)


class RealFinish(unittest.TestCase):
    """real_finish maps the graph, git and CI to the next step."""

    def run_with(self, finish, head="a" * 40, remote="a" * 40, ci=0, fetch=0):
        def run(*args, cwd=None, timeout=20):
            if "query" in args:
                return subprocess.CompletedProcess(args, 0, json.dumps(finish).encode(), b"")
            if args[:2] == ("git", "fetch"):
                return subprocess.CompletedProcess(args, fetch, b"", b"")
            if args[:3] == ("git", "rev-parse", "HEAD"):
                return subprocess.CompletedProcess(args, 0, head.encode(), b"")
            if args[:2] == ("git", "rev-parse"):
                return subprocess.CompletedProcess(args, 0, remote.encode(), b"")
            return subprocess.CompletedProcess(args, ci, b"ci said", b"")
        with mock.patch.object(guard, "run", side_effect=run):
            return guard.real_finish()["state"]

    def test_the_mapping(self):
        done = {"complete": True, "roots": {}, "blocked": {}, "acceptance": [], "files_waiting_on_acceptance": []}
        self.assertEqual(self.run_with(done), "complete")
        self.assertEqual(self.run_with(done, remote="b" * 40), "needs-push")
        self.assertEqual(self.run_with(done, fetch=1), "ci-unavailable")
        for code, kind in ((1, "needs-ci-repair"), (4, "ci-pending"), (5, "ci-pending"), (3, "ci-unavailable"), (2, "ci-unavailable")):
            with self.subTest(code=code):
                self.assertEqual(self.run_with(done, ci=code), kind)
        parked = {**done, "complete": False, "roots": {"D01 T01 §2": "parked"}}
        self.assertEqual(self.run_with(parked), "parked")
        mixed = {**done, "complete": False, "roots": {"D01 T01 §2": "parked", "D00 T02 §3": "waiting-person"}}
        self.assertEqual(self.run_with(mixed), "waiting")


class OwnedSessions(unittest.TestCase):
    """Only the run's own session, or one actually working here, holds the tree."""

    def test_an_idle_unrelated_session_does_not_block_watch(self):
        world, state = World(live=[{"id": "someone", "status": "idle"}]), active()
        self.assertIn("attempt 1", guard.watch(state, world.env(), now=1000))

    def test_a_busy_or_unknown_session_and_the_run_s_own_still_block(self):
        for live in ([{"id": "someone", "status": "busy"}], [{"id": "someone", "status": None}], ["someone"],
                     [{"id": "me", "status": "idle"}]):
            with self.subTest(live=live):
                world, state = World(live=live), active()
                self.assertTrue(guard.watch(state, world.env(), now=1000).startswith("session live here"))
                self.assertEqual(world.spawned, [])

    def test_a_second_writer_is_still_refused_while_the_owner_lives(self):
        state = active()
        with self.assertRaises(ValueError):
            guard.start(state, "other", None, World(alive=["me"]).env())

    def test_the_real_reader_keeps_each_session_s_status(self):
        here = str(guard.ROOT)
        records = [{"sessionId": "a", "cwd": here, "status": "idle"}, {"sessionId": "b", "cwd": here, "status": "busy"},
                   {"sessionId": "c", "cwd": "C:/elsewhere", "status": "busy"}]
        with mock.patch.object(guard, "session_records", return_value=records):
            self.assertEqual(guard.real_live_here(), [{"id": "a", "status": "idle"}, {"id": "b", "status": "busy"}])


class HungChild(unittest.TestCase):
    def env(self, world, procs, survivors=(), alive_again=True):
        checks = iter([True, alive_again])
        world_env = world.env()
        self.stopped = []
        return guard.replace(world_env, child_alive=lambda c: next(checks, alive_again), tree=lambda c: list(procs),
                             stop_tree=lambda c, p: self.stopped.append(c["pid"]) or list(survivors))

    def hung(self):
        return active(child={"pid": 7, "started": 2}, restart_signature="one", changed_at=0.0), guard.CHILD_HANG_HOURS * 3600

    def test_one_in_the_build_queue_is_left_running(self):
        world = World()
        state, now = self.hung()
        procs = [{"pid": 7, "started": 2, "name": "claude.exe"}, {"pid": 9, "started": 3, "name": "bosun.exe"}]
        self.assertIn("build queue", guard.watch(state, self.env(world, procs), now=now))
        self.assertEqual((self.stopped, world.spawned), ([], []))
        self.assertTrue(state["active"])

    def test_a_refused_or_partial_stop_launches_nothing_and_stalls(self):
        for left in ([7], [11]):
            with self.subTest(survivors=left):
                world = World()
                state, now = self.hung()
                procs = [{"pid": 7, "started": 2, "name": "claude.exe"}, {"pid": 11, "started": 4, "name": "node.exe"}]
                self.assertEqual(guard.watch(state, self.env(world, procs, survivors=left), now=now), "stalled")
                self.assertEqual(world.spawned, [])
                self.assertIn(str(left[0]), state["why"])

    def test_a_reused_pid_is_never_stopped(self):
        world = World()
        state, now = self.hung()
        result = guard.watch(state, self.env(world, [{"pid": 7, "started": 2, "name": "x"}], alive_again=False), now=now)
        self.assertEqual(self.stopped, [])
        self.assertIn("attempt 1", result)

    def test_the_tree_skips_a_stranger_whose_parent_pid_was_reused(self):
        table = [{"pid": 7, "ppid": 1, "name": "claude.exe", "command": ""},
                 {"pid": 8, "ppid": 7, "name": "node.exe", "command": ""},
                 {"pid": 9, "ppid": 7, "name": "stranger.exe", "command": ""},
                 {"pid": 10, "ppid": 8, "name": "bosun.exe", "command": ""}]
        starts = {8: 200, 9: 50, 10: 300}  # 9 started before 7 did: its parent was an older process with pid 7
        tree = guard.descendants(7, 100, table, starts.get)
        self.assertEqual([p["pid"] for p in tree], [7, 8, 10])
        self.assertTrue(guard.in_build_queue(tree[-1]))
        self.assertTrue(guard.in_build_queue({"name": "python3", "command": "/usr/bin/bosun run -- make"}))
        self.assertFalse(guard.in_build_queue({"name": "python3", "command": "python3 bosun_notes.py"}))

    def test_the_stop_runs_before_the_lock_so_a_stop_prompt_gets_in(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.json"
            guard.save(active(child={"pid": 7, "started": 2}, restart_signature="one", changed_at=0.0), path)
            world, heard = World(child=True), []

            def stop_tree(child, procs):
                # The operator's `stop`, through the real hook, arriving while the tree is being stopped: it must get
                # the lock at once, and the tick must then honour it.
                started = time.monotonic()
                heard.append(guard.hook(json.dumps({"hook_event_name": "UserPromptSubmit", "session_id": "me", "prompt": "stop"}),
                                        world.env(), path))
                heard.append(time.monotonic() - started)
                return []
            env = guard.replace(world.env(), tree=lambda c: [{"pid": 7, "started": 2, "name": "claude.exe"}], stop_tree=stop_tree)
            self.assertEqual(guard.watch_tick(env, path, now=guard.CHILD_HANG_HOURS * 3600), "inactive")
            self.assertIn("stopped holding this run", heard[0])
            self.assertLess(heard[1], guard.LOCK_WAIT)
            saved = guard.load(path)
            self.assertEqual((saved["active"], saved["status"]), (False, "stopped"))
            self.assertEqual(world.spawned, [], "the stopped run launched a replacement")

    def test_linux_start_times_tell_a_reused_pid_and_a_kill_skips_it(self):
        with tempfile.TemporaryDirectory() as folder:
            proc = Path(folder)
            (proc / "42").mkdir()
            rest = " ".join(["S"] + [str(n) for n in range(4, 22)] + ["777"] + ["0"] * 30)
            (proc / "42" / "stat").write_text(f"42 (odd ) name) {rest}", encoding="utf-8")
            self.assertEqual(guard.linux_started(42, proc), 777)
            self.assertIsNone(guard.linux_started(43, proc))
            (proc / "42" / "stat").write_text(f"42 (z) Z {rest[2:]}", encoding="utf-8")
            self.assertIsNone(guard.linux_started(42, proc))
        killed = []
        starts = {1: 10, 2: 20, 3: 99}  # 3 was listed with 30: its pid now belongs to a stranger
        procs = [{"pid": 1, "started": 10}, {"pid": 2, "started": 20}, {"pid": 3, "started": 30}]
        self.assertEqual(guard.kill_each(procs, starts.get, killed.append), [2, 1])
        self.assertEqual(killed, [2, 1])

    def test_a_real_tree_is_stopped_whole(self):
        script = "import subprocess, sys, time; subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)']); time.sleep(120)"
        parent = subprocess.Popen([sys.executable, "-c", script])
        try:
            child = {"pid": parent.pid, "started": guard.process_started(parent.pid)}
            deadline = time.time() + 20
            tree = guard.real_tree(child)
            while len(tree) < 2 and time.time() < deadline:
                time.sleep(0.2)
                tree = guard.real_tree(child)
            self.assertGreaterEqual(len(tree), 2, "the grandchild never appeared")
            self.assertEqual(guard.real_stop_tree(child, tree), [])
            self.assertEqual(guard.survivors(tree), [])
        finally:
            for proc in guard.real_tree({"pid": parent.pid, "started": guard.process_started(parent.pid)}) if parent.poll() is None else []:
                try:
                    os.kill(proc["pid"], 9)
                except OSError:
                    pass
            parent.kill()
            parent.wait(10)


class LostTranscript(unittest.TestCase):
    """`--resume` fails because the transcript is gone: one fresh start, owned by the new session's own prompt."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name) / "state.json"
        self.commands = []

    def env(self, output):
        def spawn(state):
            self.commands.append(guard.resume_command(state, "claude"))
            return {"pid": 100 + len(self.commands), "started": 1, "log_offset": 0}
        return guard.replace(World(rows=("D01 T02 §4",)).env(), spawn=spawn, child_output=lambda c: output(c),
                             alive=lambda s: False, live_here=lambda: [])

    def save(self, state):
        guard.save(state, self.path)

    def test_one_fresh_start_the_new_owner_and_a_second_loss_ends_it(self):
        gone = lambda c: "Error: No conversation found with session ID: me\n" if c.get("pid") in (100, 101, 103) else ""
        env = self.env(gone)
        self.save(active(permission_mode="acceptEdits", child={"pid": 100, "started": 1, "log_offset": 0}))
        self.assertEqual(guard.watch_tick(env, self.path, now=1000), "resumed, attempt 1")
        self.assertNotIn("--resume", self.commands[-1])
        self.assertEqual(self.commands[-1][-2:], ["--permission-mode", "acceptEdits"])
        logged = (self.path.with_name("run-guard.log")).read_text(encoding="utf-8")
        self.assertIn("transcript is gone", logged)
        self.assertIn("started a fresh", logged)
        # The fresh session's own `process plan` makes it the owner, across ticks whose state is saved and read back.
        guard.hook(json.dumps({"hook_event_name": "UserPromptSubmit", "session_id": "fresh", "prompt": "process plan",
                               "permission_mode": "acceptEdits"}), env, self.path)
        self.assertEqual(guard.load(self.path)["session_id"], "fresh")
        state = guard.load(self.path)
        state["child"]["fresh"] = True
        state["child"]["pid"] = 102  # the fresh child, which ended in the ordinary way
        self.save(state)
        self.assertEqual(guard.watch_tick(env, self.path, now=2000), "resumed, attempt 2")
        self.assertEqual(self.commands[-1][2:5], ["process plan", "--resume", "fresh"])
        # Its resume finds no transcript either: the run already started afresh once, so it ends.
        state = guard.load(self.path)
        state["child"]["pid"] = 103
        self.save(state)
        self.assertEqual(guard.watch_tick(env, self.path, now=3000), "stalled")
        self.assertEqual(len(self.commands), 2)

    def test_a_fresh_child_reporting_the_same_loss_ends_the_run(self):
        env = self.env(lambda c: "No conversation found with session ID: me")
        self.save(active(fresh_starts=1, child={"pid": 100, "started": 1, "log_offset": 0, "fresh": True}))
        self.assertEqual(guard.watch_tick(env, self.path, now=1000), "stalled")
        self.assertEqual(self.commands, [])

    def test_a_save_that_fails_after_the_launch_never_allows_a_second_fresh_start(self):
        env = self.env(lambda c: "No conversation found with session ID: me" if c.get("pid") == 100 else "")
        self.save(active(child={"pid": 100, "started": 1, "log_offset": 0}))
        real = guard.save

        def save(state, path=guard.STATE):
            if self.commands and state.get("child", {}).get("pid") != 100:
                raise OSError("disk full")  # the save after the launch
            return real(state, path)
        with mock.patch.object(guard, "save", side_effect=save):
            with self.assertRaises(OSError):
                guard.watch_tick(env, self.path, now=1000)
        self.assertEqual(len(self.commands), 1)
        self.assertEqual(guard.load(self.path)["fresh_starts"], 1, "the launch's spend wasn't saved before it")
        self.assertEqual(guard.watch_tick(env, self.path, now=2000), "stalled")
        self.assertEqual(len(self.commands), 1, "a second fresh start went through")

    def test_a_launch_that_fails_spends_nothing(self):
        def spawn(state):
            raise OSError("claude is not on PATH")
        env = guard.replace(self.env(lambda c: "No conversation found with session ID: me"), spawn=spawn)
        self.save(active(child={"pid": 100, "started": 1, "log_offset": 0}))
        with self.assertRaises(OSError):
            guard.watch_tick(env, self.path, now=1000)
        self.assertEqual(guard.load(self.path).get("fresh_starts", 0), 0)

    def test_a_real_fake_cli_s_error_is_read_from_its_own_output(self):
        fake = Path(self.folder.name) / ("claude.cmd" if os.name == "nt" else "claude")
        fake.write_text("@echo No conversation found with session ID: me 1>&2\r\n@exit /b 1\r\n" if os.name == "nt"
                        else "#!/bin/sh\necho 'No conversation found with session ID: me' >&2\nexit 1\n", encoding="utf-8")
        fake.chmod(0o755)
        log = Path(self.folder.name) / "resumed-session.log"
        log.write_text("an older child's line: No conversation found with session ID: old\n", encoding="utf-8")
        with mock.patch.object(guard, "STATE", self.path), mock.patch.object(guard.shutil, "which", return_value=str(fake)):
            child = guard.real_spawn(active())
            deadline = time.time() + 30
            while guard.real_child_alive(child) and time.time() < deadline:
                time.sleep(0.2)
            text = guard.real_child_output(child)
        self.assertIn("session ID: me", text)
        self.assertNotIn("old", text)


class ReviewOutlivesItsSession(unittest.TestCase):
    def test_a_dead_child_waits_for_its_review_then_resumes_the_same_session(self):
        world, state = World(), active(child={"pid": 7, "started": 2})
        reviewing = ["astra round 3"]
        commands = []
        env = guard.replace(world.env(), reviewing=lambda: reviewing[0] if reviewing else None,
                            spawn=lambda s: commands.append(guard.resume_command(s, "claude")) or {"pid": 8, "started": 3})
        self.assertTrue(guard.watch(state, env, now=1000).startswith("waiting: a review is in flight"))
        self.assertEqual(commands, [])
        reviewing.clear()
        self.assertEqual(guard.watch(state, env, now=1600), "resumed, attempt 1")
        self.assertEqual(commands, [["claude", "-p", "process plan", "--resume", "me"]])


def xml(command="C:/py/pythonw.exe", arguments='"C:/b/other.py" tick', logon="InteractiveToken", user="me", enabled="true"):
    return (f"<Task><Principals><Principal><UserId>{user}</UserId><LogonType>{logon}</LogonType></Principal></Principals>"
            f"<Settings><Enabled>{enabled}</Enabled></Settings><Actions><Exec><Command>{command}</Command>"
            f"<Arguments>{arguments}</Arguments></Exec></Actions></Task>")


class Verify(unittest.TestCase):
    NOW = 1_800_000_000.0

    def setUp(self):
        self.root = Path("C:/repos/here")
        self.own = xml(arguments=f'"{self.root / "scripts" / "run-guard.py"}" watch')
        self.files = {"C:/py/pythonw.exe", "C:/b/other.py", str(self.root / "scripts" / "run-guard.py")}

    def at(self, ago):
        return guard.stamp(self.NOW - ago)

    def run_verify(self, task=None, tick=None, code=0, ids=("me",)):
        data = {str(Path("C:/s/last-tick.json")): tick if tick is not None else {"at": self.at(60), "ok": True, "result": "inactive"}}
        files = {os.path.normcase(os.path.normpath(f)) for f in self.files}
        return guard.verify(self.root, Path("C:/s/state.json"), now=self.NOW,
                            task_xml=lambda name: (code, task if task is not None else self.own),
                            read_json=lambda p: data.get(str(p)), is_file=lambda p: os.path.normcase(os.path.normpath(p)) in files,
                            ids=set(ids))

    def unhealthy(self, words, **kwargs):
        found = self.run_verify(**kwargs)
        self.assertFalse(found["healthy"], found)
        self.assertTrue(any(words in p for p in found["problems"]), found["problems"])

    def test_the_repo_s_own_task_reads_healthy(self):
        self.assertEqual(self.run_verify(), {"healthy": True, "watchdog": guard.TASK_NAME, "task": "ok", "problems": []})

    def test_the_state_of_the_task_itself_is_one_of_four(self):
        # What a caller acts on, such as scripts/setup-clone.ps1, which installs only over `missing`.
        own_script = f'"{self.root / "scripts" / "run-guard.py"}" watch'
        for expected, kwargs in (
                ("ok", {}),
                ("ok", {"tick": {}}),  # the task is right; that it has not ticked yet is a separate problem
                ("missing", {"code": 1, "task": "ERROR: The system cannot find the file specified."}),
                ("unreadable", {"code": 1, "task": "ERROR: Access is denied."}),
                ("unreadable", {"code": None, "task": "schtasks could not run"}),
                ("mismatched", {"task": xml(arguments='"C:/b/other.py" watch')}),  # another checkout's task of this name
                ("mismatched", {"task": self.own.replace("<UserId>me<", "<UserId>someone<")}),
                ("mismatched", {"task": self.own.replace("<Enabled>true<", "<Enabled>false<")}),
                ("mismatched", {"task": xml(command="C:/gone/pythonw.exe", arguments=own_script)})):
            with self.subTest(expected=expected, kwargs=kwargs):
                self.assertEqual(self.run_verify(**kwargs)["task"], expected)

    def test_each_broken_part_reads_unhealthy_with_its_reason(self):
        self.unhealthy("program is missing", task=xml(command="C:/gone/pythonw.exe", arguments=f'"{self.root / "scripts" / "run-guard.py"}" watch'))
        self.unhealthy("runs another script", task=xml(arguments='"C:/b/other.py" watch'))
        self.unhealthy("doesn't run `watch`", task=xml(arguments=f'"{self.root / "scripts" / "run-guard.py"}" status'))
        self.unhealthy("another user", task=self.own.replace("<UserId>me<", "<UserId>someone<"))
        self.unhealthy("this user's session", task=self.own.replace("InteractiveToken", "Password"))
        self.unhealthy("disabled", task=self.own.replace("<Enabled>true<", "<Enabled>false<"))
        self.unhealthy("hasn't ticked yet", tick={})
        self.unhealthy("hasn't ticked in 20 minutes", tick={"at": self.at(21 * 60), "ok": True})
        self.unhealthy("last tick failed", tick={"at": self.at(60), "ok": False, "result": "boom"})
        self.unhealthy("can't be read", code=1, task="ERROR: Access is denied.")
        self.unhealthy("is missing", code=1, task="ERROR: The system cannot find the file specified.")

    def test_an_escaped_path_in_the_xml_is_decoded(self):
        self.root = Path("C:/R&D/here")
        self.files = {"C:/py/pythonw.exe", str(self.root / "scripts" / "run-guard.py")}
        escaped = str(self.root / "scripts" / "run-guard.py").replace("&", "&amp;")
        self.assertTrue(self.run_verify(task=xml(arguments=f'"{escaped}" watch'))["healthy"])

    def test_only_the_task_named_for_this_checkout_is_asked_for(self):
        asked = []

        def task_xml(name):
            asked.append(name)
            return 1, "ERROR: The system cannot find the file specified."
        found = guard.verify(self.root, Path("C:/s/state.json"), now=self.NOW, task_xml=task_xml, read_json=lambda p: None,
                             is_file=lambda p: True, ids={"me"})
        self.assertEqual(asked, [guard.TASK_NAME])
        self.assertIn(f"the task {guard.TASK_NAME} is missing", found["problems"])


class StatusCommand(unittest.TestCase):
    """`run-guard.py status` against a corrupt state and a failing hook, through the real CLI."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.state = Path(self.folder.name) / "state.json"
        self.env = {**os.environ, "RUN_GUARD_STATE": str(self.state), "PYTHONIOENCODING": "utf-8"}

    def cli(self, *args, stdin=""):
        return subprocess.run([sys.executable, str(guard.ROOT / "scripts" / "run-guard.py"), *args], input=stdin,
                              capture_output=True, text=True, encoding="utf-8", env=self.env, timeout=120)

    def test_a_corrupt_state_shows_in_status_and_never_blocks_the_session(self):
        self.state.write_text("{not json", encoding="utf-8")
        event = json.dumps({"hook_event_name": "Stop", "session_id": "me"})
        hooked = self.cli("hook", stdin=event)
        self.assertEqual(hooked.returncode, 0)
        self.assertIn("Run guard failed", hooked.stdout)
        shown = self.cli("status", "--json")
        self.assertEqual(shown.returncode, 0, shown.stderr)
        data = json.loads(shown.stdout)
        self.assertEqual(data["state"], "unreadable")
        self.assertIn("JSONDecodeError", data["state_error"])
        self.assertEqual(len(data["hook_failures"]), 1)
        self.assertIn("a hook failed open", self.cli("status").stdout)
        self.state.write_text("{}", encoding="utf-8")
        self.assertEqual(self.cli("hook", stdin=event).returncode, 0)
        self.assertEqual(json.loads(self.cli("status", "--json").stdout)["hook_failures"], [])

    def test_an_unwritable_health_record_still_lets_the_hook_through(self):
        self.state.write_text("{not json", encoding="utf-8")
        (self.state.with_name(guard.HOOK_FAILURES)).mkdir()  # a folder where the record would go: the write fails
        hooked = self.cli("hook", stdin=json.dumps({"hook_event_name": "Stop", "session_id": "me"}))
        self.assertEqual(hooked.returncode, 0)
        self.assertIn("Run guard failed", hooked.stdout)

    def test_verify_s_exit_code_and_reasons_in_text_and_json(self):
        spec = __import__("importlib.util").util.spec_from_file_location("run_guard_cli", guard.ROOT / "scripts" / "run-guard.py")
        cli = __import__("importlib.util").util.module_from_spec(spec)
        spec.loader.exec_module(cli)
        bad = {"healthy": False, "watchdog": guard.TASK_NAME, "problems": ["it is disabled"]}
        for argv, check in ((["status", "--verify"], lambda out: "it is disabled" in out and "unhealthy" in out),
                            (["status", "--verify", "--json"], lambda out: json.loads(out)["verify"] == bad)):
            with self.subTest(argv=argv), mock.patch.object(sys, "argv", ["run-guard.py", *argv]), \
                    mock.patch.object(cli.guard, "verify", return_value=bad), \
                    mock.patch.object(cli.guard, "load", return_value={}), \
                    mock.patch("sys.stdout", new_callable=__import__("io").StringIO) as out:
                self.assertEqual(cli.main(), 1)
                self.assertTrue(check(out.getvalue()), out.getvalue())


class Waits(unittest.TestCase):
    """A run that waits for an unavailable reviewer neither spins nor stalls, and resumes when the wait passes."""

    def test_nothing_resumes_and_no_restart_counts_while_waiting(self):
        world, state = World(), active(restarts=2)
        guard.set_wait(state, "reviewer-unavailable", 1000 + 1800, "auth: sign in")
        for minute in range(0, 30, 5):
            self.assertTrue(guard.watch(state, world.env(), now=1000 + minute * 60).startswith("waiting: reviewer-unavailable"))
        self.assertEqual((world.spawned, state["restarts"]), ([], 2))

    def test_the_turn_may_end_while_waiting_without_counting_idle(self):
        world, state = World(), active(idle=0)
        guard.set_wait(state, "reviewer-unavailable", time.time() + 600)
        self.assertIsNone(guard.on_stop(state, {"session_id": "me"}, world.env()))
        self.assertEqual(state["idle"], 0)

    def test_an_outage_across_many_waits_and_past_the_quiet_limit_resumes(self):
        world, state = World(), active(changed_at=0)
        now = 0.0
        hours = guard.MAX_QUIET_HOURS + 6
        while now < hours * 3600:
            guard.set_wait(state, "reviewer-unavailable", now + 1800)
            for _ in range(6):  # the heartbeat looks every 5 minutes
                now += 300
                result = guard.watch(state, world.env(), now=now)
                self.assertNotIn(result, ("stalled", "expired"))
            # each resume changes nothing, as a reviewer that's still down leaves the tree as it was
        self.assertTrue(state["active"])
        self.assertTrue(world.spawned, "nothing resumed after a wait")
        self.assertLess(state["restarts"], guard.MAX_RESTARTS)

    def test_a_recovered_reviewer_resumes_the_run(self):
        world, state = World(), active()
        guard.set_wait(state, "reviewer-unavailable", 1000)
        self.assertIn("attempt 1", guard.watch(state, world.env(), now=1001))
        self.assertNotIn("wait_until", state)

    def test_no_wait_without_an_active_run(self):
        with self.assertRaises(ValueError):
            guard.set_wait({}, "reviewer-unavailable", 1000)

    def test_status_shows_the_wait_and_how_to_recover(self):
        with tempfile.TemporaryDirectory() as folder:
            env = {**os.environ, "RUN_GUARD_STATE": str(Path(folder) / "state.json")}
            script = [sys.executable, str(guard.ROOT / "scripts" / "run-guard.py")]
            done = subprocess.run([*script, "wait", "--reason", "reviewer-unavailable", "--minutes", "30"], env=env,
                                  capture_output=True, text=True)
            self.assertEqual(done.returncode, 4, "a wait with no active run was saved")
            (Path(folder) / "state.json").write_text(json.dumps(active()), encoding="utf-8")
            detail = "auth: the reviewer CLI isn't signed in: sign it in (for Codex, `codex login`). See build/review-failures/x.txt."
            done = subprocess.run([*script, "wait", "--reason", "reviewer-unavailable", "--minutes", "30", "--detail", detail],
                                  env=env, capture_output=True, text=True)
            self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
            text = subprocess.run([*script, "status"], env=env, capture_output=True, text=True).stdout
            self.assertEqual(text.splitlines()[0], "waiting")
            self.assertIn("codex login", text)
            self.assertIn("build/review-failures/x.txt", text)
            data = json.loads(subprocess.run([*script, "status", "--json"], env=env, capture_output=True, text=True).stdout)
            self.assertEqual((data["state"], data["wait_reason"]), ("waiting", "reviewer-unavailable"))
            self.assertIn("codex login", data["wait_detail"])
            self.assertRegex(data["wait_until"], r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")

    def test_the_heartbeat_keeps_its_job_while_the_run_waits(self):
        self.assertIn("If it prints waiting, end the turn and keep this job", guard.heartbeat_note())




if __name__ == "__main__":
    unittest.main()
