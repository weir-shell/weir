#!/usr/bin/env python3
# Session directives [D:repl-directives]: '#' is the prefix for
# everything addressed to the tooling — #help's three forms from the
# one hover source, #quit (Ctrl+D still works), the retired :q
# teaching, and comment-only lines as silent no-ops.
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


def piped(text):
    p = subprocess.run([WEIR], input=text, capture_output=True, text=True, timeout=30)
    return p.stdout


# --- #help's three forms (piped drives dispatch identically) ---------
t = piped("#help\n#quit\n")
if "Directives:" not in t or "#quit" not in t:
    failures.append(f"#help must list the directives: {t[-200:]!r}")
if "Modules:" not in t or "Seq" not in t or "Http" not in t:
    failures.append(f"#help must list the modules (derived): {t[-200:]!r}")

t = piped("#help Seq\n#quit\n")
if "collect" not in t or "members" not in t:
    failures.append(f"#help Seq must list the module's members: {t[-200:]!r}")

t = piped("#help Seq.collect\n#quit\n")
if "Seq.collect (f:" not in t:
    failures.append(f"#help member must render the annotated signature: {t[-200:]!r}")
if "flatten" not in t:
    failures.append(f"#help member must render the hover doc text: {t[-200:]!r}")

t = piped("#help Seq.colect\n#quit\n")
if "no member 'colect'" not in t or "collect" not in t:
    failures.append(f"a dotted typo must did-you-mean in its module: {t[-200:]!r}")

# --- #quit quits; a stale :q TEACHES and stays ------------------------
t = piped("#quit\nprint \"unreached\"\n")
if "unreached" in t:
    failures.append("#quit must leave the REPL")

t = piped(":q\nprint \"still-here\"\n#quit\n")
if "`:q` is now `#quit`" not in t:
    failures.append(f"a stale :q must teach its replacement: {t[-200:]!r}")
if "still-here" not in t:
    failures.append(":q must NOT quit (it only teaches)")

# --- unknown directive names the family ------------------------------
t = piped("#time\n#quit\n")
if "unknown directive '#time'" not in t:
    failures.append(f"an unknown directive must say so: {t[-200:]!r}")

# --- comment-only lines: silent no-ops; trailing comments work --------
t = piped("//just a comment\n/// a doc\nprint \"after\"\n#quit\n")
if "Expecting" in t or "error" in t:
    failures.append(f"comment-only lines must be silent no-ops: {t[-300:]!r}")
if "after" not in t:
    failures.append("the session must continue past comment-only lines")

t = piped("let x = 5 // trailing\nx\n#quit\n")
if "5 : int" not in t:
    failures.append(f"a trailing comment on a statement must evaluate: {t[-200:]!r}")

# --- Ctrl+D still leaves (the pty half) -------------------------------
pid, fd = pty.fork()
if pid == 0:
    os.execv(WEIR, ["weir"])
time.sleep(0.8)
os.write(fd, b"\x04")
deadline = time.time() + 10
reaped = False
while time.time() < deadline:
    r, _, _ = select.select([fd], [], [], 0.2)
    if r:
        try:
            os.read(fd, 4096)
        except OSError:
            pass
    done, _ = os.waitpid(pid, os.WNOHANG)
    if done:
        reaped = True
        break
os.close(fd)
if not reaped:
    os.kill(pid, 9)
    os.waitpid(pid, 0)
    failures.append("Ctrl+D must still leave the REPL")

if failures:
    for f in failures:
        print("repl-directives FAIL:", f)
    sys.exit(1)

print("repl-directives: #help x3 (one source), #quit + Ctrl+D, :q teaches, comments no-op")
