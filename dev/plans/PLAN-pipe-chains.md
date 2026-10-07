# PLAN: pipe chains — the early-exit hang, reifiers on chains, the let-RHS teaching

**Status: APPROVED 2026-10-07** (both rulings as recommended: a chain's
code is the LEFTMOST failing stage's; `complete.stderr` concatenates every
stage's stderr in stage order). Found while answering "show me a reifier
after a pipeline".

## 1. A consumer that exits early hangs the chain (bug)

- **Probe:** `seq 1 1000000 | head -1` hangs as a statement (prints `1`,
  never returns), as `$(…)` and on a `let` RHS; `$(seq 1 10 | head -1)` is
  instant; `$(git log --oneline | head -1)` hangs in this repo. Any
  producer that writes more than a pipe buffer after its consumer quits.
- **Cause:** [D:byte-pipes]' pump (`Proc.pumpBytes`) swallows the failed
  write when the downstream stage exits and closes only the downstream's
  stdin — never the upstream's stdout. The producer blocks on a full pipe
  forever while the chain waits to reap it.
- **Fix:** a failed downstream write closes the upstream's stdout read end
  (the producer gets EPIPE/SIGPIPE, as in a shell pipe) and the pump reports
  "the downstream stopped first".
- **The exit-law ruling:** a non-final stage whose downstream stopped
  reading first was told to stop — its nonzero exit (SIGPIPE's 141, or a
  "write error" exit when SIGPIPE is ignored) is NOT a failure. A stage
  that fails while its downstream still reads raises as today (the
  leftmost failing stage, [D:byte-pipes]). bash without pipefail ignores
  upstream codes entirely; fish likewise; weir keeps pipefail for real
  failures and exempts only the told-to-stop case.

## 2. Reifiers after a chain

- **Today:** `a | b | line` (and every reifier) is refused — "must directly
  follow a single external command segment"; the ledger records it as
  "no new law", never ruled. Since [D:byte-pipes] a chain is one unit (one
  spawn plan, one exit law), and `$(a | b)` already captures one.
- **Change:** a reifier may follow a command chain (optionally value-headed,
  `xs | a | b | line`):

  | reifier | on `a \| b \| c` |
  |---|---|
  | `\| line` / `\| text` | the tail's stdout under the existing line law; any (non-exempt) stage failure raises |
  | `\| orFail "msg"` | streams the tail; raises `msg (exit N)` with the leftmost failing code |
  | `\| succeeds` | true iff no stage failed (pipefail, with the told-to-stop exemption) |
  | `\| exitCode` | streams; the LEFTMOST failing stage's code, or 0 |
  | `\| complete` | `stdout` = the tail's; `exitCode` = the leftmost failing code or 0; `stderr` = every stage's stderr concatenated in stage order |
  | `\| exec` | stays refused — a pipeline cannot replace the process (teaching names `sh -c "a \| b" \| exec`) |

- **Mechanism:** the parser desugars a reified chain to one builtin per
  reifier (`|chainCompleted`, `|chainSucceeded`, `|chainExitCoded`,
  `|chainOrFailed`, `|chainLined`, `|chainTexted`) applied to the stage list
  — each stage a `(prog, argv, env)` tuple built exactly as a single
  segment's (literal/dynamic head, splat chunks, per-stage env prefix) —
  and an optional stdin (`Some xs` for a value head). Proc gains the chain
  twins of `completedOf` and `streamCodeOf`.

## 3. The let-RHS refusal teaches

`let n = a | b | exec` (and anything still refused) died as a raw
"Expecting: identifier …" dump on a let RHS while the statement form taught.
The refusal keeps the teaching in both positions.

## Tests

Units: the pump exemption (a told-to-stop producer is not a failure; a
genuine early failure still raises), each reifier on a 2- and 3-stage chain,
value-headed chains, per-stage env, dynamic heads, splats, exec refused.
e2e: `seq 1 1000000 | head -1` in statement/capture/let positions returns
promptly; `git log | head -1 | line`; `false | cat | succeeds` is false;
`complete` stderr order. Fuzz ×3 (parser change).
