#!/usr/bin/env python3
# The depth-coverage gate [D:depth-guard]: every recursive cycle in the
# parser routes through `deepen`, asserted mechanically rather than
# remembered. The adversarial review found the type and pattern grammars
# unguarded by attacking a NAMED axis and then asking what else existed —
# a search answers "what did I find", only an enumeration answers "what
# is there". This is the enumeration: build the reference graph between
# top-level parser bindings (the same lexical extraction discipline as
# grammar-manifest.py), delete every node whose definition is wrapped in
# `deepen (`, and require the residual graph acyclic. A new recursive
# nonterminal that skips `deepen` shows up here as a named cycle, not as
# a SEGV report.
#
# In FParsec every grammar cycle passes through a forwarded ref or a
# top-level function, both of which are col-0 bindings — so col-0
# extraction sees every cycle. Over-approximation (a name mentioned in a
# non-recursive position) can only ADD edges, so a green run is safe and
# a red run names its cycle for a human read.
import re, sys, os

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
src = open(f"{root}/src/Weir/Parser.fs").read()

# strip strings, char literals, and comments so identifier matches are
# code, not prose (order matters: strings before line comments)
strip_pat = re.compile(
    r'"""(?:[^"]|"(?!""))*"""'  # triple-quoted
    r'|@"(?:[^"]|"")*"'  # verbatim
    r'|\$?"(?:\\.|[^"\\])*"'  # plain / interpolated
    r"|'(?:\\.|[^'\\])'"  # char literal ('a type vars have no closing quote)
    r"|//[^\n]*"  # line comment
    r"|\(\*(?:.|\n)*?\*\)",  # block comment
    re.M,
)
code = strip_pat.sub(lambda m: "\n" * m.group(0).count("\n"), src)

lines = code.split("\n")

# top-level definitions: col-0 `let` bindings (incl. forwarded-ref
# pairs), `<x>Ref.Value <-` / `<x>Impl <-` / `<x>.TermParser <-`
let_re = re.compile(r"^let\s+(?:private\s+|rec\s+|mutable\s+)*(\w+)(?:\s*,\s*private\s+(\w+))?")
rec_re = re.compile(r"^let\s+(?:private\s+)?rec\s|^let\s+rec\s")
asg_re = re.compile(r"^(\w+)(?:\.Value|\.TermParser)?\s*<-")

# recursive AST walkers whose depth is bounded by the deepen'd parse
# that built the tree (patLeafNames), or tail recursion the compiler
# turns into a loop (exitCodeSpine) — not input-driven stack growth
AST_WALKERS = {"patLeafNames", "exitCodeSpine"}

ref_to_node = {}  # exprRef -> expr
defs = []  # (node, start_line, is_rec)
for i, ln in enumerate(lines):
    m = let_re.match(ln)
    if m:
        defs.append((m.group(1), i, bool(rec_re.match(ln))))
        if m.group(2):  # forwarded pair: `let p, private pRef = ...`
            ref_to_node[m.group(2)] = m.group(1)
        continue
    m = asg_re.match(ln)
    if m:
        defs.append((ref_to_node.get(m.group(1), m.group(1)), i, False))

# a node with several defs (forwarded decl + ref assignment) is guarded
# when ANY def's body opens with `deepen` (possibly under a `fun` arg,
# the parameterized-parser spelling)

# body of a definition runs to the next col-0 code line
tops = {i for i, ln in enumerate(lines) if ln and not ln[0].isspace()}


def body(start):
    out = [lines[start].split("<-", 1)[-1] if "<-" in lines[start] else lines[start].split("=", 1)[-1]]
    for j in range(start + 1, len(lines)):
        if j in tops:
            break
        out.append(lines[j])
    return "\n".join(out)


nodes = {}  # node -> list of def bodies (a node can have decl + ref assignment)
rec_nodes = set()
for name, start, is_rec in defs:
    nodes.setdefault(name, []).append(body(start))
    if is_rec:
        rec_nodes.add(name)

guard_re = re.compile(r"\s*(?:fun\s+\w+\s*->\s*)?deepen(?:After)?\b")
# the hand-rolled spelling for non-FParsec recursion (parseTplBlock):
# the body bumps and checks the same counter deepen uses
manual_guard_re = re.compile(r"parseDepth\.Value > maxDepth")
guarded = {n for n, bs in nodes.items() if any(guard_re.match(b) or manual_guard_re.search(b) for b in bs)}

word = re.compile(r"\b(\w+)\b")
local_let = re.compile(r"\blet\s+(?:rec\s+|mutable\s+)?(\w+)")
edges = {n: set() for n in nodes}
for n, bs in nodes.items():
    b = "\n".join(bs)
    # names re-bound locally inside the body shadow the top-level
    # parser (mkOpp's local `opp`) — mentions of them are not edges
    shadowed = set(local_let.findall(b)) - {n}
    for w in word.findall(b):
        t = ref_to_node.get(w, w)
        if t in shadowed:
            continue
        if t in nodes and t != n:
            edges[n].add(t)
        elif t == n and (w != n or n in rec_nodes):
            # self-edge via the forwarded ref, or a `let rec` binding —
            # a plain self-mention in a non-rec `let` is shadowing
            edges[n].add(t)

# recursive AST walkers are exempt: their depth is the tree's, and the
# tree came from a deepen'd parse
for n in AST_WALKERS:
    edges.get(n, set()).discard(n)

# residual graph must be acyclic: Tarjan SCC over the unguarded nodes
live = {n for n in nodes if n not in guarded}
g = {n: sorted(t for t in edges[n] if t in live and t != n) for n in live}
self_loops = {n for n in live if n in edges[n]}

sys.setrecursionlimit(10000)
index, low, onstack, stack, sccs = {}, {}, set(), [], []


def tarjan(v):
    index[v] = low[v] = len(index)
    stack.append(v)
    onstack.add(v)
    for w in g[v]:
        if w not in index:
            tarjan(w)
            low[v] = min(low[v], low[w])
        elif w in onstack:
            low[v] = min(low[v], index[w])
    if low[v] == index[v]:
        scc = []
        while True:
            w = stack.pop()
            onstack.discard(w)
            scc.append(w)
            if w == v:
                break
        if len(scc) > 1:
            sccs.append(sorted(scc))


for v in sorted(live):
    if v not in index:
        tarjan(v)

bad = sccs or self_loops
if bad:
    print("depth-coverage FAIL: recursive parser cycle(s) that never route through deepen:", file=sys.stderr)
    for scc in sccs:
        members = set(scc)
        for n in scc:
            print(f"  {n} -> {', '.join(t for t in g[n] if t in members)}", file=sys.stderr)
        print(file=sys.stderr)
    for n in sorted(self_loops):
        print(f"  {n} -> {n} (self)", file=sys.stderr)
    sys.exit(1)

if not guarded:
    print("depth-coverage FAIL: no deepen-guarded parser found — the extractor broke, not the grammar", file=sys.stderr)
    sys.exit(1)

print(f"depth-coverage ok: {len(nodes)} parser bindings, {len(guarded)} deepen-guarded, residual graph acyclic")
