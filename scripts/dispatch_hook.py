#!/usr/bin/env python3
"""Claude Code PreToolUse hook: a review runs through the shell tool's own background option, and never on an edited pin.

A review (`scripts/review-dispatch.py ... --prompt ...`) takes minutes, often longer than the shell tool's 10 minute
limit. Started with the tool's `run_in_background`, the session is told the moment it finishes and acts on it; started
detached (`&`, `nohup`, `disown`, `setsid`, `Start-Job`, `Start-Process`) nothing tells the session, and started in the
foreground it can be cut off. This hook refuses both, naming the fix. And because the reviewer pin is the owner's,
`review-dispatch.py` and `todo-graph.py stamp` are refused while `.conclave/reviewers.toml` has uncommitted changes.

Calls that run no review (`--check-pin`, `--list-rounds`, `--help`) pass, and so does input it can't read, with a
message: this hook guards the review's timing and its pin, and `scripts/tool-guard.py` is the deny list that fails
closed. Exit 2 refuses the call and shows the reason to Claude.

Ported from Recto (`D:\\GitHub\\Recto\\scripts\\dispatch_hook.py`), with the pin check from Resolute
(`D:\\GitHub\\Resolute\\scripts\\dispatch_hook.py`), which checks the pin only; so does this, since findings files
change on the way to every commit.
"""
from __future__ import annotations

import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
PIN = ".conclave/reviewers.toml"

# The dispatcher run as a command: after `python`, `python3`, `python.exe` or the `py` launcher, quoted or not, with any
# interpreter options, or in command position (the start, after `;`, `&`, `|`, `(`, a newline, `nohup`, `setsid` or
# `exec`). Text that only mentions it (a heredoc, a commit message) is not a run.
REVIEW = re.compile(r"(?:\bpy(?:thon)?(?:3(?:\.\d+)?)?(?:\.exe)?['\"]?(?:\s+(?:-[XW]\s*\S+|-[\w.]+))*\s+['\"]?"
                    r"|(?:^|[\n;&|(]|\bnohup\b|\bsetsid\b|\bexec\b)\s*)"
                    r"(?:[\w.~/\\:-]*[/\\])?review-dispatch\.py\b")
STAMP = re.compile(r"\btodo-graph\.py\b[^\n;|&]*\bstamp\b")
# Bash: a single `&` that backgrounds a command, not `&&`, `&>`, `>&` or `2>&1`.
BASH_DETACH = re.compile(r"(?<![&>])&(?![&>])|\bnohup\b|\bdisown\b|\bsetsid\b")
# PowerShell's launchers detach whatever they start, naming the script as an argument of their own.
POWERSHELL_LAUNCHER = re.compile(r"\bStart-(?:Job|ThreadJob|Process)\b", re.IGNORECASE)
# PowerShell: `&` is the call operator before a command, so only one that ends a statement backgrounds it.
POWERSHELL_DETACH = re.compile(r"(?<![&>])&\s*(?:$|[;\n])|" + POWERSHELL_LAUNCHER.pattern, re.IGNORECASE | re.MULTILINE)
DETACH = {"Bash": BASH_DETACH, "PowerShell": POWERSHELL_DETACH}
# A heredoc's body, from `<<EOF` (or `<<-'EOF'`) to its closing line, and PowerShell's @' '@ and @" "@ strings.
HEREDOC = re.compile(r"<<-?\s*(['\"]?)(\w+)\1[^\n]*\n.*?^\s*\2\s*$|@(['\"])\r?\n.*?^\3@", re.S | re.M)


def pin_dirty(repo: Path = ROOT) -> bool:
    """True when the reviewer pin has changes not committed, staged or not."""
    try:
        done = subprocess.run(["git", "status", "--porcelain", "--", PIN], cwd=repo, capture_output=True, text=True, timeout=30)
    except (OSError, subprocess.SubprocessError):
        return False  # git isn't there to ask; the dispatcher and stamp read the pin's committed form anyway
    return done.returncode == 0 and bool(done.stdout.strip())


def verdict(payload: dict, repo: Path = ROOT) -> str | None:
    """Why this tool call is refused, or None to let it run. It reads shell text, so it is an early warning for the
    launch forms a session really uses, not a proof that no spelling gets past it."""
    tool = payload.get("tool_name")
    detach = DETACH.get(tool)
    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if detach is None or not isinstance(command, str):
        return None
    command = HEREDOC.sub("", command)  # a heredoc's body is text, never a command, wherever its lines fall
    launched =tool == "PowerShell" and "review-dispatch.py" in command and POWERSHELL_LAUNCHER.search(command)
    reviewing = "--prompt" in command and (launched or REVIEW.search(command))
    if (reviewing or STAMP.search(command)) and pin_dirty(repo):
        return (f"{PIN} has uncommitted changes, and the reviewer pin is the owner's: restore it to HEAD (or commit the "
                f"owner's change) before running review-dispatch.py or todo-graph.py stamp")
    if not reviewing:
        return None
    if launched or detach.search(command):
        return ("review-dispatch: run the review with the shell tool's run_in_background option, not detached "
                "(&, nohup, disown, setsid, Start-Job, Start-Process): only the tool's option tells the session when "
                "it finishes")
    if tool_input.get("run_in_background") is not True:
        return ("review-dispatch: a review takes minutes, often past the shell tool's 10 minute limit; run it with the "
                "shell tool's run_in_background option, so the session is told when it finishes")
    return None


def main() -> int:
    try:
        payload = json.load(sys.stdin)
        if not isinstance(payload, dict):
            raise ValueError("not an object")
    except (ValueError, OSError) as error:
        print(f"dispatch hook: couldn't read its input ({error}), so it let the call through", file=sys.stderr)
        return 0
    why = verdict(payload)
    if why:
        print(why, file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
