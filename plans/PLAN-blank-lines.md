# weir — blank lines inside brackets

Status: BLESSED (draft 2026-07-23; advisor amendments + user bless
same day). EXECUTED 2026-07-23 — see the completion addenda. REVERSES a
decided rule: blank-inside-open-bracket errors ("consistency over
convenience", the record-continuation session; extended to `[` by
PLAN-multiline-brackets). Per the DECISIONS convention a reversal
is a NEW entry naming the old key, never an edit.

Origin: user question at the Stroustrup landing. Receipts:
encodeSubref's ten replacement pairs want category gaps; any long
Stroustrup record wants visual grouping. Comment-only lines already
pass through brackets (transparent); bare blanks error — the
convenience gap is exactly one line class wide. The F#-ward
direction is free: F# accepts blank lines inside brackets, so the
`blank-line-ends-statement` divergence NARROWS.

## The reframe that makes the reversal honest

The records session chose "consistency over convenience" when blank
meant END-OF-STATEMENT everywhere. Since then, comment-only lines
became transparent to assembly — there is already a line class that
crosses brackets without joining. The proposal unifies: inside an
open bracket, a blank line is TRANSPARENT, exactly like a comment
line. Outside brackets nothing changes — blank still ends the
statement, blank still ends a pending block-let, blank still bounds
a district. Consistency is preserved; it just runs along a
different seam (transparent line classes) than before.

## The cost, and the guard that pays it

Error locality is why the old rule existed: a forgotten closer
errors AT the first blank today, near the mistake. Transparent
blanks would let an unclosed bracket swallow following statements
as `;`-joined entries until EOF — the runaway-heredoc class the
assembler refuses.

PROPOSED — **the statement-head guard**: a column-0 line whose
piece begins `let ` or `type ` while a bracket is open errors
immediately: "statement at column 0 while the '{' opened at line N
is still open — close the bracket". Keywords cannot be field
names, so no record/type entry loses. The bound, stated: a col-0
`let ... in` EXPRESSION as a list element becomes unwritable at
column 0 (indented it stays legal) — pathological, and F#'s
offside rejects col-0-anything inside brackets anyway, so the
guard NARROWS records-fields-ignore-indent at exactly the
keyword-head corner, F#-ward again. The unclosed-closer mistake
then errors one statement late instead of a file late.

## Proposed decisions

- PROPOSED — blanks inside open `{`/`[` are transparent (no join,
  no separator, no state change); runs of blanks likewise. Comment
  and blank lines become the SAME assembler class (one transparency
  rule, two members — the formalization rule's preferred shape).
- PROPOSED — the statement-head guard (above), classifier-side per
  the formalization rule.
- PROPOSED — scope: brackets only. Block-let blanks (noBodyBlank),
  district blanks, and statement-level blank-ends-statement are
  UNCHANGED. Precedence when both apply (a pending let inside an
  open bracket): bracket transparency wins while the bracket is
  open — the let's body continues after the gap; pinned.
- PROPOSED — fmt preserves blanks inside brackets verbatim (no
  state reset across them; bracket annotations survive). Collapsing
  blank RUNS to one is NOT ridden (a respace-class decision with
  its own products; parked with a pointer).
- PROPOSED — bookkeeping: `blank-line-ends-statement` row amends to
  "except inside open brackets"; the two blank-inside error pins
  (e2e + unit) flip to acceptance pins DELIBERATELY (named in the
  session report per the done-when intent rule); oracle probes
  FIRST: F# verdicts for blank-inside-type/literal/list/update
  (expected Same-accept; the update case carries the Stroustrup
  offside asymmetry — expect the existing divergence row to absorb
  it, verify), and the guard shapes (expected Same-reject at col-0,
  via the offside).
- PROPOSED — the receipts are the e2e: encodeSubref gains its
  category gaps in the flagship; the live smoke stays green.

## Work items

1. Oracle probe set (blank-inside shapes × 4, guard shapes) —
   first commit; surprises amend.
2. Assembler: the transparency arm + the statement-head guard.
3. fmt: blank preservation across bracket state; idempotence pins.
4. Pin flips (named), divergence-row amendment, products
   (× district blank unchanged, × pending-let-in-bracket, × REPL
   single-line unchanged, × doc-extractor with gapped blocks).
5. Flagship gaps; docs sweep (SKILL bracket bullet gains the blank
   sentence); DECISIONS reversal row; NOTES; timing.

**Done when:** a gapped Stroustrup record and a gapped list run;
a forgotten closer errors at the next col-0 `let`/`type` naming
the bracket and its line; block-let and district blank behavior
byte-identical to today; the two flipped pins are named in the
report; all green.

## Parked

- Collapsing blank runs (fmt respace-class, own products).
- Blanks inside districts (no receipt; command blocks are short).
- Any relaxation of blank-ends-statement OUTSIDE brackets — the
  divergence row's core stands.

---

## Completion addenda (2026-07-23)

### Done-when, discharged

Gapped Stroustrup records, lists, and `{ r with` updates run; the
flagship's encodeSubref carries its category gaps and the live
smoke is green; a forgotten closer errors at the next col-0
`let`/`type` naming the opener's kind and line ("statement at
column 0 while the '{' opened at line 4 is still open"); the
update×guard cell fires naming the with-header's own line;
block-let and district blank behavior byte-identical (both-sides
pins); runs of blanks transparent; fmt preserves gaps with
annotation survival (Stroustrup canonicalization resumes after a
blank, pinned). 763 unit / 125 oracle / full e2e / 49 doc blocks
(one now gapped, proving the extractor); timing unchanged.

### The flipped pins, named (the done-when intent rule)

- unit: "blank inside an open list names the bracket" → "…is
  transparent"; "blank inside an open type decl names the record
  type" → "…is transparent".
- e2e: "blank inside an open brace errors, naming the brace" became
  THREE pins — the gapped-but-closed record reaches the checker
  (transparency), the unclosed brace still errors at statement end
  naming the record, and the statement-head guard bounds the
  runaway.

### The archaeology honesty clause FIRED

The reframe assumed comment transparency postdated the records
session's blank-error choice. It did not: the transparency fix
round landed earlier on 2026-07-20 than the grammar-consolidation
session that chose the error rule. The DECISIONS reversal row
records this as a re-weighing on new receipts (Stroustrup
grouping, encodeSubref, the user call), not a correction of an
ill-informed decision.

### Probes (harvested pre-implementation)

All five FCS verdicts read from the pin failures against the
unmodified binary: F# accepts blank-inside type decl / literal /
list (Same after the flip); rejects blank-inside UPDATE (rides the
Stroustrup session's record-fields-ignore-indent absorption, as
the amendment predicted); Same-rejects the guard shape (offside).
Zero surprises; zero amendments.
