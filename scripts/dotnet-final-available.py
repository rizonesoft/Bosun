#!/usr/bin/env python3
"""Exit 0 when a final .NET 11 SDK (11.0.1xx, no preview or release candidate) is installed, 1 when it isn't.

The owner chose to start on the release candidate and move when the final ships, so `D00 T02 §3` waits on this, as a
wake in todo/wakes.json: the run guard checks it while a run waits, and the resumed session clears the Need. It reads
`dotnet --list-sdks`, whose lines look like `11.0.100 [C:\\Program Files\\dotnet\\sdk]`, and says what it found.
"""
from __future__ import annotations

import re
import shutil
import subprocess
import sys

FINAL = re.compile(r"^11\.0\.1\d\d$")  # 11.0.100, 11.0.101 ...; 11.0.100-rc.2.25502.107 is not final


def sdks(run=subprocess.run) -> list[str] | None:
    """The installed SDK versions, or None when `dotnet` can't be run."""
    dotnet = shutil.which("dotnet")
    if not dotnet:
        return None
    try:
        done = run([dotnet, "--list-sdks"], capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.SubprocessError):
        return None
    if done.returncode != 0:
        return None
    return [line.split()[0] for line in done.stdout.splitlines() if line.strip()]


def main(run=subprocess.run) -> int:
    found = sdks(run)
    if found is None:
        print("no final .NET 11 SDK: dotnet isn't installed or couldn't be run")
        return 1
    final = [v for v in found if FINAL.match(v)]
    if final:
        print(f"final .NET 11 SDK installed: {', '.join(final)}")
        return 0
    print(f"no final .NET 11 SDK yet; installed: {', '.join(found) or 'none'}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
