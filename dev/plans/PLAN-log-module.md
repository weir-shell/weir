# weir — the `Log` module: levelled diagnostics that respect the pipeline

Status: EXECUTED (2026-08-01; blessed same day with four rulings
folded in — default `info`, no structured logging, the lazy `With`
twins, `Log.error` DROPPED). Session report: all work items landed;
see [D:log-module] and NOTES "the Log module". The eager-argument
question ruled as recommended (1+2: eager plain members + thunk
twins, side-effect-pinned both directions). One discovery: F# module
abbreviations are file-local, so sharing the Color palette with
Builtins meant moving it to Types (Script keeps an internal alias;
Repl/Program repointed). The battery: stdout byte-identity at every
level (headline), off-silence, invalid-value startup error naming
the levels, plain-form-when-piped, env-sigil child composition, -e,
and the REPL prompt probe. `warn` tops the filterable range; the
three-channel table (Log/printerr/fail) is in SKILL, GUIDE, and the
DECISIONS row.
