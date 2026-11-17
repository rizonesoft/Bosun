"""Current policy behavior, independent of historical dispatcher fixtures."""
import copy
import subprocess
import json
import importlib.util
from pathlib import Path
import unittest
import tempfile
import sys
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))  # so test_todo_graph imports however the suite is run
spec = importlib.util.spec_from_file_location('policy_dispatch', ROOT / 'scripts/review-dispatch.py')
d = importlib.util.module_from_spec(spec)
spec.loader.exec_module(d)

import test_todo_graph as fixtures  # noqa: E402  (after the sys.path line above)


def policy():
    return {
        'completion_order': 'last', 'max_low_rounds': 8, 'severity_stop_after': 0,
        'plan': {'effort': 'medium', 'lenses': ['plan']},
        'completion': {'effort': 'medium'},
        'rounds': [{'effort': 'medium', 'lenses': ['adversarial']}],
        'rotation': {'effort': 'low', 'lenses': ['quality', 'record']},
        'escalation': {'effort': 'low', 'lenses': ['adversarial']},
    }


def historical_pin():
    """Keep effort-variant lineage tests independent of the live reviewer selection."""
    pin = d.load_pin()
    pin['routes']['codex'] = ['carbon']
    pin['reviewers']['carbon'] = dict(cli='agy', model='gemini-4-carbon-medium',
        models_by_effort=dict(low='gemini-4-carbon-low', medium='gemini-4-carbon-medium'))
    pin['schedule'] = {**policy(), 'max_low_rounds': 32, 'max_repair_cycles': 33}
    return pin


def entry(kind='round', clean=False):
    review = 'HIGHEST: none\nOVERALL: approve' if clean else 'HIGHEST: P2\nOVERALL: needs-attention'
    if kind == 'plan':
        review = 'HIGHEST: none\nPLAN: complete\nOVERALL: approve'
    if kind == 'completion':
        review = 'COMPLETION: complete'
    return dict(writer='codex', writer_model='gpt-6.2-astra', reviewer='carbon',
                cli='agy', model='gemini-4-carbon-medium', review=review,
                policy='a'*40, execution='returned', candidate='b'*40)


class LivePinCaps(unittest.TestCase):
    """The whole limit on the real pin, through the dispatcher's own sequence check: 33 rounds in a cycle."""

    def setUp(self):
        self.schedule = d.load_pin()['schedule']

    def found(self, rounds, last_clean=False):
        entries = [entry('round') for _ in range(rounds)]
        if last_clean:
            entries[-1] = entry('round', clean=True)
        return dict(plans=[entry('plan')], rounds=entries, completions=[])

    def check(self, kind, found):
        d.check_sequence(self.schedule, kind, found, 'codex', 'gpt-6.2-astra')

    def test_the_pin_gives_thirty_three(self):
        self.assertEqual(d.schedule_cap(self.schedule), 33)
        self.assertEqual(self.schedule['max_repair_cycles'], 33)

    def test_rounds_ten_to_thirty_three_run(self):
        for spent in range(9, 33):  # the round about to run is spent + 1
            with self.subTest(round=spent + 1):
                self.check('round', self.found(spent))

    def test_round_thirty_four_is_refused(self):
        with self.assertRaisesRegex(d.Refused, 'budget exhausted') as caught:
            self.check('round', self.found(33))
        message = str(caught.exception)
        self.assertIn('todo-graph.py repair', message)
        self.assertIn('park', message)
        self.assertNotIn('operator', message)

    def test_a_clean_round_stops_early(self):
        with self.assertRaisesRegex(d.Refused, 'rounds are clean'):
            self.check('round', self.found(2, last_clean=True))
        self.check('completion', self.found(2, last_clean=True))

    def test_completion_follows_a_clean_round_thirty_three(self):
        self.check('completion', self.found(33, last_clean=True))

    def test_an_unresolved_round_thirty_three_has_no_completion(self):
        with self.assertRaisesRegex(d.Refused, 'exhaustion is not approval'):
            self.check('completion', self.found(33))


class FiledLeftovers(unittest.TestCase):
    """The completion verdict a stamp accepts, by cycle, through the graph's own schedule check."""

    def verdict_ok(self, name, verdict, clean_last=True):
        dispatch = fixtures.tg.review_dispatch()
        pin = historical_pin()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / name
            plan = entry('plan')
            plan.update(attempt_reviewer='carbon', attempt_model=plan['model'], attempt_cli='agy', failover=[], effort='medium', lenses=['plan'])
            dispatch.record_completion(path, plan, '2026-11-06T00:00:00Z', 'b'*40, 'plan')
            for clean in (clean_last,):  # one adversarial round, at the pin's effort for it
                e = entry('round', clean=clean)
                e.update(attempt_reviewer='carbon', attempt_model=e['model'], attempt_cli='agy', failover=[], effort='medium', lenses=['adversarial'])
                dispatch.record(path, e, '2026-11-06T00:00:01Z')
            done = entry('completion')
            done['review'] = 'COMPLETION: gaps' if verdict != 'complete' else 'COMPLETION: complete'
            done.update(attempt_reviewer='carbon', attempt_model=done['model'], attempt_cli='agy', failover=[], effort='medium', lenses=[])
            dispatch.record_completion(path, done, '2026-11-06T00:00:02Z', 'b'*40)
            data = dict(schedule=pin['schedule'], policy='a'*40, round_cap=dispatch.schedule_cap(pin['schedule']),
                        plan=dict(report=name), completion=dict(report=name, verdict=verdict, candidate='b'*40),
                        reviews=[dict(report=name)])
            with patch.object(dispatch, 'load_pin', return_value=pin), \
                    patch.object(dispatch, 'record_policy', return_value=(pin, 'a'*40)):
                fixtures.tg.schedule_ok(Path(folder), data, 'D00 T01 §1', False)

    def test_every_cycle_accepts_filed_and_completed(self):
        for name in ('D00-T01-s1.md', 'D00-T01-s1-cycle2.md', 'D00-T01-s1-cycle12.md'):
            for verdict in ('filed', 'completed'):
                with self.subTest(name=name, verdict=verdict):
                    self.verdict_ok(name, verdict)

    def test_an_unknown_verdict_is_refused(self):
        with self.assertRaises(fixtures.tg.Refused):
            self.verdict_ok('D00-T01-s1.md', 'polished')

    def test_complete_is_accepted_in_any_cycle(self):
        for name in ('D00-T01-s1.md', 'D00-T01-s1-cycle2.md', 'D00-T01-s1-cycle3.md'):
            with self.subTest(name):
                self.verdict_ok(name, 'complete')

    def test_a_filed_stamp_still_needs_a_clean_last_round(self):
        with self.assertRaises(fixtures.tg.Refused):
            self.verdict_ok('D00-T01-s1-cycle3.md', 'filed', clean_last=False)


class CurrentPolicy(unittest.TestCase):
    def test_malformed_lineage_refuses_without_traceback(self):
        with self.assertRaisesRegex(d.Refused, 'malformed'):
            d.record_policy(d.load_pin(), None)

    def test_failed_medium_attempt_replays_at_medium(self):
        pin = historical_pin()
        found = dict(plans=[], rounds=[], completions=[])
        for index, kind in enumerate(('plan', 'round', 'round')):
            e = entry(kind, clean=True)
            e.update(_line=index, attempt_reviewer='carbon', attempt_model=e['model'],
                     attempt_cli='agy', effort='medium',
                     lenses=['plan'] if kind == 'plan' else ['adversarial'])
            if index == 1:
                e.update(reviewer=None, execution='timeout', review='No review.')
            found['plans' if kind == 'plan' else 'rounds'].append(e)
        d.validate_current_record(pin, found)
        self.assertEqual(d.scheduled(pin['schedule'], 'round', found['rounds'][:1], False)['effort'],
                         'medium')

    def test_missing_terminal_completion_refuses_without_traceback(self):
        import test_todo_graph as fixtures
        dispatch = fixtures.tg.review_dispatch()
        pin = historical_pin()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'review.md'
            for kind in ('plan', 'round'):
                e = entry(kind, clean=True)
                e.update(attempt_reviewer='carbon', attempt_model=e['model'], attempt_cli='agy',
                         failover=[], effort='medium', lenses=['plan'] if kind == 'plan' else ['adversarial'])
                if kind == 'plan':
                    dispatch.record_completion(path, e, '2026-11-06T00:00:00Z', 'b'*40, kind)
                else:
                    dispatch.record(path, e, '2026-11-06T00:00:01Z')
            data = dict(schedule=pin['schedule'], policy='a'*40, round_cap=dispatch.schedule_cap(pin['schedule']),
                        plan=dict(report='review.md'), completion=dict(report='review.md', verdict='complete'),
                        reviews=[dict(report='review.md')])
            with patch.object(dispatch, 'load_pin', return_value=pin), \
                    patch.object(dispatch, 'record_policy', return_value=(pin, 'a'*40)):
                with self.assertRaisesRegex(fixtures.tg.Refused, 'terminal completion'):
                    fixtures.tg.schedule_ok(Path(folder), data, 'D00 T01 §1', False)

    def test_record_roundtrip_and_order_replay(self):
        pin = historical_pin()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'review.md'
            for kind in ('plan', 'round', 'completion'):
                e = entry(kind, clean=True)
                e.update(attempt_reviewer='carbon', attempt_model=e['model'], attempt_cli='agy',
                         failover=[], effort='medium', lenses=['plan'] if kind == 'plan' else
                         ['adversarial'] if kind == 'round' else [])
                if kind == 'round':
                    d.record(path, e, '2026-11-06T00:00:00Z')
                else:
                    d.record_completion(path, e, '2026-11-06T00:00:00Z', 'b'*40, kind)
            found = d.read_findings(path.read_text())
            d.validate_current_record(pin, found)
            self.assertEqual(found['plans'][0]['writer_model'], 'gpt-6.2-astra')
            found['completions'][0]['_line'] = 0
            with self.assertRaises(d.Refused):
                d.validate_current_record(pin, found)

    def test_historical_inflight_policy_and_new_receipt(self):
        import test_todo_graph as fixtures
        t = fixtures.Stamp('runTest')
        t.setUp()
        try:
            original = t.start
            (t.root / '.conclave/reviewers.toml').write_bytes((ROOT / '.conclave/reviewers.toml').read_bytes())
            t.commit('policy change')
            found = d.read_findings((t.root / t.PLAN).read_text())
            old, sha = d.record_policy(d.load_pin(), found, t.root)
            self.assertEqual(sha, original)
            self.assertNotEqual(old['schedule'].get('completion_order'), 'last')
            # Appended policy metadata anchors the old pin without changing its schedule.
            resumed = dict(found['rounds'][0], policy=sha, failover=[])
            d.record(t.root / t.PLAN, resumed, '2026-11-06T00:00:00Z')
            recovered, recovered_sha = d.record_policy(d.load_pin(),
                d.read_findings((t.root / t.PLAN).read_text(encoding='utf-8')), t.root)
            self.assertEqual(recovered_sha, original)
            self.assertEqual(recovered, old)
            # Build a new cycle in the same isolated repository, with real committed policy ancestry.
            path = t.root / t.PLAN
            path.write_text('')
            policy_sha = t.head()
            e = dict(entry('plan'), writer='claude', writer_model='claude-opus-5-5', reviewer='astra', cli='codex', model='gpt-6.2-astra')
            e.update(policy=policy_sha, attempt_reviewer='astra', attempt_model=e['model'], attempt_cli='codex',
                     failover=[], effort='medium', lenses=['plan'])
            d.record_completion(path, e, '2026-11-06T00:00:00Z', policy_sha, 'plan')
            for todo in (t.root / 'todo').rglob('TODO-01-*.md'):
                todo.write_text(todo.read_text().replace(fixtures.STARTED.strip(), '> **Started:** 2026-11-06T00:00:01Z')
                                .replace('- [ ] Work.', '- [x] Work.'))
            candidate = t.commit('new build start')
            e.update(review='HIGHEST: none\nOVERALL: approve', lenses=['adversarial'])
            d.record(path, e, '2026-11-06T00:00:02Z')
            e.update(review='COMPLETION: complete', lenses=[])
            d.record_completion(path, e, '2026-11-06T00:00:03Z', candidate, 'completion')
            receipt = t.stamp('D00 T01 §1', candidate, t.reviews, ['real fixture gate'], '2026-11-06',
                              completion={'report': t.PLAN, 'verdict': 'complete'})
            self.assertTrue((t.root / receipt).exists())
            fixtures.tg.graph(t.root)
            # A receipt cannot hide a failed terminal execution or a different model.
            text = path.read_text(encoding='utf-8')
            path.write_text(text.replace('Attempt model: gpt-6.2-astra',
                                         'Attempt model: gemini-4-carbon-low'), encoding='utf-8')
            with self.assertRaises(fixtures.tg.Refused):
                fixtures.tg.graph(t.root)
        finally:
            t.tearDown()

    def filed_through_stamp(self, repairs, verdict='filed', writer='claude'):
        """A real current-policy cycle after `repairs` repairs: committed live pin, real repair lines, dispatcher
        records, completion gaps filed to the open D00 T01 §2, then the real stamp. Returns (fixture, receipt)."""
        t = fixtures.Stamp('runTest')
        t.setUp()
        todo = next((t.root / 'todo').rglob('TODO-01-*.md'))
        lines = ''.join(f'> **Repair cycle:** {k}: 2026-11-06: gaps\n\n' for k in range(1, repairs + 1))
        text = todo.read_text(encoding='utf-8')
        at = text.index(fixtures.STARTED.strip())
        before, after = text[:at] + lines, text[at + len(fixtures.STARTED.strip()):]
        todo.write_text(before + after, encoding='utf-8')
        (t.root / '.conclave/reviewers.toml').write_bytes((ROOT / '.conclave/reviewers.toml').read_bytes())
        policy_sha = t.commit('live pin and repairs')
        rel = (f'docs/reviews/00-workspace/D00-T01-s1-cycle{repairs + 1}.md' if repairs
               else 'docs/reviews/00-workspace/D00-T01-s1.md')
        path = t.root / rel
        path.write_text('', encoding='utf-8')  # this cycle's own records only, under the live pin
        pin = d.load_pin()
        name = pin['routes'][writer][0]
        reviewer = pin['reviewers'][name]
        e = dict(entry('plan'), writer=writer, writer_model='gpt-6' if writer == 'codex' else 'claude-opus-5-5',
                 reviewer=name, cli=reviewer['cli'], model=reviewer['model'])
        e.update(policy=policy_sha, attempt_reviewer=name, attempt_model=e['model'], attempt_cli=e['cli'],
                 failover=[], effort=reviewer.get('effort'), lenses=['plan'])
        d.record_completion(path, e, '2026-11-06T00:00:00Z', policy_sha, 'plan')
        todo.write_text((before + '> **Started:** 2026-11-06T00:00:01Z' + after).replace('- [ ] Work.', '- [x] Work.'),
                        encoding='utf-8')
        candidate = t.commit('build start')
        e.update(review='HIGHEST: none\nOVERALL: approve', lenses=['adversarial'])
        d.record(path, e, '2026-11-06T00:00:02Z')
        e.update(review='1. A gap.\n\nCOMPLETION: gaps', lenses=[])
        d.record_completion(path, e, '2026-11-06T00:00:03Z', candidate, 'completion')
        if verdict == 'filed':
            with open(path, 'a', encoding='utf-8', newline='') as handle:
                handle.write('\n- Filed: D00 T01 §2\n')  # a writer's note, after the record's counted lines
        else:  # completed: the gap is fixed in this cycle, in a commit after the review, and nothing is re-reviewed
            (t.root / 'fix.txt').write_text('the gap, fixed' + '\n', encoding='utf-8')
            candidate = t.commit('fix the completion gap')
        receipt = t.stamp('D00 T01 §1', candidate, [{'round': 1, 'report': rel, 'verdict': 'clean'}], ['real gate'],
                          '2026-11-06', plan=rel, completion={'report': rel, 'verdict': verdict})
        return t, receipt

    def test_astra_default_effort_stamps_and_rejects_wrong_effort(self):
        t, receipt = self.filed_through_stamp(0, writer='claude')
        try:
            data = json.loads((t.root / receipt).read_text())
            self.assertEqual(data['plan']['effort'], 'medium')
            path = t.root / data['plan']['report']
            path.write_text(path.read_text().replace('- Effort: medium', '- Effort: low'))
            with self.assertRaises(fixtures.tg.Refused):
                fixtures.tg.schedule_ok(t.root, data, 'D00 T01 §1', False)
        finally:
            t.tearDown()

    def test_a_third_cycle_files_and_stamps_through_the_real_path(self):
        t, receipt = None, None
        try:
            t, receipt = self.filed_through_stamp(2)
            data = json.loads((t.root / receipt).read_text(encoding='utf-8'))
            self.assertEqual(data['completion']['filed'], ['D00 T01 §2'])
            self.assertEqual(data['schedule'].get('completion_order'), 'last')  # the current policy
            tool = t.root / 'scripts'
            (tool / 'lib').mkdir(parents=True, exist_ok=True)
            for name in ('todo-graph.py', 'review-dispatch.py', 'lib/todo_graph.py', 'lib/host_caps.py'):
                (tool / name).write_bytes((ROOT / 'scripts' / name).read_bytes())
            done = subprocess.run([sys.executable, str(tool / 'todo-graph.py'), 'validate'], cwd=t.root,
                                  capture_output=True, text=True)
            self.assertEqual(done.returncode, 0, done.stderr + done.stdout)
            rows = fixtures.tg.graph(t.root)
            self.assertTrue(rows['D00 T01 §1'].done)
            self.assertFalse(rows['D00 T01 §2'].done)
        finally:
            if t is not None:
                t.tearDown()

    def test_a_first_cycle_fixes_and_stamps_completed_on_the_same_path(self):
        t = None
        try:
            t, receipt = self.filed_through_stamp(0, 'completed')
            data = json.loads((t.root / receipt).read_text(encoding='utf-8'))
            self.assertEqual(data['completion']['verdict'], 'completed')
            self.assertEqual(data['completion']['filed'], [])
            tool = t.root / 'scripts'
            (tool / 'lib').mkdir(parents=True, exist_ok=True)
            for name in ('todo-graph.py', 'review-dispatch.py', 'lib/todo_graph.py', 'lib/host_caps.py'):
                (tool / name).write_bytes((ROOT / 'scripts' / name).read_bytes())
            done = subprocess.run([sys.executable, str(tool / 'todo-graph.py'), 'validate'], cwd=t.root,
                                  capture_output=True, text=True)
            self.assertEqual(done.returncode, 0, done.stderr + done.stdout)
            self.assertTrue(fixtures.tg.graph(t.root)['D00 T01 §1'].done)
        finally:
            if t is not None:
                t.tearDown()

    def test_a_first_cycle_files_and_stamps_on_the_same_path(self):
        t = None
        try:
            t, receipt = self.filed_through_stamp(0)
            self.assertEqual(json.loads((t.root / receipt).read_text(encoding='utf-8'))['completion']['filed'], ['D00 T01 §2'])
            self.assertTrue(fixtures.tg.graph(t.root)['D00 T01 §1'].done)
        finally:
            if t is not None:
                t.tearDown()

    def test_default_and_invalid_caps(self):
        s = policy()
        del s['max_low_rounds']
        d.check_schedule(s, {'agy'})
        self.assertEqual(d.schedule_cap(s), 9)
        for invalid in (True, -1, 33, '8'):
            s['max_low_rounds'] = invalid
            with self.assertRaises(d.Refused):
                d.check_schedule(s, {'agy'})

    def test_sequence_clean_and_exhaustion(self):
        found = dict(plans=[entry('plan')], rounds=[], completions=[])
        with self.assertRaises(d.Refused):
            d.check_sequence(policy(), 'completion', found, 'codex', 'gpt-6.2-astra')
        found['rounds'] = [entry() for _ in range(9)]
        for kind in ('round', 'completion'):
            with self.assertRaises(d.Refused):
                d.check_sequence(policy(), kind, found, 'codex', 'gpt-6.2-astra')
        found['rounds'][-1] = entry(clean=True)
        d.check_sequence(policy(), 'completion', found, 'codex', 'gpt-6.2-astra')
        with self.assertRaises(d.Refused):
            d.check_sequence(policy(), 'round', found, 'codex', 'gpt-6.2-astra')

    def test_missing_or_changed_writer_and_plan_refused(self):
        found = dict(plans=[entry('plan')], rounds=[], completions=[])
        for model in (None, 'different-model'):
            with self.assertRaises(d.Refused):
                d.check_sequence(policy(), 'round', found, 'codex', model)
        found['plans'] = []
        with self.assertRaises(d.Refused):
            d.check_sequence(policy(), 'round', found, 'codex', 'gpt-6.2-astra')

    def test_failed_attempt_does_not_spend_round(self):
        rounds = [entry(), dict(entry(), reviewer=None, execution='timeout')]
        step = d.scheduled(policy(), 'round', rounds, False)
        self.assertEqual((step['position'], step['effort']), (2, 'low'))

    def test_clean_is_explicit_and_not_contradictory(self):
        self.assertTrue(d.clean('HIGHEST: none\nOVERALL: approve'))
        self.assertFalse(d.clean('OVERALL: approve'))
        self.assertFalse(d.clean('HIGHEST: P1\nHIGHEST: none\nOVERALL: approve'))

    def test_retired_variant_review_is_refused_even_if_cli_missing(self):
        with patch.object(d, 'find_cli', return_value=None) as find:
            with self.assertRaises(d.Refused):
                d.review(historical_pin(), 'codex', ROOT / 'docs/plan-review-prompt.md', 1,
                         dict(effort='low', lenses=[], position=2, escalated=False))
        find.assert_not_called()


class WriterReviewerRoles(unittest.TestCase):
    def test_only_two_writers_and_codex_reviewer(self):
        pin = d.load_pin()
        self.assertEqual(d.WRITERS, ('claude', 'gemini'))
        self.assertEqual(pin['routes'], {'claude': ['astra'], 'gemini': ['astra']})
        self.assertEqual({r['cli'] for r in pin['reviewers'].values()}, {'codex'})

    def test_both_writers_keep_medium_initial_and_low_followups(self):
        pin = d.load_pin()
        for writer in d.WRITERS:
            for kind, prior in [('plan', []), ('round', []), ('round', [entry()]), ('completion', [])]:
                step = d.scheduled(pin['schedule'], kind, prior, False)
                def run(argv, stdin, timeout, watched, stderr_path=None):
                    self.assertIn('model_reasoning_effort=' + step['effort'], argv)
                    watched[1].write_text('HIGHEST: none\nOVERALL: approve')
                    return 0, None
                with patch.object(d, 'find_cli', return_value='codex.exe'), patch.object(d, 'run', side_effect=run):
                    result = d.review(pin, writer, ROOT / 'docs/plan-review-prompt.md', 1, step)
                self.assertEqual(result['execution'], 'returned')
                self.assertEqual(result['model'], 'gpt-6.2-astra')
                self.assertEqual(result['effort'], 'low' if prior else 'medium')
                self.assertEqual(result['lenses'], step['lenses'])
        self.assertEqual(d.schedule_cap(pin['schedule']), 33)

    def test_historical_codex_route_remains_readable(self):
        old = (ROOT / '.conclave/reviewers.toml').read_text()
        old = old.replace('[routes]', '[reviewers.carbon]\ncli = \"agy\"\nmodel = \"gemini-pro-orchestrator\"\n\n[routes]\ncodex = [\"carbon\"]')
        pin = d.load_pin(data=old.encode())
        self.assertEqual(pin['routes']['codex'], ['carbon'])
        self.assertEqual(pin['reviewers']['carbon']['model'], 'gemini-pro-orchestrator')

    def test_retired_agy_reviewer_cannot_launch_from_old_policy(self):
        pin = historical_pin()
        pin['routes']['claude'] = ['carbon']
        with patch.object(d, 'find_cli') as find, patch.object(d, 'run') as run:
            with self.assertRaisesRegex(d.Refused, 'Codex is the only reviewer'):
                d.review(pin, 'claude', ROOT / 'docs/plan-review-prompt.md', 1)
        find.assert_not_called()
        run.assert_not_called()


class ClaudeNeverReviews(unittest.TestCase):
    SONNET = '[reviewers.sonnet]\ncli = "claude"\nmodel = "claude-sonnet-5-5"\neffort = "high"\n\n'

    def pin_text(self, routed):
        text = (ROOT / '.conclave/reviewers.toml').read_text(encoding='utf-8')
        if routed:
            self.assertEqual(text.count('gemini = ["astra"]'), 1)
            text = text.replace('gemini = ["astra"]', 'gemini = ["sonnet"]')
        head, tail = text.split('[routes]', 1)
        return head + self.SONNET + '[routes]' + tail

    def test_the_committed_pin_names_no_claude_reviewer(self):
        self.assertNotIn('claude', {r['cli'] for r in d.load_pin()['reviewers'].values()})

    def test_a_live_pin_with_a_claude_reviewer_refuses_routed_or_not(self):
        for routed in (True, False):
            with self.subTest(routed=routed), tempfile.TemporaryDirectory() as folder:
                path = Path(folder) / 'reviewers.toml'
                path.write_text(self.pin_text(routed), encoding='utf-8')
                with self.assertRaisesRegex(d.Refused, 'sonnet.*Claude is never a reviewer'):
                    d.load_pin(path)

    def test_a_past_pin_naming_claude_still_loads_for_replay(self):
        pin = d.load_pin(data=self.pin_text(True).encode('utf-8'))
        self.assertEqual(pin['reviewers']['sonnet']['cli'], 'claude')

    def test_a_replayed_claude_route_launches_nothing(self):
        pin = d.load_pin(data=self.pin_text(True).encode('utf-8'))
        with patch.object(d, 'find_cli', return_value='claude.exe') as found, patch.object(d, 'run') as run:
            with self.assertRaisesRegex(d.Refused, 'Claude is never a reviewer'):
                d.review(pin, 'gemini', ROOT / 'docs/plan-review-prompt.md', 1)
        found.assert_not_called()
        run.assert_not_called()

    def test_claude_can_still_write(self):
        self.assertEqual(d.load_pin()['routes']['claude'], ['astra'])


if __name__ == '__main__':
    unittest.main()
