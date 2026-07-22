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
expect(any("Name" in l for l in labels), f"record fields missing: {labels[:10]}")

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
expect(labels == ["e.Name", "e.Value"], f"hole inference should give exactly EnvVar fields: {labels}")

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

send({"jsonrpc": "2.0", "id": 6, "method": "shutdown", "params": {}})
read_msg()
send({"jsonrpc": "2.0", "method": "exit", "params": {}})
proc.wait(timeout=5)
print("lsp-e2e: all probes green")
