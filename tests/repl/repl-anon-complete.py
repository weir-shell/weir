#!/usr/bin/env python3
# Field completion on an anonymous record [D:anon-literals]: a value bound
# from `{| … |}` on the previous line completes its fields at once, as a
# declared record does. Asserts on the evaluated output; waits on the
# prompt, never fixed sleeps.
import os
import sys

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
from ptysession import Repl

assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []
r = Repl(WEIR)
try:
    r.prompt()
    r.submit('let r = {| zname = "xq"; zport = 1 |}\r')
    start = len(r.out)
    r.send("r.zn\t")
    if r.wait_for(r"r\.zname", start) is None:
        failures.append(f"r.zn<Tab> must complete the anonymous field: {r.out[-300:]!r}")
    t = r.submit(" |> Str.toUpper |> print\r")
    if "XQ" not in [l.strip() for l in t.split("\n")]:
        failures.append(f"the completed line must run: {t[-300:]!r}")
finally:
    r.close()

if failures:
    for f in failures:
        print("repl-anon-complete FAIL:", f)
    sys.exit(1)

print("repl-anon-complete: an anonymous record's fields complete on the next line")
