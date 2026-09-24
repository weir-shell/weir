#!/usr/bin/env python3
# REPL quality probes [D:repl-quality]: persistent history (XDG_STATE path,
# consecutive-dup dedup, 0600), Ctrl+R history search via a STUB fzf (the
# spawn-feed-select-restore mechanics, deterministic without real fzf) and
# the minimal built-in fallback when fzf is absent. Runs the real binary
# under a pty; asserts on evaluated output, not on redraw escapes.
import os, pty, sys, time, tempfile, re, stat, select

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []

def run_repl(env_extra, keys):
    env = dict(os.environ)
    env.update(env_extra)
    pid, fd = pty.fork()
    if pid == 0:
        os.execve(WEIR, ["weir"], env)
    time.sleep(0.8)
    for s, d in keys:
        os.write(fd, s.encode())
        time.sleep(d)
    out = b""
    for _ in range(40):
        r, _, _ = select.select([fd], [], [], 0.1)
        if r:
            try:
                out += os.read(fd, 4096)
            except OSError:
                break
    try:
        os.close(fd)
    except OSError:
        pass
    return re.sub(r"\x1b\[[0-9;?]*[a-zA-Z]", "", out.decode(errors="replace"))

# --- 1. history: XDG_STATE path, consecutive-dup dedup, 0600 ---
d1 = tempfile.mkdtemp()
run_repl({"XDG_STATE_HOME": d1 + "/state", "XDG_CONFIG_HOME": d1 + "/cfg"},
         [('1 + 1\r', 0.3), ('1 + 1\r', 0.3), ('2 + 2\r', 0.3), ('#quit\r', 0.3)])
hf = d1 + "/state/weir/history"
if not os.path.exists(hf):
    failures.append("history file not created at $XDG_STATE_HOME/weir/history")
else:
    lines = [l for l in open(hf).read().splitlines() if l]
    if lines[:3] != ["1 + 1", "2 + 2", "#quit"]:
        failures.append(f"consecutive dedup / content wrong: {lines}")
    mode = stat.S_IMODE(os.stat(hf).st_mode)
    if mode != 0o600:
        failures.append(f"history file not 0600: {oct(mode)}")

# --- 2. Ctrl+R via a STUB fzf: the selection REPLACES the line, and the
# invocation carries --no-extended BEFORE the config flags (weir glyphs
# ^ | $ ! are fzf query operators; literal matching is the default, and
# last-flag-wins lets finderFlags restore --extended) ---
d2 = tempfile.mkdtemp()
os.makedirs(d2 + "/bin")
with open(d2 + "/bin/fzf", "w") as f:
    f.write('#!/bin/sh\necho "$@" > "$(dirname "$0")/argv.txt"\nhead -1\n')
os.chmod(d2 + "/bin/fzf", 0o755)
out2 = run_repl({"XDG_STATE_HOME": d2 + "/state", "XDG_CONFIG_HOME": d2 + "/cfg",
                 "PATH": d2 + "/bin:" + os.environ["PATH"]},
                [('7 * 6\r', 0.4), ('\x12', 0.5), ('\r', 0.5), ('#quit\r', 0.3)])
# 42 once from the eval, again after Ctrl+R recalls "7 * 6" and Enter submits
if out2.count("42") < 2:
    failures.append(f"Ctrl+R (fzf stub) did not recall and re-evaluate 7*6: {out2!r}")
argv_path = d2 + "/bin/argv.txt"
if not os.path.exists(argv_path):
    failures.append("stub fzf never recorded its argv")
else:
    argv = open(argv_path).read().split()
    if "--no-extended" not in argv:
        failures.append(f"fzf invocation must carry --no-extended (weir glyphs are fzf operators): {argv}")
    elif "--height" in argv and argv.index("--no-extended") > argv.index("--height"):
        failures.append(f"--no-extended must precede config flags (last-flag-wins override): {argv}")

# --- 2b. #find via the SAME stub fzf [D:help-find]: candidates feed in,
# the selection's first field prints its #help answer, and the argv
# carries --no-extended plus a --preview that runs the binary's own
# headless doc render (--repl-doc) — never `weir` assumed on PATH ---
out2b = run_repl({"XDG_STATE_HOME": d2 + "/state", "XDG_CONFIG_HOME": d2 + "/cfg",
                  "PATH": d2 + "/bin:" + os.environ["PATH"]},
                 [('#find opt\r', 0.8), ('#quit\r', 0.3)])
# the stub selects the first candidate line ("Args — …"), so the module's
# member glance prints (the exact #help Args rendering)
if "Args (" not in out2b or "members" not in out2b:
    failures.append(f"#find (fzf stub) selection must print the #help rendering: {out2b!r}")
argv2b = open(d2 + "/bin/argv.txt").read()
if "--no-extended" not in argv2b:
    failures.append(f"#find's fzf invocation must carry --no-extended: {argv2b}")
if "--preview" not in argv2b or "--repl-doc" not in argv2b:
    failures.append(f"#find must wire a --preview through --repl-doc: {argv2b}")
if "--query opt" not in argv2b:
    failures.append(f"#find's initial query must pass as fzf --query: {argv2b}")

# --- 2c. the feed survives a finder that exits MID-STREAM [D:repl-quality]:
# a history larger than the pipe buffer guarantees the stub's head -1 exit
# breaks the feed (EPIPE) while weir still streams — the broken pipe is a
# normal selection outcome, never a cancel. Padded entries push the feed
# past 64k; most-recent-first makes the LAST file entry the selection ---
d2c = tempfile.mkdtemp()
os.makedirs(d2c + "/bin")
os.makedirs(d2c + "/state/weir", exist_ok=True)
with open(d2c + "/bin/fzf", "w") as f:
    f.write('#!/bin/sh\nhead -1\n')
os.chmod(d2c + "/bin/fzf", 0o755)
pad = "x" * 48
with open(d2c + "/state/weir/history", "w") as f:
    f.write("".join(f"{i} + {i} // {pad}\n" for i in range(2000)))
out2c = run_repl({"XDG_STATE_HOME": d2c + "/state", "XDG_CONFIG_HOME": d2c + "/cfg",
                  "PATH": d2c + "/bin:" + os.environ["PATH"]},
                 [('\x12', 0.8), ('\r', 0.5), ('#quit\r', 0.3)])
if "3998" not in out2c:
    failures.append(f"selection lost when the finder exits mid-feed (EPIPE must not cancel): {out2c[-300:]!r}")

# --- 3. Ctrl+R fallback (fzf absent): minimal reverse substring search ---
d3 = tempfile.mkdtemp()
out3 = run_repl({"XDG_STATE_HOME": d3 + "/state", "XDG_CONFIG_HOME": d3 + "/cfg",
                 "PATH": "/usr/bin:/bin"},  # no fzf -> the built-in fallback
                [('3 + 100\r', 0.4), ('\x12', 0.4), ('100', 0.4), ('\r', 0.4), ('\r', 0.5), ('#quit\r', 0.3)])
# recall "3 + 100" by its substring "100", submit -> 103
if "103" not in out3:
    failures.append(f"Ctrl+R fallback did not recall '3 + 100' by substring: {out3!r}")

# --- 4. Tab at the let-RHS head slot [D:let-rhs-head]: the head pool
# serves the RHS and the completion INSERTS at the RHS word. A session
# alias is the candidate (unique by construction; nothing asserts a
# PATH executable) — statement head and let-RHS both offer it
# [D:command-head-alias] ---
d4 = tempfile.mkdtemp()
out4 = run_repl({"XDG_STATE_HOME": d4 + "/state", "XDG_CONFIG_HOME": d4 + "/cfg"},
                [("#alias qqxzz = print\r", 0.4),
                 ("let x = qqx\t", 0.6), ("\x03", 0.3),
                 ("qqx\t", 0.6), ("\x03", 0.3), ("#quit\r", 0.3)])
if "let x = qqxzz" not in out4:
    failures.append(f"Tab at the let-RHS head must insert the alias head: {out4[-400:]!r}")
if "weir> qqxzz" not in out4:
    failures.append(f"Tab at the statement head must offer the alias too: {out4[-400:]!r}")

if failures:
    for f in failures:
        print("repl-quality FAIL:", f)
    sys.exit(1)
print("repl-quality: history (XDG/dedup/0600), Ctrl+R fzf-stub + minimal fallback, #find fzf-stub (--no-extended + --repl-doc preview), let-RHS head Tab (alias inserts at the RHS and the statement head) hold")
