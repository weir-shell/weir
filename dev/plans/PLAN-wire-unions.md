# weir — tagged unions cross the wire (and yaml gains its jsonl)

Status: PROPOSED (2026-09-04). Three sessions, ordered; each ships
alone. Supersedes PLAN-yaml-splitdocs (the splitter's user surface
is skipped by the designer's ruling — no receipt pressure, so the
typed endgame is built properly instead of the stopgap; the split
machinery lives on INSIDE the stream form).

The trigger is DESIGNER'S CHOICE, stated as such: no receipt exists;
the driver is coherence and power — heterogeneous document streams
(the k8s bundle) currently have no typed spelling, and the peek-
dispatch idiom weir can already express is a hand-written tagged
union. This plan compiles that idiom into the type system.

## The decomposition (the design's core claim)

Heterogeneous streams are TWO ORTHOGONAL features composed, plus one
prerequisite — not one feature:

- **P — attribute positions widen.** Union declarations and union
  cases join record fields as attribute hosts. Pure prerequisite:
  inert until U consumes it.
- **U — tagged unions become wire types.** A union with a declared
  tag field is admitted at the json AND yaml boundaries — top level,
  record field, seq element — read and write.
- **S — `from yaml stream T`.** The cardinality form: N `---`
  documents, each read as T. Any admitted T — S knows nothing about
  unions.

The composition IS the feature: a k8s bundle is `from yaml stream
KDoc` — S provides the many, U provides the per-document dispatch,
neither special-cases the other. And the decomposition's proof is
what falls out FREE: once U lands, `from jsonl KDoc` dispatches
heterogeneous NDJSON with ZERO new surface — jsonl already is
json's stream form. The symmetry (stream : yaml :: jsonl : json,
union orthogonal to both) is the makes-sense test passing.

```weir
[<Tag "kind">]
type KDoc =
    | Deployment of DepSpec
    | Service of SvcSpec
    | [<Other>] Unknown of string

for doc in File.read "bundle.yaml" |> from yaml stream KDoc do
    match doc with
    | Deployment d -> print d.metadata.name
    | Service s -> print s.spec.clusterIP
    | Unknown kind -> printerr $"skipped: {kind}"
```

## Probes — RAN 2026-09-04, against to-jsonl (`e085607`)

1. FCS accepts attributes on union TYPE declarations and on
   individual CASES (both compiled via fsy) — P NARROWS the
   attributes divergence (weir currently refuses both positions with
   "attributes attach to record fields"); the closed-registry
   posture of [divergence: attributes-registered] extends unchanged.
2. `from yaml` on a union today: "'K' is a union; 'from yaml' needs
   a record" — the refusal U retargets.
3. `from yaml {| kind: string |}` ignores extra fields (probed in
   the splitdocs planning) — the same tolerance U's tag-peek reuses
   internally: reading the tag field IS a peek, machinery that
   exists.
4. The `---` boundary law lives once in `Yaml.parseDocs` (a column-0
   line exactly `---`) and parseDocs ALREADY returns a document
   LIST — S's eval half is "stop refusing length > 1", not new
   parsing.
5. Heterogeneous WRITES exist today (`seq<Yaml>` nodes → stream);
   after U, `seq<KDoc> |> to yaml` writes a typed heterogeneous
   stream — the write side composes the same way.

## Session P — attribute positions (parser + registry) — EXECUTED 2026-09-04

Completion addenda:
- The assembler seam landed as predicted, one level up from the
  field-attr `>]` dangle: an attr-only pend (whole line is one
  complete `[<…>]` list, no pending brackets/lambdas/district)
  joins the next col-0 line; a non-decl follower re-parses as one
  statement and gets the parser's position teaching, located.
- typeDecl COMMITS past a consumed attr list (failFatally) — no
  backtrack into expression land; stacked attr lists get the same
  teaching (one `;`-separated list is the law, fields' rule).
- UnionDef stays attr-free: cases strip AFTER validation — P's
  surface is validation exactly; U threads storage.
- Two error classes kept distinct: unknown name → did-you-mean;
  registered-wrong-position → home teaching.
- Gates: unit 1435 (+7), oracle 179 (+3, divergence row narrowed
  in place), e2e attrpos cell, fmt idempotent on the own-line
  form, fuzz 3×10k fresh (parser moved).

- Parser: an attribute line before a `type` declaration attaches to
  the DECLARATION; `| [<X>] Case of T` attaches to the case. F#'s
  syntax exactly (probe 1) — the `///`-through-attribute
  transparency rule already stated for fields extends.
- Checker: the closed registry ([D:attr-registry] posture) becomes
  POSITION-SCOPED — field attrs (Short/NoShort/Default/Wire), union
  attrs (Tag), case attrs (Wire/Other). A registered name in the
  wrong position teaches its home ("'Tag' attaches to a union
  declaration"); unknown names keep didYouMean.
- Until U lands, Tag/Other VALIDATE but bind nothing — the
  attribute law's shape (validation at attachment, binding at
  consumption), stated in the row so the inert release is not read
  as dead code.
- Parser MOVED → 3× fresh 10k fuzz; oracle pins for both new
  positions (accept, matching F#); divergence row NARROWS in place.

## Session U — wire unions (the core) — EXECUTED 2026-09-04

Completion addenda:
- The mass concentrated in PLUMBING, not conversion: TEFrom's top
  slot became a DU (TopRec|TopUnion), a unions closure rides beside
  the records closure, and TETo carries a case-keyed write table (a
  VUnion value has no type name) — which forced a ruling the plan
  missed: two tagged unions sharing a case name reachable from one
  serialized type refuse at the to-site (declarable under
  ambiguous-ctor, constructible only via reads).
- A declaration law the plan missed: tagged case names must not
  spell a builtin's wire encoding (Some/None/Y-nodes) — the writers
  key by case name. Checked BEFORE the payload law (check order is
  teaching order — `Some of string` must hear about the name).
- FRICTION, pre-existing (probed on main): `for d in xs do match d`
  does not type the binder from its source; the piped Seq.iter
  spelling works and the docs use it. The fix is its own session.
- Two P-era pins tightened as their text predicted (the inert pin
  retired FOR the binding pin; int payloads → record payloads).
- Gates: unit 1443 (+8), e2e wireunion cell, the jsonl free win
  pinned, LSP tag-led hover; parser untouched — no fuzz owed.

THE FORM — internally tagged only (serde's `tag = "kind"`; the k8s
and API-ecosystem shape): the tag field sits AMONG the payload's
fields on the wire. Externally/adjacently tagged PARKED with
teachings pointing at the internal form (triggers: a real payload in
either shape).

- **Declaration laws**, each a check-time refusal that teaches:
  `[<Tag "field">]` names a string-valued discriminator; every case
  payload is a RECORD (or the case is NULLARY — a tag-only
  document); a payload record that itself declares the tag field
  refuses ("the tag rides the union"); generic unions refuse (the
  monomorphic boundary law, records' rule); tag values are case
  names, `[<Wire "apps/v1">]` per case overrides; two cases
  resolving to one tag value refuse at the declaration (the wire-key
  collision rule, one law over).
- **Admission**: a TAGGED union joins the recursive field law at
  BOTH formats — top level of `from json`/`from yaml`, record
  fields, seq elements, `Map` values. An UNtagged union keeps
  today's refusal, which now teaches `[<Tag>]`. Cycle detection
  walks through cases (K → Dep → K names its path).
- **Read**: peek the tag field (probe 3's tolerance — machinery
  exists), pick the case, parse the WHOLE document as the payload
  record (the tag field ignored as any extra field is). A missing
  tag field errors naming it; an unmatched tag value errors naming
  the value AND the declared cases — UNLESS an `[<Other>]` case
  exists: `of string` receives the raw tag (payload dropped — typed
  reads are lossy, the standing law), nullary drops both. [<Other>]
  is IN scope, not parked: without it, S∘U cannot read a bundle
  containing one CRD you don't control — the composition's honesty
  depends on it. At most one [<Other>] case; it never matches a
  PRESENT declared tag.
- **Write**: `to json`/`to yaml` on a tagged-union value render the
  payload record with the tag field REINSERTED FIRST (k8s's own
  convention; a stated position, not an option). An [<Other>] value
  refuses to write ("it names what was not understood — nothing
  faithful can be emitted").
- **Structural discrimination REFUSED as a ruling**, recorded: try-
  each-case is sniffing, and factually broken where it matters
  (every k8s doc shares its top level + extra-field tolerance ⇒
  multiple cases fit). The tag is DECLARED or the union does not
  cross.
- No parser movement (P provided the syntax) — no fuzz. LSP: hover
  on a tagged union shows the tag + per-case values; completion
  unchanged (cases are ordinary).
- The free win pinned in e2e: `from jsonl KDoc` dispatches mixed
  NDJSON — zero new surface, the decomposition's receipt.

## Session S — `from yaml stream T` — EXECUTED 2026-09-05

Completion addenda:
- Probe 4 held exactly: parseDocs already returned the list; the
  eval half was a flag and a map. The multi-doc refusal re-points at
  the stream form — the coherence bug that opened this arc closes.
- DEVIATION, ruled in the row: NO grammar churn. `stream` sits
  outside the (from|to)+adapter token; colouring it means widening
  that token in three grammars plus the zed ritual, for colour
  alone. Shipped uncoloured; trigger = it reading poorly in real
  use. The manifest/lexical/tree-sitter budget went unspent.
- `to … stream` refuses AT PARSE (the to-stage takes no argument —
  the word would otherwise fall into application-position noise).
- Found in passing: the from-yaml completion slot offered nothing
  (json/jsonl only — an accident); fixed with the stream item.
- `stream seq<T>` composes (each document a sequence document);
  Map fences; empty stream = zero documents.
- Gates: unit 1446 (+3, 2 re-points), e2e bundle cell + teaching
  re-point, fuzz 3×10k owed (parser moved) and run.

- Spelling: `stream` as a FORM word between adapter and type —
  `from yaml stream T` → `seq<T>`, each `---` document read as T.
  Any admitted T (a homogeneous stream of records is legal — the
  [D:yaml-seq] "cannot type a heterogeneous stream" clause is
  REVERSED BY U, so the retirement's survivor rules narrow to: bare
  `from yaml T` stays one-document; nothing sniffs). New row, cites
  the old one.
- `from json stream` fences with a teaching (NDJSON is `from
  jsonl`; an array is `from json seq<T>`); `from yaml stream` in a
  WRITE position fences (`to yaml` on a seq already streams).
- Eval: parseDocs already returns the list (probe 4) — the
  multi-doc refusal becomes the success path under `stream`; the
  one-document spelling keeps its refusal, re-pointed:
  "…— read a stream with `from yaml stream T`".
- Parser MOVED (the form word) → fresh fuzz; grammar trio + manifest
  + lexical + completion churn (the closed-set slot pattern:
  `stream` completes after `from yaml `); tree-sitter target ritual.
- e2e: the flagship — a real mixed bundle through `from yaml stream
  KDoc`, plus a homogeneous stream of one record type, plus the
  fences.

## What does NOT move

- Untagged unions at every boundary (refusal retargets to teach
  [<Tag>]); Args/Env (argv/env have no document to discriminate —
  stated); the yaml district/template; `to yaml`'s existing stream
  write; `from json seq<T>` / `from jsonl T` semantics.
- The peek idiom keeps working — U makes it unnecessary, not
  illegal.

## Verification (per session, the standing battery)

- P: parser pins both positions + wrong-position teachings; oracle
  accept-pins vs FCS; fuzz 3×10k; divergence row narrowed.
- U: declaration-law pins (each refusal); read pins (dispatch,
  missing tag, unmatched tag, [<Other>] both shapes, nullary case,
  field-position union, seq-element union, Map-value union, cycle);
  write pins (tag-first reinsertion, roundtrip per format,
  [<Other>] write refusal); the jsonl free-win e2e cell; LSP hover
  probe; SKILL/GUIDE (the union block REPLACES the peek idiom as
  the taught form).
- S: stream pins (mixed via KDoc, homogeneous, empty stream, the
  two fences, re-pointed teachings); fuzz; grammar gates; the
  [D:yaml-seq] successor row.

## Sizing

P one session (parser + registry + oracle). U one to two (the
admission sweep touches both formats' read AND write — the widest
diff; no parser risk). S one (parser small, churn mechanical).
Sequenced so each lands green alone and U is never blocked on S's
grammar ritual.
