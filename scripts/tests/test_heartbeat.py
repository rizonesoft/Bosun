"""The global heartbeat: one pass at a time, one resume per pass, and no repo ever left with no watchdog."""
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import heartbeat as hb  # noqa: E402

CANT_FIND = "ERROR: The system cannot find the file specified."


class Crash(Exception):
    pass


class FakeWorld(hb.World):
    """Stands in for schtasks, wsl.exe and each repo's guard. A fresh one is like a new process with the same disk."""

    def __init__(self, repos=(), tasks=(), avail=8000, watch=None, running=("Ubuntu",), clock=1_000_000.0, crash_on=None, heartbeat="true"):
        self.repos, self.tasks, self.avail = set(repos), set(tasks), avail
        self.heartbeat, self.actions = heartbeat, {}
        self.watch, self.running, self.clock, self.crash_on = watch or {}, list(running), clock, crash_on
        self.calls, self.fail, self.check_fail = [], {}, {}

    def now(self):
        return self.clock

    def sleep(self, seconds):
        self.clock += seconds

    def available_mb(self):
        return self.avail

    def python(self):
        return "python"

    def is_dir(self, path):
        return str(Path(path)) in self.repos or any(str(Path(r)) == str(Path(path)) for r in self.repos)

    def is_file(self, path):
        return any(str(path) == str(Path(r) / "scripts" / "run-guard.py") for r in self.repos)

    def run(self, argv, cwd=None, timeout=120):
        self.calls.append(argv)
        if argv[0] == "wsl.exe":
            if "--list" in argv:
                return 0, "\n".join(self.running) + "\n"
            if "test" in argv:
                return (0, "") if argv[argv.index("--cd") + 1] in self.repos else (1, "")
            return self.answer(argv[argv.index("--cd") + 1], "watch")
        if argv[0] == "schtasks":
            name = argv[argv.index("/TN") + 1]
            if name == hb.TASK:
                if self.heartbeat is None:
                    return 1, CANT_FIND
                return 0, f"<Task><Settings><Enabled>{self.heartbeat}</Enabled></Settings><Actions><Exec><Command>pythonw.exe</Command><Arguments>heartbeat.py tick</Arguments></Exec></Actions></Task>"
            if name in self.check_fail:
                return 1, self.check_fail[name]
            if name not in self.tasks:
                return 1, CANT_FIND
            # A repo's own task names its folder: by default every fake repo, as they share one folder name here.
            return 0, f"<Arguments>{self.actions.get(name, ' '.join(sorted(self.repos)))}</Arguments>"
        return self.answer(cwd, argv[-1])

    def answer(self, repo, command):
        name = Path(repo).name + " run watchdog"
        if command == "uninstall":
            if self.crash_on == "uninstall":
                raise Crash
            if name in self.fail:
                return 1, self.fail[name]
            self.tasks.discard(name)
            return 0, "deleted"
        if command == "install":
            if self.crash_on == "install":
                raise Crash
            if name in self.fail:
                return 1, self.fail[name]
            self.tasks.add(name)
            return 0, "created"
        answer = self.watch.get(repo, "nothing to do")
        if isinstance(answer, tuple):
            return answer
        return 0, answer + "\n"

    def called(self, word):
        return [c for c in self.calls if word in c]


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        patcher = mock.patch.dict(os.environ, {"LOCALAPPDATA": self.tmp.name})
        patcher.start()
        self.addCleanup(patcher.stop)
        real = Path(self.tmp.name).resolve()  # Windows may hand out a short name, which add resolves
        self.a, self.b, self.c = (str(real / n / "repo") for n in ("a b", "c", "d"))
        self.a2 = str(real / "a b" / "." / "repo")

    def go(self, argv, world):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = hb.main(argv, world)
        self.err = err.getvalue()
        return code, out.getvalue()

    def world(self, **kwargs):
        kwargs.setdefault("repos", (self.a, self.b, self.c))
        return FakeWorld(**kwargs)

    def repos(self):
        return json.loads((Path(self.tmp.name) / "Bosun" / "heartbeat" / "repos.json").read_text())["repos"]

    def status(self, world=None):
        return json.loads(self.go(["status", "--json"], world or self.world())[1])

    def add_all(self, world=None):
        world = world or self.world()
        for repo in (self.a, self.b, self.c):
            self.assertEqual(self.go(["add", repo], world)[0], 0, self.err)
        return world


class Registry(Base):
    def test_missing_file_means_no_repos(self):
        world = self.world()
        self.assertEqual(self.go(["tick"], world)[0], 0)
        self.assertEqual(self.status()["repos"], [])
        self.assertIn("no repos", self.go(["list"], world)[1])

    def test_corrupt_file_ticks_nothing_and_says_so(self):
        path = Path(self.tmp.name) / "Bosun" / "heartbeat" / "repos.json"
        path.parent.mkdir(parents=True)
        for text in ("{not json", '{"repos": 3}', '{"repos": [1]}'):
            with self.subTest(text):
                path.write_text(text)
                world = self.world()
                self.assertEqual(self.go(["tick"], world)[0], 0)
                self.assertEqual(world.called("watch"), [])
                self.assertIn("repos.json", self.status()["error"])
                self.assertEqual(self.go(["list"], world)[0], 1)

    def test_add_refuses_no_guard_naming_the_fix(self):
        world = FakeWorld()
        Path(self.a).mkdir(parents=True)
        world.is_dir = lambda p: True
        code, _ = self.go(["add", self.a], world)
        self.assertEqual(code, 1)
        self.assertIn("run-guard.py", self.err)
        self.assertIn("Steward", self.err)

    def test_add_refuses_a_missing_folder(self):
        self.assertEqual(self.go(["add", self.a], FakeWorld())[0], 1)
        self.assertIn("no such folder", self.err)

    def test_add_refuses_a_duplicate_and_list_reads_back(self):
        world = self.world()
        self.assertEqual(self.go(["add", self.a], world)[0], 0)
        self.assertEqual(self.go(["add", self.a2], world)[0], 1)
        self.assertIn("already", self.err)
        data = json.loads(self.go(["list", "--json"], world)[1])
        self.assertEqual([(r["path"], r["state"]) for r in data["repos"]], [(self.a, "active")])
        self.assertIn(self.a, self.go(["list"], world)[1])

    def test_write_leaves_no_temporary(self):
        self.add_all()
        names = [p.name for p in (Path(self.tmp.name) / "Bosun" / "heartbeat").iterdir()]
        self.assertFalse([n for n in names if n.endswith(".tmp")], names)


class Handoff(Base):
    def task(self, repo):
        return Path(repo).name + " run watchdog"

    def test_add_removes_the_own_task_and_remove_restores_it(self):
        world = self.world(tasks={self.task(self.a)})
        self.assertEqual(self.go(["add", self.a], world)[0], 0)
        self.assertNotIn(self.task(self.a), world.tasks)
        self.assertEqual(self.go(["remove", self.a], world)[0], 0)
        self.assertIn(self.task(self.a), world.tasks)
        self.assertEqual(self.repos(), [])

    def test_a_repo_with_no_own_task_adds_cleanly(self):
        world = self.world()
        self.assertEqual(self.go(["add", self.a], world)[0], 0)
        self.assertEqual(world.called("uninstall"), [])

    def test_a_real_scheduler_error_refuses_and_keeps_the_old_state(self):
        world = self.world(tasks={self.task(self.a)})
        world.fail[self.task(self.a)] = "ERROR: Access is denied."
        self.assertEqual(self.go(["add", self.a], world)[0], 1)
        self.assertEqual(self.repos(), [])
        self.assertIn(self.task(self.a), world.tasks)

    def test_an_unreadable_own_task_refuses(self):
        world = self.world()
        world.check_fail[self.task(self.a)] = "ERROR: Access is denied."
        self.assertEqual(self.go(["add", self.a], world)[0], 1)
        self.assertIn("could not check", self.err)

    def test_add_refuses_unless_the_heartbeat_task_will_really_fire(self):
        for name, heartbeat in (("missing", None), ("disabled", "false")):
            with self.subTest(name):
                world = self.world(tasks={self.task(self.a)}, heartbeat=heartbeat)
                self.assertEqual(self.go(["add", self.a], world)[0], 1)
                self.assertIn(name, self.err)
                self.assertIn(self.task(self.a), world.tasks)  # its own watchdog stays
        world = self.world(heartbeat=None)
        self.assertEqual(self.go(["add", self.a, "--allow-no-task"], world)[0], 0)

    def test_another_clones_task_of_the_same_name_is_left_alone(self):
        world = self.world()
        self.go(["add", self.a], world)
        world.tasks.add(self.task(self.a))
        world.actions[self.task(self.a)] = r'"R:\\other\\repo\\scripts\\run-guard.py" watch'
        self.go(["tick"], world)
        self.assertIn(self.task(self.a), world.tasks)
        self.assertEqual(world.called("uninstall"), [])
        self.assertIsNone(self.status(world)["repos"][0]["note"])

    def test_a_failed_restore_keeps_the_repo_on_the_heartbeat(self):
        world = self.world()
        self.go(["add", self.a], world)
        world.fail[self.task(self.a)] = "ERROR: Access is denied."
        self.assertEqual(self.go(["remove", self.a], world)[0], 1)
        self.assertEqual(self.repos()[0]["state"], "active")

    def test_crash_during_add_is_finished_by_the_next_tick(self):
        world = self.world(tasks={self.task(self.a)}, crash_on="uninstall")
        with self.assertRaises(Crash):
            self.go(["add", self.a], world)
        self.assertEqual(self.repos()[0]["state"], "adding")
        self.assertIn(self.task(self.a), world.tasks)  # the old watchdog is still there, so it is never uncovered
        after = self.world(tasks=world.tasks)
        self.go(["tick"], after)
        self.assertEqual(self.repos()[0]["state"], "active")
        self.assertNotIn(self.task(self.a), after.tasks)
        self.assertEqual(len(after.called("watch")), 1)

    def test_an_adding_entry_with_a_failed_removal_stays_adding(self):
        after = self.world(tasks={self.task(self.a)})
        self.go(["add", self.a], self.world())
        repos = self.repos()
        repos[0]["state"] = "adding"
        hb.save_repos(repos)
        after.fail[self.task(self.a)] = "ERROR: Access is denied."
        self.go(["tick"], after)
        self.assertEqual(self.repos()[0]["state"], "adding")
        self.assertIn("could not", self.status(after)["repos"][0]["note"] or "")

    def test_crash_during_remove_is_finished_by_the_next_tick(self):
        world = self.world()
        self.go(["add", self.a], world)
        crashing = self.world(crash_on="install")
        with self.assertRaises(Crash):
            self.go(["remove", self.a], crashing)
        self.assertEqual(self.repos()[0]["state"], "removing")
        after = self.world()
        self.go(["tick"], after)
        self.assertEqual(self.repos(), [])
        self.assertIn(self.task(self.a), after.tasks)  # back on its own watchdog
        self.assertEqual(after.called("watch"), [])

    def test_an_own_task_that_comes_back_is_removed_with_a_note(self):
        world = self.world()
        self.go(["add", self.a], world)
        world.tasks.add(self.task(self.a))  # an older kit's prompt told a session to install it
        self.go(["tick"], world)
        self.assertNotIn(self.task(self.a), world.tasks)
        self.assertIn("had come back", self.status(world)["repos"][0]["note"])


class Passes(Base):
    def test_a_fake_guard_ticks_with_the_repo_as_working_directory(self):
        world = self.world(watch={self.a: "nothing to do"})
        self.go(["add", self.a], world)
        seen = []
        real = world.run

        def run(argv, cwd=None, timeout=120):
            seen.append((argv, cwd))
            return real(argv, cwd, timeout)
        world.run = run
        self.go(["tick"], world)
        argv, cwd = next(s for s in seen if s[0][-1] == "watch")
        self.assertEqual(cwd, self.a)  # a spaced path, whole
        self.assertEqual(argv, ["python", str(Path(self.a) / "scripts" / "run-guard.py"), "watch"])
        self.assertEqual(self.status(world)["repos"][0]["result"], "nothing to do")

    def test_a_hung_guard_is_recorded_and_the_next_repo_still_ticks(self):
        world = self.add_all(self.world(watch={self.a: (None, "stopped after 120 seconds")}))
        self.go(["tick"], world)
        repos = self.status(world)["repos"]
        self.assertIn("stopped after", repos[0]["error"])
        self.assertEqual(repos[1]["result"], "nothing to do")

    def test_a_failed_guard_is_recorded(self):
        world = self.add_all(self.world(watch={self.b: (1, "Traceback: boom")}))
        self.go(["tick"], world)
        self.assertIn("boom", self.status(world)["repos"][1]["error"])

    def test_a_missing_folder_is_recorded_without_stopping(self):
        world = self.add_all()
        world.repos.discard(self.a)
        self.go(["tick"], world)
        repos = self.status(world)["repos"]
        self.assertEqual(repos[0]["error"], "folder is missing")
        self.assertEqual(repos[1]["result"], "nothing to do")

    def resuming(self):
        return {r: "resumed, attempt 1" for r in (self.a, self.b, self.c)}

    def test_one_resume_per_pass_in_turn_across_processes(self):
        self.add_all()
        order = []
        for _ in range(4):
            world = self.world(watch=self.resuming())  # a new process each time
            self.go(["tick"], world)
            watched = world.called("watch")
            self.assertEqual(len(watched), 1)
            order.append(Path(watched[0][1]).parents[2].name)
        self.assertEqual(order, ["a b", "c", "d", "a b"])

    def test_a_live_child_does_not_use_the_pass(self):
        self.add_all()
        world = self.world(watch={self.a: "resumed session still running", self.b: "resumed, attempt 1"})
        self.go(["tick"], world)
        self.assertEqual(len(world.called("watch")), 2)
        self.assertEqual(len(self.status(world)["repos"]), 3)

    def test_a_pass_that_resumes_nothing_ticks_everyone(self):
        world = self.add_all()
        self.go(["tick"], world)
        self.assertEqual(len(world.called("watch")), 3)


class Hold(Base):
    def test_low_memory_holds_with_a_reason_across_processes_then_ticks_anyway(self):
        self.add_all()
        start = 1_000_000.0
        first = self.world(avail=1000, clock=start)
        self.go(["tick"], first)
        self.assertEqual(first.called("watch"), [])
        self.assertIn("1000 MB available", self.status(first)["hold"])
        later = self.world(avail=1000, clock=start + 3000)
        self.go(["tick"], later)
        self.assertEqual(later.called("watch"), [])
        past = self.world(avail=1000, clock=start + 3601)
        self.go(["tick"], past)
        self.assertEqual(len(past.called("watch")), 3)
        self.assertIsNone(self.status(past)["hold"])
        again = self.world(avail=1000, clock=start + 3700)
        self.go(["tick"], again)
        self.assertEqual(again.called("watch"), [])  # a new hold, fresh limit

    def test_recovered_memory_ends_the_hold(self):
        self.add_all()
        self.go(["tick"], self.world(avail=1000))
        world = self.world(avail=9000)
        self.go(["tick"], world)
        self.assertEqual(len(world.called("watch")), 3)

    def config(self, text):
        path = Path(self.tmp.name) / "Bosun" / "config.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def test_the_floor_comes_from_config(self):
        self.add_all()
        self.config('{"heartbeat_floor_mb": 500}')
        world = self.world(avail=1000)
        self.go(["tick"], world)
        self.assertEqual(len(world.called("watch")), 3)
        self.config('{"heartbeat_floor_mb": 5000}')
        world = self.world(avail=4000)
        self.go(["tick"], world)
        self.assertEqual(world.called("watch"), [])

    def test_a_malformed_config_uses_the_default_with_a_warning(self):
        self.add_all()
        for text in ("{bad", '{"heartbeat_floor_mb": "lots"}', '{"heartbeat_floor_mb": -1}', "[1]"):
            with self.subTest(text):
                self.config(text)
                world = self.world(avail=8000)
                self.go(["tick"], world)
                self.assertEqual(len(world.called("watch")), 3)
                self.assertIn("2048", self.status(world)["warning"])


class Wsl(Base):
    PATH = "/home/u/www/site"

    def world(self, **kwargs):
        return FakeWorld(repos=(self.PATH,), **kwargs)

    def test_a_stopped_distro_stays_stopped(self):
        world = self.world()
        self.assertEqual(self.go(["add", self.PATH, "--distro", "Ubuntu"], world)[0], 0)
        stopped = self.world(running=())
        self.go(["tick"], stopped)
        self.assertEqual(stopped.called("python3"), [])
        self.assertEqual(stopped.called("--cd"), [])
        self.assertEqual(self.status(stopped)["repos"][0]["result"], "WSL not running")

    def test_a_running_distro_ticks_inside_it(self):
        world = self.world()
        self.go(["add", self.PATH, "--distro", "Ubuntu"], world)
        self.go(["tick"], world)
        call = world.called("python3")[0]
        self.assertEqual(call, ["wsl.exe", "-d", "Ubuntu", "--cd", self.PATH, "--", "python3", "scripts/run-guard.py", "watch"])

    def test_a_distro_stopping_mid_pass_is_not_restarted(self):
        path = str(Path(self.tmp.name).resolve() / "win")
        world = FakeWorld(repos=(self.PATH, path))
        self.go(["add", path], world)
        self.go(["add", self.PATH, "--distro", "Ubuntu"], world)
        real = world.answer

        def answer(repo, command):
            if command == "watch" and repo == path:
                world.running = []  # stopped while this guard ran
            return real(repo, command)
        world.answer = answer
        self.go(["tick"], world)
        self.assertEqual(world.called("python3"), [])
        self.assertEqual(self.status(world)["repos"][1]["result"], "WSL not running")

    def test_add_refuses_while_stopped_without_waking_it(self):
        world = self.world(running=())
        self.assertEqual(self.go(["add", self.PATH, "--distro", "Ubuntu"], world)[0], 1)
        self.assertIn("isn't running", self.err)
        self.assertEqual(world.called("test"), [])

    def test_add_refuses_no_guard(self):
        self.assertEqual(self.go(["add", "/home/u/none", "--distro", "Ubuntu"], self.world())[0], 1)
        self.assertIn("run-guard.py", self.err)

    def test_no_task_handoff_for_wsl(self):
        world = self.world()
        self.go(["add", self.PATH, "--distro", "Ubuntu"], world)
        self.assertEqual(self.go(["remove", self.PATH, "--distro", "Ubuntu"], world)[0], 0)
        self.assertEqual([c for c in world.calls if c[0] == "schtasks" and hb.TASK not in c], [])


class Status(Base):
    def test_empty_never_ticked_and_stale(self):
        data = self.status()
        self.assertEqual((data["schema_version"], data["repos"], data["stale"], data["last_pass"]), (1, [], True, None))
        world = self.world()
        self.go(["add", self.a], world)
        self.assertIsNone(self.status(world)["repos"][0]["last_tick"])
        self.assertIn("never ticked", self.go(["status"], world)[1])
        self.go(["tick"], world)
        self.assertFalse(self.status(world)["stale"])
        world.clock += hb.STALE_SECONDS + 1
        self.assertTrue(self.status(world)["stale"])
        self.assertIn("(stale)", self.go(["status"], world)[1])

    def test_unreadable_status_file_reads_as_stale(self):
        world = self.world()
        self.go(["tick"], world)
        (Path(self.tmp.name) / "Bosun" / "heartbeat" / "status.json").write_text("{bad")
        data = self.status(world)
        self.assertTrue(data["stale"])
        self.assertIsNone(data["last_pass"])

    def test_task_health(self):
        world = self.world(heartbeat=None)
        self.assertEqual(self.status(world)["task"]["state"], "missing")
        world.heartbeat = "true"
        task = self.status(world)["task"]
        self.assertEqual((task["state"], task["action"]), ("installed", "pythonw.exe heartbeat.py tick"))
        world.heartbeat = "false"
        self.assertEqual(self.status(world)["task"]["state"], "disabled")
        world.fail = {}
        world.run = lambda argv, cwd=None, timeout=120: (1, "ERROR: Access is denied.")
        self.assertEqual(self.status(world)["task"]["state"], "unreadable")


class RealProcesses(Base):
    """What fakes can't prove: the operating system's lock and a real child process."""

    def test_a_second_process_reports_busy_and_changes_nothing(self):
        world = self.world()
        script = (f"import os, sys, time\nsys.path.insert(0, {str(ROOT / 'scripts')!r})\nimport heartbeat as hb\n"
                  "with hb.Lock(hb.World()):\n    print('held', flush=True)\n    time.sleep(30)\n")
        holder = subprocess.Popen([sys.executable, "-c", script], stdout=subprocess.PIPE, text=True,
                                  env={**os.environ, "LOCALAPPDATA": self.tmp.name})
        self.addCleanup(holder.kill)
        self.assertEqual(holder.stdout.readline().strip(), "held")
        code, out = self.go(["tick"], world)
        self.assertEqual(code, 0)
        self.assertIn("busy", out)
        self.assertFalse((Path(self.tmp.name) / "Bosun" / "heartbeat" / "status.json").exists())
        with mock.patch.object(hb, "LOCK_WAIT_SECONDS", 5):
            self.assertEqual(self.go(["add", self.a], world)[0], 1)
        self.assertIn("another heartbeat", self.err)
        holder.kill()  # the operating system releases the lock of a dead process
        holder.wait()
        self.assertEqual(self.go(["add", self.a], world)[0], 0)

    def test_real_run_captures_output_and_stops_a_hung_child(self):
        world = hb.World()
        code, out = world.run([sys.executable, "-c", "print('resumed, attempt 2')"])
        self.assertEqual((code, out.strip()), (0, "resumed, attempt 2"))
        code, out = world.run([sys.executable, "-c", "import time; time.sleep(30)"], timeout=1)
        self.assertIsNone(code)
        self.assertIn("stopped after", out)

    def test_real_run_does_not_wait_for_a_child_the_guard_leaves_behind(self):
        script = ("import subprocess, sys\n"
                  "subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(20)'], stdin=subprocess.DEVNULL, stdout=sys.stdout.fileno())\n"
                  "print('resumed, attempt 1', flush=True)\n")
        code, out = hb.World().run([sys.executable, "-c", script], timeout=15)
        self.assertEqual((code, out.strip()), (0, "resumed, attempt 1"))

    def test_real_memory_reading(self):
        if os.name != "nt":
            self.skipTest("a Windows call")
        self.assertGreater(hb.World().available_mb(), 0)


if __name__ == "__main__":
    unittest.main()
