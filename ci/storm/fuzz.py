# fan-out/race nesting fuzzer: generator + discrete-event oracle + runner.
# usage: python3 -I fuzz.py --bin WEIR --out WORKDIR [--trees N] [--seed S] [--jobs J]
# Each tree is OUT/trees/SEED (t.weir, prog.json, log), TMPDIR is OUT/tmp; a passing
# tree's dir is removed, a failing one kept. Writes OUT/trees.jsonl (one record per
# tree) and OUT/summary.json; exit 1 if any tree failed.
import random, sys, os, subprocess, heapq, itertools, time, json, re, shutil, argparse

W = None
BASE = None
TMP = None
GAP = 0.060   # min separation of independent timer events (s)
SLACK = 0.045
CLW = os.environ.get("CLW") == "1"
BUGCOMPAT = os.environ.get("BUGCOMPAT") == "1" # cross-flow ordering slack (s)

class SExit(Exception):
    def __init__(s, n): s.n = n
class SFail(Exception):
    def __init__(s, lab): s.lab = lab
class SCut(Exception): pass

# ---------------- generator ----------------
class Gen:
    def __init__(s, rnd, rid):
        s.r = rnd; s.n = 0; s.rid = rid
    def lab(s, p):
        s.n += 1; return f"{p}{s.n}"
    def dur(s):
        return round(s.r.uniform(0.08, 0.6), 3)
    def steps(s, depth, cleanup=False, allow_exit=True):
        out = []
        for _ in range(s.r.randint(1, 3)):
            out.append(s.step(depth, cleanup, allow_exit))
        return out
    def step(s, depth, cleanup, allow_exit):
        r = s.r.random()
        if r < 0.22: return ("note", s.lab("n"))
        if r < 0.40: return ("dsleep", s.dur())
        if r < 0.55: return ("csleep", s.dur())
        if not cleanup and allow_exit and r < 0.62: return ("exit", s.r.choice([3, 5, 7, 9]))
        if not cleanup and r < 0.68: return ("fail", s.lab("f"))
        if depth < 3 and r < 0.85: return s.fan(depth + 1, cleanup)
        if depth >= 4: return ("note", s.lab("n"))
        if (not cleanup or CLW) and depth < 4 and r < 1.0:
            return ("within", s.lab("w"), s.steps(depth + 1, cleanup, allow_exit and not cleanup), s.steps(depth + 1, True, False))
        return ("note", s.lab("n"))
    def fan(s, depth, cleanup):
        kind = s.r.choice(["pmap", "piter", "pfirst"])
        width = s.r.choice([None, 1, 2, 2, 3])
        na = s.r.randint(1, int(os.environ.get("MAXARMS", "3")))
        arms = []
        for a in range(na):
            body = s.steps(depth, cleanup, not cleanup)
            if cleanup and kind != "pfirst":
                body = [x for x in body if x[0] not in ("fail",)] or [("note", s.lab("n"))]
            arms.append(body)
        return ("fan", s.lab("F"), kind, width, arms)

# ---------------- simulator ----------------
class Fan:
    def __init__(s, lab): s.lab = lab; s.cut = False

def eff_cut(ctx):
    for e in reversed(ctx):
        if e == "cleanup": return False
        if isinstance(e, Fan) and e.cut: return True
    return False

def depth_of(ctx):
    return sum(1 for e in ctx if isinstance(e, Fan))

class Sim:
    def __init__(s):
        s.now = 0.0; s.q = []; s.seq = itertools.count(); s.notes = []  # (t, lab, req set)
        s.timers = []  # (t, flowid)
        s.sleepers = {}  # flowid -> (flow, token)
        s.fid = itertools.count()
        s.origin = 0; s.ev = []; s.oc = itertools.count(1)
    def at(s, t, fn, origin=None):
        heapq.heappush(s.q, (t, next(s.seq), fn, s.origin if origin is None else origin))
    def event(s, fid, dec):
        s.ev.append((s.now, fid, s.origin, dec))
    def run(s):
        while s.q:
            t, _, fn, o = heapq.heappop(s.q); s.now = t; s.origin = o; fn()

class Flow:
    def __init__(s, sim, ctx, seen, done):
        s.sim = sim; s.ctx = ctx; s.seen = set(seen); s.done = done; s.id = next(sim.fid)
        s.gen = None
    def start(s, gen):
        s.gen = gen; s.step(("send", None))
    def step(s, how):
        sim = s.sim
        while True:
            try:
                if how[0] == "send": req = s.gen.send(how[1])
                else: req = s.gen.throw(how[1])
            except StopIteration as e:
                s.done(("ok", e.value), s); return
            except (SExit, SFail, SCut) as e:
                s.done(("err", e), s); return
            k = req[0]
            if k == "check":
                how = ("throw", SCut()) if eff_cut(s.ctx) else ("send", None); continue
            if k == "note":
                if eff_cut(s.ctx): how = ("throw", SCut()); continue
                sim.notes.append((sim.now, req[1], frozenset(s.seen))); s.seen.add(req[1]); sim.event(s.id, False)
                how = ("send", None); continue
            if k == "sleep":
                if eff_cut(s.ctx): how = ("throw", SCut()); continue
                tok = object(); s.tok = tok
                sim.sleepers[s.id] = s
                end = sim.now + req[1]
                sim.timers.append((end, s.id))
                def wake(tok=tok):
                    if s.tok is tok:
                        s.tok = None; sim.sleepers.pop(s.id, None); sim.event(s.id, False); s.step(("send", None))
                sim.at(end, wake, next(sim.oc)); return
            if k == "fan":
                if eff_cut(s.ctx): how = ("throw", SCut()); continue
                run_fan(sim, s, req[1]); return
            raise Exception(req)
    def poke(s):
        # a token was set: a sleeping flow whose ctx is now cut raises
        if s.tok is not None and eff_cut(s.ctx):
            s.tok = None; s.sim.sleepers.pop(s.id, None); s.step(("throw", SCut()))

def set_cut(sim, F):
    F.cut = True
    for fl in list(sim.sleepers.values()): fl.poke()

def interp(flow, steps):
    for st in steps:
        k = st[0]
        if k == "note": yield ("note", st[1])
        elif k in ("dsleep", "csleep"): yield ("sleep", st[1])
        elif k == "exit":
            # every evaluated node is a boundary [D:scope-tree]: a cut flow stops before it
            yield ("check",); raise SExit(st[1])
        elif k == "fail":
            yield ("check",); raise SFail(st[1])
        elif k == "fan":
            v = yield ("fan", st)
            yield ("note", f"{st[1]} done {v}")
        elif k == "within":
            # the within node itself is a boundary: a cut flow never enters it [D:scope-tree]
            yield ("check",)
            pend = None
            try:
                yield from interp(flow, st[2])
            except (SExit, SFail, SCut) as e:
                pend = e
            flow.ctx.append("cleanup")
            try:
                yield from interp(flow, st[3])
            finally:
                flow.ctx.pop()
            if pend: raise pend
    return None

def run_fan(sim, parent, st):
    _, lab, kind, width, arms = st
    F = Fan(lab)
    w = width if width is not None else (64 if depth_of(parent.ctx) == 0 else 8 if depth_of(parent.ctx) == 1 else 1)
    n = len(arms); nxt = [0]; running = [0]; res = [None] * n
    state = {"won": None, "exit": None, "seen": set(parent.seen)}
    fctx = parent.ctx + [F]
    def startable():
        if BUGCOMPAT and kind != "pfirst": return nxt[0] < n and not F.cut
        return nxt[0] < n and not F.cut and not eff_cut(parent.ctx)
    def finish():
        if state.get("fin"): return
        state["fin"] = True
        parent.seen |= state["seen"]
        if state["exit"] is not None: parent.step(("throw", SExit(state["exit"]))); return
        if eff_cut(parent.ctx): parent.step(("throw", SCut())); return
        if kind == "pfirst":
            if state["won"] is not None: parent.step(("send", state["won"])); return
            for i in range(n):
                if res[i] and res[i][0] == "err" and isinstance(res[i][1], SFail):
                    parent.step(("throw", res[i][1])); return
            parent.step(("throw", SCut())); return
        for i in range(n):
            if res[i] and res[i][0] == "err" and isinstance(res[i][1], SFail):
                parent.step(("throw", res[i][1])); return
        for i in range(n):
            if res[i] and res[i][0] == "err":
                parent.step(("throw", res[i][1])); return
        if kind == "pmap": parent.step(("send", "[" + "; ".join(str(i) for i in range(n)) + "]"))
        else: parent.step(("send", "()"))
    def arm_done(i):
        def d(r, fl):
            res[i] = r; running[0] -= 1; state["seen"] |= fl.seen
            if r[0] == "err" and isinstance(r[1], SExit):
                if state["exit"] is None and state["won"] is None and not F.cut:
                    sim.event(fl.id, True); state["exit"] = r[1].n; set_cut(sim, F)
            elif r[0] == "ok" and kind == "pfirst":
                if state["won"] is None and state["exit"] is None and not F.cut:
                    sim.event(fl.id, True); state["won"] = i; set_cut(sim, F)
            fill()
            if running[0] == 0 and not startable(): finish()
        return d
    def fill():
        while running[0] < w and startable():
            i = nxt[0]; nxt[0] += 1; running[0] += 1
            fl = Flow(sim, list(fctx), parent.seen, arm_done(i))
            def g(fl=fl, i=i):
                yield from interp(fl, arms[i]); return i
            sim.at(sim.now, lambda fl=fl, g=g: (sim.event(fl.id, False), fl.start(g())), next(sim.oc))
    fill()
    if running[0] == 0: sim.at(sim.now, finish)

def simulate(prog):
    sim = Sim(); out = {}
    def done(r, fl): out["r"] = r
    main = Flow(sim, [], set(), done)
    main.start(interp(main, prog))
    sim.run()
    return sim, out["r"]

def racy(sim):
    ev = sorted(sim.ev)
    for i, (t, f, o, d) in enumerate(ev):
        for (t2, f2, o2, d2) in ev[i+1:]:
            if t2 - t >= GAP: break
            if f != f2 and o != o2 and (d or d2): return True
    ts = sorted(sim.timers)
    for (a, fa), (b, fb) in zip(ts, ts[1:]):
        if fa != fb and b - a < GAP: return True
    return False

# ---------------- codegen ----------------
def codegen(prog, rid):
    defs = []; ctr = [0]
    def dfmt(d): return f"{int(d*1000)}ms"
    def cfmt(d): return f"sleep {d:.3f}{rid}"
    def body(steps, ind):
        L = []
        pad = "    " * ind
        for st in steps:
            k = st[0]
            if k == "note": L.append(f'{pad}note "{st[1]}"')
            elif k == "dsleep": L.append(f"{pad}Duration.sleep {dfmt(st[1])}")
            elif k == "csleep": L.append(f"{pad}{cfmt(st[1])}")
            elif k == "exit": L.append(f"{pad}exit {st[1]}")
            elif k == "fail": L.append(f'{pad}fail "{st[1]}"')
            elif k == "fan":
                _, lab, kind, width, arms = st
                names = []
                for a in arms:
                    ctr[0] += 1; nm = f"a{ctr[0]}"; names.append(nm)
                    b = body(a, 1)
                    defs.append(f"let {nm} i =\n" + "\n".join(b) + "\n    i")
                fn = f"Seq.{kind}" + (f"With {width}" if width is not None else "")
                if width is not None: fn = f"Seq.{kind}With {width}"
                xs = "[" + "; ".join(str(i) for i in range(len(arms))) + "]"
                disp = " ".join(f"if i == {j} then {nm} i el" for j, nm in enumerate(names[:-1])) + ("" if len(names) == 1 else "se ") + f"{names[-1]} i"
                disp = disp.replace(" el" + "se ", " else ").replace(" elif", " elif")
                # build if/elif/else chain
                if len(names) == 1: disp = f"{names[0]} i"
                else:
                    parts = [f"if i == 0 then {names[0]} i"] + [f"elif i == {j} then {names[j]} i" for j in range(1, len(names) - 1)] + [f"else {names[-1]} i"]
                    disp = " ".join(parts)
                if kind == "piter":
                    L.append(f"{pad}{xs} |> {fn} (fun i ->\n{pad}    let _r = {disp}\n{pad}    ())")
                    L.append(f'{pad}note "{lab} done ()"')
                else:
                    L.append(f"{pad}let r{lab} = {xs} |> {fn} (fun i -> {disp})")
                    L.append(f'{pad}note $"{lab} done {{show r{lab}}}"')
            elif k == "within":
                L.append(f"{pad}within")
                L += body(st[2], ind + 1)
                L.append(f"{pad}always")
                L += body(st[3], ind + 1)
        return L
    main = body(prog, 0)
    head = "let log = Self.args |> Seq.head\nlet note s = File.append log [s]\n"
    return head + "\n".join(defs) + "\n" + "\n".join(main) + "\n"

# ---------------- oracle check ----------------
def expect(prog):
    sim, r = simulate(prog)
    if r[0] == "ok": code, err = 0, None
    elif isinstance(r[1], SExit): code, err = r[1].n, None
    elif isinstance(r[1], SFail): code, err = 1, r[1].lab
    else: code, err = "CUT?", None
    return sim, code, err

def check(sim, code, err, rc, stderr, lines, orphans):
    probs = []
    if rc != code: probs.append(f"rc {rc} != {code}")
    if err is None and stderr.strip(): probs.append(f"stderr: {stderr.strip()[:300]}")
    if err is not None and f'"{err}"' not in stderr and err not in stderr: probs.append(f"stderr lacks {err}: {stderr.strip()[:300]}")
    exp = sorted(sim.notes)
    el = [l for _, l, _ in exp]
    if sorted(el) != sorted(lines):
        miss = [x for x in el if x not in lines]; extra = [x for x in lines if x not in el]
        probs.append(f"notes differ: missing {miss} extra {extra}")
    else:
        pos = {l: i for i, l in enumerate(lines)}
        tm = {l: t for t, l, _ in exp}
        for t, l, req in exp:
            for q in req:
                if pos[q] > pos[l]: probs.append(f"causal: {q} after {l}")
        for i, a in enumerate(lines):
            for b in lines[i+1:]:
                if tm[a] > tm[b] + SLACK: probs.append(f"order: {a}@{tm[a]:.3f} before {b}@{tm[b]:.3f}")
    if orphans: probs.append(f"orphans: {orphans}")
    return probs

def run_one(idx, seed):
    rnd = random.Random(seed)
    rid = f"{seed % 100000:05d}7"
    for _ in range(200):
        g = Gen(rnd, rid)
        prog = g.steps(0)
        if not any(x[0] == "fan" for x in prog): continue
        sim, code, err = expect(prog)
        if code == "CUT?" or racy(sim): continue
        break
    else:
        return None
    if rnd.random() < 0.4:
        prog = [("within", "wtop", prog, [("note", "outer-always")])]
        sim, code, err = expect(prog)
    src = codegen(prog, rid)
    d = f"{BASE}/{seed}"; os.makedirs(d, exist_ok=True)
    open(f"{d}/t.weir", "w").write(src)
    open(f"{d}/prog.json", "w").write(json.dumps(prog))
    logf = f"{d}/log"
    if os.path.exists(logf): os.remove(logf)
    env = dict(os.environ, TMPDIR=TMP, NO_COLOR="1")
    t0 = time.time()
    try:
        p = subprocess.run([W, f"{d}/t.weir", logf], capture_output=True, text=True, timeout=30, env=env)
        rc, se = p.returncode, p.stderr
    except subprocess.TimeoutExpired:
        rc, se = "TIMEOUT", ""
    el = time.time() - t0
    time.sleep(0.05)
    ps = subprocess.run(["ps", "-eo", "pid,args"], capture_output=True, text=True).stdout
    orph = [l.strip() for l in ps.splitlines() if rid in l and "sleep" in l]
    for l in orph:
        try: os.kill(int(l.split()[0]), 9)
        except Exception: pass
    lines = open(logf).read().splitlines() if os.path.exists(logf) else []
    probs = check(sim, code, err, rc, se, lines, orph)
    # the simulated run's end, not only its last note: a tree may end with no note at all
    tsim = max([t for t, _, _ in sim.notes] + [0, sim.now])
    if el > tsim + 2.5: probs.append(f"slow: {el:.2f}s vs sim {tsim:.2f}s")
    return (seed, probs, el)

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--bin", required=True)
    ap.add_argument("--out", required=True, help="work dir: trees.jsonl, summary.json, failing trees")
    ap.add_argument("--trees", type=int, default=100)
    ap.add_argument("--seed", type=int, default=None)
    ap.add_argument("--jobs", type=int, default=3)
    a = ap.parse_args()
    W = os.path.abspath(a.bin)
    BASE = os.path.join(os.path.abspath(a.out), "trees"); TMP = os.path.join(os.path.abspath(a.out), "tmp")
    os.makedirs(BASE, exist_ok=True); os.makedirs(TMP, exist_ok=True)
    s0 = a.seed if a.seed is not None else random.randrange(1 << 30)
    print(f"seed {s0}", flush=True)
    from concurrent.futures import ThreadPoolExecutor
    bad = 0; done = 0; t0 = time.monotonic()
    jl = open(os.path.join(a.out, "trees.jsonl"), "a")
    with ThreadPoolExecutor(a.jobs) as ex:
        for res in ex.map(lambda k: run_one(k, s0 + k), range(a.trees)):
            if res is None: continue
            done += 1
            seed, probs, el = res
            jl.write(json.dumps(dict(seed=seed, secs=round(el, 2), probs=probs)) + "\n")
            if probs:
                bad += 1; print(f"FAIL seed={seed} ({el:.2f}s): " + " | ".join(probs[:4]), flush=True)
            else:
                shutil.rmtree(f"{BASE}/{seed}", ignore_errors=True)
    secs = round(time.monotonic() - t0, 1)
    json.dump(dict(seed=s0, trees=done, bad=bad, secs=secs), open(os.path.join(a.out, "summary.json"), "w"), indent=1)
    print(f"done {done} bad {bad} in {secs}s")
    sys.exit(1 if bad else 0)
