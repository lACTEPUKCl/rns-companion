"""Pure recovery policy shared by controller and tests."""
COOLDOWN = 5 * 60



def observe(state, present, now):
    if present is None:
        state['misses'] = 0
        state.pop('sample', None)
        state['last_poll'] = now
        return 'unavailable'
    if present:
        state['misses'] = 0
        state.pop('sample', None)
        state['last_poll'] = now
        if state.get('pending'):
            state['pending'] = False
            return 'recovered'
        return 'present'
    previous = state.get('last_poll', 0)
    if previous and now - previous < 55:
        return 'too_soon'
    if not previous or now - previous > 150:
        state['misses'] = 0
        state.pop('sample', None)
    state['last_poll'] = now
    state['misses'] = state.get('misses', 0) + 1
    if state.get('pending'):
        if now - state['last_attempt'] >= 300:
            state['pending'] = False
            state['blocked'] = True
            return 'failed'
        return 'verifying'
    if state.get('blocked'):
        return 'blocked'
    if state.get('last_attempt') and now - state['last_attempt'] < COOLDOWN:
        return 'cooldown'
    return 'candidate' if state['misses'] >= 5 else 'missing'


def stable_candidates(old, new):
    if not old or old['identity'] != new['identity']:
        return []
    known = {(x['fd'], x['inode']) for x in old['candidates']}
    return [x for x in new['candidates'] if (x['fd'], x['inode']) in known][:16]
