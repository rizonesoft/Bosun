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
import json
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


def resolve(ref, root):
    """(exit code, section body) from the graph's own front door."""
    run = subprocess.run([sys.executable, str(root / "scripts" / "todo-graph.py"), "resolve", ref],
                         capture_output=True, text=True, encoding="utf-8", cwd=root)
    try:
        body = json.loads(run.stdout).get("body", "")
    except ValueError:
        body = run.stdout
    return run.returncode, body


def problem(entry, disposition, root, resolver=resolve):
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
        if code not in (0, 4):
            return "%s is not an open section (resolve exit %d)" % (refs[0], code)
        named = re.search(r"`([^`]+)`", entry)
        key = re.split(r"[:\s]", named.group(1))[0] if named else ""
        if key and key not in body:
            return "%s does not name %s, so it does not visibly own this finding" % (refs[0], key)
    elif kind == "Fixed":
        if not shas or not is_commit(shas[0], root):
            return "Fixed names no commit in history"
        if "test:" not in detail:
            return "Fixed names no regression test (test: <command>)"
    elif kind in ("Done", "Superseded"):
        if shas and is_commit(shas[0], root):
            return None
        if refs and resolver(refs[0], root)[0] == 3:
            return None
        return "%s names no commit in history and no stamped section" % kind
    elif len(detail.split()) < 5:
        return "Declined gives no reason (at least five words)"
    return None


def check(base, first, last, root=ROOT, resolver=resolve):
    shown = git("show", "%s:%s" % (base, FINDINGS), root=root)
    if shown.returncode:
        print("findings-ledger: cannot read %s at %s" % (FINDINGS, base))
        return 2
    then = shown.stdout.splitlines()
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
            why = problem(text, hits[0][len(struck):], root, resolver)
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
    return check(args.base, int(m.group(1)), int(m.group(2)))


if __name__ == "__main__":
    sys.exit(main())
