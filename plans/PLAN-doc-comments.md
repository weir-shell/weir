# weir — `///` doc comments (half 1: user declarations)

Status: BLESSED (user 2026-07-27). One session. Half 1 of two: this
establishes the machinery (attachment, alignment, hover layout,
completion detail) that half 2 — docs for the ~60-80 builtins — then
CONSUMES as a content pass. Sequenced first for two reasons: the
plumbing gets built and pinned against fixtures you control, and
**the doc-test question below decides half 2's template**, so
answering it here avoids rewriting 80 entries.

## Decided at review

- **Hover shows type FIRST, then the doc** (the universal layout).
- **`///` lines participate in alignment — they must align with the
  member they describe.**

## The attachment rule

`///` lines attach to the declaration IMMEDIATELY BELOW them,
contiguous (multiple `///` lines accumulate in order). A blank line
between the doc and the declaration BREAKS the association — which
is what every reader expects, and weir's blank-transparency law makes
"contiguous" the meaningful word. Pin both: attached, and
blank-separated-so-not-attached.

## Positions covered (the "what did I miss" answer)

1. top-level `let` bindings
2. `type` declarations (records and unions)
3. **record field declarations**
4. **union cases**
5. inner/block `let`s [verify hover reaches them — Session A's
   inner-binder hover exists, so the plumbing is there]

NOT in scope: **param docs**. A `/// - param x: …` convention is a
structured-doc design (sections, tags) and a bigger question; params
show their types on hover already. Parked with that reason.

## Alignment: `///` lines are ENTRY-STARTING lines

The consequence of the review decision, stated so it is not
rediscovered: a `///` above a record field is **part of that field's
entry**, exactly as an attribute line is. This is the
attribute-line machinery's THIRD customer (same-line attributes,
preceding-line attributes, now doc comments) — reuse it, do not
grow new logic:

- the `///` line must hit the field's MEASURED ANCHOR (the
  field-alignment session's rule);
- the sibling-separator is suppressed between the doc and its field
  (the `>]`-dangle rule's shape);
- fmt canonicalizes the `///` indentation to the anchor.

**The precedent that makes this non-negotiable**: the `>]` dangle
exemption once ALSO skipped the alignment check, and a field one
column off its own attribute line slipped through with a misleading
RUNTIME error while the checker stayed happy. Pin misalignment both
ways (doc above field, doc above union case).

## VERIFY FIRST (cheap, and it sets the session's shape)

1. **Does `///` lex as a comment in every position today**, or does
   the scanner's comment rule treat the third slash specially
   anywhere? Determines whether this starts with a lexer change or
   only an attachment change.
2. **Can the doc-test extractor scan `///` comments?** — the
   question that "sounds good" per review and may be the feature's
   best part: if `///` examples can be EXECUTABLE, builtin examples
   in half 2 become the only docs in the project that cannot rot.
   Verify-and-REPORT here (do not build the extractor change unless
   it is trivial); the answer is half 2's template input.

## Mechanics

- Lexer/parser: `///` runs are captured and attached to the
  following declaration; the AST carries them (a `Doc: string list`
  or joined string — session's call, reported); the typed tree
  mirrors them (the binder-spans session's threading precedent: the
  compiler chases every construction site).
- Hover: type first, then the doc, for each covered position. The
  `definitionFor`/`nodeAt` machinery already locates declarations
  and binders — reuse, do not re-derive.
- **Completion detail**: LSP completion items carry the doc — this
  is where a user DISCOVERS a name exists, and it is half 2's main
  delivery surface. Wire it here.
- Erasure: docs are check-time-only data, never reach eval, `show`,
  or json (the attributes precedent — pin that an attributed/documented
  declaration is byte-identical at runtime).

## Bars

- **Zero behavior change** — docs are inert data. Any pin that
  moves for a non-doc reason is a finding.
- The alignment pins (both misalignment directions) are the session's
  sharpest guard, per the precedent above.
- fmt roundtrip + idempotence with `///` lines present, at every
  covered position.
- The three grammars (micro/TextMate/tree-sitter) already color
  `//`-comments — **verify `///` colors too**, and if a distinct
  doc-comment scope is cheap, add it per the drift rule (micro is the
  spec; a stated shortfall if RE2 blocks it).
- Strict-spans deep run (the assembler grew a line class).

## Work items

1. The two verifies (lexing; doc-test extractor) — reported before
   any edit.
2. Lexer/parser attachment + AST/typed-tree threading.
3. Alignment as entry-starting lines (attribute machinery reused);
   the misalignment pins; fmt canonicalization.
4. Hover (type + doc) at all five positions; completion detail.
5. Grammar coloring check + the drift-rule outcome.
6. Pins: attached / blank-separated-not-attached / multi-line
   accumulation / erasure / fmt roundtrip / alignment ×2; strict
   spans; SKILL+GUIDE state the convention in ONE place; NOTES;
   DECISIONS row (the attachment rule, the alignment consequence,
   the param-docs park with its reason, and the doc-test answer).

**Done when:** `///` docs attach and render on hover (type first) at
all five positions; misaligned docs error like misaligned attributes;
fmt preserves and canonicalizes them; completion items carry them;
runtime is byte-identical; the doc-test answer is in the report as
half 2's template input; all green.

## Amendment — annotated signatures (2026-07-28) [D:annotated-signature]

Half 2's template gains the ANNOTATED SIGNATURE (shipped separately on
the `annotated-signatures` branch, landing before half 2's writing
pass). Every builtin entry's writing pass MUST:

- **name every parameter** — `BuiltinDoc.Params` carries the names (a
  separate field, never parsed from prose); hover renders
  `name (p1: t1) (p2: t2) : result`. `subject` beats `stringToMatch` —
  the naming forces clarity, and it is the part of the docs read most.
- keep the signature **declaration-shaped even for data-last members**:
  `Seq.choose (f: 'a -> Option<'b>) (xs: seq<'a>) : seq<'b>`.
- let the **example** show the piped idiom (`xs |> Seq.choose f`).

Division: the signature says WHAT it is; the example says HOW it is
written. A sample of 5 members is named on the annotated-signatures
branch to prove the path; naming the rest is this half's content work.
