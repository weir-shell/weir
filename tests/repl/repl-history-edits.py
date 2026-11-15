#!/usr/bin/env python3
# REPL history-edit probe [D:repl-history-edits] [D:repl-interrupt-compute]:
# an entry edited while browsing history keeps its edit when the cursor
# leaves it and comes back, and ^C stops a computation that never waits.
# Runs the real binary under a pty and asserts on evaluated output.
import os
import pty
import re
import select
import sys
import tempfile
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

d = tempfile.mkdtemp(prefix="weir-histedit-")
pid, fd = pty.fork()
if pid == 0:
    os.environ.update({"XDG_STATE_HOME": d + "/state", "XDG_CONFIG_HOME": d + "/cfg"})
    os.execv(WEIR, ["weir"])

out = b""

def pump(secs):
    global out
    end = time.time() + secs
    while time.time() < end:
        r, _, _ = select.select([fd], [], [], 0.05)
        if r:
            try:
                out += os.read(fd, 65536)
            except OSError:
                return

def send(s, delay=0.4):
    os.write(fd, s if isinstance(s, bytes) else s.encode())
    pump(delay)

UP, DOWN = "\x1b[A", "\x1b[B"
pump(1.0)
send("111\r")
send("222\r")
# recall 222, edit it, leave upward, come back: the edit is still there
send(UP)
send("9")
send(UP)
send(DOWN)
send("\r", 0.6)        # 2229
# history is now 111, 222, 2229: edit 222 two entries up, leave downward
# past the new entry, come back
send(UP)
send(UP)
send("7")
send(DOWN)
send(DOWN)
send(UP)
send(UP)
send("\r", 0.6)        # 2227
# a new entry's text survives a trip into history too
send("333")
send(UP)
send(DOWN)
send("4\r", 0.6)       # 3334
# ^C stops a computation that never reaches a wait
send("Seq.initInfinite (fun i -> i) |> Seq.length\r", 1.0)
send(b"\x03", 1.5)
send("5550 + 5\r", 0.8)
send(b"\x04", 0.5)

done = False
for _ in range(40):
    try:
        p, _ = os.waitpid(pid, os.WNOHANG)
    except ChildProcessError:
        p = pid
    if p != 0:
        done = True
        break
    time.sleep(0.1)

if not done:
    os.kill(pid, 9)
    os.waitpid(pid, 0)

text = re.sub(r"\x1b\[[0-9;?]*[A-Za-z]", "", out.decode(errors="replace")).replace("\r", "")
answers = [l.strip() for l in text.split("\n") if l.strip() and not l.lstrip().startswith("weir>")]

failures = []
if not done:
    failures.append("the REPL did not exit on ^D — the computation was still running after ^C")
for want, why in [
    ("2229", "an edited recalled entry lost its edit after Up then Down"),
    ("2227", "an edited recalled entry lost its edit after leaving it downward and returning"),
    ("3334", "the new entry's text was lost after a trip into history"),
    ("5555", "the prompt did not come back after ^C on a computation"),
]:
    if not any(a.startswith(want) for a in answers):
        failures.append(f"{why} (no {want} in the answers)")

if failures:
    print(text)
    for f in failures:
        print("repl-history-edits FAIL:", f)
    sys.exit(1)

print("repl-history-edits: edits survive history browsing; ^C stops a computation")
