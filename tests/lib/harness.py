# The harness-truth library [D:masking-mechanized] — ONE helper, all
# census/stamp checks inherit (the one-scanner pattern applied to
# harnesses). PROCESS.md: harness assertions are claims too.
import os
import subprocess
import sys


def assert_fresh(weir_bin, repo_root):
    """Stamp gate: the binary's build stamp must equal git HEAD.
    Stale results become impossible rather than catchable."""
    head = subprocess.run(
        ["git", "-C", repo_root, "rev-parse", "--short", "HEAD"],
        capture_output=True, text=True,
    ).stdout.strip()
    out = subprocess.run(
        [weir_bin, "--version"], capture_output=True, text=True
    ).stdout.strip()
    if not head:
        return  # no git (release tarball); mtime gates still apply
    if not out.startswith(head):
        print(
            f"STALE BINARY: {weir_bin} stamps '{out}', HEAD is '{head}' — "
            "rebuild with ./publish.sh",
            file=sys.stderr,
        )
        sys.exit(1)


def alive(pid):
    """waitpid-truth census: a ZOMBIE is dead. kill(pid, 0) succeeds on
    zombies and lies — the Ctrl+D incident's lesson, mechanized."""
    try:
        done, _ = os.waitpid(pid, os.WNOHANG)
        return done == 0
    except ChildProcessError:
        return False
