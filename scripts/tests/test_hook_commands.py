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
                    self.assertRegex(done.stdout, r"ran: project/scripts/(run-guard|tool-guard|dispatch_hook)\.py")

    def test_the_deny_list_hook_sees_every_shell_and_connector_tool(self):
        settings = json.loads((ROOT / ".claude" / "settings.json").read_text(encoding="utf-8"))
        groups = {g["hooks"][0]["command"].rsplit("/", 1)[-1].strip('"'): g["matcher"] for g in settings["hooks"]["PreToolUse"]}
        self.assertEqual(groups, {"tool-guard.py": "Bash|PowerShell|mcp__.*", "dispatch_hook.py": "Bash|PowerShell"})

    def test_the_registered_dispatch_hook_refuses_a_detached_review_and_passes_a_background_one(self):
        settings = json.loads((ROOT / ".claude" / "settings.json").read_text(encoding="utf-8"))
        (command,) = [g["hooks"][0]["command"] for g in settings["hooks"]["PreToolUse"] if "dispatch_hook" in g["hooks"][0]["command"]]
        env = dict(os.environ, CLAUDE_PROJECT_DIR=ROOT.as_posix())
        review = "python scripts/review-dispatch.py --writer claude --prompt p.md"
        for tool_input, code in (({"command": review + " &"}, 2), ({"command": review}, 2),
                                 ({"command": review, "run_in_background": True}, 0), ({"command": "git status"}, 0)):
            payload = json.dumps({"tool_name": "Bash", "tool_input": tool_input})
            with self.subTest(tool_input):
                done = subprocess.run([find_bash(), "-c", command], input=payload, env=env, capture_output=True, text=True, timeout=60)
                self.assertEqual(done.returncode, code, done.stderr)


if __name__ == "__main__":
    unittest.main()
