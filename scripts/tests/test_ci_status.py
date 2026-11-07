"""The CI gate: a commit passes only on real evidence, and a run always gets one clear action."""
import contextlib
import io
import json
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

    def __init__(self, runs=None, fail=None):
        self.runs, self.fail, self.calls = runs or {}, fail or {}, []

    def __call__(self, args):
        self.calls.append(args)
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
        self.assertEqual(self.go(["--gate", "--sha", "BASE"], FakeGh({"build.yml": [run(sha=a)], "plan.yml": [run(sha=a)]}), FakeGit())[0], 0)
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
        self.gate(self.green(), "BASE")
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
        self.gate(self.green(), "BASE")
        gh = FakeGh({"plan.yml": [run()]})
        self.assertEqual(self.gate(gh, git=FakeGit(["README.md"]))[1], "action: proceed")
        before = self.state().read_bytes()
        self.assertEqual(self.go(["--sha", "HEAD", "--use-proven"], gh, FakeGit(["README.md"]))[0], 0)  # the stamp itself
        self.assertEqual(self.state().read_bytes(), before)
        self.assertEqual(self.go(["--gate", "--sha", "HEAD"], gh, FakeGit(["README.md"]))[0], 0)  # and a repeat gate agrees

    def test_use_proven_refuses_code_after_the_proven_commit(self):
        self.gate(self.green(), "BASE")
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
        self.assertEqual(json.loads(self.state().read_text())["waiting"], {"sha": "c" * 40, "since": wall()})
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
        self.assertEqual(self.fresh(["--gate", "--sha", "BASE"], green)[0], 0)
        record = json.loads(self.state().read_text())
        self.assertEqual(record["proven"], OLD)
        code, out, err = self.fresh(["--gate", "--sha", "HEAD", "--json"], {"plan.yml": [run()]})
        data = json.loads(out)
        self.assertEqual((code, data["action"], data["base"]), (0, "proceed", OLD), err)
        self.assertEqual(data["workflows"][0]["verdict"], "skipped")

    def test_a_restart_while_waiting_keeps_the_proven_base(self):
        green = {"build.yml": [run(sha=OLD)], "plan.yml": [run(sha=OLD)]}
        self.fresh(["--gate", "--sha", "BASE"], green)
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
        self.gate(self.green(), "BASE", FakeGit())
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
        self.assertIn(data["action"], ("wait", "unverified"))
        self.assertIn("ran out", data["error"])

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

    def test_a_branch_name_past_the_limit_does_not_restart_the_wait(self):
        self.go(["--gate", "--sha", SHA], FakeGh(), wall=lambda: 1_000_000.0)
        out = io.StringIO()

        def tool(name, args, cwd=ci_status.ROOT, timeout=120):
            raise AssertionError("no tool starts past the limit")
        with mock.patch.object(ci_status, "run_tool", tool), contextlib.redirect_stdout(out):
            code = ci_status.main(["--gate", "--sha", "main", "--json"], root=self.root, wall=lambda: 1_000_000.0 + ci_status.WAIT_LIMIT_SECONDS)
        self.assertEqual((code, json.loads(out.getvalue())["action"]), (ci_status.EXIT_UNAVAILABLE, "unverified"))
        self.assertEqual(json.loads(self.state().read_text())["waiting"], {"sha": SHA, "since": 1_000_000.0})

    def test_a_slow_git_with_one_second_left_costs_only_that_second(self):
        self.go(["--gate", "--sha", SHA], FakeGh(), wall=lambda: 1_000_000.0)
        clock, spent = _Clock(), []

        def slow(name, args, cwd=ci_status.ROOT, timeout=120):
            spent.append((name, timeout))
            clock.t += timeout
            return 0, SHA + "\n", ""
        out = io.StringIO()
        with mock.patch.object(ci_status, "run_tool", slow), contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
            ci_status.main(["--gate", "--sha", "main"], root=self.root, clock=clock.now, sleep=clock.sleep,
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
        for word in ("--gate", "proceed", "wait", "repair", "exhausted", "unverified", "add-todo"):
            self.assertIn(word, text)


if __name__ == "__main__":
    unittest.main()
