# PLAN: the parse fast path — dispatch on the next character, fall back to the grammar for the error

**Status: SPIKE** (user, 2026-10-27, "start those") — P0 measures;
P1+ is blessed only if P0 clears its bar.

## The receipt

After perf/checker-speed the starship import spends 110 of its 250 ms
in `Parser.parseLineFull` (FParsec), allocating about 870 bytes per
character; most of the 70 ms of GC pauses are that allocation. The
`atom` parser is a `choice` of roughly two dozen alternatives tried in
order before the common case (`wordAtom`); each alternative scans and
backtracks. The session ruled reordering out: FParsec's merged
"Expecting:" lists reach users verbatim (`Script.rebuildExpecting`,
Parser.fs ~612 and ~3211 shape them), so changing the alternatives or
their order changes diagnostics.

## The idea

Keep the grammar as the oracle for errors; skip it on the happy path.

A fast path peeks at the next character (or a short prefix) and runs
ONLY the alternative it determines — `"` → string, `$"` → interpolated,
`@"` → verbatim, `[` → list, `(` → parens/unit/tuple, `{` → record,
a digit → number, `fun` → lambda, an identifier start → `wordAtom`, and
so on. If that alternative SUCCEEDS, its result is what `choice` would
have produced (the alternatives are mutually exclusive on their first
token — this must be proved per alternative, P0 lists them). If it
FAILS, the fast path does not report: it restores the position and runs
the ORIGINAL `choice`, which fails the same way it does today, with the
same merged Expecting list. Diagnostics are identical by construction:
every error is produced by the unchanged grammar; the fast path only
ever returns successes the grammar would have returned.

The same shape applies to any `choice` on the hot path (statement
heads, command segments vs expressions, patterns) one at a time, each
with its own parity run.

## Anchors

- `Parser.fs`: the `atom`/`wordAtom` choice (find it — `choice [` with
  many alternatives in the expression grammar), `appChain` (~1811),
  `parens` (~1014), `letInBody` (~2082), `deepen`/`undeepen` (the depth
  guard around nesting, TLS), `litHead` (~4477), `commandSegment`
  (~4625).
- `Script.rebuildExpecting` (~3477) — the one place Expecting text is
  reshaped; proves which errors carry alternative lists.
- The parity corpus and comparison script from the perf session in
  /output/tmp (bench.sh and the 80-file diff; 62 error cases) — the
  acceptance instrument; promote to `tools/check-parity.weir` if the
  parallel-imports branch has not already (coordinate: that branch
  owns the promotion; this one uses it).
- The perf session's measurement discipline: AOT binary only,
  `taskset -c 5`, medians of 9, the three benchmarks (starship import,
  `sys.weir`, bare).

## Phases

- **P0 — the spike (this session).** Implement the dispatch for `atom`
  only, exactly as described (peek, one alternative, on failure replay
  the original choice from the saved position). Enumerate the
  alternatives with their first-token classes and show they are
  disjoint (any overlap → that alternative stays on the slow path).
  Measure the three benchmarks. Run the parity corpus. BAR: starship
  import improves by at least 20 ms with parity byte-identical;
  otherwise the plan closes with the numbers and the reason.
- **P1 — the other hot choices** (statement head, command-vs-expression
  segment, pattern atoms), one commit each with numbers and parity.
- **P2 — allocation.** With dispatch in place, the per-character
  allocation profile (which combinators allocate on success) — only
  what the trace shows; no speculative rewrites.
- Ledger: CHANGELOG under `## v0.0.81` → `### Fixed` (extend the
  launch-speed bullets); DECISIONS row `parse-fast-path` (the
  oracle-grammar law: errors always come from the unchanged grammar);
  this plan's status and session report.

## Not here

- A hand-written lexer or parser replacing FParsec (the redesign this
  plan avoids).
- Any change to error text, position, or which error is first.
- Reordering `choice` alternatives.
