#!/usr/bin/env python3
"""One tick, fresh EOS evidence; systemd timer schedules the next tick."""
import fcntl
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.parse
import urllib.request

from policy import observe, stable_candidates

BASE = Path('/var/lib/rns-eos-recovery')


def save(path, value):
    temp = path.with_suffix('.tmp')
    temp.write_text(json.dumps(value, indent=2))
    os.chmod(temp, 0o600)
    with temp.open('r') as f:
        os.fsync(f.fileno())
    os.replace(temp, path)


def log(server, status, **extra):
    print(json.dumps(dict(server=server, status=status, **extra)), flush=True)


def catalog():
    values = {}
    for line in Path('/opt/squad-lobby/app/.env').read_text().splitlines():
        if '=' in line and not line.startswith('#'):
            key, value = line.split('=', 1)
            values[key] = value.strip().strip('\"\'')
    path = '/matchmaking/v1/5dee4062a90b42cd98fcad618b6636c2/filter'
    url = 'http://127.0.0.1:3509/debug-fetch?p=' + urllib.parse.quote(path, safe='')
    request = urllib.request.Request(url, data=b'{"criteria":[],"maxResults":5000}',
                                    headers={'X-API-Key': values['API_KEY'], 'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=30) as response:
        sessions = json.load(response)['sessions']
    if not isinstance(sessions, list) or len(sessions) < 100:
        raise RuntimeError('EOS catalog incomplete')
    names = [s.get('attributes', {}).get('SERVERNAME_s', '').strip() for s in sessions]
    return names


def remote(cfg, target, payload):
    args = ['ssh', '-i', cfg['key'], '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes',
            '-o', 'StrictHostKeyChecking=yes', '-o', 'UserKnownHostsFile=' + cfg['known_hosts'],
            '-o', 'ConnectTimeout=8', '-o', 'ServerAliveInterval=10',
            '-o', 'ServerAliveCountMax=2', 'root@' + target['host'], 'eos-recovery']
    result = subprocess.run(args, input=json.dumps(payload), text=True, capture_output=True, timeout=45)
    if result.returncode:
        raise RuntimeError('Host check/action failed')
    value = json.loads(result.stdout)
    if value.get('ok') is False:
        raise RuntimeError('Host refused action')
    return value


def run(config_path, dry_run=False):
    cfg = json.loads(Path(config_path).read_text())
    BASE.mkdir(mode=0o700, exist_ok=True)
    with (BASE / 'controller.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        path = BASE / 'controller.json'
        states = json.loads(path.read_text()) if path.exists() else {}
        try:
            names = catalog()
        except Exception as error:
            for state in states.values():
                observe(state, None, time.time())
                state['presence'] = None
                if state.get('pending') and time.time() - state['last_attempt'] >= 300:
                    state.update(pending=False, blocked=True)
                    state.get('last_run', {}).update(result='unconfirmed', finished_at=time.time())
            if not dry_run:
                save(path, states)
            log('all', 'catalog_unavailable', error=type(error).__name__)
            return 1
        for key, target in cfg['targets'].items():
            now = time.time()
            state = states.setdefault(key, {})
            present = target['name'].strip() in names
            state['presence'] = present
            state['checked_at'] = now
            if state.get('manual_request') and now - state['manual_request']['requested_at'] > 600:
                state.pop('manual_request')
                state.get('last_run', {}).update(result='expired', finished_at=now)
            try:
                if present:
                    status = observe(state, True, now)
                    if status == 'recovered':
                        state['last_recovered_at'] = now
                        state.get('last_run', {}).update(result='recovered', finished_at=now)
                    if state.pop('manual_request', None):
                        state.get('last_run', {}).update(result='already_present', finished_at=now)
                else:
                    sample = remote(cfg, target, {'server': key, 'action': 'inspect'})
                    if state.get('identity') != sample['identity']:
                        history = {k: state[k] for k in ('last_run', 'last_recovered_at', 'manual_request') if k in state}
                        state.clear()
                        state.update(history, presence=False, checked_at=now)
                        state['identity'] = sample['identity']
                    status = observe(state, False, now)
                    state['players'] = sample.get('players')
                    state['eligible'] = sample['eligible']
                    if status == 'failed':
                        state.get('last_run', {}).update(result='failed', finished_at=now)
                    if sample['eligible'] and status in ('missing', 'candidate'):
                        old = state.get('sample')
                        candidates = stable_candidates(old, sample)
                        if len(candidates) != 16:
                            state['sample'] = sample
                            state['sample_at'] = now
                        elif (status == 'candidate' or state.get('manual_request')) and now - state['sample_at'] >= 120:
                            if dry_run:
                                status = 'would_recover'
                            else:
                                # Persist before SSH: timeout/unknown outcome must never retry.
                                state.update(blocked=True, last_attempt=now,
                                             attempts=state.get('attempts', 0) + 1)
                                manual = state.pop('manual_request', None)
                                state['last_run'] = {**(manual or {'requested_at': now}), 'started_at': now,
                                                     'source': 'manual' if manual else 'automatic', 'result': 'running'}
                                save(path, states)
                                log(key, 'attempt_started', identity=sample['identity'])
                                remote(cfg, target, {'server': key, 'action': 'recover',
                                                     'identity': sample['identity'], 'candidates': candidates})
                                state.update(blocked=False, pending=True)
                                state['last_run']['result'] = 'verifying'
                                status = 'verifying'
                    elif status in ('missing', 'candidate'):
                        state.pop('sample', None)
                        status = 'ineligible'
                if status != state.get('status') or dry_run:
                    log(key, status, misses=state.get('misses', 0))
                state['status'] = status
            except Exception as error:
                observe(state, None, now)
                if state.get('last_run', {}).get('result') == 'running':
                    state['last_run'].update(result='unconfirmed', finished_at=now)
                if state.get('pending') and now - state['last_attempt'] >= 300:
                    state.update(pending=False, blocked=True)
                    state.get('last_run', {}).update(result='unconfirmed', finished_at=now)
                log(key, 'check_or_action_failed', error=type(error).__name__)
            if not dry_run:
                save(path, states)
        return 0


if __name__ == '__main__':
    os.umask(0o077)
    sys.exit(run(sys.argv[1], '--dry-run' in sys.argv[2:]))
