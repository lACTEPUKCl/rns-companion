"""Linux x86-64 recovery primitive. Called only after agent policy checks.

Backup references live in this helper, never in the target. They close on exit.
"""
import ctypes as C
import errno
import json
import os
import signal
import socket
import sys
import time

libc = C.CDLL(None, use_errno=True)
libc.ptrace.restype = C.c_long
class Regs(C.Structure):
    _fields_ = [(x, C.c_ulonglong) for x in
        'r15 r14 r13 r12 rbp rbx r11 r10 r9 r8 rax rcx rdx rsi rdi orig_rax rip cs eflags rsp ss fs_base gs_base ds es fs gs'.split()]

def ptrace(op, tid, addr=0, data=0):
    C.set_errno(0)
    result = libc.ptrace(op, tid, C.c_void_p(addr), data if not isinstance(data, int) else C.c_void_p(data))
    if result == -1 and C.get_errno():
        raise OSError(C.get_errno(), os.strerror(C.get_errno()))
    return result

def inspect_socket(pf, pid, fd):
    inode = os.readlink(f'/proc/{pid}/fd/{fd}')
    dup = libc.syscall(438, pf, fd, 0)
    if dup < 0:
        raise OSError(C.get_errno(), 'pidfd_getfd')
    with socket.socket(fileno=dup) as s:
        if (s.getsockopt(1, 39), s.getsockopt(1, 3), s.getsockopt(1, 38)) != (2, 2, 1):
            return None
        if s.getsockname() != ('0.0.0.0', 0):
            return None
        try:
            s.getpeername()
            return None
        except OSError as e:
            if e.errno != errno.ENOTCONN:
                raise
    return inode

def probe(pid, count, report, expected=None, identity_check=None):
    assert 1 <= count <= 16
    if 'TracerPid:\t0' not in open(f'/proc/{pid}/status').read():
        raise RuntimeError('Already traced')
    pf = os.pidfd_open(pid)
    candidates = []
    for fd in sorted(map(int, os.listdir(f'/proc/{pid}/fd'))):
        if fd < 32 or fd >= 512:
            continue
        try:
            inode = inspect_socket(pf, pid, fd)
        except OSError:
            continue
        if inode:
            candidates.append((fd, inode))
        if len(candidates) == count:
            break
    if len(candidates) != count:
        raise RuntimeError('Not enough eligible low ICMP descriptors')
    if expected is not None:
        candidates = [(int(x['fd']), x['inode']) for x in expected]
        if len(candidates) != count or len(set(fd for fd, _ in candidates)) != count:
            raise RuntimeError('Invalid candidate count')
        if any(fd < 32 or fd >= 512 for fd, _ in candidates):
            raise RuntimeError('Candidate outside allowed range')
    time.sleep(2)
    for fd, inode in candidates:
        if inspect_socket(pf, pid, fd) != inode:
            raise RuntimeError('Descriptor changed before intervention')
    gadget = None
    with open(f'/proc/{pid}/mem', 'rb', buffering=0) as mem:
        for line in open(f'/proc/{pid}/maps'):
            cols = line.split()
            if 'r-x' in cols[1] and cols[-1].endswith('/libc.so.6'):
                lo, hi = [int(x, 16) for x in cols[0].split('-')]
                mem.seek(lo)
                index = mem.read(hi-lo).find(b'\x0f\x05\xc3')
                if index >= 0:
                    gadget = lo + index
                    break
    if gadget is None:
        raise RuntimeError('No syscall/ret instruction found; no changes made')
    attached = []
    saved = None
    moved = []
    backups = []
    started = time.monotonic()
    try:
        for attempt in range(4):
            tids = list(map(int, os.listdir(f'/proc/{pid}/task')))
            for tid in tids:
                if tid in attached:
                    continue
                try:
                    ptrace(16, tid)
                except ProcessLookupError:
                    continue
                attached.append(tid)
                _, status = os.waitpid(tid, 0x40000000)
                if not os.WIFSTOPPED(status):
                    raise RuntimeError('Thread did not stop')
            if set(map(int, os.listdir(f'/proc/{pid}/task'))).issubset(attached):
                break
        else:
            raise RuntimeError('Could not stop all threads')
        saved = Regs()
        ptrace(12, pid, data=C.byref(saved))
        if identity_check is not None:
            identity_check()

        def syscall(number, a=0, b=0, c=0):
            regs = Regs.from_buffer_copy(saved)
            regs.rip = gadget
            regs.rax = number
            regs.orig_rax = 0xffffffffffffffff
            regs.rdi, regs.rsi, regs.rdx = a, b, c
            try:
                ptrace(13, pid, data=C.byref(regs))
                ptrace(9, pid)
                _, status = os.waitpid(pid, 0x40000000)
                if not os.WIFSTOPPED(status) or os.WSTOPSIG(status) != signal.SIGTRAP:
                    raise RuntimeError('Unexpected signal during syscall')
                ptrace(12, pid, data=C.byref(regs))
                result = C.c_longlong(regs.rax).value
                if result < 0:
                    raise OSError(-result, 'remote syscall')
                return result
            finally:
                ptrace(13, pid, data=C.byref(saved))

        for fd, inode in candidates:
            if inspect_socket(pf, pid, fd) != inode:
                raise RuntimeError('Stopped-process socket validation failed')
        # Retain references locally; no high-FD accumulation in the game process.
        for fd, inode in candidates:
            backup = libc.syscall(438, pf, fd, 0)
            if backup < 0:
                raise OSError(C.get_errno(), 'backup pidfd_getfd')
            backups.append(backup)
            moved.append({'fd': fd, 'inode': inode, 'released': False})
        for item in moved:
            syscall(3, item['fd'])
            item['released'] = True
    finally:
        if saved is not None:
            ptrace(13, pid, data=C.byref(saved))
        for tid in reversed(attached):
            try:
                ptrace(17, tid)
            except ProcessLookupError:
                pass
        os.close(pf)
        for backup in backups:
            os.close(backup)
        result = {'pid': pid, 'pauseSeconds': round(time.monotonic()-started, 3), 'descriptors': moved}
        with open(report, 'w') as f:
            json.dump(result, f, indent=2)
    return result

if __name__ == '__main__':
    if sys.argv[1] == '--self-test':
        import resource
        resource.setrlimit(resource.RLIMIT_NOFILE, (65535, resource.getrlimit(resource.RLIMIT_NOFILE)[1]))
        pid = os.fork()
        if pid == 0:
            import threading
            threading.Thread(target=lambda: time.sleep(120), daemon=True).start()
            keep = [open('/dev/null') for _ in range(40)]
            udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            udp.bind(('127.0.0.1', 0))
            sockets = [socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_ICMP) for _ in range(20)]
            while True:
                time.sleep(.02)
        try:
            time.sleep(.2)
            before = {fd: os.readlink(f'/proc/{pid}/fd/{fd}') for fd in os.listdir(f'/proc/{pid}/fd')}
            def reject_identity():
                raise RuntimeError('Simulated changed identity')
            try:
                probe(pid, 4, sys.argv[2], identity_check=reject_identity)
                raise AssertionError('Identity guard did not reject')
            except RuntimeError as error:
                assert str(error) == 'Simulated changed identity'
            assert 'TracerPid:\t0' in open(f'/proc/{pid}/status').read()
            assert before == {fd: os.readlink(f'/proc/{pid}/fd/{fd}') for fd in os.listdir(f'/proc/{pid}/fd')}
            probe(pid, 4, sys.argv[2])
            os.kill(pid, 0)
            assert 'TracerPid:\t0' in open(f'/proc/{pid}/status').read()
            d = json.load(open(sys.argv[2]))
            assert len(d['descriptors']) == 4 and all(x['released'] for x in d['descriptors'])
            for item in d['descriptors']:
                assert not os.path.exists(f'/proc/{pid}/fd/{item["fd"]}')
                assert item['inode'] not in [os.readlink(f'/proc/{pid}/fd/{fd}') for fd in os.listdir(f'/proc/{pid}/fd')]
            removed = {str(x['fd']) for x in d['descriptors']}
            after = {fd: os.readlink(f'/proc/{pid}/fd/{fd}') for fd in os.listdir(f'/proc/{pid}/fd')}
            assert after == {fd: value for fd, value in before.items() if fd not in removed}
            print('SELF TEST PASSED')
        finally:
            os.kill(pid, signal.SIGTERM)
            os.waitpid(pid, 0)
    else:
        probe(int(sys.argv[1]), int(sys.argv[2]), sys.argv[3])
