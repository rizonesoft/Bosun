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
    assertion-removed    more assertion lines left a file than came into it (self.assert..., assert, CHECK(...))
    skip-added           skipTest, unittest.skip, continue-on-error, or a -Skip switch on a test in the gate or CI
    ci-step-removed      a `run:` line gone from a workflow
    warning-relaxed      warnings-as-errors or /W4 taken out or turned off, or a noqa, type: ignore or warning
                         pragma put in
    error-swallowed      an `except` whose body is only `pass`, or an empty `catch`, whether the handler is new or
                         an existing one whose body was emptied

The id is the case's name where the signal is about a case, and `L<line>` otherwise (the old line for something
removed, the new line for something added), so two signals in one file are two things to answer.

A signal is not a verdict. Each is either undone, or answered in the body of a commit in the scanned range:

    Weakening scan: <kind> <path>:<id> -- <why this is right>

One answer covers one signal, and an answer with no reason does not count. The report lists every signal with its
answer, answered or not, so the reader sees what was removed and why. What is committed is enforced. What is not
committed yet is listed under its own heading and never fails the scan: its answer belongs in the commit body, which
does not exist yet.

A test that moved is not a removal: a case removed in one place and added once elsewhere under the same qualified
name is matched to it, and the assertions that went with it are weighed against those that came with it. A case
replaced in the same file by another (a rename, a rewrite) is not one either; one that simply goes is.

The scan skips its own file and its own test, whose patterns would flag themselves.

Exit 0 when every committed signal is answered, 1 when one is not, 2 with the reason when the range or a file in it
cannot be read: a diff git could not produce is never read as an empty one. Stdlib only.
"""
import argparse
from collections import Counter, defaultdict
import re
import subprocess
import sys
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
ASSERTION = re.compile(r"\bself\.assert\w*\s*\(|^\s*assert\s|\bCHECK\s*\(")
SKIP = re.compile(r"\bskipTest\s*\(|\bunittest\.skip|^\s*@skip\b|continue-on-error:\s*true", re.IGNORECASE)
SKIP_SWITCH = re.compile(r"_test\.ps1\b.*\s-Skip\w*", re.IGNORECASE)
RUN_STEP = re.compile(r"^\s*(?:-\s*)?run:\s*\S")
STRICT = ("CMAKE_COMPILE_WARNING_AS_ERROR", "/W4", "TreatWarningsAsErrors")
RELAX = re.compile(r"#\s*noqa\b|#\s*type:\s*ignore|#pragma\s+warning\s*\(\s*disable|\s-Wno-\w+")
OFF = re.compile(r"""^\W*(OFF|FALSE|NO|0)\b""", re.IGNORECASE)
EXCEPT = re.compile(r"^\s*except\b.*:\s*(#.*)?$")
EXCEPT_INLINE = re.compile(r"^\s*except\b.*:\s*pass\s*(#.*)?$")
PASS = re.compile(r"^\s*pass\s*(#.*)?$")
EMPTY_CATCH = re.compile(r"\bcatch\b\s*(\([^)]*\)|\[[^\]]*\])?\s*\{\s*\}")
ANSWER = re.compile(r"^Weakening scan: (\S+) (\S+?):(.+?) -- (\S.*?)\s*$")
HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")


class CannotRead(Exception):
    """The range, or a file in it, could not be read (exit 2)."""


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
        self.old, self.new = None, None  # the whole file, as lists of lines; None when it did not exist
        self.deleted = False


def run_git(root, arguments):
    try:
        done = subprocess.run(["git", "-C", str(root), "-c", "core.quotepath=false", *arguments], capture_output=True,
                              text=True, encoding="utf-8", errors="replace", timeout=120)
    except (OSError, subprocess.SubprocessError) as exc:
        raise CannotRead(f"git could not run: {exc}") from None
    if done.returncode != 0:
        said = (done.stderr or done.stdout).strip().splitlines()
        raise CannotRead(f"git {' '.join(arguments[:3])} failed: {said[0] if said else 'no output'}")
    return done.stdout


def parse_diff(text):
    """{path: Change} from `git diff -U0 --no-renames` output."""
    changes, current, old_line, new_line = {}, None, 0, 0
    for line in text.splitlines():
        if line.startswith("diff --git "):
            current = None
        elif line.startswith("+++ ") or line.startswith("--- "):
            name = line[4:].strip()
            if name != "/dev/null":
                path = name[2:] if name[:2] in ("a/", "b/") else name
                current = changes.setdefault(path, Change(path))
            if line.startswith("+++ ") and name == "/dev/null" and current is not None:
                current.deleted = True
        elif line.startswith("@@"):
            found = HUNK.match(line)
            if not found or current is None:
                raise CannotRead(f"the diff has a hunk this cannot read: {line[:80]}")
            old_line, new_line = int(found[1]), int(found[3])
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


def enforces(text, token):
    """Whether a line turns a strictness setting on: it names it, and what follows is not an off value."""
    if token not in text:
        return False
    return not OFF.search(text.split(token, 1)[1])


def swallowed(lines, added):
    """The lines of the handlers in a file's new text that swallow an error and that this change touched: an `except`
    whose whole body is `pass`, and a `catch` whose braces hold nothing, on one line or over several."""
    found = set()
    if not lines:
        return found
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
            if len(body) == 1 and PASS.match(lines[body[0] - 1]) and ({number, body[0]} & added):
                found.add(number)
    text = "\n".join(lines)
    starts = [0]
    for line in lines:
        starts.append(starts[-1] + len(line) + 1)
    for match in EMPTY_CATCH.finditer(text):
        first = next(i for i in range(len(lines)) if starts[i + 1] > match.start()) + 1
        last = next(i for i in range(len(lines)) if starts[i + 1] >= match.end()) + 1
        if set(range(first, last + 1)) & added:
            found.add(first)
    return found


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
    files = {path: change for path, change in changes.items() if path not in SELF}
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
            signals.append(Signal("assertion-removed", member, f"L{number}"))

    for path, change in sorted(files.items()):
        if not CODE.search(path):
            continue  # prose that mentions a pattern is not code that uses it
        for number, text in change.added:
            if SKIP.search(text) or (GATE.search(path) and SKIP_SWITCH.search(text)):
                signals.append(Signal("skip-added", path, f"L{number}"))
            if RELAX.search(text):
                signals.append(Signal("warning-relaxed", path, f"L{number}"))
        # A handler that swallows, wherever the edit was: on its own line, or in the body under a line left alone.
        for number in sorted(swallowed(change.new, {number for number, _ in change.added})):
            signals.append(Signal("error-swallowed", path, f"L{number}"))
        kept = [text.strip() for _, text in change.added]
        for number, text in change.removed:
            if WORKFLOW.search(path) and RUN_STEP.match(text) and text.strip() not in kept:
                signals.append(Signal("ci-step-removed", path, f"L{number}"))
            for token in STRICT:
                # The setting counts as kept only where an added line still has it on: renaming the value to OFF
                # keeps the name and drops the enforcement.
                if enforces(text, token) and not any(enforces(line, token) for line in kept):
                    signals.append(Signal("warning-relaxed", path, f"L{number}"))
                    break

    order = {signal.key: index for index, signal in enumerate(signals)}
    return sorted({signal.key: signal for signal in signals}.values(), key=lambda s: (s.path, order[s.key]))


def show(root, rev, path):
    """A file's lines at a revision, or None when it does not exist there."""
    try:
        return run_git(root, ["show", f"{rev}:{path}"]).splitlines()
    except CannotRead:
        return None


def committed(root, base, head):
    changes = parse_diff(run_git(root, ["diff", "--no-color", "--no-renames", "-U0", base, head, "--"]))
    for path, change in changes.items():
        change.old, change.new = show(root, base, path), show(root, head, path)
    return changes


def uncommitted(root):
    changes = parse_diff(run_git(root, ["diff", "--no-color", "--no-renames", "-U0", "HEAD", "--"]))
    for path, change in changes.items():
        change.old = show(root, "HEAD", path)
        target = Path(root) / path
        change.new = target.read_text(encoding="utf-8", errors="replace").splitlines() if target.is_file() else None
    for path in run_git(root, ["ls-files", "--others", "--exclude-standard"]).splitlines():
        target = Path(root) / path
        try:
            lines = target.read_text(encoding="utf-8", errors="replace").splitlines()
        except OSError as exc:
            raise CannotRead(f"{path} is new and can't be read: {exc}") from None
        change = changes.setdefault(path, Change(path))
        change.new, change.added = lines, list(enumerate(lines, 1))
    return changes


def answers(root, base, head):
    """{(kind, path, id): reason} from the commit bodies of the range."""
    found = {}
    for line in run_git(root, ["log", "--format=%B", f"{base}..{head}"]).splitlines():
        answer = ANSWER.match(line.strip())
        if answer:
            found[(answer[1], answer[2], answer[3].strip())] = answer[4]
    return found


def resolve(root, rev):
    try:
        return run_git(root, ["rev-parse", "--verify", "--quiet", f"{rev}^{{commit}}"]).strip()
    except CannotRead:
        raise CannotRead(f"{rev} is not a commit here") from None


def default_base(root):
    """The merge base of HEAD and origin/main, or HEAD when there is no such branch or no common commit."""
    try:
        return run_git(root, ["merge-base", "HEAD", "origin/main"]).strip()
    except CannotRead:
        return resolve(root, "HEAD")


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
        loose = [] if args.head else scan(uncommitted(root))
    except CannotRead as exc:
        print(f"weakening-scan: {exc}")
        return 2
    for signal in found:
        signal.reason = given.get(signal.key)
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
