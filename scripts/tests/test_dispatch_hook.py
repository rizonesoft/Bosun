"""scripts/dispatch_hook.py: a review runs in the shell tool's background, never detached or on an edited pin."""
import importlib.util
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
spec = importlib.util.spec_from_file_location("dispatch_hook", ROOT / "scripts" / "dispatch_hook.py")
hook = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hook)

REVIEW = "python scripts/review-dispatch.py --writer claude --writer-model claude-opus-5-5 --prompt p.md --record f.md"


def payload(command, tool="Bash", background=None):
    tool_input = {"command": command}
    if background is not None:
        tool_input["run_in_background"] = background
    return {"tool_name": tool, "tool_input": tool_input}


class Clean(unittest.TestCase):
    """A clean pin, so only the launch form decides."""

    def setUp(self):
        patch = mock.patch.object(hook, "pin_dirty", return_value=False)
        patch.start()
        self.addCleanup(patch.stop)

    def test_detached_reviews_are_refused(self):
        for command, tool in ((REVIEW + " &", "Bash"), ("nohup " + REVIEW, "Bash"), (REVIEW + " & disown", "Bash"),
                              ("setsid " + REVIEW, "Bash"), (f"Start-Job {{ {REVIEW} }}", "PowerShell"),
                              (f"Start-Process python -ArgumentList 'scripts/review-dispatch.py --prompt p.md'", "PowerShell"),
                              (REVIEW + " &", "PowerShell")):
            with self.subTest(command):
                self.assertIn("not detached", hook.verdict(payload(command, tool, background=True)))

    def test_a_foreground_review_is_refused_and_a_background_one_passes(self):
        self.assertIn("run_in_background", hook.verdict(payload(REVIEW)))
        self.assertIsNone(hook.verdict(payload(REVIEW, background=True)))
        self.assertIsNone(hook.verdict(payload("python3 scripts/review-dispatch.py --prompt p.md 2>&1 | tail -5", background=True)))

    def test_python_s_own_options_and_a_background_separator_hide_nothing(self):
        for command in ("python -X utf8 scripts/review-dispatch.py --prompt p.md &", "python -W ignore -u scripts/review-dispatch.py --prompt p.md &",
                        "echo starting & python scripts/review-dispatch.py --prompt p.md &", "py -3.14 -B scripts/review-dispatch.py --prompt p.md &"):
            with self.subTest(command):
                self.assertIn("not detached", hook.verdict(payload(command, background=True)))
        for command in ("python -c \"print('scripts/review-dispatch.py')\"", "python -m pytest scripts/review-dispatch.py",
                        "cat scripts/review-dispatch.py", "python scripts/review-dispatch.py --check-pin 2>&1 | head"):
            with self.subTest(command):
                self.assertIsNone(hook.verdict(payload(command)))

    def test_each_command_is_read_in_its_own_shell(self):
        wrapped = "bash -c 'python scripts/review-dispatch.py --writer claude --prompt R:/work/review.md &'"
        self.assertIn("not detached", hook.verdict(payload(wrapped, "PowerShell", background=True)))
        quoted = 'python scripts/review-dispatch.py --writer claude --prompt "R:/work/research & review.md"'
        self.assertIsNone(hook.verdict(payload(quoted, background=True)))
        two_lines = "Write-Output starting\n& python scripts/review-dispatch.py --prompt R:/work/review.md"
        self.assertIsNone(hook.verdict(payload(two_lines, "PowerShell", background=True)))
        self.assertIn("not detached", hook.verdict(payload("Write-Output a; " + REVIEW + " &", "PowerShell", background=True)))

    def test_a_backgrounded_pipeline_and_a_continued_line_are_seen(self):
        self.assertIn("not detached", hook.verdict(payload(REVIEW + " | tee R:/work/review.log &", background=True)))
        self.assertIn("not detached", hook.verdict(payload(REVIEW + " \\\n&", background=True)))
        self.assertIn("not detached", hook.verdict(payload(REVIEW + " `\n&", "PowerShell", background=True)))
        self.assertIsNone(hook.verdict(payload(REVIEW + " | tee R:/work/review.log", background=True)))
        self.assertIsNone(hook.verdict(payload(REVIEW + " 2>&1 | & tee x.log", "PowerShell", background=True)))

    def test_a_backgrounded_list_and_a_group_are_seen(self):
        for command in (REVIEW + " && echo finished &", REVIEW + " || echo failed &", f"({REVIEW}) &",
                        f"( {REVIEW}; echo done ) &", f"{{ {REVIEW}; }} &", f"(cd . && {REVIEW}) &", f"({REVIEW} &)",
                        f"({REVIEW} && echo finished &)", f"(({REVIEW} | tee x) &)", f"echo a; ( b; ({REVIEW} || c) & )"):
            with self.subTest(command):
                self.assertIn("not detached", hook.verdict(payload(command, background=True)))
        for command in (REVIEW + " && echo finished", f"({REVIEW})", f"cd scripts; ({REVIEW}) ; echo done"):
            with self.subTest(command):
                self.assertIsNone(hook.verdict(payload(command, background=True)))

    def test_a_group_can_t_hide_a_stamp_from_the_pin_check(self):
        with mock.patch.object(hook, "pin_dirty", return_value=True):
            self.assertIn("reviewer pin", hook.verdict(payload("(python scripts/todo-graph.py stamp 'D00 T01 §3')")))

    def test_compound_commands_and_comments_hide_nothing(self):
        for command in ("if test -f p.md; then " + REVIEW + " & fi", "for x in 1; do " + REVIEW + " & done",
                        "while false; do :; done; ! " + REVIEW + " &", "if " + REVIEW + "; then echo ok; fi &"):
            with self.subTest(command):
                self.assertIn("not detached", hook.verdict(payload(command, background=True)))
        self.assertIn("run_in_background", hook.verdict(payload(REVIEW + " # --help describes the options")))
        self.assertIsNone(hook.verdict(payload("# " + REVIEW + " &\necho nothing ran")))
        self.assertIsNone(hook.verdict(payload(REVIEW + ' --prompt "a # b.md"', background=True)))

    def test_a_backgrounded_compound_command_and_a_heredoc_s_opening_line(self):
        for command in ("if true; then " + REVIEW + "; fi &", "while true; do " + REVIEW + "; break; done &",
                        "case x in x) " + REVIEW + ";; esac &", "if a; then if b; then " + REVIEW + "; fi; fi &",
                        REVIEW + " <<EOF &\nbody\nEOF"):
            with self.subTest(command):
                self.assertIn("not detached", hook.verdict(payload(command, background=True)))
        self.assertIsNone(hook.verdict(payload("if true; then " + REVIEW + "; fi", background=True)))
        self.assertIsNone(hook.verdict(payload("cat <<EOF\n" + REVIEW + " &\nEOF", background=True)))

    def test_prefix_options_and_their_values_are_read_past(self):
        for prefix in ("env -u CLAUDECODE", "env -uCLAUDECODE", "nice -n5", "env -i PATH=/usr/bin", "env --unset=X", "nice -n 5", "timeout 3600",
                       "timeout -s KILL 60", "sudo -u me", "stdbuf -o L", "nohup env -u X"):
            with self.subTest(prefix):
                self.assertIn("not detached", hook.verdict(payload(f"{prefix} {REVIEW} &", background=True)))
        self.assertIn("not detached", hook.verdict(payload(f"nohup env -u X {REVIEW}", background=True)))
        self.assertIn("not detached", hook.verdict(payload(f"env -S '{REVIEW}' &", background=True)))

    def test_an_escaped_quote_or_separator_ends_nothing(self):
        self.assertIn("not detached", hook.verdict(payload(REVIEW + ' --x "a\\";b" &', background=True)))
        self.assertIsNone(hook.verdict(payload(REVIEW + ' --x "a\\";b &"', background=True)))
        self.assertIsNone(hook.verdict(payload(REVIEW + " a\\&b", background=True)))

    def test_an_ansi_c_quoted_prompt_is_one_word(self):
        base = "python scripts/review-dispatch.py --writer claude --prompt $'review\\';notes.md'"
        self.assertIn("not detached", hook.verdict(payload(base + " &", background=True)))
        self.assertIsNone(hook.verdict(payload(base, background=True)))
        self.assertIsNone(hook.verdict(payload(base + " 2>&1 | tail -3", background=True)))

    def test_every_spelling_argparse_accepts_is_a_review(self):
        for args in ("--prompt=R:/work/p.md", "--pro p.md", "--writer claude --prompt p.md", "--writer=claude --prom=p.md"):
            command = f"python scripts/review-dispatch.py {args}"
            with self.subTest(args):
                self.assertIn("not detached", hook.verdict(payload(command + " &", background=True)))
                self.assertIn("run_in_background", hook.verdict(payload(command)))
        for args in ("--check-pin", "--check-pin --json", "--list-rounds", "--list --json", "-h", "--help"):
            with self.subTest(args):
                self.assertIsNone(hook.verdict(payload(f"python scripts/review-dispatch.py {args}")))

    def test_calls_that_run_no_review_pass(self):
        for command in ("python scripts/review-dispatch.py --check-pin", "python scripts/review-dispatch.py --list-rounds --json",
                        "python scripts/review-dispatch.py --help", 'git commit -m "run review-dispatch.py --prompt later"',
                        "cat <<EOF\nreview-dispatch.py --prompt x\nEOF", "git status && npm test"):
            with self.subTest(command):
                self.assertIsNone(hook.verdict(payload(command)))

    def test_powershell_s_background_ampersand_anywhere_is_refused(self):
        for command in (REVIEW + " & Write-Output launched", REVIEW + " &", REVIEW + "& ; Get-Date"):
            with self.subTest(command):
                self.assertIn("not detached", hook.verdict(payload(command, "PowerShell", background=True)))
        self.assertIsNone(hook.verdict(payload("& python scripts/review-dispatch.py --prompt p.md", "PowerShell", background=True)))
        self.assertIsNone(hook.verdict(payload(REVIEW + " 2>&1 | Select-Object -Last 5", "PowerShell", background=True)))

    def test_quoted_text_is_never_taken_for_a_review(self):
        for command in ('rg -n "python scripts/review-dispatch.py --prompt" docs',
                        "grep 'review-dispatch.py --prompt' -r scripts", 'echo "python scripts/todo-graph.py stamp x"'):
            with self.subTest(command):
                self.assertIsNone(hook.verdict(payload(command)))
        self.assertIn("run_in_background", hook.verdict(payload('bash -c "python scripts/review-dispatch.py --prompt p.md"')))

    def test_other_tools_pass(self):
        self.assertIsNone(hook.verdict({"tool_name": "Read", "tool_input": {"file_path": "x"}}))


class Pin(unittest.TestCase):
    """The pin is the owner's: a review or a stamp waits while it has uncommitted changes."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        git = lambda *a: subprocess.run(["git", "-C", str(self.repo), *a], check=True, capture_output=True)
        git("init", "-q")
        (self.repo / ".conclave").mkdir()
        (self.repo / ".conclave" / "reviewers.toml").write_text("schema_version = 1\n", encoding="utf-8")
        (self.repo / "docs" / "reviews").mkdir(parents=True)
        (self.repo / "docs" / "reviews" / "f.md").write_text("# Findings\n", encoding="utf-8")
        git("add", "-A")
        git("-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "x")

    def decide(self, command, background=True):
        return hook.verdict(payload(command, background=background), self.repo)

    def test_a_dirty_pin_is_seen_past_python_s_options(self):
        (self.repo / ".conclave" / "reviewers.toml").write_text("schema_version = 2\n", encoding="utf-8")
        self.assertIn("reviewer pin", self.decide("python -X utf8 scripts/todo-graph.py stamp 'D00 T01 §3'"))

    def test_a_dirty_pin_refuses_a_review_and_a_stamp_staged_or_not(self):
        stamp = "python scripts/todo-graph.py stamp 'D00 T01 §3' --sha abc"
        self.assertIsNone(self.decide(REVIEW))
        self.assertIsNone(self.decide(stamp))
        (self.repo / ".conclave" / "reviewers.toml").write_text("schema_version = 2\n", encoding="utf-8")
        for command in (REVIEW, stamp):
            with self.subTest(command):
                self.assertIn("reviewer pin is the owner's", self.decide(command))
        subprocess.run(["git", "-C", str(self.repo), "add", "-A"], check=True)
        self.assertIn("reviewer pin is the owner's", self.decide(stamp))

    def test_a_modified_findings_file_does_not_stop_the_campaign(self):
        (self.repo / "docs" / "reviews" / "f.md").write_text("# Findings\n\n- Planned: x\n", encoding="utf-8")
        self.assertIsNone(self.decide(REVIEW))
        self.assertIsNone(self.decide("python scripts/todo-graph.py stamp 'D00 T01 §3'"))


class ThreatModel(unittest.TestCase):
    def test_both_hooks_state_what_they_guard_against(self):
        for name in ("tool-guard.py", "dispatch_hook.py"):
            with self.subTest(name):
                text = " ".join((ROOT / "scripts" / name).read_text(encoding="utf-8").split())
                self.assertIn("Threat model: these hooks catch the campaign session's own honest mistakes", text)
                self.assertIn("rates P3", text)
                self.assertIn("D00 T03 §17", text)


class Process(unittest.TestCase):
    def run_hook(self, raw):
        return subprocess.run([sys.executable, str(ROOT / "scripts" / "dispatch_hook.py")], input=raw,
                              capture_output=True, text=True, timeout=60)

    def test_exit_2_refuses_with_the_reason_on_stderr(self):
        done = self.run_hook(json.dumps(payload(REVIEW + " &", background=True)))
        self.assertEqual(done.returncode, 2)
        self.assertIn("not detached", done.stderr)

    def test_a_hook_without_its_parser_says_so_and_lets_the_call_through(self):
        with tempfile.TemporaryDirectory() as folder:
            lone = Path(folder) / "dispatch_hook.py"
            lone.write_bytes((ROOT / "scripts" / "dispatch_hook.py").read_bytes())
            done = subprocess.run([sys.executable, str(lone)], input=json.dumps(payload(REVIEW + " &", background=True)),
                                  capture_output=True, text=True, timeout=60)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("couldn't load scripts/tool-guard.py", done.stderr)

    def test_a_malformed_payload_fails_open_with_a_message(self):
        for raw in ("", "not json", "[1]"):
            with self.subTest(raw):
                done = self.run_hook(raw)
                self.assertEqual(done.returncode, 0)
                self.assertIn("couldn't read its input", done.stderr)


if __name__ == "__main__":
    unittest.main()
