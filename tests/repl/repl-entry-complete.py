#!/usr/bin/env python3
# Tab sees the whole entry [D:entry-complete]: the user's two reports,
# typed as entered. A for binder on the line above types `p.` inside an
# interpolation hole on the next line; the `_.` shorthand as a stage's
# argument offers the element's fields (FileRow's), a bare `|> _.` stage
# (which never type-checks over a seq) offers nothing. Asserts on the
# offered list and the evaluated output; waits on text, never fixed sleeps.
import os
import sys
import tempfile

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
from ptysession import Repl, clean

assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

FIELDS = ["name", "kind", "target", "bytes", "modified", "hidden", "path"]
d = tempfile.mkdtemp()
os.mkdir(os.path.join(d, "zqdir"))
open(os.path.join(d, "zqfile"), "w").close()

failures = []
r = Repl(WEIR, cwd=d)
try:
    r.prompt()
    # 1. the for binder above, the cursor in a hole below
    r.send("for p in ls |> where (fun f -> f.kind == Directory) do\r")
    start = len(r.out)
    r.send('    print $"THIS: {p.\t')
    t = r.wait_for(r"p\.target", start)
    if t is None or not all(("p." + f) in t for f in FIELDS):
        failures.append(f"p.<Tab> in the hole must offer FileRow's fields: {r.out[-400:]!r}")
    start = len(r.out)
    r.send("na\t")
    if r.wait_for(r"\{p\.name", start) is None:
        failures.append(f"p.na<Tab> must complete to p.name: {r.out[-300:]!r}")
    t = r.submit('}"\r')
    if "THIS: zqdir" not in t or "zqfile" in t:
        failures.append(f"the completed entry must run: {t[-300:]!r}")

    # 2. the user's second entry: a bare `|> _.` stage over a seq never
    # type-checks, so Tab offers nothing (user-ruled); Esc abandons it
    start = len(r.out)
    r.send("for p in ls |> _.\t")
    r.wait_for(r"_\.", start)
    r.send("\x1b")
    r.prompt(start)
    if "_.name" in clean(r.out[start:]):
        failures.append(f"a bare `|> _.` stage must offer nothing: {r.out[-400:]!r}")
    t = r.submit("40 + 2\r")
    if "\n42\n" not in t:
        failures.append(f"Esc must abandon the buffer: {t[-300:]!r}")

    # 3. the shorthand as a stage's argument offers the element's fields
    start = len(r.out)
    r.send("ls |> map _.\t")
    t = r.wait_for(r"_\.target", start)
    if t is None or not all(("_." + f) in t for f in FIELDS):
        failures.append(f"`ls |> map _.<Tab>` must offer FileRow's fields: {r.out[-400:]!r}")
    start = len(r.out)
    r.send("na\t")
    if r.wait_for(r"map _\.name", start) is None:
        failures.append(f"`map _.na<Tab>` must complete to _.name: {r.out[-300:]!r}")
    t = r.submit(" |> Seq.where (Str.startsWith \"zq\")\r")
    if "zqdir" not in t or "zqfile" not in t:
        failures.append(f"the completed pipeline must run: {t[-300:]!r}")
finally:
    r.close()

if failures:
    for f in failures:
        print("repl-entry-complete FAIL:", f)
    sys.exit(1)

print("repl-entry-complete: Tab completes a binder from an earlier line, the `_.` argument's element, nothing for a bare `|> _.`")
