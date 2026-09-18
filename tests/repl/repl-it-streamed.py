#!/usr/bin/env python3
# The streamed-statement `it` trap [D:repl-it]: a bare command statement
# at a tty takes the inherit path [D:colour-inherit] — the child writes
# the terminal, weir never holds the bytes — so `it` does NOT bind. The
# meta line must say so (not read like a value landed), and an unbound
# `it` right after must teach the `let` capture with the command
# verbatim. The let path, the fresh session, and the piped REPL are
# pinned unchanged.
import os
import pty
import re
import select
import subprocess
import sys
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []


def session(lines_with_settle):
    pid, fd = pty.fork()
    if pid == 0:
        os.execv(WEIR, ["weir"])
    time.sleep(1.0)
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
        failures.append("session did not exit within the deadline")
    return re.sub(r"\x1b\[[0-9;?]*[a-zA-Z]|\x1b[=>]", "", out.decode(errors="replace"))


STREAM_META = ": seq<string> (streamed — not bound to 'it'; let x = … captures)"
TEACH_1 = "unbound variable 'it' — the last command streamed to the terminal; weir never held its output"

# --- (a)+(b): the trap — a bare command streams, the meta says so, and
# `it` right after teaches the verbatim `let` capture ---
cmd = 'sh -c "echo STREAMED-OUT"'
out = session([(cmd.encode() + b"\r", 1.2), (b"it\r", 0.8)])
if "STREAMED-OUT" not in out:
    failures.append(f"(a) the child's bytes never reached the terminal: {out!r}")
if STREAM_META not in out:
    failures.append(f"(a) the streamed meta line must say 'it' did not bind: {out!r}")
if TEACH_1 not in out:
    failures.append(f"(b) the unbound-it teach line is missing: {out!r}")
if f"to capture (and bind 'it'): let x = {cmd}" not in out:
    failures.append(f"(b) the teach must carry the streamed command verbatim: {out!r}")
if "Did you mean" in out:
    failures.append(f"(b) the generic did-you-mean noise must not ride the teach: {out!r}")

# --- (c): the capturing spelling works and `it` binds — no teach ---
out = session([(b'let y = sh -c "echo CAP-OUT"\r', 1.2), (b"it\r", 0.8)])
if out.count("CAP-OUT") < 3:  # typed line's echo + let echo + it echo
    failures.append(f"(c) let-captured output must echo for the let AND for it: {out!r}")
if "unbound variable 'it'" in out or "streamed" in out:
    failures.append(f"(c) a let-bound command must bind it with no teach: {out!r}")

# --- (d): fresh-session `it` keeps the ordinary unbound error ---
out = session([(b"it\r", 0.8)])
if "unbound variable 'it'" not in out:
    failures.append(f"(d) fresh-session it must still error unbound: {out!r}")
if "streamed to the terminal" in out or "to capture (and bind 'it')" in out:
    failures.append(f"(d) the teach must not fire without a prior streamed statement: {out!r}")

# --- (e): piped REPL — the bare command takes the VALUE path, binds
# `it`, and the byte surface carries no streamed note ---
p = subprocess.run(
    [WEIR],
    input='sh -c "echo PIPED-OUT"\nit\n#quit\n',
    capture_output=True,
    text=True,
)
if p.stdout.count('["PIPED-OUT"] : seq<string>') != 2:
    failures.append(f"(e) piped: the bare command must bind it (value path, bytes unmoved): {p.stdout!r}")
if "streamed" in p.stdout or "streamed" in p.stderr:
    failures.append(f"(e) piped: no streamed note on the pinned byte surface: {p.stdout!r} {p.stderr!r}")

if failures:
    for f in failures:
        print("FAIL:", f)
    sys.exit(1)
print("ok: streamed-statement it — honest meta, targeted teach, let/fresh/piped unchanged")
