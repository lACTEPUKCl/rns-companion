#!/usr/bin/env python3
"""Forced-command SSH endpoint. No shell commands, restarts or arbitrary targets."""
import datetime as dt
import fcntl
import json
import os
from pathlib import Path
import re
import signal
import socket
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.request

from icmp_recovery import inspect_socket, probe
from policy import COOLDOWN

ROOT = Path('/var/lib/rns-eos-recovery')
CONFIG = Path('/etc/rns-eos-recovery/targets.json')


def atomic(path, data):
    temp = path.with_suffix('.tmp')
    temp.write_text(json.dumps(data, indent=2))
    os.chmod(temp, 0o600)
    with temp.open('r') as f:
        os.fsync(f.fileno())
    os.replace(temp, path)


def command(args):
    return subprocess.check_output(args, timeout=10, text=True)


def identity(pid):
    stat = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
    return {'pid': pid, 'start': stat[19],
            'boot': Path('/proc/sys/kernel/random/boot_id').read_text().strip()}


def game_pid(container):
    rows = command(['docker', 'top', container, '-eo', 'pid']).splitlines()[1:]
    found = []
    for row in rows:
        pid = int(row.strip())
        try:
            exe = os.readlink(f'/proc/{pid}/exe')
        except FileNotFoundError:
            continue
        if exe.endswith('/SquadGame/Binaries/Linux/SquadGameServer'):
            found.append(pid)
    if len(found) != 1:
        raise RuntimeError('Expected exactly one Squad binary in allowlisted container')
    return found[0]


def recv_exact(sock, n):
    data = b''
    while len(data) < n:
        part = sock.recv(n - len(data))
        if not part:
            raise RuntimeError('RCON disconnected')
        data += part
    return data


def rcon_info(config):
    values = {}
    for line in Path(config).read_text().splitlines():
        if '=' in line and not line.strip().startswith('//'):
            key, value = line.split('=', 1)
            values[key.strip().lower()] = value.strip()
    def send(sock, rid, kind, text):
        payload = struct.pack('<ii', rid, kind) + text.encode() + b'\0\0'
        sock.sendall(struct.pack('<i', len(payload)) + payload)
    def receive(sock):
        size = struct.unpack('<i', recv_exact(sock, 4))[0]
        if not 10 <= size <= 65536:
            raise RuntimeError('Invalid RCON frame')
        raw = recv_exact(sock, size)
        return (*struct.unpack('<ii', raw[:8]), raw[8:-2].decode('utf-8', 'replace'))
    with socket.create_connection(('127.0.0.1', int(values['port'])), 5) as sock:
        send(sock, 711, 3, values['password'])
        for _ in range(4):
            rid, kind, _ = receive(sock)
            if rid == -1:
                raise RuntimeError('RCON auth failed')
            if rid == 711 and kind == 2:
                break
        else:
            raise RuntimeError('RCON auth response missing')
        send(sock, 712, 2, 'ShowServerInfo')
        body = ''
        for _ in range(8):
            rid, _, part = receive(sock)
            if rid != 712:
                continue
            body += part
            try:
                result = json.loads(body)
            except json.JSONDecodeError:
                continue
            players = result.get('PlayerCount_I')
            if isinstance(players, str) and re.fullmatch(r'\d{1,3}', players):
                players = int(players)
            if not isinstance(players, int) or players < 0:
                raise RuntimeError('RCON player count missing')
            for field in ('PublicQueue_I', 'ReservedQueue_I'):
                value = result.get(field)
                if not re.fullmatch(r'\d{1,3}', str(value)):
                    raise RuntimeError('RCON queue count missing')
                players += int(value)
            return players
    raise RuntimeError('RCON response incomplete')


def recent_errors(path, now):
    with open(path, 'rb') as f:
        f.seek(0, 2)
        f.seek(max(0, f.tell() - 2 * 1024 * 1024))
        lines = f.read().decode('utf-8', 'replace').splitlines()
    count = 0
    for line in lines:
        if 'UpdateSession: Failed with error EOS_NoConnection' not in line:
            continue
        match = re.match(r'\[(\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2})', line)
        if match:
            stamp = dt.datetime.strptime(match[1], '%Y.%m.%d-%H.%M.%S').replace(tzinfo=dt.timezone.utc).timestamp()
            if 0 <= now - stamp <= 180:
                count += 1
    return count


def inspect(target):
    pid = game_pid(target['container'])
    ident = identity(pid)
    fds = set(map(int, os.listdir(f'/proc/{pid}/fd')))
    candidates = []
    pf = os.pidfd_open(pid)
    try:
        for fd in sorted(fds & set(range(32, 512))):
            try:
                inode = inspect_socket(pf, pid, fd)
            except OSError:
                continue
            if inode:
                candidates.append({'fd': fd, 'inode': inode})
            if len(candidates) >= 64:
                break
    finally:
        os.close(pf)
    players = rcon_info(target['rcon'])
    errors = recent_errors(target['log'], time.time())
    if identity(pid) != ident:
        raise RuntimeError('Process changed during inspection')
    density = len(fds & set(range(32, 1024))) / 992
    return {'identity': ident, 'fd_count': len(fds), 'low_density': density,
            'players': players, 'errors': errors, 'candidates': candidates,
            'eligible': errors >= 2 and len(fds) >= 1024
                        and density >= .90 and len(candidates) >= 16}


def main():
    # SSH_ORIGINAL_COMMAND is never executed. Payload is bounded, JSON only.
    request = json.loads(sys.stdin.buffer.read(16385))
    key = request['server']
    targets = json.loads(CONFIG.read_text())
    if key not in targets or request['action'] not in ('inspect', 'recover'):
        raise RuntimeError('Target/action not allowed')
    ROOT.mkdir(mode=0o700, parents=True, exist_ok=True)
    with (ROOT / (key + '.lock')).open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        target = targets[key]
        sample = inspect(target)
        if request['action'] == 'inspect':
            print(json.dumps(sample))
            return
        if not sample['eligible'] or sample['identity'] != request['identity']:
            raise RuntimeError('Recovery eligibility/identity changed')
        # A host-wide routing/DNS/TLS outage is not a descriptor-recovery case.
        try:
            with urllib.request.urlopen('https://api.epicgames.dev/', timeout=8) as response:
                if response.status >= 500:
                    raise RuntimeError('Epic connectivity probe failed')
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise RuntimeError('Epic connectivity probe failed') from None
        candidates = request['candidates']
        allowed = {(x['fd'], x['inode']) for x in sample['candidates']}
        if len(candidates) != 16 or any((x['fd'], x['inode']) not in allowed for x in candidates):
            raise RuntimeError('Candidate validation failed')
        state_path = ROOT / (key + '.json')
        state = json.loads(state_path.read_text()) if state_path.exists() else {}
        if state.get('identity') != sample['identity']:
            state = {'identity': sample['identity'], 'attempts': 0}
        now = time.time()
        if state.get('blocked'):
            raise RuntimeError('Host recovery limit/lockout')
        if now - state.get('last_attempt', 0) < COOLDOWN:
            raise RuntimeError('Host recovery cooldown')
        state.update(blocked=True, attempts=state['attempts'] + 1, last_attempt=now)
        atomic(state_path, state)  # Fail closed even if SSH/controller disappears.
        pid = sample['identity']['pid']
        def check_identity():
            if identity(pid) != sample['identity']:
                raise RuntimeError('Process identity changed before intervention')
        def expired(*_):
            raise TimeoutError('Recovery stop-time limit exceeded')
        previous = signal.signal(signal.SIGALRM, expired)
        signal.alarm(10)
        try:
            report = ROOT / (key + '-' + str(int(now)) + '.json')
            result = probe(pid, 16, str(report), candidates, check_identity)
        finally:
            signal.alarm(0)
            signal.signal(signal.SIGALRM, previous)
        # Controller alone confirms EOS outcome; local cooldown still applies.
        state['blocked'] = False
        atomic(state_path, state)
        print(json.dumps({'ok': True, 'result': result}))


if __name__ == '__main__':
    os.umask(0o077)
    try:
        main()
    except Exception as error:
        # Never echo request/config, credentials or RCON payload.
        print(json.dumps({'ok': False, 'error': type(error).__name__}))
        sys.exit(1)
