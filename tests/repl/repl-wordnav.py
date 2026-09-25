#!/usr/bin/env python3
# REPL word-navigation probe: Ctrl+Left/Right hop word-wise.
# Runs the real binary under a pty and asserts on evaluated output, not
# on redraw escape sequences (those vary by terminal state).
import os
import pty
import sys
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
import sys as _sys
_sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

pid, fd = pty.fork()
if pid == 0:
    os.execv(WEIR, ["weir"])

def send(s, delay=0.15):
    os.write(fd, s.encode())
    time.sleep(delay)

time.sleep(0.8)
send("1 + 23")
send("\x1b[1;5D")  # Ctrl+Left: to the start of '23'
send("4")
send("\r", 0.5)    # 1 + 423 = 424 proves the hop landed
send("ab.cd")
send("\x1b[1;5D")  # to the start of 'cd' ('.' separates)
send("\x1b[1;5D")  # to the start of 'ab'
send("\x1b[1;5C")  # Ctrl+Right: back over 'ab'
send("9", 0.2)
send("\r", 0.5)    # ab9.cd proves both directions hop segment-wise
# mid-line Tab: the tail past the cursor must not join the completion
# word (the `{ Line = x. })` receipt) — type the full line, cursor back
# over the tail, complete `Seq.ma` -> Seq.map, Enter; the evaluated echo
# proves both the truncation and that insertion preserved the tail
send('["zz"] |> Seq.ma (fun s -> s) |> Seq.head')
for _ in range(25):
    send("\x1b[D", 0.02)  # Left over ' (fun s -> s) |> Seq.head'
send("\t", 0.3)
send("\r", 0.6)
# kill-ring [D:repl-killring]: Ctrl+W kills the previous word into the ring,
# Ctrl+Y yanks it — kill "KRX", yank twice -> echo KRXKRX (streams KRXKRX)
send("echo ")
send("KRX")
send("\x17")       # Ctrl+W: kill "KRX" into the ring
send("\x19")       # Ctrl+Y: yank
send("\x19")       # yank again -> KRXKRX
send("\r", 0.5)
# Ctrl+U feeds the same ring: type UKILL, ^U kills it to the ring, then a
# fresh `echo `, yanked twice -> echo UKILLUKILL (streams UKILLUKILL)
send("UKILL")
send("\x15")       # Ctrl+U: kill to line start into the ring
send("echo ")
send("\x19")
send("\x19")
send("\r", 0.5)
send("\x04")       # Ctrl+D
time.sleep(0.4)

out = b""
try:
    while True:
        chunk = os.read(fd, 65536)
        if not chunk:
            break
        out += chunk
except OSError:
    pass
os.waitpid(pid, 0)
# assertions are about text, not paint: strip color spans (the input
# echo colors as of PLAN-repl-color; content must be unchanged)
import re as _re
text = _re.sub(r"\x1b\[[0-9;]*m", "", out.decode(errors="replace"))

failures = []
if "424" not in text:
    failures.append("Ctrl+Left did not land before '23' (no 424 in output)")
if "ab9.cd" not in text:
    failures.append("Ctrl+Left x2 / Ctrl+Right did not hop segment-wise (no ab9.cd echo)")
if '"zz"' not in text:
    failures.append("mid-line Tab completion did not complete Seq.ma with a tail after the cursor (no zz echo)")
if "KRXKRX" not in text:
    failures.append("Ctrl+W did not kill a word into the ring / Ctrl+Y did not yank it (no KRXKRX)")
if "UKILLUKILL" not in text:
    failures.append("Ctrl+U did not feed the kill-ring / Ctrl+Y yank failed (no UKILLUKILL)")

if failures:
    print(text)
    for f in failures:
        print("repl-wordnav FAIL:", f)
    sys.exit(1)

print("repl-wordnav: word navigation + kill-ring (Ctrl+W/Ctrl+U → ring, Ctrl+Y yanks) hold")
