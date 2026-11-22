"""Tests for scripts/build-icons.py: the frames SPEC section 18 names, each from its own master, and drift detection."""
import contextlib
import importlib.util
import io
import json
import os
import sys
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
        self.assertEqual(self.off_spec(built), [], "every frame is drawn from the master SPEC section 18 names")
        # And each master really gives a different picture at these sizes, so the frames can't match by accident.
        self.assertNotEqual(built[16], icons.render(self.masters / "bosun-256.svg", 16))
        self.assertNotEqual(built[20], icons.render(self.masters / "bosun-256.svg", 20))
        for size in (40, 48, 64):
            self.assertNotEqual(built[size], icons.render(self.masters / "bosun-32.svg", size))

    def off_spec(self, built):
        """The frame sizes not drawn from the master SPEC section 18 names. The table is written out here on its own,
        not read from the generator's: 16, 24 and 32 px from their own pixel-grid masters, 20 px from the 16 px file,
        and 40 px and above from the 256 master."""
        spec = {16: 16, 20: 16, 24: 24, 32: 32, 40: 256, 48: 256, 64: 256, 256: 256}
        return [size for size, master in spec.items() if built[size] != icons.render(self.masters / f"bosun-{master}.svg", size)]

    def test_a_frame_from_the_wrong_master_is_off_spec(self):
        # The plan review's mutation, kept as a test: 40, 48 and 64 px drawn from the 32 px master, or 20 px from the 256
        # one, are frames SPEC section 18 does not allow, and the check names exactly those.
        for wrong, named in (({40: 32, 48: 32, 64: 32}, [40, 48, 64]), ({20: 256}, [20]), ({256: 32}, [256])):
            with self.subTest(wrong=wrong), mock.patch.dict(icons.FRAMES, wrong):
                self.assertEqual(self.off_spec(frames(icons.build(self.masters))), named)

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

    def test_the_repair_a_failed_check_names_mends_what_was_checked(self):
        # Completion review: the message named the default command, which rebuilds the repository's icon, not this one.
        import contextlib
        import shlex

        spaced = self.tmp / "my icons" / "bosun.ico"
        spaced.parent.mkdir()
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            self.assertEqual(icons.main(["--check", "--masters", str(self.masters), "--out", str(spaced)]), 1)  # not built yet
        said = err.getvalue()
        repair = said.split(": run ", 1)[1].strip()
        self.assertIn(f'--out "{spaced}"', repair)  # quoted, for its space
        self.assertIn(f"--masters {self.masters}", repair)
        # Followed as written, it builds the file that was checked.
        words = shlex.split(repair, posix=False)
        self.assertEqual(words[:2], ["python", "scripts/build-icons.py"])
        self.assertEqual(icons.main([w.strip('"') for w in words[2:]]), 0)
        self.assertEqual(icons.main(["--check", "--masters", str(self.masters), "--out", str(spaced)]), 0)

    def test_the_default_check_names_the_default_repair(self):
        import contextlib

        err = io.StringIO()
        with contextlib.redirect_stderr(err), mock.patch.object(icons, "build", return_value=b"not the committed icon"):
            self.assertEqual(icons.main(["--check"]), 1)
        self.assertTrue(err.getvalue().strip().endswith(": run python scripts/build-icons.py"))


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
        with self.assertRaises(SystemExit):  # a mark with no fill is a usage error, not a badge that failed
            icons.main(["--out", str(out), "--badge-mark", str(self.mark)])
        for bad in (["--badge-fill", "#4C8DF6"],  # the default --out is the committed icon
                    ["--out", str(ROOT / "build" / "x.ico"), "--badge-fill", "#4C8DF6"]):
            with self.subTest(bad=bad):  # code 4 in one line, as every badge refusal (round 2 of D00 T03 §31)
                err = io.StringIO()
                with contextlib.redirect_stderr(err):
                    code = icons.main(bad)
                lines = [line for line in err.getvalue().splitlines() if line.strip()]
                self.assertEqual((code, len(lines)), (4, 1), lines)
                self.assertIn("give --out a path outside the repository", lines[0])
        # A fill that is not a colour, empty included, is the badge path's code 4 in one line, and never reaches the
        # app icon's build (round 1 of D00 T03 §31).
        out.write_bytes(b"the icon from before")
        for fill in ("blue", ""):
            with self.subTest(fill=fill), mock.patch.object(icons, "write_atomic", side_effect=AssertionError("something was written")):
                err = io.StringIO()
                with contextlib.redirect_stderr(err):
                    code = icons.main(["--out", str(out), "--badge-fill", fill])
                lines = [line for line in err.getvalue().splitlines() if line.strip()]
                self.assertEqual((code, len(lines)), (4, 1), lines)
                self.assertIn("--badge-fill must be a colour like #D97757", lines[0])
        self.assertEqual(out.read_bytes(), b"the icon from before")
        self.assertEqual(icons.main(["--check"]), 0, "the committed icon was touched")

    def test_a_mark_that_cant_be_drawn_is_refused_and_nothing_is_written(self):
        out = self.tmp / "launcher.ico"
        out.write_bytes(b"the icon from before")
        for name, text in (("truncated.svg", "<svg this is truncated"), ("empty.svg", '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"/>')):
            bad = self.tmp / name
            bad.write_text(text, encoding="utf-8")
            with self.subTest(mark=name):
                self.assertEqual(icons.main(["--out", str(out), "--badge-fill", "#D97757", "--badge-mark", str(bad)]), 4)  # not valid
                self.assertEqual(out.read_bytes(), b"the icon from before")
        self.assertEqual(icons.main(["--out", str(out), "--badge-fill", "#D97757", "--badge-mark", str(self.mark)]), 0, "a good mark was refused")

    def broken_masters(self) -> Path:
        """A copy of the masters with one truncated, for the renderer's own parse error (round 1 of D00 T03 §31)."""
        folder = self.tmp / "broken-masters"
        if not folder.exists():
            shutil.copytree(self.masters, folder)
            first = sorted(folder.glob("*.svg"))[0]
            first.write_text("<svg xmlns='http://www.w3.org/2000/svg'><g>", encoding="utf-8")
        return folder

    def denied_master(self):
        """Path.read_text refusing the first master, as a file another process holds exclusively (round 4 of §31)."""
        real_read = Path.read_text
        first = sorted(self.masters.glob("*.svg"))[0]
        def read(path, *args, **kwargs):
            if Path(path) == first:
                raise PermissionError(13, "The process cannot access the file because it is being used by another process", str(path))
            return real_read(path, *args, **kwargs)
        return read

    def badge_run(self, *extra, out=None):
        """main on the badge path: its exit code, its stderr lines and its stdout."""
        out = out or self.tmp / "launcher.ico"
        err, said = io.StringIO(), io.StringIO()
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(said):
            code = icons.main(["--out", str(out), "--badge-fill", "#D97757", *extra])
        return code, [line for line in err.getvalue().splitlines() if line.strip()], said.getvalue()

    def test_each_way_a_badge_fails_has_its_own_code_one_line_and_keeps_the_old_icon(self):
        # D00 T03 §31: one exit code and one fix per cause (FAILURES), never a traceback, the old icon kept.
        out = self.tmp / "launcher.ico"
        bad = self.tmp / "bad.svg"
        bad.write_text("<svg this is truncated", encoding="utf-8")
        held = self.tmp / "held.ico"
        held.write_bytes(b"the icon from before")
        empty = self.tmp / "no-masters"
        empty.mkdir()
        real_read = Path.read_text
        def denied(path, *args, **kwargs):
            if path == self.mark:
                raise PermissionError(13, "Access is denied", str(path))
            return real_read(path, *args, **kwargs)
        def renderer_breaks(master, size):
            raise RuntimeError("the renderer could not lay out the master")
        cases = (
            ("resvg-py missing", 2, mock.patch.dict(sys.modules, {"resvg_py": None}), ("--badge-mark", str(self.mark)), out, "pip install -r scripts/requirements-icons.txt"),
            ("Pillow missing", 2, mock.patch.dict(sys.modules, {"PIL": None, "PIL.Image": None}), ("--badge-mark", str(self.mark)), out, "pip install -r scripts/requirements-icons.txt"),
            ("a mark denied", 3, mock.patch.object(Path, "read_text", denied), ("--badge-mark", str(self.mark)), out, "must exist and be readable"),
            ("a mark malformed", 4, contextlib.nullcontext(), ("--badge-mark", str(bad)), out, "mend or remove it"),
            ("a master missing", 3, contextlib.nullcontext(), ("--masters", str(empty)), out, "must exist and be readable"),
            ("the renderer failing", 5, mock.patch.object(icons, "render", renderer_breaks), (), out, "report it"),
            ("a master malformed", 4, contextlib.nullcontext(), ("--masters", str(self.broken_masters())), out, "repair its SVG"),
            ("a master held or denied", 3, mock.patch.object(Path, "read_text", self.denied_master()), (), out, "must exist and be readable"),
        )
        for name, code, context, extra, target, fix in cases:
            with self.subTest(name):
                target.write_bytes(b"the icon from before")
                with context:
                    got, lines, said = self.badge_run(*extra, out=target)
                self.assertEqual(got, code, lines)
                self.assertEqual(len(lines), 1, lines)
                self.assertIn(fix, lines[0])
                self.assertTrue(lines[0].startswith(icons.FAILURES[code] + ": "), lines[0])  # led by its kind from the map
                self.assertNotIn("pip install", lines[0]) if code != 2 else None
                self.assertEqual(target.read_bytes(), b"the icon from before")
        if os.name == "nt":  # an existing ICO the replace cannot take, for real: only Windows refuses a read-only target
            held.chmod(0o444)
            try:
                got, lines, said = self.badge_run(out=held)
            finally:
                held.chmod(0o644)
            self.assertEqual((got, len(lines)), (6, 1), lines)
            self.assertIn("make it writable or choose another -IconDir", lines[0])
            self.assertEqual(held.read_bytes(), b"the icon from before")
        # Each cause of a write that fails gets its own remedy (round 3 of D00 T03 §31).
        causes = [(OSError(28, "No space left on device"), "the drive is full"),
                  (PermissionError(13, "Permission denied"), "make it writable or choose another -IconDir")]
        if os.name == "nt":
            causes.append((OSError(None, "The process cannot access the file", str(out), 32), "another program holds"))
        for error, remedy in causes:
            with self.subTest(remedy=remedy):
                out.write_bytes(b"the icon from before")
                with mock.patch.object(icons.os, "replace", side_effect=error):
                    got, lines, said = self.badge_run(out=out)
                self.assertEqual((got, len(lines)), (6, 1), lines)
                self.assertIn(remedy, lines[0])
                self.assertEqual(out.read_bytes(), b"the icon from before")

    def test_a_failed_replace_keeps_the_old_icon_and_leaves_no_temporary_file(self):
        out = self.tmp / "launcher.ico"
        out.write_bytes(b"the icon from before")

        def refused(src, dst):
            raise PermissionError(13, "the icon is open in another process", str(dst))

        with mock.patch.object(icons.os, "replace", refused):
            self.assertEqual(icons.main(["--out", str(out), "--badge-fill", "#4C8DF6"]), 6)  # the output, said in one line
        self.assertEqual(out.read_bytes(), b"the icon from before")
        self.assertEqual(sorted(p.name for p in self.tmp.iterdir() if p.suffix != ".svg"), ["launcher.ico"])


TRAY = ("tray-normal.ico", "tray-pressure.ico", "tray-needs.ico")


def rgb(colour: str):
    digits = colour.lstrip("#")[-6:]
    return tuple(int(digits[i:i + 2], 16) for i in (0, 2, 4))


def near(a, b, within=24):
    return all(abs(x - y) <= within for x, y in zip(a[:3], b[:3]))


def luminance(colour):
    def linear(c):
        c = c / 255
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    r, g, b = colour[:3]
    return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b)


def contrast(a, b):
    x, y = luminance(a), luminance(b)
    return (max(x, y) + 0.05) / (min(x, y) + 0.05)


class TrayIconsTest(unittest.TestCase):
    """The tray's three state icons (D02 T01 §3, design/SPEC.md §17): Normal plain, Pressure a triangle, Needs you a
    disc, in the theme's colours, built and checked with the app icon as one set."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.masters = self.tmp / "icon"
        shutil.copytree(ROOT / "design" / "icon", self.masters)
        self.tokens = self.tmp / "colors.json"
        shutil.copyfile(ROOT / "design" / "tokens" / "colors.json", self.tokens)
        self.out = self.tmp / "out"
        self.out.mkdir()
        self.dark = json.loads(self.tokens.read_text(encoding="utf-8"))["dark"]

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def args(self, *extra):
        return ["--masters", str(self.masters), "--out", str(self.out / "bosun.ico"), "--tray-out", str(self.out),
                "--tokens", str(self.tokens), *extra]

    def run_icons(self, *extra):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = icons.main(self.args(*extra))
        return code, err.getvalue()

    def stale(self, *extra):
        """The file names a failed --check names, in the order printed."""
        code, err = self.run_icons("--check", *extra)
        return code, [Path(line.split(" is out of date")[0]).name for line in err.splitlines() if " is out of date" in line]

    def files(self):
        return {p.name: p.read_bytes() for p in sorted(self.out.iterdir())}

    def set_token(self, name, value):
        tokens = json.loads(self.tokens.read_text(encoding="utf-8"))
        tokens["dark"][name] = value
        self.tokens.write_text(json.dumps(tokens), encoding="utf-8")

    def image(self, name, size):
        from PIL import Image

        return Image.open(io.BytesIO(frames((self.out / name).read_bytes())[size])).convert("RGBA")

    def test_each_state_has_the_four_tray_sizes_and_normal_is_the_plain_icon(self):
        self.assertEqual(self.run_icons()[0], 0)
        from PIL import Image

        # SPEC section 18's small sizes, written out here: 16 and 20 px from the 16 px master, 24 and 32 from their own.
        spec = {16: 16, 20: 16, 24: 24, 32: 32}
        for name in TRAY:
            built = frames((self.out / name).read_bytes())
            self.assertEqual(sorted(built), [16, 20, 24, 32], name)
            for size, png in built.items():
                self.assertEqual(Image.open(io.BytesIO(png)).size, (size, size))
        normal = frames((self.out / "tray-normal.ico").read_bytes())
        for size, master in spec.items():
            self.assertEqual(normal[size], icons.render(self.masters / f"bosun-{master}.svg", size), f"{size} px")

    def test_the_states_differ_in_greyscale_at_every_size(self):
        self.run_icons()
        for size in (16, 20, 24, 32):
            grey = [self.image(name, size).convert("L").tobytes() for name in TRAY]
            self.assertEqual(len(set(grey)), 3, f"the three states are three pictures at {size} px without colour")

    def overlay(self, name, size, token):
        """(the pixels the state changes from Normal, the pixels of its fill colour), each a set of (x, y)."""
        normal, state = self.image("tray-normal.ico", size), self.image(name, size)
        changed = {(x, y) for x in range(size) for y in range(size) if state.getpixel((x, y)) != normal.getpixel((x, y))}
        fill = {p for p in changed if near(state.getpixel(p), rgb(self.dark[token]))}
        return changed, fill

    def test_pressure_is_a_triangle_and_needs_you_a_disc_bottom_right_with_a_rim(self):
        self.run_icons()
        size = 32
        box = round(size * 0.66)  # the overlay's box: two thirds of the frame, flush in the corner
        for name, token in (("tray-pressure.ico", "Warn"), ("tray-needs.ico", "Info")):
            with self.subTest(state=name):
                changed, fill = self.overlay(name, size, token)
                self.assertTrue(fill, "the overlay is drawn in its token's colour")
                self.assertTrue(all(x >= size - box and y >= size - box for x, y in changed), "bottom right, inside its box")
                xs, ys = [x for x, _ in fill], [y for _, y in fill]
                left, right, top, bottom = min(xs), max(xs), min(ys), max(ys)
                width, height = right - left + 1, bottom - top + 1
                cover = len(fill) / (width * height)
                rows = [sum(1 for x, y in fill if y == row) for row in range(top, bottom + 1)]
                image = self.image(name, size)
                centre, radius, rim = icons.overlay_box(size)
                if token == "Info":
                    self.assertTrue(0.70 <= cover <= 0.85, f"a disc fills about pi/4 of its box, not {cover:.2f}")
                    mirrored = {(left + right - x, y) for x, y in fill}
                    flipped = {(x, top + bottom - y) for x, y in fill}
                    self.assertLessEqual(len(fill ^ mirrored) / len(fill), 0.05, "symmetric left to right")
                    self.assertLessEqual(len(fill ^ flipped) / len(fill), 0.05, "symmetric top to bottom")
                    edge = image.getpixel((int(centre + radius - rim / 2), int(centre)))
                else:
                    self.assertTrue(0.40 <= cover <= 0.60, f"a triangle fills about half its box, not {cover:.2f}")
                    self.assertLessEqual(rows[0], 3, "it comes to a point at the top")
                    self.assertGreaterEqual(rows[-1], 0.8 * width, "and is widest at its base")
                    self.assertTrue(all(b >= a - 1 for a, b in zip(rows, rows[1:])), "widening all the way down")
                    edge = image.getpixel((int(centre), int(centre + radius - rim / 2)))
                middle = image.getpixel((int(centre), int(centre + radius / 3)))
                self.assertTrue(near(middle, rgb(self.dark[token]), 2), f"its middle is {token}, not {middle}")
                self.assertTrue(near(edge, rgb(self.dark["Base"]), 2), f"its rim is Base, not {edge}")
                # The shape's own edge reads on any taskbar: its fill against its rim is at least 3:1.
                self.assertGreaterEqual(contrast(rgb(self.dark[token]), rgb(self.dark["Base"])), 3)

    def test_an_overlay_takes_its_colour_from_its_own_token_and_no_other(self):
        self.run_icons()
        before = self.files()
        for token, changes in (("Warn", {"tray-pressure.ico"}), ("Info", {"tray-needs.ico"}), ("Accent", set())):
            with self.subTest(token=token):
                shutil.copyfile(ROOT / "design" / "tokens" / "colors.json", self.tokens)
                self.set_token(token, "#123456")
                self.assertEqual(self.run_icons()[0], 0)
                after = self.files()
                self.assertEqual({name for name in after if after[name] != before[name]}, changes)

    def test_a_colour_with_alpha_is_drawn_at_that_strength_not_misread(self):
        # The tokens write #AARRGGBB; SVG would read the same digits as #RRGGBBAA, a different colour altogether.
        self.set_token("Warn", "#80FFB547")
        self.run_icons()
        centre, radius, _ = icons.overlay_box(32)
        at = (int(centre), int(centre + radius / 3))
        under = self.image("tray-normal.ico", 32).getpixel(at)
        drawn = self.image("tray-pressure.ico", 32).getpixel(at)
        half = tuple(round(0x80 / 255 * c + (1 - 0x80 / 255) * u) for c, u in zip((0xFF, 0xB5, 0x47), under[:3]))
        self.assertTrue(near(drawn, half, 3), f"half-strength amber over the icon is {half}, drawn {drawn[:3]}")
        self.assertFalse(near(drawn, (0x80, 0xFF, 0xB5), 40), "not the colour SVG reads from the same eight digits")
        self.assertEqual(icons.paint("#80FFB547"), 'fill="#FFB547" fill-opacity="0.5020"')
        self.assertEqual(icons.paint("#FFB547"), 'fill="#FFB547"')

    def test_bad_tokens_are_refused_and_nothing_is_written(self):
        self.run_icons()
        before = self.files()
        good = self.tokens.read_text(encoding="utf-8")

        def with_dark(change):
            tokens = json.loads(good)
            change(tokens["dark"])
            return json.dumps(tokens)

        cases = [("{ not json", "is not JSON"), ("[]", "dark is missing"), (json.dumps({"dark": []}), "dark is missing"),
                 (with_dark(lambda d: d.pop("Info")), "dark.Info"), (with_dark(lambda d: d.pop("Base")), "dark.Base"),
                 (with_dark(lambda d: d.update(Warn="amber")), "dark.Warn"), (with_dark(lambda d: d.update(Warn="#12345")), "dark.Warn"),
                 (with_dark(lambda d: d.update(Warn="#FFB547\n")), "dark.Warn"), (with_dark(lambda d: d.update(Info=5)), "dark.Info")]
        for text, names in cases:
            with self.subTest(names=names, text=text[:30]):
                self.tokens.write_text(text, encoding="utf-8")
                for extra in ((), ("--check",)):
                    code, err = self.run_icons(*extra)
                    self.assertEqual(code, 2)
                    self.assertIn(names, err)
                    self.assertIn("nothing was written", err)
                self.assertEqual(self.files(), before)
        self.tokens.unlink()
        code, err = self.run_icons()
        self.assertEqual(code, 2)
        self.assertIn("can't be read", err)
        self.assertEqual(self.files(), before)

    def test_check_names_exactly_the_files_that_are_out_of_date(self):
        self.assertEqual(self.stale(), (1, ["bosun.ico", *TRAY]))  # nothing built yet
        self.assertEqual(self.run_icons()[0], 0)
        self.assertEqual(self.stale(), (0, []))
        pristine_tokens = self.tokens.read_text(encoding="utf-8")

        def rebuilt():
            self.assertEqual(self.run_icons()[0], 0)
            self.assertEqual(self.stale(), (0, []))

        pressure = self.out / "tray-pressure.ico"
        pressure.write_bytes(pressure.read_bytes()[:-1] + b"\x00")  # a hand edit
        self.assertEqual(self.stale(), (1, ["tray-pressure.ico"]))
        rebuilt()
        self.set_token("Warn", "#FFB548")
        self.assertEqual(self.stale(), (1, ["tray-pressure.ico"]))
        rebuilt()
        self.set_token("Info", "#5AA2FF")
        self.assertEqual(self.stale(), (1, ["tray-needs.ico"]))
        rebuilt()
        self.set_token("Accent", "#123456")  # a token no tray icon uses
        self.assertEqual(self.stale(), (0, []))
        self.tokens.write_text(pristine_tokens, encoding="utf-8")
        rebuilt()
        for master, named in (("bosun-256.svg", ["bosun.ico"]), ("bosun-16.svg", ["bosun.ico", *TRAY])):
            with self.subTest(master=master):
                file = self.masters / master
                file.write_text(file.read_text(encoding="utf-8").replace("#35D6A6", "#35D6A7"), encoding="utf-8")
                self.assertEqual(self.stale(), (1, named))
                rebuilt()

    def test_the_repair_a_failed_check_names_rebuilds_the_same_set(self):
        code, err = self.run_icons("--check")
        repair = err.splitlines()[0].split(": run ", 1)[1]
        for flag in ("--masters", "--out", "--tokens", "--tray-out"):
            self.assertIn(flag, repair)

    def test_an_app_icon_named_as_a_tray_icon_is_refused_before_anything_is_written(self):
        # Round 1: --out naming tray-normal.ico in the tray folder wrote the tray icon over the app icon, exit 0.
        for name in TRAY:
            for extra in ((), ("--check",)):
                with self.subTest(name=name, extra=extra), contextlib.redirect_stderr(io.StringIO()) as err:
                    with self.assertRaises(SystemExit) as refused:
                        icons.main(["--masters", str(self.masters), "--out", str(self.out / name), "--tray-out", str(self.out),
                                    "--tokens", str(self.tokens), *extra])
                    self.assertEqual(refused.exception.code, 2)
                    self.assertIn("the name of a tray icon", err.getvalue())
        self.assertEqual(self.files(), {})
        # Seen through another spelling of the same path too.
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            icons.main(["--masters", str(self.masters), "--out", str(self.out / "sub" / ".." / "TRAY-NEEDS.ICO"),
                        "--tray-out", str(self.out), "--tokens", str(self.tokens)])
        self.assertEqual(self.files(), {})

    def test_with_out_alone_only_the_app_icon_is_built_as_before(self):
        alone = self.tmp / "alone" / "bosun.ico"
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(icons.main(["--masters", str(self.masters), "--out", str(alone)]), 0)
            self.assertEqual(icons.main(["--check", "--masters", str(self.masters), "--out", str(alone)]), 0)
        self.assertEqual([p.name for p in alone.parent.iterdir()], ["bosun.ico"])

    def test_a_file_that_cant_be_staged_changes_nothing(self):
        self.run_icons()
        before = self.files()
        self.set_token("Warn", "#FFB548")
        self.set_token("Info", "#5AA2FF")
        real, calls = icons.tempfile.mkstemp, []

        def full_on_third(*args, **kwargs):
            calls.append(1)
            if len(calls) == 3:
                raise OSError(28, "No space left on device")
            return real(*args, **kwargs)

        with mock.patch.object(icons.tempfile, "mkstemp", full_on_third):
            code, err = self.run_icons()
        self.assertEqual(code, 3)
        self.assertIn("nothing was changed", err)
        self.assertEqual(self.files(), before)  # the old icons, and no temporary file beside them
        self.assertEqual(self.stale(), (1, ["tray-pressure.ico", "tray-needs.ico"]))
        self.assertEqual(self.run_icons()[0], 0)
        self.assertEqual(self.stale(), (0, []))

    def test_an_icon_held_open_is_left_as_it_was_and_the_report_says_what_changed(self):
        self.run_icons()
        before = self.files()
        self.set_token("Warn", "#FFB548")
        self.set_token("Info", "#5AA2FF")
        real = icons.os.replace

        def pressure_is_held(src, dst):
            if str(dst).endswith("tray-pressure.ico"):
                raise PermissionError(13, "the icon is open in another process", str(dst))
            return real(src, dst)

        with mock.patch.object(icons.os, "replace", pressure_is_held):
            code, err = self.run_icons()
        self.assertEqual(code, 3)
        after = self.files()
        self.assertEqual(sorted(after), sorted(before))  # no temporary file left
        self.assertEqual(after["tray-pressure.ico"], before["tray-pressure.ico"])
        self.assertEqual(after["tray-needs.ico"], before["tray-needs.ico"], "the files after the held one are left too")
        self.assertRegex(err, r"can't replace .*tray-pressure\.ico")
        self.assertRegex(err, r"Updated: .*bosun\.ico.*tray-normal\.ico\. Left as they were: .*tray-pressure\.ico.*tray-needs\.ico")
        self.assertEqual(self.stale(), (1, ["tray-pressure.ico", "tray-needs.ico"]))
        self.assertEqual(self.run_icons()[0], 0)  # released: the retry completes the set
        self.assertEqual(self.stale(), (0, []))


if __name__ == "__main__":
    unittest.main()
