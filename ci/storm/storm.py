#!/usr/bin/env python3
"""storm.py - random-point signal storms against weir fixtures, with an oracle.

usage: python3 -I storm.py --bin WEIR --out WORKDIR [--fixture NAME[,NAME..]|all] [--runs N] [--jobs J]
                           [--kinds INT,TERM,HUP[,QUIT][,HANGUP]] [--count 1-3] [--gap 0-50] [--window MS]
                           [--seed S] [--timeout S] [--verbose] [--tty] [--group] [--keep]

Linux only (the orphan scan reads /proc). Each run: spawn `WEIR fx/NAME.weir LOG TAG RUNDIR PORT CLI`
(CLI = cli.py next to this file; PORT from the block $STORM_PORT0 (20000) .. +499, BELOW the
ephemeral range [D:harness-ports]: run % 480, a candidate something already listens on is skipped,
and a lost bind reruns the same draw on the next candidate) in a new session (default dispositions,
stdin /dev/null, TMPDIR=RUNDIR/tmp, cwd RUNDIR, RLIMIT_CORE raised to its hard limit), send COUNT
signals (kinds drawn from KINDS) to weir's pid alone (--group: to its process group, kill -- -pgid, so
its foreground children get them too), the first at U(0, WINDOW) ms after spawn (WINDOW defaults to the fixture's calibrated
unsignalled duration + 150ms), the rest GAP ms apart. --tty: weir leads a fresh 24x80 pty; INT is a
typed ^C and QUIT a typed ^\\ (both reach the whole foreground group), HANGUP closes the master (the
kernel's SIGHUP; nothing is sent after it, and half such runs give weir's stderr a file, not the pty, unless
the fixture feeds keys).
Then judge:
  rc      exit status vs the docs (R7: death by the first signal, -n, for one signal or several;
          0 or -n if DONE logged; HANGUP counts as HUP)
  core    no core dump (R2: a QUIT's death dumps none), unless the first signal was a QUIT sent before weir
          caught TERM (SigCgt: the process's first milliseconds, before .NET installs its handlers)
  once    each O/S/E line per scope path at most once; E after S after O
  nest    after `S p` (p's cleanup began) or `J p` (joined) no line of p's body or of a scope nested in p
  landed  no `L` line (an effect that must never run, e.g. a race loser past its cut)
  whole   one signal: every opened scope ran its cleanup start and end (`O p` => `S p`, `E p`)
  orphan  no process left in weir's session or carrying TAG in its argv (300ms after exit); a fixture
          line `// storm-allow-orphan: <argv prefix>` exempts a deliberate daemon (killed, not counted)
  tmp     RUNDIR/tmp empty after exit
  stderr  no internal error / .NET text / "command failed" / "One or more errors" on stderr
  hang    exit within TIMEOUT s of the last signal
  ttymode --tty, no hangup: the pty is cooked again after exit (icanon, echo, isig)
  cuterr  `// storm-cut-err: AFTER | UNLESS | TEXT`: one signal landed, a log line starts with AFTER and none
          with UNLESS => TEXT on stderr (unless a hangup took the pty weir's stderr wrote to)
  cpu     `// storm-cpu-max: S`: weir and the children it reaped used at most S s of CPU
A run whose only issues match an open finding (known_of; none is open) counts as `known`, not bad.
A fixture line `// storm-feed: TEXT MS` (--tty only; TEXT with backslash escapes) types TEXT every MS ms from
U(0, WINDOW + 1s) until weir exits (a reader at the terminal, e.g. a pager in a cleanup, gets its keys).
Log grammar written by fixtures: `O path` scope body began, `B path ...` body effect,
`S path` cleanup began, `E path` cleanup ended, `C path ...` cleanup effect, `J path` the fan-out/race at path joined (no line of
path/... may follow), `L ...` a line that must never be written, `DONE` script reached its end; `path` is
slash-separated scope nesting (outer/lock/arm1). Fixtures live in fx/ next to this file.
A fixture whose unsignalled calibration run fails (not DONE, or any issue) counts all its runs bad, unless --window
is given, which storms it anyway. Exit code: 0 if every run passed, 1 otherwise. Writes OUT/runs.jsonl (one record per run) and
OUT/summary.json (per fixture: bad/runs, failure categories, exit codes, latency); a failing run's
dir OUT/run-FX-R (log, err, tmp) is kept, a passing one removed unless --keep.
"""
import os, sys, time, json, random, signal, subprocess, argparse, shutil, threading, re
from concurrent.futures import ThreadPoolExecutor

PORT0 = int(os.environ.get('STORM_PORT0', '20000'))
if not 1024 <= PORT0 <= 32768 - 500:
    sys.exit('STORM_PORT0=%d: the 500-port block must sit below the ephemeral range (32768)' % PORT0)
if not sys.platform.startswith('linux'):
    sys.exit('storm.py is Linux only (the orphan scan reads /proc)')
HERE = os.path.dirname(os.path.abspath(__file__))
SIGS = {'INT': signal.SIGINT, 'TERM': signal.SIGTERM, 'HUP': signal.SIGHUP, 'QUIT': signal.SIGQUIT, 'HANGUP': signal.SIGHUP}
KEYS = {'INT': b'\x03', 'QUIT': b'\x1c'}
# open findings: a run whose only issues are one of these counts as `known`, not bad; add an entry here
# (returning its name) while a finding is open, delete it once its fix lands. None is open.
def known_of(a, sent, issues, err):
    return None
LOST_BIND = re.compile(r'cannot listen|Address already in use')
BAD_ERR = re.compile(r'internal error|Unhandled|Exception|   at |System\.|One or more errors|command failed|TaskScheduler', re.I)

def pre():
    import resource
    for s in (signal.SIGINT, signal.SIGQUIT, signal.SIGHUP, signal.SIGTERM, signal.SIGPIPE):
        signal.signal(s, signal.SIG_DFL)
    # a core dump must be possible for `core` to see one (runners default to 0)
    _, hard = resource.getrlimit(resource.RLIMIT_CORE)
    resource.setrlimit(resource.RLIMIT_CORE, (hard, hard))

def catches(pid, sig):
    # does pid catch sig yet (SigCgt)? before .NET installs its handlers (TERM's is one) a QUIT takes the
    # default action, a core: the process's first milliseconds, out of weir's reach
    try:
        for l in open('/proc/%d/status' % pid):
            if l.startswith('SigCgt:'): return bool(int(l.split()[1], 16) >> (sig - 1) & 1)
    except OSError: pass
    return False

def directives(fx):
    out = {}
    for l in open(os.path.join(HERE, 'fx', fx + '.weir')):
        if l.startswith('// storm-'):
            k, _, v = l[len('// storm-'):].partition(':')
            out.setdefault(k.strip(), []).append(v.strip())
    return out

def alive(pid):
    # peek, never reap: the final reap reads the core bit and the rusage
    try: return os.waitid(os.P_PID, pid, os.WEXITED | os.WNOHANG | os.WNOWAIT) is None
    except ChildProcessError: return False

def reap(p, timeout):
    """wait4 weir's pid: (rc, core, cpu seconds incl. reaped children); rc 'HANG' past timeout"""
    end = time.monotonic() + timeout if timeout is not None else None
    while True:
        pid, st, ru = os.wait4(p.pid, os.WNOHANG)
        if pid:
            p.returncode = os.waitstatus_to_exitcode(st)
            if hasattr(p, 'p'): p.p.returncode = p.returncode
            return p.returncode, os.WCOREDUMP(st), ru.ru_utime + ru.ru_stime
        if end is not None and time.monotonic() > end: return 'HANG', False, 0.0
        time.sleep(0.005)

def procs_of(sid, tag, me):
    out = []
    for d in os.listdir('/proc'):
        if not d.isdigit() or int(d) == me: continue
        try:
            st = open('/proc/%s/stat' % d).read()
            rest = st[st.rindex(')') + 2:].split()
            cl = open('/proc/%s/cmdline' % d, 'rb').read().replace(b'\0', b' ').decode(errors='replace')
        except Exception:
            continue
        if rest[0] == 'Z': continue
        if int(rest[3]) == sid or (tag in cl and 'storm.py' not in cl):
            out.append((int(d), rest[0], cl.strip()[:80]))
    return out

def judge(lines, rc, sent, nsig_before_exit, times=None):
    issues = []
    seen = {}
    started = set(); opened = set(); ended = set()
    cleanup_began = []  # paths whose S was logged, in order
    done = 'DONE' in lines
    for idx, l in enumerate(lines):
        if l == 'DONE':
            continue
        parts = l.split(' ', 2)
        if len(parts) < 2:
            issues.append('garbled:%r' % l); continue
        kind, path = parts[0], parts[1]
        if kind == 'L':
            issues.append('landed:%r' % l); continue
        if kind == 'C':
            if ('S', path) not in seen: issues.append('C-before-S:%r' % l)
            if ('E', path) in seen: issues.append('C-after-E:%r' % l)
        if kind in 'OSEJ':
            key = (kind, path)
            if key in seen: issues.append('once:%s %s x2' % (kind, path))
            seen[key] = idx
        # a signal may land between a scope's open and its body's first
        # statement (every evaluated node is a boundary [D:scope-tree]): the
        # scope's cleanup then runs with no O line; never O after S (nest)
        if kind == 'S' and ('O', path) not in seen and nsig_before_exit == 0: issues.append('S-before-O:%s' % path)
        if kind == 'E' and ('S', path) not in seen: issues.append('E-before-S:%s' % path)
        for p in cleanup_began:
            if (kind == 'B' and path == p) or (path.startswith(p + '/') and kind in 'OBSECJ'):
                issues.append('nest:%r after S %s' % (l, p)); break
        if kind in 'SJ': cleanup_began.append(path)
        if kind == 'O': opened.add(path)
        if kind == 'S': started.add(path)
        if kind == 'E': ended.add(path)
    if nsig_before_exit == 1:
        for p in sorted(opened):
            if p not in started: issues.append('whole:no S %s' % p)
            elif p not in ended: issues.append('whole:no E %s' % p)
    # rc [D:die-by-signal] (R7): after the unwind weir dies BY the first
    # signal, scoped or not, however many follow (the hard exit keeps the
    # first's code)
    # (signals sent within 5ms of the first arrive together: .NET runs their
    # handlers in no set order, so any of them may be the first weir sees)
    first = -SIGS[sent[0]] if sent else None
    firsts = {-SIGS[k] for k, t in zip(sent, times or [0] * len(sent)) if t - (times or [0])[0] < 5} | {first}
    if nsig_before_exit == 0:
        ok = rc == 0
    elif done:
        ok = rc == 0 or rc in firsts
    else:
        ok = rc in firsts
    if not ok: issues.append('rc=%s(sent %s)' % (rc, ','.join(sent[:max(1, nsig_before_exit)])))
    return issues

CLI = os.path.join(HERE, 'cli.py')

def listening(port):
    import socket
    s = socket.socket()
    s.settimeout(0.2)
    try: return s.connect_ex(('127.0.0.1', port)) == 0
    finally: s.close()

def free_port(off):
    # the first candidate from PORT0+off on that nothing listens on; a bind
    # still lost to a client socket is caught by the caller's rerun
    for i in range(500):
        p = PORT0 + (off + i) % 500
        if not listening(p): return p
    sys.exit('no free port in %d..%d' % (PORT0, PORT0 + 499))

def env_for(d):
    env = dict((k, v) for k, v in os.environ.items() if 'proxy' not in k.lower())
    env['TMPDIR'] = d + '/tmp'
    return env

def feed_of(fx):
    f = directives(fx).get('feed')
    if not f: return None
    text, _, ms = f[0].rpartition(' ')
    return text.encode().decode('unicode_escape').encode('latin-1'), float(ms) / 1000

def calibrate(a, binp, fx, base):
    d = os.path.join(base, 'calib-' + fx)
    shutil.rmtree(d, ignore_errors=True); os.makedirs(d + '/tmp')
    t0 = time.monotonic()
    argv = [binp, os.path.join(HERE, 'fx', fx + '.weir'), d + '/log', '9999999', d, str(free_port(498)), CLI]
    errf = open(d + '/err', 'wb')
    if a.tty:
        # at a terminal, as the runs are: a reader at the tty waits for its fed keys
        p = TtyProc(argv, env_for(d), errf, d)
        p.feed(feed_of(fx), 0)
    else:
        p = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=open(d + '/out', 'wb'), stderr=errf,
                             env=env_for(d), preexec_fn=pre, cwd=d)
    rc, _, _ = reap(p, 60)
    if rc == 'HANG':
        try: os.killpg(p.pid, signal.SIGKILL)
        except Exception: pass
        reap(p, None)
    ms = int((time.monotonic() - t0) * 1000)
    if a.tty: p.close()
    lines = open(d + '/log').read().splitlines() if os.path.exists(d + '/log') else []
    iss = judge(lines, rc, [], 0)
    if 'DONE' not in lines or iss:
        # a finding, not a driver error: the fixture counts all-bad unless --window lets it storm anyway
        print('  CALIBRATION FAIL %s: rc=%s issues=%s err=%r' % (fx, rc, iss, open(d + '/err', 'rb').read()[-300:]), flush=True)
        return None
    shutil.rmtree(d, ignore_errors=True)
    return ms

class TtyProc:
    """weir as the session leader of a fresh 24x80 pty (its group is the terminal's foreground group);
    key() types bytes, hangup() closes the master; everything weir writes to the terminal lands in errf
    (or, with stderr=FILE, its stderr goes to that file)"""
    def __init__(self, argv, env, errf, cwd, stderr=None):
        import fcntl, termios, struct
        m, sl = os.openpty()
        fcntl.ioctl(sl, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
        def pre_tty():
            pre()
            fcntl.ioctl(0, termios.TIOCSCTTY, 0)
        self.m = m; self.errf = errf; self.hung = False; self.stop = False
        self.p = subprocess.Popen(argv, stdin=sl, stdout=sl, stderr=stderr or sl, preexec_fn=pre_tty,
                                  start_new_session=True, env=env, cwd=cwd)
        os.close(sl)
        self.pid = self.p.pid
        self.t = threading.Thread(target=self.pump, daemon=True); self.t.start()
        self.wlock = threading.Lock()
    def pump(self):
        import select
        while not self.stop:
            r, _, _ = select.select([self.m], [], [], 0.02)
            if not r: continue
            try: c = os.read(self.m, 65536)
            except OSError: return
            if not c: return
            self.errf.write(c); self.errf.flush()
    def key(self, b):
        with self.wlock:
            if not self.hung: os.write(self.m, b)
    def feed(self, f, start):
        # type f's text every f's period from start (s after now) until weir exits or the line hangs up
        if not f: return
        def go():
            time.sleep(start)
            while alive(self.pid) and not self.hung:
                try: self.key(f[0])
                except OSError: return
                time.sleep(f[1])
        threading.Thread(target=go, daemon=True).start()
    def hangup(self):
        with self.wlock:
            self.hung = True; self.stop = True
            self.t.join(1)
            os.close(self.m)
    def termios_ok(self):
        import termios
        lf = termios.tcgetattr(self.m)[3]
        return [n for n, b in (('icanon', termios.ICANON), ('echo', termios.ECHO), ('isig', termios.ISIG)) if not lf & b]
    def close(self):
        self.stop = True
        self.t.join(1)
        if not self.hung:
            try: os.close(self.m)
            except OSError: pass

def one(a, binp, fx, window, r, base, rng):
    st = rng.getstate()
    for attempt in range(4):
        rng.setstate(st)
        rec = one_try(a, binp, fx, window, r, base, rng, (r + attempt * 97) % 480)
        if not LOST_BIND.search(rec['err']): break
    rec['attempts'] = attempt + 1
    return rec

def one_try(a, binp, fx, window, r, base, rng, poff):
    tag = '%d%05d' % (os.getpid() % 100, r)
    d = os.path.join(base, 'run-%s-%d' % (fx, r))
    shutil.rmtree(d, ignore_errors=True); os.makedirs(d + '/tmp')
    log = d + '/log'
    dirs = directives(fx)
    count = rng.randint(a.count[0], a.count[1])
    sent = [rng.choice(a.kinds) for _ in range(count)]
    t_first = rng.uniform(0, window)
    gaps = [rng.uniform(a.gap[0], a.gap[1]) for _ in range(count - 1)]
    t_feed = rng.uniform(0, window + 1000) / 1000.0
    # never for a tty reader: more reads its keys from fd 2, and a file there is an endless EOF
    err_file = a.tty and 'HANGUP' in a.kinds and rng.random() < 0.5 and not feed_of(fx)
    env = env_for(d)
    errf = open(d + '/err', 'wb')
    t0 = time.monotonic()
    argv = [binp, os.path.join(HERE, 'fx', fx + '.weir'), log, tag, d, str(free_port(poff)), CLI]
    if a.tty:
        p = TtyProc(argv, env, errf, d, stderr=open(d + '/err2', 'wb') if err_file else None)
        p.feed(feed_of(fx), t_feed)
    else:
        p = subprocess.Popen(argv, stdin=subprocess.DEVNULL, cwd=d,
                             stdout=open(d + '/out', 'wb'), stderr=errf, preexec_fn=pre, start_new_session=True, env=env)
    times = [t_first]
    for g in gaps: times.append(times[-1] + g)
    n_before = 0; t_last = None; early_quit = False
    for k, t in zip(sent, times):
        dt = t0 + t / 1000.0 - time.monotonic()
        if dt > 0: time.sleep(dt)
        if not alive(p.pid): break
        if k == 'QUIT' and n_before == 0: early_quit = not catches(p.pid, signal.SIGTERM)
        try:
            if a.tty and k == 'HANGUP': p.hangup()
            elif a.tty and k in KEYS: p.key(KEYS[k])
            elif a.group: os.killpg(p.pid, SIGS[k])
            else: os.kill(p.pid, SIGS[k])
            n_before += 1; t_last = time.monotonic()
        except ProcessLookupError:
            break
        if k == 'HANGUP': break
    rc, core, cpu = reap(p, a.timeout if t_last else a.timeout + window / 1000.0)
    hang = rc == 'HANG'
    t_exit = time.monotonic()
    lat = int((t_exit - t_last) * 1000) if t_last else None
    if hang:
        # the hung session's processes, for the artifact
        with open(d + '/ps', 'w') as f:
            subprocess.run(['ps', '-o', 'pid,ppid,pgid,sid,tpgid,stat,wchan:20,args', '-s', str(p.pid)], stdout=f, stderr=f)
            for pid, _, _ in procs_of(p.pid, tag, os.getpid()):
                try:
                    fds = {n: os.readlink('/proc/%d/fd/%s' % (pid, n)) for n in sorted(os.listdir('/proc/%d/fd' % pid))[:8]}
                    f.write('%d fds=%s syscall=%s\n' % (pid, fds, open('/proc/%d/syscall' % pid).read().strip()))
                except OSError:
                    pass
        try: os.killpg(p.pid, signal.SIGKILL)
        except Exception: pass
        rc, core, cpu = reap(p, None); rc = 'HANG'
    time.sleep(0.3)
    tty_bad = p.termios_ok() if a.tty and not p.hung and not hang else []
    if a.tty: p.close()
    left = procs_of(p.pid, tag, os.getpid())
    allow = dirs.get('allow-orphan', [])
    for pid, _, cl in left:
        if any(cl.startswith(x) for x in allow):
            try: os.kill(pid, signal.SIGKILL)
            except Exception: pass
    left = [x for x in left if not any(x[2].startswith(y) for y in allow)]
    for pid, _, _ in left:
        try: os.kill(pid, signal.SIGKILL)
        except Exception: pass
    lines = open(log).read().splitlines() if os.path.exists(log) else []
    issues = [] if hang else judge(lines, rc, sent, n_before, times)
    if hang: issues.append('HANG')
    if core and not early_quit: issues.append('core:dumped')
    for c in os.listdir(d):
        if c.startswith('core'): os.remove(os.path.join(d, c))
    if left: issues.append('orphan:%d %s' % (len(left), [x[1:] for x in left][:3]))
    leak = os.listdir(d + '/tmp')
    if leak: issues.append('tmp:%s' % leak[:3])
    if tty_bad: issues.append('ttymode:%s off' % ','.join(tty_bad))
    err = open(d + '/err', 'rb').read().decode(errors='replace')
    if err_file: err += open(d + '/err2', 'rb').read().decode(errors='replace')
    if BAD_ERR.search(err): issues.append('stderr:%r' % err.strip()[:200])
    err_seen = not a.tty or err_file or not p.hung  # a hung-up pty's later output is lost
    for ce in dirs.get('cut-err', []):
        after, unless, text = (x.strip() for x in ce.split('|', 2))
        if (n_before == 1 and not hang and err_seen and any(l.startswith(after) for l in lines)
                and not any(l.startswith(unless) for l in lines) and text not in err):
            issues.append('cuterr:no %r' % text)
    for cm in dirs.get('cpu-max', []):
        if cpu > float(cm): issues.append('cpu:%.2fs' % cpu)
    known = known_of(a, sent[:n_before], issues, err)
    if known: issues = []
    rec = dict(fx=fx, run=r, sent=sent[:max(n_before, 1)], n=n_before, known=known, at=[int(x) for x in times[:max(n_before, 1)]],
               rc=rc, lat=lat, cpu=round(cpu, 2), early_quit=early_quit, issues=issues, tail=lines[-6:], err=err.strip()[:300])
    if not issues and not a.keep:
        shutil.rmtree(d, ignore_errors=True)
    return rec

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--bin', required=True)
    ap.add_argument('--fixture', default='all')
    ap.add_argument('--runs', type=int, default=50)
    ap.add_argument('--jobs', type=int, default=3)
    ap.add_argument('--kinds', default='INT,TERM,HUP')
    ap.add_argument('--count', default='1-3')
    ap.add_argument('--gap', default='0-50')
    ap.add_argument('--window', type=int, default=0)
    ap.add_argument('--seed', type=int, default=None)
    ap.add_argument('--timeout', type=float, default=20)
    ap.add_argument('--out', required=True, help='work dir: runs.jsonl, summary.json, failing run dirs')
    ap.add_argument('--keep', action='store_true', help='keep passing run dirs')
    ap.add_argument('--verbose', action='store_true')
    ap.add_argument('--tty', action='store_true', help='weir leads a pty session; INT/QUIT are a typed ^C/^\\ (they reach the whole foreground group), HANGUP closes the master, TERM/HUP go to the pid')
    ap.add_argument('--group', action='store_true', help='send TERM/HUP (and INT/QUIT without --tty) to weir\'s process group, not its pid')
    a = ap.parse_args()
    a.kinds = a.kinds.split(',')
    a.out = os.path.abspath(a.out)
    bad = [k for k in a.kinds if k not in SIGS or (k == 'HANGUP' and not a.tty)]
    if bad: sys.exit('--kinds: %s (HANGUP needs --tty)' % bad)
    a.count = [int(x) for x in a.count.split('-')] if '-' in a.count else [int(a.count)] * 2
    a.gap = [float(x) for x in a.gap.split('-')] if '-' in a.gap else [float(a.gap)] * 2
    fxs = sorted(f[:-5] for f in os.listdir(os.path.join(HERE, 'fx')) if f.endswith('.weir')) if a.fixture == 'all' else a.fixture.split(',')
    seed = a.seed if a.seed is not None else random.randrange(1 << 30)
    os.makedirs(a.out, exist_ok=True)
    jl = open(os.path.join(a.out, 'runs.jsonl'), 'a')
    print('seed %d' % seed, flush=True)
    total_bad = 0; summary = []; t_all = time.monotonic()
    for fx in fxs:
        t_fx = time.monotonic()
        cal = calibrate(a, a.bin, fx, a.out)
        if cal is None and not a.window:
            print('%s: bad %d/%d calibration failed' % (fx, a.runs, a.runs), flush=True)
            total_bad += a.runs
            summary.append(dict(fx=fx, bad=a.runs, runs=a.runs, cats={'calib': 1}))
            continue
        window = a.window or cal + 150
        cal = cal or 0
        rngs = [random.Random('%d-%s-%d' % (seed, fx, r)) for r in range(a.runs)]
        with ThreadPoolExecutor(a.jobs) as ex:
            recs = list(ex.map(lambda r: one(a, a.bin, fx, window, r, a.out, rngs[r]), range(a.runs)))
        cats = {}; rcs = {}; lats = []
        bad = 0; known = 0
        for rec in recs:
            known += bool(rec['known'])
            jl.write(json.dumps(rec) + '\n')
            rcs[str(rec['rc'])] = rcs.get(str(rec['rc']), 0) + 1
            if rec['lat'] is not None: lats.append(rec['lat'])
            if rec['issues']:
                bad += 1
                for i in rec['issues']:
                    c = i.split(':')[0].split('=')[0]
                    cats[c] = cats.get(c, 0) + 1
                print('  FAIL %s run %d sent=%s at=%s rc=%s lat=%s %s | tail=%s' % (fx, rec['run'], rec['sent'], rec['at'], rec['rc'], rec['lat'], rec['issues'], rec['tail']), flush=True)
            elif a.verbose:
                print('  ok %s run %d sent=%s at=%s rc=%s lat=%s' % (fx, rec['run'], rec['sent'], rec['at'], rec['rc'], rec['lat']))
        lats.sort()
        pct = lambda q: lats[min(len(lats) - 1, int(q * len(lats)))] if lats else None
        print('%s: bad %d/%d calib=%dms window=%dms cats=%s rcs=%s lat p50=%s p95=%s max=%s%s' % (
            fx, bad, a.runs, cal, window, cats, rcs, pct(.5), pct(.95), lats[-1] if lats else None,
            ' known=%d' % known if known else ''), flush=True)
        total_bad += bad
        summary.append(dict(fx=fx, bad=bad, runs=a.runs, known=known, calib_ms=cal, window_ms=window, cats=cats, rcs=rcs,
                            lat_p50=pct(.5), lat_p95=pct(.95), lat_max=lats[-1] if lats else None,
                            secs=round(time.monotonic() - t_fx, 1)))
    secs = round(time.monotonic() - t_all, 1)
    with open(os.path.join(a.out, 'summary.json'), 'w') as f:
        json.dump(dict(seed=seed, count=a.count, tty=a.tty, group=a.group, kinds=a.kinds, jobs=a.jobs, bad=total_bad,
                       runs=a.runs * len(fxs), secs=secs, fixtures=summary), f, indent=1)
    print('total: bad %d/%d in %.1fs' % (total_bad, a.runs * len(fxs), secs), flush=True)
    sys.exit(1 if total_bad else 0)

if __name__ == '__main__':
    main()
