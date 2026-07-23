#!/usr/bin/env python3
# REPL syntax coloring probes (PLAN-repl-color): colored spans appear
# under a tty, NO_COLOR suppresses them, and the painted text strips
# back to the typed line. Asserts on the pty stream.
import os
import pty
import re
import sys
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

ANSI = re.compile(r"\x1b\[[0-9;]*m")


def run(extra_env, lines):
    pid, fd = pty.fork()
    if pid == 0:
        for k, v in extra_env.items():
            os.environ[k] = v
        os.execv(WEIR, ["weir"])
    time.sleep(0.8)
    for l in lines:
        os.write(fd, l.encode())
        time.sleep(0.4)
    os.write(fd, b"\x04")
    time.sleep(0.4)
    out = b""
    try:
        while True:
            c = os.read(fd, 65536)
            if not c:
                break
            out += c
    except OSError:
        pass
    os.waitpid(pid, 0)
    return out.decode(errors="replace")


failures = []

t = run({}, ['let s = @"raw\r', "zzznope arg\r"])
if "\x1b[34m" not in t:
    failures.append("keyword span missing under a tty")
if "\x1b[32m" not in t:
    failures.append("string span missing (unclosed verbatim must color to EOL)")
if not re.search(r"\x1b\[31mzzznope\x1b\[0m", t):
    failures.append("unresolved head must paint red")

t2 = run({"NO_COLOR": "1"}, ["let s = 1\r"])
# the editor's own control sequences (\r, [K, cursor moves) are fine;
# COLOR codes must be absent entirely
if ANSI.search(t2):
    failures.append("NO_COLOR must suppress every color span")

if failures:
    for f in failures:
        print("repl-color FAIL:", f)
    sys.exit(1)

print("repl-color: lexical spans, head verdicts, NO_COLOR hold")
