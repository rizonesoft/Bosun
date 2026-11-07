"""The CI gate: a commit passes only on real evidence, and a run always gets one clear action."""
import contextlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

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
        # With --commit, gh returns no runs for this commit even though main has an older green.
        code, verdicts, _ = self.verdicts(["--sha", "HEAD"], FakeGh())
        self.assertEqual(code, ci_status.EXIT_NONE)
        self.assertEqual(verdicts["build.yml"], "none")

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
        code, out = self.go(["--gate", "--sha", sha, "--base", "BASE", *extra], gh, git or FakeGit())
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
        gh = FakeGh({"plan.yml": [run()]})
        self.assertEqual(self.gate(gh, git=FakeGit(["a.md"])), (0, "action: proceed"))

    def test_wait_on_pending_none_and_unavailable(self):
        self.assertEqual(self.gate(FakeGh({"build.yml": [run("queued", None)], "plan.yml": [run()]})), (4, "action: wait"))
        self.assertEqual(self.gate(FakeGh()), (5, "action: wait"))
        self.assertEqual(self.gate(FakeGh(fail={"build.yml": "x", "plan.yml": "x"})), (3, "action: wait"))
        self.assertFalse(self.state().exists())

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
        self.assertEqual(self.gate(self.red(), "c" * 40, _Named("c" * 40))[1], "action: repair (attempt 2 of 3)")
        self.assertEqual(self.gate(self.green())[1], "action: proceed")
        self.assertFalse(self.state().exists())
        self.assertEqual(self.gate(self.red())[1], "action: repair (attempt 1 of 3)")

    def test_a_repair_that_goes_green_proceeds(self):
        self.assertIn("repair", self.gate(self.red())[1])
        self.assertEqual(self.gate(self.green()), (0, "action: proceed"))

    def test_corrupt_record_reads_as_none_used_with_one_warning(self):
        self.state().parent.mkdir(parents=True)
        self.state().write_text("{not json")
        self.assertEqual(self.gate(self.red())[1], "action: repair (attempt 1 of 3)")
        self.assertEqual(self.stderr.count("warning:"), 1)

    def test_a_count_that_cannot_be_saved_waits_rather_than_crashes(self):
        real = ci_status.write_repairs

        def full(path, data):
            raise OSError("no space left on device")
        ci_status.write_repairs = full
        self.addCleanup(setattr, ci_status, "write_repairs", real)
        self.assertEqual(self.gate(self.red()), (1, "action: wait"))
        self.assertIn("could not record", self.stderr)
        code, out = self.go(["--gate", "--sha", "HEAD", "--json"], self.red())
        self.assertEqual((code, json.loads(out)["action"]), (1, "wait"))

    def test_a_record_that_cannot_be_cleared_still_proceeds(self):
        self.state().mkdir(parents=True)  # a folder at the path makes unlink fail
        self.assertEqual(self.gate(self.green()), (0, "action: proceed"))
        self.assertIn("could not clear", self.stderr)

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
        for word in ("--gate", "proceed", "wait", "repair", "exhausted", "add-todo"):
            self.assertIn(word, text)


if __name__ == "__main__":
    unittest.main()
