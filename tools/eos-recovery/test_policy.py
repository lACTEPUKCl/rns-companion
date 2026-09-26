import unittest
from policy import observe, stable_candidates


class PolicyTests(unittest.TestCase):
    def test_requires_five_distinct_checks(self):
        s = {}
        for i in range(4):
            self.assertEqual(observe(s, False, 1000 + i * 60), 'missing')
        self.assertEqual(observe(s, False, 1240), 'candidate')
        self.assertEqual(observe(s, False, 1241), 'too_soon')
        self.assertEqual(s['misses'], 5)

    def test_unknown_and_gaps_reset_streak(self):
        for value in (None, True):
            s = {'misses': 4, 'last_poll': 1000, 'sample': {}}
            observe(s, value, 1060)
            self.assertEqual(s['misses'], 0)
            self.assertNotIn('sample', s)
        s = {'misses': 4, 'last_poll': 1000}
        self.assertEqual(observe(s, False, 1300), 'missing')

    def test_failed_attempt_is_latched(self):
        s = {'pending': True, 'last_attempt': 1000}
        self.assertEqual(observe(s, False, 1240), 'verifying')
        self.assertEqual(observe(s, False, 1300), 'failed')
        self.assertEqual(observe(s, False, 2000), 'blocked')
        observe(s, True, 2060)
        self.assertEqual(observe(s, False, 2120), 'blocked')

    def test_success_allows_next_incident_after_verification_window(self):
        s = {'pending': True, 'last_attempt': 1000, 'attempts': 1}
        self.assertEqual(observe(s, True, 1060), 'recovered')
        self.assertEqual(observe(s, False, 1120), 'cooldown')
        s['attempts'] = 3
        self.assertEqual(observe(s, False, 1360), 'missing')

    def test_reused_fd_and_new_process_rejected(self):
        old = {'identity': {'pid': 1}, 'candidates': [{'fd': 33, 'inode': 'old'}]}
        new = {'identity': {'pid': 1}, 'candidates': [{'fd': 33, 'inode': 'new'}]}
        self.assertEqual(stable_candidates(old, new), [])
        new['candidates'] = old['candidates']
        new['identity'] = {'pid': 2}
        self.assertEqual(stable_candidates(old, new), [])


if __name__ == '__main__':
    unittest.main()
