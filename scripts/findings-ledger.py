#!/usr/bin/env python3
"""Check that a range of todo/findings.md was settled (D00 T06): every entry open there at a base commit is still in the
file exactly once, struck (`- ~~<its text>~~`), and followed by a disposition the plan can act on:

  Scheduled: DNN TNN §N    an open section whose text names the finding's file (the first backticked path)
  Fixed: <commit>, test: <command>    fixed in the triage section with a regression test, the commit in history
  Done: <commit or DNN TNN §N>    already fixed earlier: a commit in history, or a stamped section
  Superseded: <commit or DNN TNN §N> ...    the code it named was redesigned or removed
  Declined: <reason>    a suggestion not taken, with a reason of at least five words

  python scripts/findings-ledger.py --base b8c4e0b3 --lines 5-40

Exit 0 when the range is settled, 1 with each unsettled entry named (by its base line), 2 when it cannot check.
"""
import argparse
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FINDINGS = "todo/findings.md"
REF = re.compile(r"D\d\d T\d\d §\d+")
KINDS = ("Scheduled", "Fixed", "Done", "Superseded", "Declined")


def git(*args, root=ROOT):
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, encoding="utf-8")


def is_commit(sha, root):
    """A commit that is in the checked-out history, not just any object."""
    if not re.fullmatch(r"[0-9a-f]{7,40}", sha):
        return False
    return git("merge-base", "--is-ancestor", sha, "HEAD", root=root).returncode == 0


def struck_in(entry, root):
    """The first commit whose todo/findings.md holds this entry struck, or None."""
    out = git("log", "--format=%H", "--reverse", "-S", "- ~~%s~~" % entry, "--", FINDINGS, root=root)
    commits = out.stdout.split() if out.returncode == 0 else []
    return commits[0] if commits else None


def shipped_after(stamp, entry, root):
    """Whether the section's stamp came after the entry was scheduled to it: then the owner has shipped it, and the
    scheduling stands settled (D00 T03 §30's validation: a stamped owner made its triage checkpoint fail)."""
    struck = struck_in(entry, root)
    return bool(struck) and git("merge-base", "--is-ancestor", struck, stamp, root=root).returncode == 0


_GRAPHS = {}


def resolve(ref, root):
    """(exit code, section body) as `todo-graph.py resolve` gives them, from one graph load per root: the graph takes
    many seconds to load, and a range names a dozen sections."""
    if root not in _GRAPHS:
        sys.path.insert(0, str(Path(root) / "scripts" / "lib"))
        import todo_graph
        _GRAPHS[root] = (todo_graph, todo_graph.graph(Path(root)))
    todo_graph, rows = _GRAPHS[root]
    found, code = todo_graph.extract_ref(ref, rows)
    if code:
        return code, ""
    row = rows[found]
    return todo_graph.disposition(row, rows), row.body

def problem(entry, disposition, root, resolver=resolve, line=0):
    """Why a struck entry's disposition does not settle it, or None."""
    m = re.match(r"(%s): (.+)" % "|".join(KINDS), disposition.strip())
    if not m:
        return "no disposition (one of %s) after the strike" % ", ".join(KINDS)
    kind, detail = m.groups()
    refs = REF.findall(detail)
    shas = [s for s in re.findall(r"`?\b([0-9a-f]{7,40})\b`?", detail)]
    if kind == "Scheduled":
        if not refs:
            return "Scheduled names no DNN TNN §N"
        code, body = resolver(refs[0], root)
        if code == 3:
            # Stamped since it was scheduled: the owner shipped it. Scheduling into a section already stamped when the
            # entry was struck stays refused.
            stamp = re.search(r"\*\*Verified:\*\*[^|\n]*\|[^|\n]*\|\s*([0-9a-f]{40})", body)
            if not (stamp and shipped_after(stamp.group(1), entry, root)):
                return "%s was already stamped when this was scheduled to it (resolve exit 3)" % refs[0]
        elif code not in (0, 4):
            return "%s is not an open section (resolve exit %d)" % (refs[0], code)
        key = finding_file(entry)
        if key and key not in body.replace("\\", "/"):
            return "%s does not name %s, so it does not visibly own this finding" % (refs[0], key)
        if not key and not re.search(r"findings base line %d(?!\d)" % line, body):
            return "the finding names no file, and %s does not cite `findings base line %d`" % (refs[0], line)
    elif kind == "Fixed":
        if not shas or not is_commit(shas[0], root):
            return "Fixed names no commit in history"
        if not re.search(r"test:\s*\S", detail):
            return "Fixed names no regression test (test: <command>)"
    elif kind in ("Done", "Superseded"):
        why = REF.sub("", re.sub(r"`?\b[0-9a-f]{7,40}\b`?", "", detail)).split()
        if kind == "Superseded" and len(why) < 3:
            return "Superseded says not what replaced the behavior (at least three words besides the commit or section)"
        if shas and is_commit(shas[0], root):
            return None
        if refs and resolver(refs[0], root)[0] == 3:
            return None
        return "%s names no commit in history and no stamped section" % kind
    elif len(detail.split()) < 5:
        return "Declined gives no reason (at least five words)"
    return None


def finding_file(entry):
    """The file a finding is about: its first backticked path, else the first path-like word, with / as separator."""
    named = re.search(r"`([^`]+)`", entry)
    word = re.split(r"[:\s]", named.group(1))[0] if named else ""
    if not re.search(r"[/\\]|\.\w+$", word):
        loose = re.search(r"[\w.-]+(?:[/\\][\w.*-]+)+", entry)
        word = loose.group(0) if loose else ""
    return word.replace("\\", "/")


def check(base, first, last, root=ROOT, resolver=resolve):
    if first < 1 or first > last:
        print("findings-ledger: the range %d-%d is empty or reversed; give first-last with first at least 1" % (first, last))
        return 2
    shown = git("show", "%s:%s" % (base, FINDINGS), root=root)
    if shown.returncode:
        print("findings-ledger: cannot read %s at %s" % (FINDINGS, base))
        return 2
    then = shown.stdout.splitlines()
    if last > len(then):
        print("findings-ledger: the range %d-%d runs past the end of %s at %s (%d lines)" % (first, last, FINDINGS, base, len(then)))
        return 2
    open_entries = [(n, then[n - 1][2:]) for n in range(first, min(last, len(then)) + 1)
                    if then[n - 1].startswith("- ") and not then[n - 1].startswith("- ~~")]
    now = (root / FINDINGS).read_text(encoding="utf-8").splitlines()
    bad = 0
    for n, text in open_entries:
        struck = "- ~~%s~~" % text
        hits = [line for line in now if line.startswith(struck)]
        still = [line for line in now if line == "- " + text]
        if still:
            why = "still open"
        elif len(hits) != 1:
            why = "struck %d times (the text must be kept exactly, once)" % len(hits) if hits else "missing (deleted or its text changed)"
        else:
            why = problem(text, hits[0][len(struck):], root, resolver, n)
        if why:
            bad += 1
            print("findings-ledger: base line %d: %s" % (n, why))
    if bad:
        print("findings-ledger: %d of %d entries on lines %d to %d (at %s) are not settled" % (bad, len(open_entries), first, last, base))
        return 1
    print("findings-ledger: all %d entries on lines %d to %d (at %s) settled" % (len(open_entries), first, last, base))
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description="Check that a range of todo/findings.md was settled.")
    parser.add_argument("--base", required=True, help="the commit the range was counted at")
    parser.add_argument("--lines", required=True, help="first-last, 1-based, inclusive")
    args = parser.parse_args(argv)
    m = re.fullmatch(r"(\d+)-(\d+)", args.lines)
    if not m:
        parser.error("--lines takes first-last, for example 5-40")
    sys.path.insert(0, str(ROOT / "scripts" / "lib"))
    from todo_graph import Refused
    try:
        return check(args.base, int(m.group(1)), int(m.group(2)))
    except (Refused, OSError, ValueError) as exc:  # an invalid plan or an unreadable file: incomplete, as todo-graph.py has it
        print("findings-ledger: cannot check: %s" % exc)
        return 2


if __name__ == "__main__":
    sys.exit(main())
