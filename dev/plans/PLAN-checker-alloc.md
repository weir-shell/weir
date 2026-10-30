# PLAN: the checker allocates less — the per-statement substitution, the post-check walks, the type finalizer, instantiation

**Status: BLESSED** (2026-10-30, perf/checker-alloc). The round after
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
