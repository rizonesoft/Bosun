"""scripts/tool-guard.py: the campaign's deny list refuses what can't be undone, and only that."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("tool_guard", ROOT / "scripts" / "tool-guard.py")
tg = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tg)


class Slashed(type(Path())):
    """A path that prints with forward slashes, as a real Bash command names a Windows path: in Bash a backslash is
    an escape, so the fixtures write paths the way a session does."""

    def __str__(self):
        return super().__str__().replace("\\", "/")


def link(target, at):
    """A directory link: a junction on Windows (no privilege needed), a symlink elsewhere."""
    if os.name == "nt":
        import _winapi
        _winapi.CreateJunction(os.path.normpath(target.as_posix()), os.path.normpath(at.as_posix()))
    else:
        os.symlink(target, at, target_is_directory=True)


REAL_ROOTS = tg.scratch_roots  # before any test replaces it
SCRATCH = Path(r"R:\scratch") if Path(r"R:\scratch").is_dir() else Path(tempfile.gettempdir())


class Base(unittest.TestCase):
    """A project, an allowed scratch folder and an outside folder, side by side in the repository's scratch area. The
    allowed roots are only this test's own scratch folder, so the real temp folder, which holds them all, can't hide
    a wrong answer."""

    def setUp(self):
        self.base = Slashed(Path(tempfile.mkdtemp(dir=str(SCRATCH), prefix="bosun-tg-")).resolve())
        self.addCleanup(lambda: shutil.rmtree(self.base, ignore_errors=True))
        self.project = self.base / "project"
        self.scratch = self.base / "scratch"
        self.outside = self.base / "outside"
        for folder in (self.project / ".git", self.project / "build", self.scratch, self.outside):
            folder.mkdir(parents=True)
        env = mock.patch.dict(os.environ, {"CLAUDE_PROJECT_DIR": str(self.project)})
        env.start()
        self.addCleanup(env.stop)
        roots = mock.patch.object(tg, "scratch_roots", lambda: [str(self.scratch)])
        roots.start()
        self.addCleanup(roots.stop)

    def decide(self, command, tool="Bash", cwd=None):
        return tg.decide({"tool_name": tool, "tool_input": {"command": command}, "cwd": str(cwd or self.project)})

    def refused(self, command, **kw):
        reason = self.decide(command, **kw)
        self.assertIsNotNone(reason, f"allowed: {command}")
        return reason

    def allowed(self, command, **kw):
        self.assertIsNone(self.decide(command, **kw), f"refused: {command}")


class Pushes(Base):
    def test_every_forced_or_deleting_push_is_refused_alone_in_a_chain_and_in_a_wrapper(self):
        forms = ["git push -f", "git push --force origin main", "git push --force-with-lease", "git push --force-with-lease=main:abc",
                 "git push --force-if-includes", "git push --mirror", "git push --delete origin old", "git push -d origin old",
                 "git push origin +main", "git push origin :old", "git push -fu origin main", "git -C repo push --force",
                 "git push --prune origin"]
        for form in forms:
            for wrapped, tool in ((form, "Bash"), (f"echo a && {form}", "Bash"), (f"cd x; {form} || true", "Bash"),
                                  (f'bash -c "{form}"', "Bash"), (f'pwsh -Command "{form}"', "PowerShell"),
                                  (f'cmd /c {form}', "PowerShell")):
                with self.subTest(wrapped):
                    self.assertIn("can't be undone", self.refused(wrapped, tool=tool))

    def test_env_s_split_string_is_the_command(self):
        for form in ("env -S 'git push --force'", 'env -S "git push -f" origin', "env --split-string='git push --mirror'",
                     "env -S'git push --force'", f"env -S 'rm -rf {self.outside}'"):
            with self.subTest(form):
                self.refused(form)
        self.allowed("env -S 'git push origin main'")
        for form in ('env -vS "git push --force origin main"', "env -iS 'git push -f'", "env -vS'git push --mirror'"):
            with self.subTest(form):
                self.refused(form)
        self.refused(f"env -iC {self.outside} rm -rf sub")
        self.refused(f"env -iC{self.outside} rm -rf sub")
        self.allowed("env -iv git status")
        for form in ('env --split-str "git push --force origin main"', "env --split 'git push -f'", "env --sp='git push --mirror'"):
            with self.subTest(form):
                self.refused(form)
        self.refused(f"env --chd {self.outside} rm -rf sub")
        self.refused(f"env --chdir {self.outside} rm -rf sub")
        for prefix in ("stdbuf --output L", "stdbuf --out L", "stdbuf --input=0", "sudo --user me", "sudo --us me",
                       "timeout --signal KILL 60", "nice --adjustment 5"):
            with self.subTest(prefix):
                self.refused(f"{prefix} git push --force origin main")

    def test_sudo_s_folder_options_move_its_command(self):
        for form in ("sudo -D /home rm -rf someone", "sudo --chdir=/home rm -rf someone", "sudo -D/home rm -rf someone",
                     "sudo -i rm -rf Documents", "sudo --login rm -rf Documents", "sudo -iu me rm -rf Documents"):
            with self.subTest(form):
                self.refused(form)
        self.allowed(f"sudo -D /home rm -rf {self.project / 'build' / 'x'}")

    def test_a_prefix_named_with_exe_is_still_the_prefix(self):
        for form in ("env.exe git push --force origin main", "C:/msys64/usr/bin/env.exe -u X git push -f",
                     "nohup.exe git push --mirror", "timeout.exe 60 git push -f"):
            with self.subTest(form):
                self.refused(form)

    def test_an_empty_attached_value_is_still_a_value(self):
        for form in ("/usr/bin/time --format= git push --force origin main", "env --unset= git push -f",
                     "timeout --signal= 60 git push --mirror"):
            with self.subTest(form):
                self.refused(form)

    def test_exec_and_time_options_take_their_values(self):
        for form in ("exec -a bosun-git git push --force origin main", "exec -cla name git push -f",
                     "time -f %e git push --force", "time --output=t.txt git push --mirror", "time -o t.txt git push -f"):
            with self.subTest(form):
                self.refused(form)

    def test_a_prefix_s_positionals_come_after_its_double_dash(self):
        for form in ("timeout -- 60 git push --force origin main", "timeout -s KILL -- 60 git push -f", "env -- git push -f",
                     "nice -- git push --mirror"):
            with self.subTest(form):
                self.refused(form)
        self.allowed("timeout -- 60 git push origin main")

    def test_every_valued_long_option_is_listed_as_a_long_option(self):
        for head, valued in tg.PREFIX_VALUES.items():
            for option in valued:
                if option.startswith("--"):
                    with self.subTest(head=head, option=option):
                        self.assertIn(option, tg.LONG_OPTIONS[head])
        for form in ("env -S 'git\\_push\\_--force'", "env -S 'git push ${FORCE}'"):
            with self.subTest(form):
                self.assertIn("can't read", self.refused(form))

    def test_a_push_past_a_prefix_s_options_is_seen(self):
        for prefix in ("env -u X", "env -i", "nice -n 5", "timeout 60", "sudo -u me", "env -C /tmp"):
            with self.subTest(prefix):
                self.refused(f"{prefix} git push --force")
        self.allowed("env -u X git push origin main")

    def test_a_push_after_a_background_separator_is_seen(self):
        self.refused("echo starting & git push --force")
        self.allowed("git push origin main 2>&1 | tail -3")
        self.allowed("make build &> build.log")

    def test_quoting_is_read_once_so_an_odd_branch_name_hides_nothing(self):
        for form in ('git push origin "release\'candidate" --force', "git push origin 'a\"b' -f", 'git push origin "x y" --mirror'):
            with self.subTest(form):
                self.refused(form)
        self.allowed('git push origin "release\'candidate"')
        for form in ('git push origin "release\\"candidate" --force', "git push origin release\\'s -f", 'git push origin a\\ b --force'):
            with self.subTest(form):
                self.refused(form)
        self.refused('git push origin "release`"candidate" --force', tool="PowerShell")
        for form in ('git push origin "release\\";candidate" --force', 'git push origin "a\\"|b" -f', "git push origin a\\;b --force"):
            with self.subTest(form):
                self.refused(form)
        self.refused('git push origin "release`";candidate" --force', tool="PowerShell")

    def test_unquoted_escapes_read_as_bash_reads_them(self):
        for form in ("git p\\ush --force origin main", "g\\it push -f", "git push --forc\\e", "git push origin \\+main"):
            with self.subTest(form):
                self.refused(form)
        self.allowed('git push origin "ma\\in"')  # inside double quotes a backslash before i is itself

    def test_ansi_c_and_locale_quoting_read_as_bash_reads_them(self):
        for form in ("git push origin main $'--force'", "git push origin main $'--\\x66orce'", "git push $'-\\146' origin",
                     "git $'p\\u0075sh' --mirror", 'git push origin main $"--force"', "$'git' push -f"):
            with self.subTest(form):
                self.refused(form)
        self.allowed("git push origin $'main'")

    def test_a_continued_line_is_one_command(self):
        self.refused("git push origin main \\\n--force")
        self.refused("git push \\\n  -f origin main")
        self.refused("git push origin main `\n--force", tool="PowerShell")
        self.allowed("git push origin \\\nmain")

    def test_ordinary_pushes_pass(self):
        for form in ("git push", "git push -u origin main", "git push origin main:main", "git push -q origin main",
                     "git push --tags", "git status && git push origin feature"):
            with self.subTest(form):
                self.allowed(form)


class Bypasses(Base):
    """What the first review round found getting past the list."""

    def test_a_cd_earlier_in_the_chain_is_followed(self):
        name = self.project.name
        self.refused(f"cd .. && rm -rf {name}")
        self.refused(f"Set-Location ..; Remove-Item -Recurse {name}", tool="PowerShell")
        self.refused("cd $SOMEWHERE && rm -rf build")
        self.refused("popd; rm -rf build")
        self.refused("cd build && rm -rf x")  # a line that moves deletes by full path
        self.allowed(f"cd build && rm -rf {self.project / 'build' / 'x'}")

    def test_a_quoted_chain_inside_a_wrapper_stays_whole(self):
        self.refused('bash -c "git push --force origin main; echo done"')
        self.refused("sh -c 'echo a && git push -f'")
        self.allowed('bash -c "git status; echo done"')

    def test_powershell_s_call_operator_and_invoke_expression_are_seen_through(self):
        for form in ("& git push --force origin main", ". git push -f", 'Invoke-Expression "git push --force"',
                     "iex 'git push --mirror'"):
            with self.subTest(form):
                self.refused(form, tool="PowerShell")
        self.allowed("& git push origin main", tool="PowerShell")

    def test_each_comma_separated_target_is_checked(self):
        self.refused("Remove-Item -Recurse build,../OtherRepo", tool="PowerShell")
        self.refused("rm -Recurse build,..", tool="PowerShell")
        self.allowed("Remove-Item -Recurse build/a,build/b", tool="PowerShell")

    def test_a_glob_followed_by_dot_dot_is_read_as_where_it_reaches(self):
        (self.project / "build" / "sub").mkdir()
        self.refused("rm -rf build/*/../../.git")
        self.refused("rm -rf build/nomatch*/../../..")
        self.allowed("rm -rf build/*")


class CompletionGaps(Base):
    """What the completion review found getting past the list."""

    def test_forced_pushes_through_ordinary_syntax(self):
        for form in ('bash -lc "git push --force origin main"', "(git push --force origin main)",
                     "git --git-dir .git push --force origin main", "echo $(git push -f)", "env GIT_TRACE=1 git push -f",
                     "nohup git push --force &", "time git push --mirror", "git --no-pager push --force"):
            with self.subTest(form):
                self.refused(form)
        self.allowed("(git push origin main)")
        self.allowed("git --git-dir .git push origin main")

    def test_powershell_colon_bound_targets_and_wrapped_context(self):
        self.refused("Remove-Item -Recurse -LiteralPath:..", tool="PowerShell")
        self.refused("Remove-Item -Recurse:$true -Path:..", tool="PowerShell")
        self.refused('pwsh -Command "rm -r build,../OtherRepo"')
        self.allowed("Remove-Item -Recurse -LiteralPath:build", tool="PowerShell")

    def test_env_s_folder_is_where_its_command_deletes(self):
        self.refused(f"env -C {self.outside} rm -rf sub")
        self.refused(f"env --chdir={self.outside} rm -rf sub")
        self.refused(f'env -C {self.outside} bash -c "rm -rf sub"')
        self.refused(f"env -C{self.outside} rm -rf sub")
        self.refused(f"env -C {self.outside} env -C . rm -rf sub")
        self.refused(f"env -C {self.base} env -C outside rm -rf sub")
        self.allowed(f"env -C {self.project} env -C build rm -rf {self.project / 'build' / 'x'}")
        self.refused(f'env -C {self.outside} bash -c "env -C . rm -rf sub"')
        self.refused(f'env -C {self.base} bash -c "env -C outside rm -rf sub"')
        self.allowed(f'env -C {self.project} bash -c "env -C build rm -rf {self.project / "build" / "x"}"')
        self.refused("env -Cbuild rm -rf x")  # a line that moves deletes by full path
        self.refused("env -C build rm -rf x")
        self.allowed(f"env -C build rm -rf {self.project / 'build' / 'x'}")
        self.refused(f'env -C {self.project} bash -c "cd {self.outside}; rm -rf sub"')
        self.refused(f'env -C {self.project} bash -c "cd {self.outside}; rm -rf {self.outside / "sub"}"')

    def test_a_failed_cd_leaves_the_folder_unknown(self):
        self.refused("cd build; cd missing; rm -rf ../.git")
        self.refused("cd nowhere && rm -rf x")
        self.allowed(f"cd build && rm -rf {self.project / 'build' / 'x'}")


class GitHub(Base):
    def test_deleting_or_publishing_a_repository_is_refused(self):
        for form in ("gh repo delete me/x --yes", "gh repo edit me/x --visibility public",
                     "gh api -X PATCH repos/me/x -f visibility=public", "gh api repos/me/x -X DELETE",
                     "gh api --method DELETE repos/me/x"):
            with self.subTest(form):
                self.refused(form)

    def test_ordinary_gh_passes(self):
        for form in ("gh pr list", "gh run view 1", "gh api repos/me/x/actions/runs", "gh repo view me/x",
                     "gh api -X DELETE repos/me/x/actions/caches/1"):
            with self.subTest(form):
                self.allowed(form)


class Deletes(Base):
    def test_inside_the_project_scratch_and_temp_passes(self):
        for form in ("rm -rf build", "rm -r build/x", f"rm -rf {self.project}/build", "Remove-Item -Recurse -Force build",
                     "rd /s /q build", f"rm -rf {self.scratch / 'run-1'}", "rm -rf ./build/*", "rm -f notes.txt"):
            with self.subTest(form):
                self.allowed(form, tool="PowerShell" if form.startswith(("Remove", "rd")) else "Bash")

    def test_outside_the_allowed_folders_is_refused(self):
        sibling = Slashed(str(self.project) + "-old")  # shares the project's name as a prefix
        sibling.mkdir()
        self.addCleanup(lambda: sibling.rmdir() if sibling.exists() else None)
        outside = self.outside
        for form in (f"rm -rf ..", f"rm -rf ../{self.project.name}-old", f"rm -rf {sibling}", f"rm -rf build {outside}",
                     f"Remove-Item -Recurse {outside}", f"rd /s /q {outside}", "rm -rf /", "rm -rf ~"):
            with self.subTest(form):
                self.refused(form, tool="PowerShell" if form.startswith(("Remove", "rd")) else "Bash")

    def test_the_project_itself_and_its_history_are_refused(self):
        for form in (f"rm -rf {self.project}", "rm -rf .", "rm -rf .git", "rm -rf ./.git/objects"):
            with self.subTest(form):
                self.refused(form)

    def test_a_link_inside_pointing_outside_is_followed_and_refused(self):
        link(self.outside, self.project / "build" / "away")
        self.refused("rm -rf build/away")

    def test_a_target_it_cant_resolve_is_refused(self):
        for form in ("rm -rf $TARGET", "rm -rf ${HOME_DIR}/x", "Remove-Item -Recurse $env:SOMEWHERE"):
            with self.subTest(form):
                self.assertIn("variable", self.refused(form, tool="PowerShell" if form.startswith("Remove") else "Bash"))


class Sends(Base):
    def test_mail_and_chat_sends_are_refused_and_drafts_pass(self):
        sends = ["mcp__claude_ai_Gmail__send_message", "mcp__claude_ai_Gmail__reply", "mcp__claude_ai_Gmail__forward",
                 "mcp__claude_ai_Microsoft_365__outlook_send_mail", "mcp__claude_ai_Microsoft_365__outlook_send_draft",
                 "mcp__claude_ai_Microsoft_365__outlook_forward_mail", "mcp__claude_ai_Microsoft_365__teams_send_chat_message",
                 "mcp__claude_ai_Microsoft_365__teams_send_channel_message",
                 "mcp__claude_ai_Microsoft_365__teams_reply_channel_message"]
        for name in sends:
            with self.subTest(name):
                self.assertIn("draft it instead", tg.decide({"tool_name": name, "tool_input": {}}))
        for name in ("mcp__claude_ai_Gmail__create_draft", "mcp__claude_ai_Microsoft_365__outlook_create_reply_draft",
                     "mcp__claude_ai_Microsoft_365__outlook_email_search", "mcp__claude_ai_Microsoft_Learn__microsoft_docs_fetch",
                     "WebFetch", "WebSearch", "Read"):
            with self.subTest(name):
                self.assertIsNone(tg.decide({"tool_name": name, "tool_input": {}}))


class FailsClosed(Base):
    def test_unreadable_input_is_refused_with_the_self_test_named(self):
        for raw in ("", "not json", "[1, 2]"):
            with self.subTest(raw):
                out = json.loads(tg.hook(raw))["hookSpecificOutput"]
                self.assertEqual(out["permissionDecision"], "deny")
                self.assertIn("--self-test", out["permissionDecisionReason"])

    def test_an_internal_error_refuses_what_might_be_protected_and_passes_the_rest(self):
        with mock.patch.object(tg, "decide_command", side_effect=RuntimeError("boom")):
            self.assertIn("couldn't decide", self.decide("git push origin main"))
            self.assertIn("couldn't decide", self.decide("rm -rf build"))
            self.assertIsNone(self.decide("ls -la"))

    def test_the_hook_runs_as_claude_code_runs_it(self):
        data = {"tool_name": "Bash", "tool_input": {"command": "git push --force"}, "cwd": str(self.project)}
        done = subprocess.run([sys.executable, str(ROOT / "scripts" / "tool-guard.py")], input=json.dumps(data),
                              capture_output=True, text=True, timeout=60)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(json.loads(done.stdout)["hookSpecificOutput"]["permissionDecision"], "deny")
        allowed = subprocess.run([sys.executable, str(ROOT / "scripts" / "tool-guard.py")],
                                 input=json.dumps({**data, "tool_input": {"command": "git status"}}),
                                 capture_output=True, text=True, timeout=60)
        self.assertEqual((allowed.returncode, allowed.stdout.strip()), (0, ""))

    def test_the_real_roots_include_scratch_and_temp(self):
        roots = [tg.canonical(r) for r in REAL_ROOTS()]
        self.assertIn(tg.canonical(tempfile.gettempdir()), roots)
        if os.name == "nt":
            self.assertIn(tg.canonical(r"R:\scratch"), roots)

    def test_the_self_test_passes_and_fails_as_it_should(self):
        done = subprocess.run([sys.executable, str(ROOT / "scripts" / "tool-guard.py"), "--self-test"],
                              capture_output=True, text=True, timeout=60)
        self.assertEqual(done.returncode, 0, done.stdout)
        with mock.patch.object(tg, "SELF_CASES", [("Bash", "git push --force", False)]):
            self.assertEqual(tg.self_test(), 1)


if __name__ == "__main__":
    unittest.main()
