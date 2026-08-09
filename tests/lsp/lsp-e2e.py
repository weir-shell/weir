#!/usr/bin/env python3
"""LSP integration test: speaks the protocol over stdio against the AOT
binary. Invoked by ci/e2e.sh; exits nonzero with a reason on failure."""
import json, os, subprocess, sys

BIN = os.environ.get("WEIR_BIN", os.path.expanduser("~/.local/bin/weir"))
import sys as _sys
_sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(BIN, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
proc = subprocess.Popen([BIN, "lsp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)

def send(obj):
    body = json.dumps(obj).encode()
    proc.stdin.write(f"Content-Length: {len(body)}\r\n\r\n".encode() + body)
    proc.stdin.flush()

RAW_FRAMES = []

def read_msg():
    length = None
    while True:
        line = proc.stdout.readline()
        if not line:
            sys.exit("server closed stream")
        line = line.strip()
        if line.startswith(b"Content-Length:"):
            length = int(line.split(b":")[1])
        elif line == b"":
            break
    body = proc.stdout.read(length)
    RAW_FRAMES.append(body)
    return json.loads(body)

def expect(cond, why):
    if not cond:
        proc.kill()
        sys.exit(f"LSP FAIL: {why}")

URI = "file:///t.weir"

send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}})
init = read_msg()
expect(init["result"]["capabilities"]["hoverProvider"], "capabilities missing")

# open a doc with an error -> diagnostic with code
send({"jsonrpc": "2.0", "method": "textDocument/didOpen",
      "params": {"textDocument": {"uri": URI, "text": "let Foo = 1\n"}}})
diag = read_msg()
expect(diag["method"] == "textDocument/publishDiagnostics", "expected diagnostics")
ds = diag["params"]["diagnostics"]
expect(len(ds) == 1 and ds[0]["code"] == "casing-law", f"bad diags: {ds}")

# fix it -> diagnostics clear
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": 'let files = [1; 2]\n\nprint $"{files |> Seq.length}"\n'}]}})
diag = read_msg()
expect(diag["params"]["diagnostics"] == [], "diagnostics should clear")

# hover the binding RHS -> a type string
send({"jsonrpc": "2.0", "id": 2, "method": "textDocument/hover",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 0, "character": 5}}})
hover = read_msg()
expect(hover["result"] and "seq<int>" in hover["result"]["contents"]["value"],
       f"hover should show seq<int>: {hover}")

# hover an INNER-let binder -> the bound VALUE's type, not the
# enclosing let-in's body type [PLAN-diagnostics-arc A2]
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": "let f n =\n    let xs = [\"a\"; \"b\"]\n    xs |> Seq.iter print\nf 1\n"}]}})
read_msg()  # diagnostics
send({"jsonrpc": "2.0", "id": 22, "method": "textDocument/hover",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 1, "character": 8}}})
hover2 = read_msg()
expect(hover2["result"] and "seq<string>" in hover2["result"]["contents"]["value"],
       f"inner binder should hover its value type: {hover2}")

# completion after Seq. -> members
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": "let n = [1] |> Seq.\n"}]}})
read_msg()  # diagnostics (an error — incomplete; fine)
send({"jsonrpc": "2.0", "id": 3, "method": "textDocument/completion",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 0, "character": 19}}})
comp = read_msg()
labels = [c["label"] for c in comp["result"]]
expect("Seq.map" in labels and "Seq.sortBy" in labels, f"Seq members missing: {labels[:10]}")

# completion after a dot on a record type (ls row field)
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": "let f = ls |> Seq.head\n\nlet n = f.\n"}]}})
read_msg()
send({"jsonrpc": "2.0", "id": 4, "method": "textDocument/completion",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 2, "character": 10}}})
comp = read_msg()
labels = [c["label"] for c in comp["result"]]
expect(any("name" in l for l in labels), f"record fields missing: {labels[:10]}")

# textEdit ranges: dot completion replaces the WHOLE dotted word (the
# Env.Env.fromFile doubling), and paren-nested completion still offers
# (micro's label-prefix filter needs textEdit to engage)
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI}, "contentChanges": [{"text": "let e = Env.\n"}]}})
read_msg()
send({"jsonrpc": "2.0", "id": 41, "method": "textDocument/completion",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 0, "character": 12}}})
comp = read_msg()
item = comp["result"][0]
expect(item["textEdit"]["range"]["start"]["character"] == 8
       and item["textEdit"]["range"]["end"]["character"] == 12
       and item["textEdit"]["newText"] == item["label"],
       f"dot textEdit must span the dotted word: {item}")

send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI}, "contentChanges": [{"text": "print (Se\n"}]}})
read_msg()
send({"jsonrpc": "2.0", "id": 42, "method": "textDocument/completion",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 0, "character": 9}}})
comp = read_msg()
expect(any(c["label"] == "Seq" and "textEdit" in c for c in comp["result"]),
       f"paren-nested completion with textEdit: {comp['result'][:3]}")

# param-dot fallback: lambda/function params are not in the env and
# mid-edit statements have no typed tree — declared-record fields are
# offered instead (t. inside a broken function body)
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": "type Target = { Stack: string; Env: string }\n\nlet quality t =\n    bicep lint (t.\n"}]}})
read_msg()
send({"jsonrpc": "2.0", "id": 43, "method": "textDocument/completion",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 3, "character": 18}}})
labels = [c["label"] for c in read_msg()["result"]]
expect("t.Stack" in labels and "t.Env" in labels, f"param-dot fallback fields missing: {labels[:8]}")

# holes: a known function applied to an out-of-scope param still
# types the pipeline element exactly (targetEnv t |> ... -> EnvVar)
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": 'let f t =\n    Env.fromFile t |> Seq.where (fun e -> e.\n'}]}})
read_msg()
send({"jsonrpc": "2.0", "id": 44, "method": "textDocument/completion",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 1, "character": 44}}})
labels = [c["label"] for c in read_msg()["result"]]
expect(labels == ["e.name", "e.value"], f"hole inference should give exactly EnvVar fields: {labels}")

# error-recovery completion: a BROKEN statement's other lines type the
# param ("in let quality t we know what t is") — exact row fields, not
# the fallback; works through interp holes and dangling parens
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": "let quality t =\n    printerr t.Stack\n    printerr (t.\n"}]}})
read_msg()
send({"jsonrpc": "2.0", "id": 45, "method": "textDocument/completion",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 2, "character": 16}}})
labels = [c["label"] for c in read_msg()["result"]]
expect(labels == ["t.Stack"], f"repair completion must give the body-inferred row exactly: {labels}")

send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": 'let quality t =\n    printerr t.Stack\n    printerr $"q: {t.\n'}]}})
read_msg()
send({"jsonrpc": "2.0", "id": 46, "method": "textDocument/completion",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 2, "character": 21}}})
labels = [c["label"] for c in read_msg()["result"]]
expect(labels == ["t.Stack"], f"repair completion through an interp hole: {labels}")

# open-row record compatibility: editing the very line that demanded
# Name still offers Name (the row fits inside declared Target); and
# mid-statement edits repair with cursor-local closers
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": 'type Target = { Name: string; Stack: string }\n\nlet quality t =\n    printerr $"q: {t.\n    printerr t.Stack\n'}]}})
read_msg()
send({"jsonrpc": "2.0", "id": 47, "method": "textDocument/completion",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 3, "character": 21}}})
labels = [c["label"] for c in read_msg()["result"]]
expect(labels == ["t.Name", "t.Stack"], f"row-compat must restore the edited field: {labels}")

# completion at line head -> a PATH command appears
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI}, "contentChanges": [{"text": "gi\n"}]}})
read_msg()
send({"jsonrpc": "2.0", "id": 5, "method": "textDocument/completion",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 0, "character": 2}}})
comp = read_msg()
labels = [c["label"] for c in comp["result"]]
expect("git" in labels, f"PATH command missing at line head: {labels[:10]}")

# unicode round-trip: emoji + CJK in document text must survive the
# JSON reader (surrogate pairs — the hand-rolled reader's failure mode)
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": 'let s = "\U0001F600 \u4e2d\u6587"\n\nprint s\n'}]}})
diag = read_msg()
expect(diag["params"]["diagnostics"] == [], f"unicode text must check clean: {diag}")

# relaxed escaping: quotes in messages are \" — never \u0022 (the
# default encoder's HTML-tuned escaping mangled in micro; user report)
expect(all(b"\\u0022" not in f for f in RAW_FRAMES), "u0022 escapes present in frames")

# a MALFORMED document URI must not kill the server [D:windows-s3] —
# the Windows hand-run watched a bare C:\ path crash it 5x. The bad
# open logs-and-skips (per-doc guard in refreshAll + the request-level
# backstop); the NEXT request still answers. Runs LAST in this section:
# the bad doc stays open and adds a publish per refresh, so the id-keyed
# drain below is the read discipline for everything after it.
send({"jsonrpc": "2.0", "method": "textDocument/didOpen",
      "params": {"textDocument": {"uri": "file://%%%bad%%%uri", "text": "1\n"}}})
send({"jsonrpc": "2.0", "id": 60, "method": "textDocument/hover",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 0, "character": 5}}})
badok = read_msg()
while badok.get("id") != 60:
    badok = read_msg()
expect("result" in badok, f"the server must survive a malformed URI: {badok}")

# close the bad doc and FENCE on one more id-keyed request so the
# message queue is positional again for the sections below
send({"jsonrpc": "2.0", "method": "textDocument/didClose",
      "params": {"textDocument": {"uri": "file://%%%bad%%%uri"}}})
send({"jsonrpc": "2.0", "id": 61, "method": "textDocument/hover",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 0, "character": 5}}})
fence = read_msg()
while fence.get("id") != 61:
    fence = read_msg()

# publishes ride the CLIENT's OWN URI spelling [D:lsp-uri-spelling]: a
# percent-encoded spelling must get its diagnostics under that exact
# string, with NO second publish under a re-derived spelling — the
# split is the Windows squiggle-blink (diag on ours, empty on theirs)
SPELL = "file:///tmp/%77eirspell-pin.weir"
send({"jsonrpc": "2.0", "method": "textDocument/didOpen",
      "params": {"textDocument": {"uri": SPELL, "text": "let Foo = 1\n"}}})
send({"jsonrpc": "2.0", "id": 62, "method": "textDocument/hover",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 0, "character": 5}}})
spell_pubs = {}
m = read_msg()
while m.get("id") != 62:
    if m.get("method") == "textDocument/publishDiagnostics":
        u = m["params"]["uri"]
        if "eirspell-pin" in u:
            spell_pubs[u] = len(m["params"]["diagnostics"])
    m = read_msg()
expect(spell_pubs.get(SPELL) == 1,
       f"diagnostics must land under the client's spelling: {spell_pubs}")
expect(list(spell_pubs) == [SPELL],
       f"no second publish under a re-derived spelling: {spell_pubs}")
send({"jsonrpc": "2.0", "method": "textDocument/didClose",
      "params": {"textDocument": {"uri": SPELL}}})
send({"jsonrpc": "2.0", "id": 63, "method": "textDocument/hover",
      "params": {"textDocument": {"uri": URI}, "position": {"line": 0, "character": 5}}})
fence = read_msg()
while fence.get("id") != 63:
    fence = read_msg()

# ---- semantic tokens [D:semantic-tokens] ----------------------------
expect(init["result"]["capabilities"]["semanticTokensProvider"]["legend"]["tokenTypes"]
       == ["weirCommandHead", "weirArgv", "weirSplice"], "token legend missing")

def decode(data):
    """the five-int delta scheme back to (line, char, len, type)."""
    out, line, char = [], 0, 0
    for i in range(0, len(data), 5):
        dl, dc, ln, ty, _mods = data[i:i + 5]
        line += dl
        char = char + dc if dl == 0 else dc
        out.append((line, char, ln, ty))
    return out

# the minimal two-line two-token case first (the encoding's off-by-one nest)
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": "echo one\necho two\n"}]}})
read_msg()  # diagnostics
send({"jsonrpc": "2.0", "id": 7, "method": "textDocument/semanticTokens/full",
      "params": {"textDocument": {"uri": URI}}})
toks = read_msg()
expect(decode(toks["result"]["data"])
       == [(0, 0, 4, 0), (0, 5, 3, 1), (1, 0, 4, 0), (1, 5, 3, 1)],
       f"two-token delta encoding: {toks}")

# value-headed pipeline [D:value-headed-pipe]: the command head colors in
# EXPRESSION position too (the walk recurses childExprs — it was always
# general; session 1's "empty" was a bare-statement discard error, not a
# walk gap). A library-headed pipe emits nothing.
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI},
                 "contentChanges": [{"text": 'let h = ["hi"] | cat\nlet n = [1] |> Seq.length\n'}]}})
read_msg()
send({"jsonrpc": "2.0", "id": 71, "method": "textDocument/semanticTokens/full",
      "params": {"textDocument": {"uri": URI}}})
toks = read_msg()
vh = decode(toks["result"]["data"])
expect((0, 17, 3, 0) in vh, f"value-headed head 'cat' must color: {vh}")
expect(not any(t[0] == 1 for t in vh), f"a library-headed pipe emits nothing: {vh}")

# the position-matrix fixture: statement cmd, block-let RHS, district,
# splices, shadowing, an expression stage
FIXTURE = """let path = "/etc"
let f r =
    let g = echo tag $r
    g |> Seq.length
if 1 > 0 then
    echo m one
let echo x = x
let y = echo 5
git status --porcelain |> Seq.map Str.trim
"""
send({"jsonrpc": "2.0", "method": "textDocument/didChange",
      "params": {"textDocument": {"uri": URI}, "contentChanges": [{"text": FIXTURE}]}})
read_msg()
import time
t0 = time.monotonic()
send({"jsonrpc": "2.0", "id": 8, "method": "textDocument/semanticTokens/full",
      "params": {"textDocument": {"uri": URI}}})
toks = read_msg()
elapsed = (time.monotonic() - t0) * 1000
got = decode(toks["result"]["data"])
expect((2, 12, 4, 0) in got and (2, 17, 3, 1) in got and (2, 21, 2, 2) in got,
       f"block-let RHS command must token: {got}")
expect((5, 4, 4, 0) in got and (5, 9, 1, 1) in got, f"command-group body must token: {got}")
expect((8, 0, 3, 0) in got and (8, 4, 6, 1) in got, f"statement command must token: {got}")
expect(not any(t[0] == 7 for t in got), f"the shadowed echo must emit nothing: {got}")
expect(not any(t[0] == 8 and t[1] > 23 for t in got),
       f"the expression stage after | must emit nothing: {got}")
expect(elapsed < 500, f"tokens latency {elapsed:.0f}ms exceeds the bound")

send({"jsonrpc": "2.0", "id": 6, "method": "shutdown", "params": {}})
read_msg()
send({"jsonrpc": "2.0", "method": "exit", "params": {}})
proc.wait(timeout=5)

# rootUri sets the resolution base, NOT the server's launch cwd: a
# relative-path command head must resolve the SAME regardless of where
# the editor launched the server (the Zed-vs-VSCode discrepancy).
repo = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
p2 = subprocess.Popen([BIN, "lsp"], cwd="/tmp",  # deliberately the WRONG cwd
                      stdin=subprocess.PIPE, stdout=subprocess.PIPE)
def send2(obj):
    b = json.dumps(obj).encode()
    p2.stdin.write(f"Content-Length: {len(b)}\r\n\r\n".encode() + b); p2.stdin.flush()
def read2():
    length = None
    while True:
        line = p2.stdout.readline().strip()
        if line.startswith(b"Content-Length:"): length = int(line.split(b":")[1])
        elif line == b"": break
    return json.loads(p2.stdout.read(length))
send2({"jsonrpc": "2.0", "id": 1, "method": "initialize",
       "params": {"rootUri": "file://" + repo}})
read2()
send2({"jsonrpc": "2.0", "method": "initialized", "params": {}})
# a script referencing a repo-relative command that EXISTS at the root
send2({"jsonrpc": "2.0", "method": "textDocument/didOpen",
       "params": {"textDocument": {"uri": "file://" + repo + "/tools/fuzz.weir",
                                    "text": open(os.path.join(repo, "tools/fuzz.weir")).read()}}})
for _ in range(5):
    m = read2()
    if m.get("method") == "textDocument/publishDiagnostics":
        rds = m["params"]["diagnostics"]
        expect(len(rds) == 0, f"rootUri base must resolve ci/deep-lock.sh (wrong-cwd server): {rds}")
        break
send2({"jsonrpc": "2.0", "id": 2, "method": "shutdown", "params": {}}); read2()
send2({"jsonrpc": "2.0", "method": "exit", "params": {}}); p2.wait(timeout=5)

# ---- modules: per-URI diagnostics + buffer-over-disk (session 4) ----
# A module's error lands on the module's OWN uri (even when only the entry is
# open), and an OPEN dependency's unsaved buffer wins over disk (decision 14).
import tempfile
td = tempfile.mkdtemp()
modp = os.path.join(td, "mod.weir"); entryp = os.path.join(td, "main.weir")
open(modp, "w").write("module Mod\nlet base = 10\n")               # clean on disk
open(entryp, "w").write('import "./mod.weir"\nprint (show Mod.base)\n')
p3 = subprocess.Popen([BIN, "lsp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
def send3(o):
    b = json.dumps(o).encode()
    p3.stdin.write(f"Content-Length: {len(b)}\r\n\r\n".encode() + b); p3.stdin.flush()
def read3():
    length = None
    while True:
        line = p3.stdout.readline().strip()
        if line.startswith(b"Content-Length:"): length = int(line.split(b":")[1])
        elif line == b"": break
    return json.loads(p3.stdout.read(length))
send3({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}); read3()
entry_uri = "file://" + entryp; mod_uri = "file://" + modp
send3({"jsonrpc": "2.0", "method": "textDocument/didOpen",
       "params": {"textDocument": {"uri": entry_uri, "text": open(entryp).read()}}})
got = {}
for _ in range(3):
    m = read3()
    if m.get("method") == "textDocument/publishDiagnostics":
        got[m["params"]["uri"]] = m["params"]["diagnostics"]; break
expect(got.get(entry_uri) == [], f"a clean entry importing a clean module publishes empty: {got}")
# open the module with a BROKEN buffer (disk stays clean)
send3({"jsonrpc": "2.0", "method": "textDocument/didOpen",
       "params": {"textDocument": {"uri": mod_uri, "text": "module Mod\nlet base = Str.trim 5\n"}}})
seen = {}
for _ in range(5):
    m = read3()
    if m.get("method") == "textDocument/publishDiagnostics":
        seen[m["params"]["uri"]] = m["params"]["diagnostics"]
        if seen.get(mod_uri): break
expect(mod_uri in seen and any("expected string" in d["message"] for d in seen[mod_uri]),
       f"a module error publishes on its OWN uri, buffer over disk: {seen}")
send3({"jsonrpc": "2.0", "id": 9, "method": "shutdown", "params": {}}); read3()
send3({"jsonrpc": "2.0", "method": "exit", "params": {}}); p3.wait(timeout=5)

# ---- cross-file navigation [D:lsp-cross-file] -----------------------
# hover and definition cross the file boundary: module members, the
# import path, and signed commands — Locations carry the CLIENT's URI
# for open targets, pathToUri only for files the client never named.
td2 = tempfile.mkdtemp()
os.makedirs(os.path.join(td2, ".weir", "sigs"))
libp = os.path.join(td2, "lib.weir")
open(libp, "w").write("module Lib\n\n/// doubles a number\nlet double n = n * 2\n")
open(os.path.join(td2, ".weir", "sigs", "mytool.weir"), "w").write(
    'module Mytool\nlet version = "mytool 1.0"\ntype Cmd = {\n    /// run without side effects\n    dryRun: bool\n}\n')
entp = os.path.join(td2, "main.weir")
ENTRY = '#sig mytool\nimport "./lib.weir" as Lib\n\nlet x = Lib.double 21\nlet st = mytool --dry-run\nprint (show x)\n'
open(entp, "w").write(ENTRY)
p4 = subprocess.Popen([BIN, "lsp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
def send4(o):
    b = json.dumps(o).encode()
    p4.stdin.write(f"Content-Length: {len(b)}\r\n\r\n".encode() + b); p4.stdin.flush()
def read4():
    length = None
    while True:
        line = p4.stdout.readline().strip()
        if line.startswith(b"Content-Length:"): length = int(line.split(b":")[1])
        elif line == b"": break
    return json.loads(p4.stdout.read(length))
def req4(i, method, uri, line, char):
    send4({"jsonrpc": "2.0", "id": i, "method": method,
           "params": {"textDocument": {"uri": uri}, "position": {"line": line, "character": char}}})
    m = read4()
    while m.get("id") != i:
        m = read4()
    return m
send4({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}); read4()
ent_uri = "file://" + entp
send4({"jsonrpc": "2.0", "method": "textDocument/didOpen",
       "params": {"textDocument": {"uri": ent_uri, "text": ENTRY}}})

# a module member hovers its annotated signature + the /// doc read from
# the MODULE file, in the local format (type, blank line, doc)
v = req4(2, "textDocument/hover", ent_uri, 3, 13)["result"]["contents"]["value"]
expect(v == "Lib.double (n: int) : int\n\ndoubles a number",
       f"module member hover must read the module's doc: {v!r}")

# definition crosses to the UNOPENED module file — pathToUri spelling
d = req4(3, "textDocument/definition", ent_uri, 3, 13)["result"]
expect(d and d["uri"] == "file://" + libp
       and d["range"]["start"] == {"line": 3, "character": 4},
       f"cross-file definition to the unopened module: {d}")

# definition on the import path opens the module file at 0:0
d = req4(4, "textDocument/definition", ent_uri, 1, 12)["result"]
expect(d and d["uri"] == "file://" + libp and d["range"]["start"]["line"] == 0,
       f"import-path definition: {d}")

# a signed head hovers identity + the RECORDED version (mytool is NOT on
# PATH — nothing spawns), and definition opens the sig file
v = req4(5, "textDocument/hover", ent_uri, 4, 10)["result"]["contents"]["value"]
expect("signed command" in v and "partial signature" in v and "version: mytool 1.0" in v,
       f"signed head hover: {v!r}")
d = req4(6, "textDocument/definition", ent_uri, 4, 10)["result"]
expect(d and d["uri"].endswith("/.weir/sigs/mytool.weir"), f"head definition: {d}")

# a flag hovers its field's type + /// doc from the sig file, and jumps
# to the field declaration
v = req4(7, "textDocument/hover", ent_uri, 4, 19)["result"]["contents"]["value"]
expect(v == "bool\n\nrun without side effects", f"flag hover: {v!r}")
d = req4(8, "textDocument/definition", ent_uri, 4, 19)["result"]
expect(d and d["uri"].endswith("/mytool.weir") and d["range"]["start"] == {"line": 4, "character": 4},
       f"flag definition: {d}")

# once the target is OPEN under the client's own spelling, Locations ride
# THAT string [D:lsp-uri-spelling] — never a re-derived one
lib_spelled = "file://" + os.path.dirname(libp) + "/%6Cib.weir"
send4({"jsonrpc": "2.0", "method": "textDocument/didOpen",
       "params": {"textDocument": {"uri": lib_spelled, "text": open(libp).read()}}})
d = req4(9, "textDocument/definition", ent_uri, 3, 13)["result"]
expect(d and d["uri"] == lib_spelled, f"open target must use the client's spelling: {d}")

# invalidation: editing the module BUFFER (spelled URI, unsaved) refreshes
# the importer's hover — buffer over disk through the decoded-path match
send4({"jsonrpc": "2.0", "method": "textDocument/didChange",
       "params": {"textDocument": {"uri": lib_spelled},
                  "contentChanges": [{"text": "module Lib\n\n/// TRIPLES a number\nlet double n = n * 3\n"}]}})
v = req4(10, "textDocument/hover", ent_uri, 3, 13)["result"]["contents"]["value"]
expect("TRIPLES a number" in v, f"module edit must refresh the importer's hover: {v!r}")

send4({"jsonrpc": "2.0", "id": 11, "method": "shutdown", "params": {}})
m = read4()
while m.get("id") != 11:
    m = read4()
send4({"jsonrpc": "2.0", "method": "exit", "params": {}}); p4.wait(timeout=5)

# ---- one pipeline, three consumers [D:walk-findings] ----------------
# the same two-error script through `check --json`, the runner, and an
# LSP didOpen: one diagnostics pipeline means identical code/line/col
# everywhere (LSP 0-based by protocol; the runner stops at the FIRST
# error by design, so it vouches for that one)
import re as _re, tempfile as _tf, time as _time
_pd = _tf.mkdtemp()
_pf = os.path.join(_pd, "parity.weir")
_ptext = 'let x = 1 + "a"\nlet Foo = 2\nprint "hi"\n'
open(_pf, "w").write(_ptext)

cj = json.loads(subprocess.run([BIN, "check", "--json", _pf],
                               capture_output=True, text=True).stdout)
expect(len(cj) == 2, f"check --json must report both errors: {cj}")
cj_rows = [(d["code"], d["line"], d["col"]) for d in cj]

rout = subprocess.run([BIN, _pf], capture_output=True, text=True).stderr
mr = _re.match(r"^" + _re.escape(_pf) + r":(\d+):(\d+): ", rout)
expect(mr, f"runner diagnostic must carry file:line:col: {rout!r}")
expect((int(mr.group(1)), int(mr.group(2))) == cj_rows[0][1:],
       f"runner and check --json must agree on the first error: {rout!r} vs {cj_rows}")

p5 = subprocess.Popen([BIN, "lsp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)

def send5(obj):
    body = json.dumps(obj).encode()
    p5.stdin.write(f"Content-Length: {len(body)}\r\n\r\n".encode() + body)
    p5.stdin.flush()

def read5():
    length = None
    while True:
        line = p5.stdout.readline()
        if not line:
            sys.exit("parity server closed stream")
        line = line.strip()
        if line.startswith(b"Content-Length:"):
            length = int(line.split(b":")[1])
        elif line == b"":
            break
    return json.loads(p5.stdout.read(length))

send5({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}})
read5()
p5uri = "file://" + _pf
send5({"jsonrpc": "2.0", "method": "textDocument/didOpen",
       "params": {"textDocument": {"uri": p5uri, "text": _ptext}}})
m = read5()
while m.get("method") != "textDocument/publishDiagnostics":
    m = read5()
lsp_rows = sorted((d["code"], d["range"]["start"]["line"] + 1,
                   d["range"]["start"]["character"] + 1)
                  for d in m["params"]["diagnostics"])
expect(lsp_rows == sorted(cj_rows),
       f"LSP and check --json must agree code/line/col: {lsp_rows} vs {sorted(cj_rows)}")

# recheck latency: a didChange republishes within 500ms — the editor's
# keystroke-to-squiggle budget, lenient over a 3-line file on AOT
send5({"jsonrpc": "2.0", "method": "textDocument/didChange",
       "params": {"textDocument": {"uri": p5uri},
                  "contentChanges": [{"text": 'let x = 1\nprint "hi"\n'}]}})
_t0 = _time.monotonic()
m = read5()
while m.get("method") != "textDocument/publishDiagnostics":
    m = read5()
_dt = _time.monotonic() - _t0
expect(m["params"]["diagnostics"] == [], f"the fix must clear: {m['params']}")
expect(_dt < 0.5, f"recheck must republish within 500ms, took {_dt:.3f}s")

send5({"jsonrpc": "2.0", "id": 2, "method": "shutdown", "params": {}})
m = read5()
while m.get("id") != 2:
    m = read5()
send5({"jsonrpc": "2.0", "method": "exit", "params": {}}); p5.wait(timeout=5)

print("lsp-e2e: all probes green")
