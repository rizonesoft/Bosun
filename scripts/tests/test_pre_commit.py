"""The pre-commit hook: a commit never leaves the generated plan stale, the graph invalid, or its evidence unstaged.

Each case commits through git itself in a temporary repository that holds a valid TODO tree, the real hook and the real
graph tool, so what is tested is what a clone runs."""
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "lib"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_todo_graph import Tree, todo_file  # noqa: E402

TOOLS = ("todo-graph.py", "review-dispatch.py", "lib/todo_graph.py", "lib/host_caps.py", "hooks/pre-commit")
ONE = [(1, "A", 0, "none", "--")]
TWO = [(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")]
GUARDED = ("todo/", "docs/reviews/")


class HookRepo(Tree):
    """A repository with one valid section, its plan in step, and the hook switched on for every commit made here."""

    def setUp(self):
        super().setUp()
        for name in TOOLS:
            target = self.root / "scripts" / name
            target.parent.mkdir(parents=True, exist_ok=True)
            # Copied with its permissions, as a checkout would have it: git on Linux and WSL ignores a hook that is
            # not executable, so making it executable here would hide a hook shipped without that bit.
            shutil.copy(ROOT / "scripts" / name, target)
        self.write(todo_file(ONE))
        (self.root / "docs" / "reviews").mkdir(parents=True)
        (self.root / "docs" / "reviews" / "note.md").write_text("evidence\n", encoding="utf-8")
        (self.root / ".gitignore").write_text("*.ignored\n__pycache__/\nbuild/\n", encoding="utf-8")
        (self.root / "README.md").write_text("fixture\n", encoding="utf-8")
        self.git("init", "-q", "-b", "main")
        self.assertEqual(self.cli("plan", "--sync").returncode, 0)
        # The fixture itself must be sound, or a refusal below would prove nothing about the hook.
        self.assertEqual(self.cli("validate").returncode, 0, "the fixture's graph is not valid")
        self.assertEqual(self.cli("plan", "--check").returncode, 0, "the fixture's plan is not in step")
        self.git("add", "-A")
        first = self.commit("start")
        self.assertEqual(first.returncode, 0, first.stderr)

    def git(self, *args, check=True, env=None):
        return subprocess.run(["git", "-C", str(self.root), "-c", "user.name=t", "-c", "user.email=t@example.com",
                               "-c", "core.autocrlf=false", *args],
                              capture_output=True, text=True, encoding="utf-8", check=check, env=env)

    def commit(self, message, hook=True, env=None):
        """Commit what is staged, through the hook unless told otherwise. Returns the finished process."""
        option = ["-c", "core.hooksPath=scripts/hooks"] if hook else ["-c", "core.hooksPath=no-such-folder"]
        return self.git(*option, "commit", "-qm", message, check=False, env=env)

    def cli(self, *args):
        return subprocess.run([sys.executable, str(self.root / "scripts" / "todo-graph.py"), *args], cwd=self.root,
                              capture_output=True, text=True, encoding="utf-8")

    def subjects(self):
        return self.git("log", "--format=%s").stdout.split()

    def make_plan_stale(self):
        """A second section in the TODO file, which the generated plan does not list yet."""
        self.write(todo_file(TWO))
        self.assertNotEqual(self.cli("plan", "--check").returncode, 0, "the fixture's plan did not go stale")


class Shipped(unittest.TestCase):
    def test_the_hook_is_committed_as_an_executable(self):
        # What a fresh clone on Linux or WSL gets is the mode git recorded, whatever this machine's filesystem says.
        listed = subprocess.run(["git", "-C", str(ROOT), "ls-files", "-s", "--", "scripts/hooks/pre-commit"],
                                capture_output=True, text=True, encoding="utf-8", check=True).stdout
        self.assertTrue(listed.startswith("100755 "), f"scripts/hooks/pre-commit is recorded as {listed.split(' ')[0]!r}; "
                        "run: git update-index --chmod=+x scripts/hooks/pre-commit")


class StalePlan(HookRepo):
    def test_a_todo_edit_with_a_stale_plan_is_refused_and_lands_once_synced(self):
        self.make_plan_stale()
        self.git("add", "todo")
        refused = self.commit("todo: add a section")
        self.assertNotEqual(refused.returncode, 0)
        self.assertIn("plan --sync", refused.stderr)
        self.assertEqual(self.subjects(), ["start"])
        self.assertEqual(self.cli("plan", "--sync").returncode, 0)
        self.git("add", "todo")
        landed = self.commit("todo: add a section")
        self.assertEqual(landed.returncode, 0, landed.stderr)
        self.assertEqual(self.subjects(), ["todo:", "add", "a", "section", "start"])

    def committed_stale(self):
        """A stale plan already in history (committed with no hook), and a clean tree."""
        self.make_plan_stale()
        self.git("add", "todo")
        self.assertEqual(self.commit("stale", hook=False).returncode, 0)

    def test_a_commit_touching_only_review_evidence_is_checked_too(self):
        self.committed_stale()
        (self.root / "docs" / "reviews" / "note.md").write_text("more evidence\n", encoding="utf-8")
        self.git("add", "docs/reviews")
        refused = self.commit("review: a round")
        self.assertNotEqual(refused.returncode, 0)
        self.assertIn("plan --sync", refused.stderr)

    def test_a_commit_touching_neither_folder_is_not_checked(self):
        self.committed_stale()
        (self.root / "README.md").write_text("changed\n", encoding="utf-8")
        self.git("add", "README.md")
        landed = self.commit("docs: readme")
        self.assertEqual(landed.returncode, 0, landed.stderr)

    def test_an_invalid_graph_is_refused(self):
        text = todo_file(ONE).replace("## Verification", "## 7. A section with no row in the table\n\n## Verification")
        self.write(text)
        self.assertNotEqual(self.cli("validate").returncode, 0, "the fixture's graph did not become invalid")
        self.git("add", "todo")
        refused = self.commit("todo: break the graph")
        self.assertNotEqual(refused.returncode, 0)
        self.assertEqual(self.subjects(), ["start"])


class WorkingTree(HookRepo):
    """What the check reads must be what is committed: nothing unstaged, untracked or ignored in either folder."""

    def tracked(self, folder):
        return self.root / ("todo/00-workspace/TODO-01-a.md" if folder == "todo/" else "docs/reviews/note.md")

    def stage_a_change(self, folder):
        path = self.tracked(folder)
        path.write_text(path.read_text(encoding="utf-8") + "\n", encoding="utf-8")
        self.git("add", str(path.relative_to(self.root)).replace(os.sep, "/"))

    def refused_naming(self, folder):
        refused = self.commit("change")
        self.assertNotEqual(refused.returncode, 0, f"{folder}: the commit landed")
        self.assertIn(f"{folder} has unstaged or untracked changes", refused.stderr)
        self.assertEqual(self.subjects(), ["start"])

    def test_an_unstaged_change_is_refused_in_each_folder(self):
        for folder in GUARDED:
            with self.subTest(folder=folder):
                self.stage_a_change(folder)
                path = self.tracked(folder)
                path.write_text(path.read_text(encoding="utf-8") + "\n", encoding="utf-8")  # and one more, not staged
                self.refused_naming(folder)
                self.git("reset", "-q", "--hard")

    def test_an_untracked_file_is_refused_in_each_folder(self):
        for folder in GUARDED:
            with self.subTest(folder=folder):
                self.stage_a_change(folder)
                extra = self.root / folder / "new.md"
                extra.write_text("not added\n", encoding="utf-8")
                self.refused_naming(folder)
                extra.unlink()
                self.git("reset", "-q", "--hard")

    def test_an_ignored_file_is_refused_in_each_folder(self):
        for folder in GUARDED:
            with self.subTest(folder=folder):
                self.stage_a_change(folder)
                extra = self.root / folder / "scratch.ignored"
                extra.write_text("git ignores this, the check would still read it\n", encoding="utf-8")
                self.assertEqual(self.git("status", "--porcelain", "--", folder + "scratch.ignored").stdout, "")
                self.refused_naming(folder)
                extra.unlink()
                self.git("reset", "-q", "--hard")


class Interpreter(HookRepo):
    """The hook takes the first Python that really runs: on Windows `python3` can be a stub that does not."""

    def stand_ins(self, python3, python):
        """A folder first on PATH with a `python3` and a `python` that each run the real interpreter or fail."""
        folder = Path(self.tmp.name + "-bin")
        folder.mkdir(exist_ok=True)
        self.addCleanup(shutil.rmtree, folder, True)
        real = Path(sys.executable).as_posix()
        for name, works in (("python3", python3), ("python", python)):
            script = folder / name
            script.write_text(f'#!/bin/sh\nexec "{real}" "$@"\n' if works else "#!/bin/sh\nexit 1\n",
                              encoding="utf-8", newline="\n")
            script.chmod(0o755)
        return {**os.environ, "PATH": str(folder) + os.pathsep + os.environ.get("PATH", "")}

    def staged_edit(self):
        self.make_plan_stale()
        self.assertEqual(self.cli("plan", "--sync").returncode, 0)
        self.git("add", "todo")

    def test_a_python3_that_does_not_run_falls_back_to_python(self):
        self.staged_edit()
        landed = self.commit("todo: add a section", env=self.stand_ins(python3=False, python=True))
        self.assertEqual(landed.returncode, 0, landed.stderr)

    def test_with_no_python_that_runs_the_commit_is_refused_saying_so(self):
        self.staged_edit()
        refused = self.commit("todo: add a section", env=self.stand_ins(python3=False, python=False))
        self.assertNotEqual(refused.returncode, 0)
        self.assertIn("Python is needed", refused.stderr)
        self.assertEqual(self.subjects(), ["start"])


class ProgressAndTiming(HookRepo):
    """D00 T07 §6: pre-commit prints its start line live before the check finishes and its measured elapsed time after."""

    def test_comment_cites_measured_saving(self):
        hook_text = (ROOT / "scripts" / "hooks" / "pre-commit").read_text(encoding="utf-8")
        self.assertIn("Measured 2026-11-23", hook_text)
        self.assertIn("19.7 s", hook_text)
        self.assertIn("36.4 s", hook_text)
        self.assertIn("16.7 s", hook_text)

    def test_pre_commit_announces_live_and_reports_measured_elapsed_seconds(self):
        # Stand-in todo-graph.py that blocks on a release flag so we observe `checking the plan...` live BEFORE exit.
        flag = self.root / "release.flag"
        for exit_code, word in ((0, "passed"), (1, "failed")):
            with self.subTest(word=word):
                if flag.exists():
                    flag.unlink()
                (self.root / "scripts" / "todo-graph.py").write_text(
                    "import pathlib, sys, time\n"
                    f"flag = pathlib.Path(r'{flag}')\n"
                    "deadline = time.monotonic() + 15\n"
                    "while not flag.exists() and time.monotonic() < deadline:\n"
                    "    time.sleep(0.05)\n"
                    f"sys.exit({exit_code})\n",
                    encoding="utf-8",
                    newline="\n",
                )
                (self.root / "docs" / "reviews" / "note.md").write_text(f"evidence {word}\n", encoding="utf-8")
                self.git("add", "docs/reviews/note.md")
                popen_kwargs = {}
                if os.name == "nt":
                    popen_kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
                else:
                    popen_kwargs["start_new_session"] = True
                proc = subprocess.Popen(
                    ["git", "-C", str(self.root), "-c", "user.name=t", "-c", "user.email=t@example.com",
                     "-c", "core.autocrlf=false", "-c", "core.hooksPath=scripts/hooks", "commit", "-qm", f"check {word}"],
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    encoding="utf-8",
                    **popen_kwargs,
                )
                lines = []
                saw_start = threading.Event()

                def reader():
                    assert proc.stderr is not None
                    for line in proc.stderr:
                        lines.append(line)
                        if "pre-commit: checking the plan..." in line:
                            saw_start.set()

                t = threading.Thread(target=reader, daemon=True)
                t.start()
                try:
                    self.assertTrue(saw_start.wait(10.0), f"start line not seen live; got: {''.join(lines)}")
                    self.assertIsNone(proc.poll(), "hook exited before release flag was written")
                    time.sleep(0.6)
                    flag.write_text("go", encoding="utf-8")
                    rc = proc.wait(timeout=15)
                finally:
                    if proc.poll() is None:
                        if os.name == "nt":
                            subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                                           capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
                        else:
                            os.killpg(proc.pid, signal.SIGKILL)
                        proc.wait(timeout=5)
                    t.join(timeout=5)
                    if proc.stdout:
                        proc.stdout.close()
                    if proc.stderr:
                        proc.stderr.close()
                stderr_text = "".join(lines)
                self.assertEqual(rc, exit_code, stderr_text)
                m = re.search(rf"pre-commit: plan check {word} in (\d+\.\d) s", stderr_text)
                self.assertIsNotNone(m, stderr_text)
                assert m is not None
                self.assertGreaterEqual(float(m.group(1)), 0.4, stderr_text)


if __name__ == "__main__":
    unittest.main()
