#!/usr/bin/env python3
# A bracketed paste is text [D:repl-bracketed-paste]: a block whose first
# line is a statement on its own lands as one entry and runs when Enter
# is pressed (Alt+Enter after a multi-line one, which composes
# [D:repl-compose]), the mode is on only while the editor reads, and a paste
# with no end marker ends where its input does.
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
ON, OFF = "\x1b[?2004h", "\x1b[?2004l"


def session(chunks, settle=0.8, path=None):
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        if path is not None:
            os.environ["PATH"] = path
        os.execv(WEIR, [WEIR])
    out = b""

    def pump(done, limit):
        nonlocal out
        end = time.time() + limit
        while not done() and time.time() < end:
            r, _, _ = select.select([fd], [], [], 0.05)
            if r:
                try:
                    chunk = os.read(fd, 65536)
                except OSError:
                    return
                if not chunk:
                    return
                out += chunk

    pump(lambda: b"weir> " in out, 20)
    for c in chunks:
        # a (text, marker) chunk waits for the marker to be painted first
        c, marker = c if isinstance(c, tuple) else (c, None)
        mark = len(out)
        os.write(fd, c.encode())
        if marker is not None:
            pump(lambda: marker.encode() in out[mark:], 30)
        pump(lambda: False, settle)
    try:
        os.kill(pid, signal.SIGKILL)
        os.waitpid(pid, 0)
    except (ProcessLookupError, ChildProcessError):
        pass
    os.close(fd)
    return out.decode(errors="replace")


def plain(s):
    return re.sub(r"\x1b\[[0-9;?]*[A-Za-z]", "", s).replace("\r", "")


def check(cond, what):
    if not cond:
        failures.append(what)


BLOCK = "xs\r    |> Seq.map (fun n -> n + 1)\r    |> Seq.length\r"

out = session(["let xs = [1; 2; 3]\r", "\x1b[200~" + BLOCK + "\x1b[201~"])
check("\n[1; 2; 3]\n" not in plain(out), "a pasted block's first line must not run on its own")
check("\n3\n" not in plain(out), "a paste must not submit itself")

out = session(["let xs = [1; 2; 3]\r", "\x1b[200~" + BLOCK + "\x1b[201~", "\x1b\r"])
check("\n3\n" in plain(out), "Alt+Enter after a pasted block runs the whole block")

out = session(["let xs = [1; 2; 3]\r", "\x1b[200~" + BLOCK + "\x1b[201~", "\r", "\r"])
check("\n3\n" in plain(out), "so does Enter on the empty line Enter adds")

out = session(["\x1b[200~1 + 2\r\x1b[201~", "\r"])
check("\n3\n" in plain(out), "a one-line paste runs on Enter")

# the end marker split across reads, a CRLF line end, a tab
out = session(["\x1b[200~let a = 1\r\nlet b =\ta + 1\x1b[2", "01~", "\x1b\r"])
check("\na = 1\nb = 2\n" in plain(out), "a paste whose end marker arrives in pieces is one entry")

# into the middle of a line
out = session(['print ""', "\x1b[D", "\x1b[200~hi\x1b[201~", "\r"])
check("\nhi\n" in plain(out), "a paste lands at the cursor")

# off before anything else owns the terminal, on again at the next prompt
out = session(["\x1b[200~sh -c 'echo ran'\x1b[201~", "\r"])
ran = out.rfind("ran\r\n")
check(ON in out and OFF in out[:ran] and out.rfind(OFF, 0, ran) > out.find(ON), "the mode is off while a command runs")
check(out.rfind(ON) > ran, "and on again at the next prompt")

# the history search reads the tty itself: off while it does (no fzf on
# PATH, so the built-in search runs), on again when the editor is back
out = session(["\x12", "\x1b"], path="/nonexistent")
search = out.find("(reverse-i-search)")
check(search > 0 and out.rfind(OFF, 0, search) > out.rfind(ON, 0, search), "the mode is off while the history search reads")
check(out.find(ON, search) > search, "and on again after the search")

out = session(["\x04"])
check(out.rstrip().endswith(OFF), "the mode is off when the session ends")

# no end marker: the paste ends where its input does — it is painted
# only then, so the Enter waits for that, not for a guessed delay
out = session([("\x1b[200~let z = 5", "let"), "\r", "z\r"])
check("\nz = 5\n" in plain(out) and "\n5\n" in plain(out), "a paste with no end marker still ends")

if failures:
    for f in failures:
        print("FAIL:", f)
    sys.exit(1)
print("repl-bracketed-paste: ok")
