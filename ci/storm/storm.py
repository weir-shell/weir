#!/usr/bin/env python3
"""storm.py - random-point signal storms against weir fixtures, with an oracle.

usage: python3 -I storm.py --bin WEIR --out WORKDIR [--fixture NAME[,NAME..]|all] [--runs N] [--jobs J]
                           [--kinds INT,TERM,HUP] [--count 1-3] [--gap 0-50] [--window MS]
                           [--seed S] [--timeout S] [--verbose] [--tty] [--keep]

Linux only (the orphan scan reads /proc). Each run: spawn `WEIR fx/NAME.weir LOG TAG RUNDIR PORT CLI`
(CLI = cli.py next to this file; PORT from the block $STORM_PORT0 (20000) .. +499, BELOW the
ephemeral range [D:harness-ports]: run % 480, a candidate something already listens on is skipped,
and a lost bind reruns the same draw on the next candidate) in a new session (default dispositions,
stdin /dev/null, TMPDIR=RUNDIR/tmp), send COUNT signals (kinds drawn from KINDS) to weir's pid
alone, the first at U(0, WINDOW) ms after spawn (WINDOW defaults to the fixture's calibrated
unsignalled duration + 150ms), the rest GAP ms apart. Then judge:
  rc      exit status vs the docs (R7: death by the first signal, -n, for one signal or several;
          0 or -n if DONE logged)
  once    each O/S/E line per scope path at most once; E after S after O
  nest    after `S p` (p's cleanup began) or `J p` (joined) no line of p's body or of a scope nested in p
  landed  no `L` line (an effect that must never run, e.g. a race loser past its cut)
  whole   one signal: every opened scope ran its cleanup start and end (`O p` => `S p`, `E p`)
  orphan  no process left in weir's session or carrying TAG in its argv (300ms after exit); a fixture
          line `// storm-allow-orphan: <argv prefix>` exempts a deliberate daemon (killed, not counted)
  tmp     RUNDIR/tmp empty after exit
  stderr  no internal error / .NET text / "command failed" / "One or more errors" on stderr
  hang    exit within TIMEOUT s of the last signal
Log grammar written by fixtures: `O path` scope body began, `B path ...` body effect,
`S path` cleanup began, `E path` cleanup ended, `C path ...` cleanup effect, `J path` the fan-out/race at path joined (no line of
path/... may follow), `L ...` a line that must never be written, `DONE` script reached its end; `path` is
slash-separated scope nesting (outer/lock/arm1). Fixtures live in fx/ next to this file.
Exit code: 0 if every run passed, 1 otherwise. Writes OUT/runs.jsonl (one record per run) and
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
SIGS = {'INT': signal.SIGINT, 'TERM': signal.SIGTERM, 'HUP': signal.SIGHUP}
LOST_BIND = re.compile(r'cannot listen|Address already in use')
BAD_ERR = re.compile(r'internal error|Unhandled|Exception|   at |System\.|One or more errors|command failed|TaskScheduler', re.I)

def pre():
    for s in (signal.SIGINT, signal.SIGQUIT, signal.SIGHUP, signal.SIGTERM, signal.SIGPIPE):
        signal.signal(s, signal.SIG_DFL)

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

def calibrate(binp, fx, base):
    d = os.path.join(base, 'calib-' + fx)
    shutil.rmtree(d, ignore_errors=True); os.makedirs(d + '/tmp')
    env = dict((k, v) for k, v in os.environ.items() if 'proxy' not in k.lower())
    env['TMPDIR'] = d + '/tmp'
    t0 = time.monotonic()
    p = subprocess.run([binp, os.path.join(HERE, 'fx', fx + '.weir'), d + '/log', '9999999', d, str(free_port(498)), CLI],
                       stdin=subprocess.DEVNULL, capture_output=True, env=env, preexec_fn=pre, timeout=60)
    ms = int((time.monotonic() - t0) * 1000)
    lines = open(d + '/log').read().splitlines() if os.path.exists(d + '/log') else []
    iss = judge(lines, p.returncode, [], 0)
    if 'DONE' not in lines or iss:
        sys.exit('calibration of %s failed: rc=%s issues=%s err=%s' % (fx, p.returncode, iss, p.stderr[:300]))
    shutil.rmtree(d, ignore_errors=True)
    return ms

class TtyProc:
    """weir as the session leader of a fresh pty (its group is the terminal's foreground group);
    ctrl_c() types 0x03; everything weir writes to the terminal lands in errf"""
    def __init__(self, argv, env, errf):
        import pty, fcntl, termios
        m, sl = os.openpty()
        def pre_tty():
            pre()
            fcntl.ioctl(0, termios.TIOCSCTTY, 0)
        self.m = m; self.errf = errf
        self.p = subprocess.Popen(argv, stdin=sl, stdout=sl, stderr=sl, preexec_fn=pre_tty, start_new_session=True, env=env)
        os.close(sl)
        self.pid = self.p.pid
        self.t = threading.Thread(target=self.pump, daemon=True); self.t.start()
    def pump(self):
        while True:
            try: c = os.read(self.m, 65536)
            except OSError: return
            if not c: return
            self.errf.write(c); self.errf.flush()
    def ctrl_c(self): os.write(self.m, b'\x03')
    def poll(self): return self.p.poll()
    def wait(self, timeout=None): return self.p.wait(timeout=timeout)
    def close(self):
        self.t.join(1)
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
    count = rng.randint(a.count[0], a.count[1])
    sent = [rng.choice(a.kinds) for _ in range(count)]
    t_first = rng.uniform(0, window)
    gaps = [rng.uniform(a.gap[0], a.gap[1]) for _ in range(count - 1)]
    env = dict((k, v) for k, v in os.environ.items() if 'proxy' not in k.lower())
    env['TMPDIR'] = d + '/tmp'
    errf = open(d + '/err', 'wb')
    t0 = time.monotonic()
    argv = [binp, os.path.join(HERE, 'fx', fx + '.weir'), log, tag, d, str(free_port(poff)), CLI]
    if a.tty:
        p = TtyProc(argv, env, errf)
    else:
        p = subprocess.Popen(argv, stdin=subprocess.DEVNULL,
                             stdout=open(d + '/out', 'wb'), stderr=errf, preexec_fn=pre, start_new_session=True, env=env)
    times = [t_first]
    for g in gaps: times.append(times[-1] + g)
    n_before = 0; t_last = None
    for k, t in zip(sent, times):
        dt = t0 + t / 1000.0 - time.monotonic()
        if dt > 0: time.sleep(dt)
        if p.poll() is not None: break
        try:
            if a.tty and k == 'INT': p.ctrl_c()
            else: os.kill(p.pid, SIGS[k])
            n_before += 1; t_last = time.monotonic()
        except ProcessLookupError:
            break
    hang = False
    try:
        rc = p.wait(timeout=a.timeout)
    except subprocess.TimeoutExpired:
        hang = True; rc = 'HANG'
    t_exit = time.monotonic()
    lat = int((t_exit - t_last) * 1000) if t_last else None
    if hang:
        try: os.killpg(p.pid, signal.SIGKILL)
        except Exception: pass
        p.wait()
    time.sleep(0.3)
    if a.tty: p.close()
    left = procs_of(p.pid, tag, os.getpid())
    allow = [l.split(':', 1)[1].strip() for l in open(os.path.join(HERE, 'fx', fx + '.weir')) if l.startswith('// storm-allow-orphan:')]
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
    if left: issues.append('orphan:%d %s' % (len(left), [x[1:] for x in left][:3]))
    leak = os.listdir(d + '/tmp')
    if leak: issues.append('tmp:%s' % leak[:3])
    err = open(d + '/err', 'rb').read().decode(errors='replace')
    if BAD_ERR.search(err): issues.append('stderr:%r' % err.strip()[:200])
    rec = dict(fx=fx, run=r, sent=sent[:max(n_before, 1)], n=n_before, at=[int(x) for x in times[:max(n_before, 1)]],
               rc=rc, lat=lat, issues=issues, tail=lines[-6:], err=err.strip()[:300])
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
    ap.add_argument('--tty', action='store_true', help='weir leads a pty session; INT is a typed ^C (reaches the whole foreground group), TERM/HUP go to the pid')
    a = ap.parse_args()
    a.kinds = a.kinds.split(',')
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
        cal = calibrate(a.bin, fx, a.out)
        window = a.window or cal + 150
        rngs = [random.Random('%d-%s-%d' % (seed, fx, r)) for r in range(a.runs)]
        with ThreadPoolExecutor(a.jobs) as ex:
            recs = list(ex.map(lambda r: one(a, a.bin, fx, window, r, a.out, rngs[r]), range(a.runs)))
        cats = {}; rcs = {}; lats = []
        bad = 0
        for rec in recs:
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
        print('%s: bad %d/%d calib=%dms window=%dms cats=%s rcs=%s lat p50=%s p95=%s max=%s' % (
            fx, bad, a.runs, cal, window, cats, rcs, pct(.5), pct(.95), lats[-1] if lats else None), flush=True)
        total_bad += bad
        summary.append(dict(fx=fx, bad=bad, runs=a.runs, calib_ms=cal, window_ms=window, cats=cats, rcs=rcs,
                            lat_p50=pct(.5), lat_p95=pct(.95), lat_max=lats[-1] if lats else None,
                            secs=round(time.monotonic() - t_fx, 1)))
    secs = round(time.monotonic() - t_all, 1)
    with open(os.path.join(a.out, 'summary.json'), 'w') as f:
        json.dump(dict(seed=seed, count=a.count, tty=a.tty, kinds=a.kinds, jobs=a.jobs, bad=total_bad,
                       runs=a.runs * len(fxs), secs=secs, fixtures=summary), f, indent=1)
    print('total: bad %d/%d in %.1fs' % (total_bad, a.runs * len(fxs), secs), flush=True)
    sys.exit(1 if total_bad else 0)

main()
