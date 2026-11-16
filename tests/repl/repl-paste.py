#!/usr/bin/env python3
# A paste paints once [D:repl-paste-repaint]: a long line fed in one
# write must cost output in proportion to its length, not its square,
# and must still be on screen and evaluate whole.
import os
import pty
import select
import signal
import sys
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []


def paste(n):
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.execv(WEIR, [WEIR])
    out = b""

    def pump(done, limit):
        nonlocal out
        end = time.time() + limit
        while not done() and time.time() < end:
            r, _, _ = select.select([fd], [], [], 0.1)
            if r:
                try:
                    chunk = os.read(fd, 65536)
                except OSError:
                    return
                if not chunk:
                    return
                out += chunk

    pump(lambda: b"weir> " in out, 20)
    before = len(out)
    line = b'Str.length "' + b"a" * n + b'"'
    sent = 0
    while sent < len(line):
        _, w, _ = select.select([], [fd], [], 5)
        if not w:
            break
        sent += os.write(fd, line[sent : sent + 1024])
        pump(lambda: False, 0.02)
    pump(lambda: False, 1.5)
    os.write(fd, b"\r")
    pump(lambda: b"\n" + str(n).encode() in out.replace(b"\r", b""), 30)
    emitted = len(out) - before
    answered = b"\n" + str(n).encode() in out.replace(b"\r", b"")
    # a REPL still repainting would block on the terminal: do not wait on it
    try:
        os.kill(pid, signal.SIGKILL)
        os.waitpid(pid, 0)
    except (ProcessLookupError, ChildProcessError):
        pass
    os.close(fd)
    return emitted, answered


for n in (2000, 8000):
    if os.environ.get("PASTE_REPORT"):
        print(n, *paste(n))
        continue
    emitted, answered = paste(n)
    if not answered:
        failures.append(f"a {n}-character pasted line must evaluate whole")
    # the old editor wrote ~n*n/2 bytes (2.2 MB at 2000, 33 MB at 8000)
    if emitted > 200 * n:
        failures.append(f"a {n}-character paste wrote {emitted} bytes — more than 200 per character")

if failures:
    for f in failures:
        print("FAIL:", f)
    sys.exit(1)
print("repl-paste: ok")
