#!/usr/bin/env python3
# The tool's own values on Tab [D:sig-dynamic-complete]: a fake cobra tool
# (dyfx) whose `__complete` answers, from a user-dir sig with `let dynamic
# = "cobra"`; a project sig asking for cobra (dyproj) that must never run
# its tool (a marker file stays absent) and hints once; git branches
# through `#alias co = git checkout` from a project sig with `let dynamic
# = "git"`; a hung `__complete` (Tab returns at the bound, no child left —
# by pid) and a key pressed while waiting (it lands in the buffer); a
# flooding `__complete` (capped, no child left). Waits on text, never
# fixed sleeps.
import os
import shutil
import subprocess
import sys
import tempfile
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "lib"))
from harness import assert_fresh
from ptysession import Repl, clean

assert_fresh(WEIR, os.path.join(HERE, "..", ".."))

root = tempfile.mkdtemp()
proj = os.path.join(root, "proj")
bindir = os.path.join(root, "bin")
cfg = os.path.join(root, "cfg")
pids = os.path.join(root, "pids")
mark = os.path.join(root, "mark")
for d in (proj, bindir, os.path.join(cfg, "weir", "sigs"), os.path.join(proj, ".weir", "sigs")):
    os.makedirs(d)

# one fake tool under two names; every run leaves a mark per name
tool = os.path.join(bindir, "dyfx")
open(tool, "w").write(
    "#!/bin/sh\n"
    'echo "$*" >> "$DYFX_MARK-${0##*/}"\n'
    '[ "$1" = __complete ] || exit 0\n'
    'case "$2" in\n'
    "    get) printf 'pod-alpha\\tRunning\\npod-beta\\tPending\\n:4\\n' ;;\n"
    '    hang) echo $$ >> "$DYFX_PIDS"; sleep 30 & echo $! >> "$DYFX_PIDS"; wait ;;\n'
    '    flood) echo $$ >> "$DYFX_PIDS"; while :; do echo floodfloodfloodflood; done ;;\n'
    "esac\n")
os.chmod(tool, 0o755)
os.symlink(tool, os.path.join(bindir, "dyproj"))

sig = ("let dynamic = \"cobra\"\n"
       "type Cmd =\n"
       "    | Get\n"
       "    | Hang\n"
       "    | Flood\n")
open(os.path.join(cfg, "weir", "sigs", "dyfx.weir"), "w").write("module Dyfx\n" + sig)
open(os.path.join(proj, ".weir", "sigs", "dyproj.weir"), "w").write("module Dyproj\n" + sig)
open(os.path.join(proj, ".weir", "sigs", "git.weir"), "w").write(
    "module Git\n"
    "let dynamic = \"git\"\n"
    "type CheckoutFlags = {\n"
    "    force: bool\n"
    "}\n"
    "type Cmd =\n"
    "    | Checkout of CheckoutFlags\n"
    "    | Commit\n")

genv = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM="1")
for args in (["init", "-q", "-b", "trunk"],
             ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "seed"],
             ["branch", "feature-zeta"], ["branch", "fix-omega"]):
    subprocess.run(["git"] + args, cwd=proj, env=genv, check=True)

env = {"XDG_CONFIG_HOME": cfg, "XDG_STATE_HOME": os.path.join(root, "state"),
       "DYFX_MARK": mark, "DYFX_PIDS": pids,
       "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1",
       "PATH": bindir + os.pathsep + os.environ.get("PATH", "")}


def alive(pid):
    # gone, or a zombie awaiting its reaper, is not alive
    try:
        stat = open(f"/proc/{pid}/stat").read()
    except OSError:
        return False
    return stat[stat.rindex(")") + 2] != "Z"


def read_pids(want):
    try:
        got = [l.strip() for l in open(pids) if l.strip()]
    except OSError:
        return []
    return got if len(got) >= want else []


failures = []
r = Repl(WEIR, cwd=proj, env=env)
try:
    r.prompt()

    def abandon():
        start = len(r.out)
        r.send("\x1b")
        r.prompt(start)

    def tab(keys, expect, why):
        start = len(r.out)
        r.send(keys)
        if r.wait_for(expect, start, timeout=10) is None:
            failures.append(f"{why}: {r.out[-300:]!r}")
        abandon()

    tab("dyfx get pod-a\t", r"dyfx get pod-alpha", "a user-dir cobra sig: the tool's candidate")
    tab("dyfx get \t", r"dyfx get pod-$", "the candidates' common prefix")

    # a project sig asking for cobra: the tool never runs, one hint
    start = len(r.out)
    r.send("dyproj get \t")
    if r.wait_for(r"needs a user-level sig: weir add sig --global dyproj", start, timeout=10) is None:
        failures.append(f"the project cobra sig hints: {r.out[-300:]!r}")
    abandon()
    start = len(r.out)
    r.send("dyproj get \t")
    r.wait_for(r"dyproj get", start, timeout=5)
    abandon()
    if clean(r.out[start:]).count("user-level sig") != 0:
        failures.append("the hint shows once per session per tool")
    if os.path.exists(mark + "-dyproj"):
        failures.append("a project sig with dynamic \"cobra\" ran its tool")

    # git branches through a session alias
    r.submit("#alias co = git checkout\r")
    tab("co feature\t", r"co feature-zeta", "co <TAB> offers a branch")
    start = len(r.out)
    r.send("co \t")
    if r.wait_for(r"feature-zeta.*fix-omega.*trunk", start, timeout=10) is None:
        failures.append(f"co <TAB> lists the branches: {r.out[-300:]!r}")
    abandon()

    # a hung __complete: Tab returns at the bound, no child is left
    for f in (pids,):
        if os.path.exists(f):
            os.remove(f)
    t0 = time.time()
    r.send("dyfx hang \t")
    got = []
    while time.time() - t0 < 5 and not got:
        got = read_pids(2)
        r._read(0.01)
    while time.time() - t0 < 5 and got and any(alive(p) for p in got):
        r._read(0.01)
    elapsed = time.time() - t0
    if not got:
        failures.append("the hung tool never started")
    elif any(alive(p) for p in got):
        failures.append(f"the hung tool's children outlived the bound: {got}")
    elif elapsed > 1.5:
        failures.append(f"Tab took {elapsed:.2f}s on a hung tool (bound 0.3s)")
    start = len(r.out)
    r.send("Z")
    if r.wait_for(r"hang Z", start, timeout=5) is None:
        failures.append(f"the editor responds after a hung Tab: {r.out[-300:]!r}")
    abandon()

    # a key while waiting: the child dies, the key lands in the buffer
    os.remove(pids)
    t0 = time.time()
    r.send("dyfx hang \t")
    got = []
    while time.time() - t0 < 5 and not got:
        got = read_pids(2)
        r._read(0.01)
    start = len(r.out)
    r.send("Q")
    if r.wait_for(r"hang Q", start, timeout=5) is None:
        failures.append(f"a key pressed while waiting is kept: {r.out[-300:]!r}")
    while time.time() - t0 < 5 and got and any(alive(p) for p in got):
        r._read(0.01)
    if not got or any(alive(p) for p in got):
        failures.append(f"a key kills the waiting tool: {got}")
    abandon()

    # a flooding __complete: capped, killed, the editor responsive
    os.remove(pids)
    t0 = time.time()
    r.send("dyfx flood \t")
    got = []
    while time.time() - t0 < 5 and not got:
        got = read_pids(1)
        r._read(0.01)
    while time.time() - t0 < 5 and got and any(alive(p) for p in got):
        r._read(0.01)
    if not got or any(alive(p) for p in got):
        failures.append(f"the flooding tool is killed: {got}")
    start = len(r.out)
    r.send("Z")
    if r.wait_for(r"flood Z", start, timeout=5) is None:
        failures.append(f"the editor responds after a flood: {r.out[-300:]!r}")
    abandon()
finally:
    r.close()
    shutil.rmtree(root, ignore_errors=True)

if failures:
    for f in failures:
        print("repl-sig-dynamic FAIL:", f)
    sys.exit(1)

print("repl-sig-dynamic: Tab asks a user-dir cobra tool and git (through an alias); a project cobra sig never runs its tool; hung and flooding tools are bounded, killed, and leave no child")
