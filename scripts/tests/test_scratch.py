"""Tests scripts/lib/scratch.py (D00 T07 §3): BOSUN_SCRATCH resolution, caller root override, fallback, and errors."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "lib"))
import scratch


class ScratchHelperTests(unittest.TestCase):
    def setUp(self) -> None:
        self.base = scratch.scratch_dir("scratch-unit")
        self.addCleanup(shutil.rmtree, self.base, True)

    def test_existing_bosun_scratch_is_honoured_and_unique(self) -> None:
        fake = self.base / "fake-scratch"
        fake.mkdir()
        with mock.patch.dict(os.environ, {"BOSUN_SCRATCH": str(fake)}):
            first = scratch.scratch_dir("alpha")
            second = scratch.scratch_dir("alpha")
            self.addCleanup(shutil.rmtree, first, True)
            self.addCleanup(shutil.rmtree, second, True)
            self.assertEqual(first.parent.resolve(), fake.resolve())
            self.assertTrue(first.name.startswith("bosun-alpha-"))
            self.assertNotEqual(first, second)
            with scratch.scratch_tempdir("beta") as td:
                td_path = Path(td)
                self.assertEqual(td_path.parent.resolve(), fake.resolve())
                self.assertTrue(td_path.name.startswith("bosun-beta-"))
            self.assertFalse(td_path.exists())

    def test_unset_empty_missing_and_file_fall_back_to_system_temp(self) -> None:
        sys_temp = Path(tempfile.gettempdir()).resolve()
        plain = self.base / "not-a-dir.txt"
        plain.write_text("x", encoding="utf-8")
        cases: list[dict[str, str]] = [
            {},
            {"BOSUN_SCRATCH": ""},
            {"BOSUN_SCRATCH": "   "},
            {"BOSUN_SCRATCH": str(self.base / "missing-dir")},
            {"BOSUN_SCRATCH": str(plain)},
        ]
        for patch in cases:
            with self.subTest(patch=patch), mock.patch.dict(os.environ, {}, clear=False):
                os.environ.pop("BOSUN_SCRATCH", None)
                os.environ.update(patch)
                created = scratch.scratch_dir("fallback")
                self.addCleanup(shutil.rmtree, created, True)
                self.assertEqual(created.parent.resolve(), sys_temp)
                self.assertEqual(scratch.scratch_root().resolve(), sys_temp)

    def test_root_override_beats_bosun_scratch_and_can_create_missing_root(self) -> None:
        general = self.base / "general"
        general.mkdir()
        campaign = self.base / "nested" / "campaign-root"
        with mock.patch.dict(os.environ, {"BOSUN_SCRATCH": str(general)}):
            with self.assertRaises(OSError):
                scratch.scratch_dir("e2e", root=campaign, create_root=False)
            self.assertFalse(campaign.exists())
            created = scratch.scratch_dir("e2e", root=campaign, create_root=True)
            self.addCleanup(shutil.rmtree, created, True)
            self.assertTrue(campaign.is_dir())
            self.assertEqual(created.parent.resolve(), campaign.resolve())
            self.assertNotEqual(created.parent.resolve(), general.resolve())

    def test_unsafe_names_are_refused(self) -> None:
        for bad in ("", ".", "..", "../escape", "a/b", "a\\b", "bad name"):
            with self.subTest(bad=bad):
                with self.assertRaises(ValueError):
                    scratch.scratch_dir(bad, root=self.base)
                with self.assertRaises(ValueError):
                    scratch.scratch_tempdir(bad, root=self.base)

    def test_child_creation_failure_names_selected_root_and_leaves_system_temp_untouched(self) -> None:
        selected = self.base / "selected-root"
        selected.mkdir()
        sys_temp = Path(tempfile.gettempdir())
        before = set(sys_temp.glob("bosun-failcase-*"))
        with mock.patch.dict(os.environ, {"BOSUN_SCRATCH": str(selected)}):
            with mock.patch.object(tempfile, "mkdtemp", side_effect=PermissionError("denied")):
                with self.assertRaises(OSError) as ctx:
                    scratch.scratch_dir("failcase")
                self.assertIn(str(selected.resolve()), str(ctx.exception))
            with mock.patch.object(tempfile, "TemporaryDirectory", side_effect=PermissionError("denied")):
                with self.assertRaises(OSError) as ctx:
                    scratch.scratch_tempdir("failcase")
                self.assertIn(str(selected.resolve()), str(ctx.exception))
        after = set(sys_temp.glob("bosun-failcase-*"))
        self.assertEqual(after - before, set())


if __name__ == "__main__":
    unittest.main()
