# The harness-truth library [D:masking-mechanized] — ONE helper, all
# census/stamp checks inherit (the one-scanner pattern applied to
# harnesses). dev/PROCESS.md: harness assertions are claims too.
import os
import subprocess
import sys


def assert_fresh(weir_bin, repo_root):
    """The ONE freshness gate — delegates to ci/check-fresh.sh so the
    stamp AND mtime checks have a single implementation (this used to be
    a Python stamp-only copy whose own comment claimed mtime coverage it
    did not have — a guard's doc drifting from the guard is the exact
    masked failure the gate exists to prevent). Exits nonzero on stale."""
    gate = os.path.join(repo_root, "ci", "check-fresh.sh")
    # Windows cannot exec a .sh (WinError 193) — route through the
    # battery's OWN bash (WEIR_BASH: a bare `bash` resolves System32's
    # WSL stub on the native PATH); POSIX untouched
    cmd = (
        [gate, weir_bin]
        if os.name != "nt"
        else [os.environ.get("WEIR_BASH", "bash"), gate, weir_bin]
    )
    r = subprocess.run(cmd)
    if r.returncode != 0:
        sys.exit(r.returncode)


def alive(pid):
    """waitpid-truth census: a ZOMBIE is dead. kill(pid, 0) succeeds on
    zombies and lies — the Ctrl+D incident's lesson, mechanized."""
    try:
        done, _ = os.waitpid(pid, os.WNOHANG)
        return done == 0
    except ChildProcessError:
        return False
