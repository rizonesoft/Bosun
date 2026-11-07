import os
import sys
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


if __name__ == "__main__":
    unittest.main()
