#!/usr/bin/env python3
"""Every C++ compile in the native build carries exactly one language standard switch, the one CMakeLists.txt pins.

    python scripts/native-std-check.py            # build/compile_commands.json against CMakeLists.txt
    python scripts/native-std-check.py <build folder> [--cmake <CMakeLists.txt>]

MSVC applies the last of two conflicting standard switches and only warns, so a stray `/std:c++latest` in a target's
options compiles the whole tool to another standard while the build stays green. This reads the compile database
Ninja writes at generation and fails, naming the source file, on a C++ compile whose standard switches (in either
spelling MSVC takes, `/std:` and `-std:`) are not exactly one, are not the pinned one, or name `c++latest`.

The pin is `set(CMAKE_CXX_STANDARD <n>)` in CMakeLists.txt, read as `c++<n>`.

Exit 0 when every compile holds, 1 naming each that does not, 2 with the reason when this cannot judge: the database
is missing, is not JSON, is not a list of records each with a file and a command, holds no C++ compile at all (an
empty database would otherwise pass, since every compile in it holds), or CMakeLists.txt names no single standard.
Stdlib only.
"""
import argparse
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SWITCH = re.compile(r"(?:^|\s)[/-]std:(\S+)")
CXX_SOURCE = re.compile(r"\.(cc|cpp|cxx|c\+\+|ixx|cppm)$", re.IGNORECASE)
PIN = re.compile(r"^\s*set\s*\(\s*CMAKE_CXX_STANDARD\s+(\d+)\s*\)", re.IGNORECASE | re.MULTILINE)


class CannotJudge(Exception):
    """The input is not something a verdict can be given on (exit 2)."""


def pinned_standard(cmake):
    """`c++20` for `set(CMAKE_CXX_STANDARD 20)`. CannotJudge unless the file names exactly one."""
    try:
        text = Path(cmake).read_text(encoding="utf-8")
    except OSError as exc:
        raise CannotJudge(f"{cmake} can't be read: {exc}") from None
    found = set(PIN.findall(text))
    if len(found) != 1:
        raise CannotJudge(f"{cmake} must set CMAKE_CXX_STANDARD once, to one value (found: {sorted(found) or 'none'})")
    return "c++" + found.pop()


def cxx_commands(database):
    """[(source file, command line)] for every C++ compile in the database. CannotJudge when it is not usable."""
    try:
        records = json.loads(Path(database).read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise CannotJudge(f"{database} does not exist (build first: pwsh scripts/build.ps1)") from None
    except (OSError, ValueError) as exc:
        raise CannotJudge(f"{database} can't be read as JSON: {exc}") from None
    if not isinstance(records, list):
        raise CannotJudge(f"{database} is not a list of compile records")
    found = []
    for index, record in enumerate(records):
        if not isinstance(record, dict) or not isinstance(record.get("file"), str):
            raise CannotJudge(f"{database}: record {index} has no file")
        command = record.get("command")
        if command is None and isinstance(record.get("arguments"), list):
            command = " ".join(str(part) for part in record["arguments"])
        if not isinstance(command, str) or not command.strip():
            raise CannotJudge(f"{database}: record {index} ({record['file']}) has no command")
        if CXX_SOURCE.search(record["file"]):
            found.append((record["file"], command))
    if not found:
        raise CannotJudge(f"{database} holds no C++ compile, so there is nothing to judge")
    return found


def problems(commands, pinned):
    """One line per compile that does not carry exactly the pinned standard."""
    bad = []
    for source, command in commands:
        switches = [s.lower() for s in SWITCH.findall(command)]
        if switches != [pinned]:
            said = ", ".join(switches) if switches else "no standard switch"
            bad.append(f"{source}: {said} (wanted exactly one: {pinned})")
    return bad


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("build", nargs="?", default=str(ROOT / "build"), help="the folder holding compile_commands.json")
    parser.add_argument("--cmake", default=str(ROOT / "CMakeLists.txt"), help="the CMakeLists.txt that pins the standard")
    args = parser.parse_args(argv)
    try:
        pinned = pinned_standard(args.cmake)
        commands = cxx_commands(Path(args.build) / "compile_commands.json")
    except CannotJudge as exc:
        print(f"native-std-check: {exc}")
        return 2
    bad = problems(commands, pinned)
    for line in bad:
        print(f"FAIL {line}")
    if bad:
        return 1
    print(f"ok   {len(commands)} C++ compiles, each with exactly {pinned}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
