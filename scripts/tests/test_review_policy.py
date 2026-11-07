"""Current policy behavior, independent of historical dispatcher fixtures."""
import copy
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


def entry(kind='round', clean=False):
    review = 'HIGHEST: none\nOVERALL: approve' if clean else 'HIGHEST: P2\nOVERALL: needs-attention'
    if kind == 'plan':
        review = 'HIGHEST: none\nPLAN: complete\nOVERALL: approve'
    if kind == 'completion':
        review = 'COMPLETION: complete'
    return dict(writer='codex', writer_model='gpt-6-astra', reviewer='argon',
                cli='agy', model='gemini-4-argon-medium', review=review,
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
        d.check_sequence(self.schedule, kind, found, 'codex', 'gpt-6-astra')

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
        pin = dispatch.load_pin()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / name
            plan = entry('plan')
            plan.update(attempt_reviewer='argon', attempt_model=plan['model'], attempt_cli='agy', failover=[], effort='medium', lenses=['plan'])
            dispatch.record_completion(path, plan, '2026-11-06T00:00:00Z', 'b'*40, 'plan')
            for clean in (clean_last,):  # one adversarial round, at the pin's effort for it
                e = entry('round', clean=clean)
                e.update(attempt_reviewer='argon', attempt_model=e['model'], attempt_cli='agy', failover=[], effort='medium', lenses=['adversarial'])
                dispatch.record(path, e, '2026-11-06T00:00:01Z')
            done = entry('completion')
            done['review'] = 'COMPLETION: gaps' if verdict != 'complete' else 'COMPLETION: complete'
            done.update(attempt_reviewer='argon', attempt_model=done['model'], attempt_cli='agy', failover=[], effort='medium', lenses=[])
            dispatch.record_completion(path, done, '2026-11-06T00:00:02Z', 'b'*40)
            data = dict(schedule=pin['schedule'], policy='a'*40, round_cap=dispatch.schedule_cap(pin['schedule']),
                        plan=dict(report=name), completion=dict(report=name, verdict=verdict, candidate='b'*40),
                        reviews=[dict(report=name)])
            with patch.object(dispatch, 'load_pin', return_value=pin), \
                    patch.object(dispatch, 'record_policy', return_value=(pin, 'a'*40)):
                fixtures.tg.schedule_ok(Path(folder), data, 'D00 T01 §1', False)

    def test_cycles_one_and_two_refuse_filed(self):
        for name in ('D00-T01-s1.md', 'D00-T01-s1-cycle2.md'):
            with self.subTest(name), self.assertRaisesRegex(fixtures.tg.Refused, 'before a third review cycle'):
                self.verdict_ok(name, 'filed')

    def test_the_third_cycle_on_accepts_filed(self):
        for name in ('D00-T01-s1-cycle3.md', 'D00-T01-s1-cycle12.md'):
            with self.subTest(name):
                self.verdict_ok(name, 'filed')

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
        pin = d.load_pin()
        found = dict(plans=[], rounds=[], completions=[])
        for index, kind in enumerate(('plan', 'round', 'round')):
            e = entry(kind, clean=True)
            e.update(_line=index, attempt_reviewer='argon', attempt_model=e['model'],
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
        pin = dispatch.load_pin()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'review.md'
            for kind in ('plan', 'round'):
                e = entry(kind, clean=True)
                e.update(attempt_reviewer='argon', attempt_model=e['model'], attempt_cli='agy',
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
        pin = d.load_pin()
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'review.md'
            for kind in ('plan', 'round', 'completion'):
                e = entry(kind, clean=True)
                e.update(attempt_reviewer='argon', attempt_model=e['model'], attempt_cli='agy',
                         failover=[], effort='medium', lenses=['plan'] if kind == 'plan' else
                         ['adversarial'] if kind == 'round' else [])
                if kind == 'round':
                    d.record(path, e, '2026-11-06T00:00:00Z')
                else:
                    d.record_completion(path, e, '2026-11-06T00:00:00Z', 'b'*40, kind)
            found = d.read_findings(path.read_text())
            d.validate_current_record(pin, found)
            self.assertEqual(found['plans'][0]['writer_model'], 'gpt-6-astra')
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
            e = entry('plan')
            e.update(policy=policy_sha, attempt_reviewer='argon', attempt_model=e['model'], attempt_cli='agy',
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
            path.write_text(text.replace('Attempt model: gemini-4-argon-medium',
                                         'Attempt model: gemini-4-argon-low'), encoding='utf-8')
            with self.assertRaises(fixtures.tg.Refused):
                fixtures.tg.graph(t.root)
        finally:
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
            d.check_sequence(policy(), 'completion', found, 'codex', 'gpt-6-astra')
        found['rounds'] = [entry() for _ in range(9)]
        for kind in ('round', 'completion'):
            with self.assertRaises(d.Refused):
                d.check_sequence(policy(), kind, found, 'codex', 'gpt-6-astra')
        found['rounds'][-1] = entry(clean=True)
        d.check_sequence(policy(), 'completion', found, 'codex', 'gpt-6-astra')
        with self.assertRaises(d.Refused):
            d.check_sequence(policy(), 'round', found, 'codex', 'gpt-6-astra')

    def test_missing_or_changed_writer_and_plan_refused(self):
        found = dict(plans=[entry('plan')], rounds=[], completions=[])
        for model in (None, 'different-model'):
            with self.assertRaises(d.Refused):
                d.check_sequence(policy(), 'round', found, 'codex', model)
        found['plans'] = []
        with self.assertRaises(d.Refused):
            d.check_sequence(policy(), 'round', found, 'codex', 'gpt-6-astra')

    def test_failed_attempt_does_not_spend_round(self):
        rounds = [entry(), dict(entry(), reviewer=None, execution='timeout')]
        step = d.scheduled(policy(), 'round', rounds, False)
        self.assertEqual((step['position'], step['effort']), (2, 'low'))

    def test_clean_is_explicit_and_not_contradictory(self):
        self.assertTrue(d.clean('HIGHEST: none\nOVERALL: approve'))
        self.assertFalse(d.clean('OVERALL: approve'))
        self.assertFalse(d.clean('HIGHEST: P1\nHIGHEST: none\nOVERALL: approve'))

    def test_variants_and_recorded_missing_execution(self):
        pin = d.load_pin()
        self.assertEqual(pin['routes']['codex'], ['argon'])
        for writer in ('claude', 'gemini', 'muse', 'meta'):
            self.assertEqual(pin['routes'][writer], ['astra'])
        with patch.object(d, 'find_cli', return_value=None):
            result = d.review(pin, 'codex', ROOT / 'docs/plan-review-prompt.md', 1,
                              dict(effort='low', lenses=[], position=2, escalated=False))
        self.assertEqual(result['attempt_model'], 'gemini-4-argon-low')
        self.assertEqual(result['execution'], 'missing')
        self.assertIsNone(result['reviewer'])

    def test_invocation_selects_low_variant_and_records_simulated_result(self):
        pin = d.load_pin()
        seen = []
        def run(argv, stdin, timeout, watched):
            seen.append(argv)
            watched[0].write_text('HIGHEST: none\nOVERALL: approve', encoding='utf-8')
            return 0, None
        with patch.object(d, 'find_cli', return_value='agy.exe'), patch.object(d, 'run', side_effect=run):
            result = d.review(pin, 'codex', ROOT / 'docs/plan-review-prompt.md', 1,
                              dict(effort='low', lenses=[], position=2, escalated=False))
        self.assertEqual(seen[0][seen[0].index('--model')+1], 'gemini-4-argon-low')
        self.assertEqual(result['model'], 'gemini-4-argon-low')
        self.assertEqual(result['execution'], 'returned')


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
