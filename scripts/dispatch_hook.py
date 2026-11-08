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

import importlib.util
import json
import os
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


def _parser():
    """The deny list's own command parser (scripts/tool-guard.py), so both hooks read a command line the same way:
    chains split outside quotes, wrappers opened, and quoted text never taken for a command."""
    spec = importlib.util.spec_from_file_location("tool_guard", Path(__file__).with_name("tool-guard.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PARSER = _parser()
NO_REVIEW = ("--check-pin", "--list-rounds", "--help")  # the dispatcher's only runs that start no reviewer


def starts_no_reviewer(words: list[str]) -> bool:
    """True when these dispatcher arguments ask only for --check-pin, --list-rounds or --help, as argparse reads them:
    whole, after `=`, or shortened. Any other run is a review, `--prompt=x` and `--pro x` included."""
    for word in words:
        option = word.split("=", 1)[0]
        if word == "-h" or (option.startswith("--") and len(option) > 3 and any(name.startswith(option) for name in NO_REVIEW)):
            return True
    return False


INTERPRETER = re.compile(r"py(?:thon)?(?:3(?:\.\d+)?)?(?:\.exe)?")


def runs(command: str, powershell: bool) -> tuple:
    """(runs a review, runs a stamp): what the command really runs, segment by segment."""
    reviewing = stamping = False
    for segment, _ in PARSER.segments(command, powershell):
        words = PARSER.tokens_of(segment)
        if not words:
            continue
        if INTERPRETER.fullmatch(os.path.basename(words[0]).lower()):
            words = [w for w in words[1:] if not w.startswith("-")][:1] + words[1:]
        script = os.path.basename(words[0]).lower() if words else ""
        reviewing |= script == "review-dispatch.py" and not starts_no_reviewer(words[1:])
        stamping |= script == "todo-graph.py" and "stamp" in words
    return reviewing, stamping


def powershell_background(command: str) -> bool:
    """A PowerShell `&` that backgrounds what comes before it: outside quotes, not `&&`, not a redirect, and not the
    call operator, which starts a statement."""
    quote, last = None, ""
    for i, char in enumerate(command):
        if quote:
            quote = None if char == quote else quote
            continue
        if char in "'\"":
            quote = char
        elif char == "&" and command[i + 1:i + 2] != "&" and command[i - 1:i] not in ("&", ">"):
            if last and last not in ";|({\n":
                return True
        if not char.isspace():
            last = char
    return False


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
    powershell = tool == "PowerShell"
    launched = powershell and "review-dispatch.py" in command and POWERSHELL_LAUNCHER.search(command) \
        and not starts_no_reviewer(PARSER.tokens_of(command))
    reviewing, stamping = runs(command, powershell)
    reviewing = reviewing or bool(launched)
    if (reviewing or stamping) and pin_dirty(repo):
        return (f"{PIN} has uncommitted changes, and the reviewer pin is the owner's: restore it to HEAD (or commit the "
                f"owner's change) before running review-dispatch.py or todo-graph.py stamp")
    if not reviewing:
        return None
    if launched or detach.search(command) or (powershell and powershell_background(command)):
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
