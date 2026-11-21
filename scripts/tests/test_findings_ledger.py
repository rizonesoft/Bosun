"""Tests for scripts/findings-ledger.py: a settled range passes, and each way a finding can look settled without being
so fails by its base line (D00 T06 §1)."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("findings_ledger", ROOT / "scripts" / "findings-ledger.py")
assert SPEC is not None and SPEC.loader is not None
ledger = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ledger)

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

    def test_an_unreadable_base_is_incomplete(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = ledger.check("0" * 40, 5, 8, root=self.root, resolver=fake_resolve)
        self.assertEqual(code, 2, out.getvalue())


if __name__ == "__main__":
    unittest.main()
