"""The campaign's deny list, as a PreToolUse hook: it refuses what can't be undone and lets everything else through.

The owner chose bypass mode with a deny list and said caution must not block the run, so this refuses only a forced
or deleting push, deleting or making public a GitHub repository, a recursive delete outside the project, scratch and
temp folders, and sending mail or chat. Each refusal says why, so the session carries on another way. It fails
closed: input it can't read, or an error while deciding a command that might be protected, is refused with a
diagnostic, since under bypass mode nothing else would stop it. `--self-test` runs it on known commands.
"""
from __future__ import annotations

import glob
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


UNKNOWN = None  # a working folder this hook couldn't follow, such as after `cd $X` or `popd`


def resolve_targets(token: str, cwd: str | None) -> list[str]:
    """Every real path a delete target can reach: a glob's matches, and the glob itself with each wildcard part taken
    as a name, so `build/*/../../.git` is read as `.git` whether or not anything matches yet."""
    raw = os.path.expandvars(os.path.expanduser(token.strip("'\"")))
    if re.search(r"\$|%[A-Za-z_]+%|\$\{|\$env:", raw):
        raise Refusal(f"it deletes {token}, which names a variable this hook can't resolve: name the folder itself")
    if cwd is UNKNOWN and not os.path.isabs(windows_path(raw)):
        raise Refusal(f"it deletes {token} after changing to a folder this hook can't follow: delete by full path")
    raw = windows_path(raw)
    full = raw if os.path.isabs(raw) else os.path.join(cwd, raw)
    if not re.search(r"[*?\[]", full):
        return [canonical(full)]
    parts = re.split(r"[\\/]", full)
    named = os.sep.join("x" if re.search(r"[*?\[]", part) else part for part in parts)
    return [canonical(named)] + [canonical(match) for match in glob.glob(full)]


def check_delete_targets(targets: list[str], cwd: str | None, project: str) -> None:
    if not targets:
        return
    project_root = canonical(project)
    git_dir = os.path.join(project_root, os.path.normcase(".git"))
    allowed = [project_root] + [canonical(r) for r in scratch_roots()]
    for token in targets:
        for target in resolve_targets(token, cwd):
            if target == git_dir or inside(target, git_dir):
                raise Refusal(f"it deletes the repository's own history ({token}), which can't be undone")
            if not any(inside(target, root) for root in allowed):
                where = "the project folder itself" if target == project_root else target
                raise Refusal(f"it deletes {where} recursively, outside the project, scratch and temp folders, which can't "
                              f"be undone: delete inside one of those, or leave it")


DOUBLE_QUOTED_ESCAPES = set('$`"\\\n')  # all a backslash escapes inside Bash's double quotes
ANSI_C = {"n": "\n", "t": "\t", "r": "\r", "a": "\a", "b": "\b", "f": "\f", "v": "\v", "e": "\x1b", "E": "\x1b",
          "\\": "\\", "'": "'", '"': '"', "?": "?"}


def ansi_c(text: str, at: int) -> tuple:
    """(the word part, where it ends) for Bash's $'...' starting at `at`, with its escapes read: \\n, \\xHH, \\NNN octal,
    \\uHHHH and the rest."""
    out, i = [], at + 2
    while i < len(text) and text[i] != "'":
        if text[i] == "\\" and i + 1 < len(text):
            following = text[i + 1]
            for prefix, size, base in (("x", 2, 16), ("u", 4, 16), ("U", 8, 16)):
                if following == prefix:
                    digits = re.match(r"[0-9a-fA-F]{1,%d}" % size, text[i + 2:])
                    if digits:
                        out.append(chr(int(digits.group(0), base)))
                        i += 2 + len(digits.group(0))
                        break
            else:
                octal = re.match(r"[0-7]{1,3}", text[i + 1:])
                if octal:
                    out.append(chr(int(octal.group(0), 8)))
                    i += 1 + len(octal.group(0))
                else:
                    out.append(ANSI_C.get(following, "\\" + following))
                    i += 2
            continue
        out.append(text[i])
        i += 1
    return "".join(out).split("\0", 1)[0], i + 1  # Bash ends the value at its first NUL


def tokens_of(text: str, powershell: bool = False) -> list[str]:
    r"""Words exactly as the shell reads them: split on whitespace outside quotes, each quote removed wherever it sits
    in a word (`--opt='a b'` is one word, `--opt=a b`), and escapes as that shell has them. Bash: a backslash outside
    quotes escapes any character (`git p\ush` is `git push`), inside double quotes only `$`, a backtick, `"`, `\` and a
    newline, inside single quotes nothing. PowerShell: the backtick escapes, outside single quotes, and backslashes are
    plain characters. An unclosed quote runs to the end, as the shell would wait for it."""
    escape = "`" if powershell else "\\"
    words, current, quote, quoted, i = [], [], None, False, 0
    while i < len(text):
        char = text[i]
        if char == escape and quote != "'" and i + 1 < len(text):
            following = text[i + 1]
            if powershell or quote is None or following in DOUBLE_QUOTED_ESCAPES:
                current.append(following)  # an escaped character is itself, never a quote or a space
                i += 2
                continue
        if not powershell and quote is None and char == "$" and text[i + 1:i + 2] in ("'", '"'):
            if text[i + 1] == "'":
                part, i = ansi_c(text, i)  # $'...', ANSI-C quoting: its escapes, read
                current.append(part)
            else:
                quote, i = '"', i + 2  # $"...", a locale string, reads as a double-quoted one
            quoted = True
            continue
        i += 1
        if quote:
            if char == quote:
                quote = None
            else:
                current.append(char)
        elif char in "'\"":
            quote, quoted = char, True
        elif char.isspace():
            if current or quoted:
                words.append("".join(current))
            current, quoted = [], False
        else:
            current.append(char)
    if current or quoted:
        words.append("".join(current))
    return words


def split_chain(command: str, powershell: bool = False) -> list[str]:
    """A command line split on `;`, `&&`, `||`, `|`, a single background `&` and newlines, but never inside quotes, so
    a wrapper's quoted command stays whole; `2>&1`, `&>` and `>&` are redirects, not separators."""
    parts, current, quote, i = [], [], None, 0
    while i < len(command):
        char = command[i]
        if char == ("`" if powershell else "\\") and quote != "'" and i + 1 < len(command):
            current.extend(command[i:i + 2])  # an escaped character is never a quote, a separator or a bracket
            i += 2
            continue
        if quote:
            current.append(char)
            if char == quote:
                quote = None
        elif char in "'\"":
            quote = char
            current.append(char)
        elif command.startswith(("&&", "||"), i):
            parts.append("".join(current))
            current = []
            i += 1
        elif char == "&" and command[i - 1:i] != ">" and command[i + 1:i + 2] != ">":
            parts.append("".join(current))  # a background `&`, or PowerShell's call operator: either way a new command
            current = []
        elif char in ";|\n":
            parts.append("".join(current))
            current = []
        else:
            current.append(char)
        i += 1
    parts.append("".join(current))
    return [p.strip() for p in parts if p.strip()]


# Commands that run the rest of their line as a command of its own.
PREFIXES = {"env", "command", "builtin", "exec", "nohup", "setsid", "time", "timeout", "sudo", "nice", "stdbuf", "chronic"}


# A prefix's options that take the next word as their value, and prefixes whose first plain word is theirs too.
PREFIX_VALUES = {"env": {"-u", "--unset", "-C", "--chdir", "-S", "--split-string"}, "nice": {"-n", "--adjustment"},
                 "sudo": {"-u", "--user", "-g", "--group", "-h", "--host", "-C", "--close-from", "-p", "--prompt",
                          "-r", "--role", "-t", "--type", "-D", "--chdir", "-R", "--chroot", "-T", "--command-timeout"},
                 "stdbuf": {"-i", "--input", "-o", "--output", "-e", "--error"},
                 "timeout": {"-s", "--signal", "-k", "--kill-after"},
                 "exec": {"-a"},  # bash: exec -a NAME command
                 "time": {"-f", "--format", "-o", "--output"}}  # GNU time; bash's own takes only -p
PREFIX_POSITIONALS = {"timeout": 1}  # `timeout DURATION command`
# Each prefix's long options, so a shortening GNU accepts (`--split-str`, `--chd`) is read as the option it names.
LONG_OPTIONS = {
    "env": ("--unset", "--chdir", "--split-string", "--ignore-environment", "--null", "--debug", "--default-signal",
            "--ignore-signal", "--block-signal", "--list-signal-handling", "--help", "--version"),
    "nice": ("--adjustment", "--help", "--version"),
    "timeout": ("--signal", "--kill-after", "--foreground", "--preserve-status", "--verbose", "--help", "--version"),
    "stdbuf": ("--input", "--output", "--error", "--help", "--version"),
    "time": ("--format", "--output", "--append", "--portability", "--verbose", "--quiet", "--help", "--version"),
    "sudo": ("--user", "--group", "--host", "--prompt", "--role", "--type", "--close-from", "--chdir", "--chroot",
             "--command-timeout", "--login", "--shell", "--preserve-env", "--set-home", "--non-interactive",
             "--background", "--help", "--version"),
}


def long_option(head: str, option: str) -> str:
    """A long option as its prefix names it in full: GNU accepts any shortening that only one option starts with."""
    names = LONG_OPTIONS.get(head, ())
    if option in names:
        return option
    matches = [name for name in names if name.startswith(option)]
    return matches[0] if len(matches) == 1 else option


# Options that run the prefix's command in another folder: env -C DIR, sudo -D DIR; and sudo -i, which runs it in the
# target user's home, a folder this hook can't name.
CHDIR = {"env": {"-C", "--chdir"}, "sudo": {"-D", "--chdir"}}
HOME_FOLDER = {"sudo": {"-i", "--login"}}
SOMEWHERE = "\0somewhere"  # a folder that can't be named: every relative delete after it is refused


def within(outer: str | None, inner: str | None) -> str | None:
    """The folder `inner` names when it is reached from `outer`: an absolute one stands alone, a relative one is taken
    from `outer`, as env and each shell it starts do."""
    if not outer or not inner:
        return inner or outer
    return inner if os.path.isabs(windows_path(inner)) else os.path.join(outer, inner)


def strip_prefixes(words: list[str]) -> tuple:
    """(the command's own words, the prefixes it was run through, the folder a prefix runs it in or None):
    PowerShell's call and dot-source operators, an opening bracket, `VAR=value`, and prefixes that run the rest of the
    line, each with its options and their values. The folder matters: `env -C DIR rm -r x` deletes DIR/x."""
    seen, chdir = [], None
    while words:
        head = os.path.basename(words[0]).lower().removesuffix(".exe")  # env.exe is env
        if words[0] in ("&", ".", "{", "(") or re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*=.*", words[0]):
            words = words[1:]
            continue
        if head not in PREFIXES:
            break
        seen.append(head)
        words, valued, plain = words[1:], PREFIX_VALUES.get(head, set()), PREFIX_POSITIONALS.get(head, 0)
        while words and (words[0].startswith("-") or plain or re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*=.*", words[0])):
            if words[0] == "--":
                # Options end here, but the prefix's own positionals still come first: `timeout -- 60 command`.
                words = words[1 + plain:]
                break
            if words[0].startswith("-"):
                option, equals, value = words[0].partition("=")
                attached = bool(equals)  # `--opt=` gives an empty value; only a bare `--opt` takes the next word
                flags = ""  # a short cluster's flag letters, before any that takes a value
                if words[0].startswith("--") and len(option) > 2:
                    option = long_option(head, option)
                elif not words[0].startswith("--"):
                    # A cluster of short options, as getopt reads it: flags until the first that takes a value, which
                    # takes the rest of the cluster or, when nothing is left, the next word. -vS 'cmd', -iCDIR, -n5.
                    option, value, flags = words[0], "", words[0][1:]
                    for at in range(1, len(words[0])):
                        if "-" + words[0][at] in valued:
                            option, value, flags = "-" + words[0][at], words[0][at + 1:], words[0][1:at]
                            attached = bool(value)
                            break
                if option in CHDIR.get(head, set()):
                    folder = value if attached else (words[1] if len(words) > 1 else None)
                    chdir = within(chdir, folder)  # each runs the next in its folder
                if option in HOME_FOLDER.get(head, set()) or any("-" + letter in HOME_FOLDER.get(head, set()) for letter in flags):
                    chdir = SOMEWHERE
                if head == "env" and option in ("-S", "--split-string"):
                    # env -S splits its string into the command it runs, so that string is the command.
                    text = value if attached else (words[1] if len(words) > 1 else "")
                    text = text[1:-1] if len(text) > 1 and text[0] == text[-1] and text[0] in "'\"" else text
                    if "\\" in text or "$" in text:
                        # GNU env -S has escapes (\\_ is a space, \\c ends it) and ${VAR} of its own; not modelled, so
                        # unread, which the deny list refuses rather than lets through.
                        raise Refusal("its env -S string uses escapes or variables this hook can't read: run the command "
                                      "directly instead")
                    words = tokens_of(text) + (words[1:] if attached else words[2:])
                    continue
                words = words[2:] if option in valued and not attached else words[1:]
            elif plain:
                words, plain = words[1:], plain - 1
            else:
                words = words[1:]
    return words, seen, chdir


def inner_command(words: list[str]) -> tuple | None:
    """(the command a wrapper runs, whether it is PowerShell) for `bash -c` (and `-lc`), `pwsh -Command`, `cmd /c` and
    `Invoke-Expression`, or None."""
    if not words:
        return None
    lowered = [w.lower() for w in words]
    name = os.path.basename(lowered[0]).removesuffix(".exe")
    if name in ("bash", "sh", "zsh", "dash"):
        for at, word in enumerate(lowered[1:], 1):
            if re.fullmatch(r"-[a-z]*c[a-z]*", word):  # -c, -lc, -ic, -xc
                return (words[at + 1], False) if at + 1 < len(words) else None
        return None
    if name in ("pwsh", "powershell"):
        for at, word in enumerate(lowered[1:], 1):
            if word in ("-command", "-c"):
                return (" ".join(words[at + 1:]), True) if at + 1 < len(words) else None
        return None
    if name == "cmd" and "/c" in lowered[1:]:
        at = lowered.index("/c") + 1
        return (" ".join(words[at:]), False) if at < len(words) else None
    if name in ("invoke-expression", "iex"):
        return (" ".join(w for w in words[1:] if w.lower() != "-command"), True) if len(words) > 1 else None
    return None


def segments(command: str, powershell: bool = False) -> list[tuple]:
    """(the command's words, whether it is PowerShell, the folder a prefix runs it in or None) for each command in a
    line, in order, with subshells, `$( )` and each wrapper's inner commands in their place, a wrapper's folder passed
    on. Words, never text joined again, so quoting is read once and nothing is lost re-reading it."""
    found = []
    for inner in re.findall(r"\$\(([^()]*)\)|`([^`]*)`", command):  # substitutions run first
        found.extend(segments(inner[0] or inner[1], powershell))
    for part in split_chain(command, powershell):
        part = part.strip()
        while part.startswith("(") and part.endswith(")"):
            part = part[1:-1].strip()
        words, _, chdir = strip_prefixes(tokens_of(part.lstrip("("), powershell))
        inner = inner_command(words)
        if inner and inner[0]:
            found.extend((text, shell, within(chdir, folder)) for text, shell, folder in segments(*inner))
        else:
            found.append((words, powershell, chdir))
    return found


def check_git(words: list[str]) -> None:
    rest = words[1:]
    while rest and rest[0].startswith("-"):  # git -C path, -c key=value, --git-dir dir, --no-pager
        rest = rest[2:] if rest[0] in ("-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--super-prefix") else rest[1:]
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


def delete_targets(words: list[str], powershell: bool = False) -> list[str] | None:
    """The targets of a recursive delete, or None when the command isn't one. PowerShell's `a,b` is two targets."""
    name = os.path.basename(words[0]).lower().removesuffix(".exe")
    rest = words[1:]
    lowered = [w.lower() for w in rest]
    if name == "rm":
        flags = [w for w in rest if w.startswith("-")]
        recursive = any(w in ("--recursive", "-recurse") or (re.fullmatch(r"-[a-zA-Z]+", w) and set(w[1:].lower()) & {"r"})
                        for w in flags) or any(w.startswith("-rec") for w in lowered)
        if not recursive:
            return None
        targets = [w for w in rest if not w.startswith("-")]
        return [t for w in targets for t in w.split(",") if t] if powershell or any(w.startswith("-rec") for w in lowered) else targets
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
                bound = re.fullmatch(r"(?i)-(?:path|literalpath):(.+)", w)
                if bound:
                    out.extend(t for t in bound.group(1).split(",") if t)
                    continue
                if w.startswith("-"):
                    skip = w.lower().startswith(("-filter", "-include", "-exclude"))
                    continue
                out.extend(t for t in w.split(",") if t)
            return out
    return None


CHANGE_DIR = {"cd", "chdir", "pushd", "set-location", "sl", "push-location"}


def follow_cd(words: list[str], cwd: str | None) -> str | None:
    """The working folder after a `cd`, `Set-Location` or `pushd`, or UNKNOWN when it can't be told."""
    targets = [w for w in words[1:] if not w.startswith("-") or w == "-"]
    if not targets:
        return os.path.expanduser("~")
    target = os.path.expandvars(os.path.expanduser(targets[0].strip("'\"")))
    if target == "-" or re.search(r"\$|%[A-Za-z_]+%", target) or cwd is UNKNOWN and not os.path.isabs(windows_path(target)):
        return UNKNOWN
    target = windows_path(target)
    folder = os.path.realpath(target if os.path.isabs(target) else os.path.join(cwd, target))
    return folder if os.path.isdir(folder) else UNKNOWN  # a failed cd leaves the shell where it was


def joined(command: str, powershell: bool) -> str:
    """The line with its continuations joined, as the shell joins them: Bash's backslash and PowerShell's backtick
    before a newline."""
    return re.sub(r"`\r?\n" if powershell else r"\\\r?\n", "", command)


def decide_command(command: str, cwd: str, project: str, powershell: bool = False) -> None:
    command = joined(command, powershell)
    here: str | None = cwd
    parts = segments(command, powershell)
    # A line that changes folder anywhere (cd, pushd, Set-Location, env -C, in a wrapper or not) can only delete
    # recursively by full path: where each shell scope stands is more than a hook should simulate, and a full path
    # costs the session nothing.
    moves = any(chdir or os.path.basename((words or [""])[0]).lower().removesuffix(".exe") in CHANGE_DIR | {"popd", "pop-location"}
                for words, _, chdir in parts)
    for words, in_powershell, chdir in parts:
        if not words:
            continue
        head = os.path.basename(words[0]).lower().removesuffix(".exe")
        if head in CHANGE_DIR:
            here = follow_cd(words, here)
            continue
        if head in ("popd", "pop-location"):
            here = UNKNOWN
            continue
        if head == "git":
            check_git(words)
        elif head == "gh":
            check_gh(words)
        targets = delete_targets(words, in_powershell)
        if targets is not None and moves:
            for token in targets:
                if not os.path.isabs(windows_path(os.path.expanduser(token.strip("'\"")))):
                    raise Refusal(f"this line changes folder, so a recursive delete in it must name its target by full "
                                  f"path, not {token}")
        if targets is not None:
            # `env -C DIR` runs this one command in DIR, so its targets are read from there.
            check_delete_targets(targets, follow_cd(["cd", chdir], here) if chdir else here, project)


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
            decide_command(command, str(data.get("cwd") or os.getcwd()), os.environ.get("CLAUDE_PROJECT_DIR") or str(ROOT),
                           powershell=tool == "PowerShell")
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
