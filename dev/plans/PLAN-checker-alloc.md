# PLAN: the checker allocates less — the per-statement substitution, the post-check walks, the type finalizer, instantiation

**Status: BUILT** (2026-10-30, perf/checker-alloc) — cuts 1–4 landed
plus the walks the profile surfaced on the way; cut 5 (merge) only
its cheap part; see the session report at the end. The round after
[PLAN-hand-parser.md](PLAN-hand-parser.md) P1: with the parser off
the critical path, the import's time is the checker's allocation and
the garbage collections it buys. Same discipline as every round since
perf/survivors: one cut at a time, each measured on the AOT binary
(alternating A/B, medians of 21) and under the allocation profiler,
the full battery at the end. No GC budget tuning (that is the memory
trade already declined) and nothing that touches binary size.

## The receipt

The starship import at feat/hand-parser-p1 (AOT, pinned to one core):
126 ms, 84 MB allocated, ten collections — nine gen0 (one every 8 MB,
~4 ms each) and one gen2 (16 ms) tripped mid-inference — 52 ms of
pause in a 126 ms run. CPU self-time: the collector ~25%, the write
barrier 7%, the allocator 4%, string compares and Map/Set tree
operations ~6% (every substitution, row and env lookup is a
string-keyed tree). The checker's own logic is small; the cost is
what it allocates. By phase: inference 27.7 MB; the post-check walks
over the typed tree 17.4 MB; module merge 6.6 MB.

By site, the first ten weir-owned: `Check.childExprs` 3.6 MB (a list
per node, consumed by ~45 walks across Check, Purity and Script),
`Script.mergeModule` 2.8 MB, `finalTy`'s `goList` 2.6 MB plus its
`go` closure 1.4 MB (the finalizer runs on every typed node's type),
`Purity.pureViolation` 1.9 MB, `Types.bindValue` 1.9 MB, `funParams`
1.4 MB, `bind` 1.4 MB (a `Map.add` path of nodes per unification),
`instantiate`'s `rename` 1.7 MB across infer and merge, `reachesCommand`
0.8 MB.

## The idea

Five cuts, in payoff order. Each is local to one mechanism, changes no
diagnostic and no tree, and is measured before the next starts.

1. **The substitution and the rows are dictionaries.** `Ctx.Subst`
   and `Ctx.Rows` are per-statement mutable `Map`s that only ever
   grow (`Map.add`) and are read by key (`Map.tryFind`); nothing
   snapshots or compares them. A `Dictionary` hashes once per lookup
   and allocates nothing per bind. Hits `bind`, `resolve` (the step
   every walk pays) and the compare cost.
2. **The post-check walks stop materialising children.** One
   primitive, `tryPickChild`, visits a node's children in
   `childExprs` order without building the list; `existsChild`,
   `forallChild`, `iterChildren` derive from it. Every walk on the
   import path (Check's usage, re-enumeration and command walks;
   Purity's effect, mutation, plan and exec walks; Script's statement
   walks) moves to the primitives; `childExprs` stays for the LSP and
   completion, which are not on the path.
3. **The finalizer allocates no closure.** `finalTy`'s `go` closes
   over `ctx` and `seen` per call; `finalizeExpr` calls it per node.
   A plain recursive function over explicit arguments, an
   allocation-free `hasVars`/`existsVar` (today: build the `Set` of
   vars, test it empty), and a var-free fast path that returns the
   input unchanged.
4. **Instantiation's small-scheme path.** `rowNames` unions Sets at
   every node and the var mapping is a `Map.ofList` even for a
   one-var scheme. Compute row names only when the scheme has a row
   var; an array mapping for small `Forall`.
5. **Module merge and env extension.** `mergeModule` rebuilds its
   rename tables per import and `bindValue` re-adds values one by
   one; starship imports many modules. Read first, then bulk-build
   or share what is per-module constant.

Expected from the byte counts, not measured: 84 → ~55 MB, four or
five collections instead of ten and likely no gen2, so ~25 ms of
pause back plus the allocator and barrier share — ~126 → ~100 ms
pinned.

## Anchors

- The oracle law is untouched: no parser change. The checker's output
  (trees, types, diagnostics) is pinned by the unit suite, the fuzz
  suite, parity (`tools/check-parity.weir` over the corpora against
  the branch's base), `fmt --check` parity, the LSP suites and the
  e2e battery.
- `Ctx.Subst`/`Ctx.Rows` as dictionaries is sound only while nothing
  snapshots them; the one `{ newCtx () with … }` copies a fresh ctx.
  A future speculative check that needs rollback adds it explicitly.
- Walk order is `childExprs` order, so a `tryPick` returns the same
  first hit as before.

## Pins

- Unit: a walk-order pin — `tryPickChild` and `childExprs` agree on
  the first hit over a tree covering every `TypedExpr` kind.
- The existing batteries (above), run at the end.

## Not here

- GC configuration (gen0 budget, server/concurrent GC): declined, the
  RSS trade.
- Interning type-variable names as ints: the whole `Ty` surface
  changes for a few percent of compares; not worth it while
  allocation dominates.
- P2 of the hand parser (command lines).

## Session report — 2026-10-30, perf/checker-alloc

- **Landed, in profile order.** `Ctx.Subst`/`Ctx.Rows` as
  dictionaries. `tryPickChild`/`iterChildren`/`existsChild`/
  `forallChild` and their `Expr` twins (`tryPickExprChild`,
  `iterExprChildren`) replace `childExprs`/`exprChildren` lists in
  every walk on the check path (Check's usage, re-enumeration,
  command and exit-site walks; Purity's effect, mutation, plan and
  exec walks; the parser's post-parse `pipeToCommand` and depth walks;
  Script's statement walks and `loadTargetsOf`). `finalTy` as a
  closure-free `finalGo`, `hasVars` without a set. `mapDrop`/`setDrop`
  guard every scope removal (`Map.remove`/`Set.remove` rebuild the
  search path even when the key is absent — `bindValue`'s open index,
  Purity's scopes, the re-enumeration trackers, `reachesCommand`).
  `bindingUsage` is one scoped pass with a frame per binder, no set
  unions, returning a hash set. The application spine reads
  `funParams` once and checks arguments in a loop instead of a fold of
  closures; `funParams` accumulates instead of mapping options.
  `instantiate` probes for row vars before building their set, lifts
  its helpers to module functions and shares var-free subtrees.
  `pureViolation` drops its per-node closures. `mergeModule` scans
  module types without listing them; the reachable-types walk uses a
  hash set. The fast parser caches its word lookahead per position.
- **Numbers** (AOT, alternating A/B, medians of 31, base =
  feat/hand-parser-p1 d91c633): starship import 126.4 → 105.5 ms
  pinned to one core (−17%); unpinned 98 → 101, inside the run-to-run
  spread of that measurement; `sys.weir` 54.8 → 56.5, same; bare
  startup 9.65 → 9.09. Allocation 83.9 → 72.6 MB, collections 10 → 9
  (7 gen0, 1 gen1, 1 gen2), pause 52 → 45 ms.
- **Agreement.** Unit 2,813 (plain and `WEIR_PARSE_ORACLE=1`, with
  the new child-walk pin and the existing corpus pin that holds
  `bindingUsage`'s free set to the occurrence walk's), fuzz 56
  (oracle), parity 0 differences on all 8,030 corpus files and `fmt
  --check` on 1,080, the three LSP suites, the three e2e shards
  (1,122 body cells), decision-keys and commit-area.
- **Read.** The byte counts were right about where allocation was and
  wrong about how much each site weighed once removed: the list-free
  walks and the usage pass each moved 3–4 MB, the dictionaries less
  than 1 MB, and the time came mostly from CPU (closures, string
  compares, `Set.union`), not from the collections — the count only
  fell by one. What remains is spread thin (`bindValue`'s env copies
  2.7 MB, merge 7 MB, the parser's spans and words 14 MB, assembly 9
  MB) and the pauses are what they are because ~1 MB survives each
  gen0: every module's logical lines, AST and typed body stay alive to
  the end for the post-check passes and the run-time replay. The next
  round is the live set, not the allocation rate:
  [PLAN-live-set.md](PLAN-live-set.md).
