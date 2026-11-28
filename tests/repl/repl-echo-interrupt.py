#!/usr/bin/env python3
# The echo yields to ^C [D:echo-interrupt]: a forced seq echoes whole, and
# a ^C during that echo stops it between lines — for the lines form, the
# table and the one-line literal — says so, returns the prompt, and leaves
# the value bound (the echo interrupted is a `let`'s own).
import os
import pty
import re
import select
import signal
import sys
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []
N = 400000
STOPPED = "the echo stopped at ^C"


def plain(b):
    return re.sub(r"\x1b\[[0-9;?]*[A-Za-z]", "", b.decode(errors="replace")).replace("\r", "")


def case(label, setup, lead=None):
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.execv(WEIR, [WEIR])

    def pump(done, limit):
        out = b""
        end = time.time() + limit
        while time.time() < end:
            r, _, _ = select.select([fd], [], [], 0.05)
            if r:
                try:
                    chunk = os.read(fd, 1 << 20)
                except OSError:
                    break
                if not chunk:
                    break
                out += chunk
            if done(out):
                break
        return out

    pump(lambda o: b"weir> " in o, 20)
    # the let's own echo is the one interrupted: the binding must survive it
    if lead is None:
        os.write(fd, (setup + "\r").encode())
    else:
        # a composed entry whose last statement is the echoed one
        os.write(fd, (lead + "\x1b\r" + setup + "\x1b\r").encode())
    # the echo is under way: a slice of it has reached the terminal
    started = pump(lambda o: len(o) > 60000, 120)
    os.write(fd, b"\x03")
    rest = pump(lambda o: STOPPED in plain(o) and plain(o).rstrip().endswith("weir>"), 20)
    os.write(fd, b"Seq.length xs\r")
    after = pump(lambda o: str(N) in plain(o), 30)
    try:
        os.kill(pid, signal.SIGKILL)
        os.waitpid(pid, 0)
    except (ProcessLookupError, ChildProcessError):
        pass
    os.close(fd)

    if "exit 130" in plain(rest):
        failures.append(f"{label}: a stopped echo is no failure: {plain(rest)[-200:]!r}")
    if len(started) <= 60000:
        failures.append(f"{label}: the echo never got under way")
    if STOPPED not in plain(rest):
        failures.append(f"{label}: a ^C during the echo must stop it and say so: {plain(rest)[-200:]!r}")
    # the whole echo is tens of megabytes; a stopped one adds little
    if len(rest) > 4_000_000:
        failures.append(f"{label}: the echo kept printing after ^C ({len(rest)} bytes)")
    if str(N) not in plain(after):
        failures.append(f"{label}: the value must stay bound after the echo stops: {plain(after)[-200:]!r}")


case("lines", f'let xs = [1..{N}] |> Seq.map (fun i -> $"line {{i}} of the long echo") |> Seq.freeze')
case("table", f'let xs = [1..{N}] |> Seq.map (fun i -> (i, i * 2, $"row {{i}}")) |> Seq.freeze')
case("literal", f'let xs = [1..{N}] |> Seq.map (fun i -> i * 1000003) |> Seq.freeze')
case("last of two", f'let xs = [1..{N}] |> Seq.map (fun i -> $"line {{i}} of the long echo") |> Seq.freeze', lead="let y = 1")

if failures:
    for f in failures:
        print("FAIL:", f)
    sys.exit(1)
print("repl-echo-interrupt: ok")
