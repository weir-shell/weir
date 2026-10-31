#!/usr/bin/env python3
# Tab offers a keyed head's open options [D:head-key-complete], `stopped`
# under a within's block [D:until-stopped-complete], and a quoted module
# path after `import` [D:import-path-complete] — typed as entered, the
# completed entries run. Waits on text, never fixed sleeps.
import os
import sys
import tempfile

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
from ptysession import Repl, clean

assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

d = tempfile.mkdtemp()
os.mkdir(os.path.join(d, "lib"))
with open(os.path.join(d, "lib", "m.weir"), "w") as f:
    f.write("module\nlet x : int\nlet x = 41\n")

failures = []
r = Repl(WEIR, cwd=d)
try:
    r.prompt()

    # 1. the reported case: the loop head's key completes in place
    start = len(r.out)
    r.send("repeat int\t")
    if r.wait_for(r"repeat interval=", start) is None:
        failures.append(f"repeat int<Tab> must complete to interval=: {clean(r.out[start:])[-300:]!r}")
    r.send("\x1b")
    r.prompt(start)

    # 2. retry lists its keys, minus the given one
    start = len(r.out)
    r.send("retry attempts=2 \t")
    t = r.wait_for(r"delay=\s+timeout=", start)
    if t is None or "attempts=" in clean(r.out[start:]).rsplit("\n", 2)[-2]:
        failures.append(f"retry attempts=2 <Tab> must offer delay= and timeout= only: {clean(r.out[start:])[-300:]!r}")
    r.send("\x1b")
    r.prompt(start)

    # 3. until under a within's block completes to stopped
    start = len(r.out)
    r.send("within\r")
    r.send('    print "held"\r')
    r.send("\x15until sto\t")
    if r.wait_for(r"until stopped", start) is None:
        failures.append(f"until sto<Tab> must complete to stopped: {clean(r.out[start:])[-300:]!r}")
    r.send("\x1b")
    r.prompt(start)

    # 4. an import path completes quoted, and the completed line runs
    start = len(r.out)
    r.send("import li\t")
    if r.wait_for(r'import "lib/"', start) is None:
        failures.append(f"import li<Tab> must complete to a quoted dir: {clean(r.out[start:])[-300:]!r}")
    r.send("m\t")
    if r.wait_for(r'import "lib/m\.weir"', start) is None:
        failures.append(f"a second Tab must complete the .weir file: {clean(r.out[start:])[-300:]!r}")
    # the caret sits inside the closing quote; End leaves the literal
    t = r.submit("\x05 as M\r")
    t = r.submit("M.x + 1\r")
    if "\n42\n" not in t:
        failures.append(f"the completed import must run: {t[-300:]!r}")
finally:
    r.close()

if failures:
    for f in failures:
        print("repl-head-keys FAIL:", f)
    sys.exit(1)

print("repl-head-keys: Tab offers a head's open option keys, stopped under a within's block, a quoted module path after import")
