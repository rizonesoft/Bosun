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


if __name__ == "__main__":
    unittest.main()
