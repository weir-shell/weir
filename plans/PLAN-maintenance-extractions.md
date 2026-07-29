# PLAN — maintenance extractions (sweep backlog)

Backlog from the 2026-07-29 maintenance sweep (5 movements, zero
behavior / zero pin movement). The sweep confirmed the tree is
healthy — this doc holds ONLY the sized refactor findings it surfaced,
for future one-per-branch sessions. Every item here is a **finding**,
not a decision: none is blessed for execution. Each is zero-behavior
by construction (pure extraction / relocation); the bar for any of
them is byte-identical behavior and zero pin movement, proven by the
existing battery before and after.

Sweep denominators (context): 79 property-claims verified (0 false),
122 DECISIONS keys / 0 dangling in src, 20 dup 6-line windows (no new
duplication), 0 warnings / 0 TODO, 827 unit + 183 e2e + 30 lsp-e2e
pins (0 tautologies, 20/20 sample meaningful).

## Two misplaced jobs (highest architectural value)

1. **Script.fs hosts the runtime `exec` engine** (2376–2436, ~61 L).
   Script.fs's job is text assembly + statement checking — no
   evaluation lives there EXCEPT `exec`, which drives runtime
   statement execution and belongs in Eval (or a new `Executor.fs`).
   Its 4 arms (CLetPat/CLet/CCmd/CExpr) also share a verbatim
   `try … with ExitRequest code -> code | ex -> …; 1` envelope
   (M2 flagged the ×4 window) — extract the envelope into one helper
   as part of the move. Risk: involved (exception + venv threading
   move to the new owner); the envelope-dedup alone is trivial.

2. **Parser.fs hosts the depth guard** (534–572 exception + throw,
   1788–1802 `exprTooDeep` walk). Depth is a semantic property of the
   parsed tree, not a syntax rule — it belongs in a post-parse
   validation pass (`Weir.Validation`), not in the recursive descent.
   Risk: involved (a cross-cutting concern touching both during-parse
   and after-parse). [D:depth-guard] governs the behavior; keep it.

## Sized extraction candidates

| File | Candidate | Lines | ~L | Risk | Why |
|---|---|---|---|---|---|
| Eval.fs | argv parse/validate cluster → own `Argv`-runtime module | 635–945 | 310 | involved | self-contained subsystem (7 helpers), mirrors check-side Argv |
| Script.fs | `assemble` state machine — split bracket (761–848) + district (850–906) logic | 661–1220 | 560 | moderate | 16-level nesting, 8 state fields, 4 concerns; preserve the fold algebra |
| Builtins.fs | builtin-docs Map → `Builtins.Docs.fs` | 1261–1627 | 367 | involved (mechanical) | manually-maintained doc source; extraction is a tuple/import reshape |
| Parser.fs | pattern subgrammar → submodule | 669–996 | 328 | moderate | own grammar (10 parsers); watch forwarding-ref init order |
| Check.fs | Args/Env boundary validation out of `infer` | 1273–1546 | 270 | involved | boundary/domain logic tangled into the typing spine |
| Builtins.fs | reifier `*WithIn` family → one templated builder | 194–358 | 165 | moderate | ×3 triple-curry match skeleton (M2 window); vary Proc call + result shape |
| Check.fs | `checkPattern` regex sub-validation → `checkRegexPattern` | 776–809 | 50 | moderate | self-contained binder/capture/arity unit |
| Check.fs | class solver per-class helpers (`satisfiesEq/Show/Ord`) | 318–377 | 60 | moderate | pure type logic, deeply nested `decompose` |
| Script.fs | `exec` 4-arm `try/with` envelope → helper | 2388–2436 | 50 | trivial | verbatim ×4 (do with move #1 above) |
| Lsp.fs | inline JSON result builders → `writeRange`/`writeLocation`/`writeTextEdit` | scattered | 30 | trivial | each handler writes its own range/location object |
| Lsp.fs | completion field-repair → pure `repairHeadPrefix` | 1184–1240 | 57 | trivial | error-recovery buried in the handler |

## Not-a-finding, recorded so it is not re-hunted

- **The 3 hover lookup paths** (Lsp.hoverType, ~712–864) read as
  parallel but did NOT trip the 6-line dedupe detector — structurally
  similar, textually distinct. **No merge is owed.** A future reader
  tempted to unify them should re-confirm they stay below the verbatim
  threshold rather than force a shared abstraction that couples the
  priority order.
- Check.fs / Eval.fs cores (inference, unification, evaluation) are
  well-factored; the candidates above are the boundary/domain
  intruders, not the cores.

## Ledger gap (separate, needs decision text)

`[D:command-district]` and `[D:command-sigils]` were proposed in
NOTES.md (line 1901, "Proposed …") and their plans executed
(4791/4846) but **never ratified into DECISIONS.md**. The features
landed; the index rows are missing. Author the two rows from the
decision content — not an extraction, a ledger repair.
