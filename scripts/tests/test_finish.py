"""The finish line: what is left before implementation is complete, close-out work, wakes, and the .NET check."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "lib"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import todo_graph as tg  # noqa: E402
from test_todo_graph import Tree, todo_file  # noqa: E402

spec = importlib.util.spec_from_file_location("dotnet_final", ROOT / "scripts" / "dotnet-final-available.py")
dotnet = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dotnet)


def section(ref, path, phase=0, done=True, depends=(), needs=(), body="**Needs:** none"):
    number = int(ref.rsplit("§", 1)[1])
    receipt = f"docs/reviews/{ref.replace(' ', '-').replace('§', 's').lower()}.json" if done else ""
    return tg.Section(ref=ref, path=path, number=number, title=ref, phase=phase, depends=list(depends), needs=list(needs),
                      done=done, status="active", body=body, receipt=receipt, order=number)


class Evidence(unittest.TestCase):
    """Close-out evidence in a real git repository: it counts only when committed after the newest stamp it covers."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "-q")
        self.path = "todo/00-x/TODO-01-a.md"
        self.rows = {r.ref: r for r in (section("D00 T01 §1", self.path), section("D00 T01 §2", self.path))}
        self.receipts(1)

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.root), "-c", "user.name=t", "-c", "user.email=t@example.com", *args],
                              check=True, capture_output=True, text=True).stdout

    def write(self, rel, text):
        (self.root / rel).parent.mkdir(parents=True, exist_ok=True)
        (self.root / rel).write_text(text, encoding="utf-8")

    def commit(self, message):
        self.git("add", "-A")
        self.git("commit", "-qm", message)

    def receipts(self, generation):
        for row in self.rows.values():
            self.write(row.receipt, json.dumps({"ref": row.ref, "generation": generation}))
        self.commit(f"stamps {generation}")

    def close(self):
        self.write(tg.INDEX_FILE, f"# Index\n\n## Active TODOs\n\n## Completed TODOs\n\n- [TODO-01-a.md]({self.path})\n")
        self.write("docs/phase-runs/2026-11-08-phase-0.md", "# Phase 0\n\n## Closeout\n\nDone.\n")
        self.commit("close out")

    def test_nothing_closed_owes_the_file_and_the_phase(self):
        self.assertEqual(tg.pending_closeouts(self.root, self.rows), ([self.path], [0]))
        self.assertFalse(tg.finish(self.root, self.rows, {})["complete"])

    def test_closed_after_the_stamps_is_complete(self):
        self.close()
        self.assertEqual(tg.pending_closeouts(self.root, self.rows), ([], []))
        self.assertTrue(tg.finish(self.root, self.rows, {})["complete"])

    def test_a_closeout_older_than_a_new_stamp_is_stale(self):
        self.close()
        self.receipts(2)  # a section reopened and stamped again after its file and phase closed
        self.assertEqual(tg.pending_closeouts(self.root, self.rows), ([self.path], [0]))

    def test_a_second_closeout_after_a_repair_the_same_day_counts(self):
        self.close()
        self.receipts(2)
        run = self.root / "docs/phase-runs/2026-11-08-phase-0.md"
        run.write_text(run.read_text(encoding="utf-8") + "\n## Closeout\n\nRepaired and closed again.\n", encoding="utf-8")
        index = self.root / tg.INDEX_FILE
        index.write_text(index.read_text(encoding="utf-8") + f"- [TODO-01-a.md]({self.path}) closed again\n", encoding="utf-8")
        self.commit("close out again")
        self.assertEqual(tg.pending_closeouts(self.root, self.rows), ([], []))

    def test_reopening_a_file_takes_back_its_completed_line(self):
        self.close()
        text = tg.reopen_in_index(self.root, self.path)
        self.assertIsNotNone(text)
        active, completed = text.split("## Completed TODOs")
        self.assertIn("TODO-01-a.md", active)
        self.assertNotIn("TODO-01-a.md", completed)
        (self.root / tg.INDEX_FILE).write_text(text, encoding="utf-8")
        self.assertIsNone(tg.reopen_in_index(self.root, self.path), "a second reopen moved it again")

    def test_an_uncommitted_closeout_does_not_count(self):
        self.write("docs/phase-runs/2026-11-08-phase-0.md", "# Phase 0\n\n## Closeout\n\nDone.\n")
        self.assertIn(0, tg.pending_closeouts(self.root, self.rows)[1])

    def test_acceptance_rows_do_not_hold_complete_and_their_file_closes_in_acceptance(self):
        other = "todo/99-x/TODO-01-b.md"
        self.rows["D99 T01 §1"] = section("D99 T01 §1", other, phase=99, done=False)
        self.close()
        found = tg.finish(self.root, self.rows, {})
        self.assertTrue(found["complete"])
        self.assertEqual([a["ref"] for a in found["acceptance"]], ["D99 T01 §1"])
        self.assertEqual(found["files_waiting_on_acceptance"], [other])


class Blockers(unittest.TestCase):
    def test_root_blockers_and_their_states(self):
        rows = {r.ref: r for r in (
            section("D00 T01 §1", "a.md", done=False, needs=["owner-decision"]),
            section("D00 T01 §2", "a.md", done=False, depends=["D00 T01 §1"]),
            section("D00 T01 §3", "a.md", done=False, depends=["D00 T01 §2"]),
            section("D00 T01 §4", "a.md", done=False, body="> **Parked:** 2026-11-08: x\n\n**Needs:** none"),
            section("D00 T01 §5", "a.md", done=False, depends=["D00 T01 §4", "D00 T01 §1"]))}
        self.assertEqual(tg.root_blockers(rows["D00 T01 §3"], rows), ["D00 T01 §1"])
        self.assertEqual(tg.root_blockers(rows["D00 T01 §5"], rows), ["D00 T01 §1", "D00 T01 §4"])
        with mock.patch.object(tg, "pending_closeouts", return_value=([], [])):
            found = tg.finish(Path("."), rows, {})
        self.assertEqual(found["roots"], {"D00 T01 §1": "waiting-person", "D00 T01 §4": "parked"})
        self.assertEqual(found["blocked"]["D00 T01 §3"], ["D00 T01 §1"])


class CloseOutWork(Tree):
    """Once every section is stamped, close-out work is what `query next` gives, as after a crash past the last stamp."""

    def test_close_out_work_follows_the_last_stamp(self):
        rows = {r.ref: r for r in (section("D00 T01 §1", "todo/00-x/TODO-01-a.md"),)}
        with mock.patch.object(tg, "pending_closeouts", return_value=(["todo/00-x/TODO-01-a.md"], [0])):
            first = tg.next_work(rows, {}, self.root)
            items = tg.work_items(self.root, rows)
        self.assertEqual(first["state"], "needs-verification")
        self.assertEqual([i["state"] for i in items], ["needs-verification", "needs-closeout"])
        with mock.patch.object(tg, "pending_closeouts", return_value=([], [0])):
            self.assertEqual(tg.next_work(rows, {}, self.root)["ref"], "phase 0")
        with mock.patch.object(tg, "pending_closeouts", return_value=([], [])):
            self.assertIsNone(tg.next_work(rows, {}, self.root))


class Wakes(Tree):
    def setUp(self):
        super().setUp()
        self.write(todo_file([(1, "A", 0, "third-party", "--")]))
        (self.root / "todo" / "wakes.json").write_text(json.dumps(
            [{"section": "D00 T01 §1", "need": "third-party", "check": ["python", "-c", "pass"]}]), encoding="utf-8")

    def runner(self, code=0, raises=None):
        def run(command, **kwargs):
            if raises:
                raise raises
            return subprocess.CompletedProcess(command, code, "", "")
        return run

    def test_a_passing_check_is_listed_and_a_failing_missing_or_slow_one_is_not(self):
        rows = tg.graph(self.root)
        self.assertEqual(tg.wake_check(self.root, rows, self.runner(0)), [{"section": "D00 T01 §1", "need": "third-party"}])
        for run in (self.runner(1), self.runner(raises=FileNotFoundError("no such check")),
                    self.runner(raises=subprocess.TimeoutExpired("check", 60))):
            with self.subTest(run=run):
                self.assertEqual(tg.wake_check(self.root, rows, run), [])

    def test_apply_clears_the_need_once_and_a_repeat_finishes_nothing_twice(self):
        applied = tg.wake_apply(self.root, "2026-11-08", self.runner(0))
        self.assertEqual(applied, [{"section": "D00 T01 §1", "need": "third-party"}])
        row = tg.graph(self.root)["D00 T01 §1"]
        self.assertEqual(row.needs, [])
        self.assertIn("> **Woken:** 2026-11-08", row.body)
        self.assertEqual(tg.wake_apply(self.root, "2026-11-08", self.runner(0)), [])  # interrupted or repeated: nothing left

    def test_a_failing_check_changes_nothing(self):
        before = (self.root / "todo/00-workspace/TODO-01-a.md").read_text(encoding="utf-8")
        self.assertEqual(tg.wake_apply(self.root, "2026-11-08", self.runner(1)), [])
        self.assertEqual((self.root / "todo/00-workspace/TODO-01-a.md").read_text(encoding="utf-8"), before)

    def test_a_malformed_wakes_file_refuses(self):
        (self.root / "todo" / "wakes.json").write_text('{"section": 1}', encoding="utf-8")
        with self.assertRaises(tg.Refused):
            tg.load_wakes(self.root)

    def test_the_real_wakes_file_names_open_sections_and_their_needs(self):
        rows = tg.graph(ROOT)
        for wake in tg.load_wakes(ROOT):
            with self.subTest(wake=wake["section"]):
                self.assertIn(wake["section"], rows)
                row = rows[wake["section"]]
                # Still waiting on its Need, or already woken from it: either way the wake names a real Need.
                self.assertTrue(wake["need"] in row.needs or f"its `{wake['need']}` check" in row.body,
                                f"{wake['section']} neither needs {wake['need']} nor was woken from it")


import run_guard as guard  # noqa: E402


class WakeToResume(Wakes):
    """The tick sees a passing wake and resumes the run; the resumed session applies it first; the row is ready."""

    def test_tick_resumes_and_the_session_s_apply_frees_the_row(self):
        run = self.runner(0)
        rows = lambda: guard.runnable([{"ref": r.ref, "state": tg.section_disposition(r, tg.graph(self.root), {}, self.root)[0]}
                                       for r in tg.graph(self.root).values() if not r.done])
        applied = []
        spawned = []

        def spawn(state):  # the resumed session: `wake --apply` is its first step, then it commits through its steps
            applied.extend(tg.wake_apply(self.root, "2026-11-08", run))
            spawned.append(dict(state))
            return {"pid": 1, "started": 2}
        env = guard.Env(rows=rows, signature=lambda: "one", alive=lambda s: False, live_here=lambda: [],
                        child_alive=lambda c: False, spawn=spawn,
                        finish=lambda: {"state": "waiting", "detail": "waiting on D00 T01 §1 (waiting-person)"},
                        wakes=lambda: tg.wake_check(self.root, tg.graph(self.root), run))
        state = {"active": True, "status": "active", "session_id": "me"}
        self.assertEqual(rows(), [])  # waiting on its third-party Need
        state.update(wait_until=5000.0, wait_reason="rows-waiting")
        self.assertIn("attempt 1", guard.watch(state, env, now=1000))  # a wake passed before the deadline
        self.assertEqual(applied, [{"section": "D00 T01 §1", "need": "third-party"}])
        self.assertEqual(rows(), ["D00 T01 §1"], "the row the wake freed isn't runnable")
        # The session's commit failed: the change is still in the tree, and a repeat finds nothing left to do.
        self.assertEqual(tg.wake_apply(self.root, "2026-11-08", run), [])
        self.assertEqual(tg.graph(self.root)["D00 T01 §1"].needs, [])


class CrashAfterTheLastStamp(Evidence):
    """Every section stamped, then the session died: the watchdog resumes the close-out work, and the run ends complete."""

    def test_the_run_resumes_the_close_out_and_then_reads_complete(self):
        rows = lambda: guard.runnable(tg.work_items(self.root, self.rows))
        finish = lambda: ({"state": "complete", "detail": "CI proven", "report": "# Acceptance"}
                          if tg.finish(self.root, self.rows, {})["complete"] else {"state": "unknown"})
        did = []

        def spawn(state):  # the resumed session takes the close-out work query next gives it
            item = tg.next_work(self.rows, {}, self.root)
            did.append(item["state"])
            self.close()
            return {"pid": 1, "started": 2}
        written = []
        env = guard.Env(rows=rows, signature=lambda: str(len(did)), alive=lambda s: False, live_here=lambda: [],
                        child_alive=lambda c: False, spawn=spawn, finish=finish,
                        write_acceptance=lambda kind, text: written.append(kind) or "docs/acceptance/x.md")
        state = {"active": True, "status": "active", "session_id": "me"}
        self.assertEqual(rows(), [f"file {self.path}", "phase 0"])
        self.assertIn("attempt 1", guard.watch(state, env, now=1000))
        self.assertEqual(did, ["needs-verification"])
        self.assertEqual(rows(), [])
        self.assertEqual(guard.watch(state, env, now=2000), "complete")
        self.assertEqual((state["status"], written), ("complete", ["complete"]))


class InterruptedAcceptance(unittest.TestCase):
    def test_a_write_cut_off_leaves_the_run_to_retry_and_the_retry_succeeds(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "docs" / "acceptance").mkdir(parents=True)
            (root / "docs" / "acceptance" / "2026-11-08.md.999.tmp").write_text("half", encoding="utf-8")  # a crash's leftover
            real = os.replace
            calls = []

            def cut_off(source, target):
                calls.append(target)
                if len(calls) == 1:
                    raise OSError("power lost mid-write")
                return real(source, target)
            with mock.patch.object(guard.os, "replace", side_effect=cut_off):
                with self.assertRaises(OSError):
                    guard.write_acceptance("complete", "# done\n", root, "2026-11-08")
                path = guard.write_acceptance("complete", "# done\n", root, "2026-11-08")
            self.assertEqual((root / path).read_text(encoding="utf-8"), "# done\n")
            self.assertEqual(path, "docs/acceptance/2026-11-08.md")


class DotnetFinal(unittest.TestCase):
    def answer(self, stdout, code=0):
        return lambda command, **kwargs: subprocess.CompletedProcess(command, code, stdout, "")

    def test_a_final_sdk_passes_and_a_release_candidate_does_not(self):
        with mock.patch.object(dotnet.shutil, "which", return_value="dotnet"):
            self.assertEqual(dotnet.main(self.answer("10.0.403 [C:\\sdk]\n11.0.100 [C:\\sdk]\n")), 0)
            self.assertEqual(dotnet.main(self.answer("11.0.100-rc.2.26504.105 [C:\\sdk]\n")), 1)
            self.assertEqual(dotnet.main(self.answer("", code=1)), 1)

    def test_no_dotnet_fails_and_says_so(self):
        with mock.patch.object(dotnet.shutil, "which", return_value=None), \
                mock.patch("sys.stdout", new_callable=__import__("io").StringIO) as out:
            self.assertEqual(dotnet.main(), 1)
        self.assertIn("isn't installed", out.getvalue())


if __name__ == "__main__":
    unittest.main()
