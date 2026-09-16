# weir — `#infer`: named types drafted from a JSON/YAML sample in the REPL

Status: PROPOSED (2026-09-15, designer conversation — the JSON-
exploration ergonomic). A REPL scaffolding directive, not a language
change; `weir check` stays evaluation-free and untouched.

## The tension it resolves

Exploring an unknown JSON blob means hand-transcribing its structure
into a shape (`from json {| items: seq<{| metadata: … |}> |}`) by
staring at the bytes. Two weir laws make check-time help impossible,
and BOTH must stay:

- `check` never evaluates (the whole guarantee).
- `from json T` never sniffs the input — you DECLARE the shape,
  nothing infers it from data.

So no check-time inference can exist. But the thing to infer from is a
concrete VALUE, and the REPL is a RUNTIME. The resolution: put the
inference where the data already is — a directive that drafts a shape
from an evaluated sample. This is NOT dynamic typing creeping in; it
is the `weir add schema` category — tooling that ingests external
structure and emits a DECLARATION you own and edit. The language
semantics do not move; the tool just writes the first draft.

## The surface

    #infer <source> from <json|jsonl|yaml> as <Name>

Reusing the adapter keywords is deliberate: the directive reads as, and
scaffolds for, the code you then write.

    weir> let raw = kubectl get po -A -o json
    weir> #infer raw from json as Pods
    defined: Pods, Metadata, Status   (3 types)
    weir> raw |> from json Pods |> _.items |> Seq.map _.⟨TAB⟩
          metadata   status

    weir> #infer $(kubectl get events -o json) from json as Events   // inline, evaluated once
    weir> #infer logs from jsonl as LogLine                          // element type; use seq<LogLine>
    weir> #infer manifest from yaml as Deployment

`<source> from <fmt> as <Name>` maps 1:1 to `<source> |> from <fmt>
<Name>` — same words, left to right.

## Why named types, not an anonymous shape

The output is a COLLECTION of named `type` declarations INJECTED into
the session (as if you had typed them), top type = the `as` name. Three
payoffs an anon shape cannot give:

1. COMPLETION FALLS OUT FOR FREE. REPL completion already works on
   typed values; making the inferred types REAL in the session env is
   all it takes for `from json Pods |> _.items |> Seq.map _.⟨TAB⟩` to
   offer fields. The feature is "materialise named types from a
   sample"; the completion is reuse, not new machinery.
2. THE DRAFT IS EDITABLE. A single sample misses optional/absent
   fields — accepted, not a problem (you infer what EXISTS; for
   exhaustiveness you reach for a schema/docs). Because the output is
   ordinary `type` decls, you correct a miss by editing the
   declaration, not fighting an opaque inference.
3. READABILITY AT DEPTH. Nested anon shapes (k8s is 3+ levels) are
   unwieldy; named types flatten the nesting into a legible set.

## The design centrepiece — auto-naming the nested types

The naming rules are the real work; everything else is wiring.

- TOP type = the `as` name. If the sample's top level is an OBJECT,
  `Name` is that record; if a bare ARRAY, `Name` names the ELEMENT and
  you write `from json seq<Name>` (weir's array-endpoint convention);
  `from jsonl as Name` names the element likewise (`seq<Name>`).
- NESTED record = its field name, Capitalised. `metadata: {…}` →
  `Metadata`; `status: {…}` → `Status`.
- SEQ-OF-RECORD element = the field name, singularised BEST-EFFORT.
  `items: seq<{…}>` → `Item`. Singularisation is heuristic
  (`data`/`status`/`metadata` do not pluralise) — when it cannot,
  fall back to the field name as-is (or a parent prefix).
- COLLISIONS: two different shapes under the same field name (a `spec`
  that is a pod spec here, a container spec there) disambiguate by
  PARENT PREFIX (`PodSpec` / `ContainerSpec`).
- DEDUP: the same shape under the same name collapses to ONE type
  (the anonymous-shape canonicalisation precedent).

## Sample → shape inference rules (stated, not clever)

- scalar → `string`/`int`/`float`/`bool` by the JSON token; integer-
  shaped numbers → `int` (a decimal anywhere in that position → `float`,
  matching the adapter's widening).
- object → a record (named per above); empty object → `{||}`? — decide
  (rec: an empty record type, editable).
- array of scalars → `seq<scalar>`; array of objects → `seq<Named>`;
  EMPTY array → cannot infer the element (`seq<string>` as the least-
  surprising default, flagged in a comment the directive prints).
- `null` field → cannot infer the payload; emit `Option<string>` with
  a printed note, or omit — decide (rec: `Option<string>` + note, since
  a present-null field usually IS optional).
- heterogeneous array (mixed object shapes — the tagged-union k8s
  bundle) → infer the FIRST element's shape and print a "heterogeneous;
  verify" note; a `[<Tag>]` union is out of scope (that needs the
  discriminator named, which is schema/docs territory).

The caveat is deliberately weak: `#infer` drafts what the sample HAS.
Absent, null-only, empty-array, and union cases are printed as notes,
not guessed silently — the user edits the emitted `type`.

## The parse — the directive owns it

`#infer`/`#help`/`#echo` are REPL-parsed specially (string-matched, not
through the weir grammar), so `#infer` splits its line on the literal
` from ` and ` as ` markers, then evaluates `<source>` as an ordinary
expression. That is what lets `<source>` be a binding (`raw`), a
capture (`$(…)`), or a bare command — WITHOUT the grammar ambiguity a
command head + `from`/`as` argv words would create. A source that
itself contains ` from ` (`grep from f`) is the rare bind-first case;
acceptable.

Format is EXPLICIT (in-spirit: weir never sniffs a wire format). A
`json` default when `from …` is omitted is a defensible REPL-only
convenience (the bare-alias divergence precedent) — but lean explicit;
it is the word you write anyway.

## Composable core + promotion

- `Json.inferShape` / `Yaml.inferShape : seq<string> -> string` — the
  same inference as a builtin returning the declaration TEXT, so it
  works outside the REPL (`sample.json |> File.read |> Json.inferShape
  |> print`, or a `weir infer-shape < sample.json` CLI for scaffolding
  a type against a new API). The directive is a thin REPL wrapper that
  ALSO injects into the session.
- PROMOTION is `#save` (see the parked REPL-session-to-script idea): the
  inferred types are ordinary session declarations, so dumping the
  session to a `.weir` file carries them verbatim — `#infer` to
  explore, `#save` to keep. No separate export step.

## Boundaries — NOT doing

- `check` never evaluates and `from json` never sniffs — untouched. The
  inference is a RUNTIME sample→declaration draft, explicitly invoked.
- NOT dynamic typing. The generated types are static declarations you
  edit; nothing infers types from data at check or run of `from json`.
- `from xml as` DEFERRED: XML inference must emit weir's xml-record
  conventions (`[<Attr>]`/`[<Elem "…">]`, everything-is-text →
  `string`) — its own inference problem. Ship json/jsonl/yaml first.
- Tagged-union inference DEFERRED (needs the discriminator; that is
  schema/docs territory) — heterogeneous arrays print a note.
- No per-keystroke inference: the shape is not in the source, so there
  is nothing for a live type field to read until a sample is evaluated.
  The `⟨TAB⟩` completion after `from json Name` is the reactive assist,
  and it is powered by the already-injected types.

## Footprint

- A `shapeOfJson`/`shapeOfYaml : Value -> TypeDecl list` walker (reuse
  the adapter's own parser to get the Value; the Yaml/JSON readers
  exist) + the auto-naming pass (the centrepiece).
- Repl.fs: the `#infer` directive — split on ` from `/` as `, evaluate
  the source, run the walker, inject the decls into the session
  TypeEnv (the path `let`/a `type` decl already uses), print
  `defined: …` + any inference notes. Help text in `#help`.
- Builtins.fs: `Json.inferShape`/`Yaml.inferShape` members + docs.
- Tests: the naming rules (nested/seq-element/collision/dedup), the
  inference rules (scalar widening, null, empty array, heterogeneous
  note), session injection makes `from json Name` check, the
  composable builtin round-trips a sample.
- Docs: a REPL-doc bullet (repl.md) + SKILL note; CHANGELOG;
  `[D:repl-infer]` (scaffolding lane, the `add schema` sibling; check
  stays pure). No grammar/keyword change — `#infer` is a directive,
  not a keyword.
- Gates: build ×4, units, publish, skill-doc, e2e cell (a REPL
  transcript inferring a small sample and checking `from json Name`).
  No fuzz owed (no parser change).

## Relation

Sibling of `weir add schema` (external structure → a local artifact you
declare against). Companion to the parked `#save` (session → script).
Independent of the plan/apply stack. `check` and `from json` semantics
are untouched — this is the REPL's exploration surface, nothing more.
