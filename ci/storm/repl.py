#!/usr/bin/env python3
"""repl.py - signal storms against the tty REPL, with an oracle (storm.py's sibling; same judge).

usage: python3 -I repl.py --bin WEIR --out WORKDIR [--mode int|kill] [--entry NAME[,NAME..]|all] [--runs N]
                          [--jobs J] [--kinds TERM,HUP,HANGUP] [--seed S] [--timeout S] [--verbose] [--keep]

Linux only. Each run starts `WEIR` with no arguments as the session leader of a fresh 24x80 pty (cwd RUNDIR,
TMPDIR=RUNDIR/tmp, XDG_STATE_HOME/XDG_CONFIG_HOME=RUNDIR, NO_COLOR=1, TERM=xterm), waits for `weir> ` and types
an entry from rx/NAME.weir next to this file (lines joined by Esc-Enter; @@LOG@@ @@TAG@@ @@DIR@@ substituted).
Entries write storm.py's log grammar, with `B top ...` for a top-level statement and `DONE` last. Each entry
is calibrated unsignalled first (from its first log line to DONE = CAL). Modes:
  int   ^C typed at U(0, CAL - 200ms) after the entry's first log line (R6, C21/C23). Then judge:
        exit130  `↳ exit 130` printed after the ^C
        after    at most one `B top` line lands after the ^C (each wait is followed by two such statements:
                 the entry ends after the statement in flight); no DONE
        once/nest/landed/whole  storm.py's judge with one signal (a cleanup a ^C reaches runs whole)
        alive    `print ("nx" + "t")` answers nxt; then ^D ends the REPL with 0
  kill  one of KINDS (TERM/HUP to the pid; HANGUP closes the master, and half those runs give stderr a file)
        at the idle prompt, at the prompt after a finished entry, or mid-entry at U(0, CAL - 200ms) (C1-1, C1-4).
        Then judge:
        rc       death by the signal (-n; a hangup may also exit 129); never `internal error`
        whole    mid-entry: storm.py's judge with one signal (cleanups whole, nothing after a cut)
Both modes also judge: orphan (nothing left in the session or carrying TAG, 300ms after exit), tmp (RUNDIR/tmp
empty), ttymode (unless hung up: the pty is cooked again after exit), stderr (storm.py's BAD_ERR over everything
weir wrote), hang (each wait bounded by TIMEOUT s). A run matching an open finding would count as `known`, not
bad (none is open). Writes OUT/runs.jsonl and OUT/summary.json; failing run dirs
(log, tty transcript) are kept. Exit 0 if every run passed, 1 otherwise.
"""
import os, sys, time, json, random, signal, argparse, shutil, re, importlib.util
from concurrent.futures import ThreadPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location('storm', os.path.join(HERE, 'storm.py'))
storm = importlib.util.module_from_spec(_spec); _spec.loader.exec_module(storm)
PROMPT = b'weir> '
EXIT130 = '↳ exit 130'.encode()
ANSI = re.compile(rb'\x1b\[[0-9;?]*[A-Za-z]|\x1b[=>]|\r')

class Buf:
    """the transcript: TtyProc's pump writes here; find() waits for bytes after a mark"""
    def __init__(self, path):
        self.b = bytearray(); self.f = open(path, 'wb')
    def write(self, c):
        self.b += c; self.f.write(c)
    def flush(self): self.f.flush()
    def find(self, what, frm, timeout, alive=None):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            if what in self.b[frm:]: return True
            if alive and not alive(): return what in self.b[frm:]
            time.sleep(0.01)
        return what in self.b[frm:]

def entry_text(name, log, tag, d):
    t = open(os.path.join(HERE, 'rx', name + '.weir')).read()
    t = t.replace('@@LOG@@', log).replace('@@TAG@@', tag).replace('@@DIR@@', d)
    return [l for l in t.rstrip('\n').split('\n') if not l.startswith('//')]

def read_log(log):
    return open(log).read().splitlines() if os.path.exists(log) else []

def wait_log(log, pred, timeout, alive):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        ls = read_log(log)
        if pred(ls): return ls
        if not alive(): break
        time.sleep(0.002)
    return None

def start(binp, d, buf, stderr=None):
    env = storm.env_for(d)
    env.update(NO_COLOR='1', TERM='xterm', XDG_STATE_HOME=d, XDG_CONFIG_HOME=d)
    return storm.TtyProc([binp], env, buf, d, stderr=stderr)

def type_entry(p, lines):
    for j, l in enumerate(lines):
        p.key(l.encode() + (b'\r' if j == len(lines) - 1 else b'\x1b\r'))
        time.sleep(0.01)

def calibrate(a, name):
    d = os.path.join(a.out, 'calib-' + name)
    shutil.rmtree(d, ignore_errors=True); os.makedirs(d + '/tmp')
    log = d + '/log'; buf = Buf(d + '/tty')
    p = start(a.bin, d, buf)
    up = lambda: storm.alive(p.pid)
    ok = buf.find(PROMPT, 0, 15, up)
    t1 = None; ls = None
    if ok:
        # the smallest tag: `sleep 0.3@@TAG@@` runs shortest, so CAL bounds every run's entry from below
        type_entry(p, entry_text(name, log, '0000000', d))
        if wait_log(log, lambda ls: ls, 15, up) is not None:
            t1 = time.monotonic()
            ls = wait_log(log, lambda ls: 'DONE' in ls, 60, up)
    cal = int((time.monotonic() - t1) * 1000) if ls else None
    m = len(buf.b)
    if ls: buf.find(PROMPT, m, 10, up)
    p.key(b'\x04')
    rc, _, _ = storm.reap(p, 10)
    if rc == 'HANG':
        os.killpg(p.pid, signal.SIGKILL); storm.reap(p, None)
    p.close()
    iss = storm.judge(ls or [], 0, [], 0) if ls else ['no DONE']
    if rc != 0: iss.append('rc=%s' % rc)
    if iss:
        print('  CALIBRATION FAIL %s: %s tty=%r' % (name, iss, bytes(buf.b[-400:])), flush=True)
        return None
    shutil.rmtree(d, ignore_errors=True)
    return cal

def one(a, name, cal, r, rng):
    tag = '%d%05d' % (os.getpid() % 100, r)
    d = os.path.join(a.out, 'run-%s-%d' % (name, r))
    shutil.rmtree(d, ignore_errors=True); os.makedirs(d + '/tmp')
    log = d + '/log'; buf = Buf(d + '/tty')
    kind = 'INT' if a.mode == 'int' else rng.choice(a.kinds)
    phase = 'entry' if a.mode == 'int' else rng.choice(['idle', 'done', 'entry'])
    at = rng.uniform(0, max(cal - 200, 50)) / 1000.0
    idle = rng.uniform(0.05, 0.6)
    err_file = kind == 'HANGUP' and rng.random() < 0.5
    p = start(a.bin, d, buf, stderr=open(d + '/err', 'wb') if err_file else None)
    up = lambda: storm.alive(p.pid)
    issues = []; n0 = None; sent = False
    lines = entry_text(name, log, tag, d)
    if not buf.find(PROMPT, 0, a.timeout, up):
        issues.append('noprompt')
    else:
        if phase == 'idle':
            time.sleep(idle)
        else:
            type_entry(p, lines)
            if wait_log(log, lambda ls: ls, a.timeout, up) is None:
                issues.append('entry:never began')
            elif phase == 'done':
                m = len(buf.b)
                if wait_log(log, lambda ls: 'DONE' in ls, a.timeout + cal / 1000.0, up) is None:
                    issues.append('entry:no DONE unsignalled')
                buf.find(PROMPT, m, a.timeout, up)
                time.sleep(idle)
            else:
                time.sleep(at)
                n0 = len(read_log(log))
        if not issues and up():
            m = len(buf.b)
            if kind == 'INT': p.key(b'\x03')
            elif kind == 'HANGUP': p.hangup()
            else: os.kill(p.pid, storm.SIGS[kind])
            sent = True
            if kind == 'INT':
                if not buf.find(EXIT130, m, a.timeout, up): issues.append('exit130:none')
                buf.find(PROMPT, m, a.timeout, up)
                m2 = len(buf.b)
                p.key(b'print ("nx" + "t")\r')
                if not buf.find(b'nxt\r\n', m2, a.timeout, up): issues.append('alive:no answer')
                p.key(b'\x04')
    rc, core, _ = storm.reap(p, a.timeout)
    hang = rc == 'HANG'
    if hang:
        try: os.killpg(p.pid, signal.SIGKILL)
        except Exception: pass
        storm.reap(p, None)
        issues.append('HANG')
    time.sleep(0.3)
    tty_bad = p.termios_ok() if not p.hung and not hang else []
    p.close()
    left = storm.procs_of(p.pid, tag, os.getpid())
    for pid, _, _ in left:
        try: os.kill(pid, signal.SIGKILL)
        except Exception: pass
    ls = read_log(log)
    if not hang:
        if kind == 'INT':
            if rc != 0: issues.append('rc=%s(^D)' % rc)
            if n0 is not None:
                tops = [l for l in ls[n0:] if l.startswith('B top ')]
                if len(tops) > 1: issues.append('after:%r' % tops[:3])
                issues += [i for i in storm.judge(ls, -2, ['INT'], 1) if not i.startswith('rc=')]
        elif sent:
            want = {-storm.SIGS[kind]} | ({129} if kind == 'HANGUP' else set())
            if rc not in want: issues.append('rc=%s(sent %s at %s)' % (rc, kind, phase))
            if phase == 'entry': issues += [i for i in storm.judge(ls, rc, [kind], 1) if not i.startswith('rc=')]
    if core: issues.append('core:dumped')
    if left: issues.append('orphan:%d %s' % (len(left), [x[1:] for x in left][:3]))
    leak = os.listdir(d + '/tmp')
    if leak: issues.append('tmp:%s' % leak[:3])
    if tty_bad: issues.append('ttymode:%s off' % ','.join(tty_bad))
    out = ANSI.sub(b'', bytes(buf.b)).decode(errors='replace')
    if err_file: out += open(d + '/err', 'rb').read().decode(errors='replace')
    # an open finding would be counted apart here, as storm.py's known_of does; none is open
    known = None
    if storm.BAD_ERR.search(out): issues.append('stderr:%r' % storm.BAD_ERR.search(out).group(0))
    rec = dict(entry=name, run=r, kind=kind, phase=phase, at=int(at * 1000) if phase == 'entry' else None,
               rc=rc, issues=issues, known=known, tail=ls[-6:], out=out.strip()[-300:])
    if not issues and not a.keep:
        shutil.rmtree(d, ignore_errors=True)
    return rec

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--bin', required=True)
    ap.add_argument('--out', required=True, help='work dir: runs.jsonl, summary.json, failing run dirs')
    ap.add_argument('--mode', choices=['int', 'kill'], default='int')
    ap.add_argument('--entry', default='all')
    ap.add_argument('--runs', type=int, default=50)
    ap.add_argument('--jobs', type=int, default=3)
    ap.add_argument('--kinds', default='TERM,HUP,HANGUP')
    ap.add_argument('--seed', type=int, default=None)
    ap.add_argument('--timeout', type=float, default=20)
    ap.add_argument('--keep', action='store_true')
    ap.add_argument('--verbose', action='store_true')
    a = ap.parse_args()
    a.kinds = a.kinds.split(',')
    if any(k not in ('TERM', 'HUP', 'HANGUP') for k in a.kinds): sys.exit('--kinds: TERM, HUP, HANGUP')
    a.out = os.path.abspath(a.out)
    names = sorted(f[:-5] for f in os.listdir(os.path.join(HERE, 'rx')) if f.endswith('.weir')) if a.entry == 'all' else a.entry.split(',')
    seed = a.seed if a.seed is not None else random.randrange(1 << 30)
    os.makedirs(a.out, exist_ok=True)
    jl = open(os.path.join(a.out, 'runs.jsonl'), 'a')
    print('seed %d mode %s' % (seed, a.mode), flush=True)
    total_bad = 0; summary = []; t_all = time.monotonic()
    for name in names:
        t0 = time.monotonic()
        cal = calibrate(a, name)
        if cal is None:
            print('%s: bad %d/%d calibration failed' % (name, a.runs, a.runs), flush=True)
            total_bad += a.runs; summary.append(dict(entry=name, bad=a.runs, runs=a.runs, cats={'calib': 1}))
            continue
        rngs = [random.Random('%d-%s-%s-%d' % (seed, a.mode, name, r)) for r in range(a.runs)]
        with ThreadPoolExecutor(a.jobs) as ex:
            recs = list(ex.map(lambda r: one(a, name, cal, r, rngs[r]), range(a.runs)))
        bad = 0; cats = {}; rcs = {}; known = 0
        for rec in recs:
            known += bool(rec['known'])
            jl.write(json.dumps(rec) + '\n')
            rcs[str(rec['rc'])] = rcs.get(str(rec['rc']), 0) + 1
            if rec['issues']:
                bad += 1
                for i in rec['issues']:
                    c = i.split(':')[0].split('=')[0]
                    cats[c] = cats.get(c, 0) + 1
                print('  FAIL %s run %d %s %s at=%s rc=%s %s | tail=%s' % (name, rec['run'], rec['kind'], rec['phase'], rec['at'], rec['rc'], rec['issues'], rec['tail']), flush=True)
            elif a.verbose:
                print('  ok %s run %d %s %s at=%s rc=%s' % (name, rec['run'], rec['kind'], rec['phase'], rec['at'], rec['rc']))
        print('%s: bad %d/%d calib=%dms cats=%s rcs=%s known=%d' % (name, bad, a.runs, cal, cats, rcs, known), flush=True)
        total_bad += bad
        summary.append(dict(entry=name, bad=bad, runs=a.runs, known=known, calib_ms=cal, cats=cats, rcs=rcs, secs=round(time.monotonic() - t0, 1)))
    secs = round(time.monotonic() - t_all, 1)
    with open(os.path.join(a.out, 'summary.json'), 'w') as f:
        json.dump(dict(seed=seed, mode=a.mode, kinds=a.kinds, jobs=a.jobs, bad=total_bad, runs=a.runs * len(names), secs=secs, entries=summary), f, indent=1)
    print('total: bad %d/%d in %.1fs' % (total_bad, a.runs * len(names), secs), flush=True)
    sys.exit(1 if total_bad else 0)

if __name__ == '__main__':
    main()
