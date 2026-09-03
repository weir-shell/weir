# weir — bare commands in match arms

Status: EXECUTED (landed 2026-09-03, proposed same day).

Completion addenda (2026-09-03):
- Probes held: the arm body already parsed chains (probe 1), the
  last-position arm was already capture-typed (probe 2); the gap was
  the boundary + the statement-position discard — the two seams sized.
- Parser seam: `notFollowedBy armBoundaryAhead` on the pipe loop
  (pipedStages), gated on a matchArmDepth ThreadLocal; the boundary
  is `| <pattern>` then `->`/`when`, detected via commaPats +
  followedBy (the guard body is NOT parsed — the `->`/`when`
  requirement alone disambiguates, and reifiers like `| orFail "m"`
  have neither, so they still bind — pinned).
- Checker seam: one armTail arm (EMatch → map armTail over arm
  bodies). Value position never reaches armTail, so capture matches
  are untouched; no check-mode EMatch propagation was needed.
- The documented corner (`echo x | y -> z`) surfaces as the existing
  unreachable-arm teaching under a leading catch-all — no new message;
  quote-repair per the standing precedent.
- A malformed `| Seq.length` (single `|` before a function)
  parse-errors identically on main and this branch — pre-existing,
  not a regression; `|>` checks clean.
- No oracle row — command arms are a bash-prior divergence the
  interior-arming rows already carry.

Originally proposed: the `!()` sigil inventory
after the docs sweep — the sigil's irreplaceable positions reduced
to TWO, and match arms are the one that reads as a gap rather than
a design line: [D:interior-arming] armed if bodies, lambda bodies,
and block-lets, enumerated arms nowhere, and the arm-body parser
ALREADY hosts the machinery (probe receipts below). Landing this
narrows `!()`'s necessity to single-line sequencing alone (`;` is
argv inside a command line — that one is a law, not a gap).

## Probes first — RAN 2026-09-03, against v0.0.14+anon-literals

1. `match 1 with | 1 -> echo only-arm` (no following arm) — PARSES
   TODAY; fails only exhaustiveness. The arm body is
   `withExprParen false seqExpr` (Parser.fs matchArm) — the chain
   grammar is already enabled there.
2. `let out = match 1 with | _ -> echo captured` then `print out` —
   WORKS TODAY, end to end: a LAST-position chain is capture-typed
   `seq<string>`, exactly the let-RHS law. The value half of the
   feature already exists.
3. `| 1 -> echo a` with a FOLLOWING arm — the chain eats `| _ ->` as
   a pipe stage, backtracks, and the expression path reports
   `unbound variable 'echo' — wrap it: $(echo …)` (single-line) or a
   hard parse error (multi-line body). THE GAP IS THE ARM BOUNDARY,
   not the arm body.
4. `match 1 with | _ -> echo hi` in STATEMENT position — checks as
   "computes a seq<string> and discards it": the
   commandish-under-TUnit arming that streams an if body does not
   reach match arms. The checker half of the gap.

## The design (two halves, both precedented)

1. **Parser — the armStop follower.** Inside a match arm's body, a
   `|` followed by a pattern (optional `when` guard) and `->`
   terminates the command chain — added to the pipe-stage boundary
   and reifierEnd's follower set, gated on an in-match-arm
   ThreadLocal depth flag. This is EXACTLY the [D:if-succeeds] shape
   (`thenStop` gated on `ifCondOk`) with a heavier lookahead: the
   stop must attempt `str_ws "|" >>. commaPats >>. opt when >>. "->"`
   rather than match one keyword. The lookahead is attempt-wrapped
   and positional by construction — only arm bodies pay it; top-level
   chains and if bodies never see the flag.
   - THE CORNER, ruled by the standing precedent: a pipe stage whose
     argv happens to spell `pattern ->` (`cmd | x -> y`) now reads as
     an arm boundary INSIDE a match arm. The repair is the
     quote-"then" law one keyword over: quote the word
     (`cmd | "x" …` / `sh -c`) to mean argv. Pin the teaching-shaped
     outcome; do not chase the pathological case further.
   - Nested matches: the flag is a DEPTH counter; an inner match's
     arms stop at the same shape, and arm attribution stays
     innermost-wins — the rule expressions already live by.
2. **Checker — arm the arms under unit.** Extend the
   commandish-under-TUnit rule (Check.fs armTail cluster,
   [D:interior-arming]) through TEMatch: when the match's demanded
   type is unit, each arm's FINAL commandish chain arms (streams,
   raises, unit) — the if-body rule, per arm. Value position stays
   capture-typed (probe 2 becomes a pin, not a change). Mixed arms
   (`| 1 -> git pull | _ -> print "skip"`) unify at unit exactly as
   if/else branches do today.

## What does NOT move

- Non-final chains INSIDE a multi-line arm body already arm
  positionally (seqExpr's fold — probe 3's multi-line case fails at
  the boundary, not the interior).
- The exitCode-discard fatal, raise timings (armed = immediate),
  known-heads/keywords falling through to expression mode — all
  inherited from the seqExpr element, zero new rules.
- `$()` capture, `within env`, the sequencing law (`;` as argv) —
  untouched. `!()` remains legal everywhere it is today (no
  deprecation; the sweep norm just stops modeling it where bare
  works).

## Verification

- Unit pins: interior arm single-line + multi-line body; last arm
  unchanged; capture-position match (probe 2); statement-position
  match streams (probe 4 flips); mixed command/expression arms;
  nested match; guard arm (`| n when n > 1 -> cmd`); the
  pattern-shaped-stage teaching; exitCode-discard inside an arm;
  raise timing.
- e2e: a match dispatching real commands per arm (the case-runner
  idiom), multi-line bodies included.
- Fuzz 3× fresh 10k (parser moved); oracle n/a for command arms
  (bash prior — the arming rows already carry the divergence
  story); timing.
- Docs: SKILL's match bullet + the armed-positions sentence gains
  arms; GUIDE's match section shows a command arm; the `!()`
  inventory in commands.md re-points its example at the ONE
  remaining necessity (single-line sequencing); DECISIONS row
  `match-arm-commands` (amends the interior-arming enumeration);
  CHANGELOG v0.0.15.

## Sizing

Small-to-moderate: one ThreadLocal + one follower arm in the chain
grammar (the if-succeeds costing held at ~1 session for the same
shape), one armTail extension arm, pins. The risk concentrates in
the armStop lookahead's interaction with reifier stages
(`| orFail "m"` must NOT read as an arm — `orFail` is a reifier
head, not a pattern… but `orFail "m"` PARSES as a pattern
(ctor-like) followed by no `->`, so the `->` requirement carries
it; pin `| 1 -> cmd | orFail "m" | _ -> …` explicitly).
