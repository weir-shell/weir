# weir — multiline type declarations + list literals

Status: BLESSED (user 2026-07-23). One session, assembler/classifier
work under the formalization rule. EXECUTED 2026-07-23 — see the
completion addenda.

Receipts: (1) record TYPE declarations didn't continue — surfaced by
the attributes session's probe, bit immediately in GUIDE's attributed
Cli; root cause: the continuation join keyed on `=` (literal fields),
type fields carry `:`. (2) List literals stayed single-line — the
record-continuation session's explicit park; the receipt arrived in
the flagship (encodeSubref's ten-pair line).

Scope: "arrays" read as LIST LITERALS (no-arrays row stands).
Parens-spanning-lines stays parked (no receipt).

## Pre-made decisions (abridged; full text in the blessing message)

- DECIDED — one rule, extended: the brace-continuation machinery
  widens to `:`-fields in type position and `[` element joining;
  F# light's own rule; oracle Same pins + F#-negatives.
- DECIDED — attributes ride the continuation (preceding-line
  attachment, THE F# style); the attributes plan note updates.
- DECIDED — blank-line rule mirrors records (error naming the open
  bracket); comment lines stay transparent.
- DECIDED — fmt house style extends (align mirroring records);
  respace shape-guard covers the new shapes.
- DECIDED — products: × greedy/offside, × districts, × pattern
  binders, × record update multiline, × attributes-on-own-line ×
  doc-extractor, × command splice.
- DECIDED — the receipts are the e2e (GUIDE Cli wraps; encodeSubref
  and the Ctx decls rewrap; live smoke green).
- Ceremony: assembler-only, zero checker surface expected; the full
  battery is the regression harness (zero pin edits).

## Completion addenda (2026-07-23)

### Done-when, discharged

All three form-block shapes run (type decl, attributed type decl
with preceding-line attributes, list literal); GUIDE's attributed
Cli wraps (the bite healed in place, the single-line workaround
note deleted); encodeSubref and all five type declarations in the
flagship rewrap and the live repo-pair smoke is green; cross-bracket
closers error naming both sides ("'}' closes the '[' opened at
line 2"); blanks inside open brackets name the bracket (kind-aware:
"this record type's {" / "this list's ["); ZERO existing-pin edits
(736 unit + 110 oracle passed untouched before the new pins);
+13 unit pins (fixture diversity: headed/standalone/nested/
at-boundary/strings-guarantee), +6 oracle pins all first-try, e2e
section, timing unchanged. 749 unit / 116 oracle at close.

### The mechanism (one rule, extended — confirmed)

`BraceDepth: int` became a bracket STACK (kind, opening line) fed
by scanner-riding `bracketFold`; the innermost kind picks the
separator rule. StartsTypeField landed in the classifier beside
StartsField per the formalization rule. Nesting (a multiline record
inside a list, and vice versa) falls out of the stack with no
dedicated code. Zero checker/parser surface, as predicted: the
assembled text is what the single-line grammar already accepts.
Preceding-line attributes needed only a dangle rule (`>]` at
line end = the next line continues the same entry).

### Resolutions the plan left open

- StartsField did NOT widen — StartsTypeField is its own classifier
  case, gated on the logical line's `type ` head (a `:`-line in a
  LITERAL record stays a value continuation).
- Wrapped list elements: a dangling operator/comma/opener at line
  end continues the element (F#-parity, oracle-pinned) — the
  every-line-an-element rule has exactly that escape.
- fmt's "bracket+2" wording resolved: the house logic is
  align-under-the-first-entry — brace+2 (`{ x`), bracket+1 (`[x`),
  matching the plan's own form block.
- Piped stdin is the REPL (single-line, the continuation-prompt
  park unchanged) — multiline forms are file-mode; probes and pins
  run through files.

## Parked (unchanged)

- Parens spanning lines — no receipt.
- Arrays as a TYPE (`[| |]`) — the no-arrays row stands.
- Multi-line strings — separate park; brackets never enter string
  literals (scanner-guaranteed, pinned).
- REPL continuation prompts — the standing park; multiline forms
  inherit file-mode scope until it opens.
