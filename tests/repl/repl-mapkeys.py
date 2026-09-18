#!/usr/bin/env python3
# Value-aware map-key completion [D:value-key-complete]: an open map's
# keys are data, so completion reads them from the SESSION's stored
# value — inside the open key literal of a pipe-form Map lookup whose
# receiver is a bare, already-materialized binding, Tab completes the
# value's own keys. The boundary is peek-vs-evaluate: a PIPELINE
# receiver would need evaluating, so it completes nothing — bind first,
# then the keys complete. This drives the real pty line editor: bind a
# pair-seq at the prompt, type the lookup prefix, Tab, read the painted
# line back. Fixture-free by design: the keys come from a literal typed
# at the prompt, nothing PATH- or filesystem-dependent.
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

BIND = 'let d = [("CoreCount", "4"); ("Zone", "b")]'


def pty_complete(setup, prefix, settle=0.6):
    # bind the receiver first (setup line), then type <prefix> and one
    # Tab; capture the paint of the completed line, then ^C + ^D.
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

    if setup:
        os.write(fd, (setup + "\r").encode())
        # a full drain: the bind's echo must land BEFORE the capture
        # starts, or its quoted keys would pollute the painted region
        drain(1.0)
    start = len(out)
    os.write(fd, prefix.encode())
    time.sleep(0.2)
    os.write(fd, b"\t")
    drain(settle)
    painted = out[start:]
    os.write(fd, b"\x03")  # abandon the line
    os.write(fd, b"\x04")  # leave
    time.sleep(0.3)
    try:
        while True:
            r, _, _ = select.select([fd], [], [], 0.2)
            if not r:
                break
            c = os.read(fd, 65536)
            if not c:
                break
            out += c
    except OSError:
        pass
    try:
        os.close(fd)
    except OSError:
        pass
    os.waitpid(pid, 0)
    return re.sub(r"\x1b\[[0-9;]*[A-Za-z]|\x1b=|\x1b\][^\x07]*\x07", "", painted.decode(errors="replace"))


# (1) a bound pair-seq's keys complete inside the lookup's key literal —
# the line the editor now holds is `d |> Map.tryGet "CoreCount`, and no
# closing quote is synthesized (the user owns the closer, the
# path-in-quotes convention)
t = pty_complete(BIND, 'd |> Map.tryGet "Cor')
if 'Map.tryGet "CoreCount' not in t:
    failures.append(f"a bound pair-seq's key must complete: {t[-400:]!r}")
if 'Map.tryGet "CoreCount"' in t:
    failures.append(f"no closing quote is synthesized: {t[-400:]!r}")

# (2) the bind-first law: a PIPELINE receiver would need evaluating, so
# Tab completes nothing — the typed prefix stays as typed
t = pty_complete(BIND, 'd |> Seq.map fst |> Map.tryGet "Cor')
if 'Map.tryGet "CoreCount' in t:
    failures.append(f"a pipeline receiver must stay silent: {t[-400:]!r}")

if failures:
    for f in failures:
        print("repl-mapkeys FAIL:", f)
    sys.exit(1)

print('repl-mapkeys: a bound pair-seq completes its key inside Map.tryGet "…, pipeline receiver stays silent (bind-first)')
