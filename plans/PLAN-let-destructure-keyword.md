# weir — the last keyword slot: let-destructure

Status: DRAFT — the single remaining keyword-domination finding after
[D:keyword-slots-residue]. `let (rec) = 1` (and `let (a, rec) = …`)
still buries its keyword teaching (reported at 1:5, the `(`, with an
expecting-list) while every other binder slot now dominates. One small
session, diagnosis-first; may legitimately end a stated finding if the
clean fix wants a commit boundary.

## The mechanism (the predictor)

A dominating fatal surfaces only where the context is committed and NOT
inside a swallowing `attempt` (LEXICON: a fatal inside an attempt is
not a fatal). `patWord` already fatals on a keyword — it dominates for
match-arm/lambda/param because those are committed, and it is SWALLOWED
here because SLetPat wraps the destructure binder in an `attempt`.

## Why this one is different

`SLetPat` (stmtWith) is `attempt (keyword "let" >>. binderPat .>> "="
>>= discriminate … .>>. rhs .>> eof)`. Its `attempt` is load-bearing:
it discriminates the THREE let shapes — a PLAIN binder (`let x = …`)
backtracks to `topLet` (via the `fail "plain binder takes the ident
path"`), an expression let-in (`let (x,y) = v in body`) backtracks to
the expression grammar, and only a true destructuring STATEMENT stays.
So a fatal raised while parsing `binderPat` is swallowed by the very
attempt that makes that three-way discrimination work.

## Options (weigh in writing; do not silently pick)

1. **A destructure keyword guard**, OUTSIDE SLetPat's attempt, like
   `letKeywordGuard` but pattern-aware: peek `let (` then scan the
   parenthesised pattern for a keyword binder and fatal at it. Cost:
   the guard must parse a pattern (nesting, tuples, `{ }` destructure),
   which duplicates pattern logic — or reuse `binderPat` under a
   `lookAhead` and re-raise. DIAGNOSE: can a `lookAhead binderPat`
   observe patWord's fatal and re-raise it outside the attempt, or does
   lookAhead swallow it too? (The property predicts lookAhead, being a
   backtrack, swallows — so the guard likely needs its own keyword scan,
   the cost above.)
2. **Narrow SLetPat's attempt** so `binderPat` parses OUTSIDE it (the
   negAtom precedent — the attempt covers only the DISCRIMINATION, the
   pattern body commits after). Cost: the discrimination reads the
   binderPat's SHAPE (`PVar` → plain, else destructure) AND needs `= …
   in body` lookahead for the expression-letin case, so the boundary is
   entangled with the pattern parse — this is the commit-boundary the
   plan warned about. STOP-and-report if narrowing wants to renegotiate
   the plain-vs-destructure-vs-letin discrimination.
3. **Stated finding**: if neither (1) nor (2) dominates without cost
   the remit forbids, record the boundary analysis and leave it — the
   error today points at 1:5 with an expecting-list, buried but not
   wrong, and the shape (a keyword as a destructure binder) is rare.

## Ground rules

Message TEXT unchanged; if fixed, pin the caret on the keyword +
teaching + neither burial marker (`Expecting:` and `Other error
messages:`), AND the three let-shape fall-throughs unchanged (plain
`let x = …`, destructure `let (a, b) = …`, expression let-in `let
(x, y) = v in body`) before/after; strict-spans deep run in
acceptance. A finding is closed by the analysis on record, not a
forced fix.

## Done when

`let (rec) = …` surfaces the keyword teaching cleanly with its guard's
attempt boundary named, OR a stated finding records why the plain-vs-
destructure-vs-letin discrimination cannot yield without a commit
change; the three let-shapes are pinned unchanged; no message text
moved; strict spans green.
