"""Tests for scripts/build-icons.py: the frames SPEC section 18 names, each from its own master, and drift detection."""
import importlib.util
import io
from pathlib import Path
import shutil
import struct
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("build_icons", ROOT / "scripts" / "build-icons.py")
assert SPEC is not None and SPEC.loader is not None
icons = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(icons)


def frames(ico: bytes):
    """{size: png bytes} read from an ICO directory."""
    _, kind, count = struct.unpack_from("<HHH", ico, 0)
    assert kind == 1
    found = {}
    for i in range(count):
        w, _, _, _, _, _, length, offset = struct.unpack_from("<BBBBHHII", ico, 6 + 16 * i)
        found[w or 256] = ico[offset:offset + length]
    return found


class BuildIconsTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.masters = self.tmp / "icon"
        shutil.copytree(ROOT / "design" / "icon", self.masters)
        self.out = self.tmp / "bosun.ico"

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_frames_follow_spec(self):
        built = frames(icons.build(self.masters))
        self.assertEqual(sorted(built), [16, 20, 24, 32, 40, 48, 64, 256])
        from PIL import Image

        for size, png in built.items():
            self.assertEqual(Image.open(io.BytesIO(png)).size, (size, size))
        for size in (16, 24, 32):
            self.assertEqual(built[size], icons.render(self.masters / f"bosun-{size}.svg", size))
        self.assertEqual(built[20], icons.render(self.masters / "bosun-16.svg", 20))
        self.assertNotEqual(built[16], icons.render(self.masters / "bosun-256.svg", 16))

    def test_reproducible(self):
        self.assertEqual(icons.build(self.masters), icons.build(self.masters))

    def test_committed_ico_matches_masters(self):
        self.assertEqual(icons.main(["--check"]), 0)

    def test_check_fails_after_master_edit(self):
        self.assertEqual(icons.main(["--masters", str(self.masters), "--out", str(self.out)]), 0)
        self.assertEqual(icons.main(["--check", "--masters", str(self.masters), "--out", str(self.out)]), 0)
        master = self.masters / "bosun-24.svg"
        master.write_text(master.read_text(encoding="utf-8").replace("#35D6A6", "#35D6A7"), encoding="utf-8")
        self.assertEqual(icons.main(["--check", "--masters", str(self.masters), "--out", str(self.out)]), 1)


if __name__ == "__main__":
    unittest.main()
