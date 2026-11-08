"""A resumed session writes, builds through the queue and commits with no one there to answer a prompt.

It starts real Claude sessions, which spend subscription usage, so it runs only with BOSUN_E2E=1. The session is seeded
with its task, then resumed exactly as the watchdog resumes it: `resume_command` in bypassPermissions, with the
environment `child_env` gives it.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "lib"))
import run_guard as guard  # noqa: E402

BOSUN = Path(os.environ.get("LOCALAPPDATA", "")) / "Bosun" / "bin" / "bosun.exe"


@unittest.skipUnless(os.environ.get("BOSUN_E2E") == "1", "starts real Claude sessions, which spend subscription usage: set BOSUN_E2E=1")
class ResumedSession(unittest.TestCase):
    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.repo), *args], check=True, capture_output=True, text=True).stdout

    def test_a_resumed_session_writes_builds_through_bosun_and_commits_with_no_prompt(self):
        claude = shutil.which("claude")
        self.assertIsNotNone(claude, "claude is not on PATH")
        self.assertTrue(BOSUN.is_file(), f"no bosun at {BOSUN}")
        self.repo = Path(tempfile.mkdtemp(prefix="bosun-e2e-"))
        self.addCleanup(lambda: shutil.rmtree(self.repo, ignore_errors=True))
        self.git("init", "-q")
        self.git("config", "user.email", "e2e@example.com")
        self.git("config", "user.name", "E2E")
        self.git("commit", "-q", "--allow-empty", "-m", "start")
        env = guard.child_env(os.environ)
        env.pop("BOSUN_SLOT", None)  # so a slot in the output can only come from the session's own bosun run
        build = f'"{BOSUN}" run -- "{sys.executable}" -c "import os; open(\'build-output.txt\', \'w\').write(\'built in slot \' + os.environ[\'BOSUN_SLOT\'])"'
        task = ("This is an automated test. When you are next told `process plan`, do exactly these steps in this folder "
                "and nothing else, without asking anything:\n"
                "1. Create the file hello.txt containing exactly the single line: resumed and working\n"
                f"2. Run this command exactly as written: {build}\n"
                "3. Run: git add hello.txt build-output.txt\n"
                "4. Run: git commit -m \"e2e: resumed session\"\n"
                "Then reply DONE. For now, reply only READY and do nothing else.")
        seed = subprocess.run([claude, "-p", task, "--output-format", "json", "--permission-mode", "bypassPermissions"],
                              cwd=self.repo, env=env, capture_output=True, text=True, timeout=300, stdin=subprocess.DEVNULL)
        self.assertEqual(seed.returncode, 0, seed.stderr[-2000:])
        session = json.loads(seed.stdout)["session_id"]
        command = guard.resume_command({"session_id": session, "permission_mode": "bypassPermissions"}, claude)
        self.assertEqual(command[1:5], ["-p", "process plan", "--resume", session])
        done = subprocess.run(command, cwd=self.repo, env=env, capture_output=True, text=True, timeout=600,
                              stdin=subprocess.DEVNULL)
        print(f"\nresumed session exit {done.returncode}; last output: {done.stdout.strip()[-300:]}")
        self.assertEqual(done.returncode, 0, done.stderr[-2000:])
        self.assertEqual((self.repo / "hello.txt").read_text(encoding="utf-8").strip(), "resumed and working")
        built = (self.repo / "build-output.txt").read_text(encoding="utf-8")
        self.assertRegex(built, r"^built in slot \d+$", "the build didn't run inside a bosun slot")
        self.assertEqual(self.git("log", "-1", "--format=%s").strip(), "e2e: resumed session")
        tree = self.git("ls-tree", "--name-only", "HEAD").split()
        self.assertEqual(sorted(tree), ["build-output.txt", "hello.txt"])
        self.assertEqual(self.git("status", "--porcelain").strip(), "", "something was left uncommitted")
        print(f"hello.txt: {(self.repo / 'hello.txt').read_text(encoding='utf-8').strip()}; build-output.txt: {built}; "
              f"commit tree: {', '.join(sorted(tree))}")


if __name__ == "__main__":
    unittest.main()
