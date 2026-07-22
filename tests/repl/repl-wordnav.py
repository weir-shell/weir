#!/usr/bin/env python3
# REPL word-navigation probe: Ctrl+Left/Right hop word-wise (2026-07-21).
# Runs the real binary under a pty and asserts on evaluated output, not
# on redraw escape sequences (those vary by terminal state).
import os
import pty
import sys
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
import sys as _sys
_sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

pid, fd = pty.fork()
if pid == 0:
    os.execv(WEIR, ["weir"])

def send(s, delay=0.15):
    os.write(fd, s.encode())
    time.sleep(delay)

time.sleep(0.8)
send("1 + 23")
send("\x1b[1;5D")  # Ctrl+Left: to the start of '23'
send("4")
send("\r", 0.5)    # 1 + 423 = 424 proves the hop landed
send("ab.cd")
send("\x1b[1;5D")  # to the start of 'cd' ('.' separates)
send("\x1b[1;5D")  # to the start of 'ab'
send("\x1b[1;5C")  # Ctrl+Right: back over 'ab'
send("9", 0.2)
send("\r", 0.5)    # ab9.cd proves both directions hop segment-wise
send("\x04")       # Ctrl+D
time.sleep(0.4)

out = b""
try:
    while True:
        chunk = os.read(fd, 65536)
        if not chunk:
            break
        out += chunk
except OSError:
    pass
os.waitpid(pid, 0)
text = out.decode(errors="replace")

failures = []
if "424" not in text:
    failures.append("Ctrl+Left did not land before '23' (no 424 in output)")
if "ab9.cd" not in text:
    failures.append("Ctrl+Left x2 / Ctrl+Right did not hop segment-wise (no ab9.cd echo)")

if failures:
    print(text)
    for f in failures:
        print("repl-wordnav FAIL:", f)
    sys.exit(1)

print("repl-wordnav: word navigation holds")
