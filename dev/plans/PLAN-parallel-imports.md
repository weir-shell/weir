# PLAN: imports check in parallel — the DAG's siblings on worker threads, reported in load order

**Status: BUILT** (2026-10-27, feat/parallel-imports) — R1 as assumed;
see the session report.

## The receipt

The perf session of 2026-10-27 ([PLAN-compat-post-1.0] neighbour,
branch perf/checker-speed) took a script importing the starship port
(6,438 lines, nine vendored files) from 373 ms to 250 ms of check
before its first line runs, and stopped: what remains is parse (110
ms, pinned by diagnostics), inference (~35), assembly (~28), module
merge (~14), GC pauses inside those. Every module is checked in turn.

The port's import graph: three core files (`config`, `fmt`, `ctx` —
`ctx` imports the other two), five `mods/*` files each importing the
three, the entry importing all eight. Sequential cost is the sum of
nine; the critical path is leaves → the slowest mod (`sys`, 1,039
lines) → the entry. On four or more cores that is roughly half.

A user's prompt script is the case: many sibling modules, run on every
prompt. It does nothing for one big file.

## Anchors

- `Script.loadModuleCachedWith` (Script.fs ~5147) — the recursive
  loader: resolves an import, reads the source (`readSourceFile`, or
  `importSourceOverride` for the LSP's open buffers), checks the module
  as a sequence of statements (each `checkStatement`, the command-helper
  table threaded through the fold since perf/checker-speed), and
  `mergeModule`s the result into the importer under its alias. Per-run
  cache keyed by absolute path: each module checks once.
- `Script.importsOfSource` (~5630) — the import lines of a source
  (`(line, col, path)`), cheap, no parse of the rest; the vendor closure
  walk already uses it to discover a tree before fetching.
- `Session.importGuard` (Session.fs ~177) — a MUTABLE list, the cycle
  guard across the recursive load; not thread-safe.
- `Extern` (Extern.fs) — `existsCache` is a ConcurrentDictionary (fine);
  `cache: Set<string> option` (line 6) is a plain mutable (the PATH
  listing) — needs a lock or a lazy.
- `Types.pendingAnonDefs` — ThreadLocal already ("parsing is …" per its
  comment); `AnonLitDefs` is a Dictionary per TypeEnv (per module, fine
  if one thread owns a module's env while it checks).
- `Types.fs ~1266` — an `Interlocked` depth guard around something that
  must be single-flight; find what, and whether a module check reaches
  it.
- The fresh type-variable counter: find it (`newCtx`?); if it is per
  `Ctx` the names are per statement already; if global, its values leak
  into rendered types only through normalisation — prove with the
  parity corpus, not by reading.
- `Session.Cwd` is AsyncLocal [D:tasks-underneath]; a worker thread
  must inherit the entry's cwd (imports resolve relative to files, not
  cwd, but `readSourceFile` and the sig/schema lookups may consult it).
- The LSP: `analyzeCached` and `targetStmts` load modules through the
  same loader with `importSourceOverride`; the one-pipeline parity law
  (`weir check`, the runner, the LSP report byte-identical diagnostics)
  is pinned by unit tests and the `check --json` e2e cells.
- The measurement kit from the perf session lives in /output/tmp
  (`bench.sh`, the 80-file parity comparison; find the script that diffs
  two binaries' `weir check` output over the corpus) — promote the
  parity check to `tools/check-parity.weir` (weir), it is this plan's
  acceptance instrument.

## Shape

1. **Discovery before checking.** From the entry file, read import
   lines transitively (`importsOfSource`, with `importSourceOverride`
   honoured) to build the DAG of absolute paths before any module is
   checked. A cycle is reported exactly as today (`importGuard`'s
   message, on the same line); a missing file likewise. Discovery is
   read-only and cheap (it reads each file once; the check reads it
   again — acceptable, or hand the bytes through).
2. **Scheduling.** A module is ready when all its imports are checked.
   A pool of at most R1 workers takes ready modules in load order (the
   order the sequential loader would have reached them — depth-first,
   import lines top to bottom), so with one worker the behaviour is
   today's exactly. Each worker checks one module with its own
   `TypeEnv`, `Ctx`, and the already-merged envs of its imports.
3. **Reporting in load order.** The entry's statements are checked on
   the main thread as today; when it reaches an import statement it
   WAITS for that module's result. Diagnostics of a module surface where
   the sequential loader would surface them: the first error reported
   is the same error, at the same position, with the same text. A
   module's failure does not cancel its siblings' checks (they are
   independent); it does stop the entry's fold exactly where it stops
   today.
4. **Thread safety (P0, the audit).** `importGuard` becomes per-load
   state passed down (or a lock-free path set per worker chain);
   `Extern.cache` behind a `lazy`; `Session.importGuard` users updated;
   any other module-level mutable reached from `checkStatement`/
   `mergeModule`/`Parser.parseLineFull` found by grep and by a
   stress pin (check the starship port 50× under 8 workers; output
   byte-identical every time).
5. **LSP.** The server's loader shares the scheduler (open buffers via
   `importSourceOverride`); its per-document cache is unchanged.
6. **Not here:** parallel statements within one file (sequential by the
   language's statement rule); a persistent cache (ruled out).

## Rulings

- **R1 — the worker bound.** *Assumed: `Environment.ProcessorCount`*,
  capped at 8 (a prompt should not fan 64 threads on a workstation;
  beyond 8 the DAG rarely has that many ready siblings). Flag if wrong.
  No flag or env var to tune it (no knobs); a single core gives today's
  sequential loader.

## Pins

- Parity: `tools/check-parity.weir` over the corpus (examples, tests,
  docs, skills, the starship files) — byte-identical stdout/stderr/exit
  between the sequential baseline binary and the parallel one.
- Determinism: a fixture with two failing sibling modules — the
  reported error is the first in load order, 50 runs; a fixture where
  the slow module (sleep via a long file) is earlier in load order than
  a fast failing one — still the earlier's error first.
- Cycle and missing-file messages unchanged (existing pins).
- The LSP: `tests/lsp/lsp-e2e.py` green; definition into a module file
  still resolves.
- Benchmark line in CHANGELOG `## v0.0.81` → `### Fixed` (extend the
  launch-speed bullets): starship import before/after medians, with the
  core count stated.
- DECISIONS row `parallel-imports`; this plan BUILT with a session
  report; README line.

## Session report (2026-10-27, feat/parallel-imports)

Built as shaped; [D:parallel-imports] is the ledger row.

- **The audit (phase 0), each mutable a module check could reach.**
  `Session.importGuard` — the RUN-time import guard
  ([D:module-import-no-commands], consulted by Proc at spawn); no check
  reaches it, and the check's cycle guard was already per-load state,
  the `chain` argument. Left alone (deviation from the shape's "becomes
  per-load state": it already was, under another name). `Extern.cache`
  (the PATH listing) — a plain mutable; now behind a lock, `refresh`
  included; `existsCache` was already concurrent. `Check.validatorSeq`
  — Interlocked; parallel order can renumber a validated union's
  `create` id, which is a registration key and never renders (parity
  holds). `Types.pendingAnonDefs`, `Check.toPhys`,
  `Check.preludeLoading`, the parser's flags and depth — ThreadLocal,
  and a module parses and checks on one thread end to end. The
  `Interlocked` depth at Types.fs ~1266 is `Waiting.during`, the REPL's
  spinner — run-time only. Fresh type variables live in `Ctx`
  (`newCtx`, per statement): no global counter. `Session.Cwd` —
  workers `enterWorker` the entry's cwd [D:tasks-underneath]. The
  stress pin: the starship port checked 50× on 15 cores, byte-identical
  to the sequential binary every time (Debug and AOT).
- **The shape.** `Script.ModuleLoads` replaces the loader's
  `Dictionary` cache: `Run key absPath importAs compute` is the one
  door (claim-or-wait, owner-aware so a worker's own nested asks
  compute); `Schedule` takes (key, path, deps, run) in load order and
  `Pump` spawns workers up to the bound while ready modules outnumber
  them, each worker taking the earliest ready module and claiming it
  at pick. `discoverImports` runs lazily at the entry's first import
  line (an import-free script pays nothing), reads each file once into
  a `PreparedSource` the check then takes (the first cut assembled
  every file twice and cost 27 ms before the first module started),
  and schedules a module post-order, as soon as its own imports are
  walked — a leaf checks while the walk continues. The entry's fold is
  untouched: `checkStatement`'s `loadImport` waits at the import line.
  One processor (`Environment.ProcessorCount` honours affinity, so
  `taskset -c 5` is this path) skips discovery entirely.
- **Numbers (AOT, `nproc` = 15, i9-14900K under KVM; medians of 9).**
  Pinned (`taskset -c 5`): starship import 250 → 249 ms, sys.weir
  96 → 94 ms, bare startup 12 → 11 ms — the sequential loader, as
  required. Unpinned: starship import 263 → 215 ms, sys.weir 97 → 106
  ms (noise: unpinned runs on this VM swing 90–130 ms), bare startup
  22 → 29 ms (same noise; pinned says 12). Not the "roughly half" the
  receipt hoped for: the traced timeline shows the five `mods/*`
  modules each taking 105–139 ms concurrently against 23–49 ms alone,
  because the workstation GC's gen0/gen1 marks are serial and stop
  every thread — 86 ms of the 128 ms parallel phase. A gen0 budget of
  256 MB (env knob, measurement only) trades GC count for one long
  mark and page faults: no gain. A server-GC publish measured 150 ms
  unpinned, 247 pinned, bare startup unchanged, +35 MB max RSS (52 →
  86 MB): the next lever, and a separate decision about a CLI's memory
  posture — not taken here.
- **Pins.** `tools/check-parity.weir` (weir; `--base --cand --corpus`,
  `Seq.pmap` over the list, exit 1 on any mismatch, negative control
  `--cand /bin/true` fails as it should): 80 files, 0 differ, both
  unpinned (parallel path) and pinned (one-worker path). Unit pins
  under "parallel imports": two failing siblings 50×, slow-earlier vs
  fast-later 20× (diagnostics publish sorted by file, so the fast
  module's line leads in both loaders — the pin says so), one worker
  == the pool, cycle and missing-file messages, a diamond's one Body.
  `tests/lsp/lsp-e2e.py` green against the AOT binary.
- **Not changed.** `Prelude.extend` still runs per module (its
  `TypeEnv` holds a per-env `AnonLitDefs` dictionary, so it cannot be
  shared across threads without a copy); `importsOfSource` (the vendor
  walk) is untouched.
