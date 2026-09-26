#!/usr/bin/env python3
"""Private recovery status/queue API; reachable only through SSH forwarding."""
import fcntl
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import time

import controller
from policy import COOLDOWN, MAX_ATTEMPTS

CONFIG = Path('/etc/rns-eos-recovery/controller.json')


def reason(state, now):
    if state.get('manual_request') or state.get('pending'):
        return 'Восстановление уже запрошено'
    if state.get('blocked'):
        return 'Повторы заблокированы после неудачной попытки — нужна ручная проверка'
    if not 0 <= now - state.get('checked_at', 0) <= 150 or state.get('presence') is None:
        return 'Нет свежей проверки EOS'
    if state.get('presence') is not False:
        return 'Сервер уже в браузере'
    if state.get('attempts', 0) >= MAX_ATTEMPTS:
        return 'Достигнут лимит попыток для этого процесса'
    if state.get('last_attempt') and now - state['last_attempt'] < COOLDOWN:
        return 'Следующая попытка возможна через 6 часов после предыдущей'
    return None


def snapshot(config, states, now):
    servers = []
    for key, target in config['targets'].items():
        state = states.get(key, {})
        why = reason(state, now)
        run = state.get('last_run', {})
        servers.append({'key': key, 'name': target['name'], 'canRequest': why is None, 'reason': why,
                        'pending': bool(state.get('pending') or state.get('manual_request')),
                        'lastRecoveredAt': state.get('last_recovered_at'),
                        'lastRun': {k: run[k] for k in ('requested_at', 'started_at', 'finished_at', 'source', 'result') if k in run}})
    return {'servers': servers}


def enqueue(config, states, key, actor, now):
    if key not in config['targets']:
        return 404, {'error': 'Сервер не настроен для восстановления'}
    state = states.setdefault(key, {})
    why = reason(state, now)
    if why:
        return 409, {'error': why}
    state['manual_request'] = {'requested_at': now, 'actor': str(actor)[:80]}
    state['last_run'] = {**state['manual_request'], 'source': 'manual', 'result': 'queued'}
    return 202, {'ok': True, 'status': 'queued'}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def reply(self, code, data):
        body = json.dumps(data, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def authorized(self):
        provided = self.headers.get('X-API-Key', '')
        return bool(self.server.api_key) and hmac.compare_digest(provided.encode(), self.server.api_key.encode())

    def do_GET(self):
        if not self.authorized():
            return self.reply(401, {'error': 'Unauthorized'})
        if self.path != '/recovery':
            return self.reply(404, {'error': 'Not found'})
        try:
            config = json.loads(CONFIG.read_text())
            path = controller.BASE / 'controller.json'
            states = json.loads(path.read_text()) if path.exists() else {}
            return self.reply(200, snapshot(config, states, time.time()))
        except Exception:
            return self.reply(503, {'error': 'Состояние восстановления недоступно'})

    def do_POST(self):
        if not self.authorized():
            return self.reply(401, {'error': 'Unauthorized'})
        if self.path != '/recovery':
            return self.reply(404, {'error': 'Not found'})
        try:
            size = int(self.headers.get('Content-Length', '0'))
            if not 1 <= size <= 2048:
                return self.reply(400, {'error': 'Некорректный запрос'})
            self.connection.settimeout(5)
            body = json.loads(self.rfile.read(size))
            key = body.get('serverKey')
            if not isinstance(key, str):
                return self.reply(400, {'error': 'serverKey обязателен'})
            config = json.loads(CONFIG.read_text())
            with (controller.BASE / 'controller.lock').open('w') as lock:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                path = controller.BASE / 'controller.json'
                states = json.loads(path.read_text())
                code, result = enqueue(config, states, key, body.get('actor', ''), time.time())
                if code == 202:
                    controller.save(path, states)
                    controller.log(key, 'manual_request_queued', actor=str(body.get('actor', ''))[:80])
            return self.reply(code, result)
        except BlockingIOError:
            return self.reply(409, {'error': 'Идёт проверка серверов. Повторите через несколько секунд'})
        except (ValueError, TypeError, AttributeError):
            return self.reply(400, {'error': 'Некорректный запрос'})
        except Exception:
            return self.reply(503, {'error': 'Не удалось поставить восстановление в очередь'})


if __name__ == '__main__':
    os.umask(0o077)
    values = dict(line.split('=', 1) for line in Path('/opt/squad-lobby/app/.env').read_text().splitlines()
                  if '=' in line and not line.startswith('#'))
    key = values.get('API_KEY', '').strip().strip('\"\'')
    if not key:
        raise RuntimeError('Missing API key')
    server = ThreadingHTTPServer(('127.0.0.1', 3511), Handler)
    server.api_key = key
    server.serve_forever()
