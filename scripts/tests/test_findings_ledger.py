"""Tests for scripts/findings-ledger.py: a settled range passes, and each way a finding can look settled without being
so fails by its base line (D00 T06 §1)."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("findings_ledger", ROOT / "scripts" / "findings-ledger.py")
assert SPEC is not None and SPEC.loader is not None
ledger = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ledger)
sys.path.insert(0, str(ROOT / "scripts" / "lib"))
from todo_graph import Refused as ledger_refused  # noqa: E402

BASE = """# Findings

Header.

- `scripts/a.py:1`: the first finding.
- `scripts/b.py`: the second finding.
- ~~`scripts/c.py`: settled before the base.~~ Done in D00 T02 §4: long ago.
- `scripts/d.py`: the fourth finding.
"""
OPEN = {"D09 T01 §1": (0, "Fix `scripts/a.py` so the first finding holds."), "D09 T01 §2": (0, "Unrelated work.")}
STAMPED = {"D09 T01 §3": (3, "")}


def fake_resolve(ref, root):
    return {**OPEN, **STAMPED}.get(ref, (1, ""))


def git(root, *args):
    subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True)


class FindingsLedgerTest(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(dir=os.environ.get("BOSUN_SCRATCH") or None))
        self.addCleanup(shutil.rmtree, self.root, True)
        git(self.root, "init", "-q")
        git(self.root, "config", "user.email", "t@example.invalid")
        git(self.root, "config", "user.name", "t")
        (self.root / "todo").mkdir()
        self.findings = self.root / "todo" / "findings.md"
        self.findings.write_text(BASE, encoding="utf-8")
        git(self.root, "add", "-A")
        git(self.root, "commit", "-q", "-m", "base")
        self.base = subprocess.run(["git", "-C", str(self.root), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()

    def test_a_scheduled_owner_stamped_after_the_strike_stays_settled(self):
        # D00 T03 §30's validation: D00 T06 §2's checkpoint failed once the section it scheduled two findings to was
        # stamped. Stamped after the strike is settled; stamped before it, scheduling into finished work, is not.
        self.settle("Scheduled: D09 T01 §1.", "Declined: a one-off script nobody runs again.", "Done: D09 T01 §3 shipped it.")
        git(self.root, "add", "-A")
        git(self.root, "commit", "-qm", "struck")
        (self.root / "later.txt").write_text("the owner's work\n", encoding="utf-8")
        git(self.root, "add", "-A")
        git(self.root, "commit", "-qm", "the owner ships and stamps")
        later = subprocess.run(["git", "-C", str(self.root), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
        stamped = lambda sha: "Fix `scripts/a.py` so the first finding holds.\n> **Verified:** 2026-11-22 | §1 | %s\n" % sha
        for sha, settled in ((later, True), (self.base, False)):
            with self.subTest(stamped_at=sha[:9]):
                resolver = lambda ref, root, sha=sha: (3, stamped(sha)) if ref == "D09 T01 §1" else fake_resolve(ref, root)
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    code = ledger.check(self.base, 5, 8, root=self.root, resolver=resolver)
                if settled:
                    self.assertEqual(code, 0, out.getvalue())
                else:
                    self.assertEqual(code, 1)
                    self.assertIn("base line 5: D09 T01 §1 was already stamped when this was scheduled to it", out.getvalue())

    def checked(self, resolver=fake_resolve, **options):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = ledger.check(self.base, 5, 8, root=self.root, resolver=resolver, **options)
        return code, out.getvalue()

    def test_an_owner_must_cite_the_findings_own_base_line(self):
        # D00 T06 §3's plan review: two findings on one file each need their own item, which naming the file can't show.
        self.settle("Scheduled: D09 T01 §1.", "Declined: a one-off script nobody runs again.", "Done: D09 T01 §3 shipped it.")
        intro = "From `scripts/a.py` (base lines 3 to 8).\n\n"  # a section introduction owns nothing (round 1 of §3)
        for body, settled in ((intro + "- [ ] Fix `scripts/a.py` (base line 5) so the first finding holds.", True),
                              (intro + "- [x] Fix `scripts/a.py` (base lines 3 to 6).", True),
                              (intro + "- [ ] Fix `scripts/a.py` (base lines 4 and 5).", True),
                              (intro + "- [ ] Fix `scripts/a.py` (base line 50) for another finding.", False),
                              (intro + "- [ ] Fix `scripts/a.py` so the first finding holds.", False)):
            with self.subTest(body=body):
                resolver = lambda ref, root, body=body: (0, body) if ref == "D09 T01 §1" else fake_resolve(ref, root)
                code, out = self.checked(resolver, cite_lines=True)
                self.assertEqual(code, 0 if settled else 1, out)
                if not settled:
                    self.assertIn("base line 5: D09 T01 §1 has no checklist item citing base line 5", out)
        code, out = self.checked()  # without --cite-lines, naming the file is enough, as before
        self.assertEqual(code, 0, out)

    def test_the_classification_must_agree_with_every_strike(self):
        self.settle("Scheduled: D09 T01 §1.", "Declined: a one-off script nobody runs again.", "Done: D09 T01 §3 shipped it.")
        record = self.root / "classification.md"
        owner = "Fix `scripts/a.py`.\n\n- [ ] Something else (base line 9).\n- [ ] Fix `scripts/a.py` (base line 5).\n- [ ] Commit: x"
        resolver = lambda ref, root: (0, owner) if ref == "D09 T01 §1" else fake_resolve(ref, root)
        good = {5: "- 5: Scheduled: D09 T01 §1 (item 2, the fix). Inspected `scripts/a.py:1`: the first finding is still true.",
                6: "- 6: Declined: one-off (a one-off script nobody runs again). Inspected `scripts/b.py`: it runs once.",
                8: "- 8: Done: D09 T01 §3 (shipped it). Inspected `scripts/d.py`: it does it now."}
        for lines, settled, says in (
                ([good[5], good[6], good[8]], True, ""),
                ([good[5], good[8]], False, "base line 6: has no classification line"),
                ([good[5].replace("§1 (", "§2 ("), good[6], good[8]], False, "base line 5: is classified as ('Scheduled', 'D09 T01 §2')"),
                # Round 1 of D00 T06 §3: a second line for one base line, a line stripped of its evidence, and a
                # scheduled line naming no item.
                ([good[5], good[6], good[8], "- 6: Scheduled: D09 T01 §1 (item 1, other). Inspected `x`: y."], False, "base line 6: is classified 2 times"),
                (["- 5: Scheduled: D09 T01 §1.", good[6], good[8]], False, "base line 5: has a classification line without its evidence"),
                ([good[5].replace("item 2, the fix", "the fix"), good[6], good[8]], False, "base line 5: is scheduled in"),
                # Round 2 of D00 T06 §3: an item number that names another finding's item.
                ([good[5].replace("item 2,", "item 1,"), good[6], good[8]], False, "base line 5: is scheduled in"),
                # Round 3 of D00 T06 §3: spaces before the kind, and a wrong item named beside the right one.
                ([good[5].replace("- 5: Scheduled", "- 5:  Scheduled").replace("item 2,", "item 1,"), good[6], good[8]], False, "base line 5: is scheduled in"),
                ([good[5].replace("item 2,", "items 1 and 2,"), good[6], good[8]], False, "base line 5: is scheduled in"),
                ([good[5].replace("item 2,", "item 7,"), good[6], good[8]], False, "base line 5: is scheduled in"),
                # Round 4: the word item repeated, so a parser reading one list would miss the second.
                ([good[5].replace("item 2,", "item 2 and item 1,"), good[6], good[8]], False, "base line 5: is scheduled in"),
                ([good[5].replace("item 2, the fix", "item 2, the fix, and item 1 too"), good[6], good[8]], False, "base line 5: is scheduled in")):
            with self.subTest(lines=lines):
                record.write_text("# Classification\n\n" + "\n".join(lines) + "\n", encoding="utf-8")
                code, out = self.checked(resolver, record=record)
                self.assertEqual(code, 0 if settled else 1, out)
                self.assertIn(says, out)

    def settle(self, first, second, fourth):
        lines = BASE.splitlines()
        lines[4] = "- ~~`scripts/a.py:1`: the first finding.~~ " + first
        lines[5] = "- ~~`scripts/b.py`: the second finding.~~ " + second
        lines[7] = "- ~~`scripts/d.py`: the fourth finding.~~ " + fourth
        self.findings.write_text("\n".join(lines) + "\n", encoding="utf-8")

    def run_check(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = ledger.check(self.base, 5, 8, root=self.root, resolver=fake_resolve)
        return code, out.getvalue()

    def test_a_settled_range_passes(self):
        self.settle("Scheduled: D09 T01 §1.", "Done: %s (the base already had it)." % self.base[:9],
                    "Declined: a one-off script nobody runs again.")
        code, out = self.run_check()
        self.assertEqual(code, 0, out)
        self.assertIn("all 3 entries", out)

    def test_each_false_settlement_fails_by_line(self):
        cases = {
            "open": None,
            "deleted": "",
            "no disposition": "Fine now.",
            "scheduled to a section that does not name the file": "Scheduled: D09 T01 §2.",
            "scheduled to a stamped section": "Scheduled: D09 T01 §3.",
            "scheduled to no section": "Scheduled: D09 T01 §9.",
            "fixed without a test": "Fixed: %s." % self.base[:9],
            "fixed by no commit": "Fixed: deadbee, test: python x.",
            "done by nothing": "Done: earlier.",
            "declined without a reason": "Declined: no.",
        }
        for name, first in cases.items():
            with self.subTest(name):
                self.settle("Scheduled: D09 T01 §1.", "Declined: a one-off script nobody runs again.", "Done: D09 T01 §3 shipped it.")
                lines = self.findings.read_text(encoding="utf-8").splitlines()
                if first is None:
                    lines[4] = "- `scripts/a.py:1`: the first finding."
                elif first == "":
                    del lines[4]
                else:
                    lines[4] = "- ~~`scripts/a.py:1`: the first finding.~~ " + first
                self.findings.write_text("\n".join(lines) + "\n", encoding="utf-8")
                code, out = self.run_check()
                self.assertEqual(code, 1, out)
                self.assertIn("base line 5:", out)
                self.assertNotIn("base line 6:", out)

    def test_an_entry_struck_twice_fails(self):
        self.settle("Scheduled: D09 T01 §1.", "Declined: a one-off script nobody runs again.", "Done: D09 T01 §3 shipped it.")
        with self.findings.open("a", encoding="utf-8") as f:
            f.write("- ~~`scripts/d.py`: the fourth finding.~~ Declined: a copy made by a careless merge here.\n")
        code, out = self.run_check()
        self.assertEqual(code, 1, out)
        self.assertIn("base line 8: struck 2 times", out)

    def test_round_1_gaps_fail(self):
        # Round 1 of D00 T06 §1: an empty test command, a bare Superseded and an entry with no backticked path passed.
        for name, disposition in {"an empty test command": "Fixed: %s, test:" % self.base[:9],
                                  "a Superseded without what replaced it": "Superseded: %s" % self.base[:9]}.items():
            with self.subTest(name):
                self.settle(disposition, "Declined: a one-off script nobody runs again.", "Done: D09 T01 §3 shipped it.")
                code, out = self.run_check()
                self.assertEqual(code, 1, out)
                self.assertIn("base line 5:", out)
        self.settle("Superseded: %s replaced the reader with a batch." % self.base[:9],
                    "Declined: a one-off script nobody runs again.", "Done: D09 T01 §3 shipped it.")
        self.assertEqual(self.run_check()[0], 0)

    def test_a_finding_without_backticks_is_owned_by_its_path_or_its_line(self):
        loose = "- The hook in scripts\\hooks\\pre-push says nothing.\n"
        self.findings.write_text(BASE + loose, encoding="utf-8")
        git(self.root, "commit", "-q", "-am", "loose")
        self.base = subprocess.run(["git", "-C", str(self.root), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
        struck = "- ~~%s~~ " % loose[2:].rstrip("\n")
        for body, ok in (("Unrelated work.", False), ("Fix scripts/hooks/pre-push so it speaks.", True)):
            with self.subTest(body):
                OPEN["D09 T01 §4"] = (0, body)
                self.findings.write_text(BASE + struck + "Scheduled: D09 T01 §4.\n", encoding="utf-8")
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    code = ledger.check(self.base, 9, 9, root=self.root, resolver=fake_resolve)
                self.assertEqual(code, 0 if ok else 1, out.getvalue())
        self.assertEqual(ledger.finding_file("- Nothing names a file here at all."), "")
        # A finding with no file is owned by a section citing its base line: exactly that number, not a longer one.
        pathless = "Nothing names a file here at all."
        for body, ok in (("Covers findings base line 19.", True), ("Covers findings base line 190.", False)):
            with self.subTest(body):
                OPEN["D09 T01 §5"] = (0, body)
                self.assertEqual(ledger.problem(pathless, " Scheduled: D09 T01 §5.", self.root, fake_resolve, 19) is None, ok)
        OPEN.pop("D09 T01 §5")
        OPEN.pop("D09 T01 §4")

    def test_an_invalid_plan_is_incomplete_not_a_traceback(self):
        # Round 2 of D00 T06 §1: a graph refusal escaped as a traceback with exit 1, the code for an unsettled finding.
        def refusing(ref, root):
            raise ledger_refused("TODO refused: unknown dependency D09 T09 §9")
        self.settle("Scheduled: D09 T01 §1.", "Declined: a one-off script nobody runs again.", "Done: D09 T01 §3 shipped it.")
        original = ledger.check
        ledger.check = lambda base, first, last, **options: original(self.base, first, last, root=self.root, resolver=refusing, **options)
        try:
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                code = ledger.main(["--base", self.base, "--lines", "5-8"])
        finally:
            ledger.check = original
        self.assertEqual(code, 2, out.getvalue())
        self.assertIn("cannot check: TODO refused", out.getvalue())

    def test_a_reversed_range_is_refused(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = ledger.check(self.base, 8, 5, root=self.root, resolver=fake_resolve)
        self.assertEqual(code, 2, out.getvalue())
        # The completion review of D00 T06 §1: a range past the file's end checked nothing and passed.
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = ledger.check(self.base, 500, 540, root=self.root, resolver=fake_resolve)
        self.assertEqual(code, 2, out.getvalue())
        self.assertIn("runs past the end", out.getvalue())

    def test_an_unreadable_base_is_incomplete(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = ledger.check("0" * 40, 5, 8, root=self.root, resolver=fake_resolve)
        self.assertEqual(code, 2, out.getvalue())


if __name__ == "__main__":
    unittest.main()
