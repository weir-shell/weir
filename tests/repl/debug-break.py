#!/usr/bin/env python3
# Debug.break () [D:debug-aids]: at a terminal the script stops at a
# prompt that sees the scope the break is written in — a parameter's
# value, a pattern's binds — echoes an expression with its type, keeps a
# `let` for the prompt's life, names a mistake without ending the
# script, and goes on at an empty line; #quit ends the script.
import os
import pty
import select
import sys
import tempfile
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

SCRIPT = """type Shape = Sq of int | Rect of int * int
let area s =
    match s with
    | Sq n -> n * n
    | Rect (a, b) ->
        Debug.break ()
        a * b
let total = [Sq 2; Rect (3, 4)] |> Seq.map area |> Seq.sum
print $"total {total}"
"""

failures = []


def session(lines):
    d = tempfile.mkdtemp()
    path = os.path.join(d, "b.weir")
    with open(path, "w") as f:
        f.write(SCRIPT)
    pid, fd = pty.fork()
    if pid == 0:
        os.chdir(d)
        os.execv(WEIR, [WEIR, "b.weir"])
    out = b""

    def pump(done, limit=30):
        nonlocal out
        end = time.time() + limit
        while not done() and time.time() < end:
            r, _, _ = select.select([fd], [], [], 0.2)
            if r:
                try:
                    chunk = os.read(fd, 4096)
                except OSError:
                    return
                if not chunk:
                    return
                out += chunk

    for line in lines:
        seen = out.count(b"break> ")
        pump(lambda: out.count(b"break> ") > 0 if seen == 0 else out.count(b"break> ") >= seen)
        os.write(fd, line.encode() + b"\n")
        pump(lambda: out.count(b"break> ") > seen, limit=15)
    pump(lambda: False, limit=3)
    _, status = os.waitpid(pid, 0)
    return out.decode(errors="replace").replace("\r\n", "\n"), os.waitstatus_to_exitcode(status)


text, code = session(["a + b", "let k = a * 10", "k + b", "#vars", "nope + 1", ""])
for needle, what in [
    ("break at b.weir:6", "the prompt names where the script stopped"),
    ("7 : int", "an expression over a pattern's binds echoes with its type"),
    ("34 : int", "a let lasts for the prompt"),
    ("a : int", "#vars lists a bind with its type"),
    ("s : Shape", "#vars lists the parameter"),
    ("unbound variable 'nope'", "a mistake is named"),
    ("total 16", "an empty line goes on and the script finishes"),
]:
    if needle not in text:
        failures.append(f"{what}: expected {needle!r} in {text[-600:]!r}")
if code != 0:
    failures.append(f"the resumed script must exit 0, got {code}")

text, code = session(["#quit"])
if "total" in text or code != 1:
    failures.append(f"#quit ends the script with exit 1: exit {code}, {text[-300:]!r}")

if failures:
    for f in failures:
        print("FAIL:", f)
    sys.exit(1)
print("debug-break: ok")
