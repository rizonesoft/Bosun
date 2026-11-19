import contextlib
import datetime
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
import uuid
import unittest.mock

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
        self.assertIsNone(job["started_at"])  # its command's start is its holder's record's to say, never the shim's (round 5)
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

    def test_a_process_that_cant_be_read_is_said_never_an_empty_scan(self):
        # Completion review: a denied /proc read was taken for a process that went, so a scan of nothing looked clean.
        self.process(10, "claude", 0, 1024, 5000, cwd=self.repo)
        self.process(20, "codex", 0, 1024, 5001, cwd=self.repo)
        real = dl.read_proc

        def denied(proc, pid):
            if pid == 20:
                raise dl.Unreadable("Permission denied")
            return real(proc, pid)

        dl.read_proc = denied
        try:
            doc = self.scan()
        finally:
            dl.read_proc = real
        self.assertEqual([r["pid"] for r in doc["instances"]], [10])
        self.assertEqual((doc["unreadable"], [p["source"] for p in doc["problems"]]), (1, ["processes"]))
        self.assertIn("1 of the processes", doc["problems"][0]["why"])

    def test_a_process_that_went_while_read_is_skipped_quietly(self):
        self.process(10, "claude", 0, 1024, 5000, cwd=self.repo)
        os.remove(os.path.join(self.proc, "10", "stat"))  # gone between the listing and the read
        doc = self.scan()
        self.assertEqual((doc["instances"], doc["unreadable"], doc["problems"]), ([], 0, []))

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


# --- Pressure throttle (D05 T02 §5) -----------------------------------------------------------------------------------

GB = 1024 * 1024 * 1024
NOW = datetime.datetime(2026, 11, 19, 12, 0, 0, tzinfo=datetime.timezone.utc)
FIXTURE = os.path.join(os.path.dirname(__file__), "fixtures", "pressure-policy.json")


class FakeSlice:
    """A stand-in for `systemctl --user`: one slice's MemoryHigh, with each way it can fail."""

    def __init__(self, value=dl.INFINITY):
        self.value = value
        self.initial = str(value)
        self.calls = []
        self.fail_show = False
        self.refuse_set = False
        self.ignore_set = False   # accepts the write and changes nothing
        self.set_to = None        # accepts a write that lowers and sets something else (putting the original back still works)
        self.on_show = {}         # {n: value}: the nth show (from 1) finds the slice already set to value, as an operator would

    def __call__(self, args):
        self.calls.append(list(args))
        if args[0] == "show":
            shows = sum(1 for c in self.calls if c[0] == "show")
            if shows in self.on_show:
                self.value = self.on_show[shows]
            return (1, "Failed to connect to bus: No medium found") if self.fail_show else (0, str(self.value))
        if self.refuse_set:
            return 1, "Access denied"
        wanted = args[3].split("=", 1)[1]
        if self.set_to is not None and wanted != self.initial:
            wanted = self.set_to
        if not self.ignore_set:
            self.value = wanted if wanted == dl.INFINITY else int(wanted)
        return 0, ""

    def sets(self):
        return [c for c in self.calls if c[0] == "set-property"]


class PolicyReading(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="bosun-policy-")
        self.addCleanup(shutil.rmtree, self.dir, True)

    def put(self, text):
        with open(os.path.join(self.dir, dl.POLICY_FILE), "w", encoding="utf-8") as f:
            f.write(text)

    def test_the_file_the_panels_save_path_writes_reads_with_its_names_and_units(self):
        shutil.copy(FIXTURE, os.path.join(self.dir, dl.POLICY_FILE))  # tests/fixtures/pressure-policy.json, checked by PressurePolicyTests
        self.assertEqual(dl.read_policy(self.dir), ({"enabled": True, "floor_mb": 3072, "recovery_band_mb": 1024, "job_limit_mb": 6144}, None))

    def test_no_file_is_no_policy_and_no_reason(self):
        self.assertEqual(dl.read_policy(self.dir), (None, None))

    def test_a_file_that_is_not_a_policy_is_no_policy_with_why(self):
        good = {"schema_version": 1, "enabled": True, "floor_mb": 2048, "recovery_band_mb": 1024, "job_limit_mb": 4096}
        for change, word in (({"schema_version": 2}, "version"), ({"enabled": "yes"}, "enabled"), ({"floor_mb": 1.5}, "floor_mb"),
                             ({"floor_mb": 0}, "floor_mb"), ({"recovery_band_mb": -1}, "recovery_band_mb"),
                             ({"job_limit_mb": 1023}, "least allowed"), ({"job_limit_mb": True}, "job_limit_mb")):
            self.put(json.dumps({**good, **change}))
            policy, why = dl.read_policy(self.dir)
            self.assertIsNone(policy, change)
            self.assertIn(word, why)
        self.put("not json")
        self.assertIn("not JSON", dl.read_policy(self.dir)[1])
        self.put("[]")
        self.assertIn("not a JSON object", dl.read_policy(self.dir)[1])


class HostReading(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="bosun-host-")
        self.addCleanup(shutil.rmtree, self.dir, True)

    def put(self, available=3000, total=32000, age=0, **change):
        at = (NOW - datetime.timedelta(seconds=age)).strftime("%Y-%m-%dT%H:%M:%SZ")
        doc = {"schema_version": 1, "side": "windows", "at": at, "memory": {"available_mb": available, "total_mb": total}}
        doc.update(change)
        with open(os.path.join(self.dir, "current-windows.json"), "w", encoding="utf-8") as f:
            json.dump(doc, f)

    def test_a_fresh_windows_snapshot_gives_windows_available_memory(self):
        self.put(available=1234)
        self.assertEqual(dl.read_host(self.dir, NOW), (1234, None))
        self.put(available=1234, age=90)   # exactly three intervals old is still fresh
        self.assertEqual(dl.read_host(self.dir, NOW)[0], 1234)

    def test_a_stale_missing_or_invalid_sample_gives_nothing_and_says_why(self):
        self.put(age=91)
        self.assertEqual(dl.read_host(self.dir, NOW), (None, "the Windows snapshot is stale"))
        os.remove(os.path.join(self.dir, "current-windows.json"))
        self.assertEqual(dl.read_host(self.dir, NOW)[0], None)
        self.assertIn("no Windows snapshot", dl.read_host(self.dir, NOW)[1])
        for change in ({"side": "wsl"}, {"schema_version": 2}, {"schema_version": True}, {"at": "yesterday"}, {"at": None}, {"memory": None},
                       {"memory": {"available_mb": None, "total_mb": 1}}, {"memory": {"available_mb": -1, "total_mb": 5}},
                       {"memory": {"available_mb": 9000, "total_mb": 8000}}, {"memory": {"available_mb": True, "total_mb": 8000}}):
            self.put(**change)
            self.assertIsNone(dl.read_host(self.dir, NOW)[0], change)
        with open(os.path.join(self.dir, "current-windows.json"), "w") as f:
            f.write("{broken")
        self.assertIn("not JSON", dl.read_host(self.dir, NOW)[1])

    def test_a_clock_behind_the_snapshot_is_not_stale(self):
        self.put(age=-30)
        self.assertEqual(dl.read_host(self.dir, NOW)[0], 3000)


class ThrottleCase(unittest.TestCase):
    """The throttle through its real entry (run_once) on files in a folder, with a stand-in for systemctl."""

    LIMIT = 2 * GB   # the policy's job limit, 2048 MB

    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="bosun-throttle-")
        self.addCleanup(shutil.rmtree, self.dir, True)
        patch = unittest.mock.patch.object(dl, "meminfo", lambda: {"MemTotal": 16 * 1024 * 1024})  # a 16 GB guest
        patch.start()
        self.addCleanup(patch.stop)
        quiet = unittest.mock.patch.object(sys, "stderr", io.StringIO())
        quiet.start()
        self.addCleanup(quiet.stop)
        self.slice = FakeSlice(8 * GB)
        self.throttle = dl.Throttle(self.dir, self.slice, unit="bosun-jobs.slice", distro="alpha")
        self.policy()

    def policy(self, **change):
        doc = {"schema_version": 1, "enabled": True, "floor_mb": 2048, "recovery_band_mb": 1024, "job_limit_mb": 2048}
        doc.update(change)
        with open(os.path.join(self.dir, dl.POLICY_FILE), "w", encoding="utf-8") as f:
            json.dump(doc, f)

    def host(self, available, age=0, guest=None):
        doc = {"schema_version": 1, "side": "windows", "at": (NOW - datetime.timedelta(seconds=age)).strftime("%Y-%m-%dT%H:%M:%SZ"),
               "memory": {"available_mb": available, "total_mb": 32000}}
        with open(os.path.join(self.dir, "current-windows.json"), "w", encoding="utf-8") as f:
            json.dump(doc, f)
        if guest is not None:   # the guest's own snapshot is never what the trigger reads
            with open(os.path.join(self.dir, "current-wsl.json"), "w", encoding="utf-8") as f:
                json.dump({"schema_version": 1, "side": "wsl", "memory": {"available_mb": guest, "total_mb": 12000}}, f)

    def tick(self, available=None, **kw):
        if available is not None:
            self.host(available, **kw)
        return self.throttle.run_once(now=NOW)["state"]

    def status(self):
        with open(os.path.join(self.dir, self.throttle.status_name), encoding="utf-8") as f:
            return json.load(f)

    def state_file(self):
        with open(os.path.join(self.dir, self.throttle.state_name), encoding="utf-8") as f:
            return json.load(f)["lowered"]


class ThrottleTrigger(ThrottleCase):
    def test_it_acts_on_windows_memory_and_never_on_the_guests(self):
        self.assertEqual(self.tick(5000, guest=50), "idle")          # Windows has plenty, the guest has almost none
        self.assertEqual(self.slice.sets(), [])
        self.assertEqual(self.tick(1000, guest=11000), "lowered")    # Windows is short, the guest has plenty
        self.assertEqual(self.slice.value, self.LIMIT)

    def test_it_starts_under_the_floor_and_not_at_it(self):
        self.assertEqual(self.tick(2048), "idle")
        self.assertEqual(self.slice.sets(), [])
        self.assertEqual(self.tick(2047), "lowered")

    def test_it_leaves_only_over_the_floor_plus_the_band(self):
        self.tick(1500)
        for available in (2049, 3000, 3072):                          # over the floor, not over the band: still lowered
            self.assertEqual(self.tick(available), "lowered", available)
            self.assertEqual(self.slice.value, self.LIMIT)
        self.assertEqual(self.tick(3073), "restored")
        self.assertEqual(self.slice.value, 8 * GB)

    def test_inside_the_band_after_leaving_it_does_not_start_again(self):
        self.tick(1500)
        self.tick(4000)
        self.assertEqual(self.tick(2500), "idle")                      # over the floor: not an episode
        self.assertEqual(len(self.slice.sets()), 2)                    # lowered once, restored once

    def test_a_stale_missing_or_invalid_sample_starts_nothing(self):
        self.assertEqual(self.tick(100, age=91), "idle")
        os.remove(os.path.join(self.dir, "current-windows.json"))
        self.assertEqual(self.tick(), "idle")
        with open(os.path.join(self.dir, "current-windows.json"), "w") as f:
            f.write("{broken")
        self.assertEqual(self.tick(), "idle")
        self.host(99999)                                               # more available than installed
        self.assertEqual(self.tick(), "idle")
        self.assertEqual(self.slice.sets(), [])

    def test_a_stale_missing_or_invalid_sample_keeps_the_state_as_it_is(self):
        self.tick(1000)
        self.assertEqual(self.tick(10000, age=91), "lowered")         # a healthy reading too old to trust does not end it
        os.remove(os.path.join(self.dir, "current-windows.json"))
        self.assertEqual(self.tick(), "lowered")
        self.assertEqual(self.slice.value, self.LIMIT)
        self.assertEqual(len(self.slice.sets()), 1)
        self.assertEqual(self.tick(5000), "restored")

    def test_nothing_acts_with_the_switch_off_or_no_policy_and_no_file_is_written(self):
        self.policy(enabled=False)
        self.assertEqual(self.tick(100), "off")
        os.remove(os.path.join(self.dir, dl.POLICY_FILE))
        self.assertEqual(self.tick(100), "off")
        self.assertEqual(self.slice.calls, [])
        self.assertFalse(os.path.exists(os.path.join(self.dir, self.throttle.status_name)))   # a machine that never opted in gets no file
        self.assertFalse(os.path.exists(os.path.join(self.dir, self.throttle.state_name)))


class ThrottleLimits(ThrottleCase):
    def test_a_limit_is_lowered_and_the_original_is_restored_by_its_own_value(self):
        self.assertEqual(self.tick(1000), "lowered")
        self.assertEqual(self.state_file(), {"unit": "bosun-jobs.slice", "original": 8 * GB, "applied": self.LIMIT, "unverified": False})
        self.assertEqual(self.tick(5000), "restored")
        self.assertEqual(self.slice.value, 8 * GB)
        self.assertIsNone(self.state_file())

    def test_an_unlimited_slice_is_lowered_and_restored_to_infinity(self):
        self.slice.value = dl.INFINITY
        self.tick(1000)
        self.assertEqual(self.slice.value, self.LIMIT)
        self.assertEqual(self.state_file()["original"], dl.INFINITY)
        self.tick(5000)
        self.assertEqual(self.slice.value, dl.INFINITY)
        self.assertEqual(self.slice.sets()[-1], ["set-property", "--runtime", "bosun-jobs.slice", "MemoryHigh=infinity"])

    def test_a_limit_only_ever_goes_down(self):
        for existing in (GB, self.LIMIT):                              # tighter than the policy's, and equal to it
            self.slice.value = existing
            self.assertEqual(self.tick(1000), "idle")
            self.assertEqual(self.slice.value, existing)
            self.assertEqual(self.slice.sets(), [])
            self.assertFalse(os.path.exists(os.path.join(self.dir, self.throttle.state_name)))

    def test_the_original_survives_repeated_low_samples(self):
        for _ in range(4):
            self.tick(500)
        self.assertEqual(self.state_file()["original"], 8 * GB)       # never the lowered value
        self.assertEqual(len(self.slice.sets()), 1)
        self.tick(9000)
        self.assertEqual(self.slice.value, 8 * GB)

    def test_a_limit_over_the_guests_total_or_under_the_least_is_refused_and_nothing_changes(self):
        self.policy(job_limit_mb=20000)                                # the guest is 16,384 MB
        self.assertEqual(self.tick(1000), "error")
        self.assertIn("over the guest's total memory", self.status()["detail"])
        self.policy(job_limit_mb=512)
        self.assertEqual(self.tick(1000), "off")
        self.assertIn("least allowed", self.status()["detail"])
        self.assertEqual(self.slice.sets(), [])


class ThrottleFailures(ThrottleCase):
    def test_no_delegation_says_why_changes_nothing_and_the_next_sample_tries_again(self):
        self.slice.fail_show = True
        self.assertEqual(self.tick(1000), "error")
        self.assertIn("MemoryHigh", self.status()["detail"])
        self.assertIn("not delegated", self.status()["detail"])
        self.assertEqual(self.slice.sets(), [])
        self.assertFalse(os.path.exists(os.path.join(self.dir, self.throttle.state_name)))
        self.slice.fail_show = False
        self.assertEqual(self.tick(1000), "lowered")

    def test_a_refused_write_leaves_the_slice_as_it_was_and_drops_the_record(self):
        self.slice.refuse_set = True
        self.assertEqual(self.tick(1000), "error")
        self.assertIn("Access denied", self.status()["detail"])
        self.assertEqual(self.slice.value, 8 * GB)
        self.assertIsNone(self.state_file())
        self.slice.refuse_set = False
        self.assertEqual(self.tick(1000), "lowered")

    def test_a_write_that_reads_back_wrong_is_undone_and_said(self):
        self.slice.ignore_set = True
        self.assertEqual(self.tick(1000), "error")
        self.assertIn("after the write", self.status()["detail"])
        self.assertIsNone(self.state_file())
        self.slice.ignore_set = False
        self.slice.set_to = str(5 * GB)                                # a write that lands somewhere else: the operator's (round 5)
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual(self.slice.value, 5 * GB)                     # not rolled back
        self.assertIsNone(self.state_file())

    def test_an_original_that_can_not_be_put_back_is_kept_and_retried_until_it_is(self):
        original_set = self.slice.__call__

        def stubborn(args):
            if args[0] == "set-property" and self.slice.refuse_set is False and args[3] == "MemoryHigh=%d" % (8 * GB):
                return 1, "busy"                                       # lowering works, putting the original back does not
            code = original_set(args)
            if args[0] == "set-property":
                self.slice.fail_show = True                            # and the verification read after the write fails
            return code
        self.throttle.systemctl = stubborn
        self.assertEqual(self.tick(1000), "error")
        self.assertEqual(self.state_file()["original"], 8 * GB)
        self.assertTrue(self.state_file()["unverified"])
        self.assertEqual(self.tick(1000), "error")                     # still kept, never taken for an operator's change
        self.assertEqual(self.state_file()["original"], 8 * GB)
        self.throttle.systemctl = self.slice
        self.slice.fail_show = False
        self.assertEqual(self.tick(1000), "lowered")                   # it reads Bosun's value: the lowering took (round 8)
        self.assertFalse(self.state_file()["unverified"])
        self.assertEqual(self.tick(9000), "restored")
        self.assertEqual(self.slice.value, 8 * GB)
        self.assertIsNone(self.state_file())

    def test_a_failed_restore_keeps_the_original_through_repeated_samples_and_clears_it_only_when_verified(self):
        self.tick(1000)
        self.slice.refuse_set = True
        for _ in range(3):
            self.assertEqual(self.tick(9000), "error")
            self.assertIn("could not be put back", self.status()["detail"])
            self.assertEqual(self.state_file()["original"], 8 * GB)
        self.assertEqual(self.slice.value, self.LIMIT)
        self.slice.refuse_set = False
        self.assertEqual(self.tick(9000), "restored")
        self.assertEqual(self.slice.value, 8 * GB)
        self.assertIsNone(self.state_file())

    def test_a_restore_that_reads_back_wrong_is_not_cleared(self):
        self.tick(1000)
        self.slice.ignore_set = True
        self.assertEqual(self.tick(9000), "error")
        self.assertEqual(self.state_file()["original"], 8 * GB)
        self.assertEqual(self.slice.value, self.LIMIT)

    def test_a_failed_read_while_lowered_keeps_the_record_and_sampling_goes_on(self):
        self.tick(1000)
        self.slice.fail_show = True
        self.assertEqual(self.tick(9000), "error")
        self.assertEqual(self.state_file()["original"], 8 * GB)
        self.slice.fail_show = False
        self.assertEqual(self.tick(9000), "restored")

    def test_when_the_record_can_not_be_written_no_limit_is_changed(self):
        real = dl.write_snapshot

        def refuse_state(log_dir, text, name="current-wsl.json", **kw):
            return False if name == self.throttle.state_name else real(log_dir, text, name=name, **kw)
        with unittest.mock.patch.object(dl, "write_snapshot", refuse_state):
            self.assertEqual(self.tick(1000), "error")
        self.assertIn(self.throttle.state_name, self.status()["detail"])
        self.assertEqual(self.slice.sets(), [])

    def test_a_record_that_can_not_be_read_stops_action_until_it_is_fixed(self):
        with open(os.path.join(self.dir, self.throttle.state_name), "w") as f:
            f.write("{broken")
        throttle = dl.Throttle(self.dir, self.slice, distro="alpha")
        self.assertEqual(throttle.run_once(now=NOW)["state"], "error")
        self.host(100)
        self.assertEqual(throttle.run_once(now=NOW)["state"], "error")
        self.assertEqual(self.slice.sets(), [])

    def test_the_status_says_why_in_a_file_with_a_time(self):
        self.slice.fail_show = True
        self.tick(1000)
        doc = self.status()
        self.assertEqual((doc["state"], doc["schema_version"], doc["unit"]), ("error", 1, "bosun-jobs.slice"))
        datetime.datetime.strptime(doc["at"], "%Y-%m-%dT%H:%M:%SZ")


class ThrottleRecordAndRace(ThrottleCase):
    def test_an_operators_limit_set_while_the_record_is_saved_is_never_written_over(self):
        # Round 7: the operator set 1 GiB between Bosun's first read and its write; Bosun wrote 2 GiB over it and lost it.
        self.slice.on_show = {2: 1 * GB}                               # the read just before the write finds the operator's
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual(self.slice.value, 1 * GB)
        self.assertEqual(self.slice.sets(), [])                         # nothing written
        self.assertEqual(self.tick(1000), "left_alone")                 # held for the episode

    def test_a_record_that_breaks_the_writers_rules_authorises_nothing(self):
        # Round 7: applied 0 with original infinity passed, and stop() could lift an operator's MemoryHigh=0.
        for lowered in ({"unit": "bosun-jobs.slice", "original": "infinity", "applied": 0, "unverified": False},
                        {"unit": "bosun-jobs.slice", "original": 2 * GB, "applied": 4 * GB, "unverified": False},
                        {"unit": "bosun-jobs.slice", "original": "infinity", "applied": 2 * GB, "unverified": "yes"}):
            with open(os.path.join(self.dir, self.throttle.state_name), "w", encoding="utf-8") as f:
                json.dump({"schema_version": 1, "distro": "alpha", "lowered": lowered, "operator": None}, f)
            self.slice.value = lowered["applied"]
            self.slice.calls.clear()
            self.throttle.stop()
            self.assertEqual(self.slice.sets(), [], lowered)
            self.assertEqual(self.tick(5000), "error", lowered)
            self.assertEqual(self.slice.sets(), [], lowered)


class ThrottleStop(ThrottleCase):
    def test_a_stopping_service_puts_its_own_lowering_back(self):
        # Round 6: a stopped service left a running job throttled until another logger started.
        self.assertEqual(self.tick(1000), "lowered")
        self.assertEqual(self.slice.value, self.LIMIT)
        self.throttle.stop()
        self.assertEqual(self.slice.value, 8 * GB)
        self.assertIsNone(self.throttle.safe_load_state()[0])

    def test_a_stopping_service_leaves_an_operators_value_and_an_untouched_slice_alone(self):
        self.throttle.stop()                                          # nothing lowered: nothing written
        self.assertEqual(self.slice.sets(), [])
        self.tick(1000)
        self.slice.value = 3 * GB                                     # the operator's
        self.throttle.stop()
        self.assertEqual(self.slice.value, 3 * GB)


class ThrottleDisableAndOperator(ThrottleCase):
    def test_turning_the_switch_off_while_lowered_restores(self):
        self.tick(1000)
        self.policy(enabled=False)
        self.assertEqual(self.tick(1000), "restored")                  # still short of memory: off means off
        self.assertEqual(self.slice.value, 8 * GB)
        self.assertIsNone(self.state_file())

    def test_the_policy_file_going_while_lowered_restores(self):
        self.tick(1000)
        os.remove(os.path.join(self.dir, dl.POLICY_FILE))
        self.assertEqual(self.tick(1000), "restored")
        self.assertEqual(self.slice.value, 8 * GB)

    def test_a_policy_that_became_unusable_while_lowered_restores_and_says_why(self):
        self.tick(1000)
        with open(os.path.join(self.dir, dl.POLICY_FILE), "w") as f:
            f.write("{broken")
        self.assertEqual(self.tick(1000), "restored")
        self.assertIn("not JSON", self.status()["detail"])
        self.assertEqual(self.slice.value, 8 * GB)

    def test_a_logger_restart_after_throttling_still_restores_the_original(self):
        self.tick(1000)
        restarted = dl.Throttle(self.dir, self.slice, unit="bosun-jobs.slice", distro="alpha")  # a new process reads only the record
        self.assertEqual(restarted.state["original"], 8 * GB)
        self.assertEqual(restarted.run_once(now=NOW)["state"], "lowered")      # still under pressure: kept
        self.host(9000)
        self.assertEqual(restarted.run_once(now=NOW)["state"], "restored")
        self.assertEqual(self.slice.value, 8 * GB)

    def test_a_restart_of_wsl_that_dropped_the_runtime_limit_drops_the_record_without_a_write(self):
        self.tick(1000)
        self.slice.value = 8 * GB                                       # the VM restarted: the runtime limit is gone
        sets = len(self.slice.sets())
        self.assertEqual(self.tick(9000), "restored")                   # the episode is over: dropped (under pressure it is a hold, round 4)
        self.assertEqual(len(self.slice.sets()), sets)
        self.assertIsNone(self.state_file())

    def test_an_operators_own_change_is_left_alone_and_said(self):
        self.tick(1000)
        self.slice.value = 3 * GB                                       # neither Bosun's value nor the original
        sets = len(self.slice.sets())
        self.assertEqual(self.tick(9000), "left_alone")                 # even when Windows has recovered
        self.assertEqual(self.slice.value, 3 * GB)
        self.assertEqual(len(self.slice.sets()), sets)
        self.assertIn("someone changed it", self.status()["detail"])
        self.assertIsNone(self.state_file())
        self.assertEqual(self.tick(9000), "idle")                      # and the next sample does not touch it either
        self.assertEqual(self.slice.value, 3 * GB)

    def test_an_operators_change_is_kept_when_the_switch_is_turned_off_too(self):
        self.tick(1000)
        self.slice.value = 3 * GB
        self.policy(enabled=False)
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual(self.slice.value, 3 * GB)


class ThrottleReviewRound1(ThrottleCase):
    """The seven findings of D05 T02 section 5's round 1, each a test that fails without its fix."""

    def other(self, distro, value):
        slice_ = FakeSlice(value)
        return slice_, dl.Throttle(self.dir, slice_, unit="bosun-jobs.slice", distro=distro)

    def test_each_distro_has_its_own_record_and_a_restart_restores_only_its_own_original(self):
        beta_slice, beta = self.other("beta", dl.INFINITY)
        alpha_slice = self.slice
        alpha_slice.value = alpha_slice.initial = 4 * GB
        self.host(1000)
        self.assertEqual(self.throttle.run_once(now=NOW)["state"], "lowered")
        self.assertEqual(beta.run_once(now=NOW)["state"], "lowered")     # written last, as the finding has it
        self.assertNotEqual(self.throttle.state_name, beta.state_name)
        self.assertTrue(os.path.exists(os.path.join(self.dir, f"pressure-state-alpha-u{os.getuid()}.json")))
        self.assertTrue(os.path.exists(os.path.join(self.dir, f"pressure-status-beta-u{os.getuid()}.json")))
        restarted = dl.Throttle(self.dir, alpha_slice, unit="bosun-jobs.slice", distro="alpha")
        self.assertEqual(restarted.state["original"], 4 * GB)
        self.host(9000)
        self.assertEqual(restarted.run_once(now=NOW)["state"], "restored")
        self.assertEqual(alpha_slice.value, 4 * GB)                       # not infinity, which is beta's
        self.assertEqual(beta_slice.value, self.LIMIT)                    # and beta's slice was never touched by alpha
        self.assertEqual(beta.state["original"], dl.INFINITY)

    def test_a_record_of_another_distro_is_never_acted_on(self):
        self.tick(1000)
        path = os.path.join(self.dir, self.throttle.state_name)
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
        doc["distro"] = "beta"
        with open(path, "w", encoding="utf-8") as f:
            json.dump(doc, f)
        sets = len(self.slice.sets())
        restarted = dl.Throttle(self.dir, self.slice, unit="bosun-jobs.slice", distro="alpha")
        self.assertIsNone(restarted.state)
        self.host(9000)
        self.assertEqual(restarted.run_once(now=NOW)["state"], "error")
        self.assertEqual(len(self.slice.sets()), sets)

    def test_the_distro_comes_from_the_environment_wsl_gives_and_is_validated(self):
        with unittest.mock.patch.dict(os.environ, {"WSL_DISTRO_NAME": "Ubuntu-24.04"}):
            self.assertEqual(dl.Throttle(self.dir, self.slice).state_name, f"pressure-state-Ubuntu-24.04-u{os.getuid()}.json")
        with unittest.mock.patch.dict(os.environ, {"WSL_DISTRO_NAME": "../evil"}):
            self.assertIsNone(dl.Throttle(self.dir, self.slice).distro)   # a name that is no file name is no identity, never "default"

    def test_an_operators_take_over_is_kept_for_the_whole_episode_and_forgotten_after_it(self):
        self.assertEqual(self.tick(1000), "lowered")
        self.slice.value = 3 * GB
        sets = len(self.slice.sets())
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual(self.tick(1000), "left_alone")                   # the finding: this sample lowered it again
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual((self.slice.value, len(self.slice.sets())), (3 * GB, sets))
        restarted = dl.Throttle(self.dir, self.slice, unit="bosun-jobs.slice", distro="alpha")
        self.assertEqual(restarted.run_once(now=NOW)["state"], "left_alone")  # even through a logger restart
        self.assertEqual(self.tick(3000), "left_alone")                   # inside the band: the episode is not over
        self.assertEqual(self.tick(9000), "idle")                         # over the floor plus the band: forgotten
        self.assertEqual(self.slice.value, 3 * GB)
        self.assertEqual(self.tick(1000), "lowered")                      # a new episode, and only ever a lowering
        self.assertEqual(self.slice.value, self.LIMIT)

    def test_an_operators_take_over_is_forgotten_when_the_switch_goes_off(self):
        self.tick(1000)
        self.slice.value = 3 * GB
        self.assertEqual(self.tick(1000), "left_alone")
        self.policy(enabled=False)
        self.assertEqual(self.tick(1000), "off")
        self.policy(enabled=True)
        self.assertEqual(self.tick(1000), "lowered")

    def test_an_unverified_lowering_never_restores_over_an_operators_change(self):
        done = []

        def flaky(args):
            result = self.slice(args)
            if args[0] == "set-property" and not done:   # the connection drops after the lowering write
                done.append(1)
                self.slice.fail_show = self.slice.refuse_set = True
            return result
        self.throttle.systemctl = flaky
        self.assertEqual(self.tick(1000), "error")
        self.assertTrue(self.state_file()["unverified"])
        self.slice.fail_show = self.slice.refuse_set = False
        self.slice.value = 512 * 1024 * 1024                              # the operator's stricter cap
        sets = len(self.slice.sets())
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual((self.slice.value, len(self.slice.sets())), (512 * 1024 * 1024, sets))
        self.assertIsNone(self.state_file())
        self.assertIn("someone changed it", self.status()["detail"])

    def test_deeply_nested_json_is_an_unreadable_file_in_every_read_never_an_exception(self):
        deep = "[" * 2000 + "]" * 2000
        for name in (dl.POLICY_FILE, "current-windows.json", self.throttle.state_name):
            with open(os.path.join(self.dir, name), "w") as f:
                f.write(deep)
        # Whether json.loads raises RecursionError for 2,000 brackets depends on the interpreter (3.14 does not), so the
        # raise is also forced: what is under test is that the logger treats it as an unreadable file.
        for loads in (json.loads, unittest.mock.Mock(side_effect=RecursionError("maximum recursion depth exceeded"))):
            with unittest.mock.patch.object(dl.json, "loads", loads):
                self.assertIsNone(dl.read_policy(self.dir)[0])
                self.assertIsNone(dl.read_host(self.dir, NOW)[0])
                throttle = dl.Throttle(self.dir, self.slice, unit="bosun-jobs.slice", distro="alpha")   # start-up does not raise
                self.assertIsNotNone(throttle.state_fault)
                self.assertEqual(throttle.run_once(now=NOW)["state"], "error")
        with unittest.mock.patch.object(dl.json, "loads", side_effect=RecursionError("deep")):
            self.assertIn("not JSON", dl.read_policy(self.dir)[1])
            self.assertIn("not JSON", dl.read_host(self.dir, NOW)[1])
        self.assertEqual(self.slice.sets(), [])

    def test_a_start_up_that_raises_on_the_record_is_a_fault_not_a_stopped_service(self):
        with unittest.mock.patch.object(dl, "read_small_json", side_effect=RecursionError("deep")):
            throttle = dl.Throttle(self.dir, self.slice, unit="bosun-jobs.slice", distro="alpha")
        self.assertIn("can't be read", throttle.state_fault)

    def test_a_record_unreadable_at_start_is_read_again_at_the_next_sample(self):
        self.tick(1000)
        path = os.path.join(self.dir, self.throttle.state_name)
        with open(path, encoding="utf-8") as f:
            good = f.read()
        with open(path, "w") as f:
            f.write("{broken")
        restarted = dl.Throttle(self.dir, self.slice, unit="bosun-jobs.slice", distro="alpha")
        self.assertIsNotNone(restarted.state_fault)
        with open(path, "w") as f:
            f.write(good)                                                 # readable again
        self.host(9000)
        self.assertEqual(restarted.run_once(now=NOW)["state"], "restored")
        self.assertEqual(self.slice.value, 8 * GB)

    def test_a_windows_sample_dated_in_the_future_starts_nothing(self):
        self.assertEqual(self.tick(1000, age=-86400), "idle")
        self.assertIn("future", self.throttle.last["detail"])
        self.assertEqual(self.slice.sets(), [])
        self.assertEqual(dl.read_host(self.dir, NOW), (None, "the Windows snapshot is dated in the future"))
        self.host(1000, age=-60)                                          # within the clock-skew allowance
        self.assertEqual(dl.read_host(self.dir, NOW)[0], 1000)


class ThrottleReviewRound4(ThrottleCase):
    """The three findings of D05 T02 section 5's round 4, each a test that fails without its fix."""

    def test_an_operator_putting_the_original_back_mid_episode_is_held_off_for_the_episode(self):
        self.slice.value = self.slice.initial = dl.INFINITY
        self.assertEqual(self.tick(1000), "lowered")
        self.slice.value = dl.INFINITY                                    # the operator sets the original back
        sets = len(self.slice.sets())
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual((self.slice.value, len(self.slice.sets())), (dl.INFINITY, sets))   # never lowered again
        self.assertEqual(self.throttle.operator["seen"], dl.INFINITY)
        self.assertEqual(self.tick(9000), "idle")                         # recovery forgets the hold
        self.assertEqual(self.tick(1000), "lowered")                      # a new episode: Bosun may lower again
        self.assertEqual(self.slice.value, self.LIMIT)

    def test_bosuns_own_restore_whose_record_could_not_be_dropped_is_not_taken_for_an_operator(self):
        self.tick(1000)
        real = dl.write_snapshot
        def refuse_state(log_dir, text, *a, **k):
            return False if k.get("name") == self.throttle.state_name else real(log_dir, text, *a, **k)
        with unittest.mock.patch.object(dl, "write_snapshot", refuse_state):
            self.assertEqual(self.tick(9000), "restored")                 # the limit is back, the record could not be dropped
        self.assertEqual(self.slice.value, 8 * GB)
        self.assertEqual(self.tick(1000), "restored")                     # next sample: Bosun's own restore, not a hold
        self.assertIsNone(self.state_file())

    def test_a_logger_with_no_identity_never_throttles_and_says_why(self):
        with unittest.mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("BOSUN_DISTRO", None)
            os.environ.pop("WSL_DISTRO_NAME", None)
            nameless = dl.Throttle(self.dir, self.slice, unit="bosun-jobs.slice")
        self.assertIsNone(nameless.distro)
        self.host(1000)
        self.assertEqual(nameless.run_once(now=NOW)["state"], "error")
        self.assertIn("no distro identity", nameless.last["detail"])
        self.assertEqual(self.slice.sets(), [])
        self.assertFalse(os.path.exists(os.path.join(self.dir, f"pressure-state-default-u{os.getuid()}.json")))
        with open(os.path.join(self.dir, f"pressure-status-default-u{os.getuid()}.json"), encoding="utf-8") as f:
            self.assertIn("no distro identity", json.load(f)["detail"])

    def test_the_installers_name_comes_before_wsls_and_each_distro_keeps_its_own_record(self):
        with unittest.mock.patch.dict(os.environ, {"BOSUN_DISTRO": "Installed", "WSL_DISTRO_NAME": "FromWsl"}):
            self.assertEqual(dl.Throttle(self.dir, self.slice).distro, "Installed")
        with unittest.mock.patch.dict(os.environ, {"WSL_DISTRO_NAME": "FromWsl"}):
            os.environ.pop("BOSUN_DISTRO", None)
            self.assertEqual(dl.Throttle(self.dir, self.slice).distro, "FromWsl")

    def test_a_status_write_that_failed_is_retried_on_an_idle_sample(self):
        self.tick(1000)
        real = dl.write_snapshot
        def failing_status(*a, **k):   # only the status file's replace fails; the state record is written as usual
            return real(*a, replace=self.refuse_replace, attempts=0, **k) if k.get("name") == self.throttle.status_name else real(*a, **k)
        with unittest.mock.patch.object(dl, "write_snapshot", failing_status):
            self.assertEqual(self.tick(9000), "restored")                 # its status write fails
            self.assertEqual(self.tick(9000), "idle")                     # and so does the following idle sample's
        self.assertEqual(self.status()["state"], "lowered")               # the old document is still on disk
        self.assertEqual(self.tick(9000), "idle")                         # an idle sample with the write working again
        self.assertEqual(self.status()["state"], "idle")                  # the disk shows the current status, not "lowered"
        self.assertEqual(self.tick(9000), "idle")

    @staticmethod
    def refuse_replace(src, dst):
        raise PermissionError("held open")


@unittest.skipUnless(sys.platform.startswith("linux") and shutil.which("bash") and shutil.which("grep"), "needs Linux with bash")
class InstallScript(unittest.TestCase):
    """wsl/install.sh run against a temporary HOME and a stand-in systemctl: the unit it writes names the distro."""

    def run_install(self, **env):
        home = tempfile.mkdtemp(prefix="bosun-install-")
        self.addCleanup(shutil.rmtree, home, True)
        fake = os.path.join(home, "fakebin")
        os.makedirs(fake)
        with open(os.path.join(fake, "systemctl"), "w", newline="\n") as f:
            f.write("#!/bin/sh\nexit 0\n")
        os.chmod(os.path.join(fake, "systemctl"), 0o755)
        environ = {k: v for k, v in os.environ.items() if k != "WSL_DISTRO_NAME"}
        environ.update(HOME=home, PATH=fake + os.pathsep + os.environ["PATH"], **env)
        done = subprocess.run(["bash", os.path.join(os.path.dirname(__file__), "..", "wsl", "install.sh"), "/mnt/c/bosun.exe", "/mnt/c/logs"],
                              env=environ, capture_output=True, text=True, timeout=60)
        unit = os.path.join(home, ".config", "systemd", "user", "bosun-log.service")
        if not os.path.exists(unit):
            return done, None
        with open(unit, encoding="utf-8") as f:
            return done, f.read()

    def test_the_unit_carries_the_distro_name_from_install_time(self):
        done, unit = self.run_install(WSL_DISTRO_NAME="Ubuntu-24.04")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("Environment=BOSUN_DISTRO=Ubuntu-24.04\n", unit)

    def test_install_fails_without_a_usable_distro_name(self):
        for env in ({}, {"WSL_DISTRO_NAME": ""}, {"WSL_DISTRO_NAME": "../evil"}, {"WSL_DISTRO_NAME": "a b"},
                    {"WSL_DISTRO_NAME": "Ubuntu\nExecStartPre=/usr/bin/false"}):  # round 6: one good line passed
            done, unit = self.run_install(**env)
            self.assertNotEqual(done.returncode, 0, env)
            self.assertIsNone(unit, env)


class ThrottleReviewRound5(ThrottleCase):
    """The two findings of D05 T02 section 5's round 5, each a test that fails without its fix."""

    def test_a_once_run_samples_and_reports_but_never_throttles(self):
        with open(os.path.join(self.dir, "current-windows.json"), "w", encoding="utf-8") as f:   # under the floor, dated now: main reads the real clock
            json.dump({"schema_version": 1, "side": "windows", "memory": {"available_mb": 1000, "total_mb": 32000},
                       "at": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}, f)
        real = dl.Throttle
        with unittest.mock.patch.object(dl, "sample", lambda *a, **k: None),                 unittest.mock.patch.object(dl, "Throttle", lambda log_dir: real(log_dir, self.slice, unit="bosun-jobs.slice", distro="alpha")):
            self.assertEqual(dl.main(["bosun_log.py", self.dir, "--once"]), 0)
        self.assertEqual(self.slice.sets(), [])
        self.assertEqual(self.slice.value, 8 * GB)
        self.assertFalse(os.path.exists(os.path.join(self.dir, self.throttle.state_name)))
        self.assertIn("--once", dl.__doc__)

    def test_a_record_written_by_another_process_is_honoured_and_restored_on_recovery(self):
        self.assertEqual(self.tick(9000), "idle")                         # the service's first sample: no record
        other = dl.Throttle(self.dir, self.slice, unit="bosun-jobs.slice", distro="alpha")
        self.host(1000)
        self.assertEqual(other.run_once(now=NOW)["state"], "lowered")     # another process lowers the slice and saves the record
        self.assertEqual(self.slice.value, self.LIMIT)
        self.assertEqual(self.tick(1000), "lowered")                      # the service sees it, in memory it had none
        self.assertEqual(self.tick(9000), "restored")                     # and puts the original back on recovery
        self.assertEqual(self.slice.value, 8 * GB)
        self.assertIsNone(self.state_file())

    def test_a_stricter_limit_set_between_the_write_and_the_read_is_the_operators_not_rolled_back(self):
        self.slice.value = self.slice.initial = dl.INFINITY
        self.slice.set_to = str(1 * GB)                                   # Bosun writes 2 GiB; the read returns the operator's 1 GiB
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual(self.slice.value, 1 * GB)
        self.assertEqual(len(self.slice.sets()), 1)                       # no restore write
        self.assertEqual(self.throttle.operator["seen"], 1 * GB)
        self.assertIsNone(self.state_file())
        self.assertEqual(self.tick(1000), "left_alone")                   # held for the episode
        self.assertEqual((self.slice.value, len(self.slice.sets())), (1 * GB, 1))
        self.assertIn("someone else", self.status()["detail"])


class ThrottleReviewRound8(ThrottleCase):
    """The two findings of D05 T02 section 5's round 8, each a test that fails without its fix."""

    def test_each_user_manager_has_its_own_record_in_the_same_distro_and_folder(self):
        slice_a, slice_b = FakeSlice(dl.INFINITY), FakeSlice(dl.INFINITY)
        a = dl.Throttle(self.dir, slice_a, unit="bosun-jobs.slice", distro="alpha", uid=1000)
        b = dl.Throttle(self.dir, slice_b, unit="bosun-jobs.slice", distro="alpha", uid=1001)
        self.assertEqual(a.state_name, "pressure-state-alpha-u1000.json")
        self.assertEqual(b.status_name, "pressure-status-alpha-u1001.json")
        self.host(1000)
        self.assertEqual(a.run_once(now=NOW)["state"], "lowered")
        self.assertEqual(b.run_once(now=NOW)["state"], "lowered")        # B sees its own untouched slice, not an operator override
        self.assertEqual((slice_a.value, slice_b.value), (self.LIMIT, self.LIMIT))
        self.assertEqual(a.safe_load_state()[0]["original"], dl.INFINITY)   # A's record survived B's start
        self.host(9000)
        self.assertEqual(a.run_once(now=NOW)["state"], "restored")
        self.assertEqual(slice_a.value, dl.INFINITY)
        self.assertEqual(b.run_once(now=NOW)["state"], "restored")
        self.assertEqual(slice_b.value, dl.INFINITY)

    def test_a_record_of_another_uid_is_never_acted_on(self):
        self.tick(1000)
        path = os.path.join(self.dir, self.throttle.state_name)
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
        self.assertEqual(doc["uid"], self.throttle.uid)
        doc["uid"] = self.throttle.uid + 1
        with open(path, "w", encoding="utf-8") as f:
            json.dump(doc, f)
        sets = len(self.slice.sets())
        self.host(9000)
        self.assertEqual(self.throttle.run_once(now=NOW)["state"], "error")
        self.assertEqual(len(self.slice.sets()), sets)

    def lower_with_a_failed_verification_read(self):
        """Infinity; Bosun writes 2 GiB; the verification read fails while the operator sets 1 GiB."""
        self.slice.value = dl.INFINITY
        done = []

        def flaky(args):
            result = self.slice(args)
            if args[0] == "set-property" and not done:
                done.append(1)
                self.slice.fail_show = True
                self.slice.value = GB                                    # the operator's cap, set as the read fails
            return result
        self.throttle.systemctl = flaky
        self.assertEqual(self.tick(1000), "error")
        self.assertTrue(self.state_file()["unverified"])
        self.slice.fail_show = False

    def test_a_failed_verification_read_never_writes_the_original_over_an_operators_cap(self):
        self.lower_with_a_failed_verification_read()
        sets = len(self.slice.sets())
        self.assertEqual(self.tick(1000), "left_alone")
        self.assertEqual(self.slice.value, GB)
        self.assertEqual(len(self.slice.sets()), sets)                   # nothing written, infinity above all
        self.assertNotIn(["set-property", "--runtime", "bosun-jobs.slice", "MemoryHigh=infinity"], self.slice.sets())
        self.assertIsNone(self.state_file())
        with open(os.path.join(self.dir, self.throttle.state_name), encoding="utf-8") as f:
            self.assertEqual(json.load(f)["operator"], {"unit": "bosun-jobs.slice", "seen": GB})   # a hold is recorded

    def test_a_failed_verification_read_then_bosuns_value_is_kept_and_restored_on_recovery(self):
        self.slice.value = dl.INFINITY
        done = []

        def flaky(args):
            result = self.slice(args)
            if args[0] == "set-property" and not done:
                done.append(1)
                self.slice.fail_show = True
            return result
        self.throttle.systemctl = flaky
        self.assertEqual(self.tick(1000), "error")
        self.slice.fail_show = False
        self.assertEqual(self.tick(1000), "lowered")                     # reads 2 GiB: the lowering took
        self.assertEqual(self.state_file()["original"], dl.INFINITY)
        self.assertFalse(self.state_file()["unverified"])
        self.assertEqual(self.tick(9000), "restored")
        self.assertEqual(self.slice.value, dl.INFINITY)
        self.assertIsNone(self.state_file())

    def test_a_failed_verification_read_then_the_original_drops_the_record(self):
        self.slice.value = 8 * GB
        done = []

        def flaky(args):
            if args[0] == "set-property" and not done:
                done.append(1)
                self.slice.fail_show = True
                return 0, ""                                             # accepted, but it did not take
            return self.slice(args)
        self.throttle.systemctl = flaky
        self.assertEqual(self.tick(1000), "error")
        self.slice.fail_show = False
        self.assertEqual(self.tick(9000), "idle")
        self.assertEqual(self.slice.value, 8 * GB)
        with open(os.path.join(self.dir, self.throttle.state_name), encoding="utf-8") as f:
            doc = json.load(f)
        self.assertEqual((doc["lowered"], doc["operator"]), (None, None))

    def test_a_restore_reads_before_it_writes_and_never_writes_over_a_value_that_is_not_bosuns(self):
        self.tick(1000)
        self.slice.value = GB                                            # between the decision and the restore
        self.throttle.state, self.throttle.operator, _ = self.throttle.safe_load_state()
        sets = len(self.slice.sets())
        self.assertEqual(self.throttle.restore_now(), "held")
        self.assertEqual((self.slice.value, len(self.slice.sets())), (GB, sets))


class ThrottleOnARealSlice(unittest.TestCase):
    """Lowers and restores a disposable bosun-test.slice for real: its memory.high in the kernel's own cgroup file. Not
    skipped where a systemd user session exists, and it fails rather than skips when that session can't run a scope."""

    # Each run its own slice (round 3: one shared name let two concurrent runs read and stop each other's), named with a
    # single dash so systemd nests it directly under bosun.slice, as the job slice is.
    UNIT = None

    @classmethod
    def setUpClass(cls):
        if subprocess.run(["systemctl", "--user", "show-environment"], capture_output=True).returncode != 0:
            raise unittest.SkipTest("no systemd user session here (the WSL step runs this test where there is one)")

    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="bosun-real-slice-")
        self.UNIT = f"bosun-t{os.getpid()}r{uuid.uuid4().hex[:8]}.slice"
        self.scope = f"bosun-test-sleep-{os.getpid()}-{uuid.uuid4().hex[:8]}"
        self.addCleanup(self.cleanup)
        uid = os.getuid()
        self.cgroup = f"/sys/fs/cgroup/user.slice/user-{uid}.slice/user@{uid}.service/bosun.slice/{self.UNIT}"

    def cleanup(self):
        # Always, even after a failure: the scope, the slice and the runtime limit this test wrote are removed.
        for args in (["stop", self.scope + ".scope"], ["stop", self.UNIT], ["revert", self.UNIT]):
            subprocess.run(["systemctl", "--user", *args], capture_output=True, timeout=30)
        shutil.rmtree(os.path.join("/run/user", str(os.getuid()), "systemd", "user.control", self.UNIT + ".d"), ignore_errors=True)
        shutil.rmtree(self.dir, True)

    def kernel_limit(self):
        with open(os.path.join(self.cgroup, "memory.high"), encoding="ascii") as f:
            return f.read().strip()

    def test_the_throttle_lowers_and_restores_the_kernels_limit_on_a_disposable_slice(self):
        # A live scope in the slice makes the kernel's cgroup exist, so what is read back is the kernel's own limit.
        child = subprocess.Popen(["systemd-run", "--user", "--scope", "--quiet", "--collect", f"--slice={self.UNIT}", f"--unit={self.scope}", "--", "sleep", "300"],
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
        self.addCleanup(child.wait)
        self.addCleanup(lambda: subprocess.run(["systemctl", "--user", "kill", "--signal=KILL", self.scope + ".scope"], capture_output=True, timeout=30))
        deadline = time.monotonic() + 20
        while not os.path.exists(os.path.join(self.cgroup, "memory.high")) and time.monotonic() < deadline:
            time.sleep(0.1)
        self.assertTrue(os.path.exists(os.path.join(self.cgroup, "memory.high")), f"the scope never started under {self.cgroup}: this machine's systemd user session can't run a scope")
        self.assertEqual(self.kernel_limit(), "max")

        shutil.copy(FIXTURE, os.path.join(self.dir, dl.POLICY_FILE))     # the real file the panel writes: floor 3072, band 1024, limit 6144
        stderr = unittest.mock.patch.object(sys, "stderr", io.StringIO())
        stderr.start()
        self.addCleanup(stderr.stop)
        throttle = dl.Throttle(self.dir, unit=self.UNIT, distro="realslice")                 # the real systemctl
        windows = lambda available: {"schema_version": 1, "side": "windows", "at": NOW.strftime("%Y-%m-%dT%H:%M:%SZ"),
                                     "memory": {"available_mb": available, "total_mb": 32000}}

        def tick(available):
            with open(os.path.join(self.dir, "current-windows.json"), "w") as f:
                json.dump(windows(available), f)
            with unittest.mock.patch.object(dl, "meminfo", lambda: {"MemTotal": 16 * 1024 * 1024}):
                return throttle.run_once(now=NOW)

        self.assertEqual(tick(5000)["state"], "idle")
        self.assertEqual(self.kernel_limit(), "max")
        self.assertEqual(tick(2000)["state"], "lowered")                  # under the 3072 MB floor
        self.assertEqual(self.kernel_limit(), str(6144 * 1024 * 1024))    # the kernel's own limit, lowered
        self.assertEqual(tick(2000)["state"], "lowered")
        self.assertEqual(tick(4096)["state"], "lowered")                  # floor plus the band is not enough
        self.assertEqual(self.kernel_limit(), str(6144 * 1024 * 1024))
        self.assertEqual(tick(4097)["state"], "restored")
        self.assertEqual(self.kernel_limit(), "max")                      # and restored


if __name__ == "__main__":
    unittest.main()
