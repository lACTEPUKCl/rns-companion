import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import controller


class ControllerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.config = self.base / 'config.json'
        self.config.write_text(json.dumps({'targets': {'mod-4': {'name': 'WZ', 'host': 'test'}}}))
        self.clock = 1000
        self.calls = []
        self.sample = {'identity': {'pid': 123, 'start': '1', 'boot': 'test'}, 'eligible': True, 'players': 100,
                       'candidates': [{'fd': i, 'inode': str(i)} for i in range(32, 64)]}

    def tearDown(self):
        self.temp.cleanup()

    def remote(self, cfg, target, req):
        self.calls.append(req)
        if req['action'] == 'inspect':
            return self.sample
        return {'ok': True}

    def tick(self, present=False, dry=False, remote=None):
        with patch.object(controller, 'BASE', self.base), \
             patch.object(controller, 'catalog', return_value=['WZ'] if present else ['other']), \
             patch.object(controller, 'remote', side_effect=remote or self.remote), \
             patch.object(controller.time, 'time', return_value=self.clock), \
             contextlib.redirect_stdout(io.StringIO()):
            result = controller.run(str(self.config), dry)
        self.clock += 60
        return result

    def test_recovers_again_after_success_and_new_outage(self):
        for _ in range(5):
            self.tick()
        self.assertEqual(sum(x['action'] == 'recover' for x in self.calls), 1)
        self.tick(present=True)
        for _ in range(6):
            self.tick()
        self.assertEqual(sum(x['action'] == 'recover' for x in self.calls), 2)

    def test_ambiguous_ssh_outcome_is_latched(self):
        def failing(cfg, target, req):
            if req['action'] == 'recover':
                raise TimeoutError()
            return self.remote(cfg, target, req)
        for _ in range(5):
            self.tick(remote=failing)
        state = json.loads((self.base / 'controller.json').read_text())['mod-4']
        self.assertTrue(state['blocked'])
        for _ in range(6):
            self.tick()
        self.assertFalse(any(x['action'] == 'recover' for x in self.calls))

    def test_dry_run_never_mutates_or_sends_recovery(self):
        for _ in range(4):
            self.tick()
        before = (self.base / 'controller.json').read_bytes()
        self.tick(dry=True)
        self.assertEqual(before, (self.base / 'controller.json').read_bytes())
        self.assertFalse(any(x['action'] == 'recover' for x in self.calls))

    def test_ineligible_server_never_recovered(self):
        self.sample['eligible'] = False
        for _ in range(7):
            self.tick()
        self.assertFalse(any(x['action'] == 'recover' for x in self.calls))

    def test_catalog_error_resets_streak(self):
        for _ in range(4):
            self.tick()
        with patch.object(controller, 'BASE', self.base), \
             patch.object(controller, 'catalog', side_effect=TimeoutError()), \
             patch.object(controller.time, 'time', return_value=self.clock), \
             contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(controller.run(str(self.config)), 1)
        self.clock += 60
        self.tick()
        state = json.loads((self.base / 'controller.json').read_text())['mod-4']
        self.assertEqual(state['misses'], 1)
        self.assertFalse(any(x['action'] == 'recover' for x in self.calls))

    def test_manual_waits_for_stable_sockets_but_not_five_misses(self):
        self.tick()
        path=self.base/'controller.json'
        states=json.loads(path.read_text())
        states['mod-4']['manual_request']={'requested_at':self.clock,'actor':'owner'}
        path.write_text(json.dumps(states))
        self.tick()
        self.assertFalse(any(x['action']=='recover' for x in self.calls))
        self.tick()
        self.assertEqual(sum(x['action']=='recover' for x in self.calls),1)
        self.tick(present=True)
        s=json.loads(path.read_text())['mod-4']
        self.assertEqual(s['last_run']['source'],'manual')
        self.assertEqual(s['last_run']['result'],'recovered')
        self.assertEqual(s['last_recovered_at'],self.clock-60)

    def test_manual_cancelled_if_server_returns_without_action(self):
        self.tick()
        path=self.base/'controller.json'
        states=json.loads(path.read_text())
        states['mod-4'].update(manual_request={'requested_at':1000},last_run={'result':'queued'})
        path.write_text(json.dumps(states))
        self.tick(present=True)
        s=json.loads(path.read_text())['mod-4']
        self.assertEqual(s['last_run']['result'],'already_present')
        self.assertNotIn('last_recovered_at',s)
        self.assertFalse(any(x['action']=='recover' for x in self.calls))


if __name__ == '__main__':
    unittest.main()
