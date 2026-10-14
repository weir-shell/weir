#!/usr/bin/env python3
# Type-ahead [D:owned-line-editor]: a line typed with Enter before the
# first prompt (the terminal still cooked, ICRNL turning the CR into the
# Ctrl+J newline byte) submits like an Enter at the prompt — it must not
# become a newline in the buffer. The keys go in right at fork, before
# the REPL can be ready; the rest waits on output, never fixed sleeps.
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
    r.send('print (Str.toUpper "early")\r')
    t = r.wait_for(r"\nEARLY\n(.|\n)*weir> $")
    if t is None:
        failures.append(f"an Enter typed before the prompt must submit: {r.out[-400:]!r}")
    else:
        t = r.submit('print (Str.toUpper "late")\r')
        if "LATE" not in t:
            failures.append(f"the next line runs at its own prompt: {t[-300:]!r}")
finally:
    r.close()

if failures:
    for f in failures:
        print("repl-typeahead FAIL:", f)
    sys.exit(1)

print("repl-typeahead: an Enter typed before the first prompt submits the line")
