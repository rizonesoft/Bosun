"""Every project hook command must start Python where the interpreter is named python or python3."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


def find_bash():
    # On Windows a bare "bash" can be WSL's launcher, which would not see the temporary PATH.
    if os.name == "nt":
        path = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Git" / "bin" / "bash.exe"
        return str(path) if path.exists() else None
    return shutil.which("bash")


def hook_commands():
    settings = json.loads((ROOT / ".claude" / "settings.json").read_text(encoding="utf-8"))
    return sorted({hook["command"] for groups in settings["hooks"].values() for group in groups for hook in group["hooks"]})


@unittest.skipUnless(find_bash(), "needs bash")
class HookInterpreter(unittest.TestCase):
    def run_with_only(self, interpreter, command):
        with tempfile.TemporaryDirectory() as tmp:
            stub = Path(tmp) / interpreter
            stub.write_text('#!/bin/sh\necho "ran: $1"\n', newline="\n")
            stub.chmod(0o755)
            env = dict(os.environ, PATH=tmp, CLAUDE_PROJECT_DIR="project")
            return subprocess.run([find_bash(), "-c", command], env=env, capture_output=True, text=True, timeout=30)

    def test_every_hook_starts_under_either_name(self):
        commands = hook_commands()
        self.assertTrue(commands)
        for command in commands:
            for interpreter in ("python", "python3"):
                with self.subTest(command=command, interpreter=interpreter):
                    done = self.run_with_only(interpreter, command)
                    self.assertEqual(done.returncode, 0, done.stderr)
                    self.assertIn("ran: project/scripts/run-guard.py", done.stdout)


if __name__ == "__main__":
    unittest.main()
