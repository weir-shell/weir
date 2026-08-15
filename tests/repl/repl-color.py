#!/usr/bin/env python3
# REPL syntax coloring probes (PLAN-repl-color): colored spans appear
# under a tty, NO_COLOR suppresses them, and the painted text strips
# back to the typed line. Asserts on the pty stream.
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

ANSI = re.compile(r"\x1b\[[0-9;]*m")


def run(extra_env, lines):
    pid, fd = pty.fork()
    if pid == 0:
        for k, v in extra_env.items():
            os.environ[k] = v
        os.execv(WEIR, ["weir"])
    time.sleep(0.8)
    for l in lines:
        os.write(fd, l.encode())
        time.sleep(0.4)
    os.write(fd, b"\x04")
    time.sleep(0.4)
    out = b""
    try:
        while True:
            c = os.read(fd, 65536)
            if not c:
                break
            out += c
    except OSError:
        pass
    os.waitpid(pid, 0)
    return out.decode(errors="replace")


failures = []

t = run({}, ['let s = @"raw\r', "zzznope arg\r"])
if "\x1b[34m" not in t:
    failures.append("keyword span missing under a tty")
if "\x1b[32m" not in t:
    failures.append("string span missing (unclosed verbatim must color to EOL)")
if not re.search(r"\x1b\[31mzzznope\x1b\[0m", t):
    failures.append("unresolved head must paint red")

# the yaml district marker tints like the `!` markers [D:yaml-district];
# the `to yaml` adapter must NOT (the classifier is shared, not a second one)
# the marker line opens the multiline buffer (Enter-completeness), so
# cancel with Ctrl+C — Ctrl+D is EOF only at an EMPTY buffer
t3 = run({}, ["let d = yaml\r", "\x03"])
if not re.search(r"\x1b\[36myaml\x1b\[0m", t3):
    failures.append("line-end yaml marker must tint cyan")
t5 = run({}, ["let d = yaml schema=x\r", "\x03"])
if not re.search(r"\x1b\[36myaml schema=x\x1b\[0m", t5):
    failures.append("the schema-bearing marker must tint whole [D:yaml-schemas]")
t4 = run({}, ["x |> to yaml\r"])
if re.search(r"\x1b\[36myaml\x1b\[0m", t4):
    failures.append("`to yaml` adapter must NOT tint as a marker")

# the TRIPLE pin [D:windows-s3]: a leading-space line (a) EXECUTES,
# (b) COMPLETES on the first Enter (no continuation prompt), and
# (c) still paints its head verdict — the session-2 pair pin let (c)
# regress because the colorizer was the dedent's unenumerated third
# consumer
t6 = run({}, ["  echo tri-out\r"])
plain6 = re.sub(r"\x1b\[[0-9;?]*[a-zA-Z]|\x1b=", "", t6)
# the echoed VALUE — only execution produces it (the lines form
# [D:echo-lines]: a string seq presents as its lines + the type footer)
if "\ntri-out" not in plain6 or ": seq<string>" not in plain6:
    failures.append(f"leading-space line must execute: {plain6[-200:]!r}")
if "  ... " in plain6:
    failures.append("leading-space line must complete, not open a buffer")
if not re.search(r"\x1b\[1;34mecho\x1b\[0m", t6):  # the known-head paint (bold+blue)
    failures.append("leading-space head must keep its verdict paint")

# the within KIND paints as part of the form [D:within-kinds]: `within`
# is a keyword (blue), and `cd`/`tmp`/`env` after it are the SAME blue —
# not an identifier's colour. The block opens a multiline buffer, so
# cancel with Ctrl+C.
t7 = run({}, ["within cd helperDir\r", "\x03"])
if not re.search(r"\x1b\[34mwithin\x1b\[0m", t7):
    failures.append("within keyword must paint blue")
if not re.search(r"\x1b\[34mcd\x1b\[0m", t7):
    failures.append("the within KIND must paint as the form, not an identifier")

# Log.* at the prompt: stderr interleaves after evaluation, the next
# prompt still renders (the harness completing IS the no-corruption
# check) [D:log-module]
t6 = run({}, ['Log.info "ping"\r', "let z = 1\r"])
if "INFO" not in t6 or "ping" not in t6:
    failures.append("Log.info must reach the REPL stream")

# TERM=dumb suppresses color like NO_COLOR does [D:colored-diagnostics]
# — the walk found only the NO_COLOR half pinned
t8 = run({"TERM": "dumb"}, ["let s = 1\r"])
if ANSI.search(t8):
    failures.append("TERM=dumb must suppress every color span")

# `weir check` REPORTS on stdout [D:colored-diagnostics]: diagnostics
# ARE its output — colored when stdout is a tty, plain when redirected
# (capture-safe). The runner's errors go to stderr; that is a different
# command's law. The previous pin asserted colored-stderr and passed by
# matching .NET's terminfo INIT noise (ESC[?1h ESC=) on the pty — with
# TERM unset (CI) the init vanishes and so did the pin's evidence.
import tempfile as _tf
_d = _tf.mkdtemp()
open(_d + "/bad.weir", "w").write("let Foo = 1\n")

def run_check(argv_path, keep_fd, redirect_fd, outfile):
    # keep keep_fd on the pty; send redirect_fd to a file
    pid, fd = pty.fork()
    if pid == 0:
        o = os.open(outfile, os.O_WRONLY | os.O_CREAT | os.O_TRUNC)
        os.dup2(o, redirect_fd)
        os.execv(WEIR, ["weir", "check", argv_path])
    out = b""
    deadline = time.time() + 30
    status = None
    while time.time() < deadline:
        r, _, _ = select.select([fd], [], [], 0.25)
        if r:
            try:
                out += os.read(fd, 65536)
            except OSError:
                break  # EIO: child gone and buffer drained
        else:
            done, st = os.waitpid(pid, os.WNOHANG)
            if done:
                status = st
                break
    os.close(fd)
    if status is None:
        try:
            os.waitpid(pid, 0)
        except ChildProcessError:
            pass
    return out.decode(errors="replace"), open(outfile).read()

# tty stdout: the diagnostic arrives THERE, colored; stderr carries none
pty_out, err_file = run_check(_d + "/bad.weir", 1, 2, _d + "/err.txt")
if "casing-law" not in pty_out or "\x1b[" not in pty_out:
    failures.append(
        f"check on a tty must color its stdout diagnostic: {pty_out[-200:]!r}")
if "casing-law" in err_file:
    failures.append(f"stderr must carry no diagnostic: {err_file[-200:]!r}")

# redirected stdout: the diagnostic is PLAIN in the capture (pipe-safe)
_pty_err, out_file = run_check(_d + "/bad.weir", 2, 1, _d + "/out.txt")
if "casing-law" not in out_file or "\x1b[" in out_file:
    failures.append(
        f"a redirected check must capture the plain diagnostic: {out_file[-200:]!r}")

t2 = run({"NO_COLOR": "1"}, ["let s = 1\r"])
# the editor's own control sequences (\r, [K, cursor moves) are fine;
# COLOR codes must be absent entirely
if ANSI.search(t2):
    failures.append("NO_COLOR must suppress every color span")

# --- the table's tint [D:table-polish]: header bold, rule dim; cells
# stay untinted data; NO_COLOR strips the whole dressing ---------------
import tempfile as _tf
_td = _tf.mkdtemp()
open(_td + "/a.txt", "w").write("x")
tt = run({}, ["cd \"%s\"\r" % _td, "ls\r"])
if "\x1b[1m" not in tt or "\x1b[2m" not in tt:
    failures.append(f"the tty table must bold its header and dim its rule: {tt[-300:]!r}")
tt2 = run({"NO_COLOR": "1"}, ["cd \"%s\"\r" % _td, "ls\r"])
if "\u2500" not in tt2:
    failures.append(f"NO_COLOR must still tabulate: {tt2[-300:]!r}")
if ANSI.search(tt2):
    failures.append("NO_COLOR must strip the table's dressing too")

if failures:
    for f in failures:
        print("repl-color FAIL:", f)
    sys.exit(1)

print("repl-color: lexical spans, head verdicts, NO_COLOR+TERM=dumb hold, check reports on stdout (tty-colored, redirect-plain), table dressing (bold header/dim rule, NO_COLOR plain)")
