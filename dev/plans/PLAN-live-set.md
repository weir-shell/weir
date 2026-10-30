# PLAN: the live set — what an import keeps alive is what every collection pays for

**Status: BUILT** (2026-10-30, perf/live-set). The round after
[PLAN-checker-alloc.md](PLAN-checker-alloc.md), stacked on it. That
round's read: the collections' cost was never their count — a gen0
with nothing alive is a page flip — it was the ~1 MB that survived
each one and got marked and compacted, and the gen2 that fired once
enough of it had been promoted. This round shrinks what survives and
what each node weighs; see the session report at the end.

## The receipt

At perf/checker-alloc (AOT, starship import pinned to one core):
105 ms, 72.6 MB allocated, 9 collections (7 gen0, 1 gen1, 1 gen2) —
45 ms of pause, ~1 MB promoted out of every gen0, 7 MB out of gen1,
the gen2 (16 ms) tripped mid-inference once gen1 filled. The GC's own
knobs, probed on the AOT binary with RSS measured: a 16 MB gen0 budget
halves the collections and gains nothing (108 vs 107 ms; the fresh
pages cost what the collections did), 32 MB loses 15 ms, a gen1
budget changes nothing, and concurrent GC off is slower and bigger.
So the lever is the live set, not the budget.

What was alive: every imported module's `Body` — its logical lines,
AST and typed statements — held in the load cache and the importer's
`CImport` until exit, for a replay (`run`), a capability walk
(`check --can`) or an editor (LSP) that a plain `weir check` never
performs. And every tree node carried its `Span` as a heap object
(`Pos` was already a struct).

## The idea

1. **A check-only run keeps no module body** [D:check-drops-bodies].
   `weir check` sets `Script.dropModuleBodies`; a loaded module then
   carries `Body = []` once its own load-time passes have read it.
   `#sig` loads keep theirs (the sig loader reads them after). run,
   REPL, LSP and `--can` are untouched.
2. **`Span` is a struct** like `Pos`: one object fewer per `Expr`,
   `TypedExpr` and `Pattern` node, no syntax change anywhere.
3. **The assembler allocates no intermediate strings** — the
   perf/assemble-once commit re-applied on the formatted tree: the
   scanner probes openers on their three characters and stops at the
   comment, the col-0 checks read the line in place, the joins append
   their parts.

## Anchors

- No diagnostic or tree changes: the oracle, parity, `fmt` parity,
  the unit, fuzz, LSP and e2e batteries pin it.
- The dropped bodies are a mode of one entry point; anything that
  reads `lm.Body` (`Can.walkStmts`, the LSP's hover into modules,
  the runner's replay and `recordModule`, the `#sig` reader) runs in
  a process that never sets the flag — `#sig` is the one that shares
  the process and keeps its body by `sigContract`.

## Not here

- The GC budget (declined, and measured useless here anyway).
- Interned names, span-based lexing without line strings: the next
  tier if it is ever needed; this round reached the target without.

## Session report — 2026-10-30, perf/live-set

- **Numbers** (AOT, alternating A/B, medians of 21, base =
  perf/checker-alloc): the body drop alone took the starship import
  from 106.8 to 96.4 ms pinned and 109 to 87 ms on 15, collections 9
  → 9 but the gen2 gone and total pause 45 → 29 ms; `Span` as a
  struct 108.4 → 102.6 ms pinned on its own; with the assembler
  re-applied the branch stands at 107.3 → 97.5 ms pinned, 106.6 →
  81.6 ms on 15, `sys.weir` 67.9 → 53.4 ms, bare startup 10.0 → 9.6
  ms; 8 collections (7 gen0, 1 gen1), 27.6 ms of pause, 8 MB
  promoted out of gen0 over the run (was 10).
- **Probes that said no.** `DOTNET_GCgen0size` 16 MB: 108 vs 107 ms
  for +5 MB RSS; 32 MB: 123 ms, +27 MB; `GCgen1size`: no change;
  `gcConcurrent=0`: 111 ms and +20 MB. The budget is not the lever.
- **Read.** What survives a gen0 is what the run still points at;
  the modules' bodies were most of it and nothing in a check read
  them again. ~1 MB still survives each gen0 (the entry's statements,
  the env maps, schemes, the prepared sources until each module takes
  its own) — the next tier, if ever wanted, is the merge's per-import
  rebuild (2.4 MB, the top site now) and `bindValue`'s env copy per
  binder (2.2 MB).
