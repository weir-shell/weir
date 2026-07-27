# weir — the anchor residue: four findings, three sessions

Status: BLESSED (user 2026-07-27). Four independent findings from
the diagnostics sessions, each self-contained. **Bundling ruled:
A+B run together (shared gate lineage, neither can stop in a way
that blocks the other); C runs ALONE (its stop condition is now
PREDICTED, not merely permitted — see the mechanism below, and a
stop inside a bundle would strand A+B's work or tempt pushing
through); D runs any time (different subsystem).**

Standing bars for all: message TEXT unchanged unless a section says
otherwise; exact line:col pins, never contains-checks; strict-spans
deep run in every acceptance; a site that cannot be fixed cleanly
is a stated FINDING, not a forced call.

## THE MECHANISM (the predictor — quote it in each diagnosis)

From [D:message-domination]: **consume-then-anchor works only when
the anchor position has no competing parser.** Consuming the
trigger CLEARS the competitors that sit there; seeking back then
raises alone. Where a competitor survives at the anchor, FParsec
merges its expected-set and the message buries. This one sentence
explains the whole (a)/(b)/(c) split and PREDICTS each remaining
site's outcome — so every diagnose-first step below asks it
verbatim: *does the anchor position have a competing parser?*

## THE PROPERTY (record it once, from whichever session runs first)

**A fatal inside an `attempt` is not a fatal** — FParsec backtracks
fatals too, so it is advisory. Three sightings now: the arms loop
backing out of a consumed separator (arm-commit), `DepthExceeded`
needing a thrown exception because a `failFatally` deep in
speculative `attempt`/`choice` was swallowed, and `letKeywordGuard`
having to fire OUTSIDE `topLet`'s attempt. A teaching error that
must survive needs one of: an anchor outside the attempt, an
exception channel, or a commit point ahead of it.

**Owed by the first session to run**: a LEXICON entry and a PROCESS
line stating it, so the fourth sighting is a lookup rather than a
rediscovery.

---

## A. foldChain reifier / multi-external caret drift  [finding (c)]

STATUS: EXECUTED (2026-07-27, the A+B session). foldChain has ONE
real error branch (the reifier-not-after-a-single-external), owned by
the MARKER (`mspan`, in scope) — the sweep's "ambiguous" was wrong,
recorded. Widened to `Result<Expr, string * Span>`; both callers raise
`failFatallyAtCol`. The marker position had no surviving competitor,
so it dominates clean (no `Expecting:`, no `Other error messages:`).

SIZE: small. RUN FIRST — the anchor is available; this is threading,
not restructure.

**The self-correction, recorded**: the sweep called this anchor
"ambiguous" — WRONG on re-read. `foldChain` already holds each
segment's span (`mspan` per marker, `seg.Span` per stage); the gap
is only that it returns `Result<Expr, string>` (a bare message, no
position), so both callers `failFatally m` at the drifted position.
A finding is a claim, not a fact — the report says so, since
"ambiguous" was a judgment made under sweep pressure.

- Widen to `Result<Expr, string * Span>`; tag each `Result.Error`
  with the OFFENDING segment's span; both callers
  (`cmdLineWith`, the value-headed tail) raise via
  `failFatallyAtCol`.
- **Diagnose per error branch** (not once for the function): a fold
  has several failure modes and they do not all blame the same
  segment. For each branch: which segment owns the blame, and
  **does that anchor position have a competing parser?** A branch
  whose blame is genuinely split is a stated finding.
- Pins: multi-external and reifier-misuse shapes, exact line:col on
  the offending segment + no `Expecting:` AND no
  `Other error messages:` (both burial markers — pinning one leaves
  the other free to return).

## B. reserved word in PARAM / FIELD position  [domination residue (ii)]

STATUS: EXECUTED (2026-07-27, the A+B session). Boundary map:
PARAM is inside topLet's attempt (so `letKeywordGuard`, already
outside it, was extended to scan the name AND simple-ident params —
`many1 (spanned rawWord)` up to `=`, first keyword wins); record-DECL
field commits with typeDecl, so a dominating `fieldNameDecl`
propagates. FIXED and pinned (`let f rec`, `let f when`, `type T = {
let: int }`, plus the keyword×position fall-through matrix). FINDING
left: record-LITERAL field (`{ let = 1 }`) sits inside recordLit's
field-vs-update commit-check [D:arm-commit] — dominating it means
reworking that commit boundary, out of remit; keywords inside a
PARENTHESISED param pattern share that finding.

SIZE: small-medium. Runs with A.

`let f rec = 1`, `{ let = 1 }`, `type T = { let: int }` fail at
distinct sites with a generic "expecting '='", never the keyword
teaching. Three positions, three parsers (binderParam,
record-literal field name, record-decl field name).

- **The prerequisite the report supplied**: `letKeywordGuard` works
  because it fires OUTSIDE `topLet`'s attempt. So **per slot,
  identify that slot's enclosing attempt boundary BEFORE writing
  the guard.** A guard placed inside the attempt silently does
  nothing — the worst failure mode, because it looks implemented
  and passes any test that only checks the message is not worse.
- **Pin shape, explicit**: the guard fires AND the teaching reaches
  the user — exact caret on the keyword + the teaching text present
  + neither burial marker. "No crash" is not a pin.
- Diagnose per slot: legitimate keyword fall-through at that
  position? (A field/param name never falls through to a keyword
  parser — likely clean — but record-literal field parsing shares
  machinery with record UPDATE and expression atoms, so confirm.)
  Plus the mechanism question. Any slot that cannot dominate
  without breaking a real parse is a stated finding.
- Risk pins: **every reserved word × every position where it
  legitimately heads something** — `if/then/else/elif/match/when/
  fun/let/in/type` in their fall-through positions. A matrix, not
  a spot check: the corpus uses the common ones, and `when`/`elif`
  are rare enough to slip. The fuzzer's generator already emits
  compounds with these heads — a deep run is cheap extra coverage.

## C. neg-int-out-of-range, contested  [domination residue (i)]

STATUS: EXECUTED (2026-07-27, alone). The predicted-hard case turned
out CLEANER than the prediction — the premise ("parsed TWICE by
negIntLit and prefix-minus") was WRONG on diagnosis (negIntLit is
RANGE-ONLY; general expressions use `negAtom`). The real root: for
`let x = -N`, `negAtom`'s operand `intLit` hits int-out-of-range, and
`negAtom`'s OWN `attempt` SWALLOWS that fatal (the property this
residue documented — a fatal inside an attempt is not a fatal), then
the merge buries it. FIX = the property in action, NOT the plan's
three options (both broke the risk surface): narrow negAtom's
`attempt` to cover only the prefix DETECTION, so the operand parses
OUTSIDE it and its fatal propagates. This narrows an EXISTING attempt
(applies the documented property), not a new consumed-separator commit
— and the entire risk surface (`a - 1`, `a-1`, `-5`, `[10.. -1 ..8]`,
`f -1`) is BYTE-IDENTICAL before/after (pinned), so the stop-condition
did not trigger. Overflow now reports "int literal out of range" at
1:10 (the digits), clean.

SIZE: small but tricky. **RUNS ALONE.** The mechanism already
PREDICTS the difficulty: the anchor is the `-`, which is the unary
minus operator's contested spot — a competitor survives there, so
seek-back buries. This is the known-hard case, not a hunch.

`-99999999999999999999` is parsed TWICE (by `negIntLit` and by the
opp's prefix-minus over a positive `intLit`), both hit
int-out-of-range at cols 9 vs 10, neither dominates.

- **Diagnosis is the work**: decide which parser OWNS a literal
  `-<digits>` adjacency. Weigh, do not silently pick:
  (1) prefix-minus does not engage on a bare digit-run
  (`notFollowedBy digit`), so `negIntLit` solely owns `-N`;
  (2) drop `negIntLit`, let prefix-minus + `intLit` own it;
  (3) leave as a stated finding if either disturbs the risk
  surface.
- **Risk surface, pinned BEFORE and after**: `a - 1`, `a-1`,
  `[10.. -1 ..1]` (the spaced range step that motivated
  `negIntLit`), valid `-5`, and **`f -1`** (application vs
  subtraction — spacing-sensitive, and option (1) could shift it;
  pin whatever weir does TODAY before touching anything).
- **STOP-and-report if the clean fix wants a new commit point** —
  grammar surgery is not this session's remit, and the
  consumed-separator law's instances are not up for
  renegotiation here.

## D. cross-statement field-type MISMATCH sibling  [Session D residue]

SIZE: small-medium. Independent subsystem (checker, not parser) —
run any time.

Session D anchored the cross-statement NO-FIELD error at the access
with the meet as a note. The sibling — right field name, WRONG
type, cross-statement — still reports at the meet, because
`dischargeRow`'s Some-arm `bind … fspan …` uses the
within-statement fspan, not the recorded origin. The asymmetry is
worse than either behavior alone: the user learns the good
behavior from the name case, then gets the old one for the type
case.

- **The load-bearing diagnose-first question**: does the mismatch
  even flow through `dischargeRow`'s Some-arm — or does it unify
  earlier in `mergeRows`, or in a later `bind`? If it unifies
  before discharge, the origin was never consulted because the
  path does not pass through the consulting site, and D is a
  bigger fix than "extend the Some-arm" — a stated finding, not a
  forced extension.
- Also confirm the origin is POPULATED for the mismatch path (it
  may only be recorded for accesses that reached discharge).
- Pins: the reduced bicep mismatch (error moves meet → access,
  meet as note); the WITHIN-statement mismatch unchanged;
  direct-access byte-identical; zero movement elsewhere;
  oracle re-run (Session D's precedent — origins ride schemes).

---

## Work items

1. **A+B session**: the property's LEXICON+PROCESS entry (owed
   here, as the first to run); A's per-branch diagnosis and
   threading; B's per-slot attempt-boundary identification, gates,
   and the keyword×position risk matrix; strict spans; moved pins
   named.
2. **C session**: the before-pins (incl. `f -1`); the ownership
   diagnosis with all three options weighed in writing; fix or
   stated finding; zero movement on subtraction/range/application.
3. **D session**: the flow diagnosis; the Some-arm extension or the
   finding; pins per above.

**Done when:** every foldChain error anchors on its segment;
param/field keyword teachings surface cleanly with their guards
proven to be outside their attempts; neg-int is fixed or a stated
finding with its ownership analysis on record; the cross-statement
mismatch anchors at the access or its flow finding is written; the
fatal-in-attempt property is in LEXICON and PROCESS; no message
text moved; strict spans green throughout.
