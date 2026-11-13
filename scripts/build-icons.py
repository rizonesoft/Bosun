#!/usr/bin/env python3
"""Builds the app icon `resources/bosun.ico` and the tray's state icons from the SVG masters in `design/icon/`.

    python scripts/build-icons.py            regenerate resources/bosun.ico and the three resources/tray-*.ico
    python scripts/build-icons.py --check    exit 1 naming each of the four that differs from what the masters give

The frames follow `design/SPEC.md` section 18: 16, 20, 24, 32, 40, 48, 64 and 256 px. 16, 24 and 32 px come from
their own pixel-grid masters, 20 px renders from the 16 px master, and 40 px and above from the 256 px master. Each
frame is stored as PNG, which Windows reads for every size, so the file is byte-for-byte reproducible from the
masters. Edit the masters, never the ICO. Needs `resvg-py` (pinned in `scripts/requirements-icons.txt`).

The tray icons (`design/SPEC.md` section 17) are `tray-normal.ico`, `tray-pressure.ico` and `tray-needs.ico`, each
with frames of 16, 20, 24 and 32 px: Normal is the plain icon, Pressure carries a triangle in the dark theme's `Warn`,
Needs you a disc in its `Info`, each with a rim in `Base`, so the states differ in shape and read on a light or a dark
taskbar. The colours come from `design/tokens/colors.json`, so a token change fails `--check` until the icons are
rebuilt. The four files are written as one set. `--out <file.ico>` alone builds or checks the app icon only;
`--tray-out <folder>` and `--tokens <file>` say where the tray icons go and where their colours come from. Exit 0
done, 1 a file differs (`--check`), 2 a token is bad, 3 an icon couldn't be written.

    python scripts/build-icons.py --out <file.ico> --badge-fill "#D97757" [--badge-mark <mark.svg>]

writes a campaign launcher's icon instead (`scripts/start-run.ps1 -InstallShortcut`): the same frames, each with a
round badge in the bottom-right corner, in the runner's colour with a white outline. A runner's mark (the Claude
symbol, the Gemini sparkle) is another company's trademark, so none is kept in this repository: when the mark file
exists on this PC it is drawn in white inside the badge from 32 px up, and without it the badge carries its colour
alone. A badged icon is a per-machine file and is never written into the repository.
"""
import argparse
import base64
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
# Frame size -> the master it renders from (SPEC section 18).
FRAMES = {16: 16, 20: 16, 24: 24, 32: 32, 40: 256, 48: 256, 64: 256, 256: 256}


def render(master: Path, size: int) -> bytes:
    import resvg_py

    png = resvg_py.svg_to_bytes(svg_path=str(master), width=size, height=size)
    return bytes(png)


MARK_FROM = 32  # below this the mark is a smudge: the colour alone tells the runners apart
WHITE = "0 0 0 0 1  0 0 0 0 1  0 0 0 0 1  0 0 0 1 0"  # whatever colour the mark file uses, the badge shows it in white


def badged(png: bytes, size: int, fill: str, mark: Path | None) -> bytes:
    """One frame with its runner's badge: a circle of `fill` with a white outline, bottom right, a quarter of the
    frame in radius, with the mark inside it when there is one and the frame is large enough to show it."""
    import resvg_py

    def uri(kind: str, data: bytes) -> str:
        return f"data:image/{kind};base64,{base64.b64encode(data).decode('ascii')}"
    radius = size * 0.24
    centre = size - radius - max(0.25, size * 0.01)
    ring = max(1.0, size * 0.025)
    inside = ""
    if mark and size >= MARK_FROM:
        side = radius * 1.15
        inside = (f'<filter id="w"><feColorMatrix type="matrix" values="{WHITE}"/></filter>'
                  f'<image x="{centre - side / 2}" y="{centre - side / 2}" width="{side}" height="{side}" filter="url(#w)" '
                  f'href="{uri("svg+xml", mark.read_bytes())}"/>')
    svg = (f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 {size} {size}">'
           f'<image width="{size}" height="{size}" href="{uri("png", png)}"/>'
           f'<circle cx="{centre}" cy="{centre}" r="{radius}" fill="#fff"/>'
           f'<circle cx="{centre}" cy="{centre}" r="{radius - ring}" fill="{fill}"/>{inside}</svg>')
    return bytes(resvg_py.svg_to_bytes(svg_string=svg, width=size, height=size))


def check_mark(mark: Path) -> None:
    """Raise ValueError unless the mark file draws something on its own. Embedded in the badge, a mark the renderer
    can't read is dropped in silence, which would ship a colour-only badge as if the mark were there."""
    import io

    import resvg_py
    from PIL import Image

    try:
        png = bytes(resvg_py.svg_to_bytes(svg_string=mark.read_text(encoding="utf-8"), width=64, height=64))
        drawn = Image.open(io.BytesIO(png)).convert("RGBA").getextrema()[3][1] > 0
    except Exception as error:  # the renderer and the decoder raise their own types for a file they can't read
        raise ValueError(f"the mark {mark} can't be drawn ({error})") from error
    if not drawn:
        raise ValueError(f"the mark {mark} draws nothing")


# --- The tray's state icons (D02 T01 §3, design/SPEC.md §17) ---
# Frame size -> master: the notification area at 100%, 125%, 150% and 200% scaling, each from the master SPEC §18 names.
TRAY_FRAMES = {16: 16, 20: 16, 24: 24, 32: 32}
# State -> (file, overlay shape, the dark theme's token for its colour). Normal is the plain icon. The states differ in
# shape, not only colour, and each overlay has a rim in the theme's Base: a dark rim keeps the shape's edge against its
# own fill on a light taskbar, where a white one vanishes.
TRAY_STATES = {
    "normal": ("tray-normal.ico", None, None),
    "pressure": ("tray-pressure.ico", "triangle", "Warn"),
    "needs": ("tray-needs.ico", "disc", "Info"),
}
TOKENS = ROOT / "design" / "tokens" / "colors.json"
COLOUR = re.compile(r"#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})")


class TokenError(Exception):
    """A token the tray icons need is missing or isn't a colour, named by its path in the token file."""


def tray_colours(tokens: Path) -> dict:
    """The dark theme's Warn, Info and Base from the token file, each #RRGGBB or #AARRGGBB, or TokenError."""
    try:
        data = json.loads(tokens.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError) as error:
        raise TokenError(f"{tokens} can't be read ({error})") from error
    except json.JSONDecodeError as error:
        raise TokenError(f"{tokens} is not JSON ({error})") from error
    dark = data.get("dark") if isinstance(data, dict) else None
    if not isinstance(dark, dict):
        raise TokenError(f"{tokens}: dark is missing or not an object")
    colours = {}
    for name in ("Warn", "Info", "Base"):
        value = dark.get(name)
        if not isinstance(value, str) or not COLOUR.fullmatch(value):
            raise TokenError(f"{tokens}: dark.{name} must be #RRGGBB or #AARRGGBB, not {value!r}")
        colours[name] = value
    return colours


def paint(colour: str) -> str:
    """A token colour as SVG attributes. The tokens write alpha first (#AARRGGBB), as WPF reads it; SVG would read the
    same eight digits as #RRGGBBAA, so the alpha is split off into fill-opacity."""
    digits = colour[1:]
    if len(digits) == 8:
        return f'fill="#{digits[2:]}" fill-opacity="{int(digits[:2], 16) / 255:.4f}"'
    return f'fill="#{digits}"'


def overlay_box(size: int) -> tuple:
    """The overlay's centre, half-size and rim width for a frame: a box of whole pixels, two thirds of the frame wide,
    flush in the bottom-right corner, so its edges fall on pixel edges and a 16 px triangle is still a triangle. (A
    smaller box set a quarter pixel in from the corner drew a smudge at 16 px.)"""
    side = round(size * 0.66)
    radius = side / 2
    centre = size - radius
    rim = max(1.0, round(size * 0.06))
    return centre, radius, rim


def overlaid(png: bytes, size: int, shape: str, fill: str, rim: str) -> bytes:
    """One frame with its state's overlay: a disc or an upward triangle of `fill`, with a rim of `rim`, bottom right."""
    import resvg_py

    centre, radius, width = overlay_box(size)
    if shape == "disc":
        marks = (f'<circle cx="{centre}" cy="{centre}" r="{radius}" {paint(rim)}/>'
                 f'<circle cx="{centre}" cy="{centre}" r="{radius - width}" {paint(fill)}/>')
    else:
        # An upward triangle in the same box, and the same triangle drawn smaller about its centroid for the fill:
        # the inset that leaves a rim of `width` all round is width over the triangle's inradius (0.618 of the radius).
        outer = [(centre, centre - radius), (centre + radius, centre + radius), (centre - radius, centre + radius)]
        cx, cy = centre, centre + radius / 3
        keep = 1 - width / (0.618 * radius)
        inner = [(cx + (x - cx) * keep, cy + (y - cy) * keep) for x, y in outer]
        points = lambda corners: " ".join(f"{x:.3f},{y:.3f}" for x, y in corners)
        marks = f'<polygon points="{points(outer)}" {paint(rim)}/><polygon points="{points(inner)}" {paint(fill)}/>'
    href = f"data:image/png;base64,{base64.b64encode(png).decode('ascii')}"
    svg = (f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 {size} {size}">'
           f'<image width="{size}" height="{size}" href="{href}"/>{marks}</svg>')
    return bytes(resvg_py.svg_to_bytes(svg_string=svg, width=size, height=size))


def build_tray(masters: Path, state: str, colours: dict) -> bytes:
    """One tray state's ICO: its frames from the masters, with the state's overlay in its token's colour."""
    _, shape, token = TRAY_STATES[state]
    frames = []
    for size, source in sorted(TRAY_FRAMES.items()):
        master = masters / f"bosun-{source}.svg"
        if not master.is_file():
            raise FileNotFoundError(f"missing master {master}")
        png = render(master, size)
        frames.append((size, overlaid(png, size, shape, colours[token], colours["Base"]) if shape else png))
    return ico(frames)


def build(masters: Path, badge_fill: str | None = None, badge_mark: Path | None = None) -> bytes:
    """The whole ICO file: header, one directory entry per frame, then the PNG frames in size order."""
    frames = []
    for size, source in sorted(FRAMES.items()):
        master = masters / f"bosun-{source}.svg"
        if not master.is_file():
            raise FileNotFoundError(f"missing master {master}")
        png = render(master, size)
        frames.append((size, badged(png, size, badge_fill, badge_mark) if badge_fill else png))
    return ico(frames)


def ico(frames: list) -> bytes:
    """An ICO file from (size, PNG) frames in size order."""
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = len(header) + 16 * len(frames)
    entries, data = [], []
    for size, png in frames:
        dim = 0 if size >= 256 else size  # 0 means 256 in an ICO directory entry
        entries.append(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset))
        data.append(png)
        offset += len(png)
    return header + b"".join(entries) + b"".join(data)


def write_atomic(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(content)
        os.replace(tmp, path)
    except BaseException:
        Path(tmp).unlink(missing_ok=True)
        raise


def discard(temp: str) -> None:
    try:
        os.unlink(temp)
    except OSError:
        pass  # already gone, or held: a stray temporary file changes nothing a reader sees


def write_set(outputs: dict) -> int:
    """Writes the icon files as one set, or says exactly what happened. Each is staged as a temporary file beside its
    target first, so a full disk or a denied folder fails before any icon is changed; then each is moved over its
    target. A move that fails (an icon held open) leaves that file and the ones after it as they were, the temporary
    files are removed, and the report names what was updated and what wasn't. Returns 0, or 3."""
    staged = []  # (temporary file, target)
    try:
        for path, content in outputs.items():
            path.parent.mkdir(parents=True, exist_ok=True)
            fd, temp = tempfile.mkstemp(dir=path.parent, prefix=path.name + ".", suffix=".tmp")
            staged.append((temp, path))
            with os.fdopen(fd, "wb") as handle:
                handle.write(content)
    except OSError as error:
        for temp, _ in staged:
            discard(temp)
        print(f"build-icons: can't write the icons ({error}); nothing was changed", file=sys.stderr)
        return 3
    done = []
    for index, (temp, path) in enumerate(staged):
        try:
            os.replace(temp, path)
            done.append(path)
        except OSError as error:
            for other, _ in staged[index:]:
                discard(other)
            left = ", ".join(str(p) for _, p in staged[index:])
            print(f"build-icons: can't replace {path} ({error}). Updated: {', '.join(map(str, done)) or 'nothing'}. "
                  f"Left as they were: {left}. Close whatever holds the file and run the script again; --check fails "
                  "until it has.", file=sys.stderr)
            return 3
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Build resources/bosun.ico and the tray state icons from the SVG masters in design/icon/.")
    parser.add_argument("--check", action="store_true", help="report drift instead of writing")
    parser.add_argument("--masters", type=Path, default=ROOT / "design" / "icon")
    parser.add_argument("--out", type=Path, default=ROOT / "resources" / "bosun.ico")
    parser.add_argument("--tokens", type=Path, default=TOKENS, help="the token file the tray overlays take their colours from")
    parser.add_argument("--tray-out", type=Path, help="the folder for tray-normal.ico, tray-pressure.ico and tray-needs.ico "
                        "(default resources/, beside the app icon; with --out alone they are not built)")
    parser.add_argument("--badge-fill", help="write a campaign launcher's icon: the badge's colour, as #RRGGBB")
    parser.add_argument("--badge-mark", type=Path, help="the runner's mark, an SVG file on this PC, drawn white in the badge")
    args = parser.parse_args(argv)
    if args.badge_fill:
        if not re.fullmatch(r"#[0-9A-Fa-f]{6}", args.badge_fill):
            parser.error(f"--badge-fill must look like #D97757, not {args.badge_fill}")
        if args.check or args.out.resolve().is_relative_to(ROOT):
            parser.error("a badged icon is a per-machine file: give --out a path outside the repository, without --check")
    elif args.badge_mark:
        parser.error("--badge-mark needs --badge-fill")
    mark = args.badge_mark if args.badge_mark and args.badge_mark.is_file() else None
    if mark:
        try:
            check_mark(mark)
        except ValueError as error:
            print(f"{error}: nothing was written; mend or remove it", file=sys.stderr)
            return 1
    if args.badge_fill:
        content = build(args.masters, args.badge_fill, mark)
        write_atomic(args.out, content)
        print(f"wrote {args.out} ({len(FRAMES)} frames, {len(content)} bytes)")
        return 0
    # The app icon, and with it the tray's three state icons: beside it in resources/ by default, or where --tray-out
    # says. With --out alone only the app icon is built or checked, as before the tray icons existed.
    outputs = {args.out: (build(args.masters), len(FRAMES))}
    tray_dir = args.tray_out or (ROOT / "resources" if args.out == parser.get_default("out") else None)
    if tray_dir:
        try:
            colours = tray_colours(args.tokens)
        except TokenError as error:
            print(f"{error}: nothing was written", file=sys.stderr)
            return 2
        for state, (name, _, _) in TRAY_STATES.items():
            outputs[tray_dir / name] = (build_tray(args.masters, state, colours), len(TRAY_FRAMES))
    if args.check:
        stale = [path for path, (content, _) in outputs.items() if not path.is_file() or path.read_bytes() != content]
        if stale:
            # The repair names the same inputs and outputs this check was given, so following it mends what was checked.
            repair = ["python", "scripts/build-icons.py"]
            for flag, value in (("--masters", args.masters), ("--out", args.out), ("--tokens", args.tokens)):
                if value != parser.get_default(flag[2:]):
                    repair += [flag, str(value)]
            if args.tray_out:
                repair += ["--tray-out", str(args.tray_out)]
            for path in stale:
                print(f"{path} is out of date with {args.masters}: run {subprocess.list2cmdline(repair)}", file=sys.stderr)
            return 1
        for path in outputs:
            print(f"{path} matches the masters")
        return 0
    code = write_set({path: content for path, (content, _) in outputs.items()})
    if code:
        return code
    for path, (content, frames) in outputs.items():
        print(f"wrote {path} ({frames} frames, {len(content)} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
