# weir — blank lines inside blocks: the core reversal

Status: BLESSED (draft 2026-07-23; advisor additions + user bless
same day). EXECUTED 2026-07-23 — see the completion addenda.
REVERSES the CORE of `blank-line-ends-statement` — the divergence
row RETIRES (the no-elif/no-record-update retirement class). The
bracket half (PLAN-blank-lines, executed hours earlier) explicitly
parked this half; the receipt arrived the same day: the user gapped
a function BODY in the flagship (`status`'s prints) and check
failed — the original ask was "blank lines in the BLOCKS", and
brackets were the scoped half. This is the other half, and the
bigger prize: the whole divergence narrows to zero.

## The mechanism insight that makes this small

Blank-ends-statement looks load-bearing but is mostly REDUNDANT
with a stronger weir law: **statements start at column 0** (the
column-zero-statements divergence). Every col-0 non-continuation
line ALREADY closes the pending statement — blanks only mattered as
an EXTRA boundary, and every error the blank boundary produces also
exists at the col-0/EOF boundary with the same or better locality.
The col-0 law's carve-outs already handle every boundary blanks
were guarding: col-0 `|` lines are the |-inertness customers,
col-0 `else` was already the standing multiline boundary, and the
sibling rule is INDENT-KEYED, not adjacency-keyed — the receipt's
exact shape (equal-indent effect lines around a gap) gets its `;`
insertion gap-invariantly.

The implementation is therefore a DELETION: the blank branch's
three error/close arms (noBodyBlank, the district blank close,
statement close-on-blank) collapse into "pending → skip". The
bracket transparency arm from PLAN-blank-lines is SUBSUMED; the
statement-head guard for open brackets STAYS verbatim (brackets are
the one place col-0 does not close). fmt mirrors with a deferred
reset.

## The cost, stated honestly (the stray class)

Under transparency a stray after a gap JOINS and surfaces as a
checker/parse error on joined text — exactly what the same stray
gets today WITHOUT the blank. Transparency makes blank-adjacent
strays CONSISTENT with blank-free strays; the checker remains the
catcher. A softer-landing warning was considered and NOT ridden
(warnings channel is unreachable-arms only; a "looked complete"
heuristic is a parse in disguise). Pinned as a deliberate
consequence, both spellings compared. The orphan error survives
where it is true: no pending statement.

## Decisions

- DECIDED — transparency is total while a statement pends: bodies,
  block-let siblings, match arms, compound bodies, DISTRICTS
  (uniformity), brackets (subsumed). noBodyBlank retires.
- DECIDED — the col-0 law is the sole statement boundary (plus
  EOF); the bracket statement-head guard stays verbatim.
- DECIDED — the divergence row RETIRES with a retirement note;
  column-zero-statements absorbs the boundary role; the oracle pin
  FLIPS to Same.
- DECIDED — the retirement archaeology tells the founding arc
  (advisor addition): a FOUNDING divergence, narrowed by seams
  (comment transparency → brackets → the col-0 insight) → retired.
  The standing answer to "does the process ever REMOVE
  strictness?": yes, when proven redundant with a stronger law, on
  receipts plus a mechanism insight. NOTES carries the full life.
- DECIDED — the twin pin flips by name (four-hour life; the flip
  distinguishes pin-as-regression-guard from pin-as-constitution).
- DECIDED — match-head-to-first-arm gets its own probe and pin
  (advisor addition): the one shape where blank handling and
  |-inertness meet directly; exercised explicitly, never assumed.
- DECIDED — fmt: deferred reset; annotations survive gaps;
  blank-run collapsing stays parked.
- DECIDED — oracle probes FIRST: six shapes (body, arms,
  head-to-first-body, if-body, match-head-to-arm, stray).
- DECIDED — fixture diversity on GAP POSITION (advisor addition):
  first-after-head / mid / before-close, per construct.
- DECIDED — the receipts are the e2e: the user's exact status gap;
  a gapped match dispatch rides along.

## Products

× compounds (gap-invariant sibling `;`, effect-counted); × match
arms across a gap (assembler + fmt resume); × match-head-to-arm;
× districts with gaps (effect count byte-identical); × pending-let
+ gap + body (the flipped twin); × blank before a head's FIRST
continuation; × strays (both spellings); × brackets (the
PLAN-blank-lines battery re-runs UNTOUCHED); × doc-extractor;
× REPL single-line unchanged; × LSP segment translation across
gaps; × timing.

**Done when:** the gapped `status` checks and runs; gaps between
match arms, block-let siblings, district commands, and
match-head-to-arm all run; the stray pin records both spellings;
the row is RETIRED with the pin flipped to Same and the founding
arc in NOTES; the bracket battery passes byte-identical; the
twin's four-hour life is named; all green.

## Parked / watches

- Blank-run collapsing in fmt (unchanged park).
- The bracket guard's keyword-set WATCH (unchanged; strictly less
  exposed now — col-0 closes everything non-bracket).
- REPL continuation prompts (unchanged park).
- Statement-separator ergonomics (`;;`-style) — no receipt.
- Board note on landing: the remaining divergence rows are all
  structural identity (single-line strings, col-0 statements, the
  statement rule) or deliberate boundaries (no-floats, no-arrays)
  — the leanest the F#-refugee's map has ever been.

---

## Completion addenda (2026-07-23)

### Done-when, discharged

The user's gapped `status` checks and runs (the e2e receipt is the
exact shape); gaps between match arms, match-head-to-first-arm,
block-let siblings, district commands, head-to-first-body — all
run; the flagship carries body, arm, and dispatch gaps and the
live smoke is green; the stray pin compares both spellings
(identical checker error, e2e); the row is RETIRED with the
oracle pin flipped to Same; the bracket battery passed untouched
(subsumption behavior-identical); segment translation across gaps
pinned (an error below a gap maps to its physical line). 766 unit
/ 131 oracle / full e2e / 49 doc blocks; timing unchanged.

### The probes (harvested pre-implementation)

Five gap shapes F#-accept, as expected. The STRAY probe beat the
plan's hope: F# REJECTS `let x = 1` / blank / indented `2` — so
the cost paragraph's consistency claim upgraded to full
F#-alignment (both languages refuse the stray; weir's wording
differs, the verdicts agree). Zero surprises requiring amendment.

### The deletion, confirmed

Assembler: three blank arms → "pending → skip"; noBodyBlank
retired; the bracket arm subsumed; the guard untouched. fmt:
reset-on-blank removed outright — the col-0 in-branch resets are
the deferred decision. Net negative lines in both files.

### Flips, named (six + the oracle)

- "blank then continuation is an error" → joins.
- "blank line inside a block names its cause" (the noBodyBlank
  attribution pin) → transparent.
- product-matrix C×E, E×F, E×G (blank-boundary errors) → joins,
  gap-invariant sibling `;`.
- the FOUR-HOUR twin ("a blank still kills a pending block-let")
  → "body continues across a gap" — pin-as-regression-guard,
  the system working.
- oracle: "blank inside a block ends the statement (weir only)"
  → "…is transparent (row RETIRED)", Same.

### Residue found en route

SKILL line 439 still said "a blank line inside an open `{` is an
error" — stale from BEFORE the same-day bracket landing; that
sweep grepped one bullet and missed the records bullet. Swept.
The docs-sweep rule held in principle and missed in practice
once; noted here rather than a new rule (two same-day landings
touching one sentence family is the actual cause).
