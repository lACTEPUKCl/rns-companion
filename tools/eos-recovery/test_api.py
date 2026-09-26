import unittest
from api import enqueue, snapshot


class ApiTests(unittest.TestCase):
    def setUp(self):
        self.cfg={'targets':{'mod-4':{'name':'WZ'}}}
        self.state={'mod-4':{'presence':False,'checked_at':1000,'players':0}}

    def test_reject_healthy_stale_unknown_and_blocked(self):
        self.assertEqual(enqueue(self.cfg,self.state,'other','owner',1001)[0],404)
        for field,value in [('presence',True),('checked_at',100),('blocked',True)]:
            state={'mod-4':{**self.state['mod-4'],field:value}}
            self.assertEqual(enqueue(self.cfg,state,'mod-4','owner',1001)[0],409)
            self.assertNotIn('manual_request',state['mod-4'])

    def test_occupied_server_can_request_recovery(self):
        self.state['mod-4']['players'] = 100
        self.assertTrue(snapshot(self.cfg,self.state,1001)['servers'][0]['canRequest'])
        self.assertEqual(enqueue(self.cfg,self.state,'mod-4','owner',1001)[0],202)

    def test_queue_once_and_preserve_verified_history(self):
        self.state['mod-4']['last_recovered_at']=900
        self.assertEqual(enqueue(self.cfg,self.state,'mod-4','owner',1001)[0],202)
        self.assertEqual(enqueue(self.cfg,self.state,'mod-4','owner',1002)[0],409)
        row=snapshot(self.cfg,self.state,1002)['servers'][0]
        self.assertTrue(row['pending'])
        self.assertEqual(row['lastRecoveredAt'],900)
        self.assertEqual(row['lastRun']['result'],'queued')
        self.assertNotIn('actor',row['lastRun'])

if __name__=='__main__':unittest.main()
