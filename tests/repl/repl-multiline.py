#!/usr/bin/env python3
# Multiline REPL editing pins [D:repl-multiline]: Enter-completeness (grow
# when incomplete, submit when the parser says complete), Up/Down within the
# buffer (history only from the first line), Ctrl+J force-newline, multiline
# history entries (encoded storage, whole-entry recall), the fzf display
# form (one line per entry, ⏎-joined) mapped back to the full entry, Esc
# abandon, and wrap math at TWO terminal widths. Assertions ride evaluated
# OUTPUT, never cursor positions (the driver lesson — drivers lie about
# cursors; values do not).
import os, pty, sys, time, select, re, tempfile, fcntl, struct, termios

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []

def run(keys, cols=80, seed=None, path=None):
    d = tempfile.mkdtemp()
    if seed is not None:
        os.makedirs(d + "/state/weir", exist_ok=True)
        open(d + "/state/weir/history", "w").write(seed)
    env = dict(os.environ)
    env.update({"XDG_STATE_HOME": d + "/state", "XDG_CONFIG_HOME": d + "/cfg",
                "PATH": path or "/usr/bin:/bin"})
    pid, fd = pty.fork()
    if pid == 0:
        os.execve(WEIR, ["weir"], env)
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 24, cols, 0, 0))
    time.sleep(0.8)
    for s, dl in keys:
        os.write(fd, s.encode()); time.sleep(dl)
    out = b""
    for _ in range(50):
        r, _, _ = select.select([fd], [], [], 0.1)
        if r:
            try:
                out += os.read(fd, 4096)
            except OSError:
                break
    return re.sub(r"\x1b\[[0-9;?]*[a-zA-Z]", "", out.decode(errors="replace")), d

# --- 1. Enter grows on incomplete, submits on complete (the parser's answer)
t, _ = run([("match 1 with\r", 0.4), ("| _ -> 9\r", 0.6), (":q\r", 0.3)])
if "9 : int" not in t:
    failures.append(f"Enter-incomplete must grow, then submit when complete: {t[-300:]!r}")

# --- 2. Ctrl+J forces newlines even when complete; Up moves WITHIN the
# buffer; a line above the cursor edits; Enter submits the whole statement
keys = [("match 1 with", 0.3), ("\n", 0.2),
        ('| 2 -> "two"', 0.3), ("\n", 0.2),
        ('| _ -> "no"', 0.3),
        ("\x1b[A", 0.2), ("\x1b[A", 0.2),   # Up x2 -> first line
        ("\x1b[F", 0.2),                     # End
        ("\x1b[D" * 5, 0.3),                 # Left x5 -> after the scrutinee
        ("\x7f", 0.2), ("2", 0.2),           # 1 -> 2
        ("\r", 0.6), (":q\r", 0.3)]
t, _ = run(keys)
if '"two" : string' not in t:
    failures.append(f"Up-within-buffer edit of the scrutinee must change the result: {t[-300:]!r}")

# --- 3. both ways, identical meaning: the same lines as a SCRIPT
d3 = tempfile.mkdtemp()
open(d3 + "/m.weir", "w").write('print (match 2 with\n| 2 -> "two"\n| _ -> "no")\n')
import subprocess
sc = subprocess.run([WEIR, d3 + "/m.weir"], capture_output=True, text=True)
if sc.stdout.strip() != "two":
    failures.append(f"script twin must print two: {sc.stdout!r} {sc.stderr!r}")
# (the REPL evaluated "two" in pin 2 — same statement text, same value)

# --- 4. a multiline history entry recalls WHOLE via Up at the first line
t, _ = run([("\x1b[A", 0.4), ("\r", 0.6), ("mm + 1\r", 0.5), (":q\r", 0.3)],
           seed="let mm =\\n    41\n")
if "mm : int = 41" not in t or "42 : int" not in t:
    failures.append(f"multiline recall must return the whole block-let: {t[-300:]!r}")

# --- 5. the fzf feed is the one-line DISPLAY form, mapped back to the entry
d5 = tempfile.mkdtemp()
os.makedirs(d5 + "/bin")
with open(d5 + "/bin/fzf", "w") as f:
    f.write('#!/bin/sh\ntee "$(dirname "$0")/fed.txt" | head -1\n')
os.chmod(d5 + "/bin/fzf", 0o755)
t, _ = run([("\x12", 0.6), ("\r", 0.6), (":q\r", 0.3)],
           seed="let zz =\\n    7\n",
           path=d5 + "/bin:/usr/bin:/bin")
fed = open(d5 + "/bin/fed.txt").read() if os.path.exists(d5 + "/bin/fed.txt") else ""
if "let zz = ⏎     7" not in fed:
    failures.append(f"fzf must be fed the one-line display form: {fed!r}")
if "zz : int = 7" not in t:
    failures.append(f"the display selection must map back to the FULL entry: {t[-300:]!r}")

# --- 6. Esc abandons the whole buffer
t, _ = run([("let broken = (", 0.3), ("\x1b", 0.5), ("5 + 5\r", 0.5), (":q\r", 0.3)])
if "10 : int" not in t or "error" in t:
    failures.append(f"Esc must abandon the buffer cleanly: {t[-300:]!r}")

# --- 7. wrap math at two widths (the resize class made cheap): a line
# longer than the narrow terminal edits and evaluates identically
for cols in (30, 80):
    t, _ = run([("1 + 2 + 3 + 4 + 5 + 6 + 7 + 8 + 9 + 10", 0.4),
                ("\x1b[D" * 4, 0.3),        # Left through the wrap boundary
                ("\x1b[F", 0.2),            # End again
                ("\r", 0.6), (":q\r", 0.3)], cols=cols)
    if "55 : int" not in t:
        failures.append(f"wrapped line at width {cols} must edit and evaluate: {t[-300:]!r}")

# --- 8. an UNCLOSED STRING submits (weir strings are single-line — more
# input can never fix it; growing would trap the user; found when the
# repl-color probe hung on `let s = @"raw` + Enter)
t, _ = run([('let s = @"raw\r', 0.5), ("7 * 7\r", 0.5), (":q\r", 0.3)])
if "49 : int" not in t:
    failures.append(f"an unclosed string must submit (error) and free the prompt: {t[-300:]!r}")

# --- 9a. a leading-space FIRST line has no statement above to continue —
# it dedents and EXECUTES (the Windows runbook's find; a Linux regression
# from the multiline session's adoption of the script assembler)
# [D:windows-s2]
t, _ = run([("  1 + 1\r", 0.5), (":q\r", 0.3)])
if "2 : int" not in t:
    failures.append(f"a leading-space first line must execute: {t[-300:]!r}")

# --- 9b. the OTHER half: an indented buffer dedents WHOLE (relative
# structure preserved), and an indented line inside the open buffer
# still continues it
t, _ = run([("  match 1 with\r", 0.4), ("  | _ -> 8\r", 0.6), (":q\r", 0.3)])
if "8 : int" not in t:
    failures.append(f"an indented entry must dedent whole and still assemble: {t[-300:]!r}")

# --- 9c. blank-line ESCAPE: Enter on an empty final line closes a
# PENDING buffer (the error shows, the prompt frees) — the general
# protection against every uncompletable state [D:windows-s2]
t, _ = run([("match 1 with\r", 0.4), ("\r", 0.5), ("5 + 5\r", 0.5), (":q\r", 0.3)])
if "10 : int" not in t:
    failures.append(f"a blank Enter must close a pending buffer: {t[-300:]!r}")
# the KEEPS-THE-INPUT half [D:windows-s3]: the buffer was SUBMITTED (its
# parse error shows), not discarded the way Ctrl+C discards
if "match" not in t or "error" not in t:
    failures.append(f"the escaped buffer must submit and show its error: {t[-300:]!r}")

# --- 9d. ...but Ctrl+J's DELIBERATE blank line stays composing
t, _ = run([("match 1 with", 0.3), ("\n", 0.2), ("\n", 0.2), ("| _ -> 6\r", 0.6), (":q\r", 0.3)])
if "6 : int" not in t:
    failures.append(f"a Ctrl+J blank inside composition must not submit: {t[-300:]!r}")

# --- 9. piped input never enters the editor (untouched)
p = subprocess.run([WEIR], input="1 + 1\n:q\n", capture_output=True, text=True)
if "2 : int" not in p.stdout:
    failures.append(f"piped input must behave as before: {p.stdout!r}")

if failures:
    for f in failures:
        print("repl-multiline FAIL:", f)
    sys.exit(1)
print("repl-multiline: 2D buffer, Enter-completeness, whole-entry history, fzf display form, wrap at two widths hold")
