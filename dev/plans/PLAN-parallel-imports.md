# PLAN: imports check in parallel — the DAG's siblings on worker threads, reported in load order

**Status: RULED** (user, 2026-10-27, "start those") — R1 assumed as
stated, flagged.

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
