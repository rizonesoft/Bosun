#!/usr/bin/env python3
"""Builds the app icon `resources/bosun.ico` from the SVG masters in `design/icon/`.

    python scripts/build-icons.py            regenerate resources/bosun.ico
    python scripts/build-icons.py --check    exit 1 when resources/bosun.ico differs from what the masters give

The frames follow `design/SPEC.md` section 18: 16, 20, 24, 32, 40, 48, 64 and 256 px. 16, 24 and 32 px come from
their own pixel-grid masters, 20 px renders from the 16 px master, and 40 px and above from the 256 px master. Each
frame is stored as PNG, which Windows reads for every size, so the file is byte-for-byte reproducible from the
masters. Edit the masters, never the ICO. Needs `resvg-py` (pinned in `scripts/requirements-icons.txt`).

    python scripts/build-icons.py --out <file.ico> --badge-fill "#D97757" [--badge-mark <mark.svg>]

writes a campaign launcher's icon instead (`scripts/start-run.ps1 -InstallShortcut`): the same frames, each with a
round badge in the bottom-right corner, in the runner's colour with a white outline. A runner's mark (the Claude
symbol, the Gemini sparkle) is another company's trademark, so none is kept in this repository: when the mark file
exists on this PC it is drawn in white inside the badge from 32 px up, and without it the badge carries its colour
alone. A badged icon is a per-machine file and is never written into the repository.
"""
import argparse
import base64
import os
from pathlib import Path
import re
import struct
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


def build(masters: Path, badge_fill: str | None = None, badge_mark: Path | None = None) -> bytes:
    """The whole ICO file: header, one directory entry per frame, then the PNG frames in size order."""
    frames = []
    for size, source in sorted(FRAMES.items()):
        master = masters / f"bosun-{source}.svg"
        if not master.is_file():
            raise FileNotFoundError(f"missing master {master}")
        png = render(master, size)
        frames.append((size, badged(png, size, badge_fill, badge_mark) if badge_fill else png))
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


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Build resources/bosun.ico from the SVG masters in design/icon/.")
    parser.add_argument("--check", action="store_true", help="report drift instead of writing")
    parser.add_argument("--masters", type=Path, default=ROOT / "design" / "icon")
    parser.add_argument("--out", type=Path, default=ROOT / "resources" / "bosun.ico")
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
    content = build(args.masters, args.badge_fill, mark)
    if args.check:
        current = args.out.read_bytes() if args.out.is_file() else None
        if current != content:
            print(f"{args.out} is out of date with {args.masters}: run python scripts/build-icons.py", file=sys.stderr)
            return 1
        print(f"{args.out} matches the masters")
        return 0
    write_atomic(args.out, content)
    print(f"wrote {args.out} ({len(FRAMES)} frames, {len(content)} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
