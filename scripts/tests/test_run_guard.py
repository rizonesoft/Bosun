from __future__ import annotations

import json
import os
import shutil
import subprocess
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
    """Stands in for the machine: rows, the tree and which sessions are running."""

    def __init__(self, rows=("D01 T02 §4",), signature="one", alive=()):
        self.rows, self.sig = rows, signature
        self.alive = set(alive)

    def env(self):
        return guard.Env(
            rows=lambda: None if self.rows is None else list(self.rows),
            signature=lambda: self.sig,
            alive=lambda s: s in self.alive,
        )


def active(**extra):
    return {"active": True, "status": "active", "session_id": "me", **extra}


class WriterRoles(unittest.TestCase):
    def test_codex_cannot_start_a_campaign(self):
        self.assertEqual(guard.RUNNERS, ('claude', 'antigravity'))
        with self.assertRaises(ValueError):
            guard.start({}, 'reviewer', World().env(), runner='codex', owner_pid=42, owner_started=7)


class Prompts(unittest.TestCase):
    def test_process_plan_switches_the_run_on_in_each_spelling(self):
        for prompt in ("process plan", "/process-plan", "Process the plan", "process plan."):
            state = {}
            note = guard.on_prompt(state, {"prompt": prompt, "session_id": "me", "permission_mode": "acceptEdits"}, World().env())
            self.assertTrue(state.get("active"), prompt)
            self.assertEqual(state["session_id"], "me")
            self.assertIn("CronCreate", note)
            self.assertNotIn("install", note)  # no scheduled task: nothing resumes a run

    def test_please_also_starts_it(self):
        state = {}
        guard.on_prompt(state, {"prompt": "please process the plan", "session_id": "me"}, World().env())
        self.assertTrue(state["active"])

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
        state = active(yields=2)
        guard.on_prompt(state, {"prompt": "process plan", "session_id": "new"}, World().env())
        self.assertEqual(state["session_id"], "new")
        self.assertEqual(state["yields"], 2)


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
    def test_the_run_ends_with_its_session_whatever_the_reason(self):
        for reason in sorted(guard.USER_EXITS):
            state = active()
            guard.on_session_end(state, {"session_id": "me", "reason": reason})
            self.assertEqual((state["active"], state["status"]), (False, "stopped"), reason)
        # A closed window or any other end: the run lived in that session, and nothing resumes it.
        for reason in ("other", None):
            state = active()
            guard.on_session_end(state, {"session_id": "me", "reason": reason})
            self.assertEqual((state["active"], state["status"]), (False, "ended"), reason)
            self.assertIn("nothing resumes a run", state["why"])

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
            guard.start(state, "other", World(alive=["me"]).env())
        self.assertEqual(state["session_id"], "me")
        guard.start(state, "other", World().env())
        self.assertEqual(state["session_id"], "other")
        # Nothing the guard keeps could start a session again: no mode, no model, no child.
        self.assertEqual(sorted(state), ["active", "session_id", "started_at", "status"])

    def test_the_heartbeat_is_recorded_and_its_absence_is_said(self):
        state = active()
        self.assertIn("not armed", guard.on_stop(state, {"session_id": "me"}, World().env()))
        guard.on_cron_created(state, {"session_id": "other"})
        self.assertNotIn("heartbeat_at", state)
        guard.on_cron_created(state, {"session_id": "me"})
        self.assertNotIn("not armed", guard.on_stop(state, {"session_id": "me"}, World(signature="two").env()))


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

    def test_the_cron_tool_records_the_heartbeat(self):
        self.path.write_text(json.dumps(active()), encoding="utf-8")
        self.event(hook_event_name="PostToolUse", tool_name="CronCreate", session_id="me")
        self.assertIn("heartbeat_at", guard.load(self.path))

    def test_a_stop_gets_in_while_a_stop_hook_does_its_slow_reads(self):
        self.path.write_text(json.dumps(active()), encoding="utf-8")
        reading, release = threading.Event(), threading.Event()
        env = World().env()

        def slow_rows():
            reading.set()
            release.wait(10)
            return ["D01 T02 §4"]

        env.rows = slow_rows
        results = []
        hook = threading.Thread(target=lambda: results.append(
            guard.hook(json.dumps({"hook_event_name": "Stop", "session_id": "me"}), env, self.path)))
        hook.start()
        self.assertTrue(reading.wait(5))
        self.event(hook_event_name="UserPromptSubmit", prompt="stop", session_id="me")
        release.set()
        hook.join(10)
        self.assertEqual(results, [""], "the turn was sent back after the operator's stop")
        self.assertEqual(guard.load(self.path)["status"], "stopped")


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
        world, state = World(signature=before), active(signature=before, idle=2)
        guard.on_stop(state, {"session_id": "me"}, world.env())
        self.assertEqual(state["idle"], 3, "an attempt record reset idle")


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

    def test_the_turn_may_end_while_the_gate_polls(self):
        world = World()
        state = active(signature=world.sig, idle=1, yields=1)
        self.record(os.getpid(), time.time())
        env = self.env(world)
        for _ in range(guard.MAX_IDLE_BLOCKS * guard.MAX_YIELDS * 2):
            self.assertIsNone(guard.on_stop(state, {"session_id": "me"}, env), "the Stop hook sent the turn to the next row")
        self.assertEqual((state["idle"], state["active"]), (1, True))
        (self.root / "build" / "ci-gate.json").write_text(json.dumps({"proven": "a" * 40}), encoding="utf-8")
        self.assertIn("Unattended run is active", guard.on_stop(state, {"session_id": "me"}, env))

    def test_the_limit_is_the_gates_own(self):
        sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
        import ci_status
        self.assertEqual(guard.CI_WAIT_LIMIT, ci_status.WAIT_LIMIT_SECONDS)


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

    def test_a_5_hour_limit_ends_with_the_session_going_on_not_stalled(self):
        world, state = World(), active(signature="one", idle=3, yields=2, changed_at=0)
        guard.on_stop_failure(state, self.failure("rate_limit", "limit reached, resets in 5 hours"))
        # The session's heartbeat ends a turn every quarter of an hour: none is sent back, and none counts as idle.
        for _ in range(20):
            self.assertIsNone(guard.on_stop(state, {"session_id": "me"}, world.env()))
        self.assertEqual((state["idle"], state["yields"], state["active"]), (3, 2, True))
        state["wait_until"] = time.time() - 1  # the limit reset
        self.assertIn("Unattended run is active", guard.on_stop(state, {"session_id": "me"}, world.env()))
        self.assertNotIn("wait_until", state)
        self.assertEqual((state["idle"], state["yields"]), (1, 0), "the wait didn't give a fresh budget")

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

    def test_the_hook_saves_the_wait_and_the_stop_hook_honours_it_from_a_fresh_read(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "state.json"
            guard.save(active(), path)
            world = World()
            reset = int(time.time()) + 900
            raw = json.dumps(self.failure("rate_limit", f"usage limit reached|{reset}"))
            stop = json.dumps({"hook_event_name": "Stop", "session_id": "me"})
            guard.hook(raw, world.env(), path)
            fresh = guard.load(path)
            self.assertEqual((fresh["wait_reason"], fresh["wait_until"]), ("usage-limit", float(reset)))
            self.assertIn("waiting: usage-limit", (Path(folder) / "run-guard.log").read_text(encoding="utf-8"))
            self.assertEqual(guard.hook(stop, world.env(), path), "", "a turn was sent back during the wait")
            # Another session's failure sets nothing.
            guard.save(active(), path)
            guard.hook(json.dumps(self.failure("rate_limit", "x", session="someone-else")), world.env(), path)
            self.assertNotIn("wait_until", guard.load(path))
            # An operator halt during a wait holds once the wait has passed too.
            guard.hook(raw, world.env(), path)
            with guard.locked(path):
                state = guard.load(path)
                guard.halt(state, "operator", "stop for now")
                state["wait_until"] = time.time() - 1
                guard.save(state, path)
            self.assertEqual(guard.hook(stop, world.env(), path), "")
            self.assertEqual(guard.load(path)["status"], "halted")

    def test_status_shows_a_sign_in_wait_and_what_to_do(self):
        with tempfile.TemporaryDirectory() as folder:
            state = active(started_at=time.time())  # just started, so its session is not yet judged gone
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


class SavingAWait(unittest.TestCase):
    """A wait the disk won't take at once is kept, and taken up by the next hook event."""

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

    def test_a_wait_the_state_wont_take_is_kept_and_merged_by_the_next_hook_event(self):
        with mock.patch.object(guard, "save", self.failing(99)), mock.patch.object(guard.time, "sleep"):
            with self.assertRaises(OSError):
                guard.hook(self.payload, World().env(), self.path)
        self.assertNotIn("wait_until", guard.load(self.path))
        self.assertTrue((Path(self.temp.name) / guard.PENDING).exists())
        # The session's next turn end: the wait is taken up first, so the turn is not sent back into the limit.
        self.assertEqual(guard.hook(json.dumps({"hook_event_name": "Stop", "session_id": "me"}), World().env(), self.path), "")
        self.assertEqual(guard.load(self.path)["wait_reason"], "usage-limit")
        self.assertFalse((Path(self.temp.name) / guard.PENDING).exists())


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

    def test_status_reports_no_watchdog_and_takes_no_verify(self):
        data = json.loads(self.cli("status", "--json").stdout)
        self.assertEqual(sorted(data), ["heartbeat_current", "hook_failures", "state"])
        self.assertEqual(self.cli("status", "--verify").returncode, 2)


class NoAutoResume(unittest.TestCase):
    """The owner's rule of 2026-11-12: a run starts only when the operator starts it, and ends with its session.
    Nothing restarts or resumes one: a session that is gone is recorded as ended, and nothing is launched."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name) / "state.json"

    def test_a_run_whose_session_is_gone_is_recorded_as_ended(self):
        state = active()
        self.assertTrue(guard.close_if_gone(state, World().env()))
        self.assertEqual((state["active"], state["status"]), (False, "ended"))
        self.assertIn("session me is gone", state["why"])
        self.assertIn("nothing resumes a run", state["why"])
        self.assertFalse(guard.close_if_gone(state, World().env()), "an ended run was ended again")

    def test_a_live_session_an_idle_guard_and_a_just_started_run_are_left_alone(self):
        for state, world in ((active(), World(alive=["me"])), ({}, World()), ({"active": False, "session_id": "me"}, World())):
            before = dict(state)
            self.assertFalse(guard.close_if_gone(state, world.env()))
            self.assertEqual(state, before)
        # scripts/start-run.ps1 switches the run on just before its session starts: it has a moment to appear.
        state = {}
        guard.start(state, "me", World().env(), now=1000.0)
        self.assertFalse(guard.close_if_gone(state, World().env(), now=1000.0 + guard.START_GRACE - 1))
        self.assertTrue(state["active"])
        self.assertTrue(guard.close_if_gone(state, World().env(), now=1000.0 + guard.START_GRACE))
        # Once the session has spoken, it is judged like any other.
        state = {}
        guard.start(state, "me", World().env())
        guard.on_prompt(state, {"prompt": "process plan", "session_id": "me"}, World(alive=["me"]).env())
        self.assertTrue(guard.close_if_gone(state, World().env()))

    def test_not_knowing_whether_the_session_runs_ends_nothing(self):
        def unknown(session):
            raise OSError("cannot tell")
        state = active()
        with self.assertRaises(OSError):
            guard.close_if_gone(state, guard.replace(World().env(), alive=unknown))
        self.assertTrue(state["active"])

    def test_settling_saves_the_end_and_logs_it(self):
        guard.save(active(), self.path)
        state = guard.settle_gone(World().env(), self.path)
        self.assertEqual(state["status"], "ended")
        self.assertEqual(guard.load(self.path)["status"], "ended")
        self.assertIn("status: ended session me is gone", self.path.with_name("run-guard.log").read_text(encoding="utf-8"))
        guard.save(active(), self.path)
        self.assertTrue(guard.settle_gone(World(alive=["me"]).env(), self.path)["active"])
        self.assertTrue(guard.load(self.path)["active"])

    def test_the_guard_has_nothing_that_could_start_a_session(self):
        for name in ("watch", "watch_tick", "resume_command", "real_spawn", "verify", "TASK_NAME"):
            self.assertFalse(hasattr(guard, name), f"run_guard.{name} is back")
        self.assertFalse({"spawn", "child_alive", "task_installed"} & set(guard.Env.__dataclass_fields__))

    def test_a_dead_session_is_not_resumed(self):
        """Through the real CLI, with a `claude` on PATH that records every start: the run that the watchdog used to
        resume (an active state whose session has died) is recorded as ended, and nothing starts."""
        fakes = Path(self.folder.name) / "fakes"
        fakes.mkdir()
        started = Path(self.folder.name) / "claude-started.txt"
        if os.name == "nt":
            (fakes / "claude.cmd").write_text(f'@echo %*>>"{started}"\r\n', encoding="utf-8")
        else:
            (fakes / "claude").write_text(f'#!/bin/sh\necho "$@" >> "{started}"\n', encoding="utf-8")
            (fakes / "claude").chmod(0o755)
        guard.save(active(session_id="0d0a51de-dead-4e55-b0de-000000000000", changed_at=time.time() - 3600), self.path)
        env = {**os.environ, "RUN_GUARD_STATE": str(self.path), "PYTHONIOENCODING": "utf-8",
               "PATH": f"{fakes}{os.pathsep}{os.environ['PATH']}"}
        script = [sys.executable, str(guard.ROOT / "scripts" / "run-guard.py")]
        cli = lambda *args: subprocess.run([*script, *args], env=env, capture_output=True, text=True, encoding="utf-8", timeout=120)
        # Read from the usage line, never by running them: what the scheduled task ran, and what scheduled it.
        usage = cli("--help").stdout
        commands = usage[usage.index("{") + 1:usage.index("}")].split(",")
        self.assertEqual(sorted(commands), ["end", "halt", "hook", "start", "status", "uninstall", "wait"])
        shown = cli("status")
        self.assertEqual(shown.returncode, 0, shown.stderr)
        self.assertEqual(shown.stdout.splitlines()[0], "ended")
        self.assertIn("is gone", shown.stdout)
        self.assertEqual(cli("status").stdout.splitlines()[0], "ended")
        saved = guard.load(self.path)
        self.assertEqual((saved["active"], saved["status"]), (False, "ended"))
        self.assertNotIn("child", saved)
        self.assertFalse(started.exists(), "a session was started for a run whose session had died")
        # The dead session's leftover hooks hold nothing either: its Stop is not sent back.
        hooked = subprocess.run([*script, "hook"], env=env, capture_output=True, text=True, encoding="utf-8", timeout=120,
                                input=json.dumps({"hook_event_name": "Stop", "session_id": saved["session_id"]}))
        self.assertEqual((hooked.returncode, hooked.stdout.strip()), (0, ""))
        self.assertFalse(started.exists())


class ProcessStarts(unittest.TestCase):
    def test_linux_start_times_tell_a_reused_pid(self):
        with tempfile.TemporaryDirectory() as folder:
            proc = Path(folder)
            (proc / "42").mkdir()
            rest = " ".join(["S"] + [str(n) for n in range(4, 22)] + ["777"] + ["0"] * 30)
            (proc / "42" / "stat").write_text(f"42 (odd ) name) {rest}", encoding="utf-8")
            self.assertEqual(guard.linux_started(42, proc), 777)
            self.assertIsNone(guard.linux_started(43, proc))
            (proc / "42" / "stat").write_text(f"42 (z) Z {rest[2:]}", encoding="utf-8")
            self.assertIsNone(guard.linux_started(42, proc))

    def test_a_second_writer_is_refused_while_the_owner_lives(self):
        with self.assertRaises(ValueError):
            guard.start(active(), "other", World(alive=["me"]).env())


class EndStates(unittest.TestCase):
    """No runnable row is not one state: complete, parked, waiting and a finishing step each lead somewhere else."""

    STOP = {"session_id": "me"}

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
                self.assertIsNone(guard.on_stop(state, self.STOP, env))
                self.assertEqual((state["active"], state["status"]), (False, kind))
                self.assertEqual(writes, [(kind, "# Acceptance")])
                self.assertIn("docs/acceptance/", state["why"])

    def test_parked_is_never_reported_complete(self):
        world, state = World(rows=()), active()
        guard.on_stop(state, self.STOP, self.env(world, {"state": "parked", "report": "x"}))
        self.assertEqual(state["status"], "parked")

    def test_a_failed_acceptance_write_keeps_the_run_and_tries_again(self):
        world, state = World(rows=()), active()
        env = self.env(world, {"state": "complete", "report": "x"}, writes=OSError("disk full"))
        self.assertIsNone(guard.on_stop(state, self.STOP, env))
        self.assertEqual((state["active"], state["wait_reason"]), (True, "acceptance-unwritable"))
        writes = []
        state["wait_until"] = time.time() - 1  # the ten minutes passed
        self.assertIsNone(guard.on_stop(state, self.STOP, self.env(world, {"state": "complete", "report": "x"}, writes=writes)))
        self.assertEqual((state["status"], len(writes)), ("complete", 1))

    def test_waiting_rows_keep_the_run_and_it_goes_on_when_one_clears(self):
        world, state = World(rows=()), active(changed_at=0, idle=2, yields=2, signature="one")
        env = self.env(world, {"state": "waiting", "detail": "waiting on D00 T02 §3 (waiting-person)"})
        for _ in range(guard.MAX_IDLE_BLOCKS * guard.MAX_YIELDS * 3):  # days of heartbeats, each ending its turn
            self.assertIsNone(guard.on_stop(state, self.STOP, env))
            self.assertEqual((state["active"], state["wait_reason"]), (True, "rows-waiting"))
            state["wait_until"] = time.time() - 1
        world.rows = ("D00 T02 §3",)  # the person answered: the row is ready
        self.assertIn("D00 T02 §3", guard.on_stop(state, self.STOP, env))
        self.assertNotIn("wait_until", state)

    def test_a_passing_wake_sends_a_waiting_run_on_before_its_deadline(self):
        world, state = World(rows=()), active()
        self.assertIsNone(guard.on_stop(state, self.STOP, self.env(world, {"state": "waiting"})))
        self.assertEqual(state["wait_reason"], "rows-waiting")
        self.assertTrue(guard.waiting(state))
        # The Need is still on the section, so nothing is runnable yet: applying the wake is the work.
        env = self.env(world, {"state": "waiting"}, wakes=[{"section": "D00 T02 §3", "need": "third-party"}])
        reason = guard.on_stop(state, self.STOP, env)
        self.assertIn("todo-graph.py wake --apply", reason)
        self.assertNotIn("wait_until", state)
        # A run waiting on anything else is not woken by it.
        state = active()
        guard.set_wait(state, "ci-pending", time.time() + 600)
        self.assertIsNone(guard.on_stop(state, self.STOP, env))

    def test_each_ci_answer_has_its_next_step(self):
        for kind, expect in (("ci-pending", 600), ("ci-unavailable", 1800)):
            with self.subTest(kind):
                world, state = World(rows=()), active()
                self.assertIsNone(guard.on_stop(state, self.STOP, self.env(world, {"state": kind})))
                self.assertEqual(state["wait_reason"], kind)
                self.assertAlmostEqual(state["wait_until"], time.time() + expect, delta=30)
        for kind, words in (("needs-push", "push the finished work"), ("needs-ci-repair", "red-first policy")):
            with self.subTest(kind):
                world, state = World(rows=()), active()
                self.assertIn(words, guard.on_stop(state, self.STOP, self.env(world, {"state": kind})))

    def test_recovery_to_green_ends_complete(self):
        world, state = World(rows=()), active()
        answers = iter([{"state": "needs-ci-repair"}, {"state": "ci-pending"}, {"state": "complete", "report": "x"}])
        env = self.env(world, lambda: next(answers))
        self.assertIn("red-first policy", guard.on_stop(state, self.STOP, env))
        self.assertIsNone(guard.on_stop(state, self.STOP, env))
        self.assertEqual(state["wait_reason"], "ci-pending")
        state["wait_until"] = time.time() - 1
        self.assertIsNone(guard.on_stop(state, self.STOP, env))
        self.assertEqual(state["status"], "complete")

    def test_a_finish_check_that_fails_once_waits_and_looks_again(self):
        def boom():
            raise subprocess.TimeoutExpired("git fetch", 20)
        for finish in ({"state": "unknown"}, boom):
            with self.subTest(finish=finish):
                world, state = World(rows=()), active()
                self.assertIsNone(guard.on_stop(state, self.STOP, self.env(world, finish)))
                self.assertEqual((state["active"], state["wait_reason"]), (True, "finish-unreadable"))

    def test_an_explicit_drained_still_ends_as_before(self):
        world, state = World(rows=()), active()
        self.assertIsNone(guard.on_stop(state, self.STOP, self.env(world, {"state": "drained"})))
        self.assertEqual(state["status"], "drained")


class Waits(unittest.TestCase):
    """A run that waits for an unavailable reviewer neither spins nor stalls, and goes on when the wait passes."""

    def test_the_turn_may_end_while_waiting_without_counting_idle(self):
        world, state = World(), active(idle=0)
        guard.set_wait(state, "reviewer-unavailable", time.time() + 600)
        for _ in range(guard.MAX_IDLE_BLOCKS * guard.MAX_YIELDS * 2):
            self.assertIsNone(guard.on_stop(state, {"session_id": "me"}, world.env()))
        self.assertEqual((state["idle"], state["active"]), (0, True))

    def test_an_outage_across_many_waits_never_stalls_the_run(self):
        world, state = World(), active(changed_at=0)
        for _ in range(guard.MAX_IDLE_BLOCKS * guard.MAX_YIELDS * 4):
            # The wait passes, the turn is sent back, and the reviewer is still down: the dispatcher sets the next wait.
            guard.set_wait(state, "reviewer-unavailable", time.time() - 1)
            self.assertIn("Unattended run is active", guard.on_stop(state, {"session_id": "me"}, world.env()))
        self.assertTrue(state["active"])

    def test_a_recovered_reviewer_lets_the_run_go_on(self):
        world, state = World(), active()
        guard.set_wait(state, "reviewer-unavailable", time.time() - 1)
        self.assertIn("D01 T02 §4", guard.on_stop(state, {"session_id": "me"}, world.env()))
        self.assertNotIn("wait_until", state)
        self.assertIn("last_wait_ended", state)

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
            (Path(folder) / "state.json").write_text(json.dumps(active(started_at=time.time())), encoding="utf-8")
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


class AntigravityRuns(unittest.TestCase):
    """D00 T03 §19: an Antigravity run is held through Antigravity's own Stop hook, lives while its launcher's window
    process does, and, with no heartbeat to wake it, is sent on to wait inside its session."""

    SESSION = "4a6e0b1c-agy0-4000-8000-000000000019"

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name) / "state.json"
        self.owner = {"pid": 4242, "started": 777, "alive": True}

    def env(self, world=None, reviewing=None, ci=None):
        base = (world or World()).env()
        return guard.replace(base, process_alive=lambda pid, started: self.owner["alive"] and (pid, started) == (self.owner["pid"], self.owner["started"]),
                             reviewing=lambda: reviewing, ci_waiting=lambda: ci)

    def run_state(self, **extra):
        state = {}
        guard.start(state, self.SESSION, self.env(), runner="antigravity", owner_pid=self.owner["pid"], owner_started=self.owner["started"])
        state.update(extra)
        return state

    def stop(self, state, env=None, termination="NO_TOOL_CALL", said="Finish the Bosun implementation plan", session=None):
        guard.save(state, self.path)
        raw = json.dumps({"hookEventName": "Stop", "conversationId": "c-1", "cwd": str(guard.ROOT), "terminationReason": termination,
                          "continuation_active": True, "lastUserInput": said})
        out = guard.hook_antigravity(raw, session or self.SESSION, env or self.env(), self.path)
        return (json.loads(out) if out else None), guard.load(self.path)

    def test_the_run_lives_while_its_owner_process_does_and_not_after(self):
        state = self.run_state()
        self.assertEqual((state["runner"], state["owner_pid"]), ("antigravity", 4242))
        self.assertFalse(guard.close_if_gone(state, self.env()), "a live owner's run was ended")
        reused = dict(state, owner_started=778)  # the same pid, started at another time: another process
        self.assertTrue(guard.close_if_gone(reused, self.env()), "a reused pid kept the run alive")
        self.owner["alive"] = False
        self.assertTrue(guard.close_if_gone(state, self.env()))
        self.assertEqual(state["status"], "ended")

    def test_a_claude_start_is_refused_while_it_lives_and_the_other_way_round(self):
        state = self.run_state()
        with self.assertRaisesRegex(ValueError, "antigravity session .* owns the active run"):
            guard.start(state, "claude-session", self.env())
        claude = active(session_id="claude-session")
        with self.assertRaisesRegex(ValueError, "owns the active run"):
            guard.start(claude, self.SESSION, self.env(World(alive=["claude-session"])), runner="antigravity",
                        owner_pid=self.owner["pid"], owner_started=self.owner["started"])
        with self.assertRaisesRegex(ValueError, "needs its owner process"):
            guard.start({}, self.SESSION, self.env(), runner="antigravity")

    def test_a_run_claimed_a_moment_ago_refuses_another_start(self):
        claude = {}
        guard.start(claude, "claude-session", self.env(), now=1000.0)
        with self.assertRaisesRegex(ValueError, "about to start"):
            guard.start(claude, self.SESSION, self.env(), now=1000.0 + guard.START_GRACE - 1, runner="antigravity",
                        owner_pid=self.owner["pid"], owner_started=self.owner["started"])
        with self.assertRaisesRegex(ValueError, "about to start"):
            guard.start(claude, "another-claude", self.env(), now=1000.0 + 5)
        guard.start(claude, self.SESSION, self.env(), now=1000.0 + guard.START_GRACE, runner="antigravity",
                    owner_pid=self.owner["pid"], owner_started=self.owner["started"])
        self.assertEqual((claude["session_id"], claude["runner"]), (self.SESSION, "antigravity"))

    def test_a_claude_session_taking_over_a_dead_antigravity_run_owns_it_itself(self):
        state = self.run_state()
        self.owner["alive"] = False  # the Antigravity window was closed
        world = World(alive=["claude-live"])
        guard.on_prompt(state, {"prompt": "process plan", "session_id": "claude-live"}, self.env(world))
        self.assertEqual(state["session_id"], "claude-live")
        self.assertFalse({"runner", "owner_pid", "owner_started"} & set(state), "the former owner's process stayed")
        self.assertFalse(guard.close_if_gone(state, self.env(world)), "the live Claude Code run was taken for gone")
        with self.assertRaisesRegex(ValueError, "owns the active run"):
            guard.start(state, "second-run", self.env(world), runner="antigravity", owner_pid=self.owner["pid"], owner_started=self.owner["started"])

    def test_end_records_its_own_run_ended_and_nothing_else(self):
        state = self.run_state()
        self.assertTrue(guard.end(state, self.SESSION, "agy exited (0)"))
        self.assertEqual((state["active"], state["status"], state["why"]), (False, "ended", "agy exited (0)"))
        again = dict(state)
        self.assertFalse(guard.end(again, self.SESSION, "twice"))
        self.assertEqual(again, state)
        halted = self.run_state()
        guard.halt(halted, "operator", "pause")
        kept = dict(halted)
        self.assertFalse(guard.end(halted, self.SESSION, "agy exited (0)"))
        self.assertEqual(halted, kept)
        other = self.run_state()
        self.assertFalse(guard.end(other, "someone-else", "agy exited (0)"))
        self.assertTrue(other["active"])

    def test_rows_left_send_the_session_on_without_heartbeat_advice(self):
        answer, state = self.stop(self.run_state(signature="one"))
        self.assertEqual(answer["decision"], "continue")
        self.assertIn("Unattended run is active", answer["reason"])
        self.assertNotIn("CronCreate", answer["reason"])
        self.assertTrue(state["active"])

    def test_a_review_or_a_ci_gate_in_flight_is_waited_for_in_the_session(self):
        for kwargs, what in (({"reviewing": "astra, dispatcher pid 1"}, "a review"), ({"ci": "abc123, gate pid 2"}, "a CI gate")):
            answer, _ = self.stop(self.run_state(), self.env(**kwargs))
            self.assertEqual(answer["decision"], "continue")
            self.assertIn(f"waiting for {what} in flight", answer["reason"])
            self.assertIn("wait for it in this session", answer["reason"])

    def test_a_timed_wait_is_answered_with_its_end_then_the_ordinary_continuation(self):
        state = self.run_state(signature="one")
        guard.set_wait(state, "reviewer-unavailable", time.time() + 1800, "the reviewer is rate limited")
        answer, kept = self.stop(state)
        self.assertIn("waits for reviewer-unavailable until", answer["reason"])
        self.assertIn(guard.stamp(state["wait_until"]), answer["reason"])
        self.assertIn("sleep in steps of at most 10 minutes", answer["reason"])
        self.assertIn("wait_until", kept, "the wait was dropped before it passed")
        kept["wait_until"] = time.time() - 1
        answer, after = self.stop(kept)
        self.assertIn("Unattended run is active", answer["reason"])
        self.assertNotIn("wait_until", after)

    def test_the_end_states_and_another_session_answer_nothing(self):
        state = self.run_state()
        guard.halt(state, "operator", "pause")
        self.assertEqual(self.stop(state)[0], None)
        self.assertEqual(self.stop(self.run_state(), session="someone-else")[0], None)
        answer, state = self.stop(self.run_state(), termination="TERMINATION_REASON_CHAINED")
        self.assertEqual((answer, state["active"]), (None, True), "a chained step ended the run")

    def test_a_turn_antigravity_ended_itself_is_recorded_not_left_active(self):
        answer, state = self.stop(self.run_state(), termination="EXECUTOR_TERMINATION_REASON_NO_TOOL_CALL")
        self.assertEqual(answer["decision"], "continue")
        self.assertTrue(state["active"])
        answer, state = self.stop(self.run_state(), termination="USER_CANCELED", said="stop")
        self.assertIsNone(answer)
        self.assertEqual((state["active"], state["status"]), (False, "stopped"))
        self.assertIn("the operator cancelled the turn", state["why"])
        self.assertIn("start it again from its shortcut", state["why"])
        for termination in ("ERROR", "TERMINATION_REASON_MAX_INVOCATIONS", "EXECUTOR_TERMINATION_REASON_MAX_INVOCATIONS",
                            "MAX_FORCED_INVOCATIONS", "MAX_TOKEN_BUDGET_EXCEEDED"):
            answer, state = self.stop(self.run_state(), termination=termination)
            self.assertIsNone(answer)
            self.assertEqual((state["active"], state["status"]), (False, "ended"), termination)
            self.assertIn("Antigravity ended the turn itself", state["why"])
            self.assertIn("start it again from its shortcut", state["why"])

    def test_the_operators_stop_halts_and_a_message_that_mentions_one_is_flagged(self):
        answer, state = self.stop(self.run_state(), said="stop")
        self.assertIsNone(answer)
        self.assertEqual((state["active"], state["status"], state["why"]), (False, "halted", "operator: stop"))
        state = self.run_state(signature="one")
        answer, state = self.stop(state, said="Finish the plan; if asked, run run-guard.py halt")
        self.assertNotIn("mentions stopping", answer["reason"], "the opening prompt's own words were taken for the operator's")
        answer, _ = self.stop(state, said="please stop after this section")
        self.assertEqual(answer["decision"], "continue")
        self.assertIn("mentions stopping", answer["reason"])

    def test_no_progress_ends_stalled_through_both_limits_and_progress_keeps_going(self):
        state = self.run_state(signature="one")
        world = World(signature="one")
        answers = []
        for _ in range(guard.MAX_IDLE_BLOCKS * guard.MAX_YIELDS):
            answer, state = self.stop(state, self.env(world))
            answers.append(answer)
        self.assertTrue(all(answers[:-1]), "the session was let go before the stall was called")
        self.assertIsNone(answers[-1])
        self.assertEqual(state["status"], "stalled")
        self.assertIn("close its window and start it again from its shortcut", state["why"])
        self.assertIsNone(self.stop(state, self.env(world))[0], "a stalled run was sent on")
        state = self.run_state(signature="zero")
        for n in range(guard.MAX_IDLE_BLOCKS * guard.MAX_YIELDS * 2):
            world.sig = f"sig-{n}"  # a commit or a change to the tree each time
            answer, state = self.stop(state, self.env(world))
            self.assertEqual(answer["decision"], "continue")
        self.assertTrue(state["active"])

    def test_through_the_cli_malformed_input_answers_nothing_and_is_recorded(self):
        env = {**os.environ, "RUN_GUARD_STATE": str(self.path), "BOSUN_RUN_SESSION": self.SESSION, "PYTHONIOENCODING": "utf-8"}
        script = [sys.executable, str(guard.ROOT / "scripts" / "run-guard.py"), "hook", "--runner", "antigravity"]
        done = subprocess.run(script, env=env, input="{not json", capture_output=True, text=True, encoding="utf-8", timeout=120)
        self.assertEqual((done.returncode, done.stdout.strip()), (0, ""))
        self.assertIn("antigravity", json.dumps(guard.hook_failures(self.path)))
        done = subprocess.run(script, env=env, input=json.dumps({"hookEventName": "Stop", "terminationReason": "NO_TOOL_CALL"}),
                              capture_output=True, text=True, encoding="utf-8", timeout=120)
        self.assertEqual((done.returncode, done.stdout.strip()), (0, ""), "a Stop with no active run was answered")

    def test_start_and_end_through_the_cli(self):
        # A build folder of its own: the writer's own reviews and CI gates in flight would read as waits here.
        env = {**os.environ, "RUN_GUARD_STATE": str(self.path), "RUN_GUARD_BUILD": str(Path(self.folder.name) / "build"), "PYTHONIOENCODING": "utf-8"}
        script = [sys.executable, str(guard.ROOT / "scripts" / "run-guard.py")]
        cli = lambda *args: subprocess.run([*script, *args], env=env, capture_output=True, text=True, encoding="utf-8", timeout=120)
        done = cli("start", "--session", self.SESSION, "--runner", "antigravity", "--owner-pid", str(os.getpid()))
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        data = json.loads(cli("status", "--json").stdout)
        self.assertEqual((data["state"], data["runner"]), ("active", "antigravity"))
        self.assertEqual(cli("end", "--session", self.SESSION, "--reason", "agy exited (1)").stdout.strip(), "ended")
        self.assertEqual(json.loads(cli("status", "--json").stdout)["state"], "ended")
        self.assertEqual(cli("end", "--session", self.SESSION).stdout.strip(), "ended", "a second end changed the record")

    def test_the_workspace_hook_is_wired(self):
        doc = json.loads((guard.ROOT / ".agents" / "hooks.json").read_text(encoding="utf-8"))
        self.assertNotIn("hooks", doc, "Claude Code's outer 'hooks' wrapper causes agy to skip .agents/hooks.json")
        stops = doc["run-guard"]["Stop"]
        self.assertIsInstance(stops, list)
        self.assertEqual(len(stops), 1)
        self.assertEqual(stops[0]["type"], "command")
        self.assertEqual(stops[0]["command"], "python ../scripts/run-guard.py hook --runner antigravity")
        self.assertNotIn("matcher", stops[0])
        self.assertGreater(stops[0]["timeout"], 0)
        # Prove the command resolves ../scripts/run-guard.py from .agents/, where agy executes workspace hooks.
        state = {"active": True, "status": "active", "session_id": self.SESSION, "runner": "antigravity",
                 "owner_pid": os.getpid(), "owner_started": guard.process_started(os.getpid()),
                 "started_at": time.time(), "wait_reason": "reviewer-unavailable", "wait_until": time.time() + 300}
        guard.save(state, self.path)
        env = {**os.environ, "RUN_GUARD_STATE": str(self.path), "RUN_GUARD_BUILD": str(Path(self.folder.name) / "build"),
               "BOSUN_RUN_SESSION": self.SESSION, "PYTHONIOENCODING": "utf-8"}
        cmd = stops[0]["command"] if shutil.which("python") else stops[0]["command"].replace("python ", f'"{sys.executable}" ', 1)
        done = subprocess.run(cmd, shell=True, cwd=guard.ROOT / ".agents", env=env,
                              input=json.dumps({"hookEventName": "Stop", "terminationReason": "EXECUTOR_TERMINATION_REASON_NO_TOOL_CALL"}),
                              capture_output=True, text=True, encoding="utf-8", timeout=120)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(json.loads(done.stdout)["decision"], "continue")
        agy_exe = shutil.which("agy")
        if agy_exe:
            repo = Path(self.folder.name) / "repo"
            (repo / ".agents").mkdir(parents=True)
            (repo / "scripts").mkdir(parents=True)
            (repo / ".agents" / "hooks.json").write_text((guard.ROOT / ".agents" / "hooks.json").read_text(encoding="utf-8"), encoding="utf-8")
            (repo / ".agents" / "plugin.json").write_text(json.dumps({"name": "bosun-test", "version": "0.1.0", "description": "test"}), encoding="utf-8")
            (repo / "scripts" / "run-guard.py").write_text("", encoding="utf-8")
            validated = subprocess.run([agy_exe, "plugin", "validate", str(repo / ".agents"), "--json"],
                                       capture_output=True, text=True, encoding="utf-8", timeout=60)
            self.assertEqual(validated.returncode, 0, validated.stdout + validated.stderr)
            report = json.loads(validated.stdout)
            self.assertEqual(report["components"]["hooks"], 1)
            self.assertEqual([f for f in report.get("findings", []) if f.get("file") == "hooks.json"], [])


class LocalGates(unittest.TestCase):
    """D00 T03 §21: a long local gate (check-all, a build measurement) is a wait, as a review or a CI gate is: its mark
    holds while the gate's process runs, the turn may end under it, and `status` says waiting."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        self.marks = self.root / "build" / "gate-in-flight"
        self.marks.mkdir(parents=True)
        self.path = self.root / "state.json"

    def mark(self, name="1.json", **doc):
        (self.marks / name).write_text(json.dumps(doc), encoding="utf-8")

    def live(self, **extra):
        return {"pid": os.getpid(), "started": guard.process_started(os.getpid()), "what": "check-all", **extra}

    def test_only_a_mark_of_the_process_still_running_counts(self):
        self.assertIsNone(guard.gate_in_flight(self.root))
        self.mark(**self.live(since="2026-01-01T00:00:00Z"))  # older than any step's limit: age is not what ends it
        self.assertIn("check-all, pid", guard.gate_in_flight(self.root))
        (self.marks / "1.json").unlink()
        dead = int(subprocess.run([sys.executable, "-c", "import os; print(os.getpid())"], capture_output=True, text=True).stdout)
        for doc in ({"pid": dead, "started": 1, "what": "gone"}, {**self.live(), "started": 1}, {"what": "no pid"}):
            self.mark(**doc)
            self.assertIsNone(guard.gate_in_flight(self.root), f"{doc} held the run")
        (self.marks / "1.json").write_text("{not json", encoding="utf-8")
        self.assertIsNone(guard.gate_in_flight(self.root))

    def test_every_gate_running_at_once_is_named_and_one_ending_leaves_the_other(self):
        holder = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"], **guard.hidden())
        self.addCleanup(lambda: (holder.kill(), holder.wait()))
        self.mark("a.json", **self.live(what="check-all"))
        self.mark("b.json", pid=holder.pid, started=guard.process_started(holder.pid), what="measure-build baseline")
        both = guard.gate_in_flight(self.root)
        self.assertIn("check-all, pid", both)
        self.assertIn(f"measure-build baseline, pid {holder.pid}", both)
        holder.kill()
        holder.wait()
        self.assertEqual(guard.gate_in_flight(self.root), f"check-all, pid {os.getpid()}", "the gate still running was hidden")

    def env(self, world, gate):
        return guard.replace(world.env(), gating=lambda: gate)

    def test_a_claude_turn_may_end_under_a_gate_without_spending_its_budget(self):
        world = World(signature="one")
        state = active(signature="one", idle=1, yields=1)
        for _ in range(guard.MAX_IDLE_BLOCKS * guard.MAX_YIELDS * 2):
            self.assertIsNone(guard.on_stop(state, {"session_id": "me"}, self.env(world, "check-all, pid 1")))
        self.assertEqual((state["idle"], state["yields"], state["active"]), (1, 1, True))
        self.assertIn("Unattended run is active", guard.on_stop(state, {"session_id": "me"}, self.env(world, None)))

    def test_an_antigravity_session_is_sent_to_wait_for_the_gate(self):
        state = {}
        env = guard.replace(World().env(), process_alive=lambda pid, started: True, gating=lambda: "measure-build x, pid 7")
        guard.start(state, "agy", env, runner="antigravity", owner_pid=7, owner_started=1)
        reason = guard.on_antigravity_stop(state, {"session_id": "agy", "termination": "NO_TOOL_CALL", "last_user_input": ""}, env)
        self.assertIn("waiting for a local gate in flight (measure-build x, pid 7)", reason)
        self.assertIn("wait for it in this session", reason)

    def test_status_waits_for_each_thing_in_flight_and_is_active_after(self):
        state = active()
        for field, value, what in (("reviewing", "astra, dispatcher pid 1", "a review"), ("ci_waiting", "abc, gate pid 2", "a CI gate"),
                                   ("gating", "check-all, pid 3", "a local gate")):
            env = guard.replace(World().env(), **{field: lambda value=value: value})
            word, data, lines = guard.status_view(state, env, path=self.path)
            self.assertEqual((word, data["state"]), ("waiting", "waiting"), what)
            self.assertEqual(data["in_flight"], f"{what} in flight ({value})")
            self.assertIn(f"waiting for {what} in flight ({value})", lines)
        both = guard.replace(World().env(), ci_waiting=lambda: "abc, gate pid 2", gating=lambda: "check-all, pid 3")
        self.assertEqual(guard.status_view(state, both, path=self.path)[1]["in_flight"],
                         "a CI gate in flight (abc, gate pid 2); a local gate in flight (check-all, pid 3)", "one wait hid another")
        word, data, lines = guard.status_view(state, World().env(), path=self.path)
        self.assertEqual((word, "in_flight" in data, lines), ("active", False, []))
        self.assertEqual(guard.status_view({"active": False, "status": "ended"}, guard.replace(World().env(), gating=lambda: "x"), path=self.path)[0], "ended")

    def test_through_the_cli_a_real_mark_holds_both_runners_and_status(self):
        holder = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"], **guard.hidden())
        self.addCleanup(lambda: (holder.kill(), holder.wait()))
        # A build folder of the test's own (RUN_GUARD_BUILD): the repository's holds the mark of the gate running this
        # suite (round 1 of the review) and the writer's own reviews and CI gates in flight.
        build = self.root / "own-build"
        marks = build / "gate-in-flight"
        marks.mkdir(parents=True)
        mark = marks / f"{holder.pid}.json"
        self.addCleanup(lambda: mark.unlink(missing_ok=True))
        mark.write_text(json.dumps({"pid": holder.pid, "started": guard.process_started(holder.pid), "what": "check-all (test)"}), encoding="utf-8")
        env = {k: v for k, v in os.environ.items() if k != "BOSUN_GATE_MARK_DIR"}
        env.update(RUN_GUARD_STATE=str(self.path), RUN_GUARD_BUILD=str(build), PYTHONIOENCODING="utf-8")
        script = [sys.executable, str(guard.ROOT / "scripts" / "run-guard.py")]
        cli = lambda *args, **kw: subprocess.run([*script, *args], env={**env, **kw.pop("extra", {})}, capture_output=True, text=True,
                                                  encoding="utf-8", timeout=300, **kw)
        guard.save(active(session_id="claude-1", changed_at=time.time(), started_at=time.time()), self.path)
        done = cli("hook", input=json.dumps({"hook_event_name": "Stop", "session_id": "claude-1"}))
        self.assertEqual((done.returncode, done.stdout.strip()), (0, ""), "a Claude Code Stop under a running gate was sent back")
        shown = cli("status")
        self.assertEqual(shown.stdout.splitlines()[0], "waiting")
        self.assertIn("waiting for a local gate in flight (check-all (test), pid", shown.stdout)
        self.assertIn("a local gate in flight", json.loads(cli("status", "--json").stdout)["in_flight"])
        state = {}
        guard.start(state, "agy-1", guard.REAL, runner="antigravity", owner_pid=os.getpid(), owner_started=guard.process_started(os.getpid()))
        guard.save(state, self.path)
        done = cli("hook", "--runner", "antigravity", extra={"BOSUN_RUN_SESSION": "agy-1"},
                   input=json.dumps({"hookEventName": "Stop", "terminationReason": "NO_TOOL_CALL"}))
        self.assertIn("waiting for a local gate in flight (check-all (test), pid", json.loads(done.stdout)["reason"])
        mark.unlink()
        self.assertEqual(cli("status").stdout.splitlines()[0], "active", "the wait outlived the gate's mark")


class FakeHold:
    """A campaign hold record and its automatic restore, as the guard sees them (D00 T03 §24)."""

    def __init__(self, applied, expires=None, ok=True):
        self.record = {"applied": applied, "expires": applied + 86400 if expires is None else expires, "restore_tried": None}
        self.ok, self.restores = ok, 0

    def hold(self):
        return None if self.record is None else dict(self.record)

    def restore(self, mark=""):
        self.marks = getattr(self, "marks", []) + [mark]
        self.restores += 1
        if self.ok:
            self.record = None
            return {"exit": 0, "output": "restored every held value; record removed"}
        self.record["restore_tried"] = time.time()
        return {"exit": 1, "output": "FAIL restore left the record"}

    def env(self, world=None):
        return guard.replace((world or World()).env(), hold=self.hold, restore_hold=self.restore)


class CampaignHold(unittest.TestCase):
    """D00 T03 §24: the hold is restored once, by whichever path ends the run it was applied for, or at its expiry."""

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name) / "state.json"
        self.now = time.time()

    def run_state(self, **extra):
        return {**active(started_at=self.now - 3600, started=guard.stamp(self.now - 3600)), **extra}

    def test_each_hook_path_that_ends_a_run_restores_once(self):
        cases = {"SessionEnd": ({"hook_event_name": "SessionEnd", "session_id": "me", "reason": "other"}, World()),
                 "the Stop hook's drain": ({"hook_event_name": "Stop", "session_id": "me"}, World(rows=()))}
        for name, (event, world) in cases.items():
            with self.subTest(name):
                fake = FakeHold(applied=self.now - 600)
                guard.save(self.run_state(), self.path)
                guard.hook(json.dumps(event), fake.env(world), self.path)
                state = guard.load(self.path)
                self.assertFalse(state["active"])
                self.assertEqual(fake.restores, 1)
                self.assertEqual(state["hold_restore"]["why"], "the run it was applied for ended")
                guard.hook(json.dumps({"hook_event_name": "SessionStart", "session_id": "next"}), fake.env(world), self.path)
                self.assertEqual(fake.restores, 1, "a restored hold is not restored again")

    def test_an_antigravity_run_that_ends_restores_its_hold(self):
        session = "4a6e0b1c-agy0-4000-8000-000000000024"
        fake = FakeHold(applied=self.now - 600)
        env = guard.replace(fake.env(), process_alive=lambda pid, started: (pid, started) == (4242, 777))
        state = {}
        guard.start(state, session, env, runner="antigravity", owner_pid=4242, owner_started=777)
        guard.save(state, self.path)
        raw = json.dumps({"hookEventName": "Stop", "conversationId": "c-1", "cwd": str(guard.ROOT), "terminationReason": "USER_CANCELED",
                          "continuation_active": True, "lastUserInput": "process plan"})
        guard.hook_antigravity(raw, session, env, self.path)
        self.assertFalse(guard.load(self.path)["active"])
        self.assertEqual(fake.restores, 1)

    def test_a_hold_applied_after_a_run_ended_waits_for_the_next_run_to_end(self):
        fake = FakeHold(applied=self.now)
        ended = {"active": False, "status": "ended", "session_id": "old", "ended": guard.stamp(self.now - 86400)}
        guard.save(ended, self.path)
        guard.settle_gone(fake.env(), self.path, now=self.now + 1)  # the status read before launch
        self.assertEqual(fake.restores, 0)
        state = guard.load(self.path)
        guard.start(state, "me", fake.env(), now=self.now + 2)
        guard.save(state, self.path)
        guard.hook(json.dumps({"hook_event_name": "Stop", "session_id": "me"}), fake.env(World(alive=("me",))), self.path)
        self.assertEqual(fake.restores, 0, "a live run's ordinary stop restores nothing")
        guard.hook(json.dumps({"hook_event_name": "SessionEnd", "session_id": "me", "reason": "other"}), fake.env(), self.path)
        self.assertEqual(fake.restores, 1)

    def test_a_run_that_died_before_the_hold_never_restores_it(self):
        # Round 1: a window closed with no SessionEnd, a hold applied for the next campaign, then a status read
        # recorded the old run's end, later than the hold, and undid it.
        fake = FakeHold(applied=self.now)
        guard.save(self.run_state(), self.path)  # the old run, still recorded active, its session gone
        state = guard.settle_gone(fake.env(World(alive=())), self.path, now=self.now + 60)
        self.assertFalse(state["active"])
        self.assertEqual(fake.restores, 0)
        self.assertIsNotNone(fake.record)

    def test_a_restore_that_failed_before_marking_the_record_is_not_tried_again(self):
        # Round 1: the script could fail before writing RestoreTried (an unwritable folder), and every read retried.
        fake = FakeHold(applied=self.now - 600, expires=self.now - 1)
        fake.restore = lambda mark: (setattr(fake, "restores", fake.restores + 1), {"exit": 1, "output": "FAIL cannot write"})[1]
        guard.save(self.run_state(), self.path)
        world = World(alive=("me",))
        guard.settle_gone(fake.env(world), self.path, now=self.now)
        guard.settle_gone(fake.env(world), self.path, now=self.now + 30)
        guard.hook(json.dumps({"hook_event_name": "Stop", "session_id": "me"}), fake.env(world), self.path)
        self.assertEqual(fake.restores, 1)

    def test_an_older_holds_failure_does_not_ask_to_undo_a_new_hold(self):
        # Round 1: a failed restore, a manual restore, a fresh hold: status asked to restore the fresh one.
        fake = FakeHold(applied=self.now)
        state = self.run_state(hold_restore={"at": guard.stamp(self.now - 3600), "applied": guard.stamp(self.now - 7200),
                                             "why": "the hold expired", "exit": 1, "output": "FAIL"})
        word, data, lines = guard.status_view(state, fake.env())
        self.assertFalse([line for line in lines if "failed" in line], lines)
        self.assertEqual(data["hold_restore"]["exit"], 1, "the old attempt is still in the JSON")

    def test_only_the_runs_own_session_claims_its_hold(self):
        # Completion review: a foreign session's hook claimed a fresh hold for a dead run, and the next status undid it.
        fake = FakeHold(applied=self.now)
        guard.save(self.run_state(), self.path)
        guard.hook(json.dumps({"hook_event_name": "Stop", "session_id": "someone-else"}), fake.env(World(alive=("me",))), self.path)
        self.assertNotIn("hold_for", guard.load(self.path))
        guard.settle_gone(fake.env(World(alive=())), self.path, now=self.now + 60)
        self.assertEqual(fake.restores, 0, "a hold no event of its run ever saw is left for the next run")

    def test_a_claimed_run_that_crashes_is_restored_with_the_hold_it_saw(self):
        fake = FakeHold(applied=self.now - 60)
        state = self.run_state()
        self.assertTrue(guard.claim_hold(state, fake.hold()))  # as the launcher's start claims it, before any hook
        guard.save(state, self.path)
        guard.settle_gone(fake.env(World(alive=())), self.path, now=self.now)
        self.assertEqual(fake.restores, 1)
        self.assertEqual(fake.marks, [guard.stamp(self.now - 60)], "the restore names the hold it was chosen for")

    def test_a_quiet_live_run_restores_at_expiry_on_a_status_read(self):
        fake = FakeHold(applied=self.now - 600, expires=self.now + 100)
        guard.save(self.run_state(), self.path)
        world = World(alive=("me",))
        guard.settle_gone(fake.env(world), self.path, now=self.now + 99)
        self.assertEqual(fake.restores, 0)
        state = guard.settle_gone(fake.env(world), self.path, now=self.now + 100)
        self.assertEqual(fake.restores, 1)
        self.assertTrue(state["active"], "an expired hold goes back; the run goes on")
        self.assertEqual(guard.load(self.path)["hold_restore"]["why"], "the hold expired")

    def test_another_sessions_end_leaves_the_hold(self):
        fake = FakeHold(applied=self.now - 600)
        state = self.run_state()
        self.assertFalse(guard.end(state, "other", "a stale launcher"))
        guard.release_hold(state, fake.env(), self.now)  # the run still active claims its hold, and restores nothing
        self.assertEqual(fake.restores, 0)
        self.assertTrue(state["active"])

    def test_a_failed_restore_is_tried_once_and_kept_across_a_new_run(self):
        fake = FakeHold(applied=self.now - 600, ok=False)
        guard.save(self.run_state(), self.path)
        guard.hook(json.dumps({"hook_event_name": "SessionEnd", "session_id": "me", "reason": "other"}), fake.env(), self.path)
        self.assertEqual(fake.restores, 1)
        state = guard.load(self.path)
        guard.start(state, "next", fake.env(), now=self.now + 5)
        self.assertEqual(state["hold_restore"]["exit"], 1, "a new run keeps the failed restore for status")
        guard.save(state, self.path)
        guard.hook(json.dumps({"hook_event_name": "SessionEnd", "session_id": "next", "reason": "other"}), fake.env(), self.path)
        self.assertEqual(fake.restores, 1, "the record's mark stops a second automatic restore")
        word, data, lines = guard.status_view(guard.load(self.path), fake.env())
        self.assertIn("restore_tried", data["hold"])
        self.assertTrue(any("failed" in line and "campaign-hold.ps1 -Restore" in line for line in lines), lines)
        self.assertTrue(any("tried at" in line for line in lines), lines)

    def test_status_shows_a_hold_and_nothing_without_one(self):
        fake = FakeHold(applied=self.now - 7200)
        word, data, lines = guard.status_view(self.run_state(), fake.env())
        self.assertEqual(sorted(data["hold"]), ["applied", "expires", "restore_tried"])
        self.assertTrue(any(line.startswith("hold: applied 2 h 0 min ago, restored at the first hook event or status read") for line in lines), lines)
        fake.record = None
        word, data, lines = guard.status_view(self.run_state(), fake.env())
        self.assertNotIn("hold", data)
        self.assertFalse([line for line in lines if line.startswith("hold:")])

    def test_a_slow_restore_is_stopped_at_its_budget_with_its_tree(self):
        pid_file = Path(self.folder.name) / "child.pid"
        code = ("import subprocess, sys, time; "
                "c = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)']); "
                f"open(r'{pid_file}', 'w').write(str(c.pid)); time.sleep(9)")
        began = time.monotonic()
        result = guard.real_restore_hold("", [sys.executable, "-c", code])
        took = time.monotonic() - began
        self.assertIsNone(result["exit"])
        self.assertIn("stopped, with its process tree, after 7 s", result["output"])
        self.assertLess(took, 9.5)
        child = int(pid_file.read_text())
        time.sleep(0.5)
        self.assertFalse(guard.pid_alive(child), "the restore's child outlived it")

    def test_the_real_reader_reads_the_scripts_record(self):
        record = Path(self.folder.name) / "campaign-hold.json"
        record.write_text(json.dumps({"Scheme": "x", "Power": [], "Lock": None, "Applied": "2026-11-21T10:00:00.1234567Z",
                                      "Expires": "2026-11-22T10:00:00.1234567Z"}), encoding="utf-8")
        hold = guard.real_hold(record)
        self.assertEqual(guard.stamp(hold["applied"]), "2026-11-21T10:00:00Z")
        self.assertEqual(hold["expires"] - hold["applied"], 86400)
        self.assertIsNone(hold["restore_tried"])
        record.write_text(json.dumps({"Scheme": "x", "Power": [], "Lock": None}), encoding="utf-8")
        os.utime(record, (1763712000, 1763712000))
        # An old record has no expiry: a day from its write time restored a live campaign's hold on 2026-11-21.
        self.assertEqual(guard.real_hold(record), {"applied": 1763712000, "expires": None, "restore_tried": None})
        self.assertIsNone(guard.hold_due({**active()}, guard.real_hold(record), time.time()))
        record.write_text(json.dumps({"Scheme": "x", "Applied": "soon"}), encoding="utf-8")
        self.assertIn("error", guard.real_hold(record))
        with mock.patch.dict(os.environ, {"RUN_GUARD_STATE": "x"}, clear=False):
            os.environ.pop("BOSUN_HOLD_RECORD", None)
            self.assertIsNone(guard.hold_record_path(), "a guard on another state never reads the machine's hold")


@unittest.skipUnless(os.name == "nt" and shutil.which("pwsh"), "the hold is Windows' powercfg and registry")
class CampaignHoldEndToEnd(unittest.TestCase):
    """The real guard command ends a run and the real hold script restores the fixture, with no -Restore or status
    call from the test (D00 T03 §24)."""

    ORIGINAL = {"scheme": "381b4222-f694-41f0-9685-ff5bb260df2e",
                "values": {"SUB_SLEEP/HIBERNATEIDLE": {"AC": 0, "DC": 10800}, "SUB_SLEEP/STANDBYIDLE": {"AC": 1800, "DC": 600},
                           "SUB_VIDEO/VIDEOIDLE": {"AC": 7200, "DC": 180}}}

    def setUp(self):
        import winreg
        self.winreg = winreg
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        work = Path(self.folder.name)
        self.power = work / "power.json"
        self.power.write_text(json.dumps(self.ORIGINAL), encoding="utf-8")
        cmd = work / "powercfg.cmd"
        cmd.write_text(f'@"{sys.executable}" "{guard.ROOT / "tests" / "fakes" / "powercfg.py"}" %*\r\n', encoding="ascii")
        self.record = work / "campaign-hold.json"
        self.subkey = f"Software\\BosunTest\\guard-hold-{os.getpid()}"
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, self.subkey) as key:
            winreg.SetValueEx(key, "ScreenSaverIsSecure", 0, winreg.REG_SZ, "1")
        self.addCleanup(lambda: winreg.DeleteKey(winreg.HKEY_CURRENT_USER, self.subkey))
        self.env = {**os.environ, "PYTHONIOENCODING": "utf-8", "RUN_GUARD_STATE": str(work / "state.json"),
                    "RUN_GUARD_BUILD": str(work / "build"), "BOSUN_FAKE_POWERCFG": str(self.power),
                    "BOSUN_HOLD_RECORD": str(self.record), "BOSUN_HOLD_POWERCFG": str(cmd),
                    "BOSUN_HOLD_LOCKKEY": f"HKCU:\\{self.subkey}"}

    def cli(self, *args):
        return subprocess.run([sys.executable, str(guard.ROOT / "scripts" / "run-guard.py"), *args], capture_output=True,
                              text=True, encoding="utf-8", env=self.env, timeout=120, creationflags=subprocess.CREATE_NO_WINDOW)

    def lock(self):
        with self.winreg.OpenKey(self.winreg.HKEY_CURRENT_USER, self.subkey) as key:
            return self.winreg.QueryValueEx(key, "ScreenSaverIsSecure")[0]

    def apply(self):
        held = subprocess.run(["pwsh", "-NoProfile", "-File", str(guard.ROOT / "scripts" / "campaign-hold.ps1"), "-Apply"],
                              capture_output=True, text=True, env=self.env, timeout=120, creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(held.returncode, 0, held.stdout + held.stderr)
        self.assertEqual(self.lock(), "0")

    def test_ending_or_halting_the_run_restores_the_machine(self):
        for how in ("end", "halt"):
            with self.subTest(how):
                self.apply()
                self.assertEqual(self.cli("start", "--session", "me").returncode, 0)
                self.assertIn("hold_for", json.loads(Path(self.env["RUN_GUARD_STATE"]).read_text(encoding="utf-8")), "the launcher's start claims the hold in force")
                stale = self.cli("end", "--session", "someone-else", "--reason", "a stale launcher")
                self.assertEqual(stale.returncode, 0, stale.stdout)
                self.assertTrue(self.record.exists(), "another session's end restored the hold")
                began = time.monotonic()
                done = self.cli(*(("end", "--session", "me", "--reason", "test") if how == "end" else ("halt", "--ground", "operator", "--reason", "test")))
                took = time.monotonic() - began
                self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
                self.assertEqual(json.loads(self.power.read_text(encoding="utf-8"))["values"], self.ORIGINAL["values"])
                self.assertEqual(self.lock(), "1")
                self.assertFalse(self.record.exists())
                self.assertLess(took, 5, f"the end, its restore included, took {took:.1f} s")
                Path(self.env["RUN_GUARD_STATE"]).unlink()


if __name__ == "__main__":
    unittest.main()
