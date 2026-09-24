#!/usr/bin/env python3
# The FSI-parity `it` flow [D:repl-it] and the function-value echo
# [D:repl-fn-echo]: a bare command statement at a tty streams (the
# inherit path [D:colour-inherit]) and binds `it := ()` — the meta is
# the plain `: seq<string>` (the v0.0.43 streamed parenthetical
# reverted), `it` right after echoes `() : unit` with no error, and
# MISUSING the unit `it` appends the capture repair with the command
# verbatim. A `let` binds no `it` (FSI: `let o = 10;;`); the fresh
# session and the piped REPL are pinned unchanged. Named function
# values echo a mini-help: builtins the #help signature + glance,
# session functions their name, scheme, and recorded definition line.
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


# --- (a): the FSI-parity flow — a bare command streams, the meta is the
# PLAIN type (the 43 parenthetical reverted), and `it` right after
# echoes `() : unit`, no error ---
cmd = 'sh -c "echo STREAMED-OUT"'
out = session([(cmd.encode() + b"\r", 1.2), (b"it\r", 0.8)])
if "STREAMED-OUT" not in out:
    failures.append(f"(a) the child's bytes never reached the terminal: {out!r}")
if ": seq<string>" not in out:
    failures.append(f"(a) the streamed meta must show the plain type: {out!r}")
if "streamed —" in out or "not bound to 'it'" in out:
    failures.append(f"(a) the v0.0.43 parenthetical must be gone — the standing teach was noise: {out!r}")
if "() : unit" not in out:
    failures.append(f"(a) it after a streamed command must echo () : unit (it := (), FSI parity): {out!r}")
if "unbound variable" in out:
    failures.append(f"(a) it is BOUND after a streamed command — no unbound error: {out!r}")

# --- (b): misuse — the unit `it` where unit fails the check gets the
# located type error PLUS the capture repair with the command verbatim ---
out = session([(cmd.encode() + b"\r", 1.2), (b"it |> map Str.trim\r", 0.8)])
if f"to capture: let x = {cmd}" not in out:
    failures.append(f"(b) the misuse teach must carry the streamed command verbatim: {out!r}")
if "to capture (and bind 'it')" in out:
    failures.append(f"(b) the old teach spelling must be gone: {out!r}")

# --- (b2): #infer over the unit `it` is the same misuse — the adapter
# error appends the same repair ---
out = session([(cmd.encode() + b"\r", 1.2), (b"#infer it from yaml as Podz\r", 0.8)])
if "#infer: the source has type unit" not in out:
    failures.append(f"(b2) #infer must name the unit source: {out!r}")
if f"to capture: let x = {cmd}" not in out:
    failures.append(f"(b2) the #infer misuse must append the capture repair: {out!r}")

# --- (c): a `let` binds its NAME, never `it` — FSI parity (`let o = 10;;`
# binds no it); a fresh-session `it` after a let is the PLAIN unbound
# error, no repair ---
out = session([(b'let y = sh -c "echo CAP-OUT"\r', 1.2), (b"it\r", 0.8), (b"y\r", 0.8)])
if "CAP-OUT" not in out:
    failures.append(f"(c) the let capture lost its value: {out!r}")
if "unbound variable 'it'" not in out:
    failures.append(f"(c) a let must NOT bind it (FSI parity) — it stays unbound: {out!r}")
if "to capture:" in out:
    failures.append(f"(c) no streamed latch, no repair — the plain error stands: {out!r}")

# --- (d): fresh-session `it` keeps the ordinary unbound error ---
out = session([(b"it\r", 0.8)])
if "unbound variable 'it'" not in out:
    failures.append(f"(d) fresh-session it must still error unbound: {out!r}")
if "to capture:" in out:
    failures.append(f"(d) the repair must not fire without a prior streamed statement: {out!r}")

# --- (e): piped REPL — the bare command takes the VALUE path, binds
# `it`, and the byte surface is unmoved (no streamed note, no unit echo) ---
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
if "() : unit" in p.stdout:
    failures.append(f"(e) piped: unit stays invisible on the pinned byte surface: {p.stdout!r}")

# --- (f): a bare BUILTIN function echoes the #help mini-help — the
# qualified signature plus the doc's first line, one renderer ---
out = session([(b"Seq.map\r", 0.8)])
if "Seq.map (f: 'a -> 'b) (xs: seq<'a>) : seq<'b>" not in out:
    failures.append(f"(f) Seq.map must echo the #help signature: {out!r}")
if "Apply a function to every element, lazily." not in out:
    failures.append(f"(f) Seq.map must echo the doc's first line: {out!r}")
if "<builtin>" in out:
    failures.append(f"(f) the opaque <builtin> line must be gone for a named builtin: {out!r}")

# --- (f2): a bare ALIAS names its home ---
out = session([(b"find\r", 0.8)])
if "Seq.find (pred:" not in out:
    failures.append(f"(f2) the bare alias must echo its qualified home Seq.find: {out!r}")

# --- (g): a session-defined function echoes name : scheme + its
# recorded definition's first line; redefinition shows the LAST accepted ---
out = session(
    [
        (b"let f x = x + 1\r", 0.8),
        (b"f\r", 0.8),
        (b"let f x = x + 2\r", 0.8),
        (b"f\r", 0.8),
    ]
)
if "f : int -> int" not in out:
    failures.append(f"(g) the session function must echo name : scheme: {out!r}")
# the def line echoes beyond the keystroke echo: typed twice, shown twice more
if out.count("let f x = x + 1") < 2:
    failures.append(f"(g) the first echo must show the recorded definition line: {out!r}")
if out.count("let f x = x + 2") < 2:
    failures.append(f"(g) redefinition must show the LAST accepted definition: {out!r}")

# --- (h): a bare command that EXITS NONZERO renders a QUIET exit-code
# status (there is no $?, so the code must show), NOT the loud `error:` —
# the raise is caught on the inherit path and the session continues
# [D:repl-cmd-fail]. `it` after binds () like any streamed command.
out = session([(b'sh -c "echo OUT; exit 3"\r', 1.2), (b"it\r", 0.8), (b'print "after"\r', 0.8)])
if "OUT" not in out:
    failures.append(f"(h) the failing command's output must still stream: {out!r}")
if "exit 3" not in out:
    failures.append(f"(h) a nonzero exit must show its code (the $?-less REPL's only surface): {out!r}")
if "error: command failed" in out:
    failures.append(f"(h) a bare command failure must NOT be the loud error at a tty: {out!r}")
if "() : unit" not in out:
    failures.append(f"(h) it after a failed streamed command still binds () : unit: {out!r}")
if "after" not in out:
    failures.append(f"(h) the session must continue past a failed command: {out!r}")

# --- (h2): a VALUE-position command failure stays the LOUD error — there
# it aborted a computation, not a shell `$?` [D:repl-cmd-fail] ---
out = session([(b'let x = $(sh -c "exit 4")\r', 1.0)])
if "error: command failed with exit code 4" not in out:
    failures.append(f"(h2) a $() capture failure must keep the loud error (it broke a value): {out!r}")

if failures:
    for f in failures:
        print("FAIL:", f)
    sys.exit(1)
print("ok: it FSI-parity — streamed binds (), misuse teaches the capture, functions echo mini-help, failed command quiets to an exit-code status")
