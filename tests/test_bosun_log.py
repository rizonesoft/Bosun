import contextlib
import datetime
import io
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "wsl"))
import bosun_log as dl


class RepoForDir(unittest.TestCase):
    def test_walks_up_to_nearest_git(self):
        gits = {"/home/u/projects/app"}
        self.assertEqual(dl.repo_for_dir("/home/u/projects/app/src/x", gits.__contains__, {}), "/home/u/projects/app")

    def test_same_named_repos_stay_apart(self):
        gits = {"/home/u/a/app", "/home/u/b/app"}
        self.assertNotEqual(
            dl.repo_for_dir("/home/u/a/app/src", gits.__contains__, {}),
            dl.repo_for_dir("/home/u/b/app/src", gits.__contains__, {}),
        )

    def test_windows_mounted_repo_is_attributed(self):
        gits = {"/mnt/d/GitHub/Bosun"}
        self.assertEqual(dl.repo_for_dir("/mnt/d/GitHub/Bosun/src", gits.__contains__, {}), "/mnt/d/GitHub/Bosun")

    def test_no_repo_is_empty(self):
        self.assertEqual(dl.repo_for_dir("/home/u", set().__contains__, {}), "")


class Attribute(unittest.TestCase):
    def test_child_inherits_nearest_ancestor(self):
        procs = {1: ("init", 0, 0, 0), 10: ("claude", 1, 0, 0), 11: ("node", 10, 0, 0)}
        result = dl.attribute(procs, {10: "app", 11: ""})
        self.assertEqual(result, {1: "", 10: "app", 11: "app"})

    def test_own_repo_beats_parent(self):
        procs = {10: ("a", 0, 0, 0), 11: ("b", 10, 0, 0)}
        self.assertEqual(dl.attribute(procs, {10: "x", 11: "y"})[11], "y")

    def test_parent_cycle_terminates(self):
        procs = {10: ("a", 11, 0, 0), 11: ("b", 10, 0, 0)}
        self.assertEqual(dl.attribute(procs, {})[10], "")


class CsvName(unittest.TestCase):
    def test_encodes_separators(self):
        self.assertEqual(dl.csv_name('a,b"c%'), "a%2Cb%22c%25")
        self.assertNotEqual(dl.csv_name("a,b"), dl.csv_name("a_b"))


class Utf8Text(unittest.TestCase):
    def test_a_path_that_is_not_utf8_becomes_writable_text(self):
        raw = b"/home/u/caf\xe9".decode("utf-8", "surrogateescape")  # as os.readlink gives a Latin-1 named folder
        text = dl.utf8_text(raw)
        self.assertEqual(text, "/home/u/caf�")
        text.encode("utf-8")  # a UTF-8 write takes it
        self.assertEqual(dl.utf8_text("/home/u/café"), "/home/u/café")


class Snapshot(unittest.TestCase):
    """current-wsl.json (D01 T01 §4): the Windows schema, written whole or not at all."""

    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="bosun-snap-")
        self.addCleanup(shutil.rmtree, self.dir, True)

    def path(self):
        return os.path.join(self.dir, "current-wsl.json")

    def leftovers(self):
        return [n for n in os.listdir(self.dir) if n != "current-wsl.json"]

    def test_every_field_from_a_fixed_sample(self):
        memory = {"available_mb": 9000, "total_mb": 14000, "swap_used_mb": 12, "psi_full_avg10_x100": None, "oom_kills": 3}
        repos = [("/home/u/a\"b", (3, 2048 * 1024, 1024 * 1024))]
        procs = [("cc1plus", (4, 3072 * 1024, 2048 * 1024)), ("make", (1, 1024, 2048))]
        doc = json.loads(dl.snapshot_json("2026-11-13T09:00:05Z", 7, memory, repos, procs))
        self.assertEqual(doc["schema_version"], 1)
        self.assertEqual((doc["side"], doc["at"], doc["interval_s"]), ("wsl", "2026-11-13T09:00:05Z", 7))
        self.assertEqual(doc["memory"], memory)
        self.assertIsNone(doc["wsl_vm"])
        self.assertEqual(doc["repos"], [{"repo": "/home/u/a\"b", "processes": 3, "private_mb": 2048, "rss_mb": 1024}])
        self.assertEqual(doc["processes"], [{"name": "cc1plus", "processes": 4, "private_mb": 3072, "rss_mb": 2048},
                                            {"name": "make", "processes": 1, "private_mb": 1, "rss_mb": 2}])
        self.assertIs(doc["history_written"], True)

    def test_a_history_that_cant_be_written_still_gives_a_snapshot_and_sampling_goes_on(self):
        csv = os.path.join(self.dir, "wsl-" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d") + ".csv")
        os.mkdir(csv)  # a folder where the day's CSV would go: opening it to append fails, as a full disk or denied file does
        with contextlib.redirect_stderr(io.StringIO()) as err:
            dl.sample(self.dir, {}, interval=5)  # returns, never raises out of the logger's loop
        self.assertIn("history is being lost", err.getvalue())
        with open(self.path(), encoding="utf-8") as f:
            doc = json.load(f)
        self.assertIs(doc["history_written"], False)
        self.assertGreater(doc["memory"]["total_mb"], 0)
        os.rmdir(csv)
        dl.sample(self.dir, {}, interval=5)  # recovered: the next sample writes both
        self.assertTrue(os.path.isfile(csv))
        with open(self.path(), encoding="utf-8") as f:
            self.assertIs(json.load(f)["history_written"], True)

    def test_lands_only_through_a_replace_from_the_same_folder(self):
        calls = []

        def replace(src, dst):
            calls.append((src, dst))
            os.replace(src, dst)

        self.assertTrue(dl.write_snapshot(self.dir, '{"v":1}', replace=replace))
        self.assertEqual(len(calls), 1)
        self.assertEqual(os.path.dirname(calls[0][0]), self.dir)
        self.assertEqual(calls[0][1], self.path())
        with open(self.path(), encoding="utf-8") as f:
            self.assertEqual(f.read(), '{"v":1}')
        self.assertEqual(self.leftovers(), [])

    def test_a_reader_holding_the_old_file_still_reads_the_old_one(self):
        dl.write_snapshot(self.dir, '{"old":1}')
        with open(self.path(), encoding="utf-8") as held:
            dl.write_snapshot(self.dir, '{"new":2}')
            self.assertEqual(held.read(), '{"old":1}')  # a write in place would show the new bytes here
        with open(self.path(), encoding="utf-8") as f:
            self.assertEqual(f.read(), '{"new":2}')

    def test_a_failed_replace_keeps_the_old_file_and_the_next_one_lands(self):
        dl.write_snapshot(self.dir, '{"old":1}')

        def refuse(src, dst):
            raise PermissionError(13, "held by a Windows reader")

        with contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertFalse(dl.write_snapshot(self.dir, '{"new":2}', replace=refuse, attempts=2))
        self.assertIn("can't replace the live snapshot", err.getvalue())
        with open(self.path(), encoding="utf-8") as f:
            self.assertEqual(f.read(), '{"old":1}')
        self.assertEqual(self.leftovers(), [])
        self.assertTrue(dl.write_snapshot(self.dir, '{"next":3}'))
        with open(self.path(), encoding="utf-8") as f:
            self.assertEqual(f.read(), '{"next":3}')

    def test_a_temporary_file_that_cant_be_made_keeps_the_old_one(self):
        dl.write_snapshot(self.dir, '{"old":1}')
        saved = dl.tempfile.mkstemp

        def full(*args, **kwargs):
            raise OSError(28, "No space left on device")

        dl.tempfile.mkstemp = full
        self.addCleanup(setattr, dl.tempfile, "mkstemp", saved)
        with contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertFalse(dl.write_snapshot(self.dir, '{"new":2}'))  # returns, never raises out of sample()
        self.assertIn("No space left", err.getvalue())
        with open(self.path(), encoding="utf-8") as f:
            self.assertEqual(f.read(), '{"old":1}')
        self.assertEqual(self.leftovers(), [])

    def test_a_sample_without_psi_has_no_psi_row_and_a_null(self):
        saved = dl.psi_full_avg10_x100
        dl.psi_full_avg10_x100 = lambda: None
        self.addCleanup(setattr, dl, "psi_full_avg10_x100", saved)
        dl.sample(self.dir, {}, interval=9)
        csv = [n for n in os.listdir(self.dir) if n.endswith(".csv")]
        self.assertEqual(len(csv), 1)
        with open(os.path.join(self.dir, csv[0]), encoding="utf-8") as f:
            rows = f.read().splitlines()
        self.assertFalse([r for r in rows if ",psi_full_avg10_x100," in r])
        with open(self.path(), encoding="utf-8") as f:
            doc = json.load(f)
        self.assertIsNone(doc["memory"]["psi_full_avg10_x100"])
        self.assertEqual(doc["interval_s"], 9)
        # The snapshot holds the same figures as the sample's own rows.
        avail = [r for r in rows if ",mem_available_mb," in r][0].split(",")
        self.assertEqual(doc["at"], avail[0] + "Z")
        self.assertEqual(doc["memory"]["available_mb"], int(avail[5]))
        for r in rows:
            f = r.split(",")
            if len(f) == 7 and f[2] == "repo":
                match = [g for g in doc["repos"] if dl.csv_name(g["repo"]) == f[3]]
                self.assertEqual(len(match), 1)
                self.assertEqual((match[0]["processes"], match[0]["private_mb"], match[0]["rss_mb"]), (int(f[4]), int(f[5]), int(f[6])))
        procs = [r.split(",") for r in rows if ",proc," in r]
        self.assertEqual([dl.csv_name(p["name"]) for p in doc["processes"]], [p[3] for p in procs])


class Instances(unittest.TestCase):
    """The WSL instance scanner (D03 T02 §1) over a fixture /proc: the rules of src/core.hpp, from Linux's own files."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.proc = os.path.join(self.root, "proc")
        self.home = os.path.join(self.root, "home")
        os.makedirs(self.proc)
        os.makedirs(os.path.join(self.home, ".claude", "sessions"))
        with open(os.path.join(self.proc, "stat"), "w") as f:
            f.write("cpu 1 2 3\nbtime 1700000000\n")
        self.repo = os.path.join(self.root, "app")
        os.makedirs(os.path.join(self.repo, ".git"))
        os.makedirs(os.path.join(self.repo, "src"))
        self.outside = os.path.join(self.root, "loose")
        os.makedirs(self.outside)

    def tearDown(self):
        shutil.rmtree(self.root)

    def process(self, pid, name, ppid, rss_kb, start, cwd=None, argv=None):
        d = os.path.join(self.proc, str(pid))
        os.makedirs(d)
        with open(os.path.join(d, "status"), "w") as f:
            f.write(f"Name:\t{name}\nPPid:\t{ppid}\nVmRSS:\t{rss_kb + 10} kB\nRssAnon:\t{rss_kb} kB\n")
        fields = ["S", str(ppid)] + ["0"] * 17 + [str(start)] + ["0"] * 10
        with open(os.path.join(d, "stat"), "w") as f:
            f.write(f"{pid} ({name}) " + " ".join(fields) + "\n")
        with open(os.path.join(d, "cmdline"), "wb") as f:
            f.write(b"\0".join(a.encode() for a in (argv or [name])) + b"\0")
        if cwd:
            os.symlink(cwd, os.path.join(d, "cwd"))

    def session(self, pid, start, status):
        with open(os.path.join(self.home, ".claude", "sessions", f"{pid}.json"), "w") as f:
            json.dump({"pid": pid, "procStart": str(start), "peerFeatures": ["x"], "status": status}, f)

    def mark(self, pid, deadline, reviewer="astra"):
        folder = os.path.join(self.repo, "build", "review-in-flight")
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, f"{pid}-{reviewer}.json"), "w") as f:
            json.dump({"pid": pid, "reviewer": reviewer, "started": "x", "deadline": deadline}, f)

    def scan(self, now=1700001000.0):
        return dl.instances(proc=self.proc, home=self.home, now=now, distro="Ubuntu", ticks=100)

    def rows(self, doc):
        return {r["pid"]: r for r in doc["instances"]}

    def test_agents_jobs_folders_memory_and_starts(self):
        self.process(1, "init", 0, 1024, 1)
        self.process(10, "claude", 1, 300 * 1024, 5000, cwd=os.path.join(self.repo, "src"))
        self.process(11, "claude", 10, 40 * 1024, 5100)        # a claude under a claude is the same one
        self.process(12, "node", 10, 60 * 1024, 5200)          # its tool, counted in it
        # A job the claude started through the shim: its own row, its tree never counted in the claude.
        self.process(20, "bosun", 10, 2 * 1024, 6000, cwd=self.repo, argv=["/bin/bash", "/home/u/bin/bosun", "run", "--slots", "2", "--", "make", "-j8", "a b"])
        self.process(21, "make", 20, 900 * 1024, 6100)
        # A shim inside that job's slot passes through: not a second job.
        self.process(22, "bosun", 21, 1024, 6200, cwd=self.repo, argv=["bash", "/x/wsl/bosun", "run", "--", "true"])
        self.process(30, "codex", 1, 8 * 1024, 7000, cwd=self.outside)
        self.process(40, "code", 1, 50 * 1024, 7100)           # its folder can't be read
        doc = self.scan()
        self.assertEqual((doc["schema_version"], doc["side"], doc["distro"], doc["problems"]), (1, "wsl", "Ubuntu", []))
        rows = self.rows(doc)
        self.assertEqual(sorted(rows), [10, 20, 30, 40])
        claude, job, codex, code = rows[10], rows[20], rows[30], rows[40]
        self.assertEqual((claude["kind"], claude["repo"], claude["cwd"], claude["private_mb"]), ("claude", self.repo, os.path.join(self.repo, "src"), 400))
        self.assertEqual(claude["started_at"], "2023-11-14T22:14:10Z")  # boot at 1700000000, plus 5000 ticks of 100 a second
        self.assertEqual((job["kind"], job["name"], job["command"], job["private_mb"], job["repo"]), ("job", "make", "make -j8 'a b'", 2 + 900 + 1, self.repo))
        self.assertEqual((codex["repo"], codex["cwd"]), (None, self.outside))
        self.assertEqual((code["cwd"], code["repo"], doc["unreadable"]), (None, None, 1))
        self.assertTrue(all(r["state"] == "unknown" and r["evidence"] is None for r in rows.values()))  # no evidence at all
        self.assertEqual(list(claude), ["side", "kind", "name", "pid", "cwd", "repo", "private_mb", "started_at", "state", "evidence", "slot", "command", "guest_pid", "guest_distro"])

    def test_state_from_session_records_and_review_marks(self):
        self.process(1, "init", 0, 1024, 1)
        self.process(10, "claude", 1, 1024, 5000, cwd=self.repo)
        self.process(11, "claude", 1, 1024, 5001, cwd=self.repo)
        self.process(12, "claude", 1, 1024, 5002, cwd=self.repo)
        self.process(50, "python3", 1, 1024, 5003, cwd=self.repo)   # the dispatcher
        self.process(51, "codex", 50, 1024, 5004, cwd=self.repo)
        self.process(60, "python3", 1, 1024, 5005, cwd=self.repo)   # a dispatcher whose deadline has passed
        self.process(61, "codex", 60, 1024, 5006, cwd=self.repo)
        self.process(70, "python3", 1, 1024, 5007, cwd=self.repo)   # a live dispatcher over a busy claude
        self.process(71, "claude", 70, 1024, 5008, cwd=self.repo)
        self.session(10, 5000, "busy")
        self.session(11, 9999, "busy")      # a reused pid: the record names a process that started at another time
        self.session(12, 5002, "waiting")
        self.session(71, 5008, "busy")
        self.mark(50, 1700002000.0)
        self.mark(60, 1700000500.0)
        self.mark(70, 1700002000.0, reviewer="carbon")
        with open(os.path.join(self.home, ".claude", "sessions", "broken.json"), "w") as f:
            f.write("{not json")
        doc = self.scan()
        rows = self.rows(doc)
        self.assertEqual((rows[10]["state"], rows[10]["evidence"]), ("working", "Claude Code says busy (session record)"))
        self.assertEqual(rows[11]["state"], "unknown")
        self.assertIn("started at another time", rows[11]["evidence"])
        self.assertEqual((rows[12]["state"], rows[12]["evidence"]), ("unknown", "Claude Code says waiting (session record)"))
        self.assertEqual((rows[51]["state"], rows[51]["evidence"]), ("reviewing", "runs under astra's review (dispatcher pid 50)"))
        self.assertEqual((rows[61]["state"], rows[61]["evidence"]), ("unknown", None))
        self.assertEqual(rows[71]["state"], "reviewing")  # a live mark over a busy session: the review wins
        self.assertEqual(doc["problems"], [{"source": "sessions", "why": os.path.join(self.home, ".claude", "sessions", "broken.json") + " is not a session record"}])
        self.assertEqual(len(doc["instances"]), 6)  # every instance is still listed beside the problem

    def test_a_mark_past_a_float_s_range_is_a_problem_not_the_end_of_the_scan(self):
        # Round 1: a deadline of 400 nines stopped `bosun instances` with OverflowError.
        self.process(1, "init", 0, 1024, 1)
        self.process(50, "python3", 1, 1024, 5000, cwd=self.repo)
        self.process(51, "codex", 50, 1024, 5001, cwd=self.repo)
        folder = os.path.join(self.repo, "build", "review-in-flight")
        os.makedirs(folder)
        with open(os.path.join(folder, "bad.json"), "w") as f:
            f.write('{"pid":50,"deadline":' + "9" * 400 + "}")
        doc = self.scan()
        self.assertEqual(self.rows(doc)[51]["state"], "unknown")
        self.assertEqual([p["source"] for p in doc["problems"]], ["review-marks"])
        self.assertIn("bad.json is not a review mark", doc["problems"][0]["why"])

    def test_each_distro_writes_its_own_file_beside_the_snapshot(self):
        # Round 1: the panel reads this file, never wsl.exe, which would start a distro the person had stopped.
        self.process(10, "claude", 0, 1024, 5000, cwd=self.repo)
        logs = os.path.join(self.root, "logs")
        os.makedirs(logs)
        for distro in ("Ubuntu", "Debian", "no/such"):
            self.assertTrue(dl.write_instances(logs, proc=self.proc, home=self.home, now=1700001000.0, distro=distro, ticks=100))
        self.assertEqual(sorted(os.listdir(logs)), ["instances-wsl-Debian.json", "instances-wsl-Ubuntu.json", "instances-wsl-default.json"])
        with open(os.path.join(logs, "instances-wsl-Ubuntu.json"), encoding="utf-8") as f:
            doc = json.load(f)
        self.assertEqual((doc["distro"], doc["instances"][0]["pid"]), ("Ubuntu", 10))

    def test_a_folder_that_cant_be_listed_is_a_problem(self):
        self.process(10, "claude", 0, 1024, 5000, cwd=self.repo)
        sessions = os.path.join(self.home, ".claude", "sessions")
        shutil.rmtree(sessions)
        with open(sessions, "w") as f:
            f.write("a file where the folder should be")
        doc = self.scan()
        self.assertEqual([p["source"] for p in doc["problems"]], ["sessions"])
        self.assertEqual(len(doc["instances"]), 1)

    def test_the_text_and_json_forms(self):
        self.process(10, "claude", 0, 2048, 5000, cwd=self.repo)
        doc = self.scan()
        self.assertEqual(dl.instances_text(doc), f"claude 10 {self.repo} unknown 2 MB\n")
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            dl.main(["bosun_log.py", "instances", "--json"])
        self.assertEqual(json.loads(out.getvalue())["side"], "wsl")  # the command's own output parses as the shape


if __name__ == "__main__":
    unittest.main()
