# Standing process rules

The rules the project has learned, one place. Each rule cites its
naming incident. Plans and sessions follow these unless a blessed
plan explicitly overrides.

## Composition-product rule

A new grammar/assembler/checker decision ships with pins for its
products against the EXISTING decisions — the matrix row, written
before the pins. Cells are pinned, N/A'd with a reason, or triaged
red. Naming incident: the greedy-`;` silent swallow (conditional
execution the user never wrote, alive across sessions with a green
suite) — the rule postdated the decision that needed it; the
retroactive sweep (tests/PRODUCT-MATRIX.md) paid that debt.

## Behavioral pins over parse-shape pins

Where execution order or effect COUNT is the invariant, the pin is
effect-counted e2e on the AOT binary — a parse-shape pin cannot see
a legal-parse-wrong-meaning bug. Naming incidents: ext→ext piping
(the original), the silent swallow (the reminder).

## Fixture shape diversity

Every grammar construct's battery includes at least one fixture per
structural context it can occupy: headed (under if/let/match),
standalone (top-level), nested (inside another compound), and
at-boundary (first line, last line, adjacent to blank). Naming
incident: the standalone-marker district bug — every district
fixture was if-headed, and the compound supplied the sibling level
by accident until the first standalone marker appeared. The rule's
first enforcement (this sweep's backfill) immediately caught the
field-value-on-next-line record bug.

## Position-matrix rule

New expression forms and tokens sweep the expression-position
inventory (tests/POSITIONS.md) — pin or explicit exclusion per
position, enumerated in session notes. Naming incident: the let-RHS
sequencing miss. The inventory is maintained, not re-derived.

## Done-when intent rule

Done-when clauses bind to INTENT. When a session's work dissolves a
clause's premise, the report says so explicitly and the pin moves to
where the behavior is real. Grafting complexity into an example to
satisfy a checkbox's letter is the named anti-pattern. Naming
incident: "Exit.code propagates in the bicep translation" — the F#
original's exit-code plumbing dissolved under raise-at-force, so the
propagation shape is pinned in e2e where it is real, and the example
correctly carries none.

## Established rules (index; full text in their origin archaeology)

- Stop-and-report on behavior deltas mid-refactor, on budget
  overruns, and on precedence-class grammar changes (NOTES: district
  session, greedy review).
- Verify rule: run the battery for its EXIT CODE first, then grep
  the log (e2e masking, x4).
- New line-shape logic goes in classify/scanner/Join — a StartsWith
  or quote-state loop in the assembler fold is a review flag
  (PLAN-assembler-formalization).
- Claim-vs-behavior: features ship with pins that would fail if the
  claim drifts; doc examples are executed (skill-doc).
- Reject-don't-guess at typed boundaries; every rejection names its
  escape hatch (from porcelain/json, Env.load, Env.fromFile).
- Receipts before features: parked items reopen on recorded
  triggers; user fiat can override, and the override is recorded as
  such (env-sugar layers).
