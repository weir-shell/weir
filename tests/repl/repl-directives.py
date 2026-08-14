#!/usr/bin/env python3
# Session directives [D:repl-directives]: '#' is the prefix for
# everything addressed to the tooling — #help's three forms from the
# one hover source, #quit (Ctrl+D still works), and comment-only
# lines as silent no-ops. The :q teaching arm retired 2026-08-14.
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

# --- #quit quits; :q is fully retired (user 2026-08-14) ---------------
t = piped("#quit\nprint \"unreached\"\n")
if "unreached" in t:
    failures.append("#quit must leave the REPL")

t = piped(":q\nprint \"still-here\"\n#quit\n")
if "`:q` is now" in t:
    failures.append("the :q teaching arm should be gone")
if "still-here" not in t:
    failures.append(":q (now an ordinary error) must not quit the session")

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

# --- the echo cap [D:echo-cap]: report / set / all / teach ------------
t = piped("#echo\n#echo 25\n#echo\n#echo all\n#echo\n#echo nope\n#echo 0\n#quit\n")
if "echo cap: 100" not in t:
    failures.append(f"#echo bare must report the default: {t[-200:]!r}")
if t.count("echo cap: 25") < 2:
    failures.append(f"#echo 25 must set AND the next bare #echo report it: {t[-300:]!r}")
if t.count("echo cap: all") < 2:
    failures.append(f"#echo all must uncap and report: {t[-300:]!r}")
if t.count("positive count or 'all'") != 2:
    failures.append(f"invalid arguments (nope, 0) must both teach: {t[-300:]!r}")

t = piped("#help\n#quit\n")
if "#echo" not in t:
    failures.append(f"#help must list #echo: {t[-300:]!r}")
if "hang" not in t:
    failures.append(f"#help's #echo line must carry the all-hangs warning: {t[-300:]!r}")

# --- piped bytes are UNMOVED by the session cap [D:echo-cap]: the piped
# echo keeps its pinned constant even after #echo changes the session's
t = piped('#echo 3\n[1; 2; 3; 4; 5; 6; 7; 8; 9; 10; 11; 12] |> Seq.map (fun x -> x)\n#quit\n')
if "first 10 of an unforced seq" not in t:
    failures.append(f"the piped echo must keep its pinned cap of 10: {t[-300:]!r}")

# --- the cap at a tty: default 100 covers command-sized output; #echo
# moves it live; all uncaps (the acceptance rides the lines form) ------
def pty_session(lines, settle=0.6):
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

    segs = []
    for l in lines:
        start = len(out)
        os.write(fd, (l + "\r").encode())
        drain(settle)
        segs.append(re.sub(r"\x1b\[[0-9;]*[A-Za-z]|\x1b=", "", out[start:].decode(errors="replace")))
    os.write(fd, b"\x04")
    deadline = time.time() + 10
    reaped = False
    while time.time() < deadline:
        r, _, _ = select.select([fd], [], [], 0.2)
        if r:
            try:
                out += os.read(fd, 65536)
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
        failures.append("the #echo pty session did not exit on ^D")
    return segs

segs = pty_session(
    [
        'let xs = [1..29] |> Seq.map (fun i -> $"l{i}")',
        "#echo 5",
        "xs",
        "#echo all",
        "xs",
    ]
)
# the acceptance: 29 unforced lines fit under the default cap — no
# Seq.force, no clip sentence ("l29" is a RESULT spelling; the typed
# line never contains it)
if "l29" not in segs[0] or "first" in segs[0]:
    failures.append(f"29 lines must echo whole under the default cap: {segs[0][-300:]!r}")
if "l5" not in segs[2] or "l6" in segs[2] or "first 5 of an unforced seq" not in segs[2]:
    failures.append(f"#echo 5 must clip the tty echo at 5 with the live-cap sentence: {segs[2][-300:]!r}")
if "l29" not in segs[4] or "first" in segs[4]:
    failures.append(f"#echo all must uncap the tty echo: {segs[4][-300:]!r}")

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

print("repl-directives: #help x3 (one source), #quit + Ctrl+D, :q retired, comments no-op, #echo cap (report/set/all/teach, tty live, piped pinned)")
