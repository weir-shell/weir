#!/usr/bin/env python3
# Pins for the harness library itself: the zombie lie, and the stamp
# gate against a deliberately-stale stub.
import os
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(__file__))
from harness import alive

# 1. the zombie: kill(pid,0) says alive; waitpid-truth says dead
pid = os.fork()
if pid == 0:
    os._exit(0)
time.sleep(0.2)  # child exits; unreaped -> zombie
try:
    os.kill(pid, 0)
    kill_says_alive = True
except ProcessLookupError:
    kill_says_alive = False
assert kill_says_alive, "expected a zombie to fool kill(pid,0)"
assert not alive(pid), "waitpid-truth must call the zombie dead"

# 2. the stamp gate: a stub stamping the wrong hash must fail loudly
with tempfile.TemporaryDirectory() as d:
    stub = os.path.join(d, "weir")
    with open(stub, "w") as f:
        f.write("#!/bin/sh\necho deadbee\n")
    os.chmod(stub, 0o755)
    repo = os.path.join(os.path.dirname(__file__), "..", "..")
    r = subprocess.run(
        [sys.executable, "-c",
         "import sys; sys.path.insert(0, %r); import harness; "
         "harness.assert_fresh(%r, %r)" % (os.path.dirname(os.path.abspath(__file__)), stub, repo)],
        capture_output=True, text=True,
    )
    assert r.returncode == 1, f"stale stub must fail the gate (rc={r.returncode})"
    assert "STALE BINARY" in r.stderr, r.stderr

print("harness-selftest: zombie truth + stamp gate hold")
