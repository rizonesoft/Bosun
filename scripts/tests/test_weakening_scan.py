"""The weakening scan: every hunk that can weaken what the tests prove is named, answered one by one, and shown.

Each case builds a small git repository, commits a change, and runs the scan over it, so what is tested is the scan
reading real diffs through git."""
import contextlib
import importlib.util
import io
import os
import re
from pathlib import Path
import subprocess
import sys
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "lib"))
from scratch import scratch_tempdir
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
        self.tmp = scratch_tempdir("weakening-scan")
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
        """The signals of the range from the first commit to `head` (default HEAD), as printed, without their state
        and without the `#<fp>` fingerprint of a line signal (the Fingerprints cases read those)."""
        code, text = self.run_scan("--base", self.base, "--head", head or "HEAD")
        return code, [re.sub(r"(:L\d+)#[0-9a-f]{6}$", r"\1", line.strip().split(" ", 1)[1].split(" -- ")[0])
                      for line in text.splitlines() if line.startswith("  answered") or line.startswith("  unanswered")]


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

    def test_an_xunit_skip_is_a_signal(self):
        # 2026-11-19: nine Assert.Skip calls came into a C# test file and the scan saw none.
        self.write("tests/Panel.Tests/ThingTests.cs",
                   "public class ThingTests\n{\n    [Fact]\n    public void A() { Assert.Skip(\"not elevated\"); }\n"
                   "    [Fact(Skip = \"later\")]\n    public void B() { }\n"
                   "    [Fact]\n    public void C() { Assert.SkipUnless(false, \"no desk\"); }\n"
                   "    [Fact]\n    public void D() { Assert.True(true); }\n}\n")
        self.commit("skips in C#")
        found = [s for s in self.signals()[1] if s.startswith("skip-added tests/Panel.Tests/ThingTests.cs")]
        self.assertEqual(found, ["skip-added tests/Panel.Tests/ThingTests.cs:L4", "skip-added tests/Panel.Tests/ThingTests.cs:L5",
                                 "skip-added tests/Panel.Tests/ThingTests.cs:L8"])

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

    def test_the_type_checker_lowered_is_reported(self):
        # D00 T07 §2: zero pyright errors must not be bought by switching a rule off, a blanket ignore, a path dropped
        # from what it checks or its mode lowered; a stricter setting is no signal.
        config = '{\n  "include": ["scripts", "tests"],\n  "exclude": ["build"]\n}\n'
        self.write("pyrightconfig.json", config)
        self.base = self.commit("the checker's settings")
        for name, change, expected in (
                ("a rule switched off", lambda: self.edit("pyrightconfig.json", '"exclude": ["build"]',
                                                         '"exclude": ["build"],\n  "reportOptionalSubscript": false'),
                 ["warning-relaxed pyrightconfig.json:L1"]),
                ("a blanket ignore hiding a known error", lambda: self.edit(SOURCE, "    return 1", "    return None[0]  # pyright: ignore"),
                 [f"warning-relaxed {SOURCE}:L2"]),
                ("a file put on basic", lambda: self.edit(SOURCE, "def run", "# pyright: basic\ndef run"),
                 [f"warning-relaxed {SOURCE}:L1"]),
                ("a path dropped", lambda: self.edit("pyrightconfig.json", '["scripts", "tests"]', '["scripts"]'),
                 ["warning-relaxed pyrightconfig.json:L1"]),
                ("the mode lowered", lambda: self.edit("pyrightconfig.json", '"exclude": ["build"]',
                                                       '"exclude": ["build"],\n  "typeCheckingMode": "basic"'),
                 ["warning-relaxed pyrightconfig.json:L1"]),
                # Round 5 of D00 T07 §2: the scan no longer judges a config edit, so a stricter one is answered too.
                ("a stricter mode", lambda: self.edit("pyrightconfig.json", '"exclude": ["build"]',
                                                      '"exclude": ["build"],\n  "typeCheckingMode": "strict"'),
                 ["warning-relaxed pyrightconfig.json:L1"]),
                ("a comment only", lambda: self.edit("pyrightconfig.json", "{", "{\n  // generated folders stay out"),
                 ["warning-relaxed pyrightconfig.json:L1"]),
                # Round 1 of D00 T07 §2: a file-level rule set to a severity, and a rule lowered for one folder.
                ("a file-level rule set to none", lambda: self.edit(SOURCE, "def run", "# pyright: reportAssignmentType=none\ndef run"),
                 [f"warning-relaxed {SOURCE}:L1"]),
                ("a rule lowered for one folder", lambda: self.edit("pyrightconfig.json", '"exclude": ["build"]',
                                                                    '"exclude": ["build"],\n  "executionEnvironments": [{"root": "scripts", "reportAssignmentType": "none"}]'),
                 ["warning-relaxed pyrightconfig.json:L1"])):
            with self.subTest(name):
                change()
                head = self.commit(name)
                self.assertEqual(self.signals(head)[1], expected)
                self.git("reset", "-q", "--hard", self.base)

    def test_each_checker_weakening_is_answered_on_its_own(self):
        # Round 1 of D00 T07 §2: every config weakening had the file's first-line id, so an answer given for one (an
        # ignored test) also covered a different one made later in the range (a rule switched off).
        self.write("pyrightconfig.json", '{\n  "include": ["scripts"]\n}\n')
        self.base = self.commit("the checker's settings")
        self.edit("pyrightconfig.json", '"include": ["scripts"]', '"include": ["scripts"],\n  "ignore": ["scripts/tests/test_x.py"]')
        first = self.commit("ignore one test")
        code, text = self.run_scan("--base", self.base, "--head", first)
        ignored = [line.split()[2] for line in text.splitlines() if line.strip().startswith("unanswered")][0]
        self.edit("pyrightconfig.json", '"include": ["scripts"],', '"include": ["scripts"],\n  "reportAssignmentType": "none",')
        self.commit("switch a rule off",
                    f"Weakening scan: warning-relaxed {ignored} -- the test is generated and checked on its own")
        code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        # The range's config is its final content, which the answer written for the earlier content does not cover.
        self.assertEqual(code, 1, text)
        self.assertEqual(sum(line.strip().startswith("unanswered") for line in text.splitlines()), 1, text)
        self.assertEqual(sum(line.strip().startswith("answered") for line in text.splitlines()), 0, text)

    def test_every_rule_of_a_directive_is_read(self):
        # Round 3 of D00 T07 §2: only a directive's first rule was read.
        self.assertTrue(scan.RELAX.search("# pyright: reportArgumentType=error, reportAssignmentType=none"))
        self.assertFalse(scan.RELAX.search("# pyright: reportArgumentType=error, reportAssignmentType=error"))
        self.assertFalse(scan.RELAX.search("# pyright: strict"))

    def test_a_config_answer_needs_its_fingerprint(self):
        # Round 6 of D00 T07 §2: an answer giving only L1 matched any config signal, whose line is always 1.
        self.write("pyrightconfig.json", "{}\n")
        self.base = self.commit("an empty config")
        self.edit("pyrightconfig.json", "{}", '{"exclude": ["scripts"]}')
        self.commit("exclude the scripts", "Weakening scan: warning-relaxed pyrightconfig.json:L1 -- a formatting change")
        code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        self.assertEqual(code, 1, text)  # the bare L1 answer covers nothing
        signal = [line.split()[2] for line in text.splitlines() if line.strip().startswith("unanswered")][0]
        self.git("commit", "-q", "--allow-empty", "-m", "answer it", "-m", f"Weakening scan: warning-relaxed {signal} -- checked")
        self.assertEqual(self.run_scan("--base", self.base, "--head", "HEAD")[0], 0)  # its fingerprinted answer does

    def test_the_scan_s_own_files_are_read_for_real_suppressions(self):
        # Round 7 of D00 T07 §2: the scan's own files skipped every line pattern, so a real pyright comment there was
        # never seen. Real comments are read by the tokenizer; the same words in a fixture string stay quiet.
        own = "scripts/tests/test_weakening_scan.py"
        self.write(own, "FIXTURE = 'x = 1  # pyright: ignore'\n")
        self.base = self.commit("the scan's own test")
        self.edit(own, "FIXTURE = 'x = 1  # pyright: ignore'\n",
                  "FIXTURE = 'x = 1  # pyright: ignore'\nOTHER = 'y = 2  # type: ignore'\nprobe: int = 'bad'  # pyright: ignore\n")
        self.assertEqual(self.signals(self.commit("suppress in the scan's own test"))[1], [f"warning-relaxed {own}:L3"])

    def test_a_python_directive_counts_only_as_a_real_comment(self):
        # Round 9 of D00 T07 §2: a directive held in a string was reported like a real one. A file that does not
        # tokenize is still read line by line, so a broken file hides nothing.
        self.write("scripts/probe.py", "x = 1\n")
        self.base = self.commit("a module")
        self.edit("scripts/probe.py", "x = 1\n", "x = 1\nFIXTURE = '# pyright: ignore'\ny: int = 'a'  # pyright: ignore\n")
        self.assertEqual(self.signals(self.commit("a directive and a string"))[1], ["warning-relaxed scripts/probe.py:L3"])
        self.edit("scripts/probe.py", "x = 1\n", "x = (1\n")  # an unclosed bracket: the tokenizer gives up
        self.edit("scripts/probe.py", "y: int = 'a'  # pyright: ignore\n", "y: int = 'a'  # type: ignore\n")
        self.assertIn("warning-relaxed scripts/probe.py:L3", self.signals(self.commit("a broken file"))[1])

    def test_the_completion_gaps_of_d00_t07_s2(self):
        # A mode anywhere in a directive's list relaxes checking; strict and an error-level rule do not.
        self.assertTrue(scan.RELAX.search("# pyright: reportAssignmentType=error, basic"))
        self.assertFalse(scan.RELAX.search("# pyright: reportAssignmentType=error, strict"))
        # A fixture string made live by removing its quotes, the directive's own line unchanged.
        self.write("scripts/probe.py", 'X = """\n# pyright: reportAssignmentType=none\n"""\n')
        self.base = self.commit("a fixture string")
        self.edit("scripts/probe.py", 'X = """\n# pyright: reportAssignmentType=none\n"""\n',
                  "X = 1\n# pyright: reportAssignmentType=none\n")
        head = self.commit("the quotes taken off")
        self.assertEqual(self.signals(head)[1], ["warning-relaxed scripts/probe.py:L2"])
        self.base = head
        # A compiler flag in a Python command string is still a flag.
        self.edit("scripts/probe.py", "X = 1\n", 'X = 1\nCMD = "clang -Wno-unused-variable"\n')
        head = self.commit("a build command")
        self.assertEqual(self.signals(head)[1], ["warning-relaxed scripts/probe.py:L2"])
        self.base = head
        # The scan's own test, broken, still has its real directive read.
        self.write(self.OWN_TEST, "x = (1\ny = 2  # pyright: ignore\n")
        self.assertIn(f"warning-relaxed {self.OWN_TEST}:L2", self.signals(self.commit("a broken own test"))[1])
        # The config's final newline alone is a change.
        self.write("pyrightconfig.json", "{}\n")
        self.base = self.commit("a config")
        self.write("pyrightconfig.json", "{}")
        self.assertEqual(self.signals(self.commit("its last newline"))[1], ["warning-relaxed pyrightconfig.json:L1"])

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

    def test_a_handler_body_deleted_outright_is_seen_though_no_line_was_added(self):
        self.write("scripts/tool.ps1", "try {\n  Get-Item x\n} catch {\n  Remove-Item y\n  throw\n}\n'after'\n")
        self.write("src/tool.cpp", "void f() {\n  try { g(); } catch (...) {\n    cleanup();\n    throw;\n  }\n}\n")
        self.edit(SOURCE, "    return 1", "    try:\n        return 1\n    except OSError:\n        log()\n        pass")
        before = self.commit("handlers that do something")
        self.edit("scripts/tool.ps1", "  Remove-Item y\n  throw\n", "")
        self.edit("src/tool.cpp", "    cleanup();\n    throw;\n", "")
        self.edit(SOURCE, "        log()\n", "")
        self.commit("delete the bodies")
        code, text = self.run_scan("--base", before, "--head", "HEAD")
        self.assertEqual(code, 1)
        for expected in ("error-swallowed scripts/tool.ps1:L3", "error-swallowed src/tool.cpp:L2", f"error-swallowed {SOURCE}:L4"):
            self.assertIn(expected, text)

    def test_a_deletion_next_to_an_old_swallowing_handler_is_not_a_change_to_it(self):
        self.write("scripts/tool.ps1", "'before'\n'remove me'\ntry { Get-Item x } catch {\n}\n'remove me too'\n'after'\n")
        before = self.commit("an old empty handler")
        self.edit("scripts/tool.ps1", "'remove me'\n", "")
        self.edit("scripts/tool.ps1", "'remove me too'\n", "")
        self.commit("delete the lines around it")
        self.assertEqual(self.run_scan("--base", before, "--head", "HEAD")[0], 0)

    def test_other_ways_of_swallowing_are_seen_too(self):
        self.write("scripts/a.ps1", "try { Get-Item x } catch {\n  # nothing to do\n}\n")
        self.write("src/a.cpp", "void f() {\n  try { g(); } catch (...) { // ignored\n  }\n}\n")
        self.write("scripts/a.py", "import contextlib\n\n\ndef f():\n    try:\n        g()\n    except OSError:\n        ...\n"
                                   "    with contextlib.suppress(ValueError):\n        h()\n")
        self.commit("quiet handlers")
        code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        self.assertEqual(code, 1)
        for expected in ("error-swallowed scripts/a.ps1:L1", "error-swallowed src/a.cpp:L2", "error-swallowed scripts/a.py:L7",
                         "error-swallowed scripts/a.py:L9"):
            self.assertIn(expected, text)

    def test_a_handler_that_says_why_and_acts_is_not_swallowed(self):
        self.write("scripts/a.ps1", "try { Get-Item x } catch {\n  # the file may be gone already\n  'gone'\n}\n")
        self.write("src/a.cpp", "void f() {\n  try { g(); } catch (...) { // report it\n    report();\n  }\n}\n")
        self.commit("handlers that act")
        self.assertEqual(self.signals(), (0, []))

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

    OWN_TEST = "scripts/tests/test_weakening_scan.py"
    OWN_TEXT = ("import unittest\n\n\nFIXTURE = \"\"\"\n    self.skipTest('in a fixture')  # noqa\n    except OSError:\n        pass\n\"\"\"\n\n\n"
                "class Kinds(unittest.TestCase):\n    def test_a_rule(self):\n        self.write('x', 'self.skipTest(1)')\n        self.assertEqual(1, 1)\n\n"
                "    def test_another_rule(self):\n        self.assertTrue(True)\n")

    def test_the_patterns_the_scan_s_own_files_hold_as_text_are_not_flagged(self):
        # Patterns held as text, in strings as the scan's rules hold them; a real comment would be its own signal (round 7).
        self.write("scripts/weakening-scan.py", 'SKIP = "skipTest("\nNOQA = "# noqa"\n')
        self.write(self.OWN_TEST, self.OWN_TEXT)
        self.commit("the scan and its test")
        self.assertEqual(self.signals(), (0, []))

    def test_real_weakening_of_the_scan_s_own_test_is_flagged(self):
        self.write(self.OWN_TEST, self.OWN_TEXT)
        before = self.commit("the scan's test")
        self.edit(self.OWN_TEST, "    def test_another_rule(self):\n        self.assertTrue(True)\n", "")
        self.edit(self.OWN_TEST, "        self.assertEqual(1, 1)\n", "")
        self.edit(self.OWN_TEST, "    def test_a_rule(self):\n", "    def test_a_rule(self):\n        self.skipTest('later')\n")
        self.commit("thin the scan's own test")
        code, text = self.run_scan("--base", before, "--head", "HEAD")
        self.assertEqual(code, 1)
        for expected in (f"test-removed {self.OWN_TEST}:Kinds.test_another_rule", f"assertion-removed {self.OWN_TEST}:L",
                         f"skip-added {self.OWN_TEST}:L13"):
            self.assertIn(expected, text)
        (self.root / self.OWN_TEST).unlink()
        self.commit("delete the scan's own test")
        self.assertIn(f"test-file-deleted {self.OWN_TEST}:file", self.run_scan("--base", before, "--head", "HEAD")[1])


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

    def test_a_signal_in_a_path_with_a_space_can_be_answered_by_the_text_the_report_prints(self):
        spaced = "tests/queue copy_test.ps1"
        self.write(spaced, PS_TEXT)
        before = self.commit("a test with a space in its name")
        self.edit(spaced, "Check 'the second thing holds' ($b -eq 2)\n", "")
        self.commit("drop a check")
        code, text = self.run_scan("--base", before, "--head", "HEAD")
        self.assertEqual(code, 1)
        printed = next(line.strip().split(" ", 1)[1] for line in text.splitlines() if line.startswith("  unanswered"))
        self.assertEqual(printed, f"test-removed {spaced}:the second thing holds")
        self.git("commit", "-q", "--allow-empty", "-m", "explain", "-m", f"Weakening scan: {printed} -- the option it covered is gone")
        code, text = self.run_scan("--base", before, "--head", "HEAD")
        self.assertEqual(code, 0, text)
        self.assertIn(f"  answered   {printed} -- the option it covered is gone", text)

    def test_an_answer_in_a_later_commit_of_the_range_counts_and_one_outside_it_does_not(self):
        first = self.two_removed()
        self.edit(SOURCE, "return 1", "return 2")
        answered = self.commit("explain", f"Weakening scan: test-removed {PS}:the first thing holds -- gone with the feature\n"
                                          f"Weakening scan: test-removed {PS}:the second thing holds -- gone with the feature")
        self.assertEqual(self.run_scan("--base", self.base, "--head", answered)[0], 0)
        self.assertEqual(self.run_scan("--base", self.base, "--head", first)[0], 1)  # the answers come after this range


HANDLER = "    try:\n        a()\n    except Failure:\n        pass  # {why}\n"


class Fingerprints(Repo):
    """A line signal's id carries a fingerprint of the code it flags, so an answer survives an edit that moves it."""

    def handlers(self, *whys):
        self.edit(SOURCE, "    return 1", "".join(HANDLER.format(why=why) for why in whys) + "    return 1")
        return self.commit("add handlers")

    def printed(self):
        """(exit code, [(state, id)] of the signals of the range, ids as printed)."""
        code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        return code, [(line.split()[0], line.split()[2].split(":", 1)[1]) for line in text.splitlines()
                      if line.startswith(("  answered", "  unanswered"))]

    def answer(self, *ids):
        self.git("commit", "-q", "--allow-empty", "-m", "explain",
                 "-m", "\n".join(f"Weakening scan: error-swallowed {SOURCE}:{ident} -- the read below says so" for ident in ids))

    def test_an_answered_signal_stays_answered_when_lines_are_inserted_above_it(self):
        self.handlers("the read below says it is fine")
        ident = self.printed()[1][0][1]
        self.assertRegex(ident, r"^L4#[0-9a-f]{6}$")
        self.answer(ident)
        self.assertEqual(self.printed()[0], 0)
        self.edit(SOURCE, "def run():", "# one\n# two\n# three\n# four\n# five\ndef run():")
        self.commit("insert five lines above")
        self.assertEqual(self.printed(), (0, [("answered", f"L9#{ident.split('#')[1]}")]))

    def test_two_identical_handler_lines_with_different_following_comments_get_different_fingerprints(self):
        self.handlers("the read below says it is fine", "the write is retried")
        found = self.printed()[1]
        self.assertEqual(len(found), 2)
        self.assertNotEqual(found[0][1].split("#")[1], found[1][1].split("#")[1])

    def test_two_truly_identical_handlers_need_two_answers(self):
        self.handlers("the same reason", "the same reason")
        first, second = (ident for _, ident in self.printed()[1])
        self.assertEqual(first.split("#")[1], second.split("#")[1])
        self.answer(first)
        code, found = self.printed()
        self.assertEqual((code, [state for state, _ in found]), (1, ["answered", "unanswered"]))
        self.edit(SOURCE, "def run():", "# one\n# two\ndef run():")  # moved: still one answer for two signals
        self.commit("insert two lines above")
        self.assertEqual([state for state, _ in self.printed()[1]], ["answered", "unanswered"])
        self.answer(second)
        self.assertEqual(self.printed()[0], 0)

    def test_an_answer_with_only_a_line_number_covers_nothing(self):
        # Rounds 6 and 8 of D00 T07 §2: a bare L<line> answer covered whatever later stood on that line, a pyright
        # directive put where a noqa was answered included. Only the fingerprinted form covers a signal.
        self.handlers("the read below says it is fine")
        self.answer("L4")
        code, found = self.printed()
        self.assertEqual((code, [state for state, _ in found]), (1, ["unanswered"]))
        self.assertRegex(found[0][1], r"^L4#[0-9a-f]{6}$")  # the report prints the form to answer with
        self.answer(found[0][1])
        self.assertEqual(self.printed()[0], 0)

    def test_an_answer_whose_fingerprint_does_not_match_a_changed_line_leaves_it_unanswered(self):
        self.handlers("the read below says it is fine")
        self.answer(self.printed()[1][0][1])
        self.edit(SOURCE, "pass  # the read below says it is fine", "pass  # nothing says it is fine")
        self.commit("change the handler")
        code, found = self.printed()
        self.assertEqual((code, [state for state, _ in found]), (1, ["unanswered"]))


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

    def test_a_staged_removal_put_back_in_the_working_tree_only_is_still_seen(self):
        original = (self.root / PS).read_text(encoding="utf-8")
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)\n", "")
        self.git("add", PS)  # the next commit removes the check
        self.write(PS, original)  # the working tree has it back, unstaged
        self.assertEqual(self.git("diff", "HEAD", "--", PS).stdout, "")  # against HEAD the two cancel out
        code, text = self.run_scan("--base", self.base)
        self.assertEqual(code, 0)
        self.assertIn(f"test-removed {PS}:the first thing holds", text.split("not committed yet:")[1])

    def test_an_unstaged_removal_with_nothing_staged_is_seen_and_listed_once(self):
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)\n", "")
        tail = self.run_scan("--base", self.base)[1].split("not committed yet:")[1]
        self.assertEqual(tail.count(f"test-removed {PS}:the first thing holds"), 1)
        self.git("add", PS)
        tail = self.run_scan("--base", self.base)[1].split("not committed yet:")[1]
        self.assertEqual(tail.count(f"test-removed {PS}:the first thing holds"), 1)

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

    def test_a_file_s_text_that_cannot_be_read_exits_2_and_is_not_taken_for_an_absent_file(self):
        # The handler's `except` line is unchanged, so the signal needs the file's new text: without it the scan
        # would find nothing and pass.
        self.edit(SOURCE, "    return 1", "    try:\n        return 1\n    except OSError:\n        raise\n        pass")
        before = self.commit("a handler that raises")
        self.edit(SOURCE, "        raise\n", "")
        self.commit("stop raising")
        self.assertEqual(self.run_scan("--base", before, "--head", "HEAD")[0], 1)  # read normally, it is seen
        real_run = subprocess.run
        for side in ("old", "new"):
            with self.subTest(side=side):
                def failing(cmd, *args, side=side, **kwargs):
                    if "cat-file" in cmd and "--batch" in cmd:
                        return subprocess.CompletedProcess(
                            cmd, 128, b"", f"error: unable to read the {side} object (I/O error)\n".encode("utf-8"))
                    return real_run(cmd, *args, **kwargs)

                with mock.patch.object(subprocess, "run", failing):
                    code, text = self.run_scan("--base", before, "--head", "HEAD")
                self.assertEqual(code, 2, text)
                self.assertIn("I/O error", text)
                self.assertNotIn("0 signal(s)", text)

    def test_a_working_file_that_cannot_be_read_exits_2(self):
        self.edit(SOURCE, "    return 1", "    return 2")
        real = Path.read_text

        def unreadable(path, *args, **kwargs):
            if path.name == "tool.py":
                raise PermissionError("the file is in use by another process")
            return real(path, *args, **kwargs)

        with mock.patch.object(Path, "read_text", unreadable):
            code, text = self.run_scan("--base", self.base)
        self.assertEqual(code, 2)
        self.assertIn("scripts/tool.py can't be read", text)

    def test_a_new_file_and_a_deleted_file_are_known_from_the_diff_and_read_on_one_side_only(self):
        (self.root / PS).unlink()
        self.write("scripts/fresh.py", "def f():\n    return 1\n")
        self.commit("one comes, one goes")
        asked = []
        batch_calls = 0
        real_run = subprocess.run

        def recording(cmd, *args, **kwargs):
            nonlocal batch_calls
            if "cat-file" in cmd and "--batch" in cmd:
                batch_calls += 1
                raw = kwargs.get("input", b"")
                for line in raw.decode("utf-8").splitlines():
                    asked.append(line.split(":", 1))
            return real_run(cmd, *args, **kwargs)

        with mock.patch.object(subprocess, "run", recording):
            code, found = self.signals()
        self.assertEqual((code, found), (1, [f"test-file-deleted {PS}:file"]))
        head = self.git("rev-parse", "HEAD").stdout.strip()
        self.assertEqual(batch_calls, 1)
        self.assertEqual(sorted(asked), sorted([[self.base, PS], [head, "scripts/fresh.py"]]))

    def test_a_base_that_cannot_be_worked_out_exits_2_and_is_not_taken_for_nothing_to_scan(self):
        self.git("update-ref", "refs/remotes/origin/main", self.base)
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)\n", "")
        self.commit("drop")
        self.assertEqual(self.run_scan()[0], 1)  # worked out normally, the removal is seen
        real = scan.git_result
        for command in ("rev-parse", "merge-base"):
            with self.subTest(command=command):
                def failing(root, arguments, command=command):
                    if arguments[0] == command and "origin/main" in " ".join(arguments):
                        return 128, "", "fatal: unable to read tree (I/O error)"
                    return real(root, arguments)

                with mock.patch.object(scan, "git_result", failing):
                    code, text = self.run_scan()
                self.assertEqual(code, 2, text)
                self.assertIn("I/O error", text)

    def test_histories_with_no_common_commit_fall_back_to_head(self):
        self.git("checkout", "-q", "--orphan", "other")
        self.git("commit", "-q", "--allow-empty", "-m", "unrelated")
        self.git("update-ref", "refs/remotes/origin/main", "HEAD")
        self.git("checkout", "-q", "main")
        code, text = self.run_scan()
        head = self.git("rev-parse", "HEAD").stdout.strip()
        self.assertEqual(code, 0)
        self.assertIn(f"committed {head[:7]}..{head[:7]}: 0 signal(s)", text)

    def test_outside_a_repository_it_exits_2(self):
        with scratch_tempdir("weakening-scan") as empty:
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                code = scan.main([], root=Path(empty))
        self.assertEqual(code, 2)


class BatchAndLines(Repo):
    """D00 T07 §5: one binary-framed `git cat-file --batch` per blob set and per-signal before/after lines."""

    def test_batch_and_show_readers_produce_identical_signals(self):
        self.edit(PY, "        self.assertTrue(True)\n", "")
        self.edit(SOURCE, "    return 1", "    try:\n        return 1\n    except OSError:\n        pass")
        (self.root / WORKFLOW).unlink()
        self.write("scripts/utf8.py", "# café 日本語 🚀\ndef g():\n    return 1\n")
        head = self.commit("mixed changes")
        batch_sigs = scan.scan(scan.committed(self.root, self.base, head, reader=scan.read_blobs_batch))
        show_sigs = scan.scan(scan.committed(self.root, self.base, head, reader=scan.read_blobs_show))
        self.assertEqual([(s.key, s.before, s.after) for s in batch_sigs],
                         [(s.key, s.before, s.after) for s in show_sigs])
        self.assertGreaterEqual(len(batch_sigs), 2)

    def test_total_git_subprocesses_do_not_grow_per_file(self):
        def count_git_runs(num_files):
            for i in range(num_files):
                self.write(f"scripts/f{i}.py", f"def f{i}():\n    return {i}\n")
            start = self.commit(f"add {num_files}")
            for i in range(num_files):
                self.write(f"scripts/f{i}.py", f"def f{i}():\n    return {i + 1}\n")
            end = self.commit(f"edit {num_files}")
            cmds = []
            real_run = subprocess.run

            def counting(cmd, *args, **kwargs):
                cmds.append(list(cmd))
                return real_run(cmd, *args, **kwargs)

            with mock.patch.object(subprocess, "run", counting):
                code, _ = self.run_scan("--base", start, "--head", end)
            self.assertEqual(code, 0)
            self.assertFalse(any("show" in c for c in cmds), cmds)
            self.assertEqual(sum(1 for c in cmds if "cat-file" in c and "--batch" in c), 1)
            return len(cmds)

        self.assertEqual(count_git_runs(1), count_git_runs(10))

    def test_batch_multibyte_utf8_framing_and_refusals(self):
        # Multibyte UTF-8 blob followed by a second blob in one batch proves byte-length framing alignment.
        utf8_body = "# naïve résumé 日本語 🧭\ndef a():\n    return 1\n"
        second_body = "def b():\n    return 2  # noqa: F841\n"
        self.write("scripts/a_utf8.py", utf8_body)
        self.write("scripts/b_next.py", "def b():\n    return 2\n")
        before = self.commit("utf8 pair")
        self.write("scripts/a_utf8.py", utf8_body + "x = 1\n")
        self.write("scripts/b_next.py", second_body)
        head = self.commit("edit utf8 pair")
        code, text = self.run_scan("--base", before, "--head", head)
        self.assertEqual(code, 1, text)
        self.assertIn("warning-relaxed scripts/b_next.py:L2", text)
        self.assertIn("+ L2:     return 2  # noqa: F841", text)

        # Exercise every batch framing refusal through main(): missing, non-blob, short payload, missing terminator,
        # and non-zero exit even after valid blob output.
        valid_blob = b"x = 1\n"
        valid_frame = f"{'a' * 40} blob {len(valid_blob)}\n".encode("utf-8") + valid_blob + b"\n"
        bad_cases = [
            ("missing", 0, valid_frame + b"bad:path missing\n", b""),
            ("non-blob", 0, valid_frame + f"{'b' * 40} tree 4\nabcd\n".encode("utf-8"), b""),
            ("short-payload", 0, valid_frame + f"{'b' * 40} blob 50\nabc".encode("utf-8"), b""),
            ("missing-terminator", 0, valid_frame + f"{'b' * 40} blob 3\nabcX".encode("utf-8"), b""),
            ("nonzero-after-valid", 128, valid_frame * 4, b"fatal: packfile corrupt\n"),
        ]
        real_run = subprocess.run
        for label, rc, out_bytes, err_bytes in bad_cases:
            with self.subTest(label=label):
                def spoof(cmd, *args, rc=rc, out_bytes=out_bytes, err_bytes=err_bytes, **kwargs):
                    if "cat-file" in cmd and "--batch" in cmd:
                        return subprocess.CompletedProcess(cmd, rc, out_bytes, err_bytes)
                    return real_run(cmd, *args, **kwargs)

                with mock.patch.object(subprocess, "run", spoof):
                    code, text = self.run_scan("--base", before, "--head", head)
                self.assertEqual(code, 2, text)
                self.assertIn("weakening-scan:", text)
                self.assertNotIn("0 signal(s)", text)

    def test_report_shows_exact_before_and_after_lines_for_distant_changes_and_cap(self):
        # Removed assertion test: reads the exact removed line in the report with no invented '+' line.
        self.edit(PY, "        self.assertTrue(True)\n", "")
        self.commit("thin assertion")
        code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        self.assertEqual(code, 1, text)
        self.assertIn("    - L9:         self.assertTrue(True)", text)
        self.assertNotIn("    + L", text)

        # Two distant changes in one file: each signal prints only its own hunk's lines, capped at 3 per side.
        filler = "".join(f"# filler {i}\n" for i in range(25))
        many_ps = (
            "Check 'top removed' ($a -eq 1)\n"
            "# top extra 1\n# top extra 2\n# top extra 3\n# top extra 4\n"
            + filler
            + "Check 'bottom removed' ($b -eq 2)\n"
        )
        self.write(PS, many_ps)
        before = self.commit("distant checks")
        self.write(PS, filler)
        self.commit("remove both distant checks")
        code, text = self.run_scan("--base", before, "--head", "HEAD")
        self.assertEqual(code, 1, text)
        lines = text.splitlines()
        top_idx = next(i for i, line in enumerate(lines) if "test-removed tests/a_test.ps1:top removed" in line)
        bot_idx = next(i for i, line in enumerate(lines) if "test-removed tests/a_test.ps1:bottom removed" in line)
        top_block = lines[top_idx + 1:bot_idx]
        bot_block = [line for line in lines[bot_idx + 1:] if line.startswith("    ")]
        self.assertEqual(len(top_block), 3, top_block)  # capped at 3 lines
        self.assertIn("    - L1: Check 'top removed' ($a -eq 1)", top_block[0])
        self.assertFalse(any("bottom removed" in line for line in top_block))
        self.assertEqual(bot_block, ["    - L31: Check 'bottom removed' ($b -eq 2)"])

        # pyrightconfig.json edit below line 3 shows its actual edited line number, and final-newline-only edit marks the side.
        cfg = '{\n  "include": [\n    "scripts"\n  ],\n  "exclude": ["build"]\n}\n'
        self.write("pyrightconfig.json", cfg)
        c1 = self.commit("config")
        self.edit("pyrightconfig.json", '"exclude": ["build"]', '"exclude": ["build", "tmp"]')
        c2 = self.commit("edit config line 5")
        code, text = self.run_scan("--base", c1, "--head", c2)
        self.assertEqual(code, 1, text)
        self.assertIn('    - L5:   "exclude": ["build"]', text)
        self.assertIn('    + L5:   "exclude": ["build", "tmp"]', text)

        self.write("pyrightconfig.json", cfg.rstrip("\n"))
        c3 = self.commit("drop trailing newline")
        code, text = self.run_scan("--base", c1, "--head", c3)
        self.assertEqual(code, 1, text)
        self.assertIn("(trailing newline)", text)
        self.assertIn("(no trailing newline)", text)

    def test_report_context_lines_across_committed_and_uncommitted_states(self):
        # Answered committed + unanswered committed
        self.edit(PS, "Check 'the first thing holds' ($a -eq 1)\n", "")
        self.edit(PS, "Check 'the second thing holds' ($b -eq 2)\n", "")
        self.commit("drop two", f"Weakening scan: test-removed {PS}:the first thing holds -- replaced")
        code, text = self.run_scan("--base", self.base, "--head", "HEAD")
        self.assertEqual(code, 1, text)
        self.assertIn("    - L2: Check 'the first thing holds' ($a -eq 1)", text)
        self.assertIn("    - L3: Check 'the second thing holds' ($b -eq 2)", text)

        # Staged change later edited again in the working tree: staged signal shows staged/HEAD lines, not working tree.
        self.write("scripts/staged.py", "def f():\n    return 1\n")
        base2 = self.commit("staged base")
        self.write("scripts/staged.py", "def f():\n    return 1  # noqa: F841\n")
        self.git("add", "scripts/staged.py")
        self.write("scripts/staged.py", "def f():\n    return 999\n")
        # Unstaged change + untracked file
        self.edit(WORKFLOW, "      - name: Core tests\n        run: build/Release/test_core.exe\n", "")
        self.write("scripts/untracked.py", "try:\n    x()\nexcept OSError:\n    pass\n")
        code, text = self.run_scan("--base", base2)
        self.assertEqual(code, 0, text)
        tail = text.split("not committed yet:")[1]
        self.assertIn("    + L2:     return 1  # noqa: F841", tail)
        self.assertNotIn("return 999", tail)
        self.assertIn("    - L9:         run: build/Release/test_core.exe", tail)
        self.assertIn("    + L3: except OSError:", tail)
        untracked_section = tail.split("error-swallowed scripts/untracked.py:")[1]
        self.assertNotIn("    - L", untracked_section)


if __name__ == "__main__":
    unittest.main()
