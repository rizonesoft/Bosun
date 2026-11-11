#!/usr/bin/env python3
"""Builds the app icon `resources/bosun.ico` from the SVG masters in `design/icon/`.

    python scripts/build-icons.py            regenerate resources/bosun.ico
    python scripts/build-icons.py --check    exit 1 when resources/bosun.ico differs from what the masters give

The frames follow `design/SPEC.md` section 18: 16, 20, 24, 32, 40, 48, 64 and 256 px. 16, 24 and 32 px come from
their own pixel-grid masters, 20 px renders from the 16 px master, and 40 px and above from the 256 px master. Each
frame is stored as PNG, which Windows reads for every size, so the file is byte-for-byte reproducible from the
masters. Edit the masters, never the ICO. Needs `resvg-py` (pinned in `scripts/requirements-icons.txt`).
"""
import argparse
import os
from pathlib import Path
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


def build(masters: Path) -> bytes:
    """The whole ICO file: header, one directory entry per frame, then the PNG frames in size order."""
    frames = []
    for size, source in sorted(FRAMES.items()):
        master = masters / f"bosun-{source}.svg"
        if not master.is_file():
            raise FileNotFoundError(f"missing master {master}")
        frames.append((size, render(master, size)))
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
    args = parser.parse_args(argv)
    content = build(args.masters)
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
