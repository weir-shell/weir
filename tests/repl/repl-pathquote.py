#!/usr/bin/env python3
# Expression-position path completion QUOTES [D:repl-path-quote]: a bare
# filesystem path is not a valid weir expression, so completing a path as
# a FUNCTION argument (File.read ./x) must yield a QUOTED string literal —
# File.read "./x" — which parses on Enter. A COMMAND-argv path (cat ./x)
# stays BARE. This drives the real pty line editor: type the prefix, Tab,
# then read the painted line back.
import os
import pty
import re
import select
import subprocess
import sys
import tempfile
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []

# a scratch dir with a single, unambiguous entry — one candidate means
# the first Tab completes the whole word (no list step to reason about)
work = tempfile.mkdtemp(prefix="weir-pathq-")
open(os.path.join(work, "somefile.txt"), "w").close()


def pty_complete(setup, prefix, settle=0.6):
    # cd into the scratch dir first (setup line), then type <prefix> and
    # one Tab; capture the paint of the completed line, then ^C + ^D.
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
        drain(0.4)
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


cd_line = 'cd "%s"' % work

# (1) after a function head the completion comes back QUOTED — the line
# the editor now holds is `File.read "./somefile.txt"`, which parses.
t = pty_complete(cd_line, "File.read ./some")
if 'File.read "./somefile.txt"' not in t:
    failures.append(f"expression-slot path must complete QUOTED: {t[-400:]!r}")

# (2) a command-argv path stays BARE — `cat ./somefile.txt`, no quotes
t = pty_complete(cd_line, "cat ./some")
if "cat ./somefile.txt" not in t:
    failures.append(f"command-argv path must stay BARE: {t[-400:]!r}")
if 'cat "./some' in t:
    failures.append(f"command-argv path must NOT be quoted: {t[-400:]!r}")

# (3) completing INSIDE an already-open quote does not double the quote:
# the entry lands within the string (no second opening quote, and no
# closing quote is synthesized — the user owns the closer, as before)
t = pty_complete(cd_line, 'File.read "./some')
if 'File.read "./somefile.txt' not in t:
    failures.append(f"an open quote must complete WITHIN it: {t[-400:]!r}")
if '""' in t:
    failures.append(f"an open quote must not be doubled: {t[-400:]!r}")

if failures:
    for f in failures:
        print("repl-pathquote FAIL:", f)
    sys.exit(1)

print("repl-pathquote: expression-slot path completes quoted (File.read \"./somefile.txt\"), command-argv stays bare, an open quote is not doubled")
