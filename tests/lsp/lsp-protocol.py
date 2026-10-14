#!/usr/bin/env python3
"""LSP protocol conformance: every request is answered. A throwing
handler answers -32603, an unknown method -32601, a message that is not
JSON -32700 (null id), a request after `shutdown` -32600; `exit` without
`shutdown` ends with 1 (0 with it); header names are case-insensitive.
Each read waits for its own response id (bounded), never a fixed sleep.
Invoked by ci/e2e.sh; exits nonzero with a reason on failure."""
import json, os, subprocess, sys, threading, queue

BIN = os.environ.get("WEIR_BIN", os.path.expanduser("~/.local/bin/weir"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(BIN, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []


class Server:
    def __init__(self):
        self.p = subprocess.Popen([BIN, "lsp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL)
        self.q = queue.Queue()
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self):
        f = self.p.stdout
        while True:
            hdrs = {}
            while True:
                line = f.readline()
                if not line:
                    return
                line = line.strip()
                if not line:
                    break
                k, _, v = line.partition(b":")
                hdrs[k.strip().lower()] = v.strip()
            self.q.put(json.loads(f.read(int(hdrs.get(b"content-length", b"0")))))

    def raw(self, body, name="Content-Length"):
        self.p.stdin.write(f"{name}: {len(body)}\r\n\r\n".encode() + body)
        self.p.stdin.flush()

    def send(self, obj, name="Content-Length"):
        self.raw(json.dumps(obj).encode(), name)

    def wait(self, pred, timeout=20):
        try:
            while True:
                m = self.q.get(timeout=timeout)
                if pred(m):
                    return m
        except queue.Empty:
            return None

    def reply(self, rid):
        return self.wait(lambda m: m.get("id") == rid and ("result" in m or "error" in m))

    def init(self):
        self.send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"capabilities": {}}})
        self.reply(1)
        self.send({"jsonrpc": "2.0", "method": "textDocument/didOpen",
                   "params": {"textDocument": {"uri": "file:///w/x.weir", "languageId": "weir",
                                               "version": 1, "text": "print \"a\"\n"}}})

    def exit_code(self):
        try:
            return self.p.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.p.kill()
            return "timeout"


def hover(rid, line=0):
    return {"jsonrpc": "2.0", "id": rid, "method": "textDocument/hover",
            "params": {"textDocument": {"uri": "file:///w/x.weir"},
                       "position": {"line": line, "character": 0}}}


def code(m):
    return (m or {}).get("error", {}).get("code")


s = Server()
s.init()
# a position past Int32 throws inside the handler: answered, not dropped
s.raw(b'{"jsonrpc":"2.0","id":5,"method":"textDocument/hover","params":{"textDocument":{"uri":"file:///w/x.weir"},"position":{"line":1e30,"character":1}}}')
m = s.reply(5)
if code(m) != -32603:
    failures.append(f"a throwing handler must answer -32603: {m}")
s.send({"jsonrpc": "2.0", "id": 6, "method": "weir/noSuchMethod", "params": {}})
if code(s.reply(6)) != -32601:
    failures.append("an unknown request must answer -32601")
s.raw(b'{"jsonrpc":"2.0","id":')
m = s.wait(lambda m: "error" in m and m.get("id") is None)
if code(m) != -32700:
    failures.append(f"a message that is not JSON must answer -32700 with a null id: {m}")
# a lowercase header still frames, and the session kept serving
s.send(hover(7), name="content-length")
if s.reply(7) is None:
    failures.append("a lowercase content-length header must frame")
s.send({"jsonrpc": "2.0", "id": 8, "method": "shutdown"})
s.reply(8)
s.send(hover(9))
if code(s.reply(9)) != -32600:
    failures.append("a request after shutdown must answer -32600")
s.send({"jsonrpc": "2.0", "method": "exit"})
rc = s.exit_code()
if rc != 0:
    failures.append(f"exit after shutdown must end 0, got {rc}")

s = Server()
s.init()
s.send({"jsonrpc": "2.0", "method": "exit"})
rc = s.exit_code()
if rc != 1:
    failures.append(f"exit without shutdown must end 1, got {rc}")

if failures:
    for f in failures:
        print("lsp-protocol FAIL:", f)
    sys.exit(1)
print("lsp-protocol ok: throwing handler -32603, unknown method -32601, bad JSON -32700, after-shutdown -32600, exit 1 without shutdown, case-insensitive headers")
