#!/usr/bin/env python3
# A sig drives Tab in command argv [D:sig-arg-complete]: the project's
# .weir/sigs/ (gitfx, generated from tests/sigs/gitfx) and the user dir
# $XDG_CONFIG_HOME/weir/sigs/ (a hand sig with enumerated choices) — the
# subcommand word, a case's flag, a `--flag=` value (paths for a file
# flag, the choices for an enumerated one), and a sig-less head keeps its
# path completion. Waits on text, never fixed sleeps.
import os
import shutil
import sys
import tempfile

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "lib"))
from harness import assert_fresh
from ptysession import Repl

assert_fresh(WEIR, os.path.join(HERE, "..", ".."))

root = tempfile.mkdtemp()
proj = os.path.join(root, "proj")
os.makedirs(os.path.join(proj, ".git"))
os.makedirs(os.path.join(proj, ".weir", "sigs"))
shutil.copy(os.path.join(HERE, "..", "fixtures", "sigs", "gitfx.weir"), os.path.join(proj, ".weir", "sigs", "gitfx.weir"))
open(os.path.join(proj, "zqmsg.txt"), "w").close()
cfg = os.path.join(root, "cfg")
os.makedirs(os.path.join(cfg, "weir", "sigs"))
open(os.path.join(cfg, "weir", "sigs", "chtool.weir"), "w").write(
    "module Chtool\n"
    "type Color = | Always | Never | Auto\n"
    "type Cmd = {\n"
    "    color: Option<Color>\n"
    "    verbose: bool\n"
    "}\n")

env = {"XDG_CONFIG_HOME": cfg, "XDG_STATE_HOME": os.path.join(root, "state")}
failures = []
r = Repl(WEIR, cwd=proj, env=env)
try:
    r.prompt()

    def tab(keys, expect, why):
        start = len(r.out)
        r.send(keys)
        if r.wait_for(expect, start, timeout=10) is None:
            failures.append(f"{why}: {r.out[-300:]!r}")
        # abandon the buffer
        start = len(r.out)
        r.send("\x1b")
        r.prompt(start)

    tab("gitfx com\t", r"gitfx commit", "a subcommand word from the project sig")
    tab("gitfx commit --am\t", r"gitfx commit --amend", "a case's flag")
    tab("gitfx commit --file=zq\t", r"--file=zqmsg\.txt", "a file flag's value part completes as a path")
    tab("chtool --color=ne\t", r"chtool --color=never", "an enumerated value from the user-dir sig")
    tab("chtool --verb\t", r"chtool --verbose", "a flag from the user-dir sig")
    tab("nosigtool zqm\t", r"nosigtool zqmsg\.txt", "a sig-less head keeps path completion")
finally:
    r.close()
    shutil.rmtree(root, ignore_errors=True)

if failures:
    for f in failures:
        print("repl-sig-complete FAIL:", f)
    sys.exit(1)

print("repl-sig-complete: Tab completes subcommands, flags and --flag= values from project and user-dir sigs")
