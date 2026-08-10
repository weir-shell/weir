#!/usr/bin/env python3
# The waiting indicator [D:waiting-indicator]: draws on stderr at a tty
# during weir's OWN blocking work (after a 500ms grace), is erased
# before anything else prints, never appears for fast calls, and the
# piped byte surface does not move. Asserts on the pty stream.
import os
import pty
import re
import subprocess
import sys
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

SPINNER = re.compile("[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]")
ERASE = "\r\x1b[2K"


def run(lines, settle=0.4):
    import select
    pid, fd = pty.fork()
    if pid == 0:
        os.execv(WEIR, ["weir"])
    time.sleep(0.8)
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
                    return
    for l in lines:
        os.write(fd, l.encode())
        drain(settle)
    os.write(fd, b"\x04")
    # deadline-bounded: drain until the child is reaped — a hang FAILS
    # here instead of wedging the battery
    deadline = time.time() + 15
    reaped = False
    while time.time() < deadline:
        r, _, _ = select.select([fd], [], [], 0.2)
        if r:
            try:
                out += os.read(fd, 65536)
            except OSError:
                pass  # EIO: the slave closed — the reap below ends the loop
        done, _st = os.waitpid(pid, os.WNOHANG)
        if done:
            reaped = True
            break
    os.close(fd)
    if not reaped:
        os.kill(pid, 9)
        os.waitpid(pid, 0)
        print("waiting-indicator FAIL: the REPL did not exit on ^D")
        sys.exit(1)
    return out.decode(errors="replace")


failures = []

# a slow sleep: the spinner appears (grace passed) and is ERASED —
# after the erase sequence no spinner glyph remains on the stream tail
t = run(["Duration.sleep 1300ms\r"], settle=1.8)
if not SPINNER.search(t):
    failures.append(f"no spinner during a 1300ms sleep: {t[-200:]!r}")
if "sleeping" not in t:
    failures.append(f"the label must name the work: {t[-200:]!r}")
if ERASE not in t:
    failures.append(f"the indicator must erase its line: {t[-200:]!r}")
else:
    after = t[t.rindex(ERASE) + len(ERASE):]
    if SPINNER.search(after):
        failures.append(f"spinner bytes after the final erase: {after[-200:]!r}")

# a fast call: the 500ms grace keeps it SILENT
t2 = run(["Duration.sleep 120ms\r"], settle=0.7)
if SPINNER.search(t2) or "sleeping" in t2:
    failures.append(f"a fast call must stay silent: {t2[-200:]!r}")

# a child owning the terminal: NEVER drawn over — sleep here is the
# COREUTILS command (weir never shadows it), i.e. a spawned child
t3 = run(["sh -c \"sleep 0.9; echo child-done\"\r"], settle=1.5)
if SPINNER.search(t3):
    failures.append(f"indicator must never draw while a child owns the terminal: {t3[-200:]!r}")
if "child-done" not in t3:
    failures.append(f"the child run itself must succeed: {t3[-200:]!r}")

# piped: the byte surface does not move — no spinner, no erase, and
# stdout is byte-identical to the pre-indicator contract
p = subprocess.run(
    [WEIR], input="Duration.sleep 700ms\nprint \"after\"\n",
    capture_output=True, text=True, timeout=30)
if SPINNER.search(p.stdout) or ERASE in p.stdout:
    failures.append(f"piped stdout must carry no indicator bytes: {p.stdout[-200:]!r}")
if SPINNER.search(p.stderr) or "sleeping" in p.stderr:
    failures.append(f"redirected stderr must carry no indicator: {p.stderr[-200:]!r}")
if "after" not in p.stdout:
    failures.append(f"the piped session must still evaluate: {p.stdout[-200:]!r}")

if failures:
    for f in failures:
        print("waiting-indicator FAIL:", f)
    sys.exit(1)

print("waiting-indicator: draws after the grace, erases, silent when fast/piped/child-owned")
