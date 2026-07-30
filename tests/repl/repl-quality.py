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
         [('1 + 1\r', 0.3), ('1 + 1\r', 0.3), ('2 + 2\r', 0.3), (':q\r', 0.3)])
hf = d1 + "/state/weir/history"
if not os.path.exists(hf):
    failures.append("history file not created at $XDG_STATE_HOME/weir/history")
else:
    lines = [l for l in open(hf).read().splitlines() if l]
    if lines[:3] != ["1 + 1", "2 + 2", ":q"]:
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
                [('7 * 6\r', 0.4), ('\x12', 0.5), ('\r', 0.5), (':q\r', 0.3)])
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

# --- 3. Ctrl+R fallback (fzf absent): minimal reverse substring search ---
d3 = tempfile.mkdtemp()
out3 = run_repl({"XDG_STATE_HOME": d3 + "/state", "XDG_CONFIG_HOME": d3 + "/cfg",
                 "PATH": "/usr/bin:/bin"},  # no fzf -> the built-in fallback
                [('3 + 100\r', 0.4), ('\x12', 0.4), ('100', 0.4), ('\r', 0.4), ('\r', 0.5), (':q\r', 0.3)])
# recall "3 + 100" by its substring "100", submit -> 103
if "103" not in out3:
    failures.append(f"Ctrl+R fallback did not recall '3 + 100' by substring: {out3!r}")

if failures:
    for f in failures:
        print("repl-quality FAIL:", f)
    sys.exit(1)
print("repl-quality: history (XDG/dedup/0600), Ctrl+R fzf-stub + minimal fallback hold")
