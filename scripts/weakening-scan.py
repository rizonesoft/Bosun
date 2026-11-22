#!/usr/bin/env python3
"""Name the hunks of a diff that can weaken what the tests prove, so each one is answered and the reviewer sees it.

    python scripts/weakening-scan.py                              # commits since origin/main, then what is not committed
    python scripts/weakening-scan.py --base <rev>                 # commits since <rev>, then what is not committed
    python scripts/weakening-scan.py --base <rev> --head <rev>    # exactly that committed range, nothing else

A passing suite cannot tell a correct change from one that deleted the test, switched it off, dropped its assertion or
swallowed the error it checked. This reads a diff and reports each hunk of those shapes as `<kind> <path>:<id>`:

Line patterns are read in code only (scripts, sources, workflows, build files), never in prose that mentions them.

    test-file-deleted    a test file is gone, and its cases did not all move elsewhere
    test-removed         a case is gone: a Python `def test_` (named Class.test_x), a PowerShell `Check '...'` line
                         (named by its text), or a row of the groups[] table in a C++ test (named by its group)
    test-disabled        a groups[] row whose function became nullptr
    assertion-removed    more assertion lines left a file than came into it (self.assert..., assert, CHECK(...)),
                         or a PowerShell check kept by name lost part of its condition (fewer `-and` terms in the
                         file than before, or a condition that became a bare $true)
    skip-added           skipTest, unittest.skip, continue-on-error, or a -Skip switch on a test in the gate or CI
    ci-step-removed      a `run:` line gone from a workflow
    warning-relaxed      warnings-as-errors or /W4 taken out or turned off, or a noqa, type: ignore, pyright:
                         ignore, file-level pyright mode or rule below an error or warning pragma put in, or any
                         change to pyrightconfig.json (each distinct edit its own signal)
    error-swallowed      an `except` whose body is only `pass` or `...`, a `catch` whose braces hold nothing or
                         only a comment, or a `contextlib.suppress` put in; whether the handler is new or an
                         existing one whose body was emptied, by a replacement or by deleting it

The id is the case's name where the signal is about a case, and `L<line>#<fp>` otherwise (the old line for something
removed, the new line for something added). The fingerprint is the first 6 hex of the SHA-1 of the flagged line and
the next non-blank line, whitespace normalised, so two signals in one file are two things to answer, and an answer
still finds its signal when a later edit only moves the line. An answer matches by kind, path and fingerprint with
the line number ignored; an answer giving only `L<line>` matches nothing, since it would cover whatever later stood
on that line. Signals that share a fingerprint in one file need one answer each.

A signal is not a verdict. Each is either undone, or answered in the body of a commit in the scanned range:

    Weakening scan: <kind> <path>:<id> -- <why this is right>

One answer covers one signal, and an answer with no reason does not count. The report lists every signal with its
answer, answered or not, so the reader sees what was removed and why. What is committed is enforced. What is not
committed yet is listed under its own heading and never fails the scan: its answer belongs in the commit body, which
does not exist yet.

A test that moved is not a removal: a case removed in one place and added once elsewhere under the same qualified
name is matched to it, and the assertions that went with it are weighed against those that came with it. A case
replaced in the same file by another (a rename, a rewrite) is not one either; one that simply goes is.

In its own file and its own test the scan does not read the line patterns, which both hold as text; a test deleted,
a case removed, an assertion lost or a real skip (found by parsing the file) is reported there as anywhere.

Exit 0 when every committed signal is answered, 1 when one is not, 2 with the reason when the range or a file in it
cannot be read: a diff git could not produce is never read as an empty one. Stdlib only.
"""
import argparse
import ast
from collections import Counter, defaultdict
import hashlib
import io
import re
import subprocess
import sys
import tokenize
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SELF = ("scripts/weakening-scan.py", "scripts/tests/test_weakening_scan.py")

PY_TEST = re.compile(r"(^|/)(test_[^/]*|[^/]*_test)\.py$")
PS_TEST = re.compile(r"(^|/)tests/[^/]*_test\.ps1$")
CPP_TEST = re.compile(r"(^|/)tests/[^/]*\.(cpp|cc|cxx)$")
WORKFLOW = re.compile(r"^\.github/workflows/[^/]+\.ya?ml$")
CODE = re.compile(r"\.(py|ps1|psm1|sh|mjs|js|cpp|cc|cxx|h|hpp|cs|ya?ml|cmake|props|targets|csproj|toml)$|(^|/)CMakeLists\.txt$"
                  r"|(^|/)CMakePresets\.json$|(^|/)scripts/hooks/[^/]+$|(^|/)wsl/bosun$", re.IGNORECASE)
GATE = re.compile(r"^(scripts/check-all\.ps1|\.github/workflows/[^/]+\.ya?ml)$")

PY_CASE = re.compile(r"^(\s*)(?:async\s+)?def (test_\w+)\s*\(")
PY_CLASS = re.compile(r"^(\s*)class (\w+)")
PS_CASE = re.compile(r"^\s*Check\b")
PS_NAME_FIRST = re.compile(r"""^\s*Check\s+(['"])(.+?)\1""")
PS_NAME_LAST = re.compile(r"""(['"])((?:(?!\1).)+)\1\s*(?:\)|$)""")
CPP_ROW = re.compile(r"""^\s*\{\s*"(\w+)"\s*,\s*(\w+)\s*,""")
PS_TERM = re.compile(r"\s-and\b", re.IGNORECASE)
PS_TRIVIAL = re.compile(r"\(\s*\$true\s*\)", re.IGNORECASE)
ASSERTION = re.compile(r"\bself\.assert\w*\s*\(|^\s*assert\s|\bCHECK\s*\(")
# A skip in Python (unittest), a CI step allowed to fail, and an xunit skip in C#: Assert.Skip, SkipWhen and SkipUnless
# called in a test, or a Skip = "..." on its attribute (2026-11-19: nine Assert.Skip calls came in unseen).
SKIP = re.compile(r"\bskipTest\s*\(|\bunittest\.skip|^\s*@skip\b|continue-on-error:\s*true"
                  r"|\bAssert\.Skip(?:When|Unless)?\s*\(|\[\s*(?:Fact|Theory)\s*\([^)]*\bSkip\s*=", re.IGNORECASE)
SKIP_SWITCH = re.compile(r"_test\.ps1\b.*\s-Skip\w*", re.IGNORECASE)
RUN_STEP = re.compile(r"^\s*(?:-\s*)?run:\s*\S")
STRICT = ("CMAKE_COMPILE_WARNING_AS_ERROR", "/W4", "TreatWarningsAsErrors")
# A pyright directive lowers checking when it is ignore, or names basic or off or a rule below an error anywhere in
# its comma-separated list (round 3 of D00 T07 §2: only the first rule was read; its completion review: only the first
# mode). A comment in Python is read by the tokenizer; a compiler flag is read on the line wherever it is, a command
# string in a Python build script included (FLAG, its completion review).
RELAX_COMMENT = re.compile(r"#\s*noqa\b|#\s*type:\s*ignore|#\s*pyright:\s*ignore\b"
                           r"|#\s*pyright:[^#\n]*?(?<![\w=])(basic|off)\b"
                           r"|#\s*pyright:[^#\n]*\breport\w+\s*=\s*(false|none|information|warning)\b")
FLAG = re.compile(r"#pragma\s+warning\s*\(\s*disable|\s-Wno-\w+")
RELAX = re.compile(f"{RELAX_COMMENT.pattern}|{FLAG.pattern}")
OFF = re.compile(r"""^\W*(OFF|FALSE|NO|0)\b""", re.IGNORECASE)
EXCEPT = re.compile(r"^\s*except\b.*:\s*(#.*)?$")
EXCEPT_INLINE = re.compile(r"^\s*except\b.*:\s*(pass|\.\.\.)\s*(#.*)?$")
SUPPRESS = re.compile(r"\bcontextlib\.suppress\s*\(|\bwith\s+suppress\s*\(")
HASH_COMMENT = re.compile(r"#.*$")
SLASH_COMMENT = re.compile(r"//.*$")
PASS = re.compile(r"^\s*(pass|\.\.\.)\s*(#.*)?$")
EMPTY_CATCH = re.compile(r"\bcatch\b\s*(\([^)]*\)|\[[^\]]*\])?\s*\{\s*\}")
ANSWER = re.compile(r"^Weakening scan: (.+?) -- (\S.*?)\s*$")
LINE_ID = re.compile(r"^(\S+) (.+):L(\d+)(?:#([0-9a-f]{6}))?$")  # a line signal's name: kind, path, line, fingerprint
HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")


class CannotRead(Exception):
    """The range, or a file in it, could not be read (exit 2)."""


CHECKER_CONFIG = "pyrightconfig.json"  # its signals are matched by fingerprint alone (see apply_answers)


def line_id(lines, number):
    """`L<n>#<fp>` for a line signal: its line number, and a fingerprint of the code it flags (the first 6 hex of the
    SHA-1 of the whitespace-normalised flagged line plus the next non-blank line), so an answer still finds the signal
    after an edit above it moves the line. Plain `L<n>` when the line is not in the text."""
    if not lines or not 1 <= number <= len(lines):
        return f"L{number}"
    text = " ".join(lines[number - 1].split())
    for below in lines[number:]:
        if below.strip():
            text += "\n" + " ".join(below.split())
            break
    return f"L{number}#{hashlib.sha1(text.encode('utf-8')).hexdigest()[:6]}"


class Signal:
    def __init__(self, kind, path, ident):
        self.kind, self.path, self.ident = kind, path, ident
        self.reason = None

    @property
    def key(self):
        return self.kind, self.path, self.ident

    def __str__(self):
        return f"{self.kind} {self.path}:{self.ident}"


class Change:
    """One file's side of a diff: the lines that left it, the lines that came in, and its text before and after."""

    def __init__(self, path):
        self.path = path
        self.removed, self.added = [], []  # [(line number, text)]
        self.gaps = set()  # new line numbers after which lines were deleted with nothing put in their place
        self.old, self.new = None, None  # the whole file, as lists of lines; None when it did not exist
        self.deleted = False  # the change removes the file: it has no new text
        self.created = False  # the change adds the file: it has no old text


def git_result(root, arguments):
    """(exit code, output, what it said on failure) of one git command. CannotRead only when git could not run."""
    try:
        done = subprocess.run(["git", "-C", str(root), "-c", "core.quotepath=false", *arguments], capture_output=True,
                              text=True, encoding="utf-8", errors="replace", timeout=120)
    except (OSError, subprocess.SubprocessError) as exc:
        raise CannotRead(f"git could not run: {exc}") from None
    said = (done.stderr or done.stdout).strip().splitlines()
    return done.returncode, done.stdout, said[0] if said else "no output"


def run_git(root, arguments):
    code, out, said = git_result(root, arguments)
    if code != 0:
        raise CannotRead(f"git {' '.join(arguments[:3])} failed: {said}")
    return out


def parse_diff(text):
    """{path: Change} from `git diff -U0 --no-renames` output."""
    changes, current, old_line, new_line, created = {}, None, 0, 0, False
    for line in text.splitlines():
        if line.startswith("diff --git "):
            current, created = None, False
        elif line.startswith("+++ ") or line.startswith("--- "):
            name = line[4:].strip()
            if name != "/dev/null":
                path = name[2:] if name[:2] in ("a/", "b/") else name
                current = changes.setdefault(path, Change(path))
                current.created = current.created or created
            elif line.startswith("--- "):
                created = True  # no old side: the file is new, which the `+++` line that follows will name
            if line.startswith("+++ ") and name == "/dev/null" and current is not None:
                current.deleted = True
        elif line.startswith("@@"):
            found = HUNK.match(line)
            if not found or current is None:
                raise CannotRead(f"the diff has a hunk this cannot read: {line[:80]}")
            old_line, new_line = int(found[1]), int(found[3])
            if found[4] == "0":  # a hunk that only deletes: the new file's line `new_line` is the one before the gap
                current.gaps.add(new_line)
        elif current is not None and line.startswith("-"):
            current.removed.append((old_line, line[1:]))
            old_line += 1
        elif current is not None and line.startswith("+"):
            current.added.append((new_line, line[1:]))
            new_line += 1
    return changes


def is_test(path):
    return bool(PY_TEST.search(path) or PS_TEST.search(path) or CPP_TEST.search(path))


def python_case(text, number, lines):
    """`Class.test_x` for a `def test_x` line, its class found by walking up the file it is in."""
    found = PY_CASE.match(text)
    if not found:
        return None
    indent = len(found[1])
    for index in range(min(number - 1, len(lines or [])) - 1, -1, -1):
        above = PY_CLASS.match(lines[index])
        if above and len(above[1]) < indent:
            return f"{above[2]}.{found[2]}"
    return found[2]


def enclosing_case(lines, number):
    """The line of the `def test_` a Python line sits in, or None. A removed case's own assertions are part of that
    one removal, not a second thing to answer."""
    if not lines or number > len(lines):
        return None
    indent = len(lines[number - 1]) - len(lines[number - 1].lstrip())
    for index in range(number - 2, -1, -1):
        found = PY_CASE.match(lines[index])
        if found and len(found[1]) < indent:
            return index + 1
        if lines[index].strip() and len(lines[index]) - len(lines[index].lstrip()) < indent and not found:
            indent = len(lines[index]) - len(lines[index].lstrip())
    return None


def relaxing_comments(lines):
    """[(line number, the line's whitespace-normalised text)] for each real comment in Python `lines` that relaxes a
    check, found by the tokenizer and so never inside a string; None when the text does not tokenize."""
    found = []
    try:
        for token in tokenize.generate_tokens(io.StringIO("\n".join(lines or []) + "\n").readline):
            if token.type == tokenize.COMMENT and RELAX_COMMENT.search(token.string):
                found.append((token.start[0], " ".join(lines[token.start[0] - 1].split())))
    except (tokenize.TokenError, SyntaxError):
        return None
    return found


def own_relaxations(old, new):
    """The lines of a Python file whose real relaxing comment is new: one the old text did not have as a comment on
    the same code, so an edit that turns a fixture string into a live directive (removing the quotes around it, the
    line itself unchanged) is caught as surely as an added line (rounds 7 and 9 of D00 T07 §2, its completion review).
    A line the edit only moved is not new. None when the new text does not tokenize, so the caller reads its added
    lines with the line pattern instead; an old text that does not tokenize counts as having no such comment."""
    now = relaxing_comments(new)
    if now is None:
        return None
    before = {}
    for _, text in relaxing_comments(old) or []:
        before[text] = before.get(text, 0) + 1
    fresh = []
    for number, text in now:
        if before.get(text, 0):
            before[text] -= 1
        else:
            fresh.append(number)
    return fresh


def own_skips(lines, added):
    """The added lines of a Python file that really skip a test: a call to skipTest, a skip decorator, or a raised
    SkipTest, found in the parsed code and so never inside a string. A file that does not parse reports none here;
    its tests cannot run at all, which the suite says louder."""
    try:
        tree = ast.parse("\n".join(lines or []))
    except (SyntaxError, ValueError):
        return []
    found = set()
    for node in ast.walk(tree):
        names = []
        if isinstance(node, ast.Call):
            names.append(node.func)
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            names += [d.func if isinstance(d, ast.Call) else d for d in node.decorator_list]
        if isinstance(node, ast.Raise) and node.exc is not None:
            names.append(node.exc.func if isinstance(node.exc, ast.Call) else node.exc)
        for name in names:
            word = name.attr if isinstance(name, ast.Attribute) else name.id if isinstance(name, ast.Name) else ""
            if word in ("skipTest", "SkipTest") or word.startswith("skip"):
                found.add(name.lineno)
    return sorted(found & set(added))


def enforces(text, token):
    """Whether a line turns a strictness setting on: it names it, and what follows is not an off value."""
    if token not in text:
        return False
    return not OFF.search(text.split(token, 1)[1])


def swallowed(lines, added, gaps=(), path=""):
    """The lines of the handlers in a file's new text that swallow an error and that this change touched: an `except`
    whose whole body is `pass`, and a `catch` whose braces hold nothing, on one line or over several. A change
    touches a handler when it added one of its lines, or deleted lines from inside it (`gaps`: the new line numbers
    after which a deletion left nothing), which is how a body is emptied without a line being added."""
    found = set()
    if not lines:
        return found

    def touched(first, last):
        span = set(range(first, last + 1))
        return bool(span & set(added)) or any(gap in span and gap + 1 in span for gap in gaps)
    for index, text in enumerate(lines):
        number = index + 1
        if EXCEPT_INLINE.match(text):
            if number in added:
                found.add(number)
            continue
        if EXCEPT.match(text):
            indent = len(text) - len(text.lstrip())
            body = []
            for below in range(index + 1, len(lines)):
                line = lines[below]
                if not line.strip() or line.lstrip().startswith("#"):
                    continue
                if len(line) - len(line.lstrip()) <= indent:
                    break
                body.append(below + 1)
            if len(body) == 1 and PASS.match(lines[body[0] - 1]) and touched(number, body[0]):
                found.add(number)
    # A catch whose braces hold only a comment is as empty as one that holds nothing: the comments go first, each
    # replaced by spaces so every position still falls on its own line.
    comment = HASH_COMMENT if path.lower().endswith((".ps1", ".psm1")) else SLASH_COMMENT
    bare = [comment.sub(lambda found: " " * len(found[0]), line) for line in lines]
    text = "\n".join(bare)
    starts = [0]
    for line in lines:
        starts.append(starts[-1] + len(line) + 1)
    for match in EMPTY_CATCH.finditer(text):
        first = next(i for i in range(len(lines)) if starts[i + 1] > match.start()) + 1
        last = next(i for i in range(len(lines)) if starts[i + 1] >= match.end()) + 1
        if touched(first, last):
            found.add(first)
    return found


def check_at(lines, number):
    """The name of the PowerShell check a line belongs to: its own, or the nearest one above it."""
    for index in range(min(number, len(lines or [])) - 1, -1, -1):
        name = powershell_case(lines[index])
        if name:
            return name
    return None


def powershell_case(text):
    if not PS_CASE.match(text):
        return None
    first = PS_NAME_FIRST.match(text)
    if first:
        return first[2]
    last = PS_NAME_LAST.findall(text)
    return last[-1][1] if last else text.strip()


def cases(change, side):
    """[(name, line number)] of the test cases on one side of a change."""
    lines = change.removed if side == "-" else change.added
    whole = change.old if side == "-" else change.new
    found = []
    for number, text in lines:
        name = None
        if PY_TEST.search(change.path):
            name = python_case(text, number, whole)
        elif PS_TEST.search(change.path):
            name = powershell_case(text)
        elif CPP_TEST.search(change.path):
            row = CPP_ROW.match(text)
            name = row[1] if row else None
        if name:
            found.append((name, number))
    return found


def scan(changes):
    """Every signal in the changes, in a steady order."""
    signals = []
    files = dict(changes)
    tests = {path: change for path, change in files.items() if is_test(path)}

    # Cases that left and cases that came, matched one to one by name across the whole diff (a move), then, within
    # one file, a case that left against one that came (a rename or a rewrite in place).
    gone = {path: cases(change, "-") for path, change in tests.items()}
    came = {path: cases(change, "+") for path, change in tests.items()}
    arrivals = Counter(name for found in came.values() for name, _ in found)
    departures = Counter(name for found in gone.values() for name, _ in found)
    moved_with = defaultdict(set)  # files joined by a case that moved between them
    arrived_at = defaultdict(list)
    for path, found in came.items():
        for name, _ in found:
            arrived_at[name].append(path)
    unmatched = {}
    flagged = defaultdict(set)  # the lines of the cases reported as removed, so their assertions are not counted again
    for path, found in gone.items():
        left = []
        for name, number in found:
            if arrivals[name] > 0:
                arrivals[name] -= 1
                for other in arrived_at[name]:
                    moved_with[path].add(other)
                    moved_with[other].add(path)
            else:
                left.append((name, number))
        unmatched[path] = left
    for path, left in unmatched.items():
        spare = sum(1 for name, _ in came[path] if departures[name] == 0)  # new names in this file
        change = tests[path]
        excess = left[min(spare, len(left)):]  # each new case in the file stands in for one that left it
        if change.deleted:
            if left or not gone[path]:
                signals.append(Signal("test-file-deleted", path, "file"))
            continue
        for name, number in excess:
            signals.append(Signal("test-removed", path, name))
            flagged[path].add(number)

    # A groups[] row whose function became nullptr: the case is still listed and no longer runs.
    for path, change in tests.items():
        if not CPP_TEST.search(path):
            continue
        before = {row[1]: row[2] for row in (CPP_ROW.match(text) for _, text in change.removed) if row}
        for _, text in change.added:
            row = CPP_ROW.match(text)
            if row and row[2] == "nullptr" and before.get(row[1], "nullptr") != "nullptr":
                signals.append(Signal("test-disabled", path, row[1]))

    # Assertions, weighed over each set of files a moved case joins: what left against what came.
    seen = set()
    for path in sorted(tests):
        if path in seen:
            continue
        group, stack = set(), [path]
        while stack:
            member = stack.pop()
            if member not in group:
                group.add(member)
                stack.extend(moved_with[member] - group)
        seen |= group
        lost = [(member, number) for member in sorted(group) for number, text in tests[member].removed
                if ASSERTION.search(text) and not (tests[member].deleted and any(s.path == member for s in signals))
                and enclosing_case(tests[member].old, number) not in flagged[member]]
        gained = sum(1 for member in group for _, text in tests[member].added if ASSERTION.search(text))
        for member, number in lost[gained:]:  # those that nothing came in to replace
            signals.append(Signal("assertion-removed", member, line_id(tests[member].old, number)))

    # A PowerShell check kept by name with less in its condition: fewer `-and` terms left the file than came into
    # it, or the condition became a bare $true. Named by the check it belongs to.
    for path, change in sorted(tests.items()):
        if not PS_TEST.search(path) or change.deleted:
            continue
        lost = [(number, len(PS_TERM.findall(text))) for number, text in change.removed
                if PS_TERM.search(text) and number not in flagged[path]]
        excess = sum(count for _, count in lost) - sum(len(PS_TERM.findall(text)) for _, text in change.added)
        for number, count in reversed(lost):
            if excess <= 0:
                break
            signals.append(Signal("assertion-removed", path, check_at(change.old, number) or line_id(change.old, number)))
            excess -= count
        for number, text in change.added:
            if PS_CASE.match(text) and PS_TRIVIAL.search(text):
                signals.append(Signal("assertion-removed", path, powershell_case(text) or line_id(change.new, number)))

    for path, change in sorted(files.items()):
        if path == CHECKER_CONFIG:
            # What the type checker reads: any change to it is a signal, fingerprinted by the whole new file, so each
            # distinct edit is answered on its own and no answer covers another. Judging which edits weaken pyright
            # (paths, modes, rules, environments and their order, comments) reopened in four review rounds of
            # D00 T07 §2, so the scan no longer judges: a stricter edit is answered as briefly as it is made.
            exact = getattr(change.new, "raw", "\n".join(change.new or []))
            if getattr(change.old, "raw", change.old) != getattr(change.new, "raw", change.new):
                digest = hashlib.sha1(exact.encode("utf-8")).hexdigest()[:6]
                signals.append(Signal("warning-relaxed", path, f"L1#{digest}"))
            continue
        if not CODE.search(path):
            continue  # prose that mentions a pattern is not code that uses it
        if path in SELF:
            # The scan's own files hold every pattern as text, in the rules and in the fixtures of their tests. So
            # the line patterns are not read there. A skip is found by parsing the file as Python, which tells a
            # call or a decorator from the same words inside a fixture string. Everything structural above (a file deleted, a case removed, assertions lost)
            # applies to them as to any test.
            for number in own_skips(change.new, {number for number, _ in change.added}):
                signals.append(Signal("skip-added", path, line_id(change.new, number)))
            relaxed = own_relaxations(change.old, change.new)
            if relaxed is None:  # it does not tokenize: its added lines are read by the pattern, as anywhere else
                relaxed = [number for number, text in change.added if RELAX_COMMENT.search(text)]
            for number in relaxed:
                signals.append(Signal("warning-relaxed", path, line_id(change.new, number)))
            continue
        # In Python only a real comment relaxes a check, so a directive held in a string is not one (round 9 of
        # D00 T07 §2); a file that does not tokenize is read line by line, so nothing in it goes unread.
        relaxed = own_relaxations(change.old, change.new) if path.endswith(".py") else None
        for number in sorted(set(relaxed or [])):
            signals.append(Signal("warning-relaxed", path, line_id(change.new, number)))
        for number, text in change.added:
            if SKIP.search(text) or (GATE.search(path) and SKIP_SWITCH.search(text)):
                signals.append(Signal("skip-added", path, line_id(change.new, number)))
            if FLAG.search(text) if relaxed is not None else RELAX.search(text):
                signals.append(Signal("warning-relaxed", path, line_id(change.new, number)))
        # A handler that swallows, wherever the edit was: on its own line, or in the body under a line left alone.
        for number in sorted(swallowed(change.new, {number for number, _ in change.added}, change.gaps, path)):
            signals.append(Signal("error-swallowed", path, line_id(change.new, number)))
        for number, text in change.added:
            if SUPPRESS.search(text):
                signals.append(Signal("error-swallowed", path, line_id(change.new, number)))
        kept = [text.strip() for _, text in change.added]
        for number, text in change.removed:
            if WORKFLOW.search(path) and RUN_STEP.match(text) and text.strip() not in kept:
                signals.append(Signal("ci-step-removed", path, line_id(change.old, number)))
            for token in STRICT:
                # The setting counts as kept only where an added line still has it on: renaming the value to OFF
                # keeps the name and drops the enforcement.
                if enforces(text, token) and not any(enforces(line, token) for line in kept):
                    signals.append(Signal("warning-relaxed", path, line_id(change.old, number)))
                    break

    order = {signal.key: index for index, signal in enumerate(signals)}
    return sorted({signal.key: signal for signal in signals}.values(), key=lambda s: (s.path, order[s.key]))


class Text(list):
    """A file's lines, keeping its exact text for a check that must see every byte, a final newline included
    (the completion review of D00 T07 §2: a config edit to its last newline alone was not seen)."""

    def __init__(self, raw):
        super().__init__(raw.splitlines())
        self.raw = raw


def show(root, rev, path):
    """A file's lines at a revision. The diff says whether the file exists on a side, so this is only asked for one
    that does, and a failure here is a failure to read (a timeout, a disk error), never taken for an absent file:
    the checks that need the text would otherwise pass in silence."""
    return Text(run_git(root, ["show", f"{rev}:{path}"]))


def committed(root, base, head):
    changes = parse_diff(run_git(root, ["diff", "--no-color", "--no-renames", "-U0", base, head, "--"]))
    for path, change in changes.items():
        change.old = None if change.created else show(root, base, path)
        change.new = None if change.deleted else show(root, head, path)
    return changes


def working_text(root, path):
    try:
        return Text((Path(root) / path).read_text(encoding="utf-8", errors="replace"))
    except OSError as exc:
        raise CannotRead(f"{path} can't be read: {exc}") from None


def uncommitted(root):
    """What is not committed, as two sets of changes scanned apart: what is staged (HEAD to the index, which is
    what the next commit holds) and what is not (the index to the working tree, with untracked files). Together they
    would cancel out when a staged removal is put back in the working tree only, and the next commit would still
    remove it."""
    staged = parse_diff(run_git(root, ["diff", "--cached", "--no-color", "--no-renames", "-U0", "HEAD", "--"]))
    for path, change in staged.items():
        change.old = None if change.created else show(root, "HEAD", path)
        change.new = None if change.deleted else show(root, "", path)  # `:path` is the index's copy
    unstaged = parse_diff(run_git(root, ["diff", "--no-color", "--no-renames", "-U0", "--"]))
    for path, change in unstaged.items():
        change.old = None if change.created else show(root, "", path)
        change.new = None if change.deleted else working_text(root, path)
    for path in run_git(root, ["ls-files", "--others", "--exclude-standard"]).splitlines():
        lines = working_text(root, path)
        change = unstaged.setdefault(path, Change(path))
        change.created, change.new, change.added = True, lines, list(enumerate(lines, 1))
    return staged, unstaged


def answers(root, base, head):
    """[[text, reason, parsed, used]] from the commit bodies of the range, one entry per answer line. `text` is the
    signal's name (`<kind> <path>:<id>`) as the report prints it, so nothing about a path (a space in it, say) can
    keep an answer from matching; `parsed` is (kind, path, line, fingerprint or None) for a line signal, else None."""
    found = []
    for line in run_git(root, ["log", "--format=%B", f"{base}..{head}"]).splitlines():
        answer = ANSWER.match(line.strip())
        if answer:
            text = answer[1].strip()
            line_part = LINE_ID.match(text)
            parsed = (line_part[1], line_part[2], int(line_part[3]), line_part[4]) if line_part else None
            found.append([text, answer[2], parsed, False])
    return found


def apply_answers(signals, given):
    """Set each signal's reason from the answers. An answer covers one signal and is used once. In order: the same
    name; then, for a signal that carries a fingerprint, an answer with the same kind, path and fingerprint wherever
    the line now is. Several signals with one fingerprint need one answer each. An answer giving only a line no longer
    matches: it covered whatever later stood on that line (rounds 6 and 8 of D00 T07 §2), and the gate scans only
    commits not yet on origin/main, all of whose answers carry fingerprints."""
    def take(signal, matches):
        for entry in given:
            if not entry[3] and matches(entry):
                entry[3] = True
                signal.reason = entry[1]
                return

    for signal in signals:
        take(signal, lambda entry: entry[0] == str(signal))
    for signal in signals:
        here = LINE_ID.match(str(signal))
        if signal.reason or not here or not here[4]:
            continue
        fingerprint = here[4]
        take(signal, lambda entry: entry[2] is not None and entry[2][3] == fingerprint
             and entry[2][:2] == (signal.kind, signal.path))


def resolve(root, rev):
    try:
        return run_git(root, ["rev-parse", "--verify", "--quiet", f"{rev}^{{commit}}"]).strip()
    except CannotRead:
        raise CannotRead(f"{rev} is not a commit here") from None


def default_base(root):
    """The merge base of HEAD and origin/main, or HEAD when there is no such branch or no common commit. Those two
    are answers git gives (exit 1); anything else is a failure to read, never taken for "nothing to scan"."""
    code, _, said = git_result(root, ["rev-parse", "--verify", "--quiet", "refs/remotes/origin/main^{commit}"])
    if code == 1:
        return resolve(root, "HEAD")
    if code != 0:
        raise CannotRead(f"origin/main could not be looked up: {said}")
    code, out, said = git_result(root, ["merge-base", "HEAD", "origin/main"])
    if code == 1:
        return resolve(root, "HEAD")
    if code != 0 or not out.strip():
        raise CannotRead(f"the merge base with origin/main could not be found: {said}")
    return out.strip()


def main(argv=None, root=ROOT):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", help="the commit the range starts after (default: the merge base with origin/main)")
    parser.add_argument("--head", help="the commit the range ends at; with it, nothing uncommitted is looked at")
    args = parser.parse_args(argv)
    try:
        base = resolve(root, args.base) if args.base else default_base(root)
        head = resolve(root, args.head or "HEAD")
        found = scan(committed(root, base, head))
        given = answers(root, base, head)
        loose = []
        if not args.head:
            for changes in uncommitted(root):
                loose += [signal for signal in scan(changes) if signal.key not in {known.key for known in loose}]
    except CannotRead as exc:
        print(f"weakening-scan: {exc}")
        return 2
    apply_answers(found, given)
    open_ones = [signal for signal in found if not signal.reason]
    print(f"committed {base[:7]}..{head[:7]}: {len(found)} signal(s), {len(open_ones)} unanswered")
    for signal in found:
        print(f"  answered   {signal} -- {signal.reason}" if signal.reason else f"  unanswered {signal}")
    if open_ones:
        print("answer each in a commit body of the range, or undo it:  Weakening scan: <kind> <path>:<id> -- <why>")
    if not args.head:
        print(f"not committed yet: {len(loose)} signal(s), each to be answered in the body of the commit that makes it")
        for signal in loose:
            print(f"  {signal}")
    return 1 if open_ones else 0


if __name__ == "__main__":
    reconfigure = getattr(sys.stdout, "reconfigure", None)
    if reconfigure:
        reconfigure(encoding="utf-8")
    sys.exit(main())
