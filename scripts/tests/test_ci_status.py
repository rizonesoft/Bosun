"""The CI gate: a commit passes only on real evidence, and a run always gets one clear action."""
import contextlib
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
sys.path.insert(0, str(ROOT / "scripts"))
import ci_status

BUILD = """name: build
on:
  push:
    branches: [main]
    paths-ignore:
      - 'todo/**'
      - 'docs/**'
      - '**/*.md'
  pull_request:
    paths-ignore:
      - 'never/**'
jobs: {}
"""
PLAN = """name: plan
on:
  push:
    branches: [main]
    paths:
      - 'todo/**'
      - 'scripts/**'
  workflow_dispatch:
jobs: {}
"""
ALWAYS = """name: always
on:
  push:
    branches: [main]
jobs: {}
"""
SHA = "a" * 40
OLD = "b" * 40


def run(status="completed", conclusion="success", created="2026-11-07T10:00:00Z", sha=SHA, run_id=1):
    return {"databaseId": run_id, "headSha": sha, "status": status, "conclusion": conclusion,
            "url": f"https://x/{run_id}", "createdAt": created, "event": "push"}


class FakeGh:
    """Answers per workflow and records the arguments, so the lookup itself can be asserted."""

    def __init__(self, runs=None, fail=None, head=SHA, head_fails=None, dispatch_fails=None, on_dispatch=None):
        self.runs, self.fail, self.calls = runs or {}, fail or {}, []
        # The branch's remote head (or what a failed lookup says), and what starting a workflow does.
        self.head, self.head_fails, self.dispatch_fails, self.on_dispatch = head, head_fails, dispatch_fails, on_dispatch
        self.started = []

    def __call__(self, args):
        self.calls.append(args)
        if args[0] == "api":
            return (1, "", self.head_fails) if self.head_fails else (0, f"{self.head}\n", "")
        if args[:2] == ["workflow", "run"]:
            if self.dispatch_fails:
                return 1, "", self.dispatch_fails
            self.started.append(args[2:])
            if self.on_dispatch:
                self.on_dispatch(self, args[2])
            return 0, "", ""
        if args[:2] == ["run", "view"]:
            return 0, json.dumps({"jobs": [{"name": "Build", "status": "completed", "conclusion": "failure", "url": "u"}]}), ""
        workflow = args[args.index("--workflow") + 1]
        if workflow in self.fail:
            return 1, "", self.fail[workflow]
        return 0, json.dumps(self.runs.get(workflow, [])), ""


class FakeGit:
    def __init__(self, changed=("src/a.cpp",), workflows=None, ancestor=True, diff_code=0):
        self.changed, self.ancestor, self.diff_code = changed, ancestor, diff_code
        self.workflows = workflows if workflows is not None else {"build.yml": BUILD, "plan.yml": PLAN}

    def __call__(self, args):
        if args[0] == "rev-parse":
            ref = args[-1].split("^")[0]
            return (0, {"HEAD": SHA, "BASE": OLD, SHA: SHA, OLD: OLD}[ref] + "\n", "") if ref in ("HEAD", "BASE", SHA, OLD) else (1, "", "")
        if args[0] == "show":
            name = args[1].rsplit("/", 1)[1]
            return (0, self.workflows[name], "") if name in self.workflows else (128, "", "no such file")
        if args[0] == "merge-base":
            return (0 if self.ancestor else 1), "", ""
        if args[0] == "diff":
            return self.diff_code, "".join(name + "\0" for name in self.changed), ""
        raise AssertionError(args)


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        folder = self.root / ".github" / "workflows"
        folder.mkdir(parents=True)
        (folder / "build.yml").write_text(BUILD, encoding="utf-8")
        (folder / "plan.yml").write_text(PLAN, encoding="utf-8")

    def go(self, argv, gh, git=None, **kwargs):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = ci_status.main(argv, runner=gh, root=self.root, git=git or FakeGit(), **kwargs)
        self.stderr = err.getvalue()
        return code, out.getvalue()

    def verdicts(self, argv, gh, git=None):
        code, out = self.go([*argv, "--json"], gh, git)
        data = json.loads(out)
        return code, {e["workflow"]: e["verdict"] for e in data["workflows"]}, data


class Verdicts(Base):
    def test_empty_run_list_is_not_success(self):
        code, verdicts, _ = self.verdicts([], FakeGh())
        self.assertEqual(code, ci_status.EXIT_NONE)
        self.assertEqual(verdicts, {"build.yml": "none", "plan.yml": "none"})
        code, out = self.go([], FakeGh())
        self.assertEqual(code, ci_status.EXIT_NONE)
        self.assertIn("build.yml: none", out)

    def test_all_runs_pending(self):
        gh = FakeGh({w: [run("in_progress", None)] for w in ("build.yml", "plan.yml")})
        code, verdicts, _ = self.verdicts([], gh)
        self.assertEqual(code, ci_status.EXIT_PENDING)
        self.assertEqual(set(verdicts.values()), {"pending"})
        self.assertIn("still queued or in progress", self.go([], gh)[1])

    def test_older_green_with_newer_pending_is_pending(self):
        runs = [run(created="2026-11-07T10:00:00Z"), run("queued", None, "2026-11-07T11:00:00Z", run_id=2)]
        code, verdicts, _ = self.verdicts([], FakeGh({"build.yml": runs, "plan.yml": [run()]}))
        self.assertEqual(code, ci_status.EXIT_PENDING)
        self.assertEqual(verdicts, {"build.yml": "pending", "plan.yml": "green"})

    def test_green_needs_every_workflow(self):
        gh = FakeGh({"build.yml": [run()], "plan.yml": [run()]})
        self.assertEqual(self.verdicts([], gh)[0], 0)

    def test_red_names_the_failed_job_in_text_and_json(self):
        gh = FakeGh({"build.yml": [run(conclusion="failure")], "plan.yml": [run()]})
        code, out = self.go([], gh)
        self.assertEqual(code, 1)
        self.assertIn("failed job: Build", out)
        _, verdicts, data = self.verdicts([], gh)
        self.assertEqual(verdicts["build.yml"], "red")
        self.assertEqual(data["workflows"][0]["failed_jobs"][0]["name"], "Build")

    def test_cancelled_judges_nothing(self):
        runs = [run(conclusion="cancelled", created="2026-11-07T12:00:00Z", run_id=2), run(created="2026-11-07T10:00:00Z")]
        _, verdicts, _ = self.verdicts([], FakeGh({"build.yml": runs, "plan.yml": [run()]}))
        self.assertEqual(verdicts["build.yml"], "green")
        only = [run(conclusion="cancelled")]
        _, verdicts, _ = self.verdicts([], FakeGh({"build.yml": only, "plan.yml": [run()]}))
        self.assertEqual(verdicts["build.yml"], "none")


class Commit(Base):
    def test_exact_commit_is_read_with_the_commit_filter(self):
        gh = FakeGh({"build.yml": [run()], "plan.yml": [run()]})
        code, _, data = self.verdicts(["--sha", "HEAD"], gh)
        self.assertEqual(code, 0)
        self.assertEqual(data["sha"], SHA)
        lookup = next(c for c in gh.calls if c[:2] == ["run", "list"])
        self.assertEqual(lookup[lookup.index("--commit") + 1], SHA)
        self.assertNotIn("--branch", lookup)

    def test_green_older_commit_cannot_satisfy_a_newer_one(self):
        older = [run(sha=OLD)]

        def gh(args):
            # Like the real gh: --commit filters by commit, and --branch returns the older green.
            if "--commit" in args and args[args.index("--commit") + 1] != OLD:
                return 0, "[]", ""
            return 0, json.dumps(older), ""
        code, verdicts, _ = self.verdicts(["--sha", "HEAD"], gh)
        self.assertEqual(code, ci_status.EXIT_NONE)
        self.assertEqual(verdicts["build.yml"], "none")
        # The same fake passes the older commit itself, so the test can tell the two apart.
        self.assertEqual(self.verdicts(["--sha", "BASE"], gh)[0], 0)

    def test_newer_attempts_of_the_same_commit_decide(self):
        green = run(created="2026-11-07T10:00:00Z")
        failed = run(conclusion="failure", created="2026-11-07T11:00:00Z", run_id=2)
        pending = run("in_progress", None, "2026-11-07T11:00:00Z", run_id=2)
        self.assertEqual(self.verdicts(["--sha", "HEAD"], FakeGh({"build.yml": [green, failed], "plan.yml": [green]}))[0], 1)
        self.assertEqual(self.verdicts(["--sha", "HEAD"], FakeGh({"build.yml": [green, pending], "plan.yml": [green]}))[0], ci_status.EXIT_PENDING)

    def test_bad_sha_is_usage(self):
        with self.assertRaises(SystemExit) as caught:
            self.go(["--sha", "nope"], FakeGh())
        self.assertEqual(caught.exception.code, 2)


class Skips(Base):
    def check(self, changed, **git):
        gh = FakeGh({"plan.yml": [run()]})
        return self.verdicts(["--sha", "HEAD", "--base", "BASE"], gh, FakeGit(changed, **git))

    def test_markdown_only_push_skips_build_with_a_reason(self):
        code, verdicts, data = self.check(["README.md", "docs/a.txt", "todo/x.md"])
        self.assertEqual(code, 0)
        self.assertEqual(verdicts["build.yml"], "skipped")
        self.assertIn("paths-ignore", data["workflows"][0]["reason"])

    def test_code_commit_before_a_markdown_stamp_is_not_skipped(self):
        code, verdicts, _ = self.check(["src/a.cpp", "todo/plan.md"])
        self.assertEqual(verdicts["build.yml"], "none")
        self.assertEqual(code, ci_status.EXIT_NONE)

    def test_paths_filter_skips_only_when_nothing_matches(self):
        gh = FakeGh({"build.yml": [run()]})
        self.assertEqual(self.verdicts(["--sha", "HEAD", "--base", "BASE"], gh, FakeGit(["src/a.cpp"]))[1]["plan.yml"], "skipped")
        self.assertEqual(self.verdicts(["--sha", "HEAD", "--base", "BASE"], gh, FakeGit(["scripts/a.py"]))[1]["plan.yml"], "none")

    def test_no_base_cannot_prove_a_skip(self):
        code, verdicts, _ = self.verdicts(["--sha", "HEAD"], FakeGh({"plan.yml": [run()]}), FakeGit(["README.md"]))
        self.assertEqual(verdicts["build.yml"], "none")
        self.assertEqual(code, ci_status.EXIT_NONE)

    def test_unknown_base_cannot_prove_a_skip(self):
        code, verdicts, _ = self.verdicts(["--sha", "HEAD", "--base", "gone"], FakeGh({"plan.yml": [run()]}), FakeGit(["README.md"]))
        self.assertEqual(verdicts["build.yml"], "none")

    def test_unprovable_cases_read_none(self):
        for name, git in (("unreadable workflow", dict(workflows={})),
                          ("no filters", dict(workflows={"build.yml": ALWAYS, "plan.yml": PLAN})),
                          ("failed diff", dict(diff_code=128)),
                          ("not an ancestor", dict(ancestor=False)),
                          ("garbled workflow", dict(workflows={"build.yml": "on: [push", "plan.yml": PLAN}))):
            with self.subTest(name):
                self.assertEqual(self.check(["README.md"], **git)[1]["build.yml"], "none")
        self.assertEqual(self.check(["a.md"] * 301)[1]["build.yml"], "none")

    def exact_revert(self):
        """A passed, then C with no file different from A."""
        a, c = OLD, SHA
        self.assertEqual(self.go(["--gate", "--sha", OLD], FakeGh({"build.yml": [run(sha=a)], "plan.yml": [run(sha=a)]}), FakeGit())[0], 0)
        return FakeGh(), FakeGit([])

    def test_an_exact_revert_to_the_proven_commit_is_a_skip(self):
        gh, git = self.exact_revert()
        code, out = self.go(["--gate", "--sha", "HEAD", "--json"], gh, git)
        data = json.loads(out)
        self.assertEqual((code, data["action"]), (0, "proceed"))
        self.assertIn("no file differs", data["workflows"][0]["reason"])

    def test_use_proven_recognises_an_exact_revert(self):
        gh, git = self.exact_revert()
        self.assertEqual(self.go(["--sha", "HEAD", "--use-proven"], gh, git)[0], 0)

    def test_an_explicit_base_proves_nothing_from_no_change(self):
        gh, git = self.exact_revert()
        self.assertEqual(self.go(["--sha", "HEAD", "--base", "BASE"], gh, git)[0], ci_status.EXIT_NONE)

    def test_empty_diff_proves_nothing(self):
        self.assertEqual(self.check([])[1]["build.yml"], "none")

    def test_negated_or_unknown_patterns_prove_nothing(self):
        odd = BUILD.replace("'todo/**'", "'!todo/**'")
        self.assertEqual(self.check(["todo/x.md"], workflows={"build.yml": odd, "plan.yml": PLAN})[1]["build.yml"], "none")

    def test_pull_request_filters_are_not_read_as_push_filters(self):
        pr_only = "on:\n  pull_request:\n    paths-ignore:\n      - '**/*.md'\n  push:\n    branches: [main]\njobs: {}\n"
        self.assertEqual(self.check(["README.md"], workflows={"build.yml": pr_only, "plan.yml": PLAN})[1]["build.yml"], "none")

    def test_a_run_outranks_a_skip(self):
        gh = FakeGh({"build.yml": [run(conclusion="failure")], "plan.yml": [run()]})
        self.assertEqual(self.verdicts(["--sha", "HEAD", "--base", "BASE"], gh, FakeGit(["README.md"]))[0], 1)

    def test_real_git_non_ascii_name_is_matched(self):
        def git(*args):
            return subprocess.run(["git", *args], cwd=self.root, capture_output=True, text=True, encoding="utf-8", check=True).stdout.strip()
        git("init", "-q")
        git("config", "user.email", "t@example.invalid")
        git("config", "user.name", "t")
        git("config", "core.hooksPath", "/dev/null")
        (self.root / "README.md").write_text("a\n")
        git("add", "-A")
        git("commit", "-qm", "base")
        base = git("rev-parse", "HEAD")
        (self.root / "scripts").mkdir()
        (self.root / "scripts" / "caf\u00e9.py").write_text("x = 1\n", encoding="utf-8")
        git("add", "-A")
        git("commit", "-qm", "code")
        gh = FakeGh({"build.yml": [run()]})
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            ci_status.main(["--sha", "HEAD", "--base", base, "--json"], runner=gh, root=self.root)
        verdicts = {e["workflow"]: e["verdict"] for e in json.loads(out.getvalue())["workflows"]}
        self.assertEqual(verdicts["plan.yml"], "none")

    def test_real_git_code_then_stamp_in_one_push(self):
        def git(*args):
            return subprocess.run(["git", *args], cwd=self.root, capture_output=True, text=True, check=True).stdout.strip()
        git("init", "-q")
        git("config", "user.email", "t@example.invalid")
        git("config", "user.name", "t")
        git("config", "core.hooksPath", "/dev/null")
        (self.root / "README.md").write_text("a\n")
        git("add", "-A")
        git("commit", "-qm", "base")
        base = git("rev-parse", "HEAD")
        (self.root / "main.cpp").write_text("int main(){}\n")
        git("add", "-A")
        git("commit", "-qm", "code")
        (self.root / "README.md").write_text("stamp\n")
        git("add", "-A")
        git("commit", "-qm", "stamp")
        stamp_base = git("rev-parse", "HEAD~1")
        gh = FakeGh({"plan.yml": [run()]})
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            ci_status.main(["--sha", "HEAD", "--base", base, "--json"], runner=gh, root=self.root)
        self.assertEqual(json.loads(out.getvalue())["workflows"][0]["verdict"], "none")
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            ci_status.main(["--sha", "HEAD", "--base", stamp_base, "--json"], runner=gh, root=self.root)
        self.assertEqual(json.loads(out.getvalue())["workflows"][0]["verdict"], "skipped")


class OverallExit(unittest.TestCase):
    def test_table(self):
        for verdicts, code in ((["green", "skipped"], 0), (["green", "green"], 0), (["skipped", "skipped"], 0),
                               (["green", "none"], 5), (["none", "pending"], 4), (["pending", "unavailable"], 3),
                               (["unavailable", "red"], 1), (["red", "pending", "none", "green"], 1), ([], 5)):
            with self.subTest(verdicts):
                self.assertEqual(ci_status.overall_exit(verdicts), code)


class Unreachable(Base):
    def test_one_failure_doesnt_hide_a_red(self):
        gh = FakeGh({"plan.yml": [run(conclusion="failure")]}, fail={"build.yml": "auth required"})
        code, verdicts, data = self.verdicts([], gh)
        self.assertEqual(code, 1)
        self.assertEqual(verdicts["build.yml"], "unavailable")
        self.assertIn("auth required", data["error"])

    def test_unavailable_alone_is_exit_3_in_text_and_json(self):
        gh = FakeGh(fail={"build.yml": "boom", "plan.yml": "boom"})
        code, out = self.go([], gh)
        self.assertEqual(code, 3)
        self.assertIn("unavailable: gh run list exited 1: boom", out)
        self.assertEqual(self.verdicts([], gh)[0], 3)

    def test_gh_not_installed_is_unavailable(self):
        def missing(args):
            raise FileNotFoundError
        self.assertEqual(self.go([], missing)[0], 3)


class Gate(Base):
    def gate(self, gh, sha="HEAD", git=None, extra=()):
        code, out = self.go(["--gate", "--sha", sha, *extra], gh, git or FakeGit())
        return code, out.strip().splitlines()[-1]

    def red(self):
        return FakeGh({"build.yml": [run(conclusion="failure")], "plan.yml": [run()]})

    def green(self):
        return FakeGh({"build.yml": [run()], "plan.yml": [run()]})

    def state(self):
        return self.root / ci_status.REPAIRS

    def test_needs_sha(self):
        with self.assertRaises(SystemExit):
            self.go(["--gate"], self.green())

    def test_proceed(self):
        self.assertEqual(self.gate(self.green()), (0, "action: proceed"))

    def test_proceed_on_a_proven_skip(self):
        self.gate(self.green(), OLD)
        gh = FakeGh({"plan.yml": [run()]})
        self.assertEqual(self.gate(gh, git=FakeGit(["a.md"])), (0, "action: proceed"))

    def test_the_gate_takes_no_base(self):
        with self.assertRaises(SystemExit) as caught:
            self.go(["--gate", "--sha", "HEAD", "--base", "BASE"], self.green())
        self.assertEqual(caught.exception.code, 2)
        self.assertIn("takes no --base", self.err_of(["--gate", "--sha", "HEAD", "--base", "BASE"]))

    def err_of(self, argv):
        err = io.StringIO()
        with contextlib.redirect_stderr(err), self.assertRaises(SystemExit):
            ci_status.main(argv, runner=self.green(), root=self.root, git=FakeGit())
        return err.getvalue()

    def test_git_timing_out_is_a_gate_answer_not_a_usage_error(self):
        def stuck(args):
            raise ci_status.Unavailable("git could not run: timed out")
        for extra, text in ((["--json"], '"action": "wait"'), ([], "action: wait")):
            with self.subTest(extra):
                code, out = self.go(["--gate", "--sha", "HEAD", *extra], self.green(), stuck)
                self.assertEqual(code, ci_status.EXIT_UNAVAILABLE)
                self.assertIn(text, out)
        code, out = self.go(["--sha", "HEAD"], self.green(), stuck)  # without a gate too
        self.assertEqual(code, ci_status.EXIT_UNAVAILABLE)
        self.assertIn("unavailable", out)

    def test_git_staying_unavailable_still_ends(self):
        def stuck(args):
            raise ci_status.Unavailable("git could not run")
        wall = lambda: 1_000_000.0
        self.go(["--gate", "--sha", "HEAD"], self.green(), stuck, wall=wall)
        later = lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS
        self.assertIn("action: unverified", self.go(["--gate", "--sha", "HEAD"], self.green(), stuck, wall=later)[1])

    def test_a_bad_commit_is_still_a_usage_error(self):
        with self.assertRaises(SystemExit) as caught:
            self.go(["--gate", "--sha", "nope"], self.green())
        self.assertEqual(caught.exception.code, 2)

    def test_no_tool_starts_once_the_wait_has_passed(self):
        clock, seen = _Clock(), []

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            seen.append(name)
            clock.t += timeout  # one call uses the whole budget
            return 0, SHA + "\n", ""
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", tool), mock.patch.object(ci_status, "RESOLVE_SECONDS", 120), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
            ci_status.main(["--gate", "--sha", "HEAD", "--wait-minutes", "0.1"], root=self.root, clock=clock.now, sleep=clock.sleep)
        self.assertEqual(seen, ["git"])
        self.assertEqual(clock.t, 6)
        self.assertIn("action: wait", out.getvalue())

    def test_wait_on_pending_none_and_unavailable(self):
        self.assertEqual(self.gate(FakeGh({"build.yml": [run("queued", None)], "plan.yml": [run()]})), (4, "action: wait"))
        self.assertEqual(self.gate(FakeGh()), (5, "action: wait"))
        self.assertEqual(self.gate(FakeGh(fail={"build.yml": "x", "plan.yml": "x"})), (3, "action: wait"))
        self.assertNotIn("seen", json.loads(self.state().read_text()))  # waiting spends no repair attempt

    def test_repairs_count_by_commit_then_exhaust(self):
        git_for = lambda: FakeGit()
        self.assertEqual(self.gate(self.red(), SHA, git_for()), (1, "action: repair (attempt 1 of 3)"))
        # Rechecking the same red commit spends nothing.
        self.assertEqual(self.gate(self.red(), SHA, git_for()), (1, "action: repair (attempt 1 of 3)"))
        for number, sha in ((2, "c"), (3, "d")):
            git = _Named(sha * 40)
            self.assertEqual(self.gate(self.red(), sha * 40, git), (1, f"action: repair (attempt {number} of 3)"))
        self.assertEqual(self.gate(self.red(), "e" * 40, _Named("e" * 40)), (1, "action: exhausted"))
        record = json.loads(self.state().read_text())
        self.assertEqual(record["first_red"], SHA)

    def test_count_survives_a_new_process_and_green_clears_it(self):
        self.gate(self.red(), SHA)
        self.assertEqual(json.loads(self.state().read_text())["seen"], [SHA])
        script = (
            "import json, sys\n"
            f"sys.path.insert(0, {str(ROOT / 'scripts')!r})\n"
            "import ci_status\n"
            "def gh(args):\n"
            "    if args[:2] == ['run', 'view']:\n"
            "        return 0, json.dumps({'jobs': []}), ''\n"
            "    return 0, json.dumps([{'status': 'completed', 'conclusion': 'failure', 'createdAt': 'x', 'databaseId': 1}]), ''\n"
            f"def git(args):\n    return 0, {'c' * 40!r} + '\\n', ''\n"
            f"sys.exit(ci_status.main(['--gate', '--sha', {'c' * 40!r}], runner=gh, root={str(self.root)!r}, git=git))\n"
        )
        done = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True)
        self.assertEqual(done.returncode, 1, done.stderr)
        self.assertEqual(done.stdout.strip().splitlines()[-1], "action: repair (attempt 2 of 3)")
        self.assertEqual(self.gate(self.green())[1], "action: proceed")
        self.assertEqual(json.loads(self.state().read_text()), {"proven": SHA})
        self.assertEqual(self.gate(self.red())[1], "action: repair (attempt 1 of 3)")

    def test_a_repair_that_goes_green_proceeds(self):
        self.assertIn("repair", self.gate(self.red())[1])
        self.assertEqual(self.gate(self.green()), (0, "action: proceed"))

    def test_corrupt_record_reads_as_none_used_with_a_warning(self):
        self.state().parent.mkdir(parents=True)
        self.state().write_text("{not json")
        self.assertEqual(self.gate(self.red())[1], "action: repair (attempt 1 of 3)")
        self.assertIn("warning:", self.stderr)

    def test_state_that_cannot_be_saved_carries_on_as_unverified(self):
        with mock.patch.object(ci_status, "write_gate", return_value=False):
            self.assertEqual(self.gate(self.red()), (1, "action: unverified"))
            self.assertEqual(self.gate(FakeGh()), (5, "action: unverified"))
        code, out = self.go(["--gate", "--sha", "HEAD", "--json"], self.green())
        self.assertEqual((code, json.loads(out)["action"]), (0, "proceed"))

    def test_a_locked_record_through_a_green_leaves_a_full_budget_for_the_next_fault(self):
        for sha in (SHA, "c" * 40, "d" * 40, "e" * 40):
            self.gate(self.red(), sha, _Named(sha))
        self.assertEqual(self.gate(self.red(), "e" * 40, _Named("e" * 40))[1], "action: exhausted")
        real = ci_status.write_gate

        def main_file_locked(path, state):
            return False if path.name == ci_status.REPAIRS.name else real(path, state)
        with mock.patch.object(ci_status, "write_gate", main_file_locked):
            self.assertEqual(self.gate(self.green()), (0, "action: proceed"))  # the marker holds it
        self.assertTrue((self.root / ci_status.CLEARED).exists())
        script = (
            "import json, sys\n"
            f"sys.path.insert(0, {str(ROOT / 'scripts')!r})\n"
            "import ci_status\n"
            "def gh(args):\n"
            "    if args[:2] == ['run', 'view']:\n"
            "        return 0, json.dumps({'jobs': []}), ''\n"
            "    return 0, json.dumps([{'status': 'completed', 'conclusion': 'failure', 'createdAt': 'x', 'databaseId': 1}]), ''\n"
            f"def git(args):\n    return 0, {'f' * 40!r} + '\\n', ''\n"
            f"sys.exit(ci_status.main(['--gate', '--sha', {'f' * 40!r}], runner=gh, root={str(self.root)!r}, git=git))\n"
        )
        done = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True)
        self.assertEqual(done.returncode, 1, done.stderr)
        self.assertEqual(done.stdout.strip().splitlines()[-1], "action: repair (attempt 1 of 3)")
        self.assertFalse((self.root / ci_status.CLEARED).exists())

    def test_when_neither_record_can_be_written_a_green_is_unverified(self):
        (self.root / ci_status.CLEARED).mkdir(parents=True)  # a folder where the marker goes
        with mock.patch.object(ci_status, "write_gate", return_value=False):
            self.assertEqual(self.gate(self.green()), (0, "action: unverified"))
        self.assertIn("could not clear", self.stderr)

    def waiting(self, seconds):
        wall = lambda: 1_000_000.0 + seconds
        return self.go(["--gate", "--sha", "HEAD"], FakeGh(), wall=wall)

    def test_the_gate_takes_no_workflow_filter(self):
        err = self.err_of(["--gate", "--sha", "HEAD", "--workflow", "build.yml"])
        self.assertIn("takes no --workflow", err)
        code, _ = self.go(["--sha", "HEAD", "--workflow", "build.yml"], self.green())
        self.assertEqual(code, 0)

    def test_the_start_of_a_wait_stands_if_the_first_lookup_is_interrupted(self):
        def killed(args):
            raise KeyboardInterrupt
        with self.assertRaises(KeyboardInterrupt):
            self.go(["--gate", "--sha", "HEAD"], killed, wall=lambda: 1_000_000.0)
        self.assertEqual(json.loads(self.state().read_text())["waiting"]["since"], 1_000_000.0)
        code, out = self.go(["--gate", "--sha", "HEAD"], FakeGh(), wall=lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS)
        self.assertIn("action: unverified", out)

    def test_a_wait_names_the_gate_polling_it(self):
        # The run guard holds the next row only while this process lives (scripts/lib/run_guard.py ci_in_flight).
        self.gate(FakeGh())
        self.assertEqual(json.loads(self.state().read_text())["waiting"]["pid"], os.getpid())
        record = json.loads(self.state().read_text())
        record["waiting"]["pid"] = 1  # an earlier gate's, now gone
        self.state().write_text(json.dumps(record))
        self.gate(FakeGh())
        after = json.loads(self.state().read_text())["waiting"]
        self.assertEqual((after["pid"], after["since"]), (os.getpid(), record["waiting"]["since"]))

    def test_a_verdict_leaves_no_wait_behind(self):
        self.gate(FakeGh())
        self.assertIn("waiting", json.loads(self.state().read_text()))
        self.gate(self.red())
        self.assertNotIn("waiting", json.loads(self.state().read_text()))
        self.gate(FakeGh())
        self.gate(self.green())
        self.assertNotIn("waiting", json.loads(self.state().read_text()))

    def test_alternating_waits_keep_the_first_start_across_processes(self):
        start = 1_000_000.0
        pending = {"build.yml": [run("queued", None)], "plan.yml": [run()]}
        steps = [(pending, 0), ({}, 3000), (pending, 6000), ({}, ci_status.WAIT_LIMIT_SECONDS - 1)]
        for runs, offset in steps:
            self.assertIn("action: wait", self.fresh(["--gate", "--sha", "HEAD"], runs, wall=start + offset)[1])
        self.assertEqual(json.loads(self.state().read_text())["waiting"]["since"], start)
        self.assertIn("action: unverified", self.fresh(["--gate", "--sha", "HEAD"], pending, wall=start + ci_status.WAIT_LIMIT_SECONDS)[1])

    def test_use_proven_recognises_a_skipped_final_stamp_and_changes_nothing(self):
        self.gate(self.green(), OLD)
        gh = FakeGh({"plan.yml": [run()]})
        self.assertEqual(self.gate(gh, git=FakeGit(["README.md"]))[1], "action: proceed")
        before = self.state().read_bytes()
        self.assertEqual(self.go(["--sha", "HEAD", "--use-proven"], gh, FakeGit(["README.md"]))[0], 0)  # the stamp itself
        self.assertEqual(self.state().read_bytes(), before)
        self.assertEqual(self.go(["--gate", "--sha", "HEAD"], gh, FakeGit(["README.md"]))[0], 0)  # and a repeat gate agrees

    def test_use_proven_refuses_code_after_the_proven_commit(self):
        self.gate(self.green(), OLD)
        before = self.state().read_bytes()
        code, _ = self.go(["--sha", "HEAD", "--use-proven"], FakeGh({"plan.yml": [run()]}), FakeGit(["src/a.cpp"]))
        self.assertEqual(code, ci_status.EXIT_NONE)
        self.assertEqual(self.state().read_bytes(), before)

    def test_a_real_red_run_outranks_the_record(self):
        self.gate(self.green())
        failed = FakeGh({"build.yml": [run(conclusion="failure")], "plan.yml": [run()]})
        self.assertEqual(self.go(["--sha", "HEAD", "--use-proven"], failed)[0], 1)

    def test_use_proven_with_no_record_proves_nothing(self):
        self.assertEqual(self.go(["--sha", "HEAD", "--use-proven"], FakeGh())[0], ci_status.EXIT_NONE)
        self.assertFalse(self.state().exists())

    def test_use_proven_honours_a_marker_the_gate_has_not_taken_up(self):
        marker = self.root / ci_status.CLEARED
        marker.parent.mkdir(parents=True)
        marker.write_text(json.dumps({"proven": SHA, "at": 5}), encoding="utf-8")
        self.assertEqual(self.go(["--sha", "HEAD", "--use-proven"], FakeGh())[0], 0)
        self.assertTrue(marker.exists())

    def test_use_proven_finds_the_marker_when_the_record_is_locked(self):
        marker = self.root / ci_status.CLEARED
        marker.parent.mkdir(parents=True)
        marker.write_text(json.dumps({"proven": SHA, "at": 5}), encoding="utf-8")
        self.state().mkdir()  # a folder at the path reads as permission denied, like a lock
        self.assertEqual(self.go(["--sha", "HEAD", "--use-proven"], FakeGh())[0], 0)

    def test_use_proven_with_an_unreadable_record_and_no_marker_proves_nothing(self):
        self.state().mkdir(parents=True)
        self.assertEqual(self.go(["--sha", "HEAD", "--use-proven"], FakeGh())[0], ci_status.EXIT_NONE)
        self.assertIn("can't be read", self.stderr)

    def test_use_proven_ignores_a_marker_already_taken_up(self):
        marker = self.root / ci_status.CLEARED
        marker.parent.mkdir(parents=True)
        marker.write_text(json.dumps({"proven": SHA, "at": 5}), encoding="utf-8")
        self.state().write_text(json.dumps({"proven": OLD, "consumed": f"{SHA} 5"}), encoding="utf-8")
        self.assertEqual(self.go(["--sha", "HEAD", "--use-proven"], FakeGh({"plan.yml": [run()]}), FakeGit(["src/a.cpp"]))[0], ci_status.EXIT_NONE)

    def test_use_proven_refuses_other_modes(self):
        for extra in (["--gate"], ["--base", "BASE"]):
            with self.subTest(extra):
                self.assertIn("--use-proven needs", self.err_of(["--sha", "HEAD", "--use-proven", *extra]))
        self.assertIn("--use-proven needs", self.err_of(["--use-proven"]))

    def test_every_unverified_says_why_and_the_action_stays_last(self):
        expired = lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS

        def cases():
            self.go(["--gate", "--sha", "HEAD"], FakeGh(), wall=lambda: 1_000_000.0)
            yield "limit", FakeGh(), expired, "7200"
            self.state().unlink()
            with mock.patch.object(ci_status, "write_gate", return_value=False):
                yield "red, unsaved", self.red(), None, "count repairs"
                yield "waiting, unsaved", FakeGh(), None, "count the wait"
                (self.root / ci_status.CLEARED).parent.mkdir(exist_ok=True)
                yield "green, unsaved", self.green(), None, "could be saved"
        for name, gh, wall, word in cases():
            with self.subTest(name):
                extra = {"wall": wall} if wall else {}
                _, text = self.go(["--gate", "--sha", "HEAD"], gh, **extra)
                lines = text.strip().splitlines()
                self.assertEqual(lines[-1], "action: unverified")
                self.assertIn(word, lines[-2])
                self.assertTrue(lines[-2].startswith("reason: "))
                _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], gh, **extra)
                data = json.loads(raw)
                self.assertEqual(data["action"], "unverified")
                self.assertIn(word, data["reason"])

    def test_waiting_on_one_commit_is_limited_across_invocations(self):
        start = 1_000_000.0
        self.assertIn("action: wait", self.fresh(["--gate", "--sha", "HEAD"], wall=start)[1])
        self.assertIn("action: wait", self.fresh(["--gate", "--sha", "HEAD"], wall=start + ci_status.WAIT_LIMIT_SECONDS - 1)[1])
        self.assertIn("action: unverified", self.fresh(["--gate", "--sha", "HEAD"], wall=start + ci_status.WAIT_LIMIT_SECONDS)[1])
        record = json.loads(self.state().read_text())
        self.assertEqual(record["waiting"]["since"], 1_000_000.0)  # durable, not reset by each call

    def test_the_limit_counts_from_the_first_look_and_caps_a_call(self):
        clock = _Clock()
        wall = lambda: 1_000_000.0 + clock.t
        code, out = self.go(["--gate", "--sha", "HEAD", "--wait-minutes", "30"], FakeGh(), clock=clock.now, sleep=clock.sleep, wall=wall)
        self.assertEqual(clock.t, 1800)
        self.assertEqual(json.loads(self.state().read_text())["waiting"]["since"], 1_000_000.0)
        # Three more 30 minute calls take it to exactly two hours, not two and a half.
        for _ in range(3):
            code, out = self.go(["--gate", "--sha", "HEAD", "--wait-minutes", "30"], FakeGh(), clock=clock.now, sleep=clock.sleep, wall=wall)
        self.assertEqual(clock.t, ci_status.WAIT_LIMIT_SECONDS)
        self.assertIn("action: unverified", out)

    def test_a_call_past_the_limit_launches_nothing(self):
        self.waiting(0)
        clock, gh = _Clock(), FakeGh()
        wall = lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS
        code, out = self.go(["--gate", "--sha", "HEAD", "--wait-minutes", "30"], gh, clock=clock.now, sleep=clock.sleep, wall=wall)
        self.assertEqual((clock.sleeps, gh.calls), (0, []))
        self.assertIn("action: unverified", out)
        code, out = self.go(["--gate", "--sha", "HEAD", "--json", "--wait-minutes", "30"], gh, clock=clock.now, sleep=clock.sleep, wall=wall)
        self.assertEqual(json.loads(out)["action"], "unverified")

    def test_a_killed_process_keeps_the_time_it_waited(self):
        clock = _Clock()
        wall = lambda: 1_000_000.0 + clock.t

        def killed(seconds):
            raise KeyboardInterrupt
        with self.assertRaises(KeyboardInterrupt):
            self.go(["--gate", "--sha", "HEAD", "--wait-minutes", "30"], FakeGh(), clock=clock.now, sleep=killed, wall=wall)
        self.assertEqual(json.loads(self.state().read_text())["waiting"]["since"], 1_000_000.0)

    def test_a_marker_that_cannot_be_deleted_is_honoured_once(self):
        marker = self.root / ci_status.CLEARED
        marker.parent.mkdir(parents=True, exist_ok=True)
        marker.write_text(json.dumps({"proven": OLD, "at": 5}), encoding="utf-8")
        real = Path.unlink

        def stuck(path, *args, **kwargs):
            if path.name == marker.name:
                raise PermissionError("sharing violation")
            return real(path, *args, **kwargs)
        with mock.patch.object(Path, "unlink", stuck):
            actions = [self.gate(self.red(), sha * 40, _Named(sha * 40))[1] for sha in "cdef"]
        self.assertEqual(actions, ["action: repair (attempt 1 of 3)", "action: repair (attempt 2 of 3)",
                                   "action: repair (attempt 3 of 3)", "action: exhausted"])
        self.assertIn("won't be again", self.stderr)
        self.assertEqual(json.loads(self.state().read_text())["proven"], OLD)

    def test_a_new_commit_starts_its_own_wait(self):
        self.waiting(0)
        wall = lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS
        code, out = self.go(["--gate", "--sha", "c" * 40], FakeGh(), _Named("c" * 40), wall=wall)
        self.assertEqual(json.loads(self.state().read_text())["waiting"], {"sha": "c" * 40, "since": wall(), "pid": os.getpid()})
        self.assertIn("action: wait", out)

    def fresh(self, argv, runs=None, changed=("README.md",), wall=1_000_000.0):
        """One step in a new interpreter, so nothing but the disk carries over."""
        script = (
            "import json, sys\n"
            f"sys.path[:0] = [{str(ROOT / 'scripts')!r}, {str(ROOT / 'scripts' / 'tests')!r}]\n"
            "import ci_status, test_ci_status as t\n"
            f"sys.exit(ci_status.main({argv!r}, runner=t.FakeGh({runs or {}!r}), root={str(self.root)!r}, git=t.FakeGit({list(changed)!r}), wall=lambda: {wall!r}))\n"
        )
        done = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True)
        return done.returncode, done.stdout, done.stderr

    def test_a_restart_after_a_push_still_has_its_base(self):
        green = {"build.yml": [run(sha=OLD)], "plan.yml": [run(sha=OLD)]}
        self.assertEqual(self.fresh(["--gate", "--sha", OLD], green)[0], 0)
        record = json.loads(self.state().read_text())
        self.assertEqual(record["proven"], OLD)
        code, out, err = self.fresh(["--gate", "--sha", "HEAD", "--json"], {"plan.yml": [run()]})
        data = json.loads(out)
        self.assertEqual((code, data["action"], data["base"]), (0, "proceed", OLD), err)
        self.assertEqual(data["workflows"][0]["verdict"], "skipped")

    def test_a_restart_while_waiting_keeps_the_proven_base(self):
        green = {"build.yml": [run(sha=OLD)], "plan.yml": [run(sha=OLD)]}
        self.fresh(["--gate", "--sha", OLD], green)
        for _ in range(2):  # the same answer from a new process each time
            code, out, err = self.fresh(["--gate", "--sha", "HEAD", "--json"], changed=["src/a.cpp"])
            data = json.loads(out)
            self.assertEqual((code, data["action"], data["base"]), (ci_status.EXIT_NONE, "wait", OLD), err)

    def history(self, code_from):
        """A real answer for each base: the diff from A holds code only when `code_from` says so."""
        a = "a" * 40

        class Git(FakeGit):
            def __call__(self, args):
                if args[0] == "rev-parse":
                    return 0, args[-1].split("^")[0] + "\n", ""
                if args[0] == "diff":
                    return 0, ("src/a.cpp\0README.md\0" if code_from else "README.md\0"), ""
                return super().__call__(args)
        self.go(["--gate", "--sha", a], FakeGh({"build.yml": [run(sha=a)], "plan.yml": [run(sha=a)]}), Git())
        return Git()

    def test_markdown_after_a_failed_commit_is_not_a_skip(self):
        # A passed, B failed, C only edits Markdown: B's code still shows in the diff from A.
        git = self.history(code_from=True)
        b, c = "b" * 40, "c" * 40
        self.assertEqual(self.gate(self.red(), b, git)[1], "action: repair (attempt 1 of 3)")
        code, out = self.go(["--gate", "--sha", c, "--json"], FakeGh({"plan.yml": [run(sha=c)]}), git)
        data = json.loads(out)
        self.assertEqual((data["base"], data["workflows"][0]["verdict"], data["action"]), ("a" * 40, "none", "wait"))
        self.assertIn("seen", json.loads(self.state().read_text()))  # the fault isn't forgotten

    def test_markdown_after_an_unverified_commit_is_not_a_skip(self):
        git = self.history(code_from=True)
        self.go(["--gate", "--sha", "b" * 40], FakeGh(), git)
        c = "c" * 40
        code, out = self.go(["--gate", "--sha", c, "--json"], FakeGh({"plan.yml": [run(sha=c)]}), git)
        self.assertEqual(json.loads(out)["action"], "wait")

    def test_a_full_revert_really_is_unchanged_inputs(self):
        # A passed, B pushed and never gated, C reverts B and edits Markdown: what builds from C is what passed at A.
        git = self.history(code_from=False)
        c = "c" * 40
        code, out = self.go(["--gate", "--sha", c, "--json"], FakeGh({"plan.yml": [run(sha=c)]}), git)
        data = json.loads(out)
        self.assertEqual((data["base"], data["workflows"][0]["verdict"], data["action"]), ("a" * 40, "skipped", "proceed"))

    def test_a_real_run_of_the_commit_still_decides_first(self):
        git = self.history(code_from=False)
        c = "c" * 40
        failed = FakeGh({"build.yml": [run(conclusion="failure", sha=c)], "plan.yml": [run(sha=c)]})
        self.assertEqual(self.go(["--gate", "--sha", c], failed, git)[0], 1)
        pending = FakeGh({"build.yml": [run("in_progress", None, sha=c)], "plan.yml": [run(sha=c)]})
        self.assertEqual(self.go(["--gate", "--sha", c], pending, git)[0], ci_status.EXIT_PENDING)

    def test_no_proven_commit_proves_no_skip(self):
        code, out = self.go(["--gate", "--sha", "HEAD", "--json"], FakeGh({"plan.yml": [run()]}), FakeGit(["README.md"]))
        self.assertEqual(json.loads(out)["workflows"][0]["verdict"], "none")

    def test_an_incomplete_marker_resets_nothing(self):
        self.gate(self.red())
        marker = self.root / ci_status.CLEARED
        for text in ("", "{", '{"proven": "x"}', "[1]"):
            with self.subTest(text):
                marker.write_text(text, encoding="utf-8")
                self.assertEqual(self.gate(self.red())[1], "action: repair (attempt 1 of 3)")
                self.assertEqual(json.loads(self.state().read_text())["seen"], [SHA])
                self.assertIn("incomplete", self.stderr)

    def test_the_marker_is_written_whole_or_not_at_all(self):
        real = ci_status.write_gate

        def main_locked(path, state):
            if path.name == ci_status.REPAIRS.name:
                return False
            return real(path, state)
        with mock.patch.object(ci_status, "write_gate", main_locked):
            self.assertEqual(self.gate(self.green())[1], "action: proceed")
        record = json.loads((self.root / ci_status.CLEARED).read_text(encoding="utf-8"))
        self.assertEqual(record["proven"], SHA)
        self.assertEqual([p.name for p in (self.root / "build").iterdir() if p.name.endswith(".tmp")], [])

    def test_a_code_push_after_a_restart_is_not_skipped(self):
        self.gate(self.green(), OLD, FakeGit())
        gh = FakeGh({"plan.yml": [run()]})
        code, out = self.go(["--gate", "--sha", "HEAD", "--json"], gh, FakeGit(["src/a.cpp"]))
        self.assertEqual((code, json.loads(out)["action"]), (ci_status.EXIT_NONE, "wait"))

    def test_waiting_prints_progress_and_keeps_stdout_one_object(self):
        clock = _Clock()
        code, out = self.go(["--gate", "--sha", "HEAD", "--json", "--wait-minutes", "2"], FakeGh(), clock=clock.now, sleep=clock.sleep)
        data = json.loads(out)  # one object, untouched by progress
        self.assertEqual(data["action"], "wait")
        lines = [line for line in self.stderr.splitlines() if line.startswith("waiting:")]
        self.assertEqual(len(lines), 2)
        self.assertIn("build.yml none", lines[0])
        self.assertIn("0s elapsed, 120s left", lines[0])
        self.assertIn("60s elapsed, 60s left", lines[1])

    def test_the_wait_is_a_real_bound(self):
        clock = _Clock()
        self.go(["--gate", "--sha", "HEAD", "--wait-minutes", "0.1"], FakeGh(), clock=clock.now, sleep=clock.sleep)
        self.assertEqual(clock.t, 6)  # six seconds, not the sixty of one poll
        self.assertEqual(clock.sleeps, 1)

    def test_tools_share_the_wait_and_none_starts_after_it(self):
        clock, seen = _Clock(), []

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            seen.append((name, timeout))
            spent = min(2, timeout)  # slow, and stopped at its limit as subprocess would
            clock.t += spent
            if name == "git":
                return 0, SHA + "\n", ""
            return 0, "[]", ""
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
            ci_status.main(["--gate", "--sha", "HEAD", "--wait-minutes", "0.1"], root=self.root, clock=clock.now, sleep=clock.sleep)
        self.assertLessEqual(clock.t, 6)  # the whole call stays inside a six second budget
        self.assertEqual(seen[0], ("git", ci_status.RESOLVE_SECONDS))  # naming the commit has its own short limit
        limits = [timeout for _, timeout in seen]
        self.assertEqual(limits, sorted(limits, reverse=True))  # and each limit shrinks as the budget does
        self.assertTrue(all(timeout > 0 for timeout in limits))
        self.assertIn("action: wait", out.getvalue())

    def nearly_expired(self):
        """A commit whose two hour limit has one second left, and a tool that uses every second it is given."""
        self.waiting(0)
        clock, seen = _Clock(), []

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            seen.append((name, timeout))
            clock.t += timeout
            return 0, (SHA + "\n" if name == "git" else "[]"), ""
        return clock, seen, tool

    def test_a_stalled_git_can_only_cost_its_short_limit(self):
        clock, spent = _Clock(), []

        def stalled(name, args, cwd=ci_status.ROOT, timeout=120):
            spent.append(timeout)
            clock.t += timeout
            raise ci_status.Unavailable(f"{name} could not run: timed out")
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", stalled), contextlib.redirect_stdout(out):
            code = ci_status.main(["--gate", "--sha", "HEAD"], root=self.root, clock=clock.now, sleep=clock.sleep)
        self.assertEqual((code, spent, clock.t), (ci_status.EXIT_UNAVAILABLE, [ci_status.RESOLVE_SECONDS], ci_status.RESOLVE_SECONDS))
        self.assertIn("action: wait", out.getvalue())

    def test_a_full_sha_with_a_recorded_wait_past_its_limit_starts_no_tool(self):
        self.go(["--gate", "--sha", SHA], FakeGh(), wall=lambda: 1_000_000.0)
        calls = []

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            calls.append(name)
            return 0, "[]", ""
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(out):
            ci_status.main(["--gate", "--sha", SHA], root=self.root, wall=lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS)
        self.assertEqual(calls, [])
        self.assertIn("action: unverified", out.getvalue())

    def git_files(self):
        """A real repo with commits, to compare read_head against git itself."""
        def git(*args):
            return subprocess.run(["git", "-C", str(self.root), *args], capture_output=True, text=True, check=True).stdout.strip()
        git("init", "-q")
        git("config", "user.email", "t@example.invalid")
        git("config", "user.name", "t")
        (self.root / "a.txt").write_text("a")
        git("add", "-A")
        git("commit", "-qm", "one")
        return git

    def test_read_head_matches_git_on_a_branch_detached_and_packed(self):
        git = self.git_files()
        self.assertEqual(ci_status.read_head(self.root), git("rev-parse", "HEAD"))
        git("pack-refs", "--all")
        self.assertEqual(ci_status.read_head(self.root), git("rev-parse", "HEAD"))
        git("checkout", "-q", "--detach")
        self.assertEqual(ci_status.read_head(self.root), git("rev-parse", "HEAD"))

    def test_read_head_matches_git_in_a_linked_worktree(self):
        git = self.git_files()
        with tempfile.TemporaryDirectory() as other:
            tree = Path(other) / "tree"
            git("worktree", "add", "-q", "-b", "side", str(tree))
            self.assertEqual(ci_status.read_head(tree), git("rev-parse", "side"))
            git("worktree", "remove", "--force", str(tree))

    def test_read_head_says_nothing_it_cannot_tell(self):
        self.assertIsNone(ci_status.read_head(self.root))  # no repo
        (self.root / ".git").mkdir()
        (self.root / ".git" / "HEAD").write_text("ref: refs/heads/none\n")
        self.assertIsNone(ci_status.read_head(self.root))  # an unborn branch
        (self.root / ".git" / "HEAD").write_text("garbage\n")
        self.assertIsNone(ci_status.read_head(self.root))

    def test_head_past_the_limit_starts_no_tool(self):
        git = self.git_files()
        head = git("rev-parse", "HEAD")
        self.go(["--gate", "--sha", head], FakeGh(), _Named(head), wall=lambda: 1_000_000.0)
        calls = []

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            calls.append(name)
            return 0, "[]", ""
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(out):
            ci_status.main(["--gate", "--sha", "HEAD"], root=self.root, wall=lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS)
        self.assertEqual(calls, [])
        self.assertIn("action: unverified", out.getvalue())

    def test_an_unreadable_head_past_the_limit_is_an_answer_with_no_tool(self):
        self.go(["--gate", "--sha", SHA], FakeGh(), wall=lambda: 1_000_000.0)
        calls = []

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            calls.append(name)
            return 0, "[]", ""
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(out):
            code = ci_status.main(["--gate", "--sha", "HEAD", "--json"], root=self.root, wall=lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS)
        self.assertEqual((code, calls), (ci_status.EXIT_UNAVAILABLE, []))
        data = json.loads(out.getvalue())
        self.assertEqual(data["action"], "unverified")
        self.assertIn("ran out", data["error"])
        self.assertEqual(json.loads(self.state().read_text())["waiting"], {"sha": SHA, "since": 1_000_000.0, "pid": os.getpid()})

    def test_a_new_full_sha_gets_its_own_wait_after_an_older_one_expired(self):
        self.go(["--gate", "--sha", SHA], FakeGh(), wall=lambda: 1_000_000.0)
        other = "c" * 40
        calls = []

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            calls.append(name)
            return 0, json.dumps([run(sha=other)]) if name == "gh" else "", ""
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(out):
            code = ci_status.main(["--gate", "--sha", other, "--json"], root=self.root, wall=lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS)
        self.assertEqual((code, json.loads(out.getvalue())["action"]), (0, "proceed"))
        self.assertIn("gh", calls)
        self.assertNotIn("git", calls)

    # --- a workflow with no run and no proof: the gate starts it for its own commit, and only for that ---

    NO_BUILD = {"plan.yml": [run()]}  # build.yml has no run, and with no record nothing proves a skip

    def started_record(self):
        """What the record holds as started, without the `unanswered` part while that is empty."""
        record = json.loads(self.state().read_text(encoding="utf-8")).get("started")
        if isinstance(record, dict) and not record.get("unanswered"):
            record.pop("unanswered", None)
        return record

    def test_the_gate_starts_a_missing_run_for_its_own_commit(self):
        gh = FakeGh(dict(self.NO_BUILD))
        code, text = self.go(["--gate", "--sha", "HEAD"], gh, FakeGit(["README.md"]))
        lines = text.strip().splitlines()
        self.assertEqual((code, lines[-1]), (ci_status.EXIT_NONE, "action: wait"))
        self.assertEqual(lines[-2], "started for this commit: build.yml")
        self.assertEqual(gh.started, [["build.yml", "--ref", "main"]])
        self.assertIn(f"recovery: started build.yml on main at {SHA}", self.stderr)
        self.assertNotIn("to get a real run", text)  # nobody is asked to run a command
        self.assertEqual(self.started_record(), {"sha": SHA, "workflows": ["build.yml"]})

    def test_the_start_comes_before_the_first_sleep_and_its_run_is_then_read(self):
        clock = _Clock()
        slept_at_start = []

        def dispatched(gh, name):
            slept_at_start.append(clock.sleeps)
            gh.runs[name] = [run("queued", None)]

        gh = FakeGh(dict(self.NO_BUILD), on_dispatch=dispatched)
        real_sleep = clock.sleep

        def sleep(seconds):
            real_sleep(seconds)
            if clock.sleeps == 2:
                gh.runs["build.yml"] = [run()]  # the started run finishes green

        code, out = self.go(["--gate", "--sha", "HEAD", "--json", "--wait-minutes", "30"], gh, FakeGit(["README.md"]),
                            clock=clock.now, sleep=sleep, wall=lambda: 1_000_000.0 + clock.t)
        data = json.loads(out)  # stdout is still one JSON object
        self.assertEqual(slept_at_start, [0])  # started at the first look, with a 30 minute wait ahead
        self.assertEqual((code, data["action"]), (0, "proceed"))
        self.assertEqual(clock.sleeps, 2)  # pending after one sleep, green after two: no half hour wait
        self.assertEqual(len(gh.started), 1)
        self.assertIn("waiting: build.yml pending", self.stderr)

    def test_a_workflow_is_started_once_per_commit(self):
        clock = _Clock()
        gh = FakeGh(dict(self.NO_BUILD))  # the started run never shows up
        code, out = self.go(["--gate", "--sha", "HEAD", "--json", "--wait-minutes", "30"], gh, FakeGit(["README.md"]),
                            clock=clock.now, sleep=clock.sleep, wall=lambda: 1_000_000.0 + clock.t)
        data = json.loads(out)
        self.assertEqual((code, data["action"], data["started"]), (ci_status.EXIT_NONE, "wait", ["build.yml"]))
        self.assertEqual(clock.t, 1800)  # every look of the whole wait
        self.assertEqual(len(gh.started), 1)
        again = FakeGh(dict(self.NO_BUILD))  # a second invocation, as after a restart
        _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], again, FakeGit(["README.md"]), wall=lambda: 1_000_000.0 + clock.t)
        self.assertEqual(again.started, [])
        self.assertEqual(json.loads(raw)["started"], ["build.yml"])

    def test_a_restart_in_a_new_process_starts_nothing_more(self):
        first = self.fresh(["--gate", "--sha", "HEAD"])
        self.assertIn("recovery: started build.yml", first[2])
        second = self.fresh(["--gate", "--sha", "HEAD"])
        self.assertNotIn("recovery: started", second[2])
        self.assertIn("started for this commit: build.yml, plan.yml", second[1])

    def test_a_new_commit_gets_its_own_start(self):
        self.go(["--gate", "--sha", "HEAD"], FakeGh(dict(self.NO_BUILD)), FakeGit(["README.md"]))
        gh = FakeGh({"plan.yml": [run(sha=OLD)]}, head=OLD)
        self.go(["--gate", "--sha", OLD], gh, FakeGit(["README.md"]))
        self.assertEqual(gh.started, [["build.yml", "--ref", "main"]])
        self.assertEqual(self.started_record(), {"sha": OLD, "workflows": ["build.yml"]})

    def test_a_commit_not_yet_pushed_is_told_to_push_not_that_the_branch_moved(self):
        # The remote head is an ancestor of the gated commit: nothing moved, the commit just isn't there yet.
        gh = FakeGh(dict(self.NO_BUILD), head=OLD)
        code, text = self.go(["--gate", "--sha", "HEAD"], gh, FakeGit(["README.md"], ancestor=True))
        lines = text.strip().splitlines()
        self.assertEqual((code, lines[-1]), (ci_status.EXIT_NONE, "action: wait"))
        self.assertEqual(lines[-2], f"this commit is not on the remote yet (main there is at {OLD}): push it, then gate it")
        self.assertEqual(gh.started, [])
        _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], gh, FakeGit(["README.md"], ancestor=True))
        data = json.loads(raw)
        self.assertEqual(data["not_pushed"], OLD)
        self.assertNotIn("moved_to", data)
        self.assertNotIn("recovery", data)

    def test_a_moved_branch_is_given_no_run_and_names_the_head_to_gate(self):
        gh = FakeGh(dict(self.NO_BUILD), head=OLD)
        code, text = self.go(["--gate", "--sha", "HEAD"], gh, FakeGit(["README.md"], ancestor=False))
        lines = text.strip().splitlines()
        self.assertEqual((code, lines[-1]), (ci_status.EXIT_NONE, "action: wait"))
        self.assertEqual(lines[-2], f"main has moved on to {OLD}: gate that commit (--sha {OLD})")
        self.assertEqual(gh.started, [])
        self.assertIsNone(self.started_record())
        _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], gh, FakeGit(["README.md"], ancestor=False))
        data = json.loads(raw)
        self.assertEqual((data["moved_to"], data["action"], data["exit"]), (OLD, "wait", ci_status.EXIT_NONE))
        self.assertNotIn("recovery", data)
        self.assertNotIn("started", data)

    def test_an_unreadable_head_is_given_no_run_and_says_why(self):
        for gh, why in ((FakeGh(dict(self.NO_BUILD), head_fails="HTTP 502: bad gateway"), "gh api exited 1: HTTP 502: bad gateway"),
                        (FakeGh(dict(self.NO_BUILD), head="not a commit"), "gh api did not return a commit for the branch")):
            with self.subTest(why=why):
                code, text = self.go(["--gate", "--sha", "HEAD"], gh, FakeGit(["README.md"]))
                lines = text.strip().splitlines()
                self.assertEqual((code, lines[-1]), (ci_status.EXIT_NONE, "action: wait"))
                self.assertEqual(lines[-2], f"no run was started: the remote head of main couldn't be read, so no run was started: {why}")
                self.assertEqual(gh.started, [])
                _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], gh, FakeGit(["README.md"]))
                data = json.loads(raw)
                self.assertIn(why, data["recovery_error"])
                self.assertEqual((data["action"], data["exit"]), ("wait", ci_status.EXIT_NONE))
                self.assertNotIn("recovery", data)

    def test_a_refused_start_is_reported_and_not_recorded(self):
        gh = FakeGh(dict(self.NO_BUILD), dispatch_fails="HTTP 422: Workflow does not have 'workflow_dispatch' trigger")
        code, raw = self.go(["--gate", "--sha", "HEAD", "--json"], gh, FakeGit(["README.md"]))
        data = json.loads(raw)
        self.assertEqual((code, data["action"]), (ci_status.EXIT_NONE, "wait"))
        self.assertIn("build.yml could not be started: gh workflow run exited 1: HTTP 422", data["recovery_error"])
        self.assertNotIn("started", data)
        self.assertEqual(self.started_record(), {"sha": SHA, "workflows": []})
        ok = FakeGh(dict(self.NO_BUILD))  # so the next look may try again
        self.go(["--gate", "--sha", "HEAD"], ok, FakeGit(["README.md"]))
        self.assertEqual(len(ok.started), 1)

    def test_a_start_with_no_clear_answer_stays_recorded_and_is_not_repeated(self):
        # The connection was lost after the request went out: GitHub may have accepted it.
        for fails in ("error connecting to api.github.com", "HTTP 502: Bad Gateway"):
            with self.subTest(fails=fails):
                self.state().unlink(missing_ok=True)
                gh = FakeGh(dict(self.NO_BUILD), dispatch_fails=fails)
                code, raw = self.go(["--gate", "--sha", "HEAD", "--json"], gh, FakeGit(["README.md"]))
                data = json.loads(raw)
                self.assertEqual((code, data["action"]), (ci_status.EXIT_NONE, "wait"))
                self.assertIn("got no clear answer", data["recovery_error"])
                self.assertIn("gh workflow run build.yml --ref main", data["recovery_error"])
                self.assertEqual(self.started_record(), {"sha": SHA, "workflows": ["build.yml"], "unanswered": {"build.yml": fails}})
                # A restart, then a whole wait of polling: nothing is started again, and each look still says the
                # start was never confirmed and how to make it by hand.
                again = FakeGh(dict(self.NO_BUILD))
                _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], again, FakeGit(["README.md"]))
                self.assertEqual(again.started, [])  # a second start could run it twice
                self.assertIn("got no clear answer", json.loads(raw)["recovery_error"])
                clock = _Clock()
                polled = FakeGh(dict(self.NO_BUILD))
                _, raw = self.go(["--gate", "--sha", "HEAD", "--json", "--wait-minutes", "30"], polled, FakeGit(["README.md"]),
                                 clock=clock.now, sleep=clock.sleep, wall=lambda: self.wall_now() + clock.t)
                data = json.loads(raw)
                self.assertEqual((polled.started, clock.t), ([], 1800))
                self.assertIn(f"got no clear answer ({fails})", data["recovery_error"])
                self.assertIn("gh workflow run build.yml --ref main", data["recovery_error"])
                _, text = self.go(["--gate", "--sha", "HEAD"], FakeGh(dict(self.NO_BUILD)), FakeGit(["README.md"]))
                self.assertIn("no run was started: the start of build.yml got no clear answer", text)
                # Once the run appears there is nothing left to warn about.
                _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], self.green(), FakeGit(["README.md"]))
                self.assertNotIn("recovery_error", json.loads(raw))

    def wall_now(self):
        import time
        return time.time()

    def test_a_record_that_cannot_be_read_holds_starts_back_until_a_green(self):
        # A workflow was started, then the record became unreadable: whether it was started is no longer known.
        self.go(["--gate", "--sha", "HEAD"], FakeGh(dict(self.NO_BUILD)), FakeGit(["README.md"]))
        self.assertEqual(self.started_record()["workflows"], ["build.yml"])
        self.state().write_text("{ not json", encoding="utf-8")
        gh = FakeGh(dict(self.NO_BUILD))
        code, raw = self.go(["--gate", "--sha", "HEAD", "--json"], gh, FakeGit(["README.md"]))
        data = json.loads(raw)
        self.assertEqual(gh.started, [])  # not started a second time
        self.assertEqual((code, data["action"]), (ci_status.EXIT_NONE, "wait"))
        self.assertIn("could not be read since the last green", data["recovery_error"])
        self.assertIn("gh workflow run build.yml --ref main", data["recovery_error"])
        # The record is readable again by now (the gate has saved over it), and the start is still held back.
        self.assertTrue(json.loads(self.state().read_text(encoding="utf-8"))[ci_status.HISTORY_LOST])
        later = FakeGh(dict(self.NO_BUILD))
        _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], later, FakeGit(["README.md"]))
        self.assertEqual(later.started, [])
        self.assertIn("could not be read since the last green", json.loads(raw)["recovery_error"])
        # A green replaces the record, and the next commit with no run is started as usual.
        self.assertEqual(self.gate(self.green())[1], "action: proceed")
        self.assertNotIn(ci_status.HISTORY_LOST, json.loads(self.state().read_text(encoding="utf-8")))
        fresh = FakeGh({"plan.yml": [run(sha=OLD)]}, head=OLD)
        self.go(["--gate", "--sha", OLD], fresh, FakeGit(["src/a.cpp"]))  # a code change, so nothing proves a skip
        self.assertEqual(fresh.started, [["build.yml", "--ref", "main"]])

    def test_a_read_that_fails_once_holds_starts_back_too(self):
        self.go(["--gate", "--sha", "HEAD"], FakeGh(dict(self.NO_BUILD)), FakeGit(["README.md"]))
        real = Path.read_text
        failed = []

        def flaky(path, *args, **kwargs):
            # The second read is the one a change is made from (the first only looks): it fails, and the save that
            # follows it would otherwise put back a record with no start in it.
            if path.name == ci_status.REPAIRS.name:
                failed.append(1)
                if len(failed) == 2:
                    raise PermissionError("the file is in use by another process")
            return real(path, *args, **kwargs)

        gh = FakeGh(dict(self.NO_BUILD))
        with mock.patch.object(Path, "read_text", flaky):
            _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], gh, FakeGit(["README.md"]))
        self.assertGreaterEqual(len(failed), 3)  # it was read again afterwards, and read fine
        self.assertEqual(gh.started, [])
        self.assertIn("could not be read since the last green", json.loads(raw)["recovery_error"])

    def test_a_start_that_cannot_run_at_all_is_treated_the_same_way(self):
        def lost(args):
            if args[:2] == ["workflow", "run"]:
                raise ci_status.Unavailable("gh could not run: timed out")
            return FakeGh(dict(self.NO_BUILD))(args)

        code, raw = self.go(["--gate", "--sha", "HEAD", "--json"], lost, FakeGit(["README.md"]))
        self.assertIn("got no clear answer (gh could not run: timed out)", json.loads(raw)["recovery_error"])
        self.assertEqual(self.started_record()["workflows"], ["build.yml"])

    def test_only_a_refusal_github_answered_counts_as_one(self):
        self.assertTrue(ci_status.confirmed_refusal(1, "", "HTTP 422: Workflow does not have 'workflow_dispatch' trigger"))
        self.assertTrue(ci_status.confirmed_refusal(1, "could not create workflow dispatch event: HTTP 403: forbidden", ""))
        for code, err in ((1, "HTTP 502: Bad Gateway"), (1, "error connecting to api.github.com"), (1, ""), (0, "HTTP 422")):
            self.assertFalse(ci_status.confirmed_refusal(code, "", err), (code, err))

    def test_many_gates_at_once_start_a_workflow_once(self):
        import threading
        gh = FakeGh(dict(self.NO_BUILD))
        ready = threading.Barrier(8)
        results = [{"workflows": [{"workflow": "build.yml", "verdict": "none"}]} for _ in range(8)]

        def look(result):
            ready.wait()
            ci_status.recover(gh, result, "main", SHA, True, self.state())

        threads = [threading.Thread(target=look, args=(result,)) for result in results]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(60)
        self.assertEqual(gh.started, [["build.yml", "--ref", "main"]])
        self.assertEqual(self.started_record(), {"sha": SHA, "workflows": ["build.yml"]})
        self.assertTrue(all(result.get("started") == ["build.yml"] for result in results), results)
        self.assertFalse((self.root / ci_status.LOCK).exists())

    def test_a_gate_that_cannot_get_the_lock_starts_nothing_on_that_look(self):
        lock = self.root / ci_status.LOCK
        lock.parent.mkdir(parents=True, exist_ok=True)
        lock.write_text("", encoding="utf-8")  # another gate is changing the record
        gh = FakeGh(dict(self.NO_BUILD))
        result = {"workflows": [{"workflow": "build.yml", "verdict": "none"}]}
        with mock.patch.object(ci_status, "LOCK_WAIT_SECONDS", 0.2):
            with ci_status.gate_lock(self.state(), 0.2) as held:
                self.assertFalse(held)
            real = ci_status.gate_lock
            with mock.patch.object(ci_status, "gate_lock", lambda path: real(path, 0.2)):
                ci_status.recover(gh, result, "main", SHA, True, self.state())
        self.assertEqual(gh.started, [])
        self.assertIn("another gate holds ci-gate.lock", result["recovery_error"])
        self.assertTrue(lock.exists())  # it was not this gate's to remove
        lock.unlink()
        ci_status.recover(gh, result, "main", SHA, True, self.state())
        self.assertEqual(len(gh.started), 1)

    def held_elsewhere(self):
        """The lock as another live gate holds it, with this gate's wait for it cut short."""
        lock = self.root / ci_status.LOCK
        lock.parent.mkdir(parents=True, exist_ok=True)
        lock.write_text("", encoding="utf-8")
        self.addCleanup(lambda: lock.unlink(missing_ok=True))
        patch = mock.patch.object(ci_status, "LOCK_WAIT_SECONDS", 0.1)
        patch.start()
        self.addCleanup(patch.stop)

    def record(self):
        return self.state().read_text(encoding="utf-8") if self.state().exists() else None

    def test_the_record_is_saved_only_under_its_lock(self):
        self.assertFalse(ci_status.write_gate(self.state(), {"proven": SHA}))
        self.assertIsNone(self.record())
        with ci_status.gate_lock(self.state()) as held:
            self.assertTrue(held and ci_status.lock_held(self.state()))
            with ci_status.gate_lock(self.state()) as again:  # a locked change may call another
                self.assertTrue(again)
            self.assertTrue(ci_status.lock_held(self.state()))  # and the inner one does not release it
            self.assertTrue(ci_status.write_gate(self.state(), {"proven": SHA}))
        self.assertFalse(ci_status.lock_held(self.state()))
        self.assertEqual(json.loads(self.record()), {"proven": SHA})
        marker = self.root / ci_status.CLEARED  # the marker exists for when the record can't be changed
        self.assertTrue(ci_status.write_gate(marker, {"proven": SHA, "at": 1.0}))

    def test_a_gate_without_the_lock_changes_nothing_in_the_record(self):
        with ci_status.gate_lock(self.state()):
            ci_status.write_gate(self.state(), {"started": {"sha": SHA, "workflows": ["build.yml"]}, "first_red": OLD, "seen": [OLD]})
        before = self.record()
        self.held_elsewhere()
        self.assertEqual(ci_status.begin_wait(self.state(), SHA, 50.0), 50.0)  # it still answers, from what it read
        self.assertEqual(self.record(), before)
        action, why = ci_status.gate_action(ci_status.EXIT_PENDING, SHA, self.state(), 60.0)
        self.assertEqual(action, "unverified")
        self.assertIn("couldn't be saved", why)
        action, why = ci_status.gate_action(ci_status.EXIT_RED, SHA, self.state(), 60.0)
        self.assertEqual(action, "unverified")  # a repair that can't be counted is not handed out
        self.assertIn("couldn't be saved", why)
        self.assertEqual(self.record(), before)
        # A green still proceeds, through the marker kept for a record that can't be changed.
        self.assertEqual(ci_status.gate_action(0, SHA, self.state(), 70.0), ("proceed", None))
        self.assertEqual(self.record(), before)
        self.assertEqual(json.loads((self.root / ci_status.CLEARED).read_text(encoding="utf-8"))["proven"], SHA)

    def test_a_refusal_that_cannot_take_the_lock_leaves_the_reservation(self):
        gh = FakeGh(dict(self.NO_BUILD), dispatch_fails="HTTP 422: no workflow_dispatch trigger")
        real = ci_status.gate_lock
        taken = []

        def once(path, wait=None):
            taken.append(1)
            if len(taken) == 1:
                return real(path, wait)  # the reservation gets the lock
            (self.root / ci_status.LOCK).write_text("", encoding="utf-8")  # another gate has it for the cleanup
            return real(path, 0.1)

        result = {"workflows": [{"workflow": "build.yml", "verdict": "none"}]}
        with mock.patch.object(ci_status, "gate_lock", once):
            ci_status.recover(gh, result, "main", SHA, True, self.state())
        (self.root / ci_status.LOCK).unlink()
        self.assertIn("build.yml could not be started", result["recovery_error"])
        self.assertEqual(self.started_record(), {"sha": SHA, "workflows": ["build.yml"]})  # kept, never rewritten unlocked

    def test_a_lock_left_by_a_dead_gate_is_taken_over(self):
        lock = self.root / ci_status.LOCK
        lock.parent.mkdir(parents=True, exist_ok=True)
        lock.write_text("", encoding="utf-8")
        old = lock.stat().st_mtime - ci_status.LOCK_STALE_SECONDS - 5
        os.utime(lock, (old, old))
        with ci_status.gate_lock(self.state(), 0.2) as held:
            self.assertTrue(held)
        self.assertFalse(lock.exists())

    def test_nothing_is_started_when_the_start_cannot_be_recorded(self):
        gh = FakeGh(dict(self.NO_BUILD))
        real = ci_status.write_gate

        def refuse_started(path, state):
            return False if state.get("started", {}).get("workflows") else real(path, state)

        with mock.patch.object(ci_status, "write_gate", refuse_started):
            code, raw = self.go(["--gate", "--sha", "HEAD", "--json"], gh, FakeGit(["README.md"]))
        data = json.loads(raw)
        self.assertEqual(gh.started, [])
        self.assertIn("couldn't be saved, so build.yml was not started", data["recovery_error"])
        self.assertEqual(data["action"], "wait")

    def test_a_run_on_a_newer_commit_never_answers_for_the_gated_one(self):
        # The branch advances between the head check and the start: the started run carries the newer commit.
        clock = _Clock()

        def advanced(gh, name):
            # The branch points at the newer commit from now on. The run GitHub starts is of that commit, so it is
            # never in the list `gh run list --commit <the gated commit>` returns, which is all the gate reads.
            gh.head = OLD

        gh = FakeGh(dict(self.NO_BUILD), on_dispatch=advanced)
        code, out = self.go(["--gate", "--sha", "HEAD", "--json", "--wait-minutes", "30"], gh, FakeGit(["README.md"], ancestor=False),
                            clock=clock.now, sleep=clock.sleep, wall=lambda: 1_000_000.0 + clock.t)
        data = json.loads(out)
        self.assertEqual((code, data["action"]), (ci_status.EXIT_NONE, "wait"))  # never proceed
        self.assertEqual({e["workflow"]: e["verdict"] for e in data["workflows"]}["build.yml"], "none")
        self.assertEqual(data["moved_to"], OLD)  # the later looks say where the branch went
        self.assertEqual(len(gh.started), 1)
        commits = {args[args.index("--commit") + 1] for args in gh.calls if "--commit" in args}
        self.assertEqual(commits, {SHA})  # every run read was asked for by this commit

    def test_no_head_lookup_or_start_once_the_wait_has_passed(self):
        result = {"workflows": [{"workflow": "build.yml", "verdict": "none"}]}
        gh = FakeGh()
        ci_status.recover(gh, result, "main", SHA, True, self.state(), expired=lambda: True)
        self.assertEqual(gh.calls, [])
        self.assertIn("the wait ran out", result["recovery_error"])
        left = iter([False, True])  # the head is read in time, the start is not
        ci_status.recover(gh, result, "main", SHA, True, self.state(), expired=lambda: next(left))
        self.assertEqual([c[0] for c in gh.calls], ["api"])
        self.assertEqual(gh.started, [])
        self.assertIn("the wait ran out before build.yml could be started", result["recovery_error"])

    def test_the_head_lookup_goes_through_the_bounded_caller(self):
        self.assertEqual(ci_status.remote_head(FakeGh(head=OLD), "main"), OLD)
        asked = []
        ci_status.remote_head(lambda args: (asked.append(args), (0, SHA + "\n", ""))[1], "release/1")
        self.assertEqual(asked, [["api", "repos/{owner}/{repo}/branches/release/1", "--jq", ".commit.sha"]])

        def gone(args):
            raise ci_status.Unavailable("the wait ran out")

        with self.assertRaises(ci_status.Unavailable):
            ci_status.remote_head(gone, "main")

    def test_a_read_only_check_names_the_recovery_and_starts_nothing(self):
        # The preflight reads `--use-proven` and passes this line on as its fix, so a pushed commit with no run is never
        # told to push again.
        gh = FakeGh(dict(self.NO_BUILD))
        code, text = self.go(["--sha", "HEAD", "--use-proven"], gh, FakeGit(["README.md"]))
        self.assertEqual(code, ci_status.EXIT_NONE)
        self.assertEqual(text.strip().splitlines()[-1], "to get a real run: gh workflow run build.yml --ref main")
        _, raw = self.go(["--sha", "HEAD", "--use-proven", "--json"], gh, FakeGit(["README.md"]))
        self.assertEqual(json.loads(raw)["recovery"], "gh workflow run build.yml --ref main")
        self.assertEqual(gh.started, [])
        self.assertFalse(self.state().exists())
        _, raw = self.go(["--sha", "HEAD", "--use-proven", "--json"], self.green())
        self.assertNotIn("recovery", json.loads(raw))

    def test_a_read_only_check_offers_no_command_for_a_moved_or_unreadable_branch(self):
        for gh, key in ((FakeGh(dict(self.NO_BUILD), head=OLD), "moved_to"),
                        (FakeGh(dict(self.NO_BUILD), head_fails="HTTP 502"), "recovery_error")):
            with self.subTest(key=key):
                code, text = self.go(["--sha", "HEAD", "--use-proven"], gh, FakeGit(["README.md"], ancestor=False))
                self.assertEqual(code, ci_status.EXIT_NONE)  # the same exit as when the head matches
                self.assertNotIn("to get a real run", text)
                self.assertNotIn("gh workflow run", text)
                _, raw = self.go(["--sha", "HEAD", "--use-proven", "--json"], gh, FakeGit(["README.md"], ancestor=False))
                data = json.loads(raw)
                self.assertIn(key, data)
                self.assertNotIn("recovery", data)
                self.assertEqual({e["workflow"]: e["verdict"] for e in data["workflows"]}, {"build.yml": "none", "plan.yml": "green"})

    def test_a_look_at_the_branch_itself_still_names_the_command(self):
        code, raw = self.go(["--json"], FakeGh({"plan.yml": [run()]}))
        self.assertEqual(json.loads(raw)["recovery"], "gh workflow run build.yml --ref main")

    def test_a_proven_skip_or_a_run_needs_no_recovery(self):
        _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], self.green())
        self.assertNotIn("recovery", json.loads(raw))
        pending = FakeGh({"build.yml": [run("queued", None)], "plan.yml": [run()]})
        _, raw = self.go(["--gate", "--sha", "HEAD", "--json"], pending)
        self.assertNotIn("recovery", json.loads(raw))

    def test_after_a_fresh_clone_a_real_run_decides(self):
        first = self.fresh(["--gate", "--sha", "HEAD"])
        self.assertIn("started for this commit: build.yml, plan.yml", first[1])
        code, out, _ = self.fresh(["--gate", "--sha", "HEAD"], {"build.yml": [run()], "plan.yml": [run()]})
        self.assertEqual((code, out.strip().splitlines()[-1]), (0, "action: proceed"))

    def test_after_a_fresh_clone_a_real_failure_stays_red(self):
        code, out, _ = self.fresh(["--gate", "--sha", "HEAD"], {"build.yml": [run(conclusion="failure")], "plan.yml": [run()]})
        self.assertEqual((code, out.strip().splitlines()[-1]), (1, "action: repair (attempt 1 of 3)"))

    def test_the_gate_takes_only_head_or_a_full_sha(self):
        self.assertIn("HEAD or a full SHA", self.err_of(["--gate", "--sha", "main"]))
        self.assertIn("HEAD or a full SHA", self.err_of(["--gate", "--sha", "abc123"]))
        self.assertEqual(self.go(["--sha", "HEAD"], self.green())[0], 0)  # without a gate, names still work
        self.assertEqual(self.go(["--sha", OLD], FakeGh({"build.yml": [run(sha=OLD)], "plan.yml": [run(sha=OLD)]}))[0], 0)

    def test_head_moving_on_gets_its_own_wait_with_no_state_to_delete(self):
        git = self.git_files()
        old = git("rev-parse", "HEAD")
        self.go(["--gate", "--sha", old], FakeGh(), _Named(old), wall=lambda: 1_000_000.0)
        expired = lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            return 0, "[]", ""
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(out):
            ci_status.main(["--gate", "--sha", "HEAD"], root=self.root, wall=expired)
        self.assertIn("action: unverified", out.getvalue())
        (self.root / "a.txt").write_text("b")
        git("add", "-A")
        git("commit", "-qm", "two")
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(out):
            ci_status.main(["--gate", "--sha", "HEAD"], root=self.root, wall=expired)
        self.assertIn("action: wait", out.getvalue())
        self.assertEqual(json.loads(self.state().read_text())["waiting"], {"sha": git("rev-parse", "HEAD"), "since": expired(), "pid": os.getpid()})

    def test_a_slow_git_with_one_second_left_costs_only_that_second(self):
        self.go(["--gate", "--sha", SHA], FakeGh(), wall=lambda: 1_000_000.0)
        clock, spent = _Clock(), []

        def slow(name, args, cwd=ci_status.ROOT, timeout=120):
            spent.append((name, timeout))
            clock.t += timeout
            return 0, SHA + "\n", ""
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", slow), mock.patch.object(ci_status, "read_head", return_value=None), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
            ci_status.main(["--gate", "--sha", "HEAD"], root=self.root, clock=clock.now, sleep=clock.sleep,
                           wall=lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS - 1 + clock.t)
        self.assertEqual(spent[0], ("git", 1))
        self.assertEqual(clock.t, 1)

    def test_a_new_commit_is_not_held_to_an_older_commits_limit(self):
        git = self.git_files()
        old = git("rev-parse", "HEAD")
        self.go(["--gate", "--sha", old], FakeGh(), _Named(old), wall=lambda: 1_000_000.0)
        (self.root / "a.txt").write_text("b")
        git("add", "-A")
        git("commit", "-qm", "two")
        calls = []

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            calls.append(name)
            return 0, "[]", ""
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(out):
            ci_status.main(["--gate", "--sha", "HEAD"], root=self.root, wall=lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS)
        self.assertIn("gh", calls)
        self.assertIn("action: wait", out.getvalue())

    def test_the_limit_bounds_a_call_with_no_wait(self):
        clock, seen, tool = self.nearly_expired()
        wall = lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS - 1
        real = ci_status.run_tool
        calls = []

        def counted(name, args, cwd=ci_status.ROOT, timeout=120):
            if name == "git":  # naming the commit comes before the limit can be looked up, so it has its own short one
                self.assertEqual(timeout, 1)  # the recorded wait bounds this too
                return 0, SHA + "\n", ""
            calls.append(timeout)
            return tool(name, args, cwd, timeout)
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", counted), mock.patch.object(ci_status, "read_head", return_value=None), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
            ci_status.main(["--gate", "--sha", "HEAD"], root=self.root, clock=clock.now, sleep=clock.sleep, wall=wall)
        self.assertEqual(calls, [1])  # one call stops at the limit, and none starts after it
        self.assertEqual(clock.t, 1)
        self.assertEqual(clock.sleeps, 0)  # with no wait it looks once and doesn't poll

    def test_a_call_with_time_to_spare_keeps_each_tools_limit(self):
        seen = []

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            seen.append(timeout)
            return 0, (SHA + "\n" if name == "git" else "[]"), ""
        clock = _Clock()
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(io.StringIO()):
            ci_status.main(["--gate", "--sha", "HEAD"], root=self.root, clock=clock.now, sleep=clock.sleep, wall=lambda: 1_000_000.0)
        self.assertEqual(seen[0], ci_status.RESOLVE_SECONDS)
        self.assertEqual(set(seen[1:]), {120})
        self.assertEqual(clock.sleeps, 0)

    def test_without_a_wait_a_tool_keeps_its_own_limit(self):
        seen = []

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            seen.append(timeout)
            return 0, (SHA + "\n" if name == "git" else "[]"), ""
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(io.StringIO()):
            ci_status.main(["--sha", "HEAD"], root=self.root)
        self.assertEqual(seen[0], ci_status.RESOLVE_SECONDS)
        self.assertEqual(set(seen[1:]), {120})

    def test_write_leaves_no_temporary_behind(self):
        self.gate(self.red())
        self.assertEqual([p.name for p in self.state().parent.iterdir()], [self.state().name])

    def test_json_carries_the_action(self):
        code, out = self.go(["--gate", "--sha", "HEAD", "--json"], self.red())
        data = json.loads(out)
        self.assertEqual((code, data["action"], data["repair_attempt"]), (1, "repair", 1))

    def test_wait_minutes_needs_gate(self):
        with self.assertRaises(SystemExit):
            self.go(["--wait-minutes", "5"], self.green())

    def test_pending_for_the_whole_wait_ends_in_wait(self):
        clock = _Clock()
        pending = FakeGh({"build.yml": [run("queued", None)], "plan.yml": [run()]})
        code, out = self.go(["--gate", "--sha", "HEAD", "--wait-minutes", "3"], pending, clock=clock.now, sleep=clock.sleep)
        self.assertEqual((code, out.strip().splitlines()[-1]), (4, "action: wait"))
        self.assertEqual(clock.sleeps, 3)

    def test_pending_turning_green_proceeds(self):
        clock = _Clock()
        flip = _Flip(FakeGh({"build.yml": [run("queued", None)], "plan.yml": [run()]}), self.green(), after=2)
        code, out = self.go(["--gate", "--sha", "HEAD", "--wait-minutes", "30"], flip, clock=clock.now, sleep=clock.sleep)
        self.assertEqual((code, out.strip().splitlines()[-1]), (0, "action: proceed"))
        self.assertLess(clock.sleeps, 30)

    def test_red_is_not_waited_on(self):
        clock = _Clock()
        code, _ = self.go(["--gate", "--sha", "HEAD", "--wait-minutes", "30"], self.red(), clock=clock.now, sleep=clock.sleep)
        self.assertEqual((code, clock.sleeps), (1, 0))


class _Clock:
    def __init__(self):
        self.t, self.sleeps = 0.0, 0

    def now(self):
        return self.t

    def sleep(self, seconds):
        self.t += seconds
        self.sleeps += 1


class _Flip:
    """Answers as one fake until it has been polled a set number of times, then as another."""

    def __init__(self, first, second, after):
        self.first, self.second, self.after, self.lists = first, second, after, 0

    def __call__(self, args):
        if args[:2] == ["run", "list"]:
            self.lists += 1
        rounds = (self.lists - 1) // 2  # two workflows per poll
        return (self.first if rounds < self.after else self.second)(args)


class _Named(FakeGit):
    """A FakeGit that also knows one more commit."""

    def __init__(self, sha):
        super().__init__()
        self.sha = sha

    def __call__(self, args):
        if args[0] == "rev-parse" and args[-1].split("^")[0] == self.sha:
            return 0, self.sha + "\n", ""
        return super().__call__(args)


class Policy(unittest.TestCase):
    TEXT = "### Red-first CI"

    def section(self, folder):
        text = (ROOT / folder / "skills" / "process-phase" / "SKILL.md").read_text(encoding="utf-8")
        self.assertIn(self.TEXT, text, folder)
        return text[text.index(self.TEXT):].split("\n## ")[0].strip()

    def test_three_mirrors_hold_the_same_policy_naming_every_action(self):
        sections = {self.section(folder) for folder in (".claude", ".codex", ".agents")}
        self.assertEqual(len(sections), 1)
        text = sections.pop()
        # The gate starts a missing run itself, so the policy no longer hands the run a command: it says that, and what
        # to do when the gate reports the branch moved or the start failed.
        for word in ("--gate", "proceed", "wait", "repair", "exhausted", "unverified", "add-todo",
                     "starts that workflow itself", "has moved on to", "--sha <that commit>", "no run was started"):
            self.assertIn(word, text)
        self.assertNotIn("gh workflow run", text)


if __name__ == "__main__":
    unittest.main()
