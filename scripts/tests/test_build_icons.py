"""Tests for scripts/build-icons.py: the frames SPEC section 18 names, each from its own master, and drift detection."""
import importlib.util
import io
from pathlib import Path
import shutil
import struct
import tempfile
import unittest
from unittest import mock

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


class BadgedIconTest(unittest.TestCase):
    """A campaign launcher's icon (scripts/start-run.ps1 -InstallShortcut): the app icon with its runner's badge."""

    MARK = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect x="1" y="1" width="8" height="8" fill="#123456"/></svg>'

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.mark = self.tmp / "mark.svg"
        self.mark.write_text(self.MARK, encoding="utf-8")
        self.masters = ROOT / "design" / "icon"

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    @staticmethod
    def image(png):
        from PIL import Image

        return Image.open(io.BytesIO(png)).convert("RGBA")

    def test_the_badge_sits_bottom_right_on_every_frame_and_leaves_the_rest_alone(self):
        plain = frames(icons.build(self.masters))
        badged = frames(icons.build(self.masters, "#D97757"))
        self.assertEqual(sorted(badged), sorted(plain))
        for size in badged:
            before, after = self.image(plain[size]), self.image(badged[size])
            self.assertEqual(after.size, (size, size))
            half = size // 2
            self.assertEqual(after.crop((0, 0, half, half)).tobytes(), before.crop((0, 0, half, half)).tobytes(),
                             f"the badge reached the top-left quarter at {size} px")
            self.assertNotEqual(after.crop((half, half, size, size)).tobytes(), before.crop((half, half, size, size)).tobytes(),
                                f"no badge was drawn at {size} px")
        # The badge is its runner's colour at its centre, inside a white outline.
        big = self.image(badged[256])
        centre = round(256 - 256 * 0.24 - 256 * 0.01)
        self.assertEqual(big.getpixel((centre, centre)), (0xD9, 0x77, 0x57, 255))
        self.assertEqual(big.getpixel((centre, round(centre - 256 * 0.24 + 2))), (255, 255, 255, 255))

    def test_two_runners_get_different_icons(self):
        self.assertNotEqual(icons.build(self.masters, "#D97757"), icons.build(self.masters, "#4C8DF6"))

    def test_a_mark_is_drawn_white_from_32_px_up_and_not_below(self):
        bare = frames(icons.build(self.masters, "#D97757"))
        marked = frames(icons.build(self.masters, "#D97757", self.mark))
        for size in (16, 20, 24):
            self.assertEqual(marked[size], bare[size], f"a mark was drawn at {size} px")
        for size in (32, 40, 48, 64, 256):
            self.assertNotEqual(marked[size], bare[size], f"no mark was drawn at {size} px")
        centre = round(256 - 256 * 0.24 - 256 * 0.01)
        self.assertEqual(self.image(marked[256]).getpixel((centre, centre)), (255, 255, 255, 255), "the mark kept its own colour")

    def test_the_cli_writes_outside_the_repository_only_and_a_missing_mark_leaves_the_colour(self):
        out = self.tmp / "launcher.ico"
        self.assertEqual(icons.main(["--out", str(out), "--badge-fill", "#4C8DF6", "--badge-mark", str(self.tmp / "no-such.svg")]), 0)
        self.assertEqual(out.read_bytes(), icons.build(self.masters, "#4C8DF6"))
        self.assertEqual(list(self.tmp.glob("*.tmp")), [])
        for bad in (["--badge-fill", "#4C8DF6"],  # the default --out is the committed icon
                    ["--out", str(ROOT / "build" / "x.ico"), "--badge-fill", "#4C8DF6"],
                    ["--out", str(out), "--badge-fill", "blue"],
                    ["--out", str(out), "--badge-mark", str(self.mark)]):
            with self.subTest(bad=bad), self.assertRaises(SystemExit):
                icons.main(bad)
        self.assertEqual(icons.main(["--check"]), 0, "the committed icon was touched")

    def test_a_mark_that_cant_be_drawn_is_refused_and_nothing_is_written(self):
        out = self.tmp / "launcher.ico"
        out.write_bytes(b"the icon from before")
        for name, text in (("truncated.svg", "<svg this is truncated"), ("empty.svg", '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"/>')):
            bad = self.tmp / name
            bad.write_text(text, encoding="utf-8")
            with self.subTest(mark=name):
                self.assertEqual(icons.main(["--out", str(out), "--badge-fill", "#D97757", "--badge-mark", str(bad)]), 1)
                self.assertEqual(out.read_bytes(), b"the icon from before")
        self.assertEqual(icons.main(["--out", str(out), "--badge-fill", "#D97757", "--badge-mark", str(self.mark)]), 0, "a good mark was refused")

    def test_a_failed_replace_keeps_the_old_icon_and_leaves_no_temporary_file(self):
        out = self.tmp / "launcher.ico"
        out.write_bytes(b"the icon from before")

        def refused(src, dst):
            raise PermissionError(13, "the icon is open in another process", str(dst))

        with mock.patch.object(icons.os, "replace", refused), self.assertRaises(OSError):
            icons.main(["--out", str(out), "--badge-fill", "#4C8DF6"])
        self.assertEqual(out.read_bytes(), b"the icon from before")
        self.assertEqual(sorted(p.name for p in self.tmp.iterdir() if p.suffix != ".svg"), ["launcher.ico"])


if __name__ == "__main__":
    unittest.main()
