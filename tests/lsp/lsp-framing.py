#!/usr/bin/env python3
"""LSP transport-hardening probe [D:lsp-transport-caps]: drives the AOT
server over raw JSON-RPC stdio and asserts the framing layer survives a
hostile Content-Length instead of allocating for it.

BEFORE the fix, `Content-Length: 2147483647\\r\\n\\r\\n` (26 bytes) made
readMessage allocate a ~2GB buffer -> OutOfMemoryException -> exit 134,
killing the editor session. AFTER: the framing layer caps accepted size
at 64MB, never allocates for a larger declared length, drains the declared
body in fixed chunks to keep the stream synced, and keeps serving.

Two scenarios:
  A. an oversized frame WITH its full declared body is DRAINED and the
     NEXT well-framed request is answered (the stream stayed synced);
  B. the bare 2147483647 header (the OOM repro): the server does NOT
     allocate/OOM/exit — it stays ALIVE with modest RSS (the drain blocks
     waiting for the declared-but-absent body, which is exactly survival:
     no 2GB allocation ever happened).

Invoked by ci/e2e.sh; exits nonzero with a reason on failure."""
import json, os, subprocess, sys, threading, time

BIN = os.environ.get("WEIR_BIN", os.path.expanduser("~/.local/bin/weir"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh, alive
assert_fresh(BIN, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

proc = subprocess.Popen([BIN, "lsp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)


def _deadline():
    sys.stderr.write("LSP-FRAMING FAIL: harness deadline (120s) — a response never arrived\n")
    try:
        proc.kill()
    except Exception:
        pass
    os._exit(1)


_wd = threading.Timer(120, _deadline)
_wd.daemon = True
_wd.start()


def send_frame(header_len, body=b""):
    """Send a frame with an ARBITRARY declared Content-Length (may differ
    from the real body length — that is the attack)."""
    proc.stdin.write(f"Content-Length: {header_len}\r\n\r\n".encode() + body)
    proc.stdin.flush()


def send(obj):
    body = json.dumps(obj).encode()
    send_frame(len(body), body)


def read_msg():
    length = None
    while True:
        line = proc.stdout.readline()
        if not line:
            sys.exit("LSP-FRAMING FAIL: server closed stream")
        line = line.strip()
        if line.startswith(b"Content-Length:"):
            length = int(line.split(b":")[1])
        elif line == b"":
            break
    body = proc.stdout.read(length)
    return json.loads(body)


def expect(cond, why):
    if not cond:
        try:
            proc.kill()
        except Exception:
            pass
        sys.exit(f"LSP-FRAMING FAIL: {why}")


def rss_kb(pid):
    try:
        with open(f"/proc/{pid}/status") as f:
            for ln in f:
                if ln.startswith("VmRSS:"):
                    return int(ln.split()[1])
    except Exception:
        pass
    return -1


# --- establish a live session -------------------------------------------
send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}})
init = read_msg()
expect(init.get("id") == 1, f"initialize did not answer with id 1: {init!r}")
expect(init["result"]["capabilities"]["hoverProvider"], "capabilities missing")

# --- Scenario A: oversized frame WITH its full body is drained ----------
# declare a length just over the 64MB cap and actually deliver that many
# bytes; the server drains the declared count in fixed chunks (no
# allocation), drops the message, and stays synced for the next frame.
CAP = 64 * 1024 * 1024
oversize = CAP + 4096
send_frame(oversize, b"x" * oversize)

send({"jsonrpc": "2.0", "id": 2, "method": "initialize", "params": {}})
resp2 = read_msg()
expect(resp2.get("id") == 2, f"next frame not answered after drained oversized body; got {resp2!r}")
expect(resp2["result"]["capabilities"]["hoverProvider"], "capabilities missing after drain")

# --- a legitimately large-but-under-cap frame still works ---------------
pad = "z" * (2 * 1024 * 1024)
send({"jsonrpc": "2.0", "id": 3, "method": "initialize", "params": {"pad": pad}})
resp3 = read_msg()
expect(resp3.get("id") == 3, f"under-cap large frame not answered; got {resp3!r}")

# RSS sanity after two multi-MB frames: no 2GB allocation happened. Allow
# generous headroom for the AOT runtime; the OOM bug would push RSS into
# the GB range.
r = rss_kb(proc.pid)
expect(r < 0 or r < 800 * 1024, f"RSS {r} KB too high — likely allocated for a declared length")

# --- Scenario B: the bare OOM repro -------------------------------------
# `Content-Length: 2147483647\r\n\r\n` with NO body. BEFORE: allocate 2GB
# -> OOM -> exit 134. AFTER: no allocation; the server enters the bounded
# drain and blocks waiting for the declared body — ALIVE, low RSS. That is
# the survival property (the lying-length client desyncs itself, but the
# server never OOMs and never dies).
send_frame(2147483647, b"")
time.sleep(1.5)  # let the server process the header and enter the drain

expect(alive(proc.pid), "server died after bare oversized Content-Length (OOM/exit-134 regression)")
r2 = rss_kb(proc.pid)
expect(r2 < 0 or r2 < 800 * 1024,
       f"RSS {r2} KB too high after bare oversized header — allocated for the declared length (OOM path)")

_wd.cancel()
try:
    proc.kill()
    proc.wait(timeout=10)
except Exception:
    pass

print("lsp-framing ok: oversized Content-Length drained (with body) / never allocated (bare), session survived, RSS bounded")
