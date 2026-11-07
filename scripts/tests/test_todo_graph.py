"""Tests for scripts/lib/todo_graph.py and scripts/todo-graph.py: the graph refuses malformed work and stamps only real evidence."""
import json
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

REPO = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts" / "lib"))
import todo_graph as tg  # noqa: E402

ADJACENCY = ("**Adjacency:** list=not-applicable (fixture); document=not-applicable (fixture); "
             "settings=not-applicable (fixture); reporting=not-applicable (fixture); "
             "notifications=not-applicable (fixture); permissions=not-applicable (fixture); "
             "audit=not-applicable (fixture); exchange=not-applicable (fixture); "
             "reverse=not-applicable (fixture)")


def section(n, title, phase, needs, extra=""):
    return (f"## {n}. {title}\n\n**Phase:** {phase}\n**Needs:** {needs}\n**Surface:** no\n\n**Build order:**\n\n"
            f"1. Do it. Done when: it shows.\n\n- [ ] Work.\n- [ ] Commit: `x: {title.lower()}`\n\n"
            f"**Test checkpoint:** `true` exits 0.{extra}\n")


def envelope(n, reviewer="astra (`m` via `codex`)", overall="approve"):
    """One round as scripts/review-dispatch.py records it (without the optional Lines and Digest fields), at the
    uniform schedule the Stamp fixture pins."""
    return (f"## Round {n}: {reviewer}\n\n- Writer: claude\n- Run: 2026-10-21T00:00:00Z\n- Effort: low\n"
            f"- Lenses: adversarial\n\nReview text.\n\nOVERALL: {overall}\n\n")


# A reviewer pin whose schedule runs every round at the same effort and lens, so tests that are not about the schedule
# need not follow one; SCHEDULE_PIN is the live shape the schedule tests use.
UNIFORM_PIN = """schema_version = 1

[reviewers.astra]
cli = "codex"
model = "m"
effort = "medium"

[routes]
claude = ["astra"]

[schedule]
severity_stop_after = 3
max_repair_cycles = 3
plan = { effort = "medium", lenses = ["plan"] }
completion = { effort = "medium" }
rounds = [{ effort = "low", lenses = ["adversarial"] }]
rotation = { effort = "low", lenses = ["adversarial"] }
escalation = { effort = "low", lenses = ["adversarial"] }
"""
SCHEDULE_PIN = UNIFORM_PIN.split("[schedule]")[0] + """[schedule]
severity_stop_after = 3
plan = { effort = "medium", lenses = ["plan"] }
completion = { effort = "medium" }
rounds = [{ effort = "high", lenses = ["adversarial", "integration", "source-defect", "plan-delivery"] },
          { effort = "medium", lenses = ["adversarial"] }, { effort = "low", lenses = ["adversarial"] }]
rotation = { effort = "low", lenses = ["consistency", "quality", "design", "optimization", "record"] }
escalation = { effort = "medium", lenses = ["adversarial"] }
"""
STARTED = "> **Started:** 2026-10-23T00:00Z\n\n"


def todo_file(rows, *, ident="alpha", domain="00-workspace", status="active", depends_on=None,
              table_override=None, inputs="- i"):
    """rows: (number, title, phase, needs, depends) tuples."""
    front = ["schema_version: 1", f"id: {ident}", f"domain: {domain}", f"status: {status}",
             'title: "TODO-01 -- A"']
    if depends_on is not None:
        front.append("depends_on: " + json.dumps(depends_on))
    table = table_override or "\n".join(
        f"| {i} | §{n} | {t} | {dep} | [ ] |" for i, (n, t, _, _, dep) in enumerate(rows, 1))
    bodies = "\n".join(section(n, t, ph, nd) for n, t, ph, nd, _ in rows)
    return ("---\n" + "\n".join(front) + "\n---\n\n# TODO-01 -- A\n\n> **Goal:** g.\n\n"
            "> **Current state:** c.\n\n## Inputs\n\n" + inputs + "\n\n## Outcome\n\n- o\n\n"
            + ADJACENCY + "\n\n"
            "## Implementation Order\n\n| Order | Section | Deliverable | Depends On | Status |\n"
            "| ----- | ------- | ----------- | ---------- | ------ |\n"
            + table + "\n\n" + bodies + "\n## Verification\n\n- v\n")


class Tree(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.tmp.name)
        (self.root / "todo/00-workspace").mkdir(parents=True)

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, text, name="TODO-01-a.md", domain="00-workspace", index=True):
        d = self.root / "todo" / domain
        d.mkdir(parents=True, exist_ok=True)
        (d / name).write_text(text, encoding="utf-8")
        if index:
            self.index(name, domain)

    def index(self, name, domain="00-workspace"):
        domain_index = self.root / "todo" / domain / "INDEX.md"
        existing = domain_index.read_text(encoding="utf-8") if domain_index.exists() else "# Index\n"
        if name not in existing:
            domain_index.write_text(existing + f"- {name}\n", encoding="utf-8")
        root_index = self.root / "todo/TODO-00-INDEX.md"
        root_existing = root_index.read_text(encoding="utf-8") if root_index.exists() else "# Index\n"
        entry = f"{domain}/{name}"
        if entry not in root_existing:
            root_index.write_text(root_existing + f"- {entry}\n", encoding="utf-8")

    def refuses(self, fragment):
        with self.assertRaises(tg.Refused) as ctx:
            tg.graph(self.root)
        self.assertIn(fragment, str(ctx.exception))


class Graph(Tree):
    def test_valid_tree_orders_by_phase_and_dependency(self):
        self.write(todo_file([(1, "Late", 1, "none", "--"), (2, "Early", 0, "none", "--"),
                              (3, "After early", 0, "windows-host", "§2")]))
        rows = tg.graph(self.root)
        self.assertEqual([r.ref for r in tg.ordered(rows)],
                         ["D00 T01 §2", "D00 T01 §3", "D00 T01 §1"])
        self.assertEqual(tg.disposition(rows["D00 T01 §2"], rows), 0)
        self.assertEqual(tg.disposition(rows["D00 T01 §3"], rows), 4)

    def test_table_order_decides_among_ready_sections(self):
        table = "| 1 | §2 | B | -- | [ ] |\n| 2 | §1 | A | -- | [ ] |"
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")],
                             table_override=table))
        self.assertEqual([r.ref for r in tg.ordered(tg.graph(self.root))],
                         ["D00 T01 §2", "D00 T01 §1"])

    def test_empty_tree_refuses(self):
        self.refuses("missing or empty")

    def test_malformed_row_is_not_empty_work(self):
        self.write(todo_file([(1, "A", 0, "none", "--")], table_override="| 1 | §1 | A | -- | [?] |"))
        self.refuses("malformed implementation row")

    def test_row_without_body_refuses(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace(
            "| 1 | §1 | A | -- | [ ] |", "| 1 | §1 | A | -- | [ ] |\n| 2 | §2 | B | -- | [ ] |")
        self.write(text)
        self.refuses("row/body mismatch")

    def test_cycle_refuses(self):
        self.write(todo_file([(1, "A", 0, "none", "§2"), (2, "B", 0, "none", "§1")]))
        self.refuses("dependency cycle")

    def test_prerequisite_in_a_later_phase_refuses(self):
        self.write(todo_file([(1, "A", 0, "none", "§2"), (2, "B", 1, "none", "--")]))
        self.refuses("prerequisite in later phase")

    def test_unknown_need_refuses(self):
        self.write(todo_file([(1, "A", 0, "novamira", "--")]))
        self.refuses("unknown/duplicate Needs")

    def test_commit_must_be_the_last_item(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]).replace(
            "- [ ] Commit: `x: a`", "- [ ] Commit: `x: a`\n- [ ] After."))
        self.refuses("Commit last")

    def test_table_line_without_its_leading_pipe_refuses(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace(
            "| 1 | §1 | A | -- | [ ] |", "| 1 | §1 | A | -- | [ ] |\n | 2 | §999 | Orphan | -- | [ ] |")
        self.write(text)
        self.refuses("without a leading pipe")

    def test_unknown_level_two_heading_refuses(self):
        for bad in ("## Remaining work", "## Inputs ", "##\tNotes", "## Inputs", "## Verification", "## Outcome"):
            self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")]).replace(
                "## 2. B\n", bad + "\n\n- [ ] Unfinished required work.\n\n## 2. B\n"))
            self.refuses("level-two headings must be")

    def test_deferral_marker_outside_a_blockquote_refuses(self):
        for bad in ("**Deferred:** Unimplemented required work.", ">**Resolved:** done -> XREF: §1",
                    "Note **Deferred:** later -> XREF: §2"):
            self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")]).replace(
                "- [ ] Work.", "- [ ] Work.\n\n" + bad + "\n", 1))
            self.refuses("must open a blockquote line and say something")

    def test_checklist_item_in_another_spelling_refuses(self):
        for bad in ("1. [ ] Unfinished work.", "2) [x] Done elsewhere.", "> - [ ] Quoted work.", ">- [ ] Quoted.",
                    "  - [ ] Indented work.", "> 1. [ ] Quoted and numbered."):
            self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")]).replace(
                "- [ ] Work.", "- [ ] Work.\n" + bad, 1))
            self.refuses("malformed checklist item")

    def test_two_deferral_markers_on_one_line_refuse(self):
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")]).replace(
            "**Test checkpoint:** `true` exits 0.\n\n## 2. B",
            "**Test checkpoint:** `true` exits 0.\n> **Resolved:** old -> XREF: §2 **Deferred:** new unowned work.\n\n## 2. B"))
        self.refuses("one Deferred or Resolved marker per line")

    def test_reference_run_into_more_text_does_not_resolve(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        rows = tg.graph(self.root)
        for text in ("D00 T01 §1oops", "D00 T01 §1.5", "todo/00-workspace/TODO-01-a.md §1x"):
            self.assertEqual(tg.extract_ref(text, rows), (None, 2))
        for text in ("D00 T01 §1", "process D00 T01 §1.", "D00 T01 §1 (run file x)", "| 1 | D00 T01 §1 | A |"):
            self.assertEqual(tg.extract_ref(text, rows), ("D00 T01 §1", 0))

    def test_empty_deferral_marker_refuses(self):
        for bad in ("> **Deferred:**", "> **Resolved:**   "):
            self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")]).replace(
                "**Test checkpoint:** `true` exits 0.\n\n## 2. B", "**Test checkpoint:** `true` exits 0.\n" + bad + "\n\n## 2. B"))
            self.refuses("must open a blockquote line and say something")

    def test_malformed_xref_does_not_resolve_to_another_section(self):
        for bad in ("-> XREF: D00 T999 §1", "-> XREF: §1x", "-> XREF: D00 T01 §1.5", "-> XREF:§1", "-> XREF: D00 T01 §1 -> XREF: D00 T99 §999",
                    "-> XREF: none yet"):
            self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")], inputs="- i " + bad))
            with self.assertRaises(tg.Refused):
                tg.graph(self.root)
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")],
                             inputs="- see §2 -> XREF: §2 (the second)"))
        tg.graph(self.root)

    def test_malformed_checklist_item_refuses(self):
        for bad in ("- [?] Work.", "- [] Work.", "- [X] Work.", "- Work without checkbox."):
            self.write(todo_file([(1, "A", 0, "none", "--")]).replace("- [ ] Work.", bad))
            self.refuses("malformed checklist item")

    def test_empty_build_order_refuses(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace(
            "1. Do it. Done when: it shows.\n\n- [ ] Work.",
            "- [ ] Work. Done when: it shows.")
        self.write(text)
        self.refuses("executable Build order required")
        missing_second = todo_file([(1, "A", 0, "none", "--")]).replace(
            "1. Do it. Done when: it shows.",
            "1. Do it. Done when: it shows.\n2. Second step without acceptance criterion.")
        self.write(missing_second)
        self.refuses("executable Build order required")

    def test_empty_field_does_not_borrow_next_line(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace(
            "**Test checkpoint:** `true` exits 0.",
            "**Test checkpoint:**\n> **Deferred:** `later` -> XREF: D00 T01 §1")
        self.write(text)
        self.refuses("missing Test checkpoint field")

    def test_missing_todo_heading_refuses(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace("# TODO-01 -- A\n\n", "")
        self.write(text)
        self.refuses("missing TODO heading")

    def test_section_zero_refuses(self):
        self.write(todo_file([(0, "Zero", 0, "none", "--")]))
        self.refuses("malformed implementation row")

    def test_waiting_needs_are_not_ready(self):
        self.write(todo_file([(1, "A", 0, "owner-decision, windows-host", "--")]))
        rows = tg.graph(self.root)
        self.assertEqual(tg.disposition(rows["D00 T01 §1"], rows), 4)

    def test_operator_needs_are_not_ready(self):
        self.write(todo_file([(1, "A", 99, "operator", "--")]))
        rows = tg.graph(self.root)
        self.assertEqual(tg.disposition(rows["D00 T01 §1"], rows), 4)

    def test_phase_98_refuses(self):
        # The closed phase list is the SDK's: a phase outside it refuses.
        self.write(todo_file([(1, "Cloud", 98, "none", "--")]))
        self.refuses("invalid Phase")

    def test_phase_outside_the_closed_list_refuses(self):
        self.write(todo_file([(1, "A", 50, "none", "--")]))
        self.refuses("invalid Phase")

    def test_draft_file_is_not_ready(self):
        self.write(todo_file([(1, "A", 0, "none", "--")], status="draft"))
        rows = tg.graph(self.root)
        self.assertEqual(tg.disposition(rows["D00 T01 §1"], rows), 4)

    def test_file_dependency_reaches_every_section(self):
        self.write(todo_file([(1, "A", 0, "none", "--")], ident="first"))
        self.write(todo_file([(1, "B", 1, "none", "--"), (2, "C", 1, "none", "--")],
                             ident="second", depends_on=["first"]), name="TODO-02-b.md")
        rows = tg.graph(self.root)
        self.assertEqual(rows["D00 T02 §2"].depends, ["D00 T01 §1"])

    def test_checked_row_without_stamp_refuses(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]).replace("| [ ] |", "| [x] |"))
        self.refuses("checkbox/stamp/receipt mismatch")

    def test_missing_from_index_refuses(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]), index=False)
        (self.root / "todo/TODO-00-INDEX.md").write_text("# Index\n", encoding="utf-8")
        (self.root / "todo/00-workspace/INDEX.md").write_text("# Index\n", encoding="utf-8")
        self.refuses("missing from domain index")

    def test_root_index_requires_domain_qualified_entry(self):
        self.write(todo_file([(1, "A", 0, "none", "--")], ident="first"))
        self.write(todo_file([(1, "B", 1, "none", "--")], ident="second", domain="01-engine"),
                   name="TODO-01-a.md", domain="01-engine", index=False)
        (self.root / "todo/01-engine/INDEX.md").write_text("# Index\n- TODO-01-a.md\n", encoding="utf-8")
        self.refuses("missing from root index: 01-engine/TODO-01-a.md")

    def test_adjacency_missing_key_refuses(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace(
            "reverse=not-applicable (fixture)", "reverse=not-applicable")
        self.write(text)
        self.refuses("adjacency entry malformed")

    def test_adjacency_blanket_form_is_valid(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace(
            ADJACENCY, "**Adjacency:** all=not-applicable (fixture with no record of its own)")
        self.write(text)
        self.assertEqual(len(tg.graph(self.root)), 1)

    def test_adjacency_blanket_reason_may_contain_semicolons(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace(
            ADJACENCY, "**Adjacency:** all=not-applicable (tooling over files; its records are the "
                       "files themselves, owned by their own sections)")
        self.write(text)
        self.assertEqual(len(tg.graph(self.root)), 1)

    def test_one_sided_xref_refuses(self):
        self.write(todo_file([(1, "A", 0, "none", "--")], ident="first"))
        self.write(todo_file([(1, "B", 0, "none", "--")], ident="second",
                             inputs="- -> XREF: D00 T01 §1 -- consumes it"), name="TODO-02-b.md")
        self.refuses("one-sided XREF")

    def test_reciprocal_xref_is_valid(self):
        self.write(todo_file([(1, "A", 0, "none", "--")], ident="first",
                             inputs="- -> XREF: D00 T02 §1 -- feeds it"))
        self.write(todo_file([(1, "B", 0, "none", "--")], ident="second",
                             inputs="- -> XREF: D00 T01 §1 -- consumes it"), name="TODO-02-b.md")
        self.assertEqual(len(tg.graph(self.root)), 2)

    def test_surface_yes_without_fidelity_refuses(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace("**Surface:** no", "**Surface:** yes")
        self.write(text)
        self.refuses("missing Fidelity field")

    def test_freeze_check_without_frozen_refuses(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace(
            "**Test checkpoint:**", "**Freeze check:** fixtures hold.\n\n**Test checkpoint:**")
        self.write(text)
        self.refuses("Freeze check must match frozen")

    def test_deferral_without_owner_refuses(self):
        text = todo_file([(1, "A", 0, "none", "--")]).replace(
            "**Test checkpoint:**", "> **Deferred:** later, somehow\n\n**Test checkpoint:**")
        self.write(text)
        self.refuses("without a resolving owner")

    def test_max_sections_per_file_refuses(self):
        rows = [(n, f"S{n}", 0, "none", "--") for n in range(1, 57)]
        self.write(todo_file(rows))
        self.refuses("over 55 sections")

    def test_max_items_per_section_refuses(self):
        many = "\n".join(f"- [ ] Item {i}." for i in range(1, 31))
        text = todo_file([(1, "A", 0, "none", "--")]).replace("- [ ] Work.", many)
        self.write(text)
        self.refuses("2-30 items")

    def test_deliverable_or_title_over_max_len_refuses(self):
        self.write(todo_file([(1, "X" * 89, 0, "none", "--")]))
        self.refuses("deliverable/title over 88 chars (wraps raw md table): D00 T01 §1")
        # The limit is the title's, which the table shows; the "## N. " prefix appears in no table.
        self.write(todo_file([(1, "X" * 88, 0, "none", "--")]))
        tg.graph(self.root)

    def test_superseded_without_successor_refuses(self):
        self.write(todo_file([(1, "A", 0, "none", "--")], status="superseded"))
        self.refuses("superseded TODO must name its successor")

    def test_projection_check_detects_drift(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        tg.projections(self.root, sync=True)
        tg.projections(self.root)
        path = self.root / "todo/00-workspace/TODO-01-a.md"
        path.write_text(path.read_text(encoding="utf-8").replace("**Phase:** 0", "**Phase:** 2"),
                        encoding="utf-8")
        with self.assertRaises(tg.Refused):
            tg.projections(self.root)

    def test_resolve_accepts_windows_and_posix_paths_and_plan_rows(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        rows = tg.graph(self.root)
        for query in (
            "D00 T01 §1",
            "| [ ] | D00 T01 §1 | A | none | 2 |",
            "todo/00-workspace/TODO-01-a.md §1",
            r"R:\GitHub\Project\todo\00-workspace\TODO-01-a.md §1",
        ):
            self.assertEqual(tg.extract_ref(query, rows), ("D00 T01 §1", 0))
        self.assertEqual(tg.extract_ref(r"R:\GitHub\Project\todo\00-workspace\TODO-01-a.md §9", rows),
                         ("D00 T01 §9", 1))
        self.assertEqual(tg.extract_ref("no reference here", rows), (None, 2))

    def test_deep_dependency_chain_validates_without_recursion_error(self):
        for file_num in range(1, 21):
            rows = []
            for sec_num in range(1, 56):
                if file_num == 20 and sec_num == 55:
                    dep = "--"
                elif sec_num < 55:
                    dep = f"D00 T{file_num:02d} §{sec_num + 1}"
                else:
                    dep = f"D00 T{file_num + 1:02d} §1"
                rows.append((sec_num, f"S{sec_num}", 0, "none", dep))
            self.write(todo_file(rows, ident=f"file-{file_num:02d}"),
                       name=f"TODO-{file_num:02d}-chain.md")
        parsed = tg.graph(self.root)
        self.assertEqual(len(parsed), 1100)
        ordered_refs = [r.ref for r in tg.ordered(parsed)]
        self.assertEqual(len(ordered_refs), 1100)
        self.assertEqual(ordered_refs[0], "D00 T20 §55")
        self.assertEqual(ordered_refs[-1], "D00 T01 §1")


def exhaust(root, path):
    """The cycle's rounds run out with a P1 open, at the cap the graph itself reads for a record without a policy
    (counting rounds already there): the one review dead end that still repairs, since completion gaps are fixed or
    filed in their own cycle."""
    dispatch = tg.review_dispatch()
    path = pathlib.Path(path)
    found = dispatch.read_findings(path.read_text(encoding="utf-8")) if path.exists() else None
    cap = tg.round_cap(root) - (len([e for e in found["rounds"] if dispatch.returned_review(e)]) if found else 0)
    result = {"writer": "claude", "writer_model": "claude-opus-5-5", "reviewer": "astra", "cli": "codex", "model": "m",
              "effort": "medium", "execution": "returned", "attempt_reviewer": "astra", "attempt_model": "m",
              "attempt_cli": "codex", "failover": [], "lenses": ["adversarial"],
              "review": "1. A finding.\n\nHIGHEST: P1\nOVERALL: needs-attention"}
    path.parent.mkdir(parents=True, exist_ok=True)
    for _ in range(cap):
        dispatch.record(path, result, "2026-11-07T00:00:00Z")


class Dispositions(Tree):
    CAPS = {"windows-host": True, "arm64-host": False, "office": False, "libreoffice": False, "display-session": False}

    def state(self, needs="none", caps=None, dep="--", n=1, rows=None):
        self.write(todo_file([(1, "A", 0, needs, "--")] if n == 1 else [(1, "A", 0, "none", "--"), (2, "B", 0, needs, dep)]))
        rows = tg.graph(self.root)
        return tg.section_disposition(rows[f"D00 T01 §{n}"], rows, caps or self.CAPS, self.root)

    def record(self, name="D00-T01-s1.md"):
        folder = self.root / "docs" / "reviews" / "00-x"
        folder.mkdir(parents=True, exist_ok=True)
        return folder / name

    def result(self, review):
        return {"writer": "claude", "writer_model": "claude-opus-5-5", "reviewer": "astra", "cli": "codex", "model": "m",
                "effort": "medium", "execution": "returned", "attempt_reviewer": "astra", "attempt_model": "m", "attempt_cli": "codex", "failover": [], "lenses": ["adversarial"], "review": review}

    def completion(self, verdict, name="D00-T01-s1.md"):
        tg.review_dispatch().record_completion(self.record(name), self.result(f"Text.\n\nCOMPLETION: {verdict}"), "2026-11-07T00:00:00Z", "a" * 40)

    def exhaust(self, name="D00-T01-s1.md"):
        exhaust(self.root, self.record(name))

    def plan(self, name):
        tg.review_dispatch().record_completion(self.record(name), self.result("Text.\n\nPLAN: complete\nOVERALL: approve"), "2026-11-07T00:00:00Z", "a" * 40, kind="plan")

    def rounds(self, count, highest="P1", overall="needs-attention", name="D00-T01-s1.md"):
        for _ in range(count):
            review = f"### adversarial\n\n## adversarial\n\n1. A finding.\n\nHIGHEST: {highest}\nOVERALL: {overall}"
            tg.review_dispatch().record(self.record(name), self.result(review), "2026-11-07T00:00:00Z")

    def test_every_need_in_the_closed_list(self):
        expected = {"windows-host": "ready", "arm64-host": "waiting-host", "display-session": "waiting-host",
                    "office": "waiting-host", "libreoffice": "waiting-host", "store-account": "waiting-person",
                    "attended": "waiting-person", "owner-decision": "waiting-person", "third-party": "waiting-person",
                    "operator": "waiting-person"}
        self.assertEqual(set(expected), tg.NEEDS)
        for need, state in expected.items():
            with self.subTest(need):
                self.assertEqual(self.state(need)[0], state)

    def test_a_person_need_beats_a_ready_host(self):
        self.assertEqual(self.state("windows-host, attended")[0], "waiting-person")

    def test_a_display_session_row_follows_the_session(self):
        self.assertEqual(self.state("display-session", {**self.CAPS, "display-session": True})[0], "ready")
        state, reason = self.state("display-session")
        self.assertEqual(state, "waiting-host")
        self.assertIn("display-session", reason)

    def test_an_open_prerequisite_waits_and_is_named(self):
        state, reason = self.state(dep="§1", n=2)
        self.assertEqual(state, "waiting-dependency")
        self.assertIn("D00 T01 §1", reason)

    def test_a_parked_section_is_parked(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]).replace("## 1. A\n\n", "## 1. A\n\n> **Parked:** 2026-11-07: x\n\n"))
        rows = tg.graph(self.root)
        self.assertEqual(tg.section_disposition(rows["D00 T01 §1"], rows, self.CAPS, self.root)[0], "parked")

    def test_completion_gaps_are_fixed_or_filed_in_their_cycle(self):
        self.completion("gaps")
        state, reason = self.state()
        self.assertEqual(state, "needs-filing")
        self.assertIn("fixed or filed", reason)

    def test_a_complete_completion_stays_ready(self):
        self.completion("complete")
        self.assertEqual(self.state()[0], "ready")

    def test_section_one_is_not_confused_with_section_ten(self):
        self.exhaust("D00-T01-s10.md")
        self.assertEqual(self.state()[0], "ready")

    def text(self):
        return (self.root / "todo/00-workspace/TODO-01-a.md").read_text(encoding="utf-8")

    def row(self):
        rows = tg.graph(self.root)
        return rows["D00 T01 §1"], rows

    def disposed(self, caps=None):
        row, rows = self.row()
        return tg.section_disposition(row, rows, caps or self.CAPS, self.root)

    def test_only_the_current_cycle_counts(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        self.exhaust()
        self.assertEqual(self.disposed()[0], "needs-repair")
        tg.repair(self.root, "D00 T01 §1", "fixing the gaps", "2026-11-07")
        self.assertEqual(self.disposed()[0], "ready")
        self.plan("D00-T01-s1-cycle2.md")
        self.assertEqual(self.disposed()[0], "ready")
        self.exhaust("D00-T01-s1-cycle2.md")
        self.assertEqual(self.disposed()[0], "needs-repair")

    def test_a_dead_end_this_session_cannot_repair_reads_what_it_waits_for(self):
        for needs, state in (("attended", "waiting-person"), ("owner-decision", "waiting-person"), ("display-session", "waiting-host")):
            with self.subTest(needs):
                self.write(todo_file([(1, "A", 0, needs, "--")]))
                self.exhaust()
                self.assertEqual(self.disposed()[0], state)
                self.record().unlink()
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "§1")]))
        self.exhaust("D00-T01-s2.md")
        rows = tg.graph(self.root)
        self.assertEqual(tg.section_disposition(rows["D00 T01 §2"], rows, self.CAPS, self.root)[0], "waiting-dependency")

    def test_repair_starts_a_new_cycle_keeping_the_findings(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        self.exhaust()
        before = self.record().read_bytes()
        name = tg.repair(self.root, "D00 T01 §1", "fixing the gaps", "2026-11-07")
        self.assertTrue(name.endswith("D00-T01-s1-cycle2.md"), name)
        self.assertEqual(self.record().read_bytes(), before)
        self.assertIn("> **Repair cycle:** 1: 2026-11-07: fixing the gaps", self.text())
        self.assertFalse((self.root / name).exists())  # it appears at the cycle's plan review

    def test_repair_refuses_what_it_cannot_do(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        with self.assertRaises(tg.Refused) as caught:
            tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
        self.assertIn("no dead end", str(caught.exception))
        self.exhaust()
        for bad in (("D00 T01 §9", "x", "2026-11-07"), ("D00 T01 §1", "", "2026-11-07"), ("D00 T01 §1", "two\nlines", "2026-11-07"), ("D00 T01 §1", "x", "soon")):
            with self.subTest(bad), self.assertRaises(tg.Refused):
                tg.repair(self.root, *bad)

    def test_running_repair_again_spends_nothing(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        self.exhaust()
        tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
        with self.assertRaises(tg.Refused):
            tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
        self.assertEqual(self.text().count("> **Repair cycle:**"), 1)

    def pin(self, value):
        (self.root / ".conclave").mkdir(exist_ok=True)
        (self.root / ".conclave" / "reviewers.toml").write_text(f'[schedule]\ncompletion_order = "last"\nmax_repair_cycles = {value}\n', encoding="utf-8")

    def test_a_bad_repair_cap_refuses_repair_but_still_answers(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        self.exhaust()
        for value in ("34", "true", '"3"', "-1"):
            with self.subTest(value):
                self.pin(value)
                with self.assertRaises(tg.Refused) as caught:
                    tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
                self.assertIn("max_repair_cycles", str(caught.exception))
                state, reason = self.disposed()
                self.assertEqual(state, "waiting-person")
                self.assertIn("max_repair_cycles", reason)
        self.pin("3")  # correcting it brings the row back, with its findings intact
        self.assertEqual(self.disposed()[0], "needs-repair")
        self.assertIn("HIGHEST: P1", self.record().read_text(encoding="utf-8"))
        self.pin("0")
        with self.assertRaises(tg.Refused) as caught:
            tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
        self.assertIn("park it", str(caught.exception))

    def test_an_unusable_pin_refuses_rather_than_assuming_a_cap(self):
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")]))
        self.exhaust()
        pin = self.root / ".conclave" / "reviewers.toml"
        pin.parent.mkdir(exist_ok=True)
        for name, text in (("truncated", "[schedule\nmax_"), ("wrong shape", "schedule = 3\n")):
            with self.subTest(name):
                pin.write_text(text, encoding="utf-8")
                before = self.text()
                with self.assertRaises(tg.Refused) as caught:
                    tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
                self.assertIn("max_repair_cycles", str(caught.exception))
                self.assertEqual(self.text(), before)
                state, reason = self.disposed()
                self.assertEqual(state, "waiting-person")
                self.assertIn("max_repair_cycles", reason)
                rows = tg.graph(self.root)  # the other row still answers
                self.assertEqual(tg.section_disposition(rows["D00 T01 §2"], rows, self.CAPS, self.root)[0], "ready")
        real = pathlib.Path.read_text

        def locked(path, *args, **kwargs):
            if path.name == "reviewers.toml":
                raise PermissionError("locked")
            return real(path, *args, **kwargs)
        with mock.patch.object(pathlib.Path, "read_text", locked):
            self.assertEqual(self.disposed()[0], "waiting-person")
            with self.assertRaises(tg.Refused):
                tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
        pin.write_text('[schedule]\nmax_repair_cycles = 3\n', encoding="utf-8")  # correcting it restores the row
        self.exhaust()  # at the corrected pin's own round cap
        self.assertEqual(self.disposed()[0], "needs-repair")
        pin.unlink()  # a tree with no pin uses the default
        self.assertEqual(self.disposed()[0], "needs-repair")

    def test_exhaustion_follows_the_cycles_own_policy_when_the_pin_changes(self):
        live = (REPO / ".conclave" / "reviewers.toml").read_text(encoding="utf-8")
        pin = self.root / ".conclave" / "reviewers.toml"
        pin.parent.mkdir(exist_ok=True)
        low = re.sub(r"max_low_rounds = \d+", "max_low_rounds = 2", live)
        high = re.sub(r"max_low_rounds = \d+", "max_low_rounds = 8", live)
        self.assertNotEqual(low, high)
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        for args in (["init", "-q"], ["config", "user.email", "t@example.com"], ["config", "user.name", "T"]):
            subprocess.run(["git", "-C", str(self.root), *args], check=True)

        def commit(text):
            pin.write_text(text, encoding="utf-8")
            subprocess.run(["git", "-C", str(self.root), "add", "-A"], check=True)
            subprocess.run(["git", "-C", str(self.root), "commit", "-qm", "pin", "--allow-empty"], check=True)
            return subprocess.run(["git", "-C", str(self.root), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()

        def cycle(policy, count):
            dispatch = tg.review_dispatch()
            self.record().unlink(missing_ok=True)
            for _ in range(count):
                review = "1. A finding.\n\nHIGHEST: P1\nOVERALL: needs-attention"
                dispatch.record(self.record(), {**self.result(review), "policy": policy}, "2026-11-07T00:00:00Z")
        started_low = commit(low)
        cycle(started_low, 3)
        self.assertEqual(self.disposed()[0], "needs-repair")  # at its own cap of 1 + 2
        pin.write_text(high, encoding="utf-8")  # raising the pin later doesn't reopen it
        self.assertEqual(self.disposed()[0], "needs-repair")
        started_high = commit(high)
        cycle(started_high, 3)
        self.assertEqual(self.disposed()[0], "ready")  # under its own cap of 1 + 8
        pin.write_text(low, encoding="utf-8")  # lowering the pin later doesn't end it early
        self.assertEqual(self.disposed()[0], "ready")

    def test_a_cycle_whose_policy_cant_be_read_waits_and_spends_nothing(self):
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")]))
        pin = self.root / ".conclave" / "reviewers.toml"
        pin.parent.mkdir(exist_ok=True)
        pin.write_text((REPO / ".conclave" / "reviewers.toml").read_text(encoding="utf-8"), encoding="utf-8")
        subprocess.run(["git", "-C", str(self.root), "init", "-q"], check=True)
        review = "1. A finding.\n\nHIGHEST: P1\nOVERALL: needs-attention"
        for _ in range(3):  # the policy commit isn't here, as in a shallow clone
            tg.review_dispatch().record(self.record(), {**self.result(review), "policy": "f" * 40}, "2026-11-07T00:00:00Z")
        state, reason = self.disposed()
        self.assertEqual(state, "waiting-person")
        self.assertIn("review policy", reason)
        before = self.text()
        with self.assertRaises(tg.Refused):
            tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
        self.assertEqual(self.text(), before)
        rows = tg.graph(self.root)
        self.assertEqual(tg.section_disposition(rows["D00 T01 §2"], rows, self.CAPS, self.root)[0], "ready")

    def test_a_findings_file_that_cant_be_read_waits_and_recovers(self):
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")]))
        self.exhaust()
        real, before = pathlib.Path.read_bytes, self.text()
        for error in (PermissionError("sharing violation"), OSError("locked")):
            def locked(path, *args, **kwargs):
                if path.name == "D00-T01-s1.md":
                    raise error
                return real(path, *args, **kwargs)
            with self.subTest(str(error)), mock.patch.object(pathlib.Path, "read_bytes", locked):
                state, reason = self.disposed()
                self.assertEqual(state, "waiting-person")
                self.assertIn("D00-T01-s1.md", reason)
                self.assertIn(str(error), reason)
                with self.assertRaises(tg.Refused):
                    tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
                self.assertEqual(self.text(), before)
                rows = tg.graph(self.root)
                self.assertEqual(tg.section_disposition(rows["D00 T01 §2"], rows, self.CAPS, self.root)[0], "ready")
        self.assertEqual(self.disposed()[0], "needs-repair")  # it reads again, and the row is back

    def with_plan(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        self.exhaust()
        tg.projections(self.root, sync=True)
        return self.root / tg.PLAN

    def assert_one_cycle(self, expected):
        """A fresh read picks exactly one cycle, and the earlier findings stand."""
        self.assertTrue(tg.section_findings(self.root, "D00 T01 §1").endswith(expected))
        self.assertIn("HIGHEST: P1", self.record().read_text(encoding="utf-8"))

    def test_a_failed_file_write_leaves_the_tree_unchanged(self):
        plan = self.with_plan()
        before, plan_before = self.text(), plan.read_bytes()
        with mock.patch.object(tg, "atomic", side_effect=OSError("disk full")):
            with self.assertRaises(OSError):
                tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
        self.assertEqual((self.text(), plan.read_bytes()), (before, plan_before))
        self.assert_one_cycle("D00-T01-s1.md")

    def test_a_failed_plan_write_restores_both_files(self):
        plan = self.with_plan()
        before, plan_before = self.text(), plan.read_bytes()
        real, calls = tg.atomic, []

        def flaky(path, data):
            calls.append(path)
            if len(calls) == 2:  # the section's write goes through, and the plan's doesn't
                raise OSError("disk full")
            return real(path, data)
        with mock.patch.object(tg, "atomic", side_effect=flaky):
            with self.assertRaises(OSError):
                tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
        self.assertEqual((self.text(), plan.read_bytes()), (before, plan_before))
        self.assert_one_cycle("D00-T01-s1.md")

    def test_a_failed_rollback_says_how_to_finish_and_leaves_one_valid_cycle(self):
        self.with_plan()
        real, calls = tg.atomic, []

        def flaky(path, data):
            calls.append(path)
            if len(calls) >= 2:  # the section's write goes through, then nothing else can be written
                raise OSError("disk full")
            return real(path, data)
        with mock.patch.object(tg, "atomic", side_effect=flaky):
            with self.assertRaises(tg.Refused) as caught:
                tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")
        self.assertIn("plan --sync", str(caught.exception))
        self.assert_one_cycle("D00-T01-s1-cycle2.md")  # the line stands, and that is the cycle
        tg.projections(self.root, sync=True)  # the named command finishes it
        tg.projections(self.root)
        with self.assertRaises(tg.Refused):  # and the cycle wasn't spent twice
            tg.repair(self.root, "D00 T01 §1", "x", "2026-11-07")

    def test_the_default_allows_a_thirty_third_repair_and_refuses_the_next(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        self.exhaust()
        for k in range(2, 35):
            tg.repair(self.root, "D00 T01 §1", f"repair {k}", "2026-11-07")
            self.exhaust(f"D00-T01-s1-cycle{k}.md")
        self.assertEqual(self.text().count("> **Repair cycle:**"), 33)
        with self.assertRaises(tg.Refused) as caught:
            tg.repair(self.root, "D00 T01 §1", "a thirty fourth", "2026-11-07")
        self.assertIn("park it", str(caught.exception))

    def test_a_cycle_under_way_keeps_its_cap_when_the_live_pin_rises(self):
        live = (REPO / ".conclave" / "reviewers.toml").read_text(encoding="utf-8")
        self.assertIn("max_low_rounds = 32", live)  # the rise itself
        pin = self.root / ".conclave" / "reviewers.toml"
        pin.parent.mkdir(exist_ok=True)
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        for args in (["init", "-q"], ["config", "user.email", "t@example.com"], ["config", "user.name", "T"]):
            subprocess.run(["git", "-C", str(self.root), *args], check=True)
        pin.write_text(live.replace("max_low_rounds = 32", "max_low_rounds = 8"), encoding="utf-8")
        subprocess.run(["git", "-C", str(self.root), "add", "-A"], check=True)
        subprocess.run(["git", "-C", str(self.root), "commit", "-qm", "the old pin"], check=True)
        old = subprocess.run(["git", "-C", str(self.root), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
        review = "1. A finding.\n\nHIGHEST: P1\nOVERALL: needs-attention"
        for _ in range(9):
            tg.review_dispatch().record(self.record(), {**self.result(review), "policy": old}, "2026-11-07T00:00:00Z")
        pin.write_text(live, encoding="utf-8")
        self.assertEqual(self.disposed()[0], "needs-repair")

    def test_on_the_live_pin_round_32_goes_on_and_an_unresolved_33_needs_repair(self):
        live = (REPO / ".conclave" / "reviewers.toml").read_text(encoding="utf-8")
        pin = self.root / ".conclave" / "reviewers.toml"
        pin.parent.mkdir(exist_ok=True)
        pin.write_text(live, encoding="utf-8")
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        for args in (["init", "-q"], ["config", "user.email", "t@example.com"], ["config", "user.name", "T"], ["add", "-A"], ["commit", "-qm", "pin"]):
            subprocess.run(["git", "-C", str(self.root), *args], check=True)
        policy = subprocess.run(["git", "-C", str(self.root), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
        review = "1. A finding.\n\nHIGHEST: P1\nOVERALL: needs-attention"
        for _ in range(32):
            tg.review_dispatch().record(self.record(), {**self.result(review), "policy": policy}, "2026-11-07T00:00:00Z")
        self.assertEqual(self.disposed()[0], "ready")
        tg.review_dispatch().record(self.record(), {**self.result(review), "policy": policy}, "2026-11-07T00:00:00Z")
        state, reason = self.disposed()
        self.assertEqual(state, "needs-repair")
        self.assertIn("33 review rounds", reason)

    def third_cycle_with_gaps(self, clean_last=True):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        self.exhaust()
        for k in (2, 3):
            tg.repair(self.root, "D00 T01 §1", f"repair {k}", "2026-11-07")
            if k == 2:
                self.exhaust("D00-T01-s1-cycle2.md")
        name = "D00-T01-s1-cycle3.md"
        review = "HIGHEST: none\nOVERALL: approve" if clean_last else "1. A finding.\n\nHIGHEST: P1\nOVERALL: needs-attention"
        tg.review_dispatch().record(self.record(name), self.result(review), "2026-11-07T00:00:00Z")
        self.completion("gaps", name)

    def test_a_third_cycle_with_gaps_after_clean_rounds_needs_filing(self):
        self.third_cycle_with_gaps()
        before = self.text()
        state, reason = self.disposed()
        self.assertEqual(state, "needs-filing")
        self.assertIn("filed", reason)
        self.assertEqual(self.text(), before)  # a restart reads it the same and spends no repair cycle
        self.assertEqual(self.disposed()[0], "needs-filing")
        rows = tg.graph(self.root)
        self.assertEqual(tg.next_work(rows, self.CAPS, self.root)["state"], "needs-filing")

    def test_a_first_cycle_with_gaps_after_a_clean_round_needs_filing(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        tg.review_dispatch().record(self.record("D00-T01-s1.md"), self.result("HIGHEST: none\nOVERALL: approve"), "2026-11-07T00:00:00Z")
        self.completion("gaps", "D00-T01-s1.md")
        state, reason = self.disposed()
        self.assertEqual(state, "needs-filing")
        self.assertIn("fixed or filed", reason)

    def test_repair_refuses_completion_gaps_and_changes_nothing(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        self.completion("gaps")
        before = self.text()
        with self.assertRaises(tg.Refused) as caught:
            tg.repair(self.root, "D00 T01 §1", "more", "2026-11-08")
        self.assertIn("stamp filed", str(caught.exception))
        self.assertEqual(self.text(), before)
        self.assertEqual(self.disposed()[0], "needs-filing")

    def test_the_operator_instructions_point_at_fix_or_file(self):
        readme = (REPO / "todo" / "README.md").read_text(encoding="utf-8")
        self.assertIn("reads `needs-filing`", readme)
        self.assertNotIn("its completion returned gaps, or its rounds ran out", readme)
        dispatcher = (REPO / "scripts" / "review-dispatch.py").read_text(encoding="utf-8")
        self.assertNotIn("require a new reviewed repair cycle", dispatcher)
        self.assertIn("nothing is re-reviewed", dispatcher)

    def test_filing_must_go_to_open_sections(self):
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--")]))
        rows = tg.graph(self.root)
        tg.require_open_targets(rows, ["D00 T01 §2"], "D00 T01 §1")
        rows["D00 T01 §2"].done = True
        with self.assertRaises(tg.Refused) as caught:
            tg.require_open_targets(rows, ["D00 T01 §2"], "D00 T01 §1")
        self.assertIn("open section", str(caught.exception))
        with self.assertRaises(tg.Refused):
            tg.require_open_targets(rows, ["D00 T01 §9"], "D00 T01 §1")

    def test_the_design_doc_and_the_pin_agree_on_the_caps(self):
        schedule = tg.tomllib.loads((REPO / ".conclave" / "reviewers.toml").read_text(encoding="utf-8"))["schedule"]
        self.assertEqual(1 + schedule["max_low_rounds"], 33)
        design = (REPO / "docs" / "specs" / "2026-11-07-control-panel-design.md").read_text(encoding="utf-8")
        self.assertIn(f"| Review cap | {1 + schedule['max_low_rounds']} rounds a cycle", design)
        self.assertIn(f"Up to {schedule['max_repair_cycles']} repair cycles", design)

    def use_up_repairs(self, count=3):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        self.pin("3")  # these tests are about what happens at a cap, so a small one keeps them short
        self.exhaust()
        for k in range(2, count + 2):
            tg.repair(self.root, "D00 T01 §1", f"repair {k}", "2026-11-07")
            self.exhaust(f"D00-T01-s1-cycle{k}.md")

    def test_a_fourth_repair_is_refused_and_named(self):
        self.use_up_repairs()
        state, reason = self.disposed()
        self.assertEqual(state, "needs-repair")
        self.assertIn("park it", reason)
        with self.assertRaises(tg.Refused) as caught:
            tg.repair(self.root, "D00 T01 §1", "again", "2026-11-07")
        self.assertIn("park it", str(caught.exception))

    def test_parking_and_the_operators_way_back(self):
        self.use_up_repairs()
        tg.park(self.root, "D00 T01 §1", "three repairs failed", "2026-11-07")
        self.assertEqual(self.disposed()[0], "parked")
        with self.assertRaises(tg.Refused):
            tg.repair(self.root, "D00 T01 §1", "again", "2026-11-07")
        with self.assertRaises(tg.Refused):
            tg.park(self.root, "D00 T01 §1", "again", "2026-11-07")
        tg.unpark(self.root, "D00 T01 §1", "owner read the findings", "2026-11-08")
        self.assertNotIn("**Parked:**", self.text())
        self.assertIn("> **Repair granted:** 2026-11-08: owner read the findings", self.text())
        self.assertEqual(self.disposed()[0], "needs-repair")
        self.assertIn("cycle5", tg.repair(self.root, "D00 T01 §1", "one more", "2026-11-08"))
        self.assertEqual(self.disposed()[0], "ready")
        self.exhaust("D00-T01-s1-cycle5.md")
        with self.assertRaises(tg.Refused):
            tg.repair(self.root, "D00 T01 §1", "and another", "2026-11-08")  # a grant is one cycle

    def test_unpark_refuses_a_section_not_parked(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        with self.assertRaises(tg.Refused):
            tg.unpark(self.root, "D00 T01 §1", "x", "2026-11-07")

    def test_a_parked_sections_dependants_wait(self):
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "§1")]))
        tg.park(self.root, "D00 T01 §1", "x", "2026-11-07")
        rows = tg.graph(self.root)
        self.assertEqual(tg.section_disposition(rows["D00 T01 §2"], rows, self.CAPS, self.root)[0], "waiting-dependency")

    def test_a_repair_line_is_not_a_reason_to_suspect_consumers(self):
        self.assertEqual(tg.unstamped_notes("> **Repair cycle:** 1: x"), 0)
        self.assertEqual(tg.reopenings("> **Repair cycle:** 1: x\n> **Unstamped:** y"), 2)

    def test_rounds_at_the_cap_with_findings_open_need_repair(self):
        self.rounds(9)
        state, reason = self.state()
        self.assertEqual(state, "needs-repair")
        self.assertIn("9 review rounds", reason)

    def test_headings_inside_a_review_do_not_hide_a_round(self):
        # Each fixture review holds a level-two heading, as a reviewer's per-lens format may.
        self.rounds(1)
        self.assertEqual(len(tg.review_dispatch().read_findings(self.record().read_text(encoding="utf-8"))["rounds"]), 1)

    def test_rounds_under_the_cap_stay_ready(self):
        self.rounds(8)
        self.assertEqual(self.state()[0], "ready")

    def test_a_clean_last_round_at_the_cap_is_waiting_for_completion_not_stuck(self):
        self.rounds(8)
        self.rounds(1, "P3", "approve")
        self.assertEqual(self.state()[0], "ready")

    def test_rounds_with_no_reply_do_not_count(self):
        dispatch = tg.review_dispatch()
        for _ in range(11):
            dispatch.record(self.record(), {**self.result(None), "reviewer": None, "execution": "failed", "reason": "no reply"}, "2026-11-07T00:00:00Z")
        self.assertEqual(self.state()[0], "ready")

    def test_a_findings_file_the_dispatcher_cannot_read_needs_repair(self):
        self.record().write_text("## Round 1: astra\n\nnot as recorded\n", encoding="utf-8")
        state, reason = self.state()
        self.assertEqual(state, "needs-repair")
        self.assertIn("can't read", reason)

    def test_the_cap_comes_from_the_pin(self):
        (self.root / ".conclave").mkdir(exist_ok=True)
        (self.root / ".conclave" / "reviewers.toml").write_text('[schedule]\ncompletion_order = "last"\nmax_low_rounds = 2\n', encoding="utf-8")
        self.rounds(2)
        self.assertEqual(self.state()[0], "ready")
        self.rounds(1)
        self.assertEqual(self.state()[0], "needs-repair")

    def test_next_work_passes_a_host_blocked_earlier_phase(self):
        self.write(todo_file([(1, "A", 0, "display-session", "--"), (2, "B", 3, "none", "--")]))
        rows = tg.graph(self.root)
        work = tg.next_work(rows, self.CAPS, self.root)
        self.assertEqual((work["ref"], work["phase"], work["state"]), ("D00 T01 §2", 3, "ready"))
        with_desktop = tg.next_work(rows, {**self.CAPS, "display-session": True}, self.root)
        self.assertEqual(with_desktop["ref"], "D00 T01 §1")

    def test_next_work_counts_a_repairable_row_and_is_none_when_nothing_can_run(self):
        self.write(todo_file([(1, "A", 0, "none", "--")]))
        self.exhaust()
        rows = tg.graph(self.root)
        self.assertEqual(tg.next_work(rows, self.CAPS, self.root)["state"], "needs-repair")
        self.write(todo_file([(1, "A", 0, "attended", "--")]))
        rows = tg.graph(self.root)
        self.assertIsNone(tg.next_work(rows, self.CAPS, self.root))

    def test_a_stamped_row_is_verified_and_old_answers_are_unchanged(self):
        self.write(todo_file([(1, "A", 0, "attended", "--")]))
        rows = tg.graph(self.root)
        self.assertEqual(tg.disposition(rows["D00 T01 §1"], rows), 0)  # resolve and query ready keep their contract
        self.assertEqual(tg.section_disposition(rows["D00 T01 §1"], rows, self.CAPS, self.root)[0], "waiting-person")


class QueryNext(Tree):
    def run_cli(self):
        """The real command on a copy of the tool, so the fixture tree is what it reads."""
        tool = self.root / "scripts"
        (tool / "lib").mkdir(parents=True, exist_ok=True)
        for name in ("todo-graph.py", "review-dispatch.py", "lib/todo_graph.py", "lib/host_caps.py"):
            (tool / name).write_bytes((REPO / "scripts" / name).read_bytes())
        return subprocess.run([sys.executable, str(tool / "todo-graph.py"), "query", "next"], cwd=self.root,
                              capture_output=True, text=True, encoding="utf-8")

    def test_prints_the_row_to_start(self):
        self.write(todo_file([(1, "A", 0, "attended", "--"), (2, "B", 3, "none", "--")]))
        done = self.run_cli()
        self.assertEqual(done.returncode, 0, done.stderr)
        answer = json.loads(done.stdout)
        self.assertEqual((answer["ref"], answer["phase"], answer["state"]), ("D00 T01 §2", 3, "ready"))

    def test_prints_null_when_nothing_can_run(self):
        self.write(todo_file([(1, "A", 0, "attended", "--")]))
        done = self.run_cli()
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIsNone(json.loads(done.stdout))


class QueryDisposition(unittest.TestCase):
    def test_lists_open_rows_with_state_and_reason(self):
        done = subprocess.run([sys.executable, str(REPO / "scripts" / "todo-graph.py"), "query", "disposition"], cwd=REPO,
                              capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(done.returncode, 0, done.stderr)
        rows = json.loads(done.stdout)
        self.assertTrue(rows)
        for row in rows:
            self.assertTrue({"ref", "title", "phase", "needs", "state", "reason"} <= set(row))
            self.assertNotEqual(row["state"], "verified")
        self.assertIn(rows[0]["state"], {"ready", "waiting-host", "waiting-person", "waiting-dependency", "needs-repair", "parked"})


class SkillsDefer(unittest.TestCase):
    def test_no_runner_chooses_a_phase_from_query_ready(self):
        for folder in (".claude", ".codex", ".agents"):
            with self.subTest(folder=folder):
                text = (REPO / folder / "skills" / "process-plan" / "SKILL.md").read_text(encoding="utf-8")
                self.assertIn("query next", text)
                self.assertNotIn("todo-graph.py query ready", text)
                self.assertNotIn("no longer counts as runnable", text)
                self.assertIn("has them fixed or filed and stamps in the same cycle", text)
                self.assertNotIn("from its third", text)
                phase = (REPO / folder / "skills" / "process-phase" / "SKILL.md").read_text(encoding="utf-8")
                self.assertIn("query disposition` before each row", phase)
                self.assertNotIn("Skip rows whose `resolve` is not exit 0", phase)
                self.assertIn("file and stamp one it calls `needs-filing`", phase)
                self.assertNotIn("repair or park one it calls `needs-repair`, and skip the rest", phase)

    def test_no_runner_skill_keeps_its_own_list_of_host_needs(self):
        for folder in (".claude", ".codex", ".agents"):
            for skill in ("process-plan", "process-phase"):
                with self.subTest(folder=folder, skill=skill):
                    text = (REPO / folder / "skills" / skill / "SKILL.md").read_text(encoding="utf-8")
                    self.assertIn("query disposition", text)
                    self.assertNotIn("`display-session`", text)
                    self.assertIn("needs-repair", text)
                    self.assertIn("needs-filing", text)
                    self.assertNotIn("unpark", text)  # the operator's, never a run's

    def test_review_files_leftovers_from_the_third_cycle_in_all_mirrors(self):
        for folder in (".claude", ".codex", ".agents"):
            with self.subTest(folder=folder):
                text = (REPO / folder / "skills" / "review-todo-section" / "SKILL.md").read_text(encoding="utf-8")
                self.assertIn("as Steward and Recto run", text)
                self.assertIn(":completed'", text)
                self.assertIn("Nothing is re-reviewed", text)
                self.assertNotIn("third review cycle", text)
                skill = (REPO / folder / "skills" / "process-todo-section" / "SKILL.md").read_text(encoding="utf-8")
                self.assertNotIn("prevent stamping", skill)
                self.assertNotIn("keep the section unstamped", skill)
                self.assertNotIn("implement every in-scope gap and excellence item", skill)
                self.assertIn("record its excellence suggestions in `todo/findings.md`", skill)
                self.assertNotIn("Only a genuine complete verdict may stamp a new-policy cycle.", text)
                self.assertIn(":filed'", text)
                self.assertIn("todo/findings.md", text)

    def test_the_workflow_doc_has_the_third_cycle_rule(self):
        text = (REPO / "docs" / "todo-workflow.md").read_text(encoding="utf-8")
        self.assertIn("never re-reviewed", text)
        self.assertNotIn("third review cycle", text)
        self.assertNotIn("stamp only on complete", text)
        design = (REPO / "docs" / "specs" / "2026-11-07-control-panel-design.md").read_text(encoding="utf-8")
        self.assertIn("Fixed or filed in the same cycle", design)

    def test_the_completion_prompt_counts_only_missing_behaviour_as_gaps(self):
        prompt = (REPO / "docs" / "completion-review-prompt.md").read_text(encoding="utf-8")
        self.assertIn("These are suggestions, not gaps", prompt)
        self.assertIn("every `missing-in-scope` and `missing-out-of-scope` item from Part 1", prompt)
        self.assertNotIn("`missing-out-of-scope`, `in-scope` and `out-of-scope` item", prompt)

    def test_review_teaches_repair_then_park_in_all_mirrors(self):
        for folder in (".claude", ".codex", ".agents"):
            with self.subTest(folder=folder):
                text = (REPO / folder / "skills" / "review-todo-section" / "SKILL.md").read_text(encoding="utf-8")
                self.assertIn("todo-graph.py repair", text)
                self.assertIn("`park`", text)
                reopened = (REPO / folder / "skills" / "process-todo-section" / "SKILL.md").read_text(encoding="utf-8")
                self.assertIn("`unstamp` or `repair` reopened", reopened)


class Stamp(Tree):
    def setUp(self):
        super().setUp()
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "owner-decision", "--")]).replace(
            "**Test checkpoint:**", STARTED + "**Test checkpoint:**", 1))
        report_path = self.root / "docs/reviews/00-workspace/D00-T01-s1.md"
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text("# Review findings\n\n" + envelope(1), encoding="utf-8")
        (self.root / ".conclave").mkdir()
        (self.root / tg.PIN).write_text(UNIFORM_PIN, encoding="utf-8")
        for args in (["init", "-q"], ["config", "user.email", "t@example.com"],
                     ["config", "user.name", "T"], ["add", "-A"], ["commit", "-qm", "x"]):
            subprocess.run(["git", "-C", str(self.root), *args], check=True)
        self.sha = self.head()
        self.start = self.sha  # the commit that records §1's Started line: the build began here
        self.reviews = [{"round": 1, "report": "docs/reviews/00-workspace/D00-T01-s1.md",
                         "verdict": "clean"}]
        self.write_completion()
        self.write_plan()

    COMPLETION = "docs/reviews/00-workspace/D00-T01-s1-completion.md"
    PLAN = "docs/reviews/00-workspace/D00-T01-s1.md"  # the section's own findings file, beside its rounds

    @staticmethod
    def only(text, plans):
        """The findings file text with only its plan reviews (plans) or with every record but them, header kept."""
        head, *records = re.split(r"(?m)^(?=## (?:Round |Plan review))", text)
        return head + "".join(r for r in records if r.startswith("## Plan review") == plans)

    def head(self):
        return subprocess.run(["git", "-C", str(self.root), "rev-parse", "HEAD"],
                              capture_output=True, text=True).stdout.strip()

    def commit(self, message="more"):
        for args in (["add", "-A"], ["commit", "-q", "--allow-empty", "-m", message]):
            subprocess.run(["git", "-C", str(self.root), *args], check=True)
        return self.head()

    def write_plan(self, review="1. Controls: all present.\n\nHIGHEST: none\n\nPLAN: complete\n\nOVERALL: approve",
                   reviewer="astra", cli="codex", writer="claude", effort="medium", lenses=("plan",), candidate=None,
                   path=None, keep=False):
        """A plan review as scripts/review-dispatch.py --kind plan records it (keep: after the attempts already there;
        otherwise the earlier plan reviews are dropped and the file's rounds stay)."""
        target = self.root / (path or self.PLAN)
        target.parent.mkdir(parents=True, exist_ok=True)
        if not keep and target.exists():
            target.write_bytes(self.only(target.read_bytes().decode("utf-8"), plans=False).encode("utf-8"))
        result = {"writer": writer, "reviewer": reviewer, "model": "m", "cli": cli, "effort": effort,
                  "lenses": list(lenses), "failover": [] if reviewer else [{"reviewer": "astra", "reason": "exited 3"}],
                  "review": review if reviewer else None}
        tg.review_dispatch().record_completion(target, result, "2026-10-22T00:00:00Z", candidate or self.start,
                                               kind="plan")

    def write_completion(self, review="1. Open file: present.\n\nCOMPLETION: complete", notes="", reviewer="sol",
                         candidate=None, path=None, effort="medium", keep=False):
        """A completion review as scripts/review-dispatch.py --kind completion records it, then the writer's notes."""
        target = self.root / (path or self.COMPLETION)
        target.parent.mkdir(parents=True, exist_ok=True)
        if not keep:
            target.unlink(missing_ok=True)
        result = {"writer": "claude", "reviewer": reviewer, "model": "gpt-6.1-sol", "cli": "codex", "effort": effort,
                  "failover": [] if reviewer else [{"reviewer": "sol", "reason": "exited 3"}],
                  "review": review if reviewer else None}
        tg.review_dispatch().record_completion(target, result, "2026-10-23T00:00:00Z", candidate or self.sha)
        if notes:
            with open(target, "a", encoding="utf-8", newline="") as handle:
                handle.write("\n" + notes + "\n")

    def ensure_plan(self, ref="D00 T01 §1"):
        """The returned plan review back in the section's own findings file when a test rewrote that file for its
        rounds (or never wrote it), as the real file carries it beside them. A file holding any plan attempt, or one
        the dispatcher cannot read, is left as the test wrote it. Returns the file."""
        report = tg.section_findings(self.root, ref)
        target = self.root / report
        plans = tg.review_dispatch().read_plans(target.read_bytes().decode("utf-8")) if target.exists() else []
        if plans == []:
            self.write_plan(path=report, keep=True)
        return report

    def stamp(self, *args, completion=None, plan=None, **kwargs):
        """tg.stamp on this tree, with a clean completion review and the section's own plan review unless the test
        names others (see ensure_plan)."""
        if plan is None:
            plan = self.ensure_plan(args[0])
        return tg.stamp(self.root, *args, completion=completion or {"report": self.COMPLETION, "verdict": "complete"},
                        plan=plan, **kwargs)

    def completion_refuses(self, completion, fragment):
        before = self.snapshot()
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-23", completion=completion)
        self.assertIn(fragment, str(ctx.exception))
        self.assertEqual(self.snapshot(), before, "a refused stamp wrote something")

    def test_a_stamp_without_a_completion_review_refuses(self):
        self.tick_work()
        before = self.snapshot()
        with self.assertRaises(tg.Refused) as ctx:
            tg.stamp(self.root, "D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-23")
        self.assertIn("--completion '<findings file>:<complete|completed|filed>' is required", str(ctx.exception))
        self.assertEqual(self.snapshot(), before)

    def test_an_unknown_completion_verdict_refuses(self):
        self.tick_work()
        for verdict in ("done", "gaps", "Complete", ""):
            with self.subTest(verdict=verdict):
                self.completion_refuses({"report": self.COMPLETION, "verdict": verdict},
                                        "--completion verdict must be one of complete, completed, filed")

    def test_a_findings_file_without_a_completion_review_refuses(self):
        self.tick_work()
        self.completion_refuses({"report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": "complete"},
                                "holds no ## Completion review")
        self.completion_refuses({"report": "docs/reviews/00-workspace/none.md", "verdict": "complete"},
                                "missing or empty review report")
        self.completion_refuses({"report": "../outside.md", "verdict": "complete"}, "invalid review report path")

    def test_a_completion_pass_that_returned_no_review_refuses(self):
        self.tick_work()
        self.write_completion(reviewer=None)
        self.completion_refuses({"report": self.COMPLETION, "verdict": "complete"},
                                "must hold exactly one completion review returned by another model family")
        self.write_completion(review="1. Open file: present.\n\nCOMPLETION: unsure")
        self.completion_refuses({"report": self.COMPLETION, "verdict": "complete"},
                                "must hold exactly one completion review returned by another model family")

    def test_the_completion_verdict_must_match_the_review(self):
        self.tick_work()
        self.write_completion(review="1. Ctrl+O: missing-in-scope.\n\nCOMPLETION: gaps")
        self.completion_refuses({"report": self.COMPLETION, "verdict": "complete"},
                                "completion verdict complete needs a review ending COMPLETION: complete")
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-23",
                             completion={"report": self.COMPLETION, "verdict": "completed"})
        data = json.loads((self.root / receipt).read_text(encoding="utf-8"))
        self.assertEqual(data["completion"], {"report": self.COMPLETION, "verdict": "completed", "candidate": self.sha,
                                              "reviewer": "sol", "model": "gpt-6.1-sol", "filed": []})
        self.assertEqual(tg.review_rounds(self.root)["sections"][0]["completion"], "completed")

    def test_filed_gaps_must_be_listed_and_must_resolve(self):
        self.tick_work()
        gaps = "1. Recent files: missing-out-of-scope.\n\nCOMPLETION: gaps"
        filed = {"report": self.COMPLETION, "verdict": "filed"}
        self.write_completion(review=gaps)
        self.completion_refuses(filed, "completion verdict filed needs each filed section listed")
        self.write_completion(review=gaps, notes="- Filed: D00 T09 §1 (recent files)")
        self.completion_refuses(filed, "completion gap filed as D00 T09 §1, which does not resolve")
        self.write_completion(review=gaps, notes="- Filed: D00 T01 §1 (itself)")
        self.completion_refuses(filed, "cannot file a completion gap to itself")
        self.write_completion(review=gaps, notes="- Filed: D00 T01 §2 (recent files)")
        self.completion_refuses({"report": self.COMPLETION, "verdict": "completed"},
                                "and only filed lists any")
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-23", completion=filed)
        data = json.loads((self.root / receipt).read_text(encoding="utf-8"))
        self.assertEqual((data["completion"]["verdict"], data["completion"]["filed"]), ("filed", ["D00 T01 §2"]))
        tg.graph(self.root)

    def test_a_filed_section_that_stops_resolving_refuses(self):
        other = self.root / "todo/00-workspace/TODO-02-b.md"
        other.write_text(todo_file([(1, "Recent files", 0, "none", "--")], ident="bravo"), encoding="utf-8")
        self.index("TODO-02-b.md")
        self.tick_work()
        self.write_completion(review="1. Recent files: missing-out-of-scope.\n\nCOMPLETION: gaps",
                              notes="- Filed: D00 T02 §1 (recent files)")
        self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-23",
                   completion={"report": self.COMPLETION, "verdict": "filed"})
        other.unlink()
        self.refuses("completion gap filed as D00 T02 §1, which does not resolve: D00 T01 §1")

    def test_the_completion_candidate_is_the_stamped_commit_or_before_it(self):
        self.tick_work()
        subprocess.run(["git", "-C", str(self.root), "commit", "-q", "--allow-empty", "-m", "later"], check=True)
        later = subprocess.run(["git", "-C", str(self.root), "rev-parse", "HEAD"], capture_output=True,
                               text=True).stdout.strip()
        self.write_completion(candidate=later)
        self.completion_refuses({"report": self.COMPLETION, "verdict": "complete"},
                                "is not the stamped commit or an ancestor of it")
        self.write_completion(candidate="f" * 40)
        self.completion_refuses({"report": self.COMPLETION, "verdict": "complete"}, "not a commit")

    def test_a_receipt_records_the_completion_review_and_edits_refuse(self):
        self.tick_work()
        receipt = self.root / self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-23")
        data = json.loads(receipt.read_text(encoding="utf-8"))
        self.assertEqual(data["completion"]["verdict"], "complete")
        for key, value in (("verdict", "completed"), ("filed", ["D00 T01 §2"]), ("reviewer", "astra")):
            with self.subTest(key=key):
                edited = json.loads(json.dumps(data))
                edited["completion"][key] = value
                receipt.write_text(json.dumps(edited), encoding="utf-8")
                with self.assertRaises(tg.Refused):
                    tg.graph(self.root)
        receipt.write_text(json.dumps(data), encoding="utf-8")
        tg.graph(self.root)
        # An edit inside the recorded completion review breaks its digest, so the receipt no longer validates.
        path = self.root / self.COMPLETION
        path.write_text(path.read_text(encoding="utf-8").replace("present", "absent"), encoding="utf-8")
        self.refuses("is not a findings file the dispatcher can read")

    def test_a_receipt_from_before_the_completion_review_still_validates(self):
        # Receipts stamped before 2026-10-23 carry no completion field (nor the later plan and schedule); once frozen,
        # their presence-free shape stays valid.
        self.tick_work()
        receipt = self.root / self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-23")
        data = json.loads(receipt.read_text(encoding="utf-8"))
        del data["completion"], data["plan"], data["schedule"]
        receipt.write_text(json.dumps(data), encoding="utf-8")
        (self.root / self.COMPLETION).unlink()
        self.refuses(tg.LEGACY_RECEIPTS)
        (self.root / tg.LEGACY_RECEIPTS).write_text(json.dumps(
            {"schema_version": 1, "receipts": {}, "before_plan": {"D00 T01 §1": tg.receipt_digest(data)}}),
            encoding="utf-8")
        tg.graph(self.root)
        self.assertIsNone(tg.review_rounds(self.root)["sections"][0]["completion"])

    def tick_work(self):
        path = self.root / "todo/00-workspace/TODO-01-a.md"
        path.write_text(path.read_text(encoding="utf-8").replace("- [ ] Work.", "- [x] Work.", 1),
                        encoding="utf-8")

    def test_stamp_writes_receipt_and_validates(self):
        self.tick_work()
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        rows = tg.graph(self.root)
        self.assertTrue(rows["D00 T01 §1"].done)
        self.assertEqual(json.loads((self.root / receipt).read_text(encoding="utf-8"))
                         ["implementation_sha"], self.sha)

    def test_receipt_records_rounds_spent_against_the_cap(self):
        self.tick_work()
        report = self.root / "docs/reviews/00-workspace/D00-T01-s1.md"
        report.write_text("# Review findings\n\n" + envelope(1, "none") + envelope(2, overall="needs-attention")
                          + envelope(3), encoding="utf-8")
        reviews = [{"round": 2, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": "fixed"},
                   {"round": 3, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": "clean"}]
        receipt = self.stamp("D00 T01 §1", self.sha, reviews, ["true -> ok"], "2026-10-13")
        data = json.loads((self.root / receipt).read_text(encoding="utf-8"))
        self.assertEqual((data["rounds_spent"], data["round_cap"]), (3, tg.REVIEW_ROUND_CAP))
        self.assertNotIn("cap_outcome", data)
        summary = tg.review_rounds(self.root)
        self.assertEqual(summary["sections"][0]["rounds_spent"], 3)
        self.assertEqual(summary["summary"]["stopped_at_cap"], 0)

    def record_rounds(self, last, report="docs/reviews/00-workspace/D00-T01-s1.md"):
        (self.root / report).write_text("".join(envelope(n, overall="needs-attention") for n in range(1, last + 1)),
                                        encoding="utf-8")
        self.ensure_plan()

    def test_the_cap_is_33_and_earlier_caps_stay_valid(self):
        # Owner decision 2026-10-23: the review-round cap moved from 22 to 33.
        self.assertEqual(tg.REVIEW_ROUND_CAP, 33)
        self.assertIn("stopped-at-33", tg.VERDICTS)
        for earlier in (22, 13, 8, 3):
            self.assertIn(f"stopped-at-{earlier}", tg.VERDICTS)
        self.assertEqual(tg.EARLIER_CAPS, {8, 13, 22})

    def test_a_receipt_written_under_the_22_round_cap_still_validates(self):
        self.tick_work()
        self.record_rounds(22)
        stopped = [{"round": 22, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": "stopped-at-22"}]
        with self.assertRaises(tg.Refused):  # a new stamp stops only at the current cap
            self.stamp("D00 T01 §1", self.sha, stopped, ["true -> ok"], "2026-10-13", cap_outcome="filed")
        self.record_rounds(33)
        receipt = self.root / self.stamp("D00 T01 §1", self.sha, [
            {"round": 33, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": "stopped-at-33"}],
            ["true -> ok"], "2026-10-13", cap_outcome="filed")
        # The same section as a receipt from the 22-round era: 22 rounds, stopped-at-22, round_cap 22.
        self.record_rounds(22)
        data = json.loads(receipt.read_text(encoding="utf-8"))
        data.update(reviews=stopped, rounds_spent=22, round_cap=22)
        receipt.write_text(json.dumps(data), encoding="utf-8")
        tg.graph(self.root)
        data["round_cap"] = 33  # but a 22-round stop is not a stop at the 33-round cap
        receipt.write_text(json.dumps(data), encoding="utf-8")
        self.refuses("must be stopped-at-33")

    def test_a_stopped_review_must_say_what_was_done(self):
        self.tick_work()
        cap = tg.REVIEW_ROUND_CAP
        self.record_rounds(cap)
        stopped = [{"round": cap, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": f"stopped-at-{cap}"}]
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T01 §1", self.sha, stopped, ["true -> ok"], "2026-10-13")
        self.assertIn("--cap-outcome", str(ctx.exception))
        with self.assertRaises(tg.Refused):
            self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13",
                     cap_outcome="nothing stopped")
        receipt = self.stamp("D00 T01 §1", self.sha, stopped, ["true -> ok"], "2026-10-13",
                           cap_outcome="filed two leftovers as D00 T01 §2")
        data = json.loads((self.root / receipt).read_text(encoding="utf-8"))
        self.assertEqual((data["rounds_spent"], data["cap_outcome"]), (cap, "filed two leftovers as D00 T01 §2"))
        self.assertEqual(tg.review_rounds(self.root)["summary"]["stopped_at_cap"], 1)
        # A receipt that drops the outcome no longer validates.
        del data["cap_outcome"]
        (self.root / receipt).write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaises(tg.Refused):
            tg.graph(self.root)

    def test_a_receipt_without_rounds_spent_holds_only_what_the_old_process_wrote(self):
        self.tick_work()
        receipt = self.root / self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        data = json.loads(receipt.read_text(encoding="utf-8"))
        del data["rounds_spent"], data["round_cap"], data["plan"], data["schedule"]
        receipt.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaises(tg.Refused) as ctx:  # the old shape alone does not make a receipt old
            tg.graph(self.root)
        self.assertIn(tg.LEGACY_RECEIPTS, str(ctx.exception))
        legacy = self.root / tg.LEGACY_RECEIPTS
        legacy.write_text(json.dumps({"schema_version": 1, "receipts": {"D00 T01 §1": tg.receipt_digest(data)}}),
                          encoding="utf-8")
        tg.graph(self.root)  # a frozen old receipt still validates
        data["date"] = "2026-10-22"
        receipt.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaises(tg.Refused):  # and any change to it is no longer the frozen receipt
            tg.graph(self.root)
        report = "docs/reviews/00-workspace/D00-T01-s1.md"
        for rounds in ([{"round": 9, "report": report, "verdict": "stopped-low-severity"}],
                       [{"round": 1, "report": report, "verdict": "stopped-at-33"}]):
            data["reviews"] = rounds
            receipt.write_text(json.dumps(data), encoding="utf-8")
            with self.subTest(rounds=rounds), self.assertRaises(tg.Refused) as ctx:
                tg.graph(self.root)
            self.assertIn("without rounds_spent", str(ctx.exception))

    def test_a_loop_past_the_cap_refuses_even_when_it_ends_clean(self):
        self.tick_work()
        report = "docs/reviews/00-workspace/D00-T01-s1.md"
        last = tg.REVIEW_ROUND_CAP + 1
        (self.root / report).write_text("".join(envelope(n, overall="needs-attention") for n in range(1, last))
                                        + envelope(last), encoding="utf-8")
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T01 §1", self.sha, [{"round": last, "report": report, "verdict": "clean"}],
                     ["true -> ok"], "2026-10-13")
        self.assertIn(f"over the {tg.REVIEW_ROUND_CAP}-round cap", str(ctx.exception))

    def test_a_repeated_round_number_refuses(self):
        self.tick_work()
        report = "docs/reviews/00-workspace/D00-T01-s1.md"
        (self.root / report).write_text("".join(envelope(1, overall="needs-attention") for _ in range(3))
                                        + envelope(1), encoding="utf-8")
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        self.assertIn("must number its rounds 1 to 4 in order", str(ctx.exception))

    def test_a_loop_whose_last_round_returned_no_review_refuses(self):
        self.tick_work()
        report = "docs/reviews/00-workspace/D00-T01-s1.md"
        (self.root / report).write_text(envelope(1) + envelope(2, "none", overall="blocked"), encoding="utf-8")
        with self.assertRaises(tg.Refused) as ctx:  # round 2 ran after the approval and returned nothing
            self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        self.assertIn("ends with round 2, which returned no review", str(ctx.exception))

    def test_rounds_spent_must_be_what_the_findings_files_record(self):
        self.tick_work()
        receipt = self.root / self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        data = json.loads(receipt.read_text(encoding="utf-8"))
        data["rounds_spent"] = 999
        receipt.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaises(tg.Refused) as ctx:
            tg.graph(self.root)
        self.assertIn("receipt rounds_spent 999 is not the 1 rounds", str(ctx.exception))

    def test_a_receipt_names_a_cap_the_process_has_used(self):
        self.tick_work()
        receipt = self.root / self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        data = json.loads(receipt.read_text(encoding="utf-8"))
        data["round_cap"] = 50
        receipt.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaises(tg.Refused) as ctx:
            tg.graph(self.root)
        self.assertIn("round_cap is invalid", str(ctx.exception))

    def test_a_review_with_carriage_returns_is_read_as_the_dispatcher_wrote_it(self):
        self.tick_work()
        dispatch = tg.review_dispatch()
        review = ["Captured progress: 10%\rCaptured progress: 100%", "Example heading:", "## Round 999: none",
                  "OVERALL: approve"]
        text = ("## Round 1: astra (`m` via `codex`)\n\n- Writer: claude\n- Run: 2026-10-21T00:00:00Z\n"
                "- Effort: low\n- Lenses: adversarial\n"
                f"- Lines: 4\n- Digest: {dispatch.digest(review)}\n\n" + "\n".join(review) + "\n")
        (self.root / "docs/reviews/00-workspace/D00-T01-s1.md").write_bytes(text.encode("utf-8"))
        self.assertIsNotNone(dispatch.read_rounds(text))
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        self.assertEqual(json.loads((self.root / receipt).read_text(encoding="utf-8"))["rounds_spent"], 1)
        tg.graph(self.root)

    def test_a_round_verdict_must_match_its_review(self):
        self.tick_work()
        report = self.root / "docs/reviews/00-workspace/D00-T01-s1.md"
        before = self.snapshot()
        report.write_text(envelope(1, overall="needs-attention"), encoding="utf-8")
        with self.assertRaises(tg.Refused) as ctx:  # a rejecting review claimed as clean
            self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        self.assertIn("verdict matching clean", str(ctx.exception))
        fixed_only = [{"round": 1, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": "fixed"}]
        with self.assertRaises(tg.Refused) as ctx:  # fixed, but no later round saw the fixes
            self.stamp("D00 T01 §1", self.sha, fixed_only, ["true -> ok"], "2026-10-13")
        self.assertIn("must end at its last reviewed round", str(ctx.exception))
        report.write_text(envelope(1), encoding="utf-8")
        with self.assertRaises(tg.Refused):  # an approving review claimed as fixed
            self.stamp("D00 T01 §1", self.sha,
                     fixed_only + [{"round": 1, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": "clean"}],
                     ["true -> ok"], "2026-10-13")
        report.unlink()
        self.assertEqual({k: v for k, v in self.snapshot().items() if not k.endswith("D00-T01-s1.md")},
                         {k: v for k, v in before.items() if not k.endswith("D00-T01-s1.md")})

    def test_a_loop_ends_at_its_last_reviewed_round(self):
        self.tick_work()
        report = "docs/reviews/00-workspace/D00-T01-s1.md"
        (self.root / report).write_text(envelope(1) + envelope(2, overall="needs-attention"), encoding="utf-8")
        before = self.snapshot()
        for claimed in ([{"round": 1, "report": report, "verdict": "clean"}, {"round": 2, "report": report, "verdict": "fixed"}],
                        [{"round": 1, "report": report, "verdict": "clean"}]):
            with self.assertRaises(tg.Refused) as ctx:  # a later failing review cannot be left behind an approval
                self.stamp("D00 T01 §1", self.sha, claimed, ["true -> ok"], "2026-10-13")
            self.assertIn("must end at its last reviewed round (2)", str(ctx.exception))
        self.assertEqual(self.snapshot(), before)

    def test_a_severity_stop_with_a_p1_or_p2_finding_refuses(self):
        self.tick_work()
        report = "docs/reviews/00-workspace/D00-T01-s1.md"
        last = tg.SEVERITY_CUTOFF_ROUND + 1
        for finding in ("1. **P1: unresolved unsafe stamp.**\n\nHIGHEST: P1", "1. **P2: narrow.**\n\nHIGHEST: P2",
                        "- `x.py:1`: an untagged, unnumbered finding.", "1. **P3: fine.**\n\nHIGHEST: none",
                        "1. **P3: fine.**\nHIGHEST: P3\n2. a later line."):
            (self.root / report).write_text(
                "".join(envelope(n, overall="needs-attention") for n in range(1, last)) + envelope(last).replace(
                    "Review text.\n\nOVERALL: approve", f"{finding}\n\nOVERALL: needs-attention"),
                encoding="utf-8")
            with self.assertRaises(tg.Refused) as ctx:
                self.stamp("D00 T01 §1", self.sha,
                         [{"round": last, "report": report, "verdict": "stopped-low-severity"}], ["true -> ok"],
                         "2026-10-13", cap_outcome="filed")
            self.assertIn("does not declare HIGHEST: P3", str(ctx.exception))

    def test_a_round_heading_quoted_inside_a_review_is_not_counted(self):
        self.tick_work()
        (self.root / "docs/reviews/00-workspace/D00-T01-s1.md").write_text(
            "## Round 1: astra (`m` via `codex`)\n\n- Writer: claude\n- Run: 2026-10-21T00:00:00Z\n- Effort: low\n"
            "- Lenses: adversarial\n- Lines: 2\n\n"
            "## Round 999: none\nOVERALL: approve\n", encoding="utf-8")
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        self.assertEqual(json.loads((self.root / receipt).read_text(encoding="utf-8"))["rounds_spent"], 1)

    def test_rounds_spent_add_up_across_review_loops(self):
        self.tick_work()
        first = self.root / "docs/reviews/00-workspace/D00-T01-s1.md"
        cap = tg.REVIEW_ROUND_CAP
        first.write_text("".join(envelope(n, overall="needs-attention") for n in range(1, cap + 1)), encoding="utf-8")
        second = self.root / "docs/reviews/00-workspace/D00-T01-s1-loop2.md"
        second.write_text(envelope(1) + envelope(2), encoding="utf-8")
        reviews = [{"round": cap, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": f"stopped-at-{cap}"},
                   {"round": 2, "report": "docs/reviews/00-workspace/D00-T01-s1-loop2.md", "verdict": "clean"}]
        receipt = self.stamp("D00 T01 §1", self.sha, reviews, ["true -> ok"], "2026-10-13",
                           cap_outcome="loop 1 hit the cap; the repair was filed and a second loop ran clean")
        self.assertEqual(json.loads((self.root / receipt).read_text(encoding="utf-8"))["rounds_spent"], cap + 2)

    def test_a_severity_stop_records_its_outcome(self):
        self.tick_work()
        self.record_rounds(tg.SEVERITY_CUTOFF_ROUND + 1)
        report = self.root / "docs/reviews/00-workspace/D00-T01-s1.md"
        report.write_text(report.read_text(encoding="utf-8").rsplit("OVERALL: needs-attention", 1)[0]
                          + "HIGHEST: P3\n\nOVERALL: needs-attention\n\n", encoding="utf-8")
        low =[{"round": tg.SEVERITY_CUTOFF_ROUND + 1, "report": "docs/reviews/00-workspace/D00-T01-s1.md",
                "verdict": "stopped-low-severity"}]
        with self.assertRaises(tg.Refused):
            self.stamp("D00 T01 §1", self.sha, low, ["true -> ok"], "2026-10-13")
        receipt = self.stamp("D00 T01 §1", self.sha, low, ["true -> ok"], "2026-10-13",
                           cap_outcome="round 9 had two P3 findings, filed as D00 T01 §2")
        data = json.loads((self.root / receipt).read_text(encoding="utf-8"))
        self.assertEqual(data["cap_outcome"], "round 9 had two P3 findings, filed as D00 T01 §2")
        summary = tg.review_rounds(self.root)["summary"]
        self.assertEqual((summary["stopped_low_severity"], summary["stopped_at_cap"]), (1, 0))

    def test_a_cap_stop_must_be_the_current_cap_reached(self):
        self.tick_work()
        self.record_rounds(tg.REVIEW_ROUND_CAP)
        before = self.snapshot()
        for n, verdict in ((1, f"stopped-at-{tg.REVIEW_ROUND_CAP}"), (3, "stopped-at-3"), (13, "stopped-at-13")):
            early = [{"round": n, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": verdict}]
            with self.assertRaises(tg.Refused) as ctx:
                self.stamp("D00 T01 §1", self.sha, early, ["true -> ok"], "2026-10-13", cap_outcome="filed")
            self.assertRegex(str(ctx.exception), f"must be stopped-at-{tg.REVIEW_ROUND_CAP}|"
                                                 f"a cap stop is the round that returns review {tg.REVIEW_ROUND_CAP}")
        self.assertEqual(self.snapshot(), before)

    def test_a_severity_stop_with_only_p3_findings_stamps(self):
        self.tick_work()
        report = "docs/reviews/00-workspace/D00-T01-s1.md"
        last = tg.SEVERITY_CUTOFF_ROUND + 1
        (self.root / report).write_text(
            "".join(envelope(n, overall="needs-attention") for n in range(1, last)) + envelope(last).replace(
                "Review text.\n\nOVERALL: approve", "1. **P3: wording.**\n2. **P3: the comment describing P1 findings.**\n\nHIGHEST: P3\n\nOVERALL: needs-attention"),
            encoding="utf-8")
        self.stamp("D00 T01 §1", self.sha, [{"round": last, "report": report, "verdict": "stopped-low-severity"}],
                 ["true -> ok"], "2026-10-13", cap_outcome="two P3 findings filed")

    def test_outage_rounds_do_not_earn_a_stop(self):
        self.tick_work()
        report = "docs/reviews/00-workspace/D00-T01-s1.md"
        # At the live cutoff of 0 any returned review may stop; outages count toward no cutoff, which shows at 3.
        with mock.patch.object(tg, "SEVERITY_CUTOFF_ROUND", 3):
            last = 4
            (self.root / report).write_text(
                "".join(envelope(n, "none") for n in range(1, last)) + envelope(last).replace(
                    "Review text.\n\nOVERALL: approve", "1. **P3: wording.**\n\nHIGHEST: P3\n\nOVERALL: needs-attention"), encoding="utf-8")
            with self.assertRaises(tg.Refused) as ctx:
                self.stamp("D00 T01 §1", self.sha, [{"round": last, "report": report, "verdict": "stopped-low-severity"}],
                         ["true -> ok"], "2026-10-13", cap_outcome="filed")
            self.assertIn("follows only 1 returned reviews", str(ctx.exception))
        cap = tg.REVIEW_ROUND_CAP
        (self.root / report).write_text(envelope(1, "none") + "".join(
            envelope(n, overall="needs-attention") for n in range(2, cap + 1)), encoding="utf-8")
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T01 §1", self.sha, [{"round": cap, "report": report, "verdict": f"stopped-at-{cap}"}],
                     ["true -> ok"], "2026-10-13", cap_outcome="filed")
        self.assertIn("a cap stop is the round that returns review", str(ctx.exception))
        # Blocked or empty replies are not returned reviews either (at the cutoff of 3, as above).
        cutoff = mock.patch.object(tg, "SEVERITY_CUTOFF_ROUND", 3)
        cutoff.start()
        self.addCleanup(cutoff.stop)
        for filler in (envelope(1).replace("OVERALL: approve", "OVERALL: blocked"), envelope(1).replace("OVERALL: approve", ""),
                       envelope(1, "claude (`claude-opus-5` via `claude`)", overall="needs-attention")):
            (self.root / report).write_text("".join(filler.replace("## Round 1:", f"## Round {n}:") for n in range(1, last))
                                            + envelope(last).replace("Review text.\n\nOVERALL: approve",
                                                                     "1. **P3: wording.**\n\nHIGHEST: P3\n\nOVERALL: needs-attention"),
                                            encoding="utf-8")
            with self.assertRaises(tg.Refused) as ctx:
                self.stamp("D00 T01 §1", self.sha,
                         [{"round": last, "report": report, "verdict": "stopped-low-severity"}],
                         ["true -> ok"], "2026-10-13", cap_outcome="filed")
            self.assertIn("follows only 1 returned reviews", str(ctx.exception))
        # An outage before the cap moves the cap stop to the round that returns the cap's last review.
        (self.root / report).write_text(envelope(1, "none") + "".join(
            envelope(n, overall="needs-attention") for n in range(2, cap + 2)), encoding="utf-8")
        self.stamp("D00 T01 §1", self.sha, [{"round": cap + 1, "report": report, "verdict": f"stopped-at-{cap}"}],
                 ["true -> ok"], "2026-10-13", cap_outcome="filed")

    def test_a_severity_stop_before_the_cutoff_refuses(self):
        # The live cutoff is 0 (owner decision 2026-10-24), so no round comes before it; the refusal is checked at the
        # earlier cutoff of 3, which a later decision could restore.
        with mock.patch.object(tg, "SEVERITY_CUTOFF_ROUND", 3):
            self.tick_work()
            self.record_rounds(3)
            before = self.snapshot()
            for n in (1, 3):
                early = [{"round": n, "report": "docs/reviews/00-workspace/D00-T01-s1.md",
                          "verdict": "stopped-low-severity"}]
                with self.assertRaises(tg.Refused) as ctx:
                    self.stamp("D00 T01 §1", self.sha, early, ["true -> ok"], "2026-10-13", cap_outcome="filed")
                self.assertIn("only after round 3", str(ctx.exception))
            self.assertEqual(self.snapshot(), before)

    def test_the_live_cutoff_stops_on_the_first_p3_round(self):
        self.assertEqual(tg.SEVERITY_CUTOFF_ROUND, 0)

    def test_a_claimed_round_must_be_a_reviewed_round_in_its_report(self):
        self.tick_work()
        report = self.root / "docs/reviews/00-workspace/D00-T01-s1.md"
        before = self.snapshot()
        for text in ("# No independent review has run\n", envelope(1, "none"), envelope(2),
                     envelope(1, "claude (`claude-opus-5` via `claude`)"),
                     envelope(1).replace("- Writer: claude", "- Writer: codex"),
                     envelope(1).replace("- Writer: claude", "- Writer: someone"),
                     envelope(1).replace("OVERALL: approve", "OVERALL: blocked"),
                     envelope(1).replace("OVERALL: approve", ""),
                     "## Round 1: astra (`m` via `codex`)\n\n- Writer: claude\n- Run: 2026-10-21T00:00:00Z\n- Lines: 0\n",
                     "## Round 1: fake\n\nNo reviewer returned a review.\n",
                     "## Round 1: astra (`m` via `codex`)\n\nNo metadata block.\n"):
            report.write_text(text, encoding="utf-8")
            with self.assertRaises(tg.Refused) as ctx:
                self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
            self.assertRegex(str(ctx.exception), "is not a review returned by another model family|"
                                                 "is not a findings file the dispatcher can read|holds no returned review|"
                                                 "must number its rounds")
        for bad in (0, -1, True):
            with self.assertRaises(tg.Refused):
                self.stamp("D00 T01 §1", self.sha,
                         [{"round": bad, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": "clean"}],
                         ["true -> ok"], "2026-10-13")
        report.write_text("# Review findings\n\n" + envelope(1), encoding="utf-8")
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        # A receipt edited to claim a round its report never recorded no longer validates.
        data = json.loads((self.root / receipt).read_text(encoding="utf-8"))
        data["reviews"][0]["round"] = 999
        data["rounds_spent"] = 999
        (self.root / receipt).write_text(json.dumps(data), encoding="utf-8")
        self.refuses("round 999 is not a review returned by another model family")

    def test_a_stamped_section_with_a_waiting_need_refuses(self):
        self.tick_work()
        self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        path = self.root / "todo/00-workspace/TODO-01-a.md"
        path.write_text(path.read_text(encoding="utf-8").replace("**Needs:** none", "**Needs:** owner-decision", 1),
                        encoding="utf-8")
        self.refuses("stamped section has a waiting Need")

    def test_stale_deferral_refuses_when_owner_stamps(self):
        self.tick_work()
        path = self.root / "todo/00-workspace/TODO-01-a.md"
        text = path.read_text(encoding="utf-8")
        target = "## 2. B\n"
        path.write_text(text.replace(target, target + "\n> **Deferred:** wait on 1 -> XREF: §1\n"),
                        encoding="utf-8")
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        self.assertIn("stale deferral", str(ctx.exception))

    def test_unticked_items_refuse(self):
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        self.assertIn("unticked", str(ctx.exception))

    def test_waiting_section_cannot_be_stamped(self):
        with self.assertRaises(tg.Refused):
            self.stamp("D00 T01 §2", self.sha, self.reviews, ["true -> ok"], "2026-10-13")

    def test_unknown_commit_refuses(self):
        self.tick_work()
        with self.assertRaises(tg.Refused):
            self.stamp("D00 T01 §1", "0" * 40, self.reviews, ["true -> ok"], "2026-10-13")

    def test_no_review_round_refuses(self):
        self.tick_work()
        with self.assertRaises(tg.Refused):
            self.stamp("D00 T01 §1", self.sha, [], ["true -> ok"], "2026-10-13")

    def test_missing_review_report_refuses(self):
        self.tick_work()
        for bad_path in (r"\Users\outside.md", "../outside.md", "docs/reviews/../outside.md", "outside.md"):
            bad_round = [{"round": 1, "report": bad_path, "verdict": "clean"}]
            with self.assertRaises(tg.Refused) as ctx:
                self.stamp("D00 T01 §1", self.sha, bad_round, ["true -> ok"], "2026-10-13")
            self.assertIn("invalid review report path", str(ctx.exception))
        missing = [{"round": 1, "report": "docs/reviews/00-workspace/missing.md", "verdict": "clean"}]
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T01 §1", self.sha, missing, ["true -> ok"], "2026-10-13")
        self.assertIn("missing or empty review report", str(ctx.exception))
        self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        (self.root / "docs/reviews/00-workspace/D00-T01-s1.md").unlink()
        self.refuses("missing or empty review report")

    def test_receipt_for_another_commit_refuses(self):
        self.tick_work()
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        data = json.loads((self.root / receipt).read_text(encoding="utf-8"))
        data["implementation_sha"] = "f" * 40
        (self.root / receipt).write_text(json.dumps(data), encoding="utf-8")
        self.refuses("receipt does not match its stamp")

    def test_matching_fabricated_sha_in_stamp_and_receipt_refuses(self):
        self.tick_work()
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        fake_sha = "0" * 40
        todo_path = self.root / "todo/00-workspace/TODO-01-a.md"
        todo_path.write_text(todo_path.read_text(encoding="utf-8").replace(self.sha, fake_sha),
                             encoding="utf-8")
        receipt_path = self.root / receipt
        receipt_path.write_text(receipt_path.read_text(encoding="utf-8").replace(self.sha, fake_sha),
                                encoding="utf-8")
        self.refuses("not a commit")

    def snapshot(self):
        self.ensure_plan()  # the fixture's plan review is put back before, not during, a refused stamp
        return {str(f.relative_to(self.root)): f.read_bytes()
                for f in self.root.rglob("*") if f.is_file() and ".git" not in f.parts}

    def test_bad_verdict_writes_nothing(self):
        self.tick_work()
        before = self.snapshot()
        bad = [{"round": 1, "report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": "cleen"}]
        with self.assertRaises(tg.Refused):
            self.stamp("D00 T01 §1", self.sha, bad, ["true -> ok"], "2026-10-13")
        self.assertEqual(self.snapshot(), before)
        tg.graph(self.root)

    def test_bad_date_writes_nothing(self):
        self.tick_work()
        before = self.snapshot()
        with self.assertRaises(tg.Refused):
            self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "13 Oct")
        self.assertEqual(self.snapshot(), before)

    def test_failed_stamp_write_rolls_back_receipt_and_allows_retry(self):
        self.tick_work()
        tg.projections(self.root, sync=True)
        before = self.snapshot()
        real_atomic = tg.atomic
        for fail_at in (2, 3):
            calls = []

            def fail_on_nth_write(path, data, target=fail_at):
                calls.append(path)
                if len(calls) == target:
                    raise OSError("simulated disk write failure")
                return real_atomic(path, data)

            tg.atomic = fail_on_nth_write
            try:
                with self.assertRaises(OSError):
                    self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"],
                             "2026-10-13", sync_plan=True)
            finally:
                tg.atomic = real_atomic
            self.assertEqual(len(calls), 4 if fail_at == 3 else 2)
            self.assertFalse((self.root / "docs/reviews/d00-t01-s1.json").exists())
            self.assertEqual(self.snapshot(), before)
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"],
                           "2026-10-13", sync_plan=True)
        self.assertTrue((self.root / receipt).is_file())
        tg.projections(self.root)

    def test_an_interrupted_stamp_rolls_back_and_allows_retry(self):
        self.tick_work()
        tg.projections(self.root, sync=True)
        before = self.snapshot()
        real_atomic = tg.atomic
        calls = []

        def interrupt_on_second_write(path, data):
            calls.append(path)
            if len(calls) == 2:
                raise KeyboardInterrupt
            return real_atomic(path, data)

        tg.atomic = interrupt_on_second_write
        try:
            with self.assertRaises(KeyboardInterrupt):
                self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13", sync_plan=True)
        finally:
            tg.atomic = real_atomic
        self.assertEqual(self.snapshot(), before)
        self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13", sync_plan=True)

    def test_unstamp_reopens_a_regressed_section_and_allows_a_restamp(self):
        self.tick_work()
        tg.projections(self.root, sync=True)
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13", sync_plan=True)
        before = self.snapshot()
        for bad in (("D00 T01 §2", "x"), ("D00 T01 §1", ""), ("D00 T01 §1", "two\nlines")):
            with self.assertRaises(tg.Refused):
                tg.unstamp(self.root, bad[0], bad[1], "2026-10-21")
        self.assertEqual(self.snapshot(), before)
        revoked = tg.unstamp(self.root, "D00 T01 §1", "checkpoint fails at abc1234; repair -> XREF: §2", "2026-10-21")
        rows = tg.graph(self.root)
        self.assertFalse(rows["D00 T01 §1"].done)
        self.assertEqual(tg.disposition(rows["D00 T01 §1"], rows), 0)
        self.assertFalse((self.root / receipt).exists())
        self.assertEqual((self.root / revoked).read_bytes(), before[str(pathlib.Path(receipt))])
        text = (self.root / "todo/00-workspace/TODO-01-a.md").read_text(encoding="utf-8")
        self.assertIn("> **Unstamped:** 2026-10-21: checkpoint fails", text)
        self.assertIn("- [ ] Commit:", text)
        tg.projections(self.root)  # the plan was synced
        self.restamp_after_repair()

    def test_gaps_repair_clean_reviews_then_a_stamp(self):
        self.tick_work()
        tg.projections(self.root, sync=True)
        dispatch = tg.review_dispatch()
        result = {"writer": "claude", "writer_model": "claude-opus-5-5", "reviewer": "astra", "cli": "codex", "model": "m",
                  "effort": "medium", "execution": "returned", "attempt_reviewer": "astra", "attempt_model": "m",
                  "attempt_cli": "codex", "failover": [], "lenses": [], "review": "Text.\n\nCOMPLETION: gaps"}
        own = self.root / "docs/reviews/00-workspace/D00-T01-s1.md"
        exhaust(self.root, own)
        kept = own.read_bytes()
        name = tg.repair(self.root, "D00 T01 §1", "its rounds ran out", "2026-10-21")
        self.assertEqual(name, "docs/reviews/00-workspace/D00-T01-s1-cycle2.md")
        self.restamp_after_repair()
        self.assertEqual(own.read_bytes(), kept)
        tg.graph(self.root)  # validation still holds for the whole tree

    def test_a_filed_stamp_persists_and_validates_in_a_fresh_process(self):
        self.tick_work()
        tg.projections(self.root, sync=True)
        self.write_completion(review="1. Gap: x.\n\nCOMPLETION: gaps", notes="- Filed: D00 T01 §2\n")
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13", sync_plan=True,
                             completion={"report": self.COMPLETION, "verdict": "filed"})
        data = json.loads((self.root / receipt).read_text(encoding="utf-8"))
        self.assertEqual(data["completion"]["filed"], ["D00 T01 §2"])
        tool = self.root / "scripts"
        (tool / "lib").mkdir(parents=True, exist_ok=True)
        for name in ("todo-graph.py", "review-dispatch.py", "lib/todo_graph.py", "lib/host_caps.py"):
            (tool / name).write_bytes((REPO / "scripts" / name).read_bytes())
        done = subprocess.run([sys.executable, str(tool / "todo-graph.py"), "validate"], cwd=self.root, capture_output=True, text=True)
        self.assertEqual(done.returncode, 0, done.stderr + done.stdout)
        rows = tg.graph(self.root)
        self.assertTrue(rows["D00 T01 §1"].done)
        self.assertFalse(rows["D00 T01 §2"].done)

    def test_a_stamp_cut_off_between_its_writes_recovers_on_retry(self):
        self.tick_work()
        tg.projections(self.root, sync=True)
        real = tg.atomic
        calls = []

        def dies_after_the_receipt(path, data):
            calls.append(path)
            real(path, data)
            if len(calls) == 1:
                raise SystemExit("power lost")  # rollback can't be trusted to run, so leave the receipt as it fell
        with mock.patch.object(tg, "atomic", dies_after_the_receipt), mock.patch.object(tg.Path, "unlink"):
            with self.assertRaises(SystemExit):
                self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        receipt = self.root / "docs/reviews/d00-t01-s1.json"
        self.assertTrue(receipt.exists())
        self.assertFalse(tg.graph(self.root)["D00 T01 §1"].done)
        self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        self.assertTrue(tg.graph(self.root)["D00 T01 §1"].done)
        self.assertTrue((self.root / "docs/reviews/d00-t01-s1.orphan-2026-10-13.json").exists())

    def test_a_full_disk_through_the_rollback_leaves_a_stamp_with_its_receipt(self):
        self.tick_work()
        tg.projections(self.root, sync=True)
        real = tg.atomic
        calls = []

        def disk_fills_after_the_section(path, data):
            calls.append(path)
            if len(calls) > 2:
                raise OSError(28, "No space left on device")
            real(path, data)
        with mock.patch.object(tg, "atomic", disk_fills_after_the_section):
            with self.assertRaises(OSError):
                self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13", sync_plan=True)
        self.assertGreater(len(calls), 3)  # plan regeneration and the rollback were both refused
        self.assertTrue((self.root / "docs/reviews/d00-t01-s1.json").exists())
        self.assertTrue(tg.graph(self.root)["D00 T01 §1"].done)  # valid: stamped, with its receipt
        tg.projections(self.root, sync=True)  # the disk cleared
        tg.projections(self.root, sync=False)

    def test_stamp_refuses_filing_to_a_stamped_section_and_the_receipt_survives_its_target_shipping(self):
        self.write(todo_file([(1, "A", 0, "none", "--"), (2, "B", 0, "none", "--"), (3, "C", 0, "none", "--")]).replace(
            "**Test checkpoint:**", STARTED + "**Test checkpoint:**"))
        for n in (1, 2, 3):
            report = self.root / f"docs/reviews/00-workspace/D00-T01-s{n}.md"
            report.write_text("# Review findings\n\n" + envelope(1), encoding="utf-8")
        path = self.root / "todo/00-workspace/TODO-01-a.md"
        path.write_text(path.read_text(encoding="utf-8").replace("- [ ] Work.", "- [x] Work."), encoding="utf-8")
        tg.projections(self.root, sync=True)
        sha = self.commit("three sections")
        for n in (1, 2, 3):
            self.write_plan(path=f"docs/reviews/00-workspace/D00-T01-s{n}.md", keep=True, candidate=self.start)
        sha = self.commit("plan reviews")
        rounds = lambda n: [{"round": 1, "report": f"docs/reviews/00-workspace/D00-T01-s{n}.md", "verdict": "clean"}]
        own = lambda n: f"docs/reviews/00-workspace/D00-T01-s{n}-completion.md"
        for n in (2, 3):
            self.write_completion(path=own(n))
        self.stamp("D00 T01 §2", sha, rounds(2), ["true -> ok"], "2026-10-13", plan="docs/reviews/00-workspace/D00-T01-s2.md",
                   completion={"report": own(2), "verdict": "complete"})
        self.write_completion(review="1. Gap: x.\n\nCOMPLETION: gaps", notes="- Filed: D00 T01 §2\n", path=own(1))
        with self.assertRaises(tg.Refused) as caught:
            self.stamp("D00 T01 §1", sha, rounds(1), ["true -> ok"], "2026-10-13", plan="docs/reviews/00-workspace/D00-T01-s1.md",
                       completion={"report": own(1), "verdict": "filed"})
        self.assertIn("open section", str(caught.exception))
        self.write_completion(review="1. Gap: x.\n\nCOMPLETION: gaps", notes="- Filed: D00 T01 §3\n", path=own(1))
        self.stamp("D00 T01 §1", sha, rounds(1), ["true -> ok"], "2026-10-13", plan="docs/reviews/00-workspace/D00-T01-s1.md",
                   completion={"report": own(1), "verdict": "filed"})
        self.stamp("D00 T01 §3", sha, rounds(3), ["true -> ok"], "2026-10-13", plan="docs/reviews/00-workspace/D00-T01-s3.md",
                   completion={"report": own(3), "verdict": "complete"})
        rows = tg.graph(self.root)  # every receipt still validates, the filing one included, now its target shipped
        self.assertTrue(all(rows[f"D00 T01 §{n}"].done for n in (1, 2, 3)))

    def test_stamp_refuses_a_parked_section(self):
        self.tick_work()
        tg.projections(self.root, sync=True)
        tg.park(self.root, "D00 T01 §1", "x", "2026-10-21")
        with self.assertRaises(tg.Refused) as caught:
            self.stamp("D00 T01 §1", self.commit("parked"), self.reviews, ["true -> ok"], "2026-10-21")
        self.assertIn("parked", str(caught.exception))

    def test_exhaustion_park_grant_repair_then_a_stamp(self):
        self.tick_work()
        tg.projections(self.root, sync=True)
        dispatch = tg.review_dispatch()
        result = {"writer": "claude", "writer_model": "claude-opus-5-5", "reviewer": "astra", "cli": "codex", "model": "m",
                  "effort": "medium", "execution": "returned", "attempt_reviewer": "astra", "attempt_model": "m",
                  "attempt_cli": "codex", "failover": [], "lenses": [], "review": "Text.\n\nCOMPLETION: gaps"}
        folder = self.root / "docs/reviews/00-workspace"
        exhaust(self.root, folder / "D00-T01-s1.md")
        for k in range(2, 5):
            tg.repair(self.root, "D00 T01 §1", f"repair {k}", "2026-10-21")
            exhaust(self.root, folder / f"D00-T01-s1-cycle{k}.md")
        with self.assertRaises(tg.Refused):
            tg.repair(self.root, "D00 T01 §1", "a fourth", "2026-10-21")
        tg.park(self.root, "D00 T01 §1", "three repairs failed", "2026-10-21")
        tg.unpark(self.root, "D00 T01 §1", "owner read the findings", "2026-10-22")
        name = tg.repair(self.root, "D00 T01 §1", "granted", "2026-10-22")
        self.assertEqual(name, "docs/reviews/00-workspace/D00-T01-s1-cycle5.md")
        self.restamp_after_repair(name)
        self.assertTrue(tg.graph(self.root)["D00 T01 §1"].done)
        for k in range(2, 5):  # every cycle's findings stand
            self.assertIn("HIGHEST: P1", (folder / f"D00-T01-s1-cycle{k}.md").read_text(encoding="utf-8"))

    def restamp_after_repair(self, cycle="docs/reviews/00-workspace/D00-T01-s1-cycle2.md"):
        """A reopened section re-stamps on its repair: a plan review, a fresh Started line, a completion review and
        rounds, all in its cycle findings file. The regressed cycle's evidence refuses."""
        unstamped = self.commit("unstamp")
        self.assertEqual(tg.section_findings(self.root, "D00 T01 §1"), cycle)
        # The first cycle's plan review, completion review and rounds approved the work that regressed.
        with self.assertRaises(tg.Refused) as ctx:
            tg.stamp(self.root, "D00 T01 §1", unstamped, self.reviews, ["true -> ok"], "2026-10-22",
                     completion={"report": self.COMPLETION, "verdict": "complete"}, plan=self.PLAN)
        self.assertIn(f"--plan must be the section's own findings file {cycle}", str(ctx.exception))
        # The repair's plan review against the reopened contract, but the old Started line kept: the build start is
        # the unstamp commit, so a plan review made after it is not before the build.
        (self.root / cycle).write_text("# Review findings\n\n", encoding="utf-8")
        self.write_plan(path=cycle, keep=True, candidate=self.commit("repair plan reviewed"))
        self.write_completion(path=cycle, keep=True, candidate=unstamped)
        with open(self.root / cycle, "a", encoding="utf-8", newline="") as handle:
            handle.write("\n" + envelope(1))
        repaired = self.commit("repair reviewed")
        cycle_rounds = [{"round": 1, "report": cycle, "verdict": "clean"}]
        with self.assertRaises(tg.Refused) as ctx:
            tg.stamp(self.root, "D00 T01 §1", repaired, cycle_rounds, ["true -> ok"], "2026-10-22",
                     completion={"report": cycle, "verdict": "complete"}, plan=cycle)
        self.assertIn("is not the commit the build began at", str(ctx.exception))
        # A fresh Started line, committed after the repair's plan review, starts the repair's build.
        path = self.root / "todo/00-workspace/TODO-01-a.md"
        path.write_text(path.read_text(encoding="utf-8").replace(STARTED.strip(), "> **Started:** 2026-10-22T00:00Z", 1),
                        encoding="utf-8")
        tg.projections(self.root, sync=True)
        started = self.commit("repair started")
        plan_text = (self.root / self.PLAN).read_text(encoding="utf-8")
        (self.root / cycle).unlink()
        self.write_plan(path=cycle, keep=True, candidate=unstamped)
        self.write_completion(path=cycle, keep=True, candidate=started)
        with open(self.root / cycle, "a", encoding="utf-8", newline="") as handle:
            handle.write("\n" + envelope(1))
        repaired = self.commit("repair reviewed again")
        # Rounds or a completion review from the regressed cycle's files refuse even with the repair's plan review. The
        # old rounds are named as their own loop ended, so the only fault left is where they come from.
        last = len(tg.review_dispatch().read_rounds((self.root / self.PLAN).read_text(encoding="utf-8")))
        old = self.reviews if last <= 1 else [{"round": last, "report": self.PLAN, "verdict": f"stopped-at-{tg.round_cap(self.root)}"}]
        for rounds, completion in ((old, cycle), (cycle_rounds, self.COMPLETION)):
            stopped = rounds[0]["verdict"].startswith("stopped-at-")
            with self.assertRaises(tg.Refused) as ctx:
                tg.stamp(self.root, "D00 T01 §1", repaired, rounds, ["true -> ok"], "2026-10-22",
                         completion={"report": completion, "verdict": "complete"}, plan=cycle,
                         cap_outcome="repaired in a new cycle" if stopped else None)
            self.assertIn("must come from its repair's findings file", str(ctx.exception))
        tg.stamp(self.root, "D00 T01 §1", repaired, cycle_rounds, ["true -> ok"], "2026-10-22", sync_plan=True,
                 completion={"report": cycle, "verdict": "complete"}, plan=cycle)
        self.assertTrue(tg.graph(self.root)["D00 T01 §1"].done)
        self.assertEqual((self.root / self.PLAN).read_text(encoding="utf-8"), plan_text)

    def test_unstamping_a_prerequisite_leaves_its_consumers_stamped_and_suspect(self):
        path = self.root / "todo/00-workspace/TODO-02-b.md"
        path.write_text(todo_file([(1, "Base", 0, "none", "--"), (2, "Mid", 0, "none", "§1"), (3, "Top", 0, "none", "§2")],
                                  ident="bravo").replace("- [ ] Work.", "- [x] Work.")
                        .replace("**Test checkpoint:**", STARTED + "**Test checkpoint:**"), encoding="utf-8")
        self.index("TODO-02-b.md")
        self.commit("start bravo")
        for n in (1, 2, 3):
            self.stamp(f"D00 T02 §{n}", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        tg.unstamp(self.root, "D00 T02 §1", "checkpoint fails", "2026-10-21")
        rows = tg.graph(self.root)
        self.assertEqual([rows[f"D00 T02 §{n}"].done for n in (1, 2, 3)], [False, True, True])
        self.assertEqual(tg.suspects(self.root, "D00 T02 §1"), ["D00 T02 §2", "D00 T02 §3"])
        # New work on a suspect stamp waits, and cannot stamp, until the reopened ancestor re-stamps.
        extra = self.root / "todo/00-workspace/TODO-03-c.md"
        extra.write_text(todo_file([(1, "On top", 0, "none", "D00 T02 §3")], ident="charlie")
                         .replace("- [ ] Work.", "- [x] Work."), encoding="utf-8")
        self.index("TODO-03-c.md")
        rows = tg.graph(self.root)
        self.assertEqual(tg.disposition(rows["D00 T03 §1"], rows), 4)
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T03 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-21")
        self.assertIn("resting on a reopened section", str(ctx.exception))
        extra.unlink()
        # Without the Unstamped note, a stamped consumer of an open section still refuses.
        path.write_text(path.read_text(encoding="utf-8").replace("> **Unstamped:** ", "> **Note:** "), encoding="utf-8")
        self.refuses("stamped section has open dependency")

    def test_an_interrupted_unstamp_rolls_back(self):
        self.tick_work()
        tg.projections(self.root, sync=True)
        self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13", sync_plan=True)
        before = self.snapshot()
        real_atomic = tg.atomic
        calls = []

        def interrupt_on_second_write(path, data):
            calls.append(path)
            if len(calls) == 2:
                raise KeyboardInterrupt
            return real_atomic(path, data)

        tg.atomic = interrupt_on_second_write
        try:
            with self.assertRaises(KeyboardInterrupt):
                tg.unstamp(self.root, "D00 T01 §1", "regressed", "2026-10-21")
        finally:
            tg.atomic = real_atomic
        self.assertEqual(self.snapshot(), before)
        tg.graph(self.root)

    def test_stamping_the_last_section_marks_the_file_done(self):
        path = self.root / "todo/00-workspace/TODO-02-b.md"
        path.write_text(todo_file([(1, "Only", 0, "none", "--")], ident="bravo")
                        .replace("- [ ] Work.", "- [x] Work.")
                        .replace("**Test checkpoint:**", STARTED + "**Test checkpoint:**"), encoding="utf-8")
        self.index("TODO-02-b.md")
        self.commit("start bravo")
        self.stamp("D00 T02 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        self.assertIn("status: done", path.read_text(encoding="utf-8"))
        self.assertTrue(tg.graph(self.root)["D00 T02 §1"].done)

    def test_malformed_receipt_refuses_instead_of_crashing(self):
        self.tick_work()
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        unhashable = json.dumps({"ref": "D00 T01 §1", "implementation_sha": self.sha,
                                 "reviews": [{"round": 1, "report": "r", "verdict": []}],
                                 "checks": ["x"], "date": "2026-10-13"})
        for bad in ("[]", json.dumps({"ref": "D00 T01 §1", "implementation_sha": self.sha,
                                      "reviews": [None], "checks": ["x"], "date": "2026-10-13"}),
                    "not json", unhashable):
            (self.root / receipt).write_text(bad, encoding="utf-8")
            self.refuses("receipt")

    def test_missing_receipt_refuses(self):
        self.tick_work()
        receipt = self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-13")
        (self.root / receipt).unlink()
        self.refuses("missing receipt")

    # The plan review and the review schedule (2026-10-24): stamp refuses a section whose reviews did not follow them.

    LOOP = "docs/reviews/00-workspace/D00-T01-s1.md"
    ROUND_1 = ["adversarial", "integration", "source-defect", "plan-delivery"]

    def live(self, surface=False):
        """Pin the live schedule shape (and, with surface, make §1 a surface section), committed as the stamped commit."""
        (self.root / tg.PIN).write_text(SCHEDULE_PIN, encoding="utf-8")
        if surface:
            path = self.root / "todo/00-workspace/TODO-01-a.md"
            path.write_text(path.read_text(encoding="utf-8").replace(
                "**Surface:** no", "**Surface:** yes\n**Fidelity:** f.\n**Job:** j.\n**Treatment:** t.\n**Chrome:** c.",
                1), encoding="utf-8")
        self.sha = self.commit("live schedule")

    def loop(self, steps, surface=False):
        """Record a review loop as the dispatcher would, each round at the pass `scheduled` gives it. A step is a
        severity (a returned needs-attention review declaring it), "clean", "outage", "self" (a self-review) or
        "malformed" (a reply without the final OVERALL line)."""
        dispatch = tg.review_dispatch()
        schedule = tg.pin_schedule(self.root, self.sha, "test")
        path = self.root / self.LOOP
        if path.exists():  # a fresh loop: the rounds go, the section's plan review stays
            path.write_bytes(self.only(path.read_bytes().decode("utf-8"), plans=True).encode("utf-8"))
        for step in steps:
            existing = path.read_bytes().decode("utf-8") if path.exists() else ""
            plan = dispatch.scheduled(schedule, "round", dispatch.read_rounds(existing), surface)
            result = {"writer": "claude", "effort": plan["effort"], "lenses": plan["lenses"], "failover": [],
                      "reviewer": "astra", "model": "m", "cli": "codex",
                      "review": "1. ok.\n\nHIGHEST: none\n\nOVERALL: approve"}
            if step == "outage":
                result.update(reviewer=None, review=None, skipped=[{"reviewer": "astra", "reason": "exited 3"}])
            elif step == "self":
                result.update(reviewer="opus", cli="claude")
            elif step == "malformed":
                result["review"] = "1. The reply stopped before its verdict."
            elif step != "clean":
                result["review"] = f"1. [{step}] gap.\n\nHIGHEST: {step}\n\nOVERALL: needs-attention"
            dispatch.record(path, result, "2026-10-23T00:00:00Z")
        self.reviews = [{"round": len(steps), "report": self.LOOP, "verdict": "clean"}]

    def edit_round(self, n, effort, lenses):
        """Rewrite round n's Effort and Lenses lines, which sit outside its digest, as a hand edit would."""
        path = self.root / self.LOOP
        text = path.read_bytes().decode("utf-8")
        start = text.index(f"## Round {n}: ")
        end = text.find("\n## Round ", start)
        end = len(text) if end < 0 else end
        block = re.sub(r"^- Effort: .*$", f"- Effort: {effort}", text[start:end], count=1, flags=re.M)
        block = re.sub(r"^- Lenses: .*$", f"- Lenses: {', '.join(lenses)}", block, count=1, flags=re.M)
        path.write_bytes((text[:start] + block + text[end:]).encode("utf-8"))

    def stamp_refuses(self, fragment):
        self.tick_work()
        before = self.snapshot()
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-24")
        self.assertIn(fragment, str(ctx.exception))
        self.assertEqual(self.snapshot(), before, "a refused stamp wrote something")
        return str(ctx.exception)

    def test_a_stamp_without_a_plan_review_refuses(self):
        self.tick_work()
        before = self.snapshot()
        with self.assertRaises(tg.Refused) as ctx:
            tg.stamp(self.root, "D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-24",
                     completion={"report": self.COMPLETION, "verdict": "complete"})
        self.assertIn("--plan '<findings file>' is required", str(ctx.exception))
        self.assertEqual(self.snapshot(), before)

    def test_a_plan_review_must_be_one_returned_by_another_model_family(self):
        family = "must hold exactly one plan review returned by another model family"
        for kwargs in ({"reviewer": "opus", "cli": "claude"}, {"reviewer": None},
                       {"review": "1. ok.\n\nHIGHEST: none\n\nOVERALL: approve"},
                       {"review": "1. ok.\n\nOVERALL: approve\n\nPLAN: complete"}):
            with self.subTest(kwargs=kwargs):
                self.write_plan(**kwargs)
                self.stamp_refuses(family)
        self.write_plan()
        path = self.root / self.PLAN
        text = path.read_bytes().decode("utf-8")
        path.write_bytes(self.only(text, plans=False).encode("utf-8"))
        self.tick_work()
        with self.assertRaises(tg.Refused) as ctx:
            tg.stamp(self.root, "D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-24", plan=self.PLAN,
                     completion={"report": self.COMPLETION, "verdict": "complete"})
        self.assertIn("holds no ## Plan review", str(ctx.exception))
        path.write_bytes(text.encode("utf-8"))
        for plan in ("docs/reviews/00-workspace/D00-T01-s2.md", self.COMPLETION,
                     "docs\\reviews\\00-workspace\\D00-T01-s1.md", "../plan.md"):
            with self.subTest(plan=plan):
                self.tick_work()
                with self.assertRaises(tg.Refused) as ctx:
                    self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-24", plan=plan)
                self.assertIn(f"--plan must be the section's own findings file {self.PLAN}", str(ctx.exception))
        path.unlink()
        self.tick_work()
        with self.assertRaises(tg.Refused) as ctx:
            tg.stamp(self.root, "D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-24", plan=self.PLAN,
                     completion={"report": self.COMPLETION, "verdict": "complete"})
        self.assertIn("missing or empty review report", str(ctx.exception))

    def test_another_sections_plan_review_does_not_stand_in(self):
        # §2's own findings file holds a returned plan review on the build-start commit; §1 cannot stamp on it.
        other = "docs/reviews/00-workspace/D00-T01-s2.md"
        self.write_plan(path=other)
        self.tick_work()
        with self.assertRaises(tg.Refused) as ctx:
            self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-24", plan=other)
        self.assertIn(f"--plan must be the section's own findings file {self.PLAN}, not {other}", str(ctx.exception))
        # Nor through a receipt edited after the stamp to name it.
        receipt = self.root / self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-24")
        data = json.loads(receipt.read_text(encoding="utf-8"))
        data["plan"]["report"] = other
        receipt.write_text(json.dumps(data), encoding="utf-8")
        self.refuses(f"--plan must be the section's own findings file {self.PLAN}, not {other}")

    def test_a_plan_review_after_the_build_began_refuses(self):
        later = self.commit("the build")
        self.write_plan(candidate=later)
        self.stamp_refuses("is not the commit the build began at")
        self.write_plan(candidate="f" * 40)
        self.stamp_refuses("not a commit")
        # Without a Started line or a commit naming the section, there is no build start to check the plan against.
        self.write_plan()
        path = self.root / "todo/00-workspace/TODO-01-a.md"
        path.write_text(path.read_text(encoding="utf-8").replace(STARTED, ""), encoding="utf-8")
        self.stamp_refuses("cannot find the commit the build began at")

    def test_the_plan_and_completion_passes_run_at_their_scheduled_effort(self):
        self.live()
        self.loop(["clean"])
        self.write_plan(effort="high")
        self.stamp_refuses("the plan review in docs/reviews/00-workspace/D00-T01-s1.md ran at high")
        self.write_plan(lenses=("adversarial",))
        self.stamp_refuses("lenses ['adversarial']; the schedule expects medium with ['plan']")
        self.write_plan()
        self.write_completion(effort="xhigh")
        self.stamp_refuses("the completion review in docs/reviews/00-workspace/D00-T01-s1-completion.md ran at xhigh")

    def test_every_plan_and_completion_attempt_runs_at_its_scheduled_effort(self):
        self.live()
        self.loop(["clean"])
        self.write_plan(reviewer=None, effort="low", lenses=("adversarial",))
        self.write_plan(keep=True)
        self.stamp_refuses("the plan review in docs/reviews/00-workspace/D00-T01-s1.md ran at low with lenses "
                           "['adversarial']")
        self.write_plan()
        self.write_completion(reviewer=None, effort="low")
        self.write_completion(keep=True)
        self.stamp_refuses("the completion review in docs/reviews/00-workspace/D00-T01-s1-completion.md ran at low")

    def test_the_schedule_check_needs_no_temporary_directory(self):
        # A reviewer's read-only sandbox runs validate with no writable temporary directory.
        self.live()
        real = tempfile.tempdir
        tempfile.tempdir = str(self.root / "no-such-directory")
        try:
            schedule = tg.pin_schedule(self.root, self.sha, "D00 T01 §1")
        finally:
            tempfile.tempdir = real
        self.assertEqual(schedule["plan"], {"effort": "medium", "lenses": ["plan"]})

    def test_the_build_start_matches_the_whole_reference(self):
        (self.root / "src").mkdir(exist_ok=True)
        (self.root / "src/a.txt").write_text("a", encoding="utf-8")
        self.commit("build\n\nD00 T01 §11")
        (self.root / "src/b.txt").write_text("b", encoding="utf-8")
        mine = self.commit("build\n\nThe section (D00 T01 §1).")
        self.assertEqual(tg.build_start(self.root, "todo/00-workspace/TODO-01-a.md", "", "D00 T01 §1"), mine)

    def test_the_build_start_is_the_sections_own_whole_started_line(self):
        # §1's Started line is in the first commit; §2 records a prefix of it, then the identical line, later.
        path = "todo/00-workspace/TODO-01-a.md"

        def record(line):
            text = (self.root / path).read_text(encoding="utf-8")
            two = tg.section_text(text, 2)
            two_now = re.sub(r"(?m)^> \*\*Started:\*\* .*\n\n", "", two)
            two_now = two_now.replace("**Test checkpoint:**", line + "\n\n**Test checkpoint:**", 1)
            (self.root / path).write_text(text.replace(two, two_now), encoding="utf-8")
            return self.commit("start §2"), tg.section_text((self.root / path).read_text(encoding="utf-8"), 2)

        prefix, body = record("> **Started:** 2026-10-23")
        self.assertEqual(tg.build_start(self.root, path, body, "D00 T01 §2"), prefix)
        whole, body = record(STARTED.strip())
        self.assertEqual(tg.build_start(self.root, path, body, "D00 T01 §2"), whole)
        self.assertEqual(tg.build_start(self.root, path, tg.section_text((self.root / path).read_text(
            encoding="utf-8"), 1), "D00 T01 §1"), self.start)

    def test_round_1_runs_at_high_effort(self):
        self.live()
        self.loop(["clean"])
        self.edit_round(1, "medium", self.ROUND_1)
        message = self.stamp_refuses(f"{self.LOOP} round 1 ran at medium")
        self.assertIn(f"the schedule expects high with {self.ROUND_1} (position 1)", message)

    def test_round_1_runs_all_four_lenses(self):
        self.live()
        self.loop(["clean"])
        self.edit_round(1, "high", self.ROUND_1[:3])
        self.stamp_refuses(f"round 1 ran at high with lenses {self.ROUND_1[:3]}; the schedule expects high with "
                           f"{self.ROUND_1}")

    def test_a_rotation_round_runs_its_one_lens(self):
        self.live()
        self.loop(["P2", "P2", "P2", "clean"])
        self.edit_round(4, "low", ["quality"])
        self.stamp_refuses("round 4 ran at low with lenses ['quality']; the schedule expects low with ['consistency'] "
                           "(position 4)")

    def test_the_design_lens_is_only_for_surface_sections(self):
        self.live()
        self.loop(["P2", "P2", "P2", "P2", "P2", "clean"])
        self.edit_round(6, "low", ["design"])
        self.stamp_refuses("round 6 ran at low with lenses ['design']; the schedule expects low with ['optimization']")

    def test_a_low_round_with_a_p1_escalates_the_next(self):
        self.live()
        self.loop(["P2", "P2", "P1", "clean"])
        self.edit_round(4, "low", ["consistency"])
        self.stamp_refuses("round 4 ran at low with lenses ['consistency']; the schedule expects medium with "
                           "['adversarial'] (position 4, escalated)")

    def test_an_outage_or_a_self_review_does_not_advance_the_schedule(self):
        for skipped in ("outage", "self", "malformed"):
            with self.subTest(skipped=skipped):
                self.live()
                self.loop([skipped, "clean"])
                self.edit_round(2, "medium", ["adversarial"])
                self.stamp_refuses(f"round 2 ran at medium with lenses ['adversarial']; the schedule expects high with "
                                   f"{self.ROUND_1} (position 1)")

    def test_a_receipt_records_the_plan_review_and_schedule_and_edits_refuse(self):
        self.tick_work()
        receipt = self.root / self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-24")
        data = json.loads(receipt.read_text(encoding="utf-8"))
        self.assertEqual(data["plan"], {"report": self.PLAN, "candidate": self.sha, "reviewer": "astra", "model": "m",
                                        "effort": "medium", "lenses": ["plan"]})
        self.assertEqual(data["schedule"]["rounds"], [{"effort": "low", "lenses": ["adversarial"]}])
        edits = [("plan", "effort", "high"), ("plan", "lenses", []), ("plan", "candidate", "f" * 40),
                 ("plan", "reviewer", "sol"), ("plan", "model", "other"), ("plan", "report", self.COMPLETION),
                 ("plan", "extra", 1), ("schedule", "severity_stop_after", 2)]
        for where, key, value in edits:
            with self.subTest(where=where, key=key):
                edited = json.loads(json.dumps(data))
                edited[where][key] = value
                receipt.write_text(json.dumps(edited), encoding="utf-8")
                with self.assertRaises(tg.Refused):
                    tg.graph(self.root)
        for dropped, fragment in ((("plan",), "plan and schedule together"),
                                  (("plan", "schedule"), "a receipt without a plan review must be one of the old")):
            with self.subTest(dropped=dropped):
                edited = json.loads(json.dumps(data))
                for key in dropped:
                    del edited[key]
                receipt.write_text(json.dumps(edited), encoding="utf-8")
                self.refuses(fragment)
        receipt.write_text(json.dumps(data), encoding="utf-8")
        tg.graph(self.root)
        # The recorded plan pass is what the findings file still says: its effort line edited after the stamp refuses.
        path = self.root / self.PLAN
        path.write_bytes(path.read_bytes().replace(b"- Effort: medium", b"- Effort: high"))
        self.refuses("receipt plan does not match its findings file")

    def test_a_loop_that_follows_the_whole_schedule_stamps(self):
        self.live(surface=True)
        self.loop(["P2", "outage", "P2", "self", "P1", "P3", "P2", "P2", "P2", "clean"], surface=True)
        self.tick_work()
        receipt = self.root / self.stamp("D00 T01 §1", self.sha, self.reviews, ["true -> ok"], "2026-10-24")
        self.assertTrue(tg.graph(self.root)["D00 T01 §1"].done)
        rounds = tg.review_dispatch().read_rounds((self.root / self.LOOP).read_bytes().decode("utf-8"))
        self.assertEqual([(r["effort"], r["lenses"]) for r in rounds], [
            ("high", self.ROUND_1), ("medium", ["adversarial"]), ("medium", ["adversarial"]),
            ("low", ["adversarial"]), ("low", ["adversarial"]), ("medium", ["adversarial"]), ("low", ["quality"]),
            ("low", ["design"]), ("low", ["optimization"]), ("low", ["record"])])
        self.assertEqual(json.loads(receipt.read_text(encoding="utf-8"))["rounds_spent"], 10)


class Repository(unittest.TestCase):
    def test_the_authoring_template_copy_is_identical(self):
        # todo/README.md and create-todo name todo/templates/TODO-template.md and each agent's skill copy as the same
        # file: Antigravity (.agents), Claude Code (.claude) and Codex (.codex) all write here.
        canonical = (tg.ROOT / "todo/templates/TODO-template.md").read_bytes()
        for kit in (".claude", ".agents", ".codex"):
            with self.subTest(kit=kit):
                self.assertEqual((tg.ROOT / kit / "skills/create-todo/todo-template.md").read_bytes(), canonical)

    def test_the_stamp_command_reads_completion_as_findings_file_and_verdict(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location("todo_graph_cli", REPO / "scripts/todo-graph.py")
        cli = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cli)
        self.assertEqual(cli.completion_review("docs/reviews/00-workspace/D00-T01-s1.md:filed"),
                         {"report": "docs/reviews/00-workspace/D00-T01-s1.md", "verdict": "filed"})
        for bad in ("docs/reviews/x.md", "docs/reviews/x.md:", ":complete"):
            with self.subTest(bad=bad), self.assertRaises(tg.Refused):
                cli.completion_review(bad)

    def test_the_repository_graph_validates_and_its_plan_is_current(self):
        r = subprocess.run([sys.executable, str(REPO / "scripts/todo-graph.py"), "plan", "--check"],
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)


if __name__ == "__main__":
    unittest.main()

