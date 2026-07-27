# weir — the last two keyword-domination slots

Status: DRAFT — the two findings left by the anchor-residue A+B
session (message-domination, [D:anchor-residue-ab]). Both are
"a keyword in a name-required slot that still buries its teaching",
but they live in DIFFERENT machinery than the slots already fixed, so
each is its own diagnosis. Standing bars: message TEXT unchanged;
exact line:col pins + BOTH burial markers absent (`Expecting:` AND
`Other error messages:`); the fatal-in-attempt property (LEXICON)
governs — a guard states its enclosing attempt boundary; strict-spans
deep run in acceptance; a slot that cannot dominate without breaking
a real parse or renegotiating a commit law is a STATED FINDING.

## THE PREDICTOR (quote in each diagnosis)

Consume-then-anchor / a dominating fatal is clean ONLY where the
anchor position has no competing parser AND the fatal is not inside a
swallowing `attempt`. Every diagnose-first step asks both: *does the
slot have a competing parser at the keyword, and what attempt encloses
it?*

---

## 1. record-LITERAL field-name keyword  [`{ let = 1 }` → 1:11 buried]

The hard one — it sits ON a commit boundary. `recordLit` discriminates
a literal from a copy-and-update by an ATTEMPT'd head-check
(`lookAhead (identSpanned .>> "=" .>> notFollowedBy "=")`) — the
[D:arm-commit] record instance. For `{ let = 1 }` the head-check's
`identSpanned` rejects `let` (non-fatal), so the literal never
commits and the parse falls into the UPDATE alternative, which fails
and buries.

- **The commit-law caveat is load-bearing**: making the head-check
  recognise `<keyword> =` as a field-assign-with-keyword-name and
  fatal MOVES the literal-vs-update discrimination. That is a
  consumed-separator-law instance — **STOP-and-report if the clean
  fix wants to renegotiate it**; the arm-commit law's instances are
  not up for change here.
- Diagnose first: can the domination live OUTSIDE the commit-check —
  e.g. a guard that fires when the record body is `{ <keyword> = `
  before recordLit's choice runs, leaving the arm-commit untouched?
  (The `letKeywordGuard` precedent: a separate, earlier, outside-the-
  attempt guard. A record literal can appear in many positions, not
  just a statement head, so its guard has no single stmt-level home —
  that is the crux to solve or to report.)
- If no placement dominates without touching arm-commit, this stays a
  STATED FINDING with the boundary analysis on record.
- Pins (if fixed): `{ let = 1 }` caret on the keyword + teaching +
  neither burial marker; and the UPDATE path unchanged
  (`{ r with x = 1 }`, `{ x = 1 }`) — before/after.

## 2. keyword in a PATTERN binder  [`let f (rec)` → 1:8 buried]

Broader than params: the pattern grammar (`patParens`, `commaPats`,
the binder pattern atoms) uses the shared `identSpanned` (non-fatal
`notKeyword`) for a binder name, so a keyword in ANY pattern-binder
position buries — parenthesised params (`let f (rec)`, `let f (a,
rec)`), and likely destructuring lets (`let (rec) = …`) and match-arm
binders (`match x with | rec -> …`). `letKeywordGuard`'s bareword
scan deliberately stopped at `(`, leaving this whole class.

- **Enumerate the pattern-binder positions FIRST** (params, let-
  destructure, match-arm, lambda-param) and, per the predictor, each
  one's enclosing attempt boundary — a pattern parser is reused across
  all of them, so a fatal placed in the shared atom is swallowed by
  whichever attempt encloses the current use (topLet's, SLetPat's,
  matchArm's). A single dominating pattern-name parser likely CANNOT
  serve all positions cleanly (different attempts) — expect to report
  which positions dominate and which stay findings.
- **The real risk**: a keyword in a pattern position is NOT always an
  error the way a binder NAME is. `match x with | Some y -> …` — `Some`
  is a constructor, not a keyword, fine; but confirm no keyword ever
  legitimately heads a pattern (`when` guards attach AFTER the pattern,
  not inside it). Pin the fall-throughs: every pattern form that
  legitimately parses (constructors, tuples, lists, wildcards, `when`
  guards) must be unaffected — a matrix, before/after.
- Diagnose whether the fix belongs in the pattern grammar (a
  dominating pattern-name) or, like params, in outside-the-attempt
  guards per position. Whichever cannot dominate cleanly is a stated
  finding.
- Pins (if fixed): `let f (rec)`, `let f (a, rec)`, and whichever
  other positions dominate — caret + teaching + neither burial marker;
  plus the pattern fall-through matrix.

---

## Bundling & order

Independent — different subsystems (recordLit's commit-check vs the
pattern grammar). Item 2 is the larger (a position enumeration + a
fall-through matrix); item 1 is smaller but pricklier (a commit
boundary with a real STOP condition). Run separately, either order;
neither depends on the other. Both may legitimately end as stated
findings if the clean fix wants the arm-commit boundary (1) or cannot
serve all pattern positions from one place (2) — the bar is the
analysis on record, not a forced fix.

## Done when

Each slot either surfaces its keyword teaching cleanly (caret +
teaching + neither burial marker, with the guard's attempt boundary
named) or is a stated finding with its boundary/position analysis
written; every legitimate record-update and pattern form is pinned
unchanged; no message text moved; strict spans green.
