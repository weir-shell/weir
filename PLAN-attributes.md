# weir — attributes: check-time, erased, consumers-registered

Status: BLESSED as design-on-file → user-opened (2026-07-23).
EXECUTED 2026-07-23 with one scope adjustment — see the completion
addenda: the consumer half (Args.load) does not exist and is
deferred to the typed-argv plan; the infrastructure landed in full.

The attributes question was walked honestly across the review
thread: first waved off with a fabricated "refused machinery"
citation (advisor error, corrected — NOTES owes the entry:
decisions are citable only by pointer to archaeology; a claim with
no pointer is under the folklore rule), then re-costed properly.
The finding: what makes .NET attributes expensive is REFLECTION —
runtime metadata, an access API, blob instantiation — and weir
wants none of it. Weir attributes are CHECK-TIME DATA attached to
AST nodes, consumed by bespoke arms, FULLY ERASED — the pattern
every weir feature already uses, plus an attachment syntax. That
deletes the expensive half.

The customer ledger is at three, the classes-arc threshold shape:
- `Short` — the typed-argv shorts override (string keys rejected by
  the user; the parallel-record design works but loses field
  locality — the attribute keeps the override ON the field).
- `Doc` — per-field help text, parked on "a syntax weir doesn't
  have"; this is that syntax.
- `Positional` — the positionals park's pre-scoped first fight was
  "the marker question: convention vs wrapper type vs catch-all";
  `[<Positional>]` IS the marker, dissolving that fight before it
  happens (the park itself stays shut — the marker existing does
  not open it; it pre-answers its hardest question).
Honest ledger status: one warm customer (shorts — no anger receipt),
two parked. Opened by user choice, on record per the standing
precedent.

## The form

    type Cli = { [<Short "C">] clean: bool;
                 [<Doc "target environment">] env: string;
                 port: Option<int> }

(F#-style attachment; same-line-before-field v1 — see the
attachment-grammar decision.)

## Pre-made decisions

- DECIDED — **Syntax is F#'s, oracle-refereeable**: `[<Name arg>]`
  / `[<Name>]` / `[<A; B>]` lists. The pleasant asymmetry, stated:
  the SYNTAX is F#-parity (FCS parses these shapes — Same pins for
  attachment grammar) while the SEMANTICS differ invisibly (F#:
  reflection metadata; weir: checker-visible erased data) — a
  distinction that never surfaces at parse level, so no do-style
  false friend. Oracle probes FIRST per the folklore rule.
- DECIDED — **v1 scope: record-field attachment only; literal args
  only** (string/int/bool — the splice family, again). Attachment
  to type declarations, lets, params, union cases: each waits for
  a customer; other positions reject with "attributes attach to
  record fields" naming this decision.
- DECIDED — **The governing rule: unknown attribute names are CHECK
  ERRORS.** Every attribute must have a registered consumer (a
  name → validator table owned by the checker; consumers are
  builtin-side in v1 — user-defined attributes are a door that
  stays shut, noted). No silent decoration, ever. Did-you-mean
  over registered names on the error.
- DECIDED — **Per-consumer arg validation is the consumer's arm**:
  `[<Short "toolong">]` is the Short consumer rejecting (one char,
  not `h`); explicit shorts are CHECK-time data against a
  CHECK-time field set, so collisions are CHECK errors, stronger
  than the derived case. Attributes validate at ATTACHMENT (name
  known, args well-formed) and bind at CONSUMPTION — a Short on a
  never-loaded record is legal-and-inert, like a comment.
- DECIDED — **Erasure is absolute**: no attribute reaches eval,
  Value, show, from/to json, or equatability — a record with
  attributes is the SAME TYPE as one without. Any implementation
  wanting runtime presence is a stop-and-report model violation
  (the classes-erasure precedent verbatim).
- DECIDED — **The three consumers land in dependency order**:
  registry + Short (NoShort suppression as a second registered
  name), then Doc (--help field lines), then Positional
  (registered, consumer parked with a not-yet error).
- DECIDED — **Mechanical sweep**: AST attrs; parser `[<` opener
  (collision-probed); assembler (same-line v1); fmt respace-guard;
  both highlighter grammars + inventory; doc-test extractor.
- DECIDED — **Ceremony, full**: POSITIONS sweep, products
  (× record update, × from json, × Env.load, × generics),
  tripwires, TRANSCRIPTION addendum, timing.
- DECIDED — **Docs**: SKILL, GUIDE, SEMANTICS, DECISIONS row, the
  fabricated-citation correction in NOTES.

## Parked

- User-defined attributes / user consumers — the door stays shut.
- Attachment beyond record fields — per-site, per-customer.
- `Positional`'s CONSUMER — the positionals park, unchanged, now
  with its marker question pre-answered.
- Field defaults — the third door stays open as a candidate with
  its own customers; weir-only-no-referee cost noted.
  [2026-07-24: prediction CASHED by [D:default-attr] — the
  ATTRIBUTE serves both named customers (Args now, Env follow-up);
  the language door itself stays shut.]

---

## Completion addenda (2026-07-23)

### STOP-AND-REPORT: the consumer half does not exist

The plan's work items 3–4 wrote `Short`/`NoShort`/`Doc` consumers
into **`Args.load` — which does not exist**. `Args` has `flag` and
`value` only (confirmed by exhaustive grep of src, SKILL, NOTES);
derived shorts, `--help`, and typed argv are an advisor-thread
design that never landed in code. Resolution, by the plan's own
validate-at-attachment/bind-at-consumption rule: ALL registered
names are legal-and-inert today (Positional's decided treatment,
extended to the other three), the infrastructure landed in full,
and consumer activation is the typed-argv plan's first work item.
The "Done when" clauses about derivation, `--help` truth, and the
not-yet message carry over to that plan.

### What landed

- `AttrArg` (AStr/AInt/ABool) + `RecordDef.Attrs` (Types);
  `AttrSpec` with spans + `DRecord` field triples (Ast); parser
  `attrList` before field idents, literal args only; the checker
  registry with unknown-name did-you-mean, per-name validators
  (Short: one char, `h` reserved; Doc: non-empty string;
  NoShort/Positional: argless), duplicate-attr, Short×NoShort
  conflict, and cross-field explicit-short collision errors — all
  at the offending spec's span.
- POSITIONS: expression, union-case, let-param, and update-field
  positions reject with "attributes attach to record fields".
- Products: × record update (updates cannot mention attrs, source
  records update unchanged), × from json (attributed T loads
  identically), × Env.load (Doc'd config field inert-legal),
  × generics (attrs are declaration data, instantiation unchanged).
- Erasure pins at unit and e2e level; 5 oracle pins (FCS confirmed
  all attachment-grammar claims first-try: F# accepts
  `System.Obsolete` on a record field, rejects weir's registered
  names, rejects `[<5>]` and expression-position attributes) —
  divergence row `attributes-registered`.
- Both highlighter grammars gained the `attribute` rule (inventory
  21, both-or-neither guard green); fmt roundtrips attribute lists
  under the respace guard; SKILL/GUIDE doc blocks run in CI.
- Docs: SKILL, GUIDE (worked Cli example), SEMANTICS, DECISIONS
  `attributes` row, NOTES entry paying both owed corrections
  (fabricated citation; parallel-record supersession with the
  Default idiom preserved), TRANSCRIPTION registry-arm addendum.

### Finding: type declarations are single-line (RESOLVED 2026-07-23
by PLAN-multiline-brackets — type decls continue, and preceding-line
attributes ride the same rule; same-line-only v1 is retired)

Probed en route (the attachment-grammar cell): record TYPE
declarations do not continue across lines — record LITERALS
continue (their fields carry `=`, which the brace-continuation
join recognizes as a field start), type fields carry `:` and the
join never learned them. Pre-existing, independent of attributes;
it bit immediately when the GUIDE's two-field attributed `Cli`
wanted to wrap. Same-line-only attachment v1 is therefore moot
(there is no preceding-line position to take). Candidate next fix,
logged not ridden.
