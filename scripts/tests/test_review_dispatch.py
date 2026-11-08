"""scripts/review-dispatch.py: the one route, read-only invocation, refusals and records, with fake reviewer CLIs."""
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap
import time
import tomllib
import unittest
import unittest.mock

SCRIPTS = Path(__file__).resolve().parents[1]
REPO_PIN = SCRIPTS.parent / ".conclave" / "reviewers.toml"


if sys.platform.startswith("linux"):
    # A subreaper: a process orphaned under this one, such as a reviewer whose dispatcher has just exited, is re-parented
    # here instead of to init, so it stays in this process's tree and can still be found and stopped.
    import ctypes
    ctypes.CDLL(None, use_errno=True).prctl(36, 1, 0, 0, 0)  # PR_SET_CHILD_SUBREAPER


def descendants(pid):
    """pid's descendants on Linux, found by parent: the dispatcher starts each reviewer in a session of its own, which
    killing the dispatcher's process group would miss."""
    children = {}
    for entry in os.listdir("/proc"):
        if entry.isdigit():
            try:
                with open(f"/proc/{entry}/stat") as stat:
                    parent = int(stat.read().rsplit(")", 1)[1].split()[1])
            except (OSError, ValueError, IndexError):
                continue  # it exited while being listed
            children.setdefault(parent, []).append(int(entry))
    tree, stack = [], [pid]
    while stack:
        tree.append(stack.pop())
        stack.extend(children.get(tree[-1], []))
    return set(tree[1:])


def kill_tree(pid, existing=frozenset()):
    """Stops pid and every descendant. On Linux that includes any orphan re-parented to this process since `existing`
    was taken, which a walk from pid alone would miss once pid's own process has exited."""
    if os.name == "nt":
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)], capture_output=True)
        return
    targets = {pid} | descendants(pid) | (descendants(os.getpid()) - set(existing))
    for member in targets:
        try:
            os.kill(member, 9)
        except OSError:
            pass  # already gone


def run_tree(argv, timeout, **kwargs):
    """subprocess.run with captured text output, except that a timeout stops the whole tree. run's own timeout kills
    only the direct child, so a fake reviewer the dispatcher had started kept running after its test: on 2026-11-08
    one flooding stderr wrote 287 GB and filled the disk. On Windows the dispatcher is still running when the timeout
    fires, so its tree is intact for taskkill /T."""
    existing = descendants(os.getpid()) if sys.platform.startswith("linux") else frozenset()
    group = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {"start_new_session": True}
    with subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, **group, **kwargs) as process:
        try:
            out, err = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            kill_tree(process.pid, existing)
            process.communicate()
            raise
    return subprocess.CompletedProcess(argv, process.returncode, out, err)

# One writer, one Codex reviewer, one route, no fallback. Most of the suite exercises the dispatcher against this
# fixture, never against the repository's own pin, which a project may change; MultiCli adds the other CLIs.
FIXTURE_PIN = textwrap.dedent('''    schema_version = 1

    [reviewers.sol]
    cli = "codex"
    model = "gpt-6.1-sol"
    effort = "xhigh"

    [routes]
    claude = ["sol"]
''')

# One fake reviewer for every CLI; a per-CLI launcher passes its name first. Launchers are a shell script on Linux and
# a .cmd file on Windows, so the suite runs natively on both.
FAKE_CLI = textwrap.dedent('''\
    import json, os, subprocess, sys, time
    name, args = sys.argv[1], sys.argv[2:]
    mode = os.environ.get("FAKE_" + name.upper(), "ok")
    if mode == "noread":
        time.sleep(30)
    # UTF-8, as the dispatcher writes both: the locale's code page would garble a settled line's "§".
    stdin = "" if sys.stdin is None or sys.stdin.isatty() else sys.stdin.buffer.read().decode("utf-8")
    pointer = "-p=Follow the review instructions in the file "
    from_file = None
    if args and args[-1].startswith(pointer):
        path = args[-1][len(pointer):].split(" exactly.")[0]
        from_file = open(path, encoding="utf-8").read() if os.path.isfile(path) else None
    marks = os.path.join(os.getcwd(), "build", "review-in-flight")
    with open(os.environ["FAKE_LOG"], "a") as log:
        log.write(json.dumps({"cli": name, "argv": args, "stdin": stdin, "cwd": os.getcwd(), "from_file": from_file,
                              "in_flight": sorted(os.listdir(marks)) if os.path.isdir(marks) else []}) + "\\n")
    if mode == "fail":
        sys.exit(3)
    if mode == "quiet":
        sys.stderr.write(os.environ.get("FAKE_STDERR", ""))
        if name == "codex":
            open(args[args.index("-o") + 1], "w").write(os.environ.get("FAKE_SAY", ""))
        sys.exit(0)
    if mode == "floodstderr":
        while True:
            sys.stderr.write("x" * 65536)
            sys.stderr.flush()
    if mode == "stderr":
        sys.stderr.write(os.environ["FAKE_STDERR"])
        sys.exit(1)
    if mode == "sleep":
        time.sleep(30)
    if mode == "flood":
        while True:
            sys.stdout.write("x" * 65536)
            sys.stdout.flush()
            time.sleep(0.01)
    if mode == "floodfile":
        with open(args[args.index("-o") + 1], "w") as out:
            while True:
                out.write("x" * 65536)
                out.flush()
                time.sleep(0.01)
    if mode == "orphan":
        child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
        open(os.environ["FAKE_CHILD_PID"], "w").write(str(child.pid))
    text = "" if mode == "empty" else "VERDICT: needs-attention, calc.py:2 subtracts. (" + name + ")"
    if mode == "headings":
        text = "## Round trip serialization\\n\\nFine.\\n\\n```\\n## Round 9: none\\n\\n- Writer: claude\\n- Run: 2026-01-01T00:00:00Z\\n```\\n\\nOVERALL: approve"
    if mode == "blocked":
        text = "## record: needs-attention\\n\\nI could not read the repository: setup refresh had errors.\\n\\nOVERALL: blocked"
    if mode == "completion":
        text = "## Completeness\\n\\n1. Ctrl+O: missing-in-scope.\\n2. Recent files: missing-out-of-scope.\\n\\n## Round 7: none\\n\\nCOMPLETION: gaps"
    if mode == "say":
        text = os.environ["FAKE_SAY"]
    if mode == "completionblocked":
        text = "I could not read the candidate commit.\\n\\nCOMPLETION: blocked"
    if name == "codex":
        open(args[args.index("-o") + 1], "w").write(text)
    else:
        print(text)
    if mode == "linger":
        # A process the reviewer started outlives it and holds the message file open for a moment, as a CLI's helper
        # process can; the reviewer exits first, so no tree kill reaches the helper.
        ready = os.environ["FAKE_CHILD_PID"] + ".ready"
        holder = "import sys, time; f = open(sys.argv[1], 'a'); open(sys.argv[2], 'w').close(); time.sleep(3)"
        subprocess.Popen([sys.executable, "-c", holder, args[args.index("-o") + 1], ready], cwd=os.path.dirname(sys.executable))
        while not os.path.exists(ready):
            time.sleep(0.05)
''')


def write_launcher(directory, name, fake):
    if os.name == "nt":
        path = directory / f"{name}.cmd"
        path.write_text(f'@"{sys.executable}" "{fake}" {name} %*\r\n', encoding="utf-8")
    else:
        path = directory / name
        path.write_text(f'#!/bin/sh\nexec "{sys.executable}" "{fake}" {name} "$@"\n', encoding="utf-8")
        path.chmod(0o755)
    return path


class Harness(unittest.TestCase):
    """A scratch repository with the dispatcher, a pin and a fake CLI on PATH; holds no tests itself."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.repo = Path(self._tmp.name) / "repo"
        (self.repo / "scripts").mkdir(parents=True)
        (self.repo / ".conclave").mkdir()
        shutil.copy(SCRIPTS / "review-dispatch.py", self.repo / "scripts/review-dispatch.py")
        (self.repo / ".conclave/reviewers.toml").write_text(FIXTURE_PIN, encoding="utf-8")
        self.bin = Path(self._tmp.name) / "bin"
        self.bin.mkdir()
        fake = Path(self._tmp.name) / "fake_cli.py"
        fake.write_text(FAKE_CLI, encoding="utf-8")
        self.codex = write_launcher(self.bin, "codex", fake)
        self.agy = write_launcher(self.bin, "agy", fake)
        self.claude = write_launcher(self.bin, "claude", fake)
        self.log = Path(self._tmp.name) / "calls.jsonl"
        self.scratch = Path(self._tmp.name) / "scratch"
        self.scratch.mkdir()
        self.prompt = self.repo / "prompt.md"
        self.prompt.write_text("Review calc.py. Do not modify any file.\n", encoding="utf-8")

    def tearDown(self):
        self._tmp.cleanup()

    def dispatch(self, *args, cwd=None, **modes):
        system_root = {"SYSTEMROOT": os.environ["SYSTEMROOT"]} if os.name == "nt" else {}
        env = {"PATH": f"{self.bin}{os.pathsep}{Path(sys.executable).parent}", "FAKE_LOG": str(self.log),
               "FAKE_CHILD_PID": str(Path(self._tmp.name) / "child.pid"), **system_root,
               **{var: str(self.scratch) for var in ("TMP", "TEMP", "TMPDIR")},
               **{f"FAKE_{k.upper()}": v for k, v in modes.items()}}
        result = run_tree([sys.executable, "-B", str(self.repo / "scripts/review-dispatch.py"), *args],
                          timeout=60, env=env, cwd=cwd)
        summary = json.loads(result.stdout) if result.stdout.strip().startswith("{") else None
        return result, summary

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def attempts(self):
        path = self.repo / "build" / "review-attempts.json"
        return json.loads(path.read_text(encoding="utf-8"))["passes"] if path.exists() else {}

    def elapse(self):
        """Stand in for a backoff having passed: every pass's not-before moves into the past."""
        path = self.repo / "build" / "review-attempts.json"
        if path.exists():
            data = json.loads(path.read_text(encoding="utf-8"))
            for entry in data["passes"].values():
                if "not_before" in entry:
                    entry["not_before"] = 0
            path.write_text(json.dumps(data), encoding="utf-8")

    def module(self):
        spec = importlib.util.spec_from_file_location("dispatch_under_test", self.repo / "scripts/review-dispatch.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module


def still_running(pid, wait=5):
    """Whether pid is still running after up to `wait` seconds. On Linux an orphan re-parented to this subreaper stays
    a zombie until it is reaped here, and a zombie still answers a signal check, so it is reaped first."""
    deadline = time.monotonic() + wait
    while time.monotonic() < deadline:
        if os.name == "nt":
            if str(pid) not in subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True).stdout:
                return False
        else:
            try:
                if os.waitpid(pid, os.WNOHANG) != (0, 0):
                    return False  # it was ours, and it has ended
            except ChildProcessError:
                try:
                    os.kill(pid, 0)
                except OSError:
                    return False
        time.sleep(0.1)
    return True


class HarnessTimeout(unittest.TestCase):
    def test_a_timeout_stops_the_grandchild_too(self):
        with tempfile.TemporaryDirectory() as tmp:
            pid_file = Path(tmp) / "grandchild.pid"
            script = ("import subprocess, sys, time\n"
                      # Started as the dispatcher starts a reviewer: output to files, so no pipe of ours waits for
                      # it, and on Linux in a session of its own, outside the harness's process group.
                      "extra = {} if sys.platform == 'win32' else {'start_new_session': True}\n"
                      "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'],\n"
                      "                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **extra)\n"
                      f"open({str(pid_file)!r}, 'w').write(str(child.pid))\n"
                      "time.sleep(60)\n")
            with self.assertRaises(subprocess.TimeoutExpired):
                run_tree([sys.executable, "-c", script], timeout=3)
            grandchild = int(pid_file.read_text())
            alive = still_running(grandchild)
            if alive:
                kill_tree(grandchild)
            self.assertFalse(alive, "the grandchild outlived the timeout")

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux re-parents orphans; Windows keeps no tree to re-parent")
    def test_a_reviewer_orphaned_by_its_dispatcher_is_still_stopped(self):
        existing = descendants(os.getpid())
        script = ("import subprocess, sys\n"
                  "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], start_new_session=True,\n"
                  "                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)\n"
                  "print(child.pid)\n")
        dispatcher = subprocess.Popen([sys.executable, "-c", script], stdout=subprocess.PIPE, text=True)
        orphan = int(dispatcher.communicate(timeout=30)[0])  # the dispatcher has exited: a walk from it finds nothing
        kill_tree(dispatcher.pid, existing)
        alive = still_running(orphan)
        if alive:
            os.kill(orphan, 9)
        self.assertFalse(alive, "the orphaned reviewer outlived kill_tree")


class Dispatch(Harness):
    RESULT = {"writer": "claude", "reviewer": "sol", "model": "gpt-6.1-sol", "cli": "codex", "effort": "xhigh",
              "failover": []}

    def test_sol_reviews_claude_work_read_only(self):
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((summary["reviewer"], summary["model"], summary["failover"]), ("sol", "gpt-6.1-sol", []))
        self.assertIn("(codex)", summary["review"])
        (call,) = self.calls()
        self.assertEqual(call["argv"][:3], ["exec", "-m", "gpt-6.1-sol"])
        for flag in ("--sandbox", "read-only", "--ephemeral", "model_reasoning_effort=xhigh"):
            self.assertIn(flag, call["argv"])
        self.assertEqual(call["argv"][-1], "-", "the prompt goes on stdin, never on the command line")
        self.assertEqual(call["stdin"], self.prompt.read_text())
        self.assertEqual(Path(call["cwd"]).resolve(), self.repo.resolve())

    def test_one_reviewer_and_no_fallback(self):
        # One reviewer, no fallback: when Sol returns no review, nothing else runs and the round is not an approval.
        for mode, reason in (("fail", "exited 3"), ("empty", "returned no review")):
            with self.subTest(mode):
                self.log.unlink(missing_ok=True)
                result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), codex=mode)
                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIsNone(summary["reviewer"])
                self.assertIsNone(summary["review"])
                self.assertEqual(summary["failover"], [{"reviewer": "sol", "reason": reason}])
                self.assertEqual([c["cli"] for c in self.calls()], ["codex"])

    def test_a_reviewer_that_could_not_reach_the_candidate_returned_no_review(self):
        # A reply ending OVERALL: blocked says the reviewer never saw the work (a sandbox that cannot read the
        # repository): it is not a review.
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), codex="blocked")
        self.assertEqual(result.returncode, 1)
        self.assertIsNone(summary["review"])
        self.assertTrue(summary["failover"][0]["reason"].startswith("could not review: "), summary["failover"])
        self.assertIn("setup refresh had errors", summary["failover"][0]["reason"])

    def test_a_missing_cli_is_no_review(self):
        self.codex.unlink()
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 1)
        self.assertEqual(summary["failover"], [{"reviewer": "sol", "reason": "codex is not installed"}])

    def test_a_timeout_is_no_review(self):
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--timeout", "2",
                                        codex="sleep")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(summary["failover"], [{"reviewer": "sol", "reason": "timed out"}])

    def test_no_review_is_never_an_approval(self):
        # A failed attempt writes nothing to the findings file: it is kept in build/, apart from the tree's reviews.
        record = self.repo / "docs/reviews/round.md"
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record),
                                        codex="fail")
        self.assertEqual(result.returncode, 1)
        self.assertIsNone(summary["reviewer"])
        self.assertFalse(record.exists())
        (entry,) = self.attempts().values()
        self.assertEqual((entry["count"], entry["last_class"]), (1, "other"))
        self.assertEqual(summary["attempt"]["attempt"], 1)

    def test_a_writer_without_a_route_is_refused_before_any_reviewer_runs(self):
        # The fixture routes only Claude, so Codex and Gemini work has no reviewer here and nothing runs.
        for writer in ("codex", "gemini"):
            with self.subTest(writer):
                result, _ = self.dispatch("--writer", writer, "--prompt", str(self.prompt))
                self.assertEqual(result.returncode, 2)
                self.assertIn(f"the reviewer pin has no route for writer {writer!r}", result.stderr)
        self.assertEqual(self.calls(), [])

    def test_rounds_are_numbered_and_appended(self):
        record = self.repo / "docs/reviews/round.md"
        record.parent.mkdir(parents=True)
        record.write_text("# Findings\n", encoding="utf-8")
        self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record))
        _, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record),
                                   codex="fail")
        self.assertNotIn("round", summary)  # nothing was recorded
        self.assertNotIn("## Round 2", record.read_text())
        self.elapse()
        _, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record))
        text = record.read_text()
        self.assertEqual(summary["round"], 2)
        self.assertTrue(text.startswith("# Findings\n"))
        self.assertIn("## Round 1: sol (`gpt-6.1-sol` via `codex`)", text)
        self.assertIn("## Round 2: sol (`gpt-6.1-sol` via `codex`)", text)
        self.assertNotIn(": none", text)
        self.assertEqual(self.attempts(), {}, "a returned review clears its pass")

    def check_pin(self, *args):
        result, _ = self.dispatch("--check-pin", *args)
        return result

    def test_check_pin_reports_a_valid_pin(self):
        result = self.check_pin("--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        module = self.module()
        pin = module.load_pin(self.repo / ".conclave/reviewers.toml")
        self.assertEqual((report["ok"], report["schema_version"]), (True, 1))
        self.assertEqual((report["reviewers"], report["routes"]), (pin["reviewers"], pin["routes"]))
        self.assertEqual(sorted(report["clis"]), sorted(module.CLIS))
        self.assertTrue(all(isinstance(v, bool) for v in report["clis"].values()))
        self.assertTrue(report["clis"]["codex"], "the fake codex launcher is on PATH")
        self.assertEqual(self.calls(), [], "--check-pin ran a reviewer")

    def test_check_pin_reports_a_refused_pin(self):
        module = self.module()
        pin = self.repo / ".conclave/reviewers.toml"
        good = pin.read_text()
        for bad in bad_pins(good) + (good.replace("schema_version = 1", "schema_version = true"), "not toml ["):
            with self.subTest(bad=bad[-40:]):
                self.assertNotEqual(bad, good, "the replacement must match the fixture, or the case tests nothing")
                pin.write_text(bad, encoding="utf-8")
                with self.assertRaises(module.Refused) as refused:
                    module.load_pin(pin)
                result = self.check_pin("--json")
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertEqual(json.loads(result.stdout), {"ok": False, "error": str(refused.exception)})
        self.assertEqual(self.calls(), [])

    def test_a_refused_shape_names_its_reason(self):
        # The strict loader names what is wrong: an effort on Antigravity, a fallback, a same-family reviewer.
        module, pin, good = self.module(), self.repo / ".conclave/reviewers.toml", FIXTURE_PIN
        mini = '\n[reviewers.mini]\ncli = "codex"\nmodel = "gpt-6-mini"\neffort = "low"\n'
        cases = {good.replace('cli = "codex"', 'cli = "agy"'):
                     "reviewer 'sol': Antigravity carries the effort in the model name, so set no effort",
                 good.replace('cli = "codex"', 'cli = "bash"'): "reviewer 'sol' needs cli = one of " + ", ".join(module.CLIS),
                 good.replace('claude = ["sol"]', 'claude = ["sol", "mini"]') + mini:
                     "route 'claude' must list exactly one reviewer",
                 good.replace('claude = ["sol"]', 'claude = []'): "route 'claude' must list exactly one reviewer",
                 good + 'opus = ["sol"]\n': "[routes] may only name the writers " + ", ".join(module.WRITERS),
                 good + 'codex = ["sol"]\n':
                     "route 'codex' names sol, of the writer's own family; a review comes from a different family",
                 good.replace('effort = "xhigh"', 'effort = "xhigh"\nsandbox = "workspace-write"'):
                     "reviewer 'sol' has unknown keys: sandbox"}
        for text, message in cases.items():
            with self.subTest(message=message, text=text[-30:]):
                pin.write_text(text, encoding="utf-8")
                with self.assertRaises(module.Refused) as refused:
                    module.load_pin(pin)
                self.assertEqual(str(refused.exception), message)

    def test_a_reviewer_of_the_writers_own_family_is_refused(self):
        # The fixture's route is cross-family; the rule in load_pin is proved by giving the writer the reviewer's family.
        module = self.module()
        with unittest.mock.patch.dict(module.WRITER_FAMILY, {"claude": "openai"}):
            with self.assertRaises(module.Refused) as refused:
                module.load_pin(self.repo / ".conclave/reviewers.toml")
        self.assertEqual(str(refused.exception),
                         "route 'claude' names sol, of the writer's own family; a review comes from a different family")
        module.load_pin(self.repo / ".conclave/reviewers.toml")

    def test_a_pin_that_cannot_be_read_is_refused_not_a_crash(self):
        pin = self.repo / ".conclave/reviewers.toml"
        cases = {"directory": lambda: (pin.unlink(), pin.mkdir()),
                 "not UTF-8": lambda: pin.write_bytes(b"schema_version = 1\n# \xff\n"),
                 "nested too deep": lambda: pin.write_text("x = " + "[" * 5000 + "0" + "]" * 5000 + "\n"),
                 "integer too long": lambda: pin.write_text("schema_version = " + "9" * 5000 + "\n")}
        for name, make in cases.items():
            with self.subTest(name):
                if pin.is_dir():
                    pin.rmdir()
                make()
                result = self.check_pin("--json")
                self.assertEqual(result.returncode, 2, result.stderr)
                report = json.loads(result.stdout)
                self.assertFalse(report["ok"])
                self.assertTrue(report["error"].startswith("unreadable reviewer pin"), report["error"])
                review, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
                self.assertEqual(review.returncode, 2, review.stderr)
                self.assertIn("review-dispatch refused: unreadable reviewer pin", review.stderr)
        self.assertEqual(self.calls(), [])

    def test_check_pin_reports_a_missing_pin(self):
        (self.repo / ".conclave/reviewers.toml").unlink()
        result = self.check_pin("--json")
        self.assertEqual(result.returncode, 2)
        self.assertTrue(json.loads(result.stdout)["error"].startswith("no reviewer pin at"), result.stdout)

    def test_check_pin_without_json_prints_one_line(self):
        result = self.check_pin()
        self.assertEqual((result.returncode, result.stdout), (0, "ok\n"))
        pin = self.repo / ".conclave/reviewers.toml"
        pin.write_text(pin.read_text().replace('effort = "xhigh"', 'effort = "extreme"'), encoding="utf-8")
        result = self.check_pin()
        self.assertEqual(result.returncode, 2)
        self.assertEqual(result.stdout, "refused: reviewer 'sol' needs effort = one of low, medium, high, xhigh\n")

    def test_check_pin_needs_no_writer_or_prompt(self):
        self.assertEqual(self.check_pin("--json").returncode, 0)
        result, _ = self.dispatch("--writer", "claude")
        self.assertEqual(result.returncode, 2, "a review without --prompt must still be refused")
        self.assertIn("--writer and --prompt are required", result.stderr)

    def test_list_rounds_reads_back_what_record_wrote(self):
        # A reviewed round written by record itself, then a failed attempt, which record no longer writes. Rounds
        # with no review that older files hold are still read: see the test below.
        record = self.repo / "docs/reviews/00-design/D00-T01-s1.md"
        self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record))
        self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record), codex="fail")
        result, listing = self.dispatch("--list-rounds", "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        (only,) = listing["records"]
        self.assertEqual(only["path"], "docs/reviews/00-design/D00-T01-s1.md")
        (reviewed,) = only["rounds"]
        self.assertEqual({k: reviewed[k] for k in ("number", "reviewer", "model", "cli", "writer", "effort", "skipped")},
                         {"number": 1, "reviewer": "sol", "model": "gpt-6.1-sol", "cli": "codex", "writer": "claude",
                          "effort": "xhigh", "skipped": []})
        self.assertRegex(reviewed["run"], r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")
        self.assertNotIn("unreadable", listing)

    def test_list_rounds_still_reads_rounds_recorded_under_earlier_reviewers(self):
        # Older findings files may hold rounds by other reviewers, a fallback chain and its Skipped lines.
        old = self.repo / "docs/reviews/old.md"
        old.parent.mkdir(parents=True)
        old.write_text("## Round 1: gemini (`gemini-4-argon-medium` via `agy`)\n\n- Writer: claude\n"
                       "- Run: 2026-10-21T00:00:00Z\n- Skipped: astra (exited 3)\n\nReview.\n", encoding="utf-8")
        result, listing = self.dispatch("--list-rounds", "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        (round_,) = listing["records"][0]["rounds"]
        self.assertEqual((round_["reviewer"], round_["cli"], round_["skipped"]),
                         ("gemini", "agy", [{"reviewer": "astra", "reason": "exited 3"}]))

    def test_list_rounds_with_no_records_and_with_an_unreadable_heading(self):
        result, listing = self.dispatch("--list-rounds", "--json")
        self.assertEqual((result.returncode, listing), (0, {"records": []}))
        good = self.repo / "docs/reviews/a.md"
        self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(good))
        bad = self.repo / "docs/reviews/b.md"
        bad.write_text("# Findings\n\n## Round two: sol\n\n- Writer: claude\n", encoding="utf-8")
        result, listing = self.dispatch("--list-rounds", "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([r["path"] for r in listing["records"]], ["docs/reviews/a.md"])
        self.assertEqual(listing["unreadable"], ["docs/reviews/b.md"])
        self.assertEqual(len(self.calls()), 1, "listing ran a reviewer")
        result, _ = self.dispatch("--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 2, "a review without --writer must still be refused")

    def test_review_text_that_looks_like_a_round_is_neither_counted_nor_listed(self):
        # A review quoting `## Round ...` lines, one a whole fake round in a fence, round-trips through record.
        record = self.repo / "docs/reviews/r.md"
        self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record), codex="headings")
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record))
        self.assertEqual(summary["round"], 2, "record counted review text as a round")
        text = record.read_text(encoding="utf-8")
        # Stored byte for byte: no escape inside or outside the fence.
        self.assertIn("\n## Round trip serialization\n", text)
        self.assertIn("```\n## Round 9: none\n", text)
        _, listing = self.dispatch("--list-rounds", "--json")
        self.assertEqual([r["number"] for r in listing["records"][0]["rounds"]], [1, 2])
        self.assertNotIn("unreadable", listing)

    def test_a_writer_note_under_a_reserved_heading_is_refused_by_name(self):
        # A writer's own `## Round budget` note must not make a returned review vanish with a bare "cannot be read
        # back": the refusal names the line, and says the prefixes are reserved for --record.
        module = self.module()
        path = self.repo / "docs/reviews/n.md"
        module.record(path, {**self.RESULT, "review": "Plain."}, "2026-10-23T00:00:00Z")
        path.write_text(path.read_text(encoding="utf-8") + "\n## Round budget (writer)\n\nCap 33.\n", encoding="utf-8")
        with self.assertRaises(module.Unrecorded) as caught:
            module.record(path, {**self.RESULT, "review": "Next."}, "2026-10-23T00:00:01Z")
        message = str(caught.exception)
        self.assertIn("'## Round budget (writer)'", message)
        self.assertIn("reserved for --record", message)
        self.assertRegex(message, r"line [0-9]+:")
        with self.assertRaises(module.Unrecorded) as caught:
            module.record_completion(path, {**self.RESULT, "review": "Gaps."}, "2026-10-23T00:00:02Z", "a" * 40)
        self.assertIn("'## Round budget (writer)'", str(caught.exception))
        # A readable file names nothing.
        self.assertEqual(module.unreadable_heading("# Findings\n\n## Writer note\n\nText.\n"), "")

    def test_the_line_count_envelope_holds_against_other_line_breaks_and_hand_edits(self):
        module = self.module()
        path = self.repo / "docs/reviews/u.md"
        # U+2028 and U+0085 split lines for str.splitlines but not for record: a fake round behind them stays text.
        module.record(path, {**self.RESULT, "review": "Quoted ## Round 9: none\n\n- Writer: codex\n- Run: now\u0085x"},
                      "2026-10-20T00:00:00Z")
        module.record(path, {**self.RESULT, "review": "Plain."}, "2026-10-20T00:00:01Z")
        self.assertEqual([r["number"] for r in module.read_rounds(path.read_text(encoding="utf-8"))], [1, 2])
        # A round recorded before the count (no Lines field) still reads, running to the next heading.
        legacy = ("## Round 1: astra (`gpt-6-astra` via `codex`)\n\n- Writer: claude\n- Run: 2026-10-16T00:00:00Z\n\n"
                  "Review.\n\n")
        self.assertEqual([r["number"] for r in module.read_rounds(legacy)], [1])
        # A hand edit that changes the review's line count is refused, not misread.
        text = path.read_text(encoding="utf-8")
        self.assertIsNone(module.read_rounds(text.replace("- Lines: 1", "- Lines: 3")))
        self.assertIsNone(module.read_rounds(text.replace("Plain.", "Plain.\nAdded by hand.")))

    def test_a_lines_value_that_is_not_ascii_digits_is_unreadable_not_a_crash(self):
        # D06 T01 §8 (1): "²" passes str.isdigit and int() raises; so does a count past Python's digit limit.
        good = self.repo / "docs/reviews/good.md"
        self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(good))
        recorded = good.read_text(encoding="utf-8")
        for name, value in (("superscript.md", "²"), ("huge.md", "9" * 5000)):
            body = re.sub(r"- Lines: \d+", "- Lines: " + value, recorded)
            self.assertNotEqual(body, recorded, name)
            (self.repo / "docs/reviews" / name).write_text(body, encoding="utf-8")
        result, listing = self.dispatch("--list-rounds", "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([r["path"] for r in listing["records"]], ["docs/reviews/good.md"])
        self.assertEqual(sorted(listing["unreadable"]), ["docs/reviews/huge.md", "docs/reviews/superscript.md"])

    def test_an_edit_inside_a_review_that_keeps_its_line_count_is_refused(self):
        # D06 T01 §8 (2): the count alone cannot see an edit of the same length; the digest can.
        module, path = self.module(), self.repo / "docs/reviews/d.md"
        module.record(path, {**self.RESULT, "review": "Finding one.\nOVERALL: needs-attention"}, "2026-10-20T00:00:00Z")
        text = path.read_bytes().decode("utf-8")
        self.assertRegex(text, r"\n- Digest: [0-9a-f]{16}\n")
        self.assertEqual(len(module.read_rounds(text)), 1)
        self.assertIsNone(module.read_rounds(text.replace("needs-attention", "approve")))
        # A CRLF checkout of the same file still reads, and its digest still matches.
        self.assertEqual(len(module.read_rounds(text.replace("\n", "\r\n"))), 1)

    def test_a_review_is_stored_byte_for_byte_and_an_append_keeps_it_readable(self):
        # D06 T01 §8 (3): a CRLF pair and a lone CR inside a review are stored as they came, and a second round
        # appended after them does not turn the lone CR into a line break that breaks the first round's count.
        module, path = self.module(), self.repo / "docs/reviews/b.md"
        module.record(path, {**self.RESULT, "review": "A\r\nB\rC\nD"}, "2026-10-20T00:00:00Z")
        self.assertIn(b"\nA\r\nB\rC\nD\n", path.read_bytes())
        module.record(path, {**self.RESULT, "review": "Second."}, "2026-10-20T00:00:01Z")
        raw = path.read_bytes()
        self.assertIn(b"\nA\r\nB\rC\nD\n", raw)
        self.assertEqual([r["number"] for r in module.read_rounds(raw.decode("utf-8"))], [1, 2])

    def test_a_model_or_name_ending_in_a_newline_is_refused(self):
        # D06 T01 §8 (4): `$` in MODEL and NAME accepted a trailing newline, which then broke the round heading.
        pin = self.repo / ".conclave/reviewers.toml"
        good = pin.read_text(encoding="utf-8")
        cases = {'model = "gpt-6.1-sol\\n"': "reviewer 'sol' has a malformed model",
                 '[reviewers."sol\\n"]': "invalid reviewer 'sol\\n'"}
        for edit, message in cases.items():
            with self.subTest(edit):
                old = 'model = "gpt-6.1-sol"' if edit.startswith("model") else "[reviewers.sol]"
                pin.write_text(good.replace(old, edit), encoding="utf-8")
                result = self.check_pin("--json")
                self.assertEqual(result.returncode, 2, result.stdout)
                self.assertEqual(json.loads(result.stdout)["error"], message)
                review, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
                self.assertEqual(review.returncode, 2, review.stderr)
        self.assertEqual(self.calls(), [])

    def test_record_refuses_to_append_to_a_findings_file_it_cannot_read(self):
        # D06 T01 §8 (5): numbering an unreadable file by counting headings counted quoted ones; now nothing is
        # appended, the review still reaches stdout, and the exit is 2.
        bad = self.repo / "docs/reviews/bad.md"
        bad.parent.mkdir(parents=True)
        body = "# Findings\n\n## Round two: sol\n\n- Writer: claude\n"
        bad.write_bytes(body.encode("utf-8"))
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(bad))
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertEqual(bad.read_bytes(), body.encode("utf-8"), "record appended to a file it cannot read")
        self.assertIn("calc.py:2 subtracts", summary["review"])
        self.assertIsNone(summary["round"])
        self.assertIn("cannot be read back", result.stderr)

    def test_a_cli_in_the_working_directory_is_not_on_path(self):
        # D01 T01 §5: Windows shutil.which looks in the working directory first unless NoDefaultCurrentDirectoryInExePath
        # is set, so a codex committed to the reviewed repository was reported as installed and run as the reviewer.
        # The dispatch() environment never sets that variable, so this runs as a plain Windows shell would.
        self.codex.unlink()
        planted = self.repo / ("codex.cmd" if os.name == "nt" else "codex")
        marker = self.repo / "planted-ran.txt"
        if os.name == "nt":
            planted.write_text(f'@echo planted> "{marker}"\r\n', encoding="utf-8")
        else:
            planted.write_text(f'#!/bin/sh\necho planted > "{marker}"\n', encoding="utf-8")
            planted.chmod(0o755)
        result, _ = self.dispatch("--check-pin", "--json", cwd=self.repo)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(json.loads(result.stdout)["clis"]["codex"], "a codex in the working directory counted as on PATH")
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), cwd=self.repo)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertFalse(marker.exists(), "the planted codex in the working directory ran as the reviewer")
        self.assertEqual(summary["failover"], [{"reviewer": "sol", "reason": "codex is not installed"}])

    @unittest.skipUnless(os.name == "nt", "drive-relative paths, PATHEXT and extensionless lookups are Windows rules")
    def test_find_cli_on_windows_paths(self):
        # Review round 1 of D01 T01 §5: a drive-relative entry (\work\repo) passed os.path.isabs on Python 3.11 and
        # 3.12; an empty PATHEXT found nothing; an extensionless executable was no longer found.
        module = self.module()
        folder = Path(self._tmp.name) / "clis"
        folder.mkdir()
        (folder / "codex.cmd").write_text("@exit /b 0\r\n", encoding="utf-8")
        drive_relative = str(folder)[2:]  # "R:\x\clis" -> "\x\clis", resolved against the current drive
        with unittest.mock.patch.dict(os.environ, {"PATH": drive_relative, "PATHEXT": ".CMD"}):
            self.assertIsNone(module.find_cli("codex"), "a drive-relative PATH entry was searched")
        with unittest.mock.patch.dict(os.environ, {"PATH": str(folder), "PATHEXT": ""}):
            self.assertEqual(module.find_cli("codex"), str(folder / "codex.CMD"), "an empty PATHEXT found nothing")
        # Round 2: a PATHEXT entry is an extension, never a path that climbs out of the PATH directory.
        outside = Path(self._tmp.name) / "reviewed"
        outside.mkdir()
        (outside / "codex.cmd").write_text("@exit /b 0\r\n", encoding="utf-8")
        (folder / "codex").mkdir()
        with unittest.mock.patch.dict(os.environ, {"PATH": str(folder), "PATHEXT": "\\..\\..\\reviewed\\codex.cmd"}):
            # The malformed entry is dropped and the defaults find the codex.cmd inside the PATH directory.
            self.assertEqual(module.find_cli("codex"), str(folder / "codex.CMD"), "a PATHEXT entry led outside PATH")
        (folder / "codex").rmdir()
        # npm puts an extensionless shell script beside codex.cmd; Windows cannot run the script, so .cmd comes first,
        # and the bare name is found only when no PATHEXT candidate is there.
        (folder / "codex").write_text("#!/bin/sh\n", encoding="utf-8")
        with unittest.mock.patch.dict(os.environ, {"PATH": str(folder), "PATHEXT": ".EXE;.CMD"}):
            self.assertEqual(module.find_cli("codex"), str(folder / "codex.CMD"))
            (folder / "codex.cmd").unlink()
            self.assertEqual(module.find_cli("codex"), str(folder / "codex"), "an extensionless executable was lost")

    def test_a_round_number_past_the_digit_limit_is_unreadable_and_keeps_the_review(self):
        # Review round 1 of D06 T01 §8: a 5,000-digit round number made the reader's int() raise, so record lost the
        # finished review's output and the listing would abort. The reader never raises; the file is just unreadable.
        bad = self.repo / "docs/reviews/huge-number.md"
        bad.parent.mkdir(parents=True)
        body = "## Round " + "9" * 5000 + ": none\n\n- Writer: claude\n- Run: 2026-10-20T00:00:00Z\n"
        bad.write_bytes(body.encode("utf-8"))
        self.assertIsNone(self.module().read_rounds(body))
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(bad))
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertIn("calc.py:2 subtracts", summary["review"])
        self.assertEqual(bad.read_bytes(), body.encode("utf-8"))
        result, listing = self.dispatch("--list-rounds", "--json")
        self.assertEqual((result.returncode, listing["unreadable"]), (0, ["docs/reviews/huge-number.md"]))

    def test_the_largest_review_record_writes_is_one_its_reader_reads(self):
        # Review round 1 of D06 T01 §8: record wrote a 7-digit Lines count the 6-digit reader refused. The largest
        # review a reviewer can return (MAX_OUTPUT_BYTES of newlines) must read back and take another round.
        module, path = self.module(), self.repo / "docs/reviews/large.md"
        module.record(path, {**self.RESULT, "review": "\n" * module.MAX_OUTPUT_BYTES}, "2026-10-20T00:00:00Z")
        module.record(path, {**self.RESULT, "review": "Second."}, "2026-10-20T00:00:01Z")
        self.assertEqual([r["number"] for r in module.read_rounds(path.read_bytes().decode("utf-8"))], [1, 2])

    @unittest.skipIf(os.name == "nt", "creating a symlink needs a privilege Windows does not grant by default")
    def test_a_symlink_loop_is_reported_without_aborting_the_listing(self):
        good = self.repo / "docs/reviews/good.md"
        self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(good))
        os.symlink("loop.md", self.repo / "docs/reviews/loop.md")
        result, listing = self.dispatch("--list-rounds", "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([r["path"] for r in listing["records"]], ["docs/reviews/good.md"])
        self.assertEqual(listing["unreadable"], ["docs/reviews/loop.md"])

    def test_a_malformed_or_unreadable_record_is_named_and_the_rest_still_listed(self):
        good = self.repo / "docs/reviews/good.md"
        self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(good))
        recorded = good.read_text(encoding="utf-8")
        cases = {
            "empty-effort.md": recorded.replace("- Effort: xhigh", "- Effort:\n- Skipped: x (y)"),
            "two-writers.md": recorded.replace("- Writer: claude", "- Writer: claude\n- Writer: codex"),
            "no-blank.md": recorded.replace("via `codex`)\n\n", "via `codex`)\n"),
        }
        for name, body in cases.items():
            self.assertNotEqual(body, recorded, name)
            (self.repo / "docs/reviews" / name).write_text(body, encoding="utf-8")
        (self.repo / "docs/reviews/not-utf8.md").write_bytes(recorded.replace("claude", "claud\udcff", 1)
                                                              .encode("utf-8", "surrogateescape"))
        (self.repo / "docs/reviews/folder.md").mkdir()  # a path that cannot be read as a file
        result, listing = self.dispatch("--list-rounds", "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([r["path"] for r in listing["records"]], ["docs/reviews/good.md"])
        self.assertEqual(sorted(listing["unreadable"]), sorted(
            f"docs/reviews/{n}" for n in (*cases, "not-utf8.md", "folder.md")))

    def test_a_runaway_reviewer_is_stopped_while_it_runs(self):
        started = time.monotonic()
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--timeout", "40",
                                        codex="flood")
        self.assertLess(time.monotonic() - started, 30, "the cap was only applied at the timeout")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(summary["failover"], [{"reviewer": "sol", "reason": "returned more than 2000000 bytes"}])

    def test_codex_flooding_its_message_file_is_stopped_while_it_runs(self):
        started = time.monotonic()
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--timeout", "40",
                                        codex="floodfile")
        self.assertLess(time.monotonic() - started, 30, "the cap was only applied at the timeout")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(summary["failover"], [{"reviewer": "sol", "reason": "returned more than 2000000 bytes"}])

    def test_a_file_still_held_after_the_reviewer_exits_does_not_cost_the_round(self):
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), codex="linger")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(summary["reviewer"], "sol")
        self.assertIn("(codex)", summary["review"])
        self.assertNotIn("left", result.stderr)
        self.assertEqual(list(self.scratch.iterdir()), [], "the reviewer's scratch directory was not removed")

    def test_a_reviewer_that_never_reads_a_large_prompt_still_times_out(self):
        # Larger than any pipe buffer: a blocking write here would hang past the timeout.
        self.prompt.write_text("Review this.\n" + "x" * 1_000_000, encoding="utf-8")
        started = time.monotonic()
        _, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--timeout", "3", codex="noread")
        self.assertLess(time.monotonic() - started, 20)
        self.assertEqual(summary["failover"], [{"reviewer": "sol", "reason": "timed out"}])

    def test_a_prompt_with_a_nul_byte_is_refused_before_any_reviewer_runs(self):
        self.prompt.write_text("Review this.\x00", encoding="utf-8")
        result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 2)
        self.assertIn("NUL byte", result.stderr)
        self.assertEqual(self.calls(), [])

    @unittest.skipIf(os.name == "nt", "Windows cannot reach a child once its reviewer has exited (see kill_tree)")
    def test_a_reviewers_children_do_not_outlive_it(self):
        _, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), codex="orphan")
        self.assertEqual(summary["reviewer"], "sol")
        pid = int((Path(self._tmp.name) / "child.pid").read_text())
        for _ in range(50):
            try:
                os.kill(pid, 0)
            except ProcessLookupError:
                return
            time.sleep(0.1)
        os.kill(pid, 9)
        self.fail("the reviewer's child process was still running")

    def test_the_prompt_never_reaches_the_command_line(self):
        # A Windows .cmd shim re-parses its arguments through cmd.exe, so prose there could become commands.
        argv, stdin = self.module().command({"cli": "codex", "model": "gpt-6.1-sol", "effort": "xhigh"},
                                            "C:/tools/codex.cmd", "x & del *", Path("C:/p.md"), Path("o"), 60)
        self.assertEqual(argv[-1], "-")
        self.assertEqual(stdin, "x & del *")
        self.assertEqual(argv[argv.index("-o") + 1], "o")

    def test_a_reviewer_that_cannot_start_is_no_review(self):
        # Installed, but not a program the system can start, so the launch itself fails.
        self.codex.unlink()
        if os.name == "nt":
            # A real program with execute denied: CreateProcess refuses it at once. A file that is not a valid image
            # would be run as a 16-bit program instead, and Windows blocks the launch on a modal "Unsupported 16-Bit
            # Application" dialog until someone dismisses it.
            target = self.bin / "codex.exe"
            shutil.copyfile(Path(os.environ["SystemRoot"]) / "System32" / "where.exe", target)
            subprocess.run(["icacls", str(target), "/deny", "*S-1-1-0:(X)"], capture_output=True, check=True)
        else:
            write_launcher(self.bin, "codex", "unused").write_text("#!/nonexistent/interpreter\n", encoding="utf-8")
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 1)
        self.assertTrue(summary["failover"][0]["reason"].startswith("could not start"), summary["failover"])

    def test_a_non_integer_schema_version_is_refused(self):
        # TOML true is Python True and 1.0 == 1, so both passed a `!= 1` check and loaded.
        pin = self.repo / ".conclave/reviewers.toml"
        good = pin.read_text()
        for bad in ("schema_version = true", "schema_version = 1.0"):
            with self.subTest(bad=bad):
                pin.write_text(good.replace("schema_version = 1", bad), encoding="utf-8")
                result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertIn("needs exactly schema_version = 1, [reviewers.*] and [routes]", result.stderr)
                self.assertEqual(self.calls(), [])

    def test_a_bad_pin_refuses_before_any_reviewer_runs(self):
        pin = self.repo / ".conclave/reviewers.toml"
        good = pin.read_text()
        for bad in bad_pins(good):
            with self.subTest(bad=bad[-60:]):
                self.assertNotEqual(bad, good, "the replacement must match the fixture, or the case tests nothing")
                pin.write_text(bad, encoding="utf-8")
                result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
                self.assertEqual(result.returncode, 2)
                self.assertEqual(self.calls(), [])
        pin.unlink()
        result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 2)
        self.assertIn("no reviewer pin", result.stderr)


    CANDIDATE = "0123456789abcdef0123456789abcdef01234567"

    def complete(self, record, *extra, **modes):
        return self.dispatch("--kind", "completion", "--candidate", self.CANDIDATE, "--writer", "claude", "--prompt",
                             str(self.prompt), "--record", str(record), *extra, **modes)

    def test_a_completion_review_is_recorded_as_its_own_section_and_not_counted_as_a_round(self):
        record = self.repo / "docs/reviews/00-workspace/D00-T01-s1.md"
        result, summary = self.complete(record, codex="completion")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((summary["kind"], summary["candidate"], summary["recorded"]),
                         ("completion", self.CANDIDATE, True))
        text = record.read_text(encoding="utf-8")
        self.assertIn("## Completion review: sol (`gpt-6.1-sol` via `codex`)\n", text)
        self.assertIn(f"\n- Candidate: {self.CANDIDATE}\n", text)
        self.assertNotRegex(text, r"(?m)^## Round 1:")
        # The first regular round after it is round 1, and the round quoted inside the completion review is not one.
        _, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record))
        self.assertEqual(summary["round"], 1)
        module = self.module()
        found = module.read_findings(record.read_bytes().decode("utf-8"))
        self.assertEqual([r["number"] for r in found["rounds"]], [1])
        (completion,) = found["completions"]
        self.assertEqual((completion["reviewer"], completion["model"], completion["candidate"], completion["writer"]),
                         ("sol", "gpt-6.1-sol", self.CANDIDATE, "claude"))
        self.assertTrue(completion["review"].endswith("COMPLETION: gaps"))
        _, listing = self.dispatch("--list-rounds", "--json")
        (entry,) = listing["records"]
        self.assertEqual(([r["number"] for r in entry["rounds"]], len(entry["completions"])), ([1], 1))

    def test_the_completion_review_is_one_pass(self):
        record = self.repo / "docs/reviews/c.md"
        # A pass that returned no review records nothing and may be followed by another attempt.
        result, _ = self.complete(record, codex="fail")
        self.assertEqual(result.returncode, 1)
        self.assertFalse(record.exists())
        self.elapse()
        result, _ = self.complete(record, codex="completionblocked")
        self.assertEqual(result.returncode, 1, "COMPLETION: blocked is not a review")
        self.assertEqual(next(iter(self.attempts().values()))["last_class"], "invalid-verdict")
        self.elapse()
        result, _ = self.complete(record, codex="completion")
        self.assertEqual(result.returncode, 0, result.stderr)
        before = record.read_bytes()
        result, summary = self.complete(record, codex="completion")
        self.assertEqual(result.returncode, 2)
        self.assertIn("already holds a completion review", result.stderr)
        self.assertFalse(summary["recorded"])
        self.assertEqual(record.read_bytes(), before, "a second completion review was appended")

    def test_a_completion_reply_without_its_verdict_leaves_the_pass_open(self):
        record = self.repo / "docs/reviews/c.md"
        result, _ = self.complete(record, codex="say", say="1. Open file: present.")
        self.assertIn("## Completion review: sol", record.read_text(encoding="utf-8"), "the attempt is recorded")
        result, summary = self.complete(record, codex="completion")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(summary["recorded"], "a reply without COMPLETION: is not the pass, so the retry is recorded")
        entries = self.module().read_completions(record.read_text(encoding="utf-8"))
        self.assertEqual([self.module().returned_pass("completion", e) for e in entries], [False, True])
        result, _ = self.complete(record, codex="completion")
        self.assertIn("already holds a completion review", result.stderr)

    def test_a_completion_review_needs_a_full_candidate_and_a_round_takes_none(self):
        for candidate in (None, "abc1234", self.CANDIDATE.upper(), self.CANDIDATE + "0"):
            with self.subTest(candidate=candidate):
                args = ["--kind", "completion", "--writer", "claude", "--prompt", str(self.prompt)]
                result, _ = self.dispatch(*args, *(["--candidate", candidate] if candidate else []))
                self.assertEqual(result.returncode, 2)
                self.assertIn("--kind completion needs --candidate", result.stderr)
        result, _ = self.dispatch("--candidate", self.CANDIDATE, "--writer", "claude", "--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 2)
        self.assertEqual(self.calls(), [])

    def test_a_completion_review_edited_by_hand_is_unreadable(self):
        record = self.repo / "docs/reviews/c.md"
        self.complete(record, codex="completion")
        text = record.read_text(encoding="utf-8")
        module = self.module()
        self.assertIsNotNone(module.read_findings(text))
        for edited in (text.replace("COMPLETION: gaps", "COMPLETION: done"),  # same line count, digest differs
                       text.replace(f"- Candidate: {self.CANDIDATE}\n", ""),
                       text.replace(f"- Candidate: {self.CANDIDATE}", "- Candidate: abc1234"),
                       re.sub(r"- Digest: [0-9a-f]{16}\n", "", text)):
            with self.subTest(edited=edited[-60:]):
                self.assertNotEqual(edited, text)
                self.assertIsNone(module.read_findings(edited))
                self.assertIsNone(module.read_rounds(edited), "a broken completion review must not hide behind rounds")

    def test_the_writers_notes_after_a_completion_review_are_read_back(self):
        record = self.repo / "docs/reviews/c.md"
        self.complete(record, codex="completion")
        with open(record, "a", encoding="utf-8", newline="") as handle:
            handle.write("\n- Implemented: gap 1 in abc1234.\n- Filed: D02 T01 §9 (gap 2)\n")
        self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record))
        found = self.module().read_findings(record.read_bytes().decode("utf-8"))
        self.assertEqual(found["completions"][0]["notes"], ["- Implemented: gap 1 in abc1234.",
                                                            "- Filed: D02 T01 §9 (gap 2)"])
        self.assertEqual([r["number"] for r in found["rounds"]], [1])


def bad_pins(good):
    """Every rule a parsable pin can break, each as one edit of the fixture, shared by the review and check tests."""
    mini = '\n[reviewers.mini]\ncli = "codex"\nmodel = "gpt-6-mini"\neffort = "low"\n'
    return (good.replace('effort = "xhigh"', 'effort = "extreme"'),
            good.replace('effort = "xhigh"\n', ''),
            good.replace('cli = "codex"', 'cli = "bash"'),
            # Antigravity takes no effort, and Claude Code cannot review its own family's work.
            good.replace('cli = "codex"', 'cli = "agy"'),
            good.replace('cli = "codex"', 'cli = "claude"'),
            good.replace('claude = ["sol"]', 'claude = ["opus"]'),
            good.replace('model = "gpt-6.1-sol"', 'model = "gpt-6.1-sol; rm -rf ~"'),
            good + '\n[extra]\nkey = 1\n',
            good.replace('claude = ["sol"]', 'claude = [["sol"]]'),
            good.replace('effort = "xhigh"', 'effort = "xhigh"\nsandbox = "workspace-write"'),
            # A fallback, an empty route, a name outside a list, an unknown writer and a same-family route.
            good.replace('claude = ["sol"]', 'claude = ["sol", "mini"]') + mini,
            good.replace('claude = ["sol"]', 'claude = []'),
            good.replace('claude = ["sol"]', 'claude = "sol"'),
            good + 'opus = ["sol"]\n',
            good + 'codex = ["sol"]\n')


SCHEDULED_PIN = FIXTURE_PIN + textwrap.dedent('''
    [schedule]
    severity_stop_after = 3

    [schedule.plan]
    effort = "medium"
    lenses = ["plan"]

    [schedule.completion]
    effort = "medium"

    [[schedule.rounds]]
    effort = "high"
    lenses = ["adversarial", "integration"]

    [[schedule.rounds]]
    effort = "medium"
    lenses = ["adversarial"]

    [[schedule.rounds]]
    effort = "low"
    lenses = ["adversarial"]

    [schedule.rotation]
    effort = "low"
    lenses = ["consistency", "design", "record"]

    [schedule.escalation]
    effort = "medium"
    lenses = ["adversarial"]
''')


FIXTURES = SCRIPTS / "tests" / "fixtures" / "review-failures"


class Attempts(Harness):
    """A failed attempt is kept in build/, classified, capped at 5 a pass and waited out, never retried forever."""

    def go(self, record=None, **modes):
        record = record or self.repo / "docs/reviews/r.md"
        return self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record), **modes)

    def outside_build(self):
        return sorted((str(p.relative_to(self.repo)), p.read_bytes()) for p in self.repo.rglob("*")
                      if p.is_file() and "build" not in p.relative_to(self.repo).parts)

    def test_each_class_from_cli_output(self):
        module = self.module()
        captured = {"codex-not-signed-in.txt": "auth", "codex-unsupported-model.txt": "model"}
        for name, expected in captured.items():
            with self.subTest(name):
                self.assertEqual(module.classify("exited 1", (FIXTURES / name).read_text(encoding="utf-8")), (expected, None))
        # Representative wording: no rate limit or timeout could be produced on purpose to capture one.
        cases = [("exited 1", "ERROR: unexpected status 429 Too Many Requests: Rate limit reached. Retry-After: 120",
                  ("rate-limit", 120)),
                 ("exited 1", "You've hit your usage limit. Try again in 5 minutes.", ("rate-limit", 300)),
                 ("exited 1", "rate limit exceeded", ("rate-limit", None)),
                 ("timed out", "", ("timeout", None)),
                 ("invalid terminal verdict", "", ("invalid-verdict", None)),
                 ("could not review: no access", "", ("invalid-verdict", None)),
                 ("exited 3", "segfault", ("other", None))]
        for reason, detail, expected in cases:
            with self.subTest(detail or reason):
                self.assertEqual(module.classify(reason, detail), expected)

    def test_a_planted_key_never_reaches_the_failure_file(self):
        key = "sk-proj-" + "A1b2C3d4E5f6G7h8I9j0" * 2
        result, summary = self.go(codex="stderr", stderr=f"ERROR: something broke, api_key={key}\nBearer {key}\n")
        self.assertEqual(result.returncode, 1)
        kept = self.repo / summary["attempt"]["failure"]
        text = kept.read_text(encoding="utf-8")
        self.assertNotIn(key, text)
        self.assertIn("[redacted]", text)
        self.assertIn("something broke", text)
        self.assertLessEqual(len(kept.read_bytes()), 4096)
        self.assertNotIn("detail", summary)

    def test_five_attempts_a_pass_then_it_waits_and_a_sixth_never_runs(self):
        before = self.outside_build()
        codes = []
        for _ in range(6):
            result, _ = self.go(codex="fail")
            codes.append(result.returncode)
            self.elapse()
        self.assertEqual(codes, [1, 1, 1, 1, 3, 3])
        self.assertEqual(len(self.calls()), 5, "a sixth attempt ran")
        self.assertEqual(self.outside_build(), before, "a failed attempt changed the tree")
        (entry,) = self.attempts().values()
        self.assertEqual(entry["count"], 5)
        self.assertIn("wait_until", entry)

    def test_auth_waits_at_once_and_says_how_to_recover(self):
        result, _ = self.go(codex="stderr", stderr=(FIXTURES / "codex-not-signed-in.txt").read_text(encoding="utf-8"))
        self.assertEqual(result.returncode, 3)
        self.assertIn("codex login", result.stderr)
        self.assertIn("No run is waiting", result.stderr)  # this scratch repository has no run guard
        self.assertEqual(len(self.calls()), 1)

    def test_a_far_not_before_runs_no_cli_in_another_process(self):
        self.go(codex="stderr", stderr="429 Too Many Requests. Retry-After: 3600")
        self.log.unlink(missing_ok=True)
        result, _ = self.go()
        self.assertEqual(result.returncode, 3)
        self.assertEqual(self.calls(), [], "a CLI ran before its retry-after")

    def test_a_new_window_after_the_wait_keeps_the_position(self):
        record = self.repo / "docs/reviews/r.md"
        self.go(record)  # round 1
        for _ in range(5):
            self.go(record, codex="fail")
            self.elapse()
        path = self.repo / "build" / "review-attempts.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        (key,) = data["passes"]
        self.assertTrue(key.endswith("|round|round 2"), key)
        data["passes"][key]["wait_until"] = 0  # the wait has passed
        path.write_text(json.dumps(data), encoding="utf-8")
        result, summary = self.go(record)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(summary["round"], 2)
        self.assertEqual(self.attempts(), {})

    def test_an_unreadable_attempt_file_refuses_before_any_cli(self):
        path = self.repo / "build" / "review-attempts.json"
        path.parent.mkdir(parents=True)
        path.write_text("{bad", encoding="utf-8")
        result, _ = self.go()
        self.assertEqual(result.returncode, 2)
        self.assertIn("attempt count is unknown", result.stderr)
        self.assertEqual(self.calls(), [])

    def test_a_short_not_before_is_slept_and_each_attempt_is_reserved_first(self):
        module = self.module()
        clock = [1000.0]
        slept = []
        module.CLOCK = lambda: clock[0]
        module.SLEEP = lambda s: (slept.append(s), clock.__setitem__(0, clock[0] + s))
        key = "docs/reviews/r.md|round|round 1"
        module.save_attempts({key: {"count": 1, "not_before": 1120.0}})
        entry = module.before_attempt(key)
        self.assertEqual(slept, [120.0])
        # A kill now loses nothing: a fresh read already counts this attempt.
        self.assertEqual(self.module().load_attempts()[key]["count"], 2)
        self.assertEqual(entry["count"], 2)

    def test_a_save_that_fails_refuses_before_any_cli(self):
        module = self.module()
        with unittest.mock.patch.object(module, "atomic_write", side_effect=OSError(28, "No space left on device")):
            with self.assertRaisesRegex(module.Refused, "can't be saved"):
                module.before_attempt("docs/reviews/r.md|round|round 1")

    def test_a_guard_that_cant_save_the_wait_refuses_rather_than_claiming_one(self):
        module = self.module()
        script = self.repo / "scripts" / "run-guard.py"
        script.write_text("", encoding="utf-8")
        for code, outcome in ((0, True), (4, False), (1, None), (2, None)):
            done = subprocess.CompletedProcess([], code, "said", "")
            with self.subTest(code), unittest.mock.patch.object(module.subprocess, "run", return_value=done):
                if outcome is None:
                    with self.assertRaisesRegex(module.Refused, "couldn't save the wait"):
                        module.guard_wait(5000.0, "auth")
                else:
                    self.assertEqual(module.guard_wait(5000.0, "auth")[0], outcome)

    def test_a_zero_exit_reply_with_no_review_keeps_its_explanation(self):
        for say, cls in (("", "other"),):  # under this legacy pin a reply with no verdict line is still a review
            with self.subTest(say=say):
                record = self.repo / f"docs/reviews/q{len(say)}.md"
                result, summary = self.go(record, codex="quiet", stderr="note: the sandbox refused the repository\n", say=say)
                self.assertEqual(result.returncode, 1, result.stderr)
                text = (self.repo / summary["attempt"]["failure"]).read_text(encoding="utf-8")
                self.assertIn("the sandbox refused the repository", text)

    def test_a_dispatcher_killed_after_its_reservation_keeps_the_attempt(self):
        record = self.repo / "docs/reviews/k.md"
        system_root = {"SYSTEMROOT": os.environ["SYSTEMROOT"]} if os.name == "nt" else {}
        env = {"PATH": f"{self.bin}{os.pathsep}{Path(sys.executable).parent}", "FAKE_LOG": str(self.log),
               "FAKE_CHILD_PID": str(Path(self._tmp.name) / "child.pid"), "FAKE_CODEX": "sleep", **system_root,
               **{var: str(self.scratch) for var in ("TMP", "TEMP", "TMPDIR")}}
        group = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {"start_new_session": True}
        process = subprocess.Popen([sys.executable, "-B", str(self.repo / "scripts/review-dispatch.py"), "--writer", "claude",
                                    "--prompt", str(self.prompt), "--record", str(record)], env=env,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **group)
        try:
            deadline = time.monotonic() + 30
            while not self.calls() and time.monotonic() < deadline:  # the CLI started, so the reservation is saved
                time.sleep(0.1)
            self.assertTrue(self.calls(), "the fake CLI never started")
        finally:
            kill_tree(process.pid)
            process.wait(30)
        (entry,) = self.attempts().values()
        self.assertEqual(entry["count"], 1)
        self.assertNotIn("not_before", entry)
        result, _ = self.go(record, codex="fail")  # a fresh process counts on from the killed one
        self.assertEqual(result.returncode, 1)
        self.assertEqual(next(iter(self.attempts().values()))["count"], 2)

    def test_a_failed_save_in_one_process_leaves_the_last_saved_count_for_the_next(self):
        record = self.repo / "docs/reviews/s.md"
        self.go(record, codex="fail")
        before = self.attempts()
        script = (f"import importlib.util, sys\n"
                  f"spec = importlib.util.spec_from_file_location('d', {str(self.repo / 'scripts/review-dispatch.py')!r})\n"
                  f"d = importlib.util.module_from_spec(spec); spec.loader.exec_module(d)\n"
                  f"def full(path, text): raise OSError(28, 'No space left on device')\n"
                  f"d.atomic_write = full\n"
                  f"d.CLOCK = lambda: 2_000_000_000\n"
                  f"try:\n    d.before_attempt({next(iter(before))!r})\nexcept d.Refused as e:\n    print('refused', e); sys.exit(2)\n")
        done = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True)
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("can't be saved", done.stdout)
        self.assertEqual(self.attempts(), before, "a failed save changed the saved count")

    def test_a_reviewer_flooding_stderr_is_stopped(self):
        result, summary = self.go(codex="floodstderr")
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("returned more than", summary["failover"][0]["reason"])

    def test_exit_3_only_after_the_wait_is_saved_and_says_whether_a_run_waits(self):
        module = self.module()
        order = []
        real_save = module.save_attempts
        module.save_attempts = lambda passes: (order.append("saved"), real_save(passes))
        for saved, said in ((True, "The run waits."), (False, "No run is waiting")):
            with self.subTest(saved):
                order.clear()
                module.guard_wait = lambda until, detail, s=saved: (order.append("guard"), (s, "no active run"))[1]
                message = module.start_wait({}, "k", {"last_class": "auth", "count": 1}, 5000.0)
                self.assertEqual(order, ["saved", "guard"])
                self.assertIn(said, message)


class InFlightMarks(Harness):
    """The dispatcher's own mark: there while the reviewer runs, read by the guard, gone however the review ends."""

    def marks(self):
        folder = self.repo / "build" / "review-in-flight"
        return sorted(p.name for p in folder.iterdir()) if folder.is_dir() else []

    def test_the_mark_stands_while_the_reviewer_runs_and_goes_after_success_failure_and_timeout(self):
        for mode, extra in (("ok", ()), ("fail", ()), ("sleep", ("--timeout", "2"))):
            with self.subTest(mode):
                self.log.unlink(missing_ok=True)
                self.dispatch("--writer", "claude", "--prompt", str(self.prompt), *extra, codex=mode)
                (call,) = self.calls()
                self.assertEqual(len(call["in_flight"]), 1, "the reviewer ran without its mark")
                self.assertRegex(call["in_flight"][0], r"^\d+-sol\.json$")
                self.assertEqual(self.marks(), [], "the mark outlived the review")

    def test_the_guard_reads_a_running_dispatcher_s_mark_as_live(self):
        sys.path.insert(0, str(SCRIPTS / "lib"))
        import run_guard
        module = self.module()
        module.IN_FLIGHT = self.repo / "build" / "review-in-flight"
        with module.in_flight("sol", 60):
            self.assertIn("sol", run_guard.review_in_flight(self.repo))
        self.assertIsNone(run_guard.review_in_flight(self.repo))

    def test_a_mark_that_cant_be_written_runs_no_reviewer(self):
        (self.repo / "build").mkdir()
        (self.repo / "build" / "review-in-flight").write_text("a file where the folder would go", encoding="utf-8")
        result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 2)
        self.assertIn("review-in-flight mark can't be written", result.stderr)
        self.assertEqual(self.calls(), [])


class SkillsKnowTheWait(unittest.TestCase):
    def test_each_agent_s_mirror_says_how_it_runs_and_waits_for_a_review(self):
        repo = SCRIPTS.parent
        own = {".claude": "run_in_background", ".codex": "write_stdin", ".agents": "at least 35 minutes"}
        for folder, words in own.items():
            for skill in ("review-todo-section", "process-todo-section"):
                with self.subTest(folder=folder, skill=skill):
                    text = (repo / folder / "skills" / skill / "SKILL.md").read_text(encoding="utf-8")
                    self.assertIn(words, text)
                    self.assertIn("final output and exit status", text)
                    for other, theirs in own.items():
                        if other != folder:
                            self.assertNotIn(theirs, text, f"{folder} carries {other}'s instruction")

    def test_every_skill_that_calls_the_dispatcher_ends_the_turn_on_exit_3(self):
        repo = SCRIPTS.parent
        for folder in (".claude", ".codex", ".agents"):
            for skill in ("process-todo-section", "review-todo-section", "process-phase", "process-plan"):
                with self.subTest(folder=folder, skill=skill):
                    text = (repo / folder / "skills" / skill / "SKILL.md").read_text(encoding="utf-8")
                    self.assertRegex(text, r"[Ee]xits? (1 or )?3")
                    self.assertIn("without building, retrying or stamping", text)
                    self.assertNotIn("or `none` with the reason when the reviewer returned no review", text)
                    self.assertNotIn("When the dispatcher exits 1, no review happened: run the pass again", text)


class Schedule(Harness):
    """The pin's [schedule]: the dispatcher picks each pass's effort and lenses from the rounds already recorded."""

    def setUp(self):
        super().setUp()
        (self.repo / ".conclave/reviewers.toml").write_text(SCHEDULED_PIN, encoding="utf-8")
        self.findings = self.repo / "findings.md"

    def round(self, say="OVERALL: approve", *extra):
        self.log.unlink(missing_ok=True)
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record",
                                        str(self.findings), *extra, codex="say", say=say)
        self.assertIn(result.returncode, (0, 1), result.stderr)
        return summary, self.calls()

    def effort_of(self, call):
        return next(a.split("=", 1)[1] for a in call["argv"] if a.startswith("model_reasoning_effort="))

    def test_rounds_follow_the_fixed_list_then_rotate_one_lens_each(self):
        seen = []
        for _ in range(7):
            summary, (call,) = self.round()
            seen.append((summary["position"], self.effort_of(call), summary["lenses"]))
            self.assertIn("## Lenses for this pass", call["stdin"])
            for name in summary["lenses"]:
                self.assertIn(f"- `{name}`:", call["stdin"])
        self.assertEqual(seen, [(1, "high", ["adversarial", "integration"]), (2, "medium", ["adversarial"]),
                                (3, "low", ["adversarial"]), (4, "low", ["consistency"]), (5, "low", ["record"]),
                                (6, "low", ["consistency"]), (7, "low", ["record"])],
                         "without --surface the rotation skips the design lens")
        rounds = self.module().read_rounds(self.findings.read_text(encoding="utf-8"))
        self.assertEqual([(r["effort"], r["lenses"]) for r in rounds][:2],
                         [("high", ["adversarial", "integration"]), ("medium", ["adversarial"])])

    def test_surface_puts_design_in_the_rotation(self):
        lenses = [self.round("OVERALL: approve", "--surface")[0]["lenses"] for _ in range(6)]
        self.assertEqual(lenses[3:], [["consistency"], ["design"], ["record"]])

    def test_an_outage_round_does_not_move_the_position(self):
        self.round()
        self.log.unlink(missing_ok=True)
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record",
                                        str(self.findings), codex="fail")
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertEqual(summary["position"], 2)
        self.elapse()
        summary, (call,) = self.round()
        self.assertEqual((summary["position"], summary["round"], self.effort_of(call)), (2, 2, "medium"))

    def test_a_self_review_or_a_reply_without_a_verdict_does_not_move_the_position(self):
        # scripts/lib/todo_graph.py stamp replays `scheduled`, so neither may count as a review, like an outage.
        module = self.module()
        schedule = module.load_pin(str(self.repo / ".conclave/reviewers.toml"))["schedule"]
        returned = {"reviewer": "astra", "cli": "codex", "writer": "claude", "effort": "high",
                    "review": "OVERALL: approve"}
        self.assertEqual(module.scheduled(schedule, "round", [returned, returned], False)["position"], 3)
        for own in ({"reviewer": "opus", "cli": "claude"}, {"reviewer": "x", "cli": "unknown"},
                    {"review": "1. The reply stopped before its verdict."}):
            with self.subTest(own=own):
                self.assertEqual(module.scheduled(schedule, "round", [returned, {**returned, **own}], False)
                                 ["position"], 2)

    def test_a_low_effort_p1_escalates_the_next_round(self):
        for _ in range(3):
            self.round()
        summary, _ = self.round("1. P1 calc.py:2 subtracts.\n\nHIGHEST: P1\nOVERALL: needs-attention")
        self.assertEqual((summary["position"], summary["escalated"]), (4, False))
        summary, (call,) = self.round()
        self.assertEqual((summary["position"], summary["escalated"], self.effort_of(call), summary["lenses"]),
                         (5, True, "medium", ["adversarial"]))
        self.assertIn("escalated after a low-effort round found a P1", call["stdin"])
        summary, (call,) = self.round()
        self.assertEqual((summary["escalated"], self.effort_of(call)), (False, "low"),
                         "a medium escalation that finds a P1 does not escalate again")

    def test_severity_stop_only_past_the_fixed_rounds_and_only_on_p3(self):
        p3 = "1. P3 calc.py:2 a name.\n\nHIGHEST: P3\nOVERALL: approve"
        stops = [self.round(p3)[0]["severity_stop"] for _ in range(4)]
        self.assertEqual(stops, [False, False, False, True])
        self.assertFalse(self.round("1. P2 calc.py:2.\n\nHIGHEST: P2\nOVERALL: needs-attention")[0]["severity_stop"])
        self.assertFalse(self.round("1. calc.py:2 untagged.\n\nOVERALL: needs-attention")[0]["severity_stop"],
                         "a round with no HIGHEST line never stops the loop")

    def test_severity_stop_from_round_one_at_zero(self):
        # The shipped pin's 0: the first round whose worst finding is P3 may end the loop.
        pin = self.repo / ".conclave/reviewers.toml"
        pin.write_text(pin.read_text(encoding="utf-8").replace("severity_stop_after = 3", "severity_stop_after = 0"),
                       encoding="utf-8")
        p3 = "1. P3 calc.py:2 a name.\n\nHIGHEST: P3\nOVERALL: approve"
        self.assertTrue(self.round(p3)[0]["severity_stop"])
        self.assertFalse(self.round("1. P2 calc.py:2.\n\nHIGHEST: P2\nOVERALL: needs-attention")[0]["severity_stop"])

    def test_an_unreadable_findings_file_is_refused_before_a_review_runs(self):
        self.findings.write_text("## Round one\n\nnot a record\n", encoding="utf-8")
        result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(self.findings))
        self.assertEqual(result.returncode, 2)
        self.assertIn("so no round was run", result.stderr)
        self.assertEqual(self.calls(), [], "no review is spent that could not be recorded")

    def test_the_plan_review_is_one_pass_recorded_with_its_lens(self):
        candidate = "a" * 40
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--kind", "plan",
                                        "--candidate", candidate, "--record", str(self.findings), codex="say",
                                        say="1. Ctrl+K reach: missing.\n\nPLAN: gaps\nOVERALL: needs-attention")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((summary["kind"], summary["effort"], summary["lenses"]), ("plan", "medium", ["plan"]))
        self.assertNotIn("position", summary)
        (call,) = self.calls()
        self.assertEqual(self.effort_of(call), "medium")
        self.assertIn("- `plan`:", call["stdin"])
        text = self.findings.read_text(encoding="utf-8")
        self.assertIn("## Plan review: sol (`gpt-6.1-sol` via `codex`)", text)
        (plan,) = self.module().read_plans(text)
        self.assertEqual((plan["candidate"], plan["lenses"], plan["effort"]), (candidate, ["plan"], "medium"))
        self.assertEqual(self.module().read_rounds(text), [], "a plan review is not a round")
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--kind", "plan",
                                        "--candidate", candidate, "--record", str(self.findings))
        self.assertEqual(result.returncode, 2)
        self.assertIn("already holds a plan review", result.stderr)
        summary, _ = self.round()
        self.assertEqual(summary["position"], 1, "the plan review does not move the round position")

    def test_a_plan_reply_without_its_verdict_leaves_the_pass_open(self):
        candidate = "a" * 40
        plan = ("--writer", "claude", "--prompt", str(self.prompt), "--kind", "plan", "--candidate", candidate,
                "--record", str(self.findings))
        self.dispatch(*plan, codex="say", say="1. Keyboard reach: missing.\n\nOVERALL: needs-attention")
        result, summary = self.dispatch(*plan, codex="say", say="1. Gap.\n\nPLAN: gaps\nOVERALL: needs-attention")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(summary["recorded"], "a reply without PLAN: is not the pass, so the retry is recorded")
        entries = self.module().read_plans(self.findings.read_text(encoding="utf-8"))
        self.assertEqual([self.module().returned_pass("plan", e) for e in entries], [False, True])
        result, _ = self.dispatch(*plan, codex="say", say="PLAN: complete\nOVERALL: approve")
        self.assertEqual(result.returncode, 2)
        self.assertIn("already holds a plan review", result.stderr)

    def test_a_plan_review_needs_a_full_candidate(self):
        for extra in ((), ("--candidate", "abc123")):
            with self.subTest(extra=extra):
                result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--kind", "plan", *extra)
                self.assertEqual(result.returncode, 2)
                self.assertIn("--kind plan needs --candidate", result.stderr)

    def test_the_completion_review_takes_its_scheduled_effort_and_no_lenses(self):
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--kind", "completion",
                                        "--candidate", "b" * 40, codex="say", say="COMPLETION: complete")
        self.assertEqual(result.returncode, 0, result.stderr)
        (call,) = self.calls()
        self.assertEqual(self.effort_of(call), "medium")
        self.assertNotIn("## Lenses for this pass", call["stdin"])
        self.assertEqual(call["stdin"], self.prompt.read_text())

    def test_a_pin_without_a_schedule_keeps_the_reviewers_effort_and_prompt(self):
        (self.repo / ".conclave/reviewers.toml").write_text(FIXTURE_PIN, encoding="utf-8")
        summary, (call,) = self.round()
        self.assertEqual(self.effort_of(call), "xhigh")
        self.assertEqual(call["stdin"], self.prompt.read_text())
        self.assertNotIn("position", summary)

    def test_check_pin_reports_the_schedule(self):
        result, summary = self.dispatch("--check-pin", "--json")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(summary["schedule"]["severity_stop_after"], 3)

    def test_a_malformed_schedule_refuses_every_dispatch(self):
        module = self.module()
        good = tomllib.loads(SCHEDULED_PIN)["schedule"]
        bad = {
            "unknown lens": {**good, "plan": {"effort": "medium", "lenses": ["vibes"]}},
            "bad effort": {**good, "completion": {"effort": "extreme"}},
            "completion lenses": {**good, "completion": {"effort": "medium", "lenses": ["plan"]}},
            "no rounds": {**good, "rounds": []},
            "duplicate lens": {**good, "rotation": {"effort": "low", "lenses": ["record", "record"]}},
            "missing key": {k: v for k, v in good.items() if k != "escalation"},
            "unknown key": {**good, "extra": 1},
            "stop negative": {**good, "severity_stop_after": -1},
            "stop past cap": {**good, "severity_stop_after": 34},
            "stop bool": {**good, "severity_stop_after": True},
        }
        module.check_schedule(good, ["codex"])
        for name, schedule in bad.items():
            with self.subTest(name), self.assertRaises(module.Refused):
                module.check_schedule(schedule, ["codex"])
        broken = SCHEDULED_PIN.replace('lenses = ["plan"]', 'lenses = ["vibes"]')
        (self.repo / ".conclave/reviewers.toml").write_text(broken, encoding="utf-8")
        result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 2)
        self.assertIn("[schedule] plan", result.stderr)
        self.assertEqual(self.calls(), [])


class MultiCli(Harness):
    """The Antigravity reviewer beside Astra: each writer family is reviewed by another family, read-only."""

    PIN = textwrap.dedent('''    schema_version = 1

        [reviewers.astra]
        cli = "codex"
        model = "gpt-6-astra"
        effort = "medium"

        [reviewers.gemini]
        cli = "agy"
        model = "gemini-4-argon-high"

        [routes]
        claude = ["astra"]
        codex = ["gemini"]
        gemini = ["astra"]
    ''')

    def setUp(self):
        super().setUp()
        (self.repo / ".conclave/reviewers.toml").write_text(self.PIN, encoding="utf-8")

    def test_gemini_runs_in_plan_mode_with_the_prompt_as_the_p_value(self):
        result, summary = self.dispatch("--writer", "codex", "--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((summary["reviewer"], summary["model"], summary["effort"]), ("gemini", "gemini-4-argon-high", None))
        self.assertIn("(agy)", summary["review"])
        (call,) = self.calls()
        self.assertEqual(call["cli"], "agy")
        self.assertIn("--sandbox", call["argv"])
        self.assertEqual(call["argv"][call["argv"].index("--mode") + 1], "plan")
        if os.name == "nt":
            # The fake is a .cmd shim on Windows, so the prompt must arrive by file, never as argument text.
            self.assertTrue(call["argv"][-1].startswith("-p=Follow the review instructions in the file "))
            self.assertEqual(call["from_file"], self.prompt.read_text())
        else:
            self.assertEqual(call["argv"][-1], "-p=" + self.prompt.read_text())

    def test_astra_reviews_gemini_work(self):
        result, summary = self.dispatch("--writer", "gemini", "--prompt", str(self.prompt))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(summary["reviewer"], "astra")
        (call,) = self.calls()
        self.assertEqual(call["cli"], "codex")

    def test_a_large_prompt_reaches_gemini_by_file(self):
        self.prompt.write_text("x" * 150_000, encoding="utf-8")
        self.dispatch("--writer", "codex", "--prompt", str(self.prompt))
        (call,) = self.calls()
        self.assertTrue(call["argv"][-1].startswith("-p=Follow the review instructions in the file "))
        self.assertEqual(call["from_file"], "x" * 150_000)

    def test_a_windows_shim_never_gets_prompt_text_in_its_arguments(self):
        reviewer = {"cli": "agy", "model": "gemini-4-argon-high"}
        argv, stdin = self.module().command(reviewer, "C:/tools/agy.cmd", "x & del *", Path("C:/p.md"), Path("o"), 60)
        self.assertEqual(argv[-1], f"-p=Follow the review instructions in the file {Path('C:/p.md')} exactly. "
                                   "Do not modify any file.")
        self.assertIsNone(stdin)
        argv, _ = self.module().command(reviewer, "/usr/bin/agy", "short prompt", Path("/p.md"), Path("o"), 60)
        self.assertEqual(argv[-1], "-p=short prompt")

    def test_a_scheduled_effort_never_reaches_antigravity(self):
        module = self.module()
        argv, _ = module.command({"cli": "agy", "model": "gemini-4-argon-high"}, "/usr/bin/agy", "p", Path("/p.md"),
                                 Path("o"), 60, "low")
        self.assertNotIn("low", " ".join(argv))

    def test_a_schedule_is_checked_against_every_effort_taking_cli(self):
        module = self.module()
        schedule = {"severity_stop_after": 0, "plan": {"effort": "max", "lenses": ["plan"]},
                    "completion": {"effort": "medium"}, "rounds": [{"effort": "high", "lenses": ["adversarial"]}],
                    "rotation": {"effort": "low", "lenses": ["quality"]},
                    "escalation": {"effort": "medium", "lenses": ["adversarial"]}}
        module.check_schedule(schedule, {"claude", "agy"})
        with self.assertRaises(module.Refused):
            module.check_schedule(schedule, {"codex", "agy"})


class RepositoryPin(unittest.TestCase):
    def test_the_repository_pin_loads_under_the_dispatchers_rules(self):
        spec = importlib.util.spec_from_file_location("dispatch_pin_check", SCRIPTS / "review-dispatch.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        pin = module.load_pin(REPO_PIN)
        self.assertTrue(pin["routes"], "the repository pin routes no writer")

    def test_the_repository_pin_is_one_reviewer_per_writer_on_the_schedule(self):
        with open(REPO_PIN, "rb") as handle:
            pin = tomllib.load(handle)
        self.assertEqual(pin["reviewers"]["argon"], {
            "cli": "agy", "model": "gemini-4-argon-medium",
            "models_by_effort": {"low": "gemini-4-argon-low", "medium": "gemini-4-argon-medium"}})
        self.assertEqual(pin["reviewers"]["astra"], {"cli": "codex", "model": "gpt-6-astra", "effort": "medium"})
        self.assertEqual(pin["routes"], {"claude": ["astra"], "codex": ["argon"],
                                       "gemini": ["astra"], "muse": ["astra"], "meta": ["astra"]})
        schedule = pin["schedule"]
        self.assertEqual(schedule["completion_order"], "last")
        self.assertEqual(schedule["max_low_rounds"], 32)
        self.assertEqual(schedule["plan"], {"effort": "medium", "lenses": ["plan"]})
        self.assertEqual(schedule["completion"], {"effort": "medium"})
        self.assertEqual(schedule["rounds"], [{"effort": "medium", "lenses": ["adversarial"]}])
        self.assertEqual(schedule["escalation"], {"effort": "low", "lenses": ["adversarial"]})
        self.assertEqual(schedule["rotation"]["effort"], "low")

    def test_the_pins_severity_stop_is_the_stamps_cutoff(self):
        # The dispatcher reports severity_stop from the pin and `stamp` re-checks it against SEVERITY_CUTOFF_ROUND: the
        # two numbers must agree, or the dispatcher would stop a loop the stamp then refuses. todo_graph ships in the
        # todo kit: installed beside this script in a project, or in the sibling kit inside Conclave-SDK.
        with open(REPO_PIN, "rb") as handle:
            if "schedule" not in tomllib.load(handle):
                self.skipTest("the repository pin has no [schedule]")
        found = [path for path in (SCRIPTS / "lib" / "todo_graph.py",
                                   SCRIPTS.parents[2] / "todo" / "templates" / "scripts" / "lib" / "todo_graph.py")
                 if path.is_file()]
        if not found:
            self.skipTest("the todo kit is not installed")
        spec = importlib.util.spec_from_file_location("todo_graph_cutoff", found[0])
        todo_graph = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = todo_graph
        try:
            spec.loader.exec_module(todo_graph)
        finally:
            del sys.modules[spec.name]
        with open(REPO_PIN, "rb") as handle:
            pin = tomllib.load(handle)
        self.assertEqual(pin["schedule"]["severity_stop_after"], todo_graph.SEVERITY_CUTOFF_ROUND)



class RepairCycles(unittest.TestCase):
    """Written with the dispatcher's own functions, so these exercise what a real cycle does."""

    def setUp(self):
        import tempfile
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.folder = Path(self.tmp.name)

    def module(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location("review_dispatch_repair", Path(__file__).resolve().parents[1] / "review-dispatch.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def schedule(self, repairs):
        pin = self.module().load_pin(Path(__file__).resolve().parents[2] / ".conclave" / "reviewers.toml")
        self.assertEqual(pin["schedule"]["max_repair_cycles"], 33)
        return pin["schedule"] | {"max_repair_cycles": repairs}

    def test_the_pin_reads_thirty_three_and_refuses_more(self):
        module = self.module()
        for value, allowed in ((0, True), (33, True), (34, False), (-1, False), ("33", False), (True, False)):
            with self.subTest(value):
                if allowed:
                    module.check_schedule(self.schedule(value), {"codex"})
                else:
                    with self.assertRaises(module.Refused):
                        module.check_schedule(self.schedule(value), {"codex"})

    def test_check_pin_reports_the_cap(self):
        import json
        import subprocess
        import sys
        root = Path(__file__).resolve().parents[2]
        done = subprocess.run([sys.executable, str(root / "scripts" / "review-dispatch.py"), "--check-pin", "--json"], capture_output=True, text=True, cwd=root)
        self.assertEqual(json.loads(done.stdout)["schedule"]["max_repair_cycles"], 33)

    def test_an_old_cycle_file_is_refused_once_a_newer_one_exists(self):
        module = self.module()
        (self.folder / "D00-T03-s1.md").write_text("x", encoding="utf-8")
        self.assertIsNone(module.older_cycle(self.folder / "D00-T03-s1.md"))
        (self.folder / "D00-T03-s1-cycle2.md").write_text("x", encoding="utf-8")
        (self.folder / "D00-T03-s10-cycle9.md").write_text("x", encoding="utf-8")  # another section's
        self.assertEqual(module.older_cycle(self.folder / "D00-T03-s1.md"), "D00-T03-s1-cycle2.md")
        self.assertIsNone(module.older_cycle(self.folder / "D00-T03-s1-cycle2.md"))
        (self.folder / "D00-T03-s1-cycle10.md").write_text("x", encoding="utf-8")
        self.assertEqual(module.older_cycle(self.folder / "D00-T03-s1-cycle2.md"), "D00-T03-s1-cycle10.md")
        self.assertIsNone(module.older_cycle(self.folder / "notes.md"))

    def test_a_whole_second_cycle_runs_in_its_own_file(self):
        module = self.module()
        result = {"writer": "claude", "writer_model": "claude-opus-5-5", "reviewer": "astra", "cli": "codex", "model": "m", "effort": "medium",
                  "execution": "returned", "attempt_reviewer": "astra", "attempt_model": "m", "attempt_cli": "codex", "failover": [], "lenses": ["plan"]}
        schedule = {"completion_order": "last", "max_low_rounds": 8}
        first = self.folder / "D00-T03-s1.md"
        module.record_completion(first, {**result, "review": "T.\n\nPLAN: complete\nOVERALL: approve"}, "2026-11-07T00:00:00Z", "a" * 40, kind="plan")
        module.record(first, {**result, "review": "T.\n\nHIGHEST: none\nOVERALL: approve"}, "2026-11-07T00:00:00Z")
        module.record_completion(first, {**result, "review": "T.\n\nCOMPLETION: gaps"}, "2026-11-07T00:00:00Z", "a" * 40)
        found = module.read_findings(first.read_text(encoding="utf-8"))
        with self.assertRaises(module.Refused):  # the old cycle takes nothing more
            module.check_sequence(schedule, "round", found, "claude", "claude-opus-5-5")
        second = self.folder / "D00-T03-s1-cycle2.md"
        empty = {"rounds": [], "completions": [], "plans": []}
        module.check_sequence(schedule, "plan", empty, "claude", "claude-opus-5-5")
        module.record_completion(second, {**result, "review": "T.\n\nPLAN: complete\nOVERALL: approve"}, "2026-11-07T00:00:00Z", "a" * 40, kind="plan")
        found = module.read_findings(second.read_text(encoding="utf-8"))
        module.check_sequence(schedule, "round", found, "claude", "claude-opus-5-5")
        self.assertEqual(module.older_cycle(first), second.name)


class Settled(Harness):
    """D00 T01 §2: the writer's settled lines reach every pass's reviewer, and each pass records its duration."""

    PIN = MultiCli.PIN  # claude -> astra via codex (prompt on stdin), codex -> gemini via agy (prompt by file or -p)
    SETTLED_LINE = "- Settled: the 33-round cap stays, owner decision 2026-11-08"

    def setUp(self):
        super().setUp()
        (self.repo / ".conclave/reviewers.toml").write_text(self.PIN, encoding="utf-8")
        self.folder = self.repo / "docs/reviews/00-workspace"
        self.folder.mkdir(parents=True)

    def received(self, call):
        """The prompt a fake reviewer was given, whichever route carried it."""
        if call["cli"] == "codex":
            return call["stdin"]
        return call["from_file"] if call["from_file"] is not None else call["argv"][-1][len("-p="):]

    def seed(self, path, *notes):
        """A findings file holding one recorded round whose review quotes a settled line, then the writer's notes."""
        module = self.module()
        result = {"writer": "claude", "reviewer": "astra", "cli": "codex", "model": "gpt-6-astra", "effort": "medium",
                  "failover": [], "review": "1. P2 x.\n- Settled: the reviewer's own words\n\nOVERALL: needs-attention"}
        module.record(path, result, "2026-11-08T00:00:00Z")
        path.write_text(path.read_text(encoding="utf-8") + "\n" + "\n".join(notes) + "\n", encoding="utf-8")

    def test_every_pass_on_both_routes_carries_the_writers_settled_lines(self):
        for writer in ("claude", "codex"):
            for kind in ("plan", "round", "completion"):
                with self.subTest(writer=writer, kind=kind):
                    self.log.unlink(missing_ok=True)
                    for old in self.folder.glob("*.md"):
                        old.unlink()
                    first = self.folder / "D00-T01-s2.md"
                    self.seed(first, self.SETTLED_LINE, "- Filed: D00 T01 §8 (worktrees)",
                              "- Not taken: a panel of two (one reviewer per route)")
                    record = self.folder / "D00-T01-s2-cycle2.md"
                    record.write_text("# Cycle 2\n\n- Settled: carried from this cycle too\n", encoding="utf-8")
                    extra = () if kind == "round" else ("--candidate", "c" * 40)
                    result, summary = self.dispatch("--writer", writer, "--prompt", str(self.prompt), "--kind", kind,
                                                    *extra, "--record", str(record))
                    self.assertEqual(result.returncode, 0, result.stderr)
                    (call,) = self.calls()
                    self.assertEqual(call["cli"], "codex" if writer == "claude" else "agy")
                    prompt = self.received(call)
                    self.assertIn("## Settled items (from the section's findings files", prompt)
                    for line in (self.SETTLED_LINE, "- Filed: D00 T01 §8 (worktrees)",
                                 "- Not taken: a panel of two (one reviewer per route)",
                                 "- Settled: carried from this cycle too"):
                        self.assertIn(line, prompt)
                    self.assertNotIn("the reviewer's own words", prompt, "a line inside a recorded review is not settled")
                    self.assertLess(prompt.index(self.SETTLED_LINE), prompt.index("carried from this cycle too"),
                                    "the first cycle's items come first")

    def test_no_settled_lines_leave_the_prompt_as_written(self):
        record = self.folder / "D00-T01-s2.md"
        self.seed(record)
        result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record))
        self.assertEqual(result.returncode, 0, result.stderr)
        (call,) = self.calls()
        self.assertEqual(call["stdin"], self.prompt.read_text())

    def test_an_unreadable_cycle_file_is_refused_before_any_reviewer_runs(self):
        record = self.folder / "D00-T01-s2-cycle2.md"
        broken = self.folder / "D00-T01-s2.md"
        for name, content in (("unparseable", "## Round one\n\nnot a record\n".encode()),
                              ("not utf-8", b"- Settled: \xff\xfe\n")):
            with self.subTest(name):
                broken.write_bytes(content)
                result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record))
                self.assertEqual(result.returncode, 2)
                self.assertIn(str(broken), result.stderr)
                self.assertIn("settled items cannot be carried; no reviewer ran", result.stderr)
                self.assertEqual(self.calls(), [])
                self.assertEqual(self.attempts(), {}, "a refusal spends no attempt")
        broken.unlink()
        result, _ = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record))
        self.assertEqual(result.returncode, 0, "a first pass on a new record runs: an absent file is not an error")

    def test_an_access_error_is_refused_not_taken_for_an_absent_file(self):
        # Round 1: Path.glob swallowed a folder it could not list and Path.exists answered False for a file it could
        # not inspect, so an inaccessible cycle passed for an absent one and its settled items vanished.
        module = self.module()
        base = self.folder / "D00-T01-s2.md"
        self.seed(base, self.SETTLED_LINE)
        record = self.folder / "D00-T01-s2-cycle3.md"
        (self.folder / "D00-T01-s2-cycle2.md").write_text("- Settled: from cycle two\n", encoding="utf-8")
        self.assertEqual(module.settled_items(record), [self.SETTLED_LINE, "- Settled: from cycle two"])

        def unlistable(*args, **kwargs):
            raise PermissionError(13, "Access is denied")

        with unittest.mock.patch.object(module.os, "scandir", unlistable):
            with self.assertRaisesRegex(module.Refused, "cannot be listed .*no reviewer ran"):
                module.settled_items(record)
        real_read, real_stat, real_exists = Path.read_bytes, Path.stat, os.path.exists

        def exists(path):
            # os.path.exists answers False when stat is denied; Python 3.14's Path.exists() calls it.
            return False if Path(path).name == "D00-T01-s2-cycle2.md" else real_exists(path)

        def guarded(real):
            def call(path, *args, **kwargs):
                if path.name == "D00-T01-s2-cycle2.md":
                    raise PermissionError(13, "Access is denied", str(path))
                return real(path, *args, **kwargs)
            return call

        # Both metadata and content are denied, as for a real file this account may not open: Path.exists() then
        # answers False, which must not pass for absence.
        with unittest.mock.patch.object(Path, "read_bytes", guarded(real_read)), \
                unittest.mock.patch.object(Path, "stat", guarded(real_stat)), \
                unittest.mock.patch("os.path.exists", exists):
            self.assertFalse((self.folder / "D00-T01-s2-cycle2.md").exists(), "the injected denial reaches exists()")
            with self.assertRaisesRegex(module.Refused, "D00-T01-s2-cycle2.md cannot be read .*no reviewer ran"):
                module.settled_items(record)
        # A section folder that does not exist yet is a first pass, not an error.
        self.assertEqual(module.settled_items(self.repo / "docs/reviews/09-new/D09-T01-s1.md"), [])

    def test_every_decision_is_carried_past_two_hundred(self):
        record = self.folder / "D00-T01-s2.md"
        self.seed(record, *[f"- Settled: decision {n}" for n in range(1, 251)])
        items = self.module().settled_items(record)
        self.assertEqual(len(items), 250)
        self.assertEqual(items[-1], "- Settled: decision 250")

    def test_a_failed_pass_records_no_approval_and_keeps_its_duration(self):
        record = self.folder / "D00-T01-s2.md"
        self.seed(record, self.SETTLED_LINE)
        before = record.read_text(encoding="utf-8")
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record),
                                        codex="fail")
        self.assertEqual(result.returncode, 1)
        self.assertIsNone(summary["reviewer"])
        self.assertGreaterEqual(summary["seconds"], 1)
        self.assertEqual(record.read_text(encoding="utf-8"), before)

    def test_a_returned_pass_records_a_positive_duration_read_back_everywhere(self):
        record = self.folder / "D00-T01-s2.md"
        result, summary = self.dispatch("--writer", "claude", "--prompt", str(self.prompt), "--record", str(record))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertGreaterEqual(summary["seconds"], 1)
        text = record.read_text(encoding="utf-8")
        self.assertIn(f"- Seconds: {summary['seconds']}\n", text)
        (entry,) = self.module().read_rounds(text)
        self.assertEqual(entry["seconds"], summary["seconds"])
        _, listing = self.dispatch("--list-rounds", "--json", "--records", str(self.repo / "docs/reviews"))
        (listed,) = listing["records"]
        self.assertEqual(listed["rounds"][0]["seconds"], summary["seconds"])

    def test_duration_is_measured_and_rounded_up(self):
        module = self.module()
        for start, end, want in ((0.0, 0.2, 1), (10.0, 12.4, 3), (5.0, 5.0, 1), (0.0, 7.0, 7)):
            with self.subTest(start=start, end=end):
                self.assertEqual(module.elapsed_seconds(start, end), want)
        # Through review itself: the clock around the reviewer's run is what is recorded, in stdout's dict, the
        # findings file and its read-back.
        pin = module.load_pin(str(self.repo / ".conclave/reviewers.toml"))
        module.find_cli = lambda name: str(self.codex)

        def fake_run(argv, stdin_text, timeout, watched, stderr_path=None):
            watched[1].write_text("1. P3 x.\n\nOVERALL: approve", encoding="utf-8")
            return 0, None

        module.run = fake_run
        for ticks, want in (((100.0, 100.2), 1), ((100.0, 102.4), 3)):
            with self.subTest(ticks=ticks):
                module.MONOTONIC = iter(ticks).__next__
                result = module.review(pin, "claude", str(self.prompt), 60)
                self.assertEqual(result["seconds"], want)
                record = self.folder / f"timed-{want}.md"
                module.record(record, result, "2026-11-08T00:00:00Z")
                self.assertIn(f"- Seconds: {want}\n", record.read_text(encoding="utf-8"))
                self.assertEqual(module.read_rounds(record.read_text(encoding="utf-8"))[0]["seconds"], want)

    def test_records_without_seconds_still_parse_and_a_bad_one_does_not(self):
        module = self.module()
        old = ("## Round 1: astra (`gpt-6-astra` via `codex`)\n\n- Writer: claude\n- Run: 2026-11-07T00:00:00Z\n"
               "- Lines: 1\n- Digest: {d}\n\nOVERALL: approve\n").format(d=module.digest(["OVERALL: approve"]))
        (entry,) = module.read_rounds(old)
        self.assertNotIn("seconds", entry)
        for bad in ("- Seconds: 1.5\n", "- Seconds: -1\n", "- Seconds: 2\n- Seconds: 3\n"):
            with self.subTest(bad=bad):
                self.assertIsNone(module.read_rounds(old.replace("- Lines:", bad + "- Lines:")))

    def test_the_review_skill_teaches_the_settled_line_forms_in_every_mirror(self):
        for root in (".claude", ".agents", ".codex"):
            with self.subTest(root=root):
                text = (SCRIPTS.parent / root / "skills/review-todo-section/SKILL.md").read_text(encoding="utf-8")
                self.assertIn("`- Settled: <item>`", text)
                self.assertIn("`- Not taken: <item> (<reason>)`", text)
                self.assertIn("carried into every later prompt", text)


if __name__ == "__main__":
    unittest.main()
