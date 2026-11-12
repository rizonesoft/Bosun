"""The weakening scan: every hunk that can weaken what the tests prove is named, answered one by one, and shown.

Each case builds a small git repository, commits a change, and runs the scan over it, so what is tested is the scan
reading real diffs through git."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
SCRATCH = os.environ.get("BOSUN_SCRATCH") if os.path.isdir(os.environ.get("BOSUN_SCRATCH") or "") else None
spec = importlib.util.spec_from_file_location("weakening_scan", ROOT / "scripts" / "weakening-scan.py")
assert spec is not None and spec.loader is not None
scan = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scan)

PY = "scripts/tests/test_a.py"
PY_TEXT = """import unittest


class One(unittest.TestCase):
    def test_x(self):
        self.assertEqual(1, 1)

    def test_y(self):
        self.assertTrue(True)
        self.assertEqual(2, 2)


class Two(unittest.TestCase):
    def test_x(self):
        self.assertEqual(3, 3)
"""
PS = "tests/a_test.ps1"
PS_TEXT = """$failed = 0
Check 'the first thing holds' ($a -eq 1)
Check 'the second thing holds' ($b -eq 2)
Check ($c -eq 3) 'an older style check'
"""
CPP = "tests/test_core.cpp"
CPP_TEXT = """static void slots() {
    CHECK(admits(0));
    CHECK(!admits(1));
}
static void budget() {
    CHECK(over(1));
}
static const Group groups[] = {
    {"slots", slots, nullptr},
    {"budget", budget, nullptr},
    {"later", nullptr, "D01 T01"},
};
"""
WORKFLOW = ".github/workflows/build.yml"
WORKFLOW_TEXT = """jobs:
  windows:
    steps:
      - name: Build
        run: pwsh scripts/build.ps1
      - name: Queue tests
        run: pwsh tests/queue_test.ps1
      - name: Core tests
        run: build/Release/test_core.exe
"""
CMAKE = "CMakeLists.txt"
CMAKE_TEXT = "add_compile_options(/W4 /permissive- /utf-8)\nset(CMAKE_COMPILE_WARNING_AS_ERROR ON)\n"
SOURCE = "scripts/tool.py"
SOURCE_TEXT = "def run():\n    return 1\n"


class Repo(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir=SCRATCH)
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.git("init", "-q", "-b", "main")
        for path, text in ((PY, PY_TEXT), (PS, PS_TEXT), (CPP, CPP_TEXT), (WORKFLOW, WORKFLOW_TEXT), (CMAKE, CMAKE_TEXT),
                           (SOURCE, SOURCE_TEXT)):
            self.write(path, text)
        self.base = self.commit("start")

    def git(self, *args, check=True):
        return subprocess.run(["git", "-C", str(self.root), "-c", "user.name=t", "-c", "user.email=t@example.com",
                               "-c", "core.autocrlf=false", *args], capture_output=True, text=True, encoding="utf-8", check=check)

    def write(self, path, text):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8", newline="\n")

    def edit(self, path, old, new):
        text = (self.root / path).read_text(encoding="utf-8")
        self.assertIn(old, text)
        self.write(path, text.replace(old, new))

    def commit(self, subject, body=""):
        self.git("add", "-A")
        self.git("commit", "-q", "-m", subject, *(["-m", body] if body else []))
        return self.git("rev-parse", "HEAD").stdout.strip()

    def run_scan(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = scan.main(list(argv), root=self.root)
        return code, out.getvalue()

    def signals(self, head=None):
        """The signals of the range from the first commit to `head` (default HEAD), as printed, without their state."""
        code, text = self.run_scan("--base", self.base, "--head", head or "HEAD")
        return code, [line.strip().split(" ", 1)[1].split(" -- ")[0] for line in text.splitlines()
                      if line.startswith("  answered") or line.startswith("  unanswered")]


class Kinds(Repo):
    def test_a_plain_feature_with_its_tests_is_clean(self):
        self.edit(SOURCE, "return 1", "return 2")
        self.write("scripts/tests/test_new.py", "import unittest\n\n\nclass New(unittest.TestCase):\n    def test_it(self):\n        self.assertEqual(1, 1)\n")
        self.edit(PY, "    def test_y(self):", "    def test_z(self):\n        self.assertTrue(1)\n\n    def test_y(self):")
        self.edit(PS, "Check ($c -eq 3)", "Check 'a new thing holds' ($d -eq 4)\nCheck ($c -eq 3)")
        self.commit("feature")
        self.assertEqual(self.signals(), (0, []))

    def test_a_deleted_test_file(self):
        (self.root / PY).unlink()
        self.commit("drop")
        self.assertEqual(self.signals(), (1, [f"test-file-deleted {PY}:file"]))

    def test_a_removed_python_case_is_named_with_its_class_and_counted_once(self):
        self.edit(PY, "    def test_y(self):\n        self.assertTrue(True)\n        self.assertEqual(2, 2)\n", "")
        self.commit("drop a case")
        self.assertEqual(self.signals(), (1, [f"test-removed {PY}:One.test_y"]))  # its assertions went with it

    def test_one_of_two_methods_of_the_same_name_removed_is_seen(self):
        self.edit(PY, "class Two(unittest.TestCase):\n    def test_x(self):\n        self.assertEqual(3, 3)\n", "class Two(unittest.TestCase):\n    pass\n")
        self.commit("empty a class")
        self.assertEqual(self.signals(), (1, [f"test-removed {PY}:Two.test_x"]))

    def test_a_removed_assertion_inside_a_kept_case(self):
        self.edit(PY, "        self.assertTrue(True)\n", "")
        self.commit("thin a case")
        self.assertEqual(self.signals(), (1, [f"assertion-removed {PY}:L9"]))

    def test_a_removed_powershell_check_in_either_style(self):
        self.edit(PS, "Check 'the second thing holds' ($b -eq 2)\n", "")
        self.edit(PS, "Check ($c -eq 3) 'an older style check'\n", "")
        self.commit("drop checks")
        self.assertEqual(self.signals(), (1, [f"test-removed {PS}:the second thing holds", f"test-removed {PS}:an older style check"]))

    def test_a_powershell_check_kept_by_name_with_less_in_its_condition(self):
        self.write(PS, "Check 'the run is clean' ($r.Exit -eq 0 -and $child -gt 0 -and (Wait-Gone $child))\n"
                       "Check 'a long one holds' ($a -eq 1 -and\n    $b -eq 2 -and $c -eq 3)\n"
                       "Check 'this one stays' ($d -eq 4)\n")
        before = self.commit("checks with conditions")
        self.edit(PS, " -and (Wait-Gone $child)", "")
        self.edit(PS, "    $b -eq 2 -and $c -eq 3)", "    $b -eq 2)")
        self.commit("thin two conditions")
        code, text = self.run_scan("--base", before, "--head", "HEAD")
        self.assertEqual(code, 1)
        self.assertIn(f"assertion-removed {PS}:the run is clean", text)
        self.assertIn(f"assertion-removed {PS}:a long one holds", text)
        self.assertNotIn("this one stays", text)

    def test_a_powershell_condition_made_always_true(self):
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)", "Check 'the first thing holds' ($true)")
        self.commit("always true")
        self.assertEqual(self.signals(), (1, [f"assertion-removed {PS}:the first thing holds"]))

    def test_a_powershell_condition_reworded_or_strengthened_is_clean(self):
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)", "Check 'the first thing holds' ($a -eq 1 -and $z -eq 9)")
        self.edit(PS, "Check 'the second thing holds' ($b -eq 2)", "Check 'the second thing holds' ($b -ge 2)")
        self.commit("stronger")
        self.assertEqual(self.signals(), (0, []))

    def test_a_removed_powershell_check_with_a_condition_is_one_signal_not_two(self):
        self.write(PS, "Check 'one' ($a -eq 1 -and $b -eq 2)\nCheck 'two' ($c -eq 3)\n")
        before = self.commit("checks")
        self.edit(PS, "Check 'one' ($a -eq 1 -and $b -eq 2)\n", "")
        self.commit("drop one")
        code, text = self.run_scan("--base", before, "--head", "HEAD")
        self.assertEqual(code, 1)
        self.assertIn(f"test-removed {PS}:one", text)
        self.assertIn("1 signal(s), 1 unanswered", text)

    def test_a_removed_and_a_disabled_group_and_a_removed_check_in_cpp(self):
        self.edit(CPP, '    {"slots", slots, nullptr},\n', "")
        self.edit(CPP, '{"budget", budget, nullptr}', '{"budget", nullptr, "later"}')
        self.edit(CPP, "    CHECK(!admits(1));\n", "")
        self.commit("thin the core tests")
        code, found = self.signals()
        self.assertEqual(code, 1)
        self.assertEqual(sorted(found), sorted([f"test-removed {CPP}:slots", f"test-disabled {CPP}:budget", f"assertion-removed {CPP}:L3"]))

    def test_skips_added(self):
        self.edit(PY, "        self.assertEqual(1, 1)", '        self.skipTest("later")\n        self.assertEqual(1, 1)')
        self.edit(WORKFLOW, "        run: pwsh tests/queue_test.ps1", "        run: pwsh tests/queue_test.ps1 -SkipWsl\n        continue-on-error: true")
        self.commit("skip things")
        self.assertEqual(self.signals(), (1, [f"skip-added {WORKFLOW}:L7", f"skip-added {WORKFLOW}:L8", f"ci-step-removed {WORKFLOW}:L7",
                                              f"skip-added {PY}:L6"]))

    def test_a_workflow_step_removed(self):
        self.edit(WORKFLOW, "      - name: Core tests\n        run: build/Release/test_core.exe\n", "")
        self.commit("drop a step")
        self.assertEqual(self.signals(), (1, [f"ci-step-removed {WORKFLOW}:L9"]))

    def test_warnings_relaxed(self):
        self.edit(CMAKE, "add_compile_options(/W4 /permissive- /utf-8)", "add_compile_options(/W3 /permissive- /utf-8)")
        self.edit(CMAKE, "set(CMAKE_COMPILE_WARNING_AS_ERROR ON)\n", "")
        self.edit(SOURCE, "    return 1", "    return undefined  # noqa: F821")
        self.commit("relax")
        self.assertEqual(self.signals(), (1, [f"warning-relaxed {CMAKE}:L1", f"warning-relaxed {CMAKE}:L2", f"warning-relaxed {SOURCE}:L2"]))

    def test_errors_swallowed(self):
        self.edit(SOURCE, "    return 1", "    try:\n        return 1\n    except OSError:\n        pass\n    try:\n        return 2\n    except ValueError: pass")
        self.write("scripts/tool.ps1", "try { Get-Item x } catch { }\ntry { Get-Item y } catch { 'said' }\n")
        self.commit("swallow")
        self.assertEqual(self.signals(), (1, ["error-swallowed scripts/tool.ps1:L1", f"error-swallowed {SOURCE}:L4", f"error-swallowed {SOURCE}:L8"]))

    def test_an_existing_handler_emptied_is_seen_though_its_except_line_did_not_change(self):
        self.edit(SOURCE, "    return 1", "    try:\n        return 1\n    except OSError:\n        return 0")
        self.write("scripts/tool.ps1", "try { Get-Item x } catch {\n  'said'\n}\n")
        before = self.commit("handle")
        self.edit(SOURCE, "    except OSError:\n        return 0", "    except OSError:\n        pass")
        self.edit("scripts/tool.ps1", "catch {\n  'said'\n}", "catch {\n\n}")
        self.commit("empty the handlers")
        code, text = self.run_scan("--base", before, "--head", "HEAD")
        self.assertEqual(code, 1)
        self.assertIn(f"error-swallowed {SOURCE}:L4", text)
        self.assertIn("error-swallowed scripts/tool.ps1:L1", text)

    def test_a_swallowing_handler_the_change_did_not_touch_is_not_reported(self):
        self.edit(SOURCE, "    return 1", "    try:\n        return 1\n    except OSError:\n        pass")
        before = self.commit("an old swallow")
        self.edit(SOURCE, "def run():", "def other():\n    return 3\n\n\ndef run():")
        self.commit("unrelated")
        self.assertEqual(self.run_scan("--base", before, "--head", "HEAD")[0], 0)

    def test_warnings_as_errors_turned_off_is_seen_though_the_name_stays(self):
        self.write("CMakePresets.json", '{\n  "cacheVariables": {\n    "CMAKE_COMPILE_WARNING_AS_ERROR": "ON"\n  }\n}\n')
        self.write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n  </PropertyGroup>\n</Project>\n")
        before = self.commit("strict")
        self.edit("CMakePresets.json", '"CMAKE_COMPILE_WARNING_AS_ERROR": "ON"', '"CMAKE_COMPILE_WARNING_AS_ERROR": "OFF"')
        self.edit("Directory.Build.props", "<TreatWarningsAsErrors>true<", "<TreatWarningsAsErrors>false<")
        self.edit(CMAKE, "set(CMAKE_COMPILE_WARNING_AS_ERROR ON)", "set(CMAKE_COMPILE_WARNING_AS_ERROR OFF)")
        self.commit("relax by value")
        code, text = self.run_scan("--base", before, "--head", "HEAD")
        self.assertEqual(code, 1)
        for expected in ("warning-relaxed CMakePresets.json:L3", "warning-relaxed Directory.Build.props:L3", f"warning-relaxed {CMAKE}:L2"):
            self.assertIn(expected, text)

    def test_a_strict_setting_moved_or_reformatted_is_not_relaxed(self):
        self.edit(CMAKE, "set(CMAKE_COMPILE_WARNING_AS_ERROR ON)\n", "# every warning fails the build\nset(CMAKE_COMPILE_WARNING_AS_ERROR   ON)\n")
        self.commit("reformat")
        self.assertEqual(self.signals(), (0, []))

    def test_an_except_that_does_something_is_not_swallowed(self):
        self.edit(SOURCE, "    return 1", "    try:\n        return 1\n    except OSError:\n        return 0")
        self.commit("handle")
        self.assertEqual(self.signals(), (0, []))

    def test_prose_that_mentions_a_pattern_is_not_code(self):
        self.write("docs/notes.md", "Never add `self.skipTest(...)` or `# noqa` without a reason; `except OSError:` then `pass` hides errors.\n")
        self.commit("notes")
        self.assertEqual(self.signals(), (0, []))

    def test_the_scan_does_not_flag_itself(self):
        self.write("scripts/weakening-scan.py", 'SKIP = "skipTest("  # noqa\n')
        self.write("scripts/tests/test_weakening_scan.py", 'TEXT = "self.skipTest(1)"\n')
        self.commit("the scan")
        self.assertEqual(self.signals(), (0, []))


class Moves(Repo):
    def test_a_renamed_test_file_is_clean(self):
        self.git("mv", PY, "scripts/tests/test_b.py")
        self.commit("rename")
        self.assertEqual(self.signals(), (0, []))

    def test_a_case_moved_to_another_file_is_clean(self):
        self.edit(PY, "    def test_y(self):\n        self.assertTrue(True)\n        self.assertEqual(2, 2)\n", "")
        self.write("scripts/tests/test_c.py", "import unittest\n\n\nclass One(unittest.TestCase):\n    def test_y(self):\n        self.assertTrue(True)\n        self.assertEqual(2, 2)\n")
        self.commit("move")
        self.assertEqual(self.signals(), (0, []))

    def test_a_move_that_drops_an_assertion_is_seen(self):
        self.edit(PY, "    def test_y(self):\n        self.assertTrue(True)\n        self.assertEqual(2, 2)\n", "")
        self.write("scripts/tests/test_c.py", "import unittest\n\n\nclass One(unittest.TestCase):\n    def test_y(self):\n        self.assertTrue(True)\n")
        self.commit("move and thin")
        code, found = self.signals()
        self.assertEqual((code, len(found)), (1, 1))
        self.assertTrue(found[0].startswith(f"assertion-removed {PY}:L"), found)

    def test_a_case_renamed_in_place_is_clean_and_one_that_just_goes_is_not(self):
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)", "Check 'the first thing holds exactly' ($a -eq 1)")
        self.commit("reword")
        self.assertEqual(self.signals(), (0, []))
        self.edit(PS, "Check 'the second thing holds' ($b -eq 2)\n", "")
        self.commit("drop")
        self.assertEqual(self.signals()[1], [f"test-removed {PS}:the second thing holds"])

    def test_a_file_deleted_with_only_some_cases_moved_is_still_a_deletion(self):
        (self.root / PY).unlink()
        self.write("scripts/tests/test_c.py", "import unittest\n\n\nclass One(unittest.TestCase):\n    def test_x(self):\n        self.assertEqual(1, 1)\n")
        self.commit("delete most")
        self.assertEqual(self.signals(), (1, [f"test-file-deleted {PY}:file"]))


class Answers(Repo):
    def two_removed(self, body=""):
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)\n", "")
        self.edit(PS, "Check 'the second thing holds' ($b -eq 2)\n", "")
        return self.commit("drop two checks", body)

    def test_one_answer_covers_one_signal(self):
        self.two_removed(f"Weakening scan: test-removed {PS}:the first thing holds -- the feature it checked was removed")
        code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        self.assertEqual(code, 1)
        self.assertIn(f"  answered   test-removed {PS}:the first thing holds -- the feature it checked was removed", text)
        self.assertIn(f"  unanswered test-removed {PS}:the second thing holds", text)
        self.assertIn("2 signal(s), 1 unanswered", text)

    def test_an_answer_with_no_reason_does_not_count(self):
        self.two_removed(f"Weakening scan: test-removed {PS}:the first thing holds -- \nWeakening scan: test-removed {PS}:the second thing holds")
        code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        self.assertEqual(code, 1)
        self.assertIn("2 signal(s), 2 unanswered", text)

    def test_a_fully_answered_range_exits_0_and_still_shows_each_signal_with_its_reason(self):
        self.two_removed(f"Weakening scan: test-removed {PS}:the first thing holds -- replaced by the unit test\n"
                         f"Weakening scan: test-removed {PS}:the second thing holds -- the option it covered is gone")
        code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        self.assertEqual(code, 0)
        self.assertIn("2 signal(s), 0 unanswered", text)
        self.assertIn("-- replaced by the unit test", text)
        self.assertIn("-- the option it covered is gone", text)

    def test_an_answer_in_a_later_commit_of_the_range_counts_and_one_outside_it_does_not(self):
        first = self.two_removed()
        self.edit(SOURCE, "return 1", "return 2")
        answered = self.commit("explain", f"Weakening scan: test-removed {PS}:the first thing holds -- gone with the feature\n"
                                          f"Weakening scan: test-removed {PS}:the second thing holds -- gone with the feature")
        self.assertEqual(self.run_scan("--base", self.base, "--head", answered)[0], 0)
        self.assertEqual(self.run_scan("--base", self.base, "--head", first)[0], 1)  # the answers come after this range


class Ranges(Repo):
    def test_a_range_is_scanned_by_its_own_ends_whatever_came_after_and_whatever_is_dirty(self):
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)\n", "")
        candidate = self.commit("candidate")
        (self.root / PY).unlink()
        self.commit("a later commit")
        self.edit(CMAKE, "set(CMAKE_COMPILE_WARNING_AS_ERROR ON)\n", "")  # and a dirty working tree
        code, found = self.signals(candidate)
        self.assertEqual((code, found), (1, [f"test-removed {PS}:the first thing holds"]))

    def test_what_is_not_committed_is_listed_and_never_fails_the_scan(self):
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)\n", "")
        self.git("add", PS)  # staged
        self.edit(WORKFLOW, "      - name: Core tests\n        run: build/Release/test_core.exe\n", "")  # not staged
        self.write("scripts/extra.py", "try:\n    pass\nexcept OSError:\n    pass\n")  # untracked
        code, text = self.run_scan("--base", self.base)
        self.assertEqual(code, 0)
        self.assertIn("0 signal(s), 0 unanswered", text)
        tail = text.split("not committed yet:")[1]
        self.assertIn("3 signal(s)", tail)
        for expected in (f"test-removed {PS}:the first thing holds", f"ci-step-removed {WORKFLOW}:L9", "error-swallowed scripts/extra.py:L3"):
            self.assertIn(expected, tail)

    def test_with_head_given_nothing_uncommitted_is_looked_at(self):
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)\n", "")
        code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        self.assertEqual(code, 0)
        self.assertNotIn("not committed yet", text)

    def test_with_no_origin_main_the_base_is_head(self):
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)\n", "")
        head = self.commit("drop")
        code, text = self.run_scan()
        self.assertEqual(code, 0)
        self.assertIn(f"committed {head[:7]}..{head[:7]}: 0 signal(s)", text)

    def test_with_an_origin_main_the_base_is_where_the_branch_left_it(self):
        self.git("update-ref", "refs/remotes/origin/main", self.base)
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)\n", "")
        self.commit("drop")
        code, text = self.run_scan()
        self.assertEqual(code, 1)
        self.assertIn(f"committed {self.base[:7]}..", text)

    def test_a_revision_that_is_not_a_commit_exits_2(self):
        for argv in (("--base", "no-such-rev"), ("--base", self.base, "--head", "0" * 40)):
            code, text = self.run_scan(*argv)
            self.assertEqual(code, 2, text)
            self.assertIn("is not a commit here", text)

    def test_a_diff_git_cannot_produce_exits_2_and_is_never_read_as_empty(self):
        (self.root / PY).unlink()
        self.commit("drop")
        real = scan.run_git

        def failing(root, arguments):
            if arguments[0] == "diff":
                raise scan.CannotRead("git diff --no-color --no-renames failed: fatal: bad object")
            return real(root, arguments)

        with mock.patch.object(scan, "run_git", failing):
            code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        self.assertEqual(code, 2)
        self.assertIn("fatal: bad object", text)
        self.assertNotIn("0 signal(s)", text)

    def test_outside_a_repository_it_exits_2(self):
        with tempfile.TemporaryDirectory(dir=SCRATCH) as empty:
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                code = scan.main([], root=Path(empty))
        self.assertEqual(code, 2)


if __name__ == "__main__":
    unittest.main()
