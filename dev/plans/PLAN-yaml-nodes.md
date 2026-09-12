# weir — yaml patch: typeless YAML read + district-addressed merge

Status: BUILT (2026-09-11, same day — branch yaml-nodes; [D:yaml-nodes]).
Probe outcomes: P1 confirmed — `$-`, `$[`, and `yaml patch` were all
errors before the claim (unclaimed syntax, no breaking-change ruling
owed). P2 held only partially: the tombstone lands in the TEMPLATE
parser as predicted, but the marker-line modifiers ride the shared
`isYamlMarkerPiece` predicate the ASSEMBLER classifies with — so fuzz
seeds WERE owed (3×10k fresh). Two laws implemented better than written:
`$-` in a plain district refuses at CHECK (a located teaching at the
tombstone's own span), not at parse — same guarantee, better span; and
`show` on a patch has its own teaching instead of the functions wording.

Originally DESIGNED same day, designer-approved. The receipt is the
KSL port ([PLAN-ksl-migration] probe 1, resolved here): its entire YAML
mutation surface, re-expressed. This un-parks [D:yaml-seq]'s deferred
"typeless `from yaml` producing Yaml nodes" — the original trigger was a
mixed-bundle READ; the receipt that actually arrived is stronger:
read-MODIFY-write over documents with unknown structure.

## The receipt, catalogued

KSL's mutation surface is four operations + a path grammar:
`editInPlace` (deep merge at root: maps upsert recursively, seqs
append-if-absent by node equality, scalars replace), `editInPlaceAtPath`
(the same merge at a JSON-path; update-or-insert spelled as try/with),
`removeNode -> bool` / `removeNodes` (mapping key, seq index, or seq item
by `[key=value]`/`[scalar]` predicate). Serialization laws: block styles,
multiline -> literal scalars, CRLF normalized to LF (weir's [D:lf-output]
already matches), and COMMENTS ARE ALREADY LOST (YamlDotNet's
RepresentationModel + a fresh serializer) — so the bar is STRUCTURAL
fidelity, not byte fidelity; weir's parse→edit→render normalizing is not
a regression.

Why typed read-modify-write cannot carry it: `from yaml T` ignores
undeclared fields — right for reading, DESTRUCTIVE for rewriting (a
kustomization's undeclared `namespace`/`configMapGenerator`/`helmCharts`
would vanish on write-back), and the merge ops target arbitrary user
YAML, not one schema. The port needs a representation that HOLDS unknown
structure: Yaml nodes — which weir already has on the write side
(`YMap`/`YSeq`/`YStr` render via `to yaml` and yaml districts). What is
missing is the read, and an edit vocabulary.

## The design road, recorded (three rejected shapes)

1. STRING PATH GRAMMAR (KSL's `images.[name=app]`) — REJECTED: paths
   interpolate DATA at nearly every call site, so the grammar is
   injection-shaped (`$"resources.[{path}]"` with a filename containing
   `=`/`]` silently matches the wrong thing — the textual hole weir's
   districts exist to close); the dotted-predicate key is ambiguous
   (literal `"a.b"` key vs nested path — KSL resolves by TRY-ORDER,
   data-dependent); and it carries a whole runtime parse-error class.
2. TYPED PATH STEPS (`[at "images"; where "name" name]`) — SOUND but
   SUPERSEDED: injection impossible, typos check-time, but a new
   vocabulary and verbose at every call site.
3. DISTRICT-AS-ADDRESS — ADOPTED: the patch's own STRUCTURE locates the
   target (kustomize's strategic-merge-patch model, the domain's own
   idiom). Mapping navigation is just nesting (a path was always
   redundant with a nested patch); sequences are addressed by a MERGE
   KEY; update-or-insert stops being control flow (KSL's try/with, the
   steps version's `tryMergeAt |> Option.defaultWith`) and becomes the
   SEMANTICS of keyed merge. Splices are values-never-text, so the
   injection guarantee is inherited from the district for free.

Removal then arrived by the same move: not kustomize's `$patch: delete`
(a magic VALUE in data-space — spoofable by parsed input, the exact
data/instruction blur weir refuses) but a SIGIL — syntax in the district
grammar beside `$name`/`$(expr)`, desugaring to constructors UNPRODUCIBLE
from data. And the patch itself became a KIND, moving the laws into the
type system.

## The shape

TWO MODULE MEMBERS, ONE DISTRICT KIND, ONE SIGIL:

    Yaml.parse : seq<string> -> Yaml            // the typeless read
    Yaml.merge : YamlPatch -> Yaml -> Yaml      // pure; the whole edit surface

    let p = yaml patch by=name                  // the kind; by= optional
        images:
            - name: $name                       // keyed upsert (by=)
              newTag: $newTag
        spec:
            oldKey: $-                          // tombstone: remove the key
        resources:
            - $newManifest                      // append-if-absent
            - $- $retired                       // tombstone: remove the item

    File.read f |> Yaml.parse |> Yaml.merge p |> to yaml |> File.write f

- `yaml patch` yields the nominal `YamlPatch`, NOT `Yaml`. `by=<key>` on
  the marker line (the `schema=` slot) sets the sequence merge key.
- MERGE SEMANTICS (KSL's, pinned by its own tests/merge.fsx, ported as
  unit pins): maps upsert recursively; seqs append-if-absent by node
  equality; scalars replace; with `by=`, a seq-of-mappings item matches
  on the key — update the match, append when absent (upsert), remove on
  tombstone. Merge is orderless and idempotent.
- `$-` TOMBSTONE, two positions: value position (`key: $-`) removes the
  mapping key; item-head position (`- $- <content>`) removes the matching
  seq item (by scalar equality, or by the `by=` key for mappings). A
  batch removal is one patch with several tombstones; one patch may add
  AND remove.

## The laws

- UNPRODUCIBLE FROM DATA: `Yaml.parse` never emits a tombstone (or any
  patch-only constructor) — a `$-`-looking string in a parsed file is a
  string. The assembler-sentinel argument, one level up.
- PATCHES DO NOT RENDER: `to yaml` on `YamlPatch` is a CHECK-time
  refusal ("a patch is instructions for a merge, not a document — merge
  it, don't render it"). The type carries the law; kustomize's spelling
  can leak `$patch:` lines into manifests, this cannot leak by
  construction.
- SIGIL SCOPE: `$-` is legal ONLY in a `yaml patch` district; in a plain
  `yaml` district it is a parse error naming the patch kind.
- `patch` × `schema=` REFUSE to combine in v1: a patch is a PARTIAL
  document (required fields legitimately absent) — partial validation is
  its own problem (kustomize punts too). Teaching: "a patch is partial;
  schema= validates whole documents." Receipt reopens.
- MERGE SOURCES ARE PATCHES ONLY (no overloading): a computed source is a
  district with `$(expr)` splices — covers every KSL case (its sources
  are all constructed, never parsed documents).
- `$[` is RESERVED: stays a parse error. If a positional receipt ever
  arrives, `$[n]` with SNAPSHOT semantics (indexes resolve against the
  pre-merge document, keeping the patch orderless) is the designed
  answer — positional addressing is otherwise refused: it imports
  JSON-Patch's sequential model into a declarative patch, and no receipt
  exists (no KSL call site uses `[3]`).

## KSL mapping (the port, op by op)

| KSL | weir |
|---|---|
| `editInPlace nodes f` | `File.read f \|> Yaml.parse \|> Yaml.merge p \|> to yaml \|> File.write f` |
| `editInPlaceAtPath n path f` | same — the patch's structure IS the path |
| `setImage` (try-at-path/else-create) | one `yaml patch by=name` merge — upsert absorbs the try/else |
| `removeNode f path` | a tombstone in the patch |
| `removeNodes f paths` | one patch, several tombstones |
| `Kustomize.add*` (6 fns) | 3-line patch merges |

## Parked, each with its trigger

- `Yaml.mergeReport` (what changed / was the tombstone's target present)
  — KSL has ONE conditional-on-removal site (`NoYamlPath onRemoved`);
  interim spelling is render-and-compare. Trigger: that site proving
  hot, or a second consumer.
- Per-list merge keys (`by=` as a map — one patch touching two lists
  keyed differently). Trigger: a real patch needing it; KSL's calls each
  touch one list.
- Partial-schema validation of patches. Trigger: a schema'd patch
  receipt + a design for absent-required-fields.
- Merging a PARSED document as the source. Trigger: a real
  doc-into-doc merge.
- `$[n]` positional addressing (spelling + snapshot semantics reserved
  above).

## Probes (build-time verifications, run FIRST)

1. `$-` and `$[` in district value/item positions are ERRORS today
   (unclaimed syntax — `$` should already be splice-reserved). If either
   currently parses as literal text, the claim is a breaking change and
   needs its own ruling.
2. The patch grammar lands in the yaml-TEMPLATE parser, NOT the statement
   assembler — confirm, so no fuzz seeds are owed (the yaml-seq
   "parser untouched" precedent). The marker-line `patch`/`by=` addition
   rides the existing `schema=` modifier slot.
3. `YamlPatch` as a def-less builtin nominal beside `Yaml` (the Proc/Map
   registration precedent — declarable-name guard included).
4. KSL `tests/merge.fsx` expectations transcribe to unit pins against
   `Yaml.merge` (scalar-into-map, nested-map-preserving-keys,
   seq-append-if-absent at minimum).

## Footprint

- Parser: marker-line `patch` (+ optional `by=key`) in the `schema=`
  slot; `$-` arm in the yaml template parser, patch-districts only.
- Types/Check: `YamlPatch` nominal; `Yaml.merge` signature; `to yaml`
  check refusal on patches; `patch`×`schema=` refusal; plain-district
  `$-` teaching.
- Eval: `Yaml.parse` (expose the owned yaml parser as nodes);
  `Yaml.merge` (pure node function — recursive F# impl, no weir-level
  recursion involved).
- Builtins: 2 members + docs (executed examples).
- Editors: district-head highlight for `patch`/`by=` if the grammars
  pattern the marker line; tree-sitter split-repo ritual IF the grammar
  repo patterns it — check, may be zero (district bodies are opaque to
  the statement grammars).
- Tests: unit pins (merge laws, tombstone both positions, laws/refusals,
  parse round-trip) + an e2e cell doing a REAL kustomization.yaml
  edit-in-place (the KSL shape, minus the name).
- Docs: SKILL bullet + run block, GUIDE, reference/adapters or a yaml
  page section, `[D:yaml-nodes]` DECISIONS row citing the [D:yaml-seq]
  un-park, CHANGELOG.
- Client-name constraint: this file lives in dev/ (exempt); everything
  that ships names kustomization.yaml as the receipt, never the client
  tooling.

## Relation to other plans

Unblocks [PLAN-ksl-migration] — this was its last capability gap (the
walk shipped as [D:structural-walk], function-types v0.0.23). Composes
with [PLAN-plan-apply]: the file round-trip's ONE mutation is a visible
`File.write`, so a planned/captured edit is exactly one op. Independent
of [PLAN-pure]; `Yaml.merge` is pure by construction.
