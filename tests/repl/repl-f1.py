#!/usr/bin/env python3
# F1 in the editor [D:repl-f1-keys]: #keys' bindings print below the
# buffer and the buffer comes back whole (every line, the cursor's row and
# column) — nothing submitted, abandoned or recorded; each raw F1 sequence
# terminals send works; the one-time "(F1: keys)" hint shows on the first
# typed continuation line only, plain under NO_COLOR, never piped.
# Assertions ride evaluated output and the history file, never cursor
# positions.
import os, pty, signal, sys, time, select, re, tempfile, fcntl, struct, termios, subprocess

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []
BINDING = "show these bindings; the buffer stays as it was"
HINT = "(F1: keys)"


def run(keys, extra_env=None, until="bye-marker", deadline=20.0):
    d = tempfile.mkdtemp()
    env = dict(os.environ)
    env.pop("NO_COLOR", None)
    env.update({"XDG_STATE_HOME": d + "/state", "XDG_CONFIG_HOME": d + "/cfg",
                "PATH": "/usr/bin:/bin", "TERM": "xterm-256color"})
    env.update(extra_env or {})
    pid, fd = pty.fork()
    if pid == 0:
        os.execve(WEIR, ["weir"], env)
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 100, 0, 0))
    os.kill(pid, signal.SIGWINCH)
    time.sleep(0.8)
    out = b""

    def drain(end):
        nonlocal out
        while time.time() < end:
            r, _, _ = select.select([fd], [], [], max(0, min(0.1, end - time.time())))
            if r:
                try:
                    out += os.read(fd, 4096)
                except OSError:
                    return

    for s, dl in keys + [('"bye-" + "marker"\r', 0.3)]:
        os.write(fd, s.encode())
        drain(time.time() + dl)
    want = until.encode()
    end = time.time() + deadline
    while time.time() < end and want not in out:
        drain(time.time() + 0.1)
    drain(time.time() + 0.3)
    os.write(fd, b"#quit\r")
    drain(time.time() + 1.0)
    try:
        os.kill(pid, signal.SIGKILL)
        os.waitpid(pid, 0)
    except OSError:
        pass
    raw = out.decode(errors="replace")
    return re.sub(r"\x1b\[[0-9;?]*[a-zA-Z]", "", raw), raw, d


def history(d):
    try:
        return open(d + "/state/weir/history").read()
    except OSError:
        return ""


# --- 1. F1 (ESC O P) mid multi-line buffer, cursor on the second line mid-
# text: the bindings print, the buffer survives, the cursor's row/column
# hold (the "1" typed after F1 lands between 40 and the rest: 401 + 2)
t, _, d = run([("let ff =\r", 0.4), ("40 + 2", 0.3), ("\x1b[D" * 4, 0.3),
               ("\x1bOP", 0.6), ("1", 0.3), ("\r", 0.3), ("\r", 0.6)])
if BINDING not in t:
    failures.append(f"F1 must print the bindings: {t[-600:]!r}")
elif "let ff =" not in t[t.index(BINDING):]:
    failures.append(f"F1 must repaint the prompt and buffer below the bindings: {t[-600:]!r}")
if "ff = 403" not in t:
    failures.append(f"F1 must keep every line and the cursor (expected ff = 403): {t[-600:]!r}")
if "let ff =\\n    401 + 2\n" not in history(d):
    failures.append(f"the submitted entry must be the whole buffer, recorded once: {history(d)!r}")
if "F1" in history(d) or history(d).count("let ff") != 1:
    failures.append(f"F1 must add nothing to history: {history(d)!r}")

# --- 2. F1 (ESC [ 1 1 ~) at an empty prompt; then an ordinary entry runs
t, _, d = run([("\x1b[11~", 0.6), ("6 * 7\r", 0.6)])
if BINDING not in t or "\n42\r\n" not in t:
    failures.append(f"F1 at an empty prompt must print the bindings and leave the prompt usable: {t[-600:]!r}")
if history(d).splitlines()[:1] != ["6 * 7"]:
    failures.append(f"F1 at an empty prompt must record nothing: {history(d)!r}")

# --- 3. the Linux console's ESC [ [ A, and F1 never abandons: the buffer
# typed before it submits after it
t, _, _ = run([("1 +", 0.3), ("\x1b[[A", 0.6), (" 2\r", 0.6)])
if BINDING not in t or "\n3\r\n" not in t:
    failures.append(f"ESC [ [ A must be F1 and keep the buffer (1 + 2): {t[-600:]!r}")

# --- 4. the one-time hint: on the first typed continuation line, dim at a
# colour tty; the session's second multi-line entry has none
t, raw, _ = run([("let a =\r", 0.5), ("1\r", 0.6), ("let b =\r", 0.5), ("2\r", 0.6)])
first, _, second = t.partition("a = 1")
if HINT not in first:
    failures.append(f"the first continuation line must carry the hint: {t[-600:]!r}")
if HINT in second:
    failures.append(f"the hint must show once per session: {second[-600:]!r}")
if "\x1b[2m  " + HINT not in raw:
    failures.append(f"the hint must be dim at a colour tty: {raw[-600:]!r}")
if "b = 2" not in t:
    failures.append(f"entries with the hint must still run: {t[-600:]!r}")

# --- 5. NO_COLOR: the hint still shows once, without the dim
t, raw, _ = run([("let a =\r", 0.5), ("1\r", 0.6)], {"NO_COLOR": "1"})
if HINT not in t or "\x1b[2m" in raw:
    failures.append(f"NO_COLOR: hint shown plain: {raw[-600:]!r}")

# --- 6. piped: no editor, no hint
p = subprocess.run([WEIR], input="let a =\n    1\na\n", capture_output=True, text=True, timeout=30)
if HINT in p.stdout + p.stderr or "1" not in p.stdout:
    failures.append(f"piped: no hint: {p.stdout!r} {p.stderr!r}")

if failures:
    for f in failures:
        print("FAIL:", f, file=sys.stderr)
    sys.exit(1)
print("ok: repl F1 bindings mid-buffer, raw F1 sequences, one-time hint")
