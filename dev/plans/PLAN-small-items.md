# weir — the small-items sweep: elif, defaulting order, masking
# mechanization, lambda-arm dedup

Status: EXECUTED (landed 2026-07-22) — as blessed: LANDED 2026-07-22 (blessed same day), both sessions.

Completion addenda:
- elif precondition: chained else-if WORKED in both line models —
  no hidden gap; elif was one keyword + a parse desugar + the
  assembler else-family case. no-elif retired.
- Defaulting: the mechanism matched the plan's diagnosis (eager
  TStr bind in checkScalarSplice); fix = a ctx pending list resolved
  at the typecheckWith/typecheckBinder boundary — inside the
  "moving the defaulting point" budget, no stop-and-report needed.
  Bidirectional battery green; zero existing pin edits; soundness
  note re-verified (simpler under the new ordering).
- Masking: stamps via -p:InformationalVersion (clean of the source-
  revision suffix); --version prints it; e2e stamp+mtime gates HARD;
  probes gate via tests/lib/harness.py; waitpid-truth census with
  the zombie + stale-stub selftest pinned. PROCESS.md line added.
- Flag 7: the inventory was FIVE arms (infer unit/name/pattern +
  check name/pattern), not three — all five over one lambdaCore;
  the check-mode unit-param asymmetry (no TUnit pin; pushed dom)
  surfaced VISIBLY in the adapter table. Zero behavior change,
  zero pin edits, TRANSCRIPTION surface smaller.

[Blessed text: see the plan message. All DECIDED items held; no
re-bless triggers fired.]
