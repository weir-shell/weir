#!/usr/bin/env python3
# A `$` splice completes only what the checker admits there
# [D:argv-splice-complete]: after `$` in a command's argv, Tab offers the
# session bindings a `$x` argument may be — never the Prelude's bare
# functions (take, toInt, tryHead…) nor a user function — after an
# external head and a builtin one (ls) alike. With one admissible
# binding the editor completes it inline. Waits on the prompt, never
# fixed sleeps.
import os
import sys

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
from ptysession import Repl

assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []
r = Repl(WEIR)
try:
    r.prompt()
    r.submit('let tq = "v1"\r')
    r.submit("let tfn x = x + 1\r")
    for line in ["git tag $t", "ls -l $t"]:
        start = len(r.out)
        r.send(line + "\t")
        got = r.wait_for(r"\$tq", start, timeout=5)
        text = r.out[start:].decode(errors="replace")
        if got is None:
            failures.append(f"{line}<Tab> must complete the one admissible binding inline: {text[-300:]!r}")
        for noise in ["takeWhile", "tryHead", "tfn"]:
            if noise in text:
                failures.append(f"{line}<Tab> must not offer {noise}: {text[-300:]!r}")
        start = len(r.out)
        r.send(b"\x03")
        r.prompt(start)
    # a `$` that is literal text offers nothing: a // comment, and a
    # text-block body (the district read off the buffer's lines above)
    for keys, what in [
        ("echo a // $t", "a // comment"),
        ("let z = <<<\r    echo $t", "a <<< body"),
    ]:
        start = len(r.out)
        r.send(keys + "\t" + "QQ")
        r.wait_for(r"QQ", start, timeout=5)
        text = r.out[start:].decode(errors="replace")
        if "tq" in text:
            failures.append(f"{what}: Tab after a literal $ must offer nothing: {text[-300:]!r}")
        start = len(r.out)
        r.send(b"\x03")
        r.prompt(start)
finally:
    r.close()

if failures:
    for f in failures:
        print("repl-splice-complete FAIL:", f)
    sys.exit(1)

print("repl-splice-complete: a $ splice completes only spliceable bindings (external and builtin heads), nothing where $ is literal")
