# weir — the last keyword slot: let-destructure

Status: EXECUTED (2026-07-27) — 1b LANDED, the class is closed. The
last keyword-domination slot: `let (rec) = 1` dominated by extending
`letKeywordGuard`'s scan through pattern delimiters (the balanced-paren
LEXICAL scan, option 1b — no pattern parse, no commit-boundary touch).
Option 2 was NOT attempted (predicted to stop, correctly). `let (rec)`
→ 1:6, `let (a, rec)` → 1:9, `let (x, in)` → 1:9, all clean; the three
let-shapes and true/false literal patterns unchanged. ONE fix landed
during the session: the scan flagged `true`/`false` (they are in the
keyword set) — excluded them (patWord's rule: literal patterns, not
name errors), which the refutable-binder test caught.

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

1b. **The lexical scan (LEAD WITH THIS — probably the cheapest thing
   that works).** Do not parse a pattern at all. Finding a reserved word
   in an IDENTIFIER position between `(` and `)` is a LEXICAL question,
   not a structural one: the guard doesn't need nesting, tuples, or
   `{ }` — it scans tokens through balanced parens for a bareword that
   `keywords.Contains`, and fatals at the first. `letKeywordGuard`'s
   existing bareword scan is exactly this shape and stopped at `(` by
   CHOICE; extend it through the balanced-paren span. It stays OUTSIDE
   SLetPat's attempt (it is a stmtWith alternative), so the fatal
   escapes, and it dodges BOTH costs option 1 prices (no re-raise of a
   swallowed fatal, no duplicated pattern logic). Diagnose: the scan
   must stop at `=` (the binder/RHS boundary) and not mistake a keyword
   in the RHS for a binder keyword; and it must treat `_`/`true`/`false`
   and uppercase constructors as non-keywords (patWord's own rule). If
   the balanced-paren token scan lands cleanly, this is the fix.
2. **Narrow SLetPat's attempt** so `binderPat` parses OUTSIDE it (the
   negAtom precedent — the attempt covers only the DISCRIMINATION, the
   pattern body commits after). PREDICTED TO STOP (stated as expected,
   the C-neg-int way — not merely permitted): the discrimination reads
   the pattern's SHAPE (`PVar` → plain, else destructure) to decide,
   AND needs `= … in body` lookahead for the expression-letin case, so
   `binderPat` cannot move outside the attempt without the
   discrimination following it. That is not a boundary you can nudge —
   it IS the boundary. Expect this to STOP-and-report; if the session
   finds otherwise, good, but framing it as expected prevents a heroic
   attempt at renegotiating the plain-vs-destructure-vs-letin
   discrimination.
3. **Stated finding — a fine outcome here, said plainly (not a
   defeat).** If 1b doesn't land cleanly and 2 stops as predicted,
   record the boundary analysis and leave it. Today's error points at
   1:5 with a dump: buried but NOT wrong. A keyword used as a
   destructure binder is vanishingly rare — it requires typing
   `let (rec) = …`, which no muscle memory produces. The class shrank
   from "every binder slot" to "one rare shape inside parens"; closing
   it costs either duplicated lexical scanning (1/1b) or a
   commit-boundary renegotiation (2), and the analysis-on-record is the
   CORRECT deliverable if 1b doesn't land — not a shortfall.

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
