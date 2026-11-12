#!/usr/bin/env python3
"""Every native executable carries the hardening the build asks the linker for, read from the file itself.

    python scripts/native-pe-check.py build/Release          # every .exe in the folder
    python scripts/native-pe-check.py bin/bosun.exe ...      # or the images named

Compile flags prove nothing about the image: control-flow guard needs the compiler to emit its checks and the linker
to set the flag, and one without the other builds without a word. So this reads each image's PE headers (no dumpbin,
so it runs in a plain shell and in CI) and requires:

    control-flow guard     IMAGE_DLLCHARACTERISTICS_GUARD_CF        linked with /guard:cf
    ASLR, high entropy     DYNAMIC_BASE and HIGH_ENTROPY_VA         the 64-bit linker's defaults, lost by /FIXED
    NX                     IMAGE_DLLCHARACTERISTICS_NX_COMPAT       the linker's default, lost by /NXCOMPAT:NO
    CET compatible         the extended DLL characteristics debug   linked with /CETCOMPAT (x64 images only: the
                           entry, bit IMAGE_DLLCHARACTERISTICS_EX_CET_COMPAT    flag has no meaning on ARM64)

Exit 0 when every image holds, 1 naming each image and what it lacks, 2 when a path is missing, is not a PE image
this can read, or a folder holds no .exe. Stdlib only.
"""
import argparse
from pathlib import Path
import struct
import sys

MACHINES = {0x8664: "x64", 0xAA64: "arm64"}
HIGH_ENTROPY_VA, DYNAMIC_BASE, NX_COMPAT, GUARD_CF = 0x0020, 0x0040, 0x0100, 0x4000
DEBUG_DIRECTORY = 6
EX_DLLCHARACTERISTICS = 20  # IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS
CET_COMPAT = 0x1
MAX_ENTRIES = 64  # a debug directory longer than this is not one this reads


class NotAnImage(Exception):
    """The file is not a PE image this can read."""


def unpack(fmt, data, offset, what):
    size = struct.calcsize(fmt)
    if offset < 0 or offset + size > len(data):
        raise NotAnImage(f"it ends before its {what}")
    return struct.unpack_from(fmt, data, offset)


def read_image(data):
    """(machine name, DLL characteristics, extended DLL characteristics or None when the image has no such entry)."""
    if data[:2] != b"MZ":
        raise NotAnImage("it does not start with MZ")
    (pe,) = unpack("<I", data, 0x3C, "PE header offset")
    if data[pe:pe + 4] != b"PE\0\0":
        raise NotAnImage("it has no PE signature")
    machine, sections, _, _, _, optional_size, _ = unpack("<HHIIIHH", data, pe + 4, "COFF header")
    optional = pe + 24
    (magic,) = unpack("<H", data, optional, "optional header")
    if magic not in (0x10B, 0x20B):
        raise NotAnImage(f"its optional header is of an unknown kind ({magic:#x})")
    (characteristics,) = unpack("<H", data, optional + 70, "DLL characteristics")
    directories = optional + (112 if magic == 0x20B else 96)
    (count,) = unpack("<I", data, directories - 4, "data directory count")
    extended = None
    if count > DEBUG_DIRECTORY:
        rva, size = unpack("<II", data, directories + 8 * DEBUG_DIRECTORY, "debug directory entry")
        table = optional + optional_size
        for index in range(min(sections, 96)):
            _, _, virtual, raw_size, raw = unpack("<8sIIII", data, table + 40 * index, "section table")
            if rva and virtual <= rva < virtual + max(raw_size, 1):
                start = raw + (rva - virtual)
                for entry in range(min(size // 28, MAX_ENTRIES)):
                    _, _, _, _, kind, length, _, pointer = unpack("<IIHHIIII", data, start + 28 * entry, "debug directory")
                    if kind == EX_DLLCHARACTERISTICS and length >= 4:
                        (extended,) = unpack("<I", data, pointer, "extended DLL characteristics")
                break
    return MACHINES.get(machine, f"machine {machine:#x}"), characteristics, extended


def judge(machine, characteristics, extended):
    """What the image lacks, as a list of reasons; empty when it holds."""
    problems = []
    if machine not in MACHINES.values():
        problems.append(f"it is built for {machine}, not x64 or ARM64")
    if not characteristics & GUARD_CF:
        problems.append("no control-flow guard (link with /guard:cf)")
    if not characteristics & DYNAMIC_BASE:
        problems.append("no ASLR (it was linked with /FIXED or /DYNAMICBASE:NO)")
    if not characteristics & HIGH_ENTROPY_VA:
        problems.append("no high-entropy ASLR (it was linked with /HIGHENTROPYVA:NO)")
    if not characteristics & NX_COMPAT:
        problems.append("not NX compatible (it was linked with /NXCOMPAT:NO)")
    if machine == "x64" and not (extended or 0) & CET_COMPAT:
        problems.append("not CET compatible (link with /CETCOMPAT)")
    return problems


def images(paths):
    """The .exe files the arguments name. Raises NotAnImage for a path that is missing or a folder with none."""
    found = []
    for path in map(Path, paths):
        if path.is_dir():
            inside = sorted(path.glob("*.exe"))
            if not inside:
                raise NotAnImage(f"{path} holds no .exe (build first: pwsh scripts/build.ps1)")
            found += inside
        elif path.is_file():
            found.append(path)
        else:
            raise NotAnImage(f"{path} does not exist (build first: pwsh scripts/build.ps1)")
    return found


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("paths", nargs="+", help="an .exe, or a folder whose .exe files are all checked")
    args = parser.parse_args(argv)
    try:
        targets = images(args.paths)
    except NotAnImage as exc:
        print(f"native-pe-check: {exc}")
        return 2
    failed = False
    for path in targets:
        try:
            problems = judge(*read_image(path.read_bytes()))
        except (NotAnImage, OSError) as exc:
            print(f"native-pe-check: {path} could not be read as a PE image: {exc}")
            return 2
        if problems:
            failed = True
            for problem in problems:
                print(f"FAIL {path}: {problem}")
        else:
            print(f"ok   {path}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
