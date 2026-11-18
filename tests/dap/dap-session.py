#!/usr/bin/env python3
# The step debugger [D:step-debugger], driven as an editor drives it:
# the Debug Adapter Protocol on `weir dap`'s stdio. A breakpoint inside
# a function stops there with the caller's frame below it and the
# pattern's binds as locals; a step over stays in the frame, a step out
# returns to the caller; the script's own output arrives as output
# events, never on the protocol stream; and the session ends with the
# script's exit code.
import json
import os
import subprocess
import sys
import tempfile
import threading
import queue

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")

SCRIPT = """type Shape = Sq of int | Rect of int * int
let area s =
    match s with
    | Sq n -> n * n
    | Rect (a, b) ->
        let w = a * 10
        w * b
let shapes = [Sq 2; Rect (3, 4)]
let total = shapes |> Seq.map area |> Seq.sum
print $"total {total}"
"""

failures = []


class Session:
    def __init__(self, cwd):
        self.p = subprocess.Popen([WEIR, "dap"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, cwd=cwd)
        self.seq = 0
        self.inbox = queue.Queue()
        self.events = []
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        f = self.p.stdout
        while True:
            length = None
            while True:
                line = f.readline()
                if not line:
                    self.inbox.put(None)
                    return
                line = line.strip()
                if not line:
                    break
                if line.lower().startswith(b"content-length:"):
                    length = int(line.split(b":")[1])
            if length is None:
                self.inbox.put({"type": "garbage"})
                return
            self.inbox.put(json.loads(f.read(length)))

    def request(self, command, arguments=None):
        self.seq += 1
        msg = {"seq": self.seq, "type": "request", "command": command}
        if arguments is not None:
            msg["arguments"] = arguments
        body = json.dumps(msg).encode()
        self.p.stdin.write(b"Content-Length: %d\r\n\r\n" % len(body) + body)
        self.p.stdin.flush()
        want = self.seq
        while True:
            m = self.take()
            if m["type"] == "response" and m["request_seq"] == want:
                return m

    def take(self, timeout=30):
        m = self.inbox.get(timeout=timeout)
        if m is None:
            raise RuntimeError("the adapter closed its stream")
        if m["type"] == "garbage":
            raise RuntimeError("something that is not the protocol reached the adapter's stdout")
        if m["type"] == "event":
            self.events.append(m)
        return m

    def wait_event(self, name, seen=0):
        while True:
            hits = [e for e in self.events if e["event"] == name]
            if len(hits) > seen:
                return hits[seen]
            self.take()

    def output(self, category):
        return "".join(e["body"]["output"] for e in self.events if e["event"] == "output" and e["body"]["category"] == category)


def check(cond, what):
    if not cond:
        failures.append(what)


d = tempfile.mkdtemp()
path = os.path.join(d, "d.weir")
with open(path, "w") as f:
    f.write(SCRIPT)

s = Session(d)
r = s.request("initialize", {"adapterID": "weir"})
check(r["success"] and r["body"].get("supportsConfigurationDoneRequest"), f"initialize: {r}")
r = s.request("launch", {"program": path, "cwd": d})
check(r["success"], f"launch: {r}")
s.wait_event("initialized")
r = s.request("setBreakpoints", {"source": {"path": path}, "breakpoints": [{"line": 6}, {"line": 9}]})
check(r["success"] and all(b["verified"] for b in r["body"]["breakpoints"]), f"setBreakpoints: {r}")
s.request("configurationDone")


def frames_now():
    return s.request("stackTrace", {"threadId": 1})["body"]["stackFrames"]


# the statement's own breakpoint first; a step in enters the function
stop = s.wait_event("stopped")
frames = frames_now()
check(stop["body"]["reason"] == "breakpoint" and frames[0]["line"] == 9, f"the first stop is line 9: {stop} {frames}")
s.request("stepIn", {"threadId": 1})
stop = s.wait_event("stopped", 1)
frames = frames_now()
check(stop["body"]["reason"] == "step" and len(frames) == 2 and frames[0]["line"] == 3, f"a step in lands in the function: {frames}")
check(frames[-1]["line"] == 9 and frames[0]["source"]["path"] == path, f"the caller's frame is the line of the call: {frames}")

# on to the breakpoint inside the Rect arm
s.request("continue", {"threadId": 1})
stop = s.wait_event("stopped", 2)
frames = frames_now()
check(stop["body"]["reason"] == "breakpoint" and frames[0]["line"] == 6, f"the breakpoint in the arm: {stop} {frames}")
scopes = s.request("scopes", {"frameId": frames[0]["id"]})["body"]["scopes"]
got = {v["name"]: v["value"] for v in s.request("variables", {"variablesReference": scopes[0]["variablesReference"]})["body"]["variables"]}
check(got.get("a") == "3" and got.get("b") == "4" and "w" not in got, f"the pattern's binds are locals, the let not yet: {got}")
# the caller's frame sees the top level as the statement does
caller = s.request("scopes", {"frameId": frames[-1]["id"]})["body"]["scopes"]
top_vars = s.request("variables", {"variablesReference": caller[1]["variablesReference"]})["body"]["variables"]
check(any(v["name"] == "shapes" for v in top_vars), f"the top level's names: {[v['name'] for v in top_vars]}")

# a step over stays in the frame and the let is bound
s.request("next", {"threadId": 1})
stop = s.wait_event("stopped", 3)
frames = frames_now()
check(len(frames) == 2 and frames[0]["line"] == 7, f"a step over stays in the frame: {frames}")
r = s.request("evaluate", {"expression": "w", "frameId": frames[0]["id"], "context": "hover"})
check(r["success"] and r["body"]["result"] == "30", f"a name evaluates for a hover: {r}")
r = s.request("evaluate", {"expression": "w + 1", "frameId": frames[0]["id"], "context": "repl"})
check(not r["success"], f"only a name is read: {r}")

# a step out leaves the function
s.request("stepOut", {"threadId": 1})
stop = s.wait_event("stopped", 4)
frames = frames_now()
check(stop["body"]["reason"] == "step" and len(frames) == 1, f"a step out lands in the caller: {frames}")

s.request("continue", {"threadId": 1})
exited = s.wait_event("exited")
s.wait_event("terminated")
check(exited["body"]["exitCode"] == 0, f"the script's exit code: {exited}")
check("total 124" in s.output("stdout"), f"the script's stdout arrives as output events: {s.output('stdout')!r}")
s.request("disconnect")
s.p.wait(timeout=10)

# a parallel arm is walked by the flow evaluator [D:eval-twin-entry]: its
# breakpoints stop too, once per arm and line, each stop with its own
# arm's names
par = os.path.join(d, "par.weir")
with open(par, "w") as f:
    f.write("let work n =\n    let out = git --version |> Seq.length\n    n + out\nlet rs = [1; 2] |> Seq.pmap work |> Seq.sum\nprint $\"{rs}\"\n")
s = Session(d)
s.request("initialize", {"adapterID": "weir"})
s.request("launch", {"program": par, "cwd": d})
s.wait_event("initialized")
s.request("setBreakpoints", {"source": {"path": par}, "breakpoints": [{"line": 2}, {"line": 3}]})
s.request("configurationDone")
seen = []
for i in range(4):
    s.wait_event("stopped", i)
    frames = s.request("stackTrace", {"threadId": 1})["body"]["stackFrames"]
    scopes = s.request("scopes", {"frameId": frames[0]["id"]})["body"]["scopes"]
    got = {v["name"]: v["value"] for v in s.request("variables", {"variablesReference": scopes[0]["variablesReference"]})["body"]["variables"]}
    seen.append((frames[0]["line"], got.get("n"), "out" in got))
    s.request("continue", {"threadId": 1})
exited = s.wait_event("exited")
check(sorted(seen) == [(2, "1", False), (2, "2", False), (3, "1", True), (3, "2", True)], f"each arm stops at each line with its own names: {seen}")
check(exited["body"]["exitCode"] == 0 and "5" in s.output("stdout"), f"the parallel script finishes: {exited} {s.output('stdout')!r}")
s.request("disconnect")
s.p.wait(timeout=10)

# a script that fails its check never reaches a breakpoint: its error is
# output, and the session ends with its exit code
bad = os.path.join(d, "bad.weir")
with open(bad, "w") as f:
    f.write("let x = nope + 1\nprint \"unreached\"\n")
s = Session(d)
s.request("initialize", {"adapterID": "weir"})
s.request("launch", {"program": bad, "cwd": d})
s.wait_event("initialized")
s.request("configurationDone")
exited = s.wait_event("exited")
check(exited["body"]["exitCode"] != 0, f"a check error exits non-zero: {exited}")
check("nope" in s.output("stderr"), f"the check error arrives as stderr output: {s.output('stderr')!r}")
s.request("disconnect")
s.p.wait(timeout=10)

if failures:
    for f in failures:
        print("FAIL:", f)
    sys.exit(1)
print("dap-session: ok")
