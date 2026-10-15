#!/usr/bin/env python3
# The line editor moves and deletes by code point [D:str-code-points]:
# Left/Right/Backspace/^D over an emoji (a UTF-16 surrogate pair)
# take one step, never leaving half a pair in the buffer. Asserts on the
# evaluated output, not the paint.
import os
import sys

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
from ptysession import Repl

assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

LEFT, RIGHT = "\x1b[D", "\x1b[C"
cases = [
    # Left x3 from the end lands before the emoji: "x" + "y" + "😀"
    ("Left over an emoji", 'print (Str.toUpper "x😀")' + LEFT * 3 + "y\r", "XY😀"),
    # Left x3 then Backspace deletes the whole emoji
    ("Backspace over an emoji", 'print (Str.toUpper "a😀b")' + LEFT * 3 + "\x7f\r", "AB"),
    # Left over `")`, the emoji, then Right steps back over it whole
    ("Right over an emoji", 'print (Str.toUpper "a😀")' + LEFT * 3 + RIGHT + "z\r", "A😀Z"),
    # ^D mid-line deletes the whole emoji under the cursor
    ("^D over an emoji", 'print (Str.toUpper "a😀b")' + LEFT * 4 + "\x04\r", "AB"),
]

failures = []
r = Repl(WEIR)
try:
    if r.prompt() is None:
        failures.append("no first prompt")
    else:
        for name, keys, want in cases:
            t = r.submit(keys)
            lines = [l.strip() for l in t.split("\n")]
            if want not in lines:
                failures.append(f"{name}: expected a line {want!r}: {t[-300:]!r}")
finally:
    r.close()

if failures:
    for f in failures:
        print("repl-codepoints FAIL:", f)
    sys.exit(1)

print("repl-codepoints: Left/Right/Backspace/^D step over an emoji whole")
