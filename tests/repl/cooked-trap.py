#!/usr/bin/env python3
# The cooked-terminal trap [D:repl-cooked-trap] + the one-enumeration
# echo [D:echo-once]: a bare command's child runs ONCE per echo (the
# table probe and the line rendering used to enumerate the lazy seq
# twice), and after a slow child the next Enter still SUBMITS (the
# second child run's cooked-tty window used to swallow it as '\n').
import os
import pty
import re
import select
import sys
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []


def session(lines_with_settle, tail=1.0):
    pid, fd = pty.fork()
    if pid == 0:
        os.execv(WEIR, ["weir"])
    time.sleep(1.2)
    out = b""

    def drain(t):
        nonlocal out
        deadline = time.time() + t
        while time.time() < deadline:
            r, _, _ = select.select([fd], [], [], 0.1)
            if r:
                try:
                    out += os.read(fd, 65536)
                except OSError:
                    pass
    for line, settle in lines_with_settle:
        os.write(fd, line)
        drain(settle)
    os.write(fd, b"\x04")
    # deadline-bounded reap — a hang FAILS instead of wedging the battery
    deadline = time.time() + 15
    reaped = False
    while time.time() < deadline:
        r, _, _ = select.select([fd], [], [], 0.2)
        if r:
            try:
                out += os.read(fd, 65536)
            except OSError:
                pass
        done, _st = os.waitpid(pid, os.WNOHANG)
        if done:
            reaped = True
            break
    os.close(fd)
    if not reaped:
        os.kill(pid, 9)
        os.waitpid(pid, 0)
    plain = re.sub(r"\x1b\[[0-9;?]*[a-zA-Z]|\x1b=", "", out.decode(errors="replace"))
    return plain, reaped


# 1. one enumeration per echo: the child's side effect happens ONCE
marker = f"/tmp/weir-once-{os.getpid()}.log"
plain, reaped = session([(f'sh -c "echo run >> {marker}; echo out"\r'.encode(), 1.2)])
runs = len(open(marker).readlines()) if os.path.exists(marker) else 0
if os.path.exists(marker):
    os.remove(marker)
if runs != 1:
    failures.append(f"a bare command's child must run exactly once per echo, ran {runs}")
if not reaped:
    failures.append("session 1 did not exit on ^D")

# 2. the trap scenario: type the next line SOON after a slow child —
# inside what used to be the second run's cooked window — and it must
# still evaluate and the REPL must still exit on ^D
plain, reaped = session([
    (b'sh -c "sleep 0.9; echo child-done"\r', 1.8),
    (b'let healed = 111 * 3\r', 0.8),
])
if "333" not in plain:
    failures.append(f"the post-child line must evaluate: {plain[-200:]!r}")
if "  ... " in plain:
    failures.append(f"no phantom continuation after a slow child: {plain[-200:]!r}")
if not reaped:
    failures.append("the REPL must exit on ^D after a slow child")

if failures:
    for f in failures:
        print("cooked-trap FAIL:", f)
    sys.exit(1)

print("cooked-trap: one child run per echo, Enter survives a slow child, ^D exits")
