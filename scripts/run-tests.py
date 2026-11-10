#!/usr/bin/env python3
"""Runs the tooling's Python tests, several test classes at a time.

    python scripts/run-tests.py                 every class under scripts/tests
    python scripts/run-tests.py test_run_guard  only the classes whose name holds one of the words given
    python scripts/run-tests.py --jobs 2        how many run at once (default: half the processors, at most 6)

`python -m unittest discover -s scripts/tests` runs the same tests one after another, which took nine minutes for 784
tests on 2026-11-10: nearly all of that is waiting on git and child processes, so the classes run side by side, each
in its own process. A class that passes prints one line, and one that fails prints its whole output. Exits 1 when any
class fails, times out or runs no test. Each class has a time limit, and its whole process tree is stopped at it.
Stdlib only.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
TESTS = ROOT / "scripts" / "tests"
CLASS_SECONDS = 900
RAN = re.compile(r"^Ran (\d+) tests? in ", re.M)
SKIPPED = re.compile(r"skipped=(\d+)")


def classes(suite, found=None):
    """Every test class under a discovered suite, as `module.Class`, in the order found."""
    found = {} if found is None else found
    for item in suite:
        if isinstance(item, unittest.TestSuite):
            classes(item, found)
        else:
            kind = type(item)
            found.setdefault(f"{kind.__module__}.{kind.__qualname__}", None)
    return list(found)


def stop_tree(proc):
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True, timeout=60)
        else:
            os.killpg(proc.pid, signal.SIGKILL)
    except (OSError, subprocess.SubprocessError):
        pass
    proc.kill()


def run_class(name, env, seconds=CLASS_SECONDS):
    """(name, ok, tests, skipped, seconds, output) for one class, run in a process of its own."""
    started = time.time()
    flags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
    proc = subprocess.Popen([sys.executable, "-m", "unittest", name], cwd=ROOT, env=env, stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=flags,
                            start_new_session=os.name != "nt")
    try:
        out, _ = proc.communicate(timeout=seconds)
        text = out.decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        stop_tree(proc)
        try:
            out, _ = proc.communicate(timeout=30)
        except subprocess.TimeoutExpired:
            out = b""
        return name, False, 0, 0, time.time() - started, out.decode("utf-8", "replace") + f"\ntimed out after {seconds} seconds; its process tree was stopped\n"
    ran = RAN.search(text)
    tests = int(ran.group(1)) if ran else 0
    skipped = SKIPPED.search(text.rsplit("\n", 3)[-2] if text.count("\n") >= 2 else text)
    ok = proc.returncode == 0 and tests > 0
    return name, ok, tests, int(skipped.group(1)) if skipped else 0, time.time() - started, text


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("words", nargs="*", help="run only classes whose `module.Class` holds one of these")
    parser.add_argument("--jobs", type=int, default=max(1, min(6, (os.cpu_count() or 2) // 2)))
    args = parser.parse_args(argv)
    if args.jobs < 1:
        parser.error("--jobs must be at least 1")
    sys.path.insert(0, str(TESTS))
    names = classes(unittest.defaultTestLoader.discover(str(TESTS)))
    broken = [n for n in names if n.startswith("unittest.loader.")]  # a module that failed to import
    names = [n for n in names if not args.words or any(w in n for w in args.words)]
    if not names:
        print("no test class matches")
        return 1
    env = {**os.environ, "PYTHONPATH": os.pathsep.join(filter(None, [str(TESTS), os.environ.get("PYTHONPATH")])),
           "PYTHONIOENCODING": "utf-8", "PYTHONDONTWRITEBYTECODE": "1"}
    started, total, skipped, failed = time.time(), 0, 0, []
    with ThreadPoolExecutor(max_workers=args.jobs) as pool:
        for name, ok, tests, skips, seconds, text in pool.map(lambda n: run_class(n, env), names):
            total, skipped = total + tests, skipped + skips
            print(f"{'ok  ' if ok else 'FAIL'} {name} ({tests} tests, {seconds:.1f}s)", flush=True)
            if not ok:
                failed.append(name)
                print(text, flush=True)
    for name in broken:
        print(f"FAIL {name}: a test module could not be imported; run `python -m unittest discover -s scripts/tests` to see why")
    failed += broken
    verdict = f"FAILED ({len(failed)} of {len(names)} classes: {', '.join(failed)})" if failed else "OK"
    print(f"Ran {total} tests in {len(names)} classes in {time.time() - started:.0f}s, {args.jobs} at a time"
          + (f" (skipped={skipped})" if skipped else "") + f": {verdict}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
