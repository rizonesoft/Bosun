"""The campaign's deny list, as a PreToolUse hook: it refuses what can't be undone and lets everything else through.

The owner chose bypass mode with a deny list and said caution must not block the run, so this refuses only a forced
or deleting push, deleting or making public a GitHub repository, a recursive delete outside the project, scratch and
temp folders, and sending mail or chat. Each refusal says why, so the session carries on another way. It fails
closed: input it can't read, or an error while deciding a command that might be protected, is refused with a
diagnostic, since under bypass mode nothing else would stop it. `--self-test` runs it on known commands.
"""
from __future__ import annotations

import json
import os
import re
import shlex
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SELF_TEST = "python scripts/tool-guard.py --self-test"
SHELL_TOOLS = {"Bash", "PowerShell"}
# Words that mark a command as one this hook decides; an error while deciding one of these refuses it.
PROTECTED_WORDS = re.compile(r"(?i)\b(push|gh|rm|rmdir|rd|del|erase|remove-item|ri|rimraf)\b")
FORCE_FLAGS = {"-f", "--force", "--mirror", "--delete", "-d", "--prune"}
FORCE_PREFIXES = ("--force-with-lease", "--force-if-includes")
DELETE_ALIASES = {"remove-item", "ri", "rm", "rmdir", "del", "erase", "rd"}
SEND = re.compile(r"(?i)(^|_)(send|forward)(_|$)|(^|_)reply$|^teams_reply_channel_message$")
MAIL_SERVERS = re.compile(r"(?i)(gmail|outlook|microsoft_365|teams|slack)")


class Refusal(Exception):
    """A command this hook refuses, with the reason the session sees."""


def scratch_roots() -> list[str]:
    roots = [tempfile.gettempdir(), os.environ.get("TEMP", ""), os.environ.get("TMP", ""), os.environ.get("TMPDIR", "")]
    roots += [r"R:\scratch", "/mnt/r/scratch"] if os.name == "nt" or Path("/mnt/r").exists() else []
    return [r for r in roots if r]


def canonical(path: str) -> str:
    """The real path, links and junctions followed, compared as the platform does (without case on Windows)."""
    return os.path.normcase(os.path.realpath(path))


def inside(path: str, root: str) -> bool:
    try:
        return os.path.commonpath([path, root]) == root and path != root
    except ValueError:  # another drive
        return False


def windows_path(token: str) -> str:
    """A Git Bash or WSL spelling of a Windows path, as Windows spells it: /d/x and /mnt/d/x are D:\\x."""
    if os.name != "nt":
        return token
    found = re.match(r"^/(?:mnt/)?([A-Za-z])(/.*|$)", token)
    return f"{found.group(1).upper()}:{found.group(2) or '/'}" if found else token


def resolve_target(token: str, cwd: str) -> str:
    raw = os.path.expandvars(os.path.expanduser(token.strip("'\"")))
    if re.search(r"\$|%[A-Za-z_]+%|\$\{|\$env:", raw):
        raise Refusal(f"it deletes {token}, which names a variable this hook can't resolve: name the folder itself")
    raw = windows_path(raw)
    raw = re.split(r"[*?\[]", raw, maxsplit=1)[0] or "."  # a glob's fixed part is the folder it reaches into
    return canonical(raw if os.path.isabs(raw) else os.path.join(cwd, raw))


def check_delete_targets(targets: list[str], cwd: str, project: str) -> None:
    if not targets:
        return
    project_root = canonical(project)
    git_dir = os.path.join(project_root, os.path.normcase(".git"))
    allowed = [project_root] + [canonical(r) for r in scratch_roots()]
    for token in targets:
        target = resolve_target(token, cwd)
        if target == git_dir or inside(target, git_dir):
            raise Refusal(f"it deletes the repository's own history ({token}), which can't be undone")
        if not any(inside(target, root) for root in allowed):
            where = "the project folder itself" if target == project_root else target
            raise Refusal(f"it deletes {where} recursively, outside the project, scratch and temp folders, which can't be "
                          f"undone: delete inside one of those, or leave it")


def tokens_of(text: str) -> list[str]:
    """Words as a shell reads them, but with backslashes kept: a Windows path's separators are never escapes."""
    try:
        words = shlex.split(text, posix=False)
    except ValueError:
        words = text.split()
    return [w[1:-1] if len(w) > 1 and w[0] == w[-1] and w[0] in "'\"" else w for w in words]


def segments(command: str) -> list[str]:
    """The simple commands in a command line, chains split, and wrappers' inner commands included."""
    parts = [p.strip() for p in re.split(r"\|\||&&|[;|\n]", command) if p.strip()]
    found = []
    for part in parts:
        found.append(part)
        words = tokens_of(part)
        lowered = [w.lower() for w in words]
        for i, word in enumerate(lowered):
            name = os.path.basename(word).removesuffix(".exe")
            inner = None
            if name in ("bash", "sh", "zsh") and "-c" in lowered[i + 1:]:
                inner = words[lowered.index("-c", i + 1) + 1] if lowered.index("-c", i + 1) + 1 < len(words) else None
            elif name in ("pwsh", "powershell"):
                for flag in ("-command", "-c"):
                    if flag in lowered[i + 1:]:
                        at = lowered.index(flag, i + 1) + 1
                        inner = " ".join(words[at:]) if at < len(words) else None
                        break
            elif name == "cmd" and "/c" in lowered[i + 1:]:
                at = lowered.index("/c", i + 1) + 1
                inner = " ".join(words[at:]) if at < len(words) else None
            if inner:
                found.extend(segments(inner))
                break
    return found


def check_git(words: list[str]) -> None:
    rest = words[1:]
    while rest and rest[0].startswith("-"):  # git -C path, -c key=value
        rest = rest[2:] if rest[0] in ("-C", "-c") else rest[1:]
    if not rest or rest[0] != "push":
        return
    for arg in rest[1:]:
        if arg in FORCE_FLAGS or arg.startswith(FORCE_PREFIXES) or (re.fullmatch(r"-[a-zA-Z]+", arg) and set(arg[1:]) & {"f", "d"}):
            raise Refusal(f"`git push {arg}` rewrites or deletes what the remote holds, which can't be undone: push "
                          f"without it, or open a new branch")
        if not arg.startswith("-") and (arg.startswith("+") or arg.startswith(":")):
            raise Refusal(f"the refspec `{arg}` force-pushes or deletes a remote branch, which can't be undone: push "
                          f"without the + or :")


def check_gh(words: list[str]) -> None:
    lowered = [w.lower() for w in words]
    if lowered[1:3] == ["repo", "delete"]:
        raise Refusal("`gh repo delete` deletes the repository, which can't be undone")
    if lowered[1:3] == ["repo", "edit"] and any(w.startswith("--visibility") for w in lowered):
        raise Refusal("changing a repository's visibility can publish it, which can't be undone: the owner decides that")
    if len(lowered) > 1 and lowered[1] == "api":
        joined = " ".join(lowered)
        if re.search(r"visibility\s*=|\"visibility\"|\bprivate\s*=\s*false", joined):
            raise Refusal("changing a repository's visibility can publish it, which can't be undone: the owner decides that")
        if re.search(r"(-x|--method)\s*delete", joined) and re.search(r"\brepos/[^/\s]+/[^/\s]+/?(\s|$)", joined):
            raise Refusal("deleting a repository through the API can't be undone")


def delete_targets(words: list[str]) -> list[str] | None:
    """The targets of a recursive delete, or None when the command isn't one."""
    name = os.path.basename(words[0]).lower().removesuffix(".exe")
    rest = words[1:]
    lowered = [w.lower() for w in rest]
    if name == "rm":
        flags = [w for w in rest if w.startswith("-")]
        recursive = any(w in ("--recursive", "-recurse") or (re.fullmatch(r"-[a-zA-Z]+", w) and set(w[1:].lower()) & {"r"})
                        for w in flags) or any(w.startswith("-rec") for w in lowered)
        if not recursive:
            return None
        return [w for w in rest if not w.startswith("-")]
    if name in DELETE_ALIASES:
        if name in ("rd", "rmdir", "del", "erase") and "/s" in lowered:
            return [w for w in rest if not w.startswith("/")]
        if any(w.startswith("-rec") for w in lowered):  # Remove-Item -Recurse, and its shortenings
            out, skip = [], False
            for w in rest:
                if skip:
                    skip = False
                    continue
                if w.lower() in ("-path", "-literalpath"):
                    continue
                if w.startswith("-"):
                    skip = w.lower().startswith(("-filter", "-include", "-exclude"))
                    continue
                out.append(w.rstrip(","))
            return out
    return None


def decide_command(command: str, cwd: str, project: str) -> None:
    for part in segments(command):
        words = tokens_of(part)
        while words and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*=.*", words[0]):  # VAR=value before a command
            words = words[1:]
        if not words:
            continue
        head = os.path.basename(words[0]).lower().removesuffix(".exe")
        if head == "git":
            check_git(words)
        elif head == "gh":
            check_gh(words)
        targets = delete_targets(words)
        if targets is not None:
            check_delete_targets(targets, cwd, project)


def decide_tool(name: str) -> None:
    if not name.startswith("mcp__"):
        return
    server, _, action = name[len("mcp__"):].partition("__")
    if MAIL_SERVERS.search(server) and SEND.search(action):
        raise Refusal(f"{name} sends mail or a chat message, which can't be taken back: draft it instead, and the "
                      f"owner sends it")


def decide(data: dict) -> str | None:
    """The reason to refuse this tool call, or None to let it through."""
    tool = str(data.get("tool_name") or "")
    tool_input = data.get("tool_input") or {}
    try:
        if tool in SHELL_TOOLS:
            command = str(tool_input.get("command") or "")
            decide_command(command, str(data.get("cwd") or os.getcwd()), os.environ.get("CLAUDE_PROJECT_DIR") or str(ROOT))
        else:
            decide_tool(tool)
    except Refusal as refusal:
        return f"Refused by the campaign's deny list: {refusal}"
    except Exception as error:  # fail closed on anything that might be protected
        text = json.dumps(tool_input)
        if tool in SHELL_TOOLS and PROTECTED_WORDS.search(text) or tool.startswith("mcp__"):
            return (f"Refused: the campaign's deny list couldn't decide this ({type(error).__name__}: {error}). "
                    f"Run `{SELF_TEST}` to check it, or rephrase the command more simply")
    return None


def deny(reason: str) -> str:
    return json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny",
                                              "permissionDecisionReason": reason}})


def hook(raw: str) -> str:
    try:
        data = json.loads(raw or "")
        if not isinstance(data, dict):
            raise ValueError("not an object")
    except ValueError as error:
        return deny(f"Refused: the campaign's deny list couldn't read its hook input ({error}), so it can't tell what "
                    f"this does. Run `{SELF_TEST}` to check it")
    reason = decide(data)
    return deny(reason) if reason else ""


SELF_CASES = [
    ("Bash", "git push --force origin main", True), ("Bash", "git push origin main", False),
    ("Bash", "echo ok && git push -f", True), ("PowerShell", "pwsh -Command \"git push origin :old\"", True),
    ("Bash", "gh repo delete me/x --yes", True), ("Bash", "rm -rf /", True), ("Bash", "rm -rf build", False),
    ("mcp__claude_ai_Gmail__send_message", None, True), ("mcp__claude_ai_Gmail__create_draft", None, False),
]


def self_test() -> int:
    failed = 0
    for tool, command, refused in SELF_CASES:
        here = os.environ.get("CLAUDE_PROJECT_DIR") or str(ROOT)  # relative cases resolve inside the project in use
        data = {"tool_name": tool, "tool_input": {"command": command} if command else {}, "cwd": here}
        got = bool(hook(json.dumps(data)))
        ok = got == refused
        failed += not ok
        print(f"{'ok  ' if ok else 'FAIL'} {'refused' if refused else 'allowed'}: {command or tool}")
    print("self-test passed" if not failed else f"self-test failed: {failed}")
    return 1 if failed else 0


def main() -> int:
    if sys.argv[1:] == ["--self-test"]:
        return self_test()
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8")
    out = hook(sys.stdin.buffer.read().decode("utf-8", "replace"))
    if out:
        print(out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
