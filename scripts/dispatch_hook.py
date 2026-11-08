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

# A heredoc's body, from `<<EOF` (or `<<-'EOF'`) to its closing line, and PowerShell's @' '@ and @" "@ strings.
HEREDOC = re.compile(r"(<<-?\s*(['\"]?)(\w+)\2[^\n]*)\n.*?^\s*\3\s*$|@(['\"])\r?\n.*?^\4@", re.S | re.M)


def without_heredocs(command: str) -> str:
    """A heredoc's body is text, never a command: it goes, and its opening line stays, with whatever operators follow
    `<<EOF` there. A PowerShell here-string is a value, so it becomes an empty one."""
    return HEREDOC.sub(lambda m: m.group(1) if m.group(1) else "''", command)
DETACHING_PREFIXES = {"nohup", "setsid"}
# Shell words that start or join a compound command, so the command proper follows them.
KEYWORDS = {"if", "then", "else", "elif", "fi", "do", "done", "while", "until", "!", "case", "esac", "in"}
LAUNCHERS = {"start-job", "start-threadjob", "start-process"}  # PowerShell's, which detach whatever they start
NO_REVIEW = ("--check-pin", "--list-rounds", "--help")  # the dispatcher's only runs that start no reviewer
INTERPRETER = re.compile(r"py(?:thon)?(?:3(?:\.\d+)?)?(?:\.exe)?")
VALUED = {"-X", "-W", "--check-hash-based-pycs"}  # Python options that take the next word as their value


def _parser():
    """The deny list's own tokenizer and wrapper reader (scripts/tool-guard.py), so both hooks read words alike."""
    spec = importlib.util.spec_from_file_location("tool_guard", Path(__file__).with_name("tool-guard.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PARSER = _parser()


def starts_no_reviewer(words: list[str]) -> bool:
    """True when these dispatcher arguments ask only for --check-pin, --list-rounds or --help, as argparse reads them:
    whole, after `=`, or shortened. Any other run is a review, `--prompt=x` and `--pro x` included."""
    for word in words:
        option = word.split("=", 1)[0]
        if word == "-h" or (option.startswith("--") and len(option) > 3 and any(name.startswith(option) for name in NO_REVIEW)):
            return True
    return False


def script_at(words: list[str]) -> int | None:
    """Where the script is in a command's words: the first word when the script runs itself, or the first argument
    after the interpreter's options (each option's value skipped); None for `python -c` and `python -m`."""
    if not INTERPRETER.fullmatch(os.path.basename(words[0]).lower()):
        return 0
    at = 1
    while at < len(words):
        word = words[at]
        if word in ("-c", "-m") or (len(word) > 2 and word[0] == "-" and word[1] in "cm" and word[1:2] != "-"):
            return None
        if word in VALUED:
            at += 2
            continue
        if word.startswith("-"):
            at += 1
            continue
        return at
    return None


CLOSE = {"(": ")", "{": "}"}
COMPOUND = {"if": "fi", "while": "done", "until": "done", "for": "done", "select": "done", "case": "esac"}
WORD = re.compile(r"[A-Za-z_]+")


def compound_end(text: str, at: int) -> int:
    """Where the compound command opened at `at` (`if`, `while`, `until`, `for`, `select`, `case`) ends: just past its
    `fi`, `done` or `esac`, nested ones matched, quotes respected; the end of the text when it never closes."""
    stack, quote, i = [], None, at
    while i < len(text):
        char = text[i]
        if quote:
            quote = None if char == quote else quote
        elif char in "'\"":
            quote = char
        elif (i == 0 or text[i - 1] in " \t\n;&|()") and (found := WORD.match(text, i)):
            word = found.group(0)
            boundary = found.end() == len(text) or text[found.end()] in " \t\n;&|()"
            if boundary and word in COMPOUND:
                stack.append(COMPOUND[word])
            elif boundary and stack and word == stack[-1]:
                stack.pop()
                if not stack:
                    return found.end()
            i = found.end()
            continue
        i += 1
    return len(text)


def group_end(text: str, at: int) -> int:
    """Where the group opened at `at` closes, quotes respected; the end of the text when it never does."""
    opener, closer, depth, quote = text[at], CLOSE[text[at]], 0, None
    for i in range(at, len(text)):
        char = text[i]
        if quote:
            quote = None if char == quote else quote
        elif char in "'\"":
            quote = char
        elif char == opener:
            depth += 1
        elif char == closer:
            depth -= 1
            if depth == 0:
                return i
    return len(text)


def parse(text: str, powershell: bool) -> list[tuple]:
    """(command, backgrounded) for each command in `text`, read as the shell reads it. A list ends at `;`, a newline
    or a background `&`, and that `&` backgrounds every command of the list, `&&`, `||` and pipelines included;
    within a list, `&&`, `||` and `|` separate commands. A group, `( )` or `{ }` where a command starts, is parsed by
    the same rules and its commands join the list around it, so an `&` at any depth marks all it backgrounds. A
    redirect (`2>&1`, `&>`, `>&`) separates nothing, nor does PowerShell's call operator, an `&` where a command
    starts. Quotes are respected throughout."""
    result, listed, current, quote, start, i = [], [], [], None, True, 0

    def finish_command():
        command = "".join(current).strip()
        if command:
            listed.append((command, False))
        current.clear()

    def finish_list(background):
        finish_command()
        result.extend((command, marked or background) for command, marked in listed)
        listed.clear()

    while i < len(text):
        char = text[i]
        if quote:
            current.append(char)
            quote = None if char == quote else quote
        elif char in "'\"":
            quote, start = char, False
            current.append(char)
        elif char == "#" and (i == 0 or text[i - 1].isspace()):
            while i < len(text) and text[i] != "\n":  # a comment: the shell runs none of it
                i += 1
            continue
        elif not "".join(current).strip() and (opener := WORD.match(text, i)) and opener.group(0) in COMPOUND \
                and (opener.end() == len(text) or text[opener.end()] in " \t\n;&|()"):
            # A compound command is one command of its list: an `&` after its `fi`, `done` or `esac` backgrounds all
            # of it. Its body is parsed by the same rules, keywords and all.
            end = compound_end(text, i)
            closer = COMPOUND[opener.group(0)]
            body_end = end - len(closer) if text[:end].endswith(closer) else end
            finish_command()
            body = text[opener.end():body_end]
            if opener.group(0) == "case":
                # `case WORD in PATTERN) commands ;; ...`: the head and each pattern are words, not commands.
                body = re.sub(r"(^|;;)\s*\(?[^;()]*\)", r"\1", body.split(" in ", 1)[-1]).replace(";;", ";")
            listed.extend(parse(body, powershell))
            i, start = end, False
            continue
        elif char in CLOSE and not "".join(current).strip():
            close = group_end(text, i)
            finish_command()
            listed.extend(parse(text[i + 1:close], powershell))
            i, start = close, False
        elif text.startswith(("&&", "||"), i):
            finish_command()
            start = True
            i += 1
        elif char == "|":
            finish_command()
            start = True
        elif char in ";\n":
            finish_list(False)
            start = True
        elif char == "&" and text[i - 1:i] != ">" and text[i + 1:i + 2] != ">":
            if powershell and start:
                current.append(char)  # the call operator: it runs what follows, here and now
            else:
                finish_list(True)
                start = True
        else:
            if not char.isspace():
                start = False
            current.append(char)
        i += 1
    finish_list(False)
    return result


def commands(line: str, powershell: bool) -> list[tuple]:
    """(command, backgrounded) for each command in a line, continuations joined first (a backslash, or PowerShell's
    backtick, before the newline)."""
    return parse(re.sub(r"`\r?\n" if powershell else r"\\\r?\n", " ", line), powershell)


def runs(line: str, powershell: bool, detached: bool = False) -> list[tuple]:
    """(what, detached) for every review and stamp the line really runs, `what` being "review" or "stamp": quoted text
    is never a command, and a wrapper's inner command is read with its own shell's rules and keeps the wrapper's
    detachment."""
    found = []
    for command, background in commands(line, powershell):
        words = PARSER.tokens_of(command)
        here = detached or background
        while words and (words[0] in ("&", ".", "{", "(") or os.path.basename(words[0]).lower() in PARSER.PREFIXES
                         or words[0] in KEYWORDS or re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*=.*", words[0])):
            here = here or os.path.basename(words[0]).lower() in DETACHING_PREFIXES
            words = words[1:]
        if not words:
            continue
        if "disown" in (w.lower() for w in words):
            here = True
        inner = PARSER.inner_command(words)
        if inner and inner[0]:
            found.extend(runs(*inner, here))
            continue
        if powershell and words[0].lower() in LAUNCHERS:
            if any(os.path.basename(w.strip("{}()'\"")).lower() == "review-dispatch.py" or "review-dispatch.py" in w for w in words) \
                    and not starts_no_reviewer(words):
                found.append(("review", True))
            continue
        at = script_at(words)
        if at is None:
            continue
        script, args = os.path.basename(words[at]).lower(), words[at + 1:]
        if script == "review-dispatch.py" and not starts_no_reviewer(args):
            found.append(("review", here))
        elif script == "todo-graph.py" and "stamp" in args:
            found.append(("stamp", here))
    return found


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
    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if tool not in ("Bash", "PowerShell") or not isinstance(command, str):
        return None
    found = runs(without_heredocs(command), tool == "PowerShell")
    if found and pin_dirty(repo):
        return (f"{PIN} has uncommitted changes, and the reviewer pin is the owner's: restore it to HEAD (or commit the "
                f"owner's change) before running review-dispatch.py or todo-graph.py stamp")
    reviews = [detached for what, detached in found if what == "review"]
    if not reviews:
        return None
    if any(reviews):
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
