"""Claude Code's SessionStart hook for the private overlay's owner-only rules (D00 T05 §5).

Wired in .claude/settings.json. At every session start it checks the overlay as it is then and, when .private is the
overlay's own clone (a real folder whose .git is a real folder git names as both its git and common folder, the rule
Get-OverlayCloneFault in scripts/lib/overlay.ps1 applies), marked by setup (bosun.overlay = true in its own config),
and holds AGENTS.md as a file of its own (not a link to a file elsewhere), it hands that file to the session as
context. Otherwise it prints nothing. It writes no file, and a file it cannot read is a note on standard error and
exit 0, never a blocked session.

    python scripts/overlay_rules_hook.py [checkout]   (Claude Code passes CLAUDE_PROJECT_DIR)
"""
import json
import os
from pathlib import Path
import stat
import subprocess
import sys

# A hook is started by Claude Code with the project's own git variables possibly set; git -C must not read them.
GIT_VARIABLES = ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                 "GIT_COMMON_DIR", "GIT_PREFIX")


def is_real(path, folder):
    """Whether path is there as itself: no symbolic link, no junction or other reparse point, of the kind asked for."""
    try:
        info = os.lstat(path)
    except OSError:
        return False
    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0):
        return False
    return stat.S_ISDIR(info.st_mode) if folder else stat.S_ISREG(info.st_mode)


def git(folder, *arguments):
    env = {k: v for k, v in os.environ.items() if k not in GIT_VARIABLES}
    done = subprocess.run(["git", "-C", str(folder), *arguments], env=env, capture_output=True, text=True, timeout=20)
    return done.stdout.strip() if done.returncode == 0 else None


def trusted_rules_file(checkout):
    """The overlay's own AGENTS.md, or None when it is not to be trusted."""
    private = Path(checkout) / ".private"
    if not is_real(private, folder=True) or not is_real(private / ".git", folder=True):
        return None
    own = os.path.normcase(os.path.realpath(private / ".git"))
    for ask in ("--absolute-git-dir", "--git-common-dir"):
        said = git(private, "rev-parse", "--path-format=absolute", ask)
        if not said or os.path.normcase(os.path.realpath(said)) != own:
            return None
    if git(private, "config", "--local", "--get", "bosun.overlay") != "true":
        return None
    rules = private / "AGENTS.md"
    return rules if is_real(rules, folder=False) else None


def main():
    checkout = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("CLAUDE_PROJECT_DIR") or str(Path(__file__).resolve().parents[1])
    rules = trusted_rules_file(checkout)
    if rules is None:
        return 0
    try:
        # utf-8-sig reads a file saved with or without a byte-order mark as its text.
        text = rules.read_text(encoding="utf-8-sig")
    except (OSError, UnicodeDecodeError) as error:
        print(f"overlay rules: .private/AGENTS.md could not be read ({error}); this session runs without them", file=sys.stderr)
        return 0
    context = ("The owner's own rules for this checkout, from the private overlay (.private/AGENTS.md). "
               "Follow them as part of the project's rules:\n\n" + text)
    sys.stdout.write(json.dumps({"hookSpecificOutput": {"hookEventName": "SessionStart", "additionalContext": context}}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
