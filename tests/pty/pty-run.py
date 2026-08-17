#!/usr/bin/env python3
# The GENERAL pty instrument [D:stream-echo]: run one command under a
# pseudo-terminal, feed scripted input with delays, capture output with
# MILLISECOND timestamps relative to start. Built for the streaming-echo
# pins but deliberately general — the concurrency review parked C14
# (TTY contention) and the REPL SIGINT split for want of exactly this;
# keep scenario logic OUT of here and IN the caller.
#
#   pty-run.py <total-timeout-s> <cmd> [args...] < scenario
#
# scenario lines (stdin):  SLEEP <ms>  |  SEND <text\n escaped: \n \t \x03>
# output: "<ms> <chunk-repr>" per read chunk, then "EXIT <code>"
import os, pty, sys, time, select, signal

timeout = float(sys.argv[1])
cmd = sys.argv[2:]
steps = []
for line in sys.stdin.read().splitlines():
    if line.startswith("SLEEP "):
        steps.append(("sleep", int(line[6:])))
    elif line.startswith("SEND "):
        steps.append(("send", line[5:].encode().decode("unicode_escape").encode()))

pid, fd = pty.fork()
if pid == 0:
    os.execvp(cmd[0], cmd)

start = time.monotonic()
out = []

def pump(until):
    while time.monotonic() < until:
        r, _, _ = select.select([fd], [], [], 0.02)
        if r:
            try:
                chunk = os.read(fd, 65536)
            except OSError:
                return False
            if not chunk:
                return False
            out.append((int((time.monotonic() - start) * 1000), chunk))
    return True

alive = True
for kind, arg in steps:
    if not alive:
        break
    if kind == "sleep":
        alive = pump(time.monotonic() + arg / 1000.0)
    else:
        os.write(fd, arg)

deadline = time.monotonic() + timeout
while alive and time.monotonic() < deadline:
    alive = pump(time.monotonic() + 0.1)
    done, status = os.waitpid(pid, os.WNOHANG)
    if done:
        pump(time.monotonic() + 0.3)
        for ms, chunk in out:
            print(ms, repr(chunk))
        print("EXIT", os.waitstatus_to_exitcode(status))
        sys.exit(0)

try:
    os.kill(pid, signal.SIGKILL)
except ProcessLookupError:
    pass
os.waitpid(pid, 0)
for ms, chunk in out:
    print(ms, repr(chunk))
print("EXIT timeout")
