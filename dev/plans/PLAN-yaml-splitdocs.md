# weir — `Yaml.splitDocs`: the stream teaching becomes expressible

Status: SUPERSEDED (2026-09-04, same day) by PLAN-wire-unions — the
designer ruled for the typed endgame over the stopgap (no receipt
pressure, so the time exists to build B properly). The split
machinery survives INSIDE `from yaml stream`; the probes below
(peek tolerance, the shared boundary law) carry over as that plan's
probes 3–4.

No direct receipt — and that is the sizing argument, not a waiver.
The driver is a coherence bug: `from yaml` on a `---` stream teaches
"split on '---' and parse each", and the language makes that split
genuinely hard to write (a lazy text→groups fold with no mutation and
no `let rec`). A teaching that points at inexpressible work breaks
weir's own law that every refusal names a repair you can type. The
splitter is also the COMMON PREFIX of every heterogeneous-stream
future — the typed-union endgame and the typeless-nodes park both
need it — so it is the one piece that cannot be wrong to build.

## Probes — RAN 2026-09-04, against to-jsonl (`e085607`)

1. THE PEEK ALREADY WORKS: `from yaml {| kind: string |}` reads a
   document and IGNORES extra fields (`replicas: 3` alongside) — so
   kind-dispatch is expressible today: peek the tag with an anonymous
   shape, re-parse the same text with the full type. Both facts are
   LOAD-BEARING for the idiom and neither is pinned — this plan pins
   them.
2. HETEROGENEOUS WRITES ALREADY WORK: `seq<Yaml>` nodes render a
   mixed `---` stream. The write half needs nothing.
3. The `---` boundary law exists ONCE, in `Yaml.parseDocs`
   (Yaml.fs:592): a line whose TrimEnd is exactly `---` — column 0
   by construction (leading whitespace survives TrimEnd), trailing
   whitespace tolerated, consecutive separators collapse. The
   splitter SHARES this rule; it must not grow a second one.
4. `from yaml` is a bespoke checker arm (TEFromYaml), not a module
   member — the splitter is an ordinary builtin, no adapter-slot
   movement, no parser movement, no fuzz owed.

## The design

**`Yaml.splitDocs : seq<string> -> seq<seq<string>>`** — text in,
one TEXT group per document out. Not nodes: each group feeds the
existing typed adapters, so the member adds zero new data model.

- The boundary rule is parseDocs's, factored so both callers share
  it: a column-0 line that is exactly `---` (trailing whitespace
  tolerated) separates; nothing else does (block-scalar content is
  indented, so it cannot false-positive; `...`/directives stay
  outside the subset and pass through as content for `from yaml` to
  refuse with its own located teachings).
- Groups with NO non-blank line are DROPPED — a leading `---`, a
  trailing one, and `--- \n ---` runs produce no phantom documents;
  a comments-only group survives (the splitter is yaml-blind beyond
  the separator) and `from yaml` refuses it as empty input, located.
- EAGER, stated: the input forces (the forcing family's register —
  a boundary cannot be known without reading past it, and manifest
  streams are small). Empty input → empty seq.
- `Yaml` is already a type-with-module precedent (Duration, Size,
  Secret); `splitDocs` is its first function member, qualified-only
  by the standing partition (not Seq/Str — no bare alias).

**The teaching re-points** (Eval.fs:1201): "… this input has N
documents — `Yaml.splitDocs` splits it; parse each" — the repair
becomes one member instead of homework.

**The idiom, documented** (GUIDE + SKILL — this IS the feature's
visible half):

```weir
for d in File.read "bundle.yaml" |> Yaml.splitDocs do
    match (d |> from yaml {| kind: string |}).kind with
    | "Deployment" -> print (d |> from yaml Deployment).metadata.name
    | "Service" -> ...
    | k -> fail $"unhandled kind: {k}"
```

Four lines, every one checked; unknown kinds refuse with a name; no
sniffing — the user declares the peek shape AND the dispatch.

**The parks, re-recorded with NAMED triggers** (the decision row):

- Tagged boundary unions (`from yaml stream KDoc`) — trigger: the
  peek-dispatch ceremony hurting in real use (a script where the
  two-pass reads or the manual dispatch table is the pain, reported,
  not imagined). First union across a wire boundary — a law change
  that waits for proof the idiom is not enough.
- Typeless nodes on the read side — trigger: a read-patch-reemit
  receipt (the kubectl-edit shape), the ONE use-class typed reads
  cannot serve because they drop undeclared fields. [D:yaml-seq]'s
  park, unchanged, now with its trigger stated sharply.

## What does NOT move

- `from yaml T` / `from yaml seq<T>` — one document each, unchanged;
  the multi-doc refusal stays a refusal (no auto-split: the splitter
  is the USER's explicit step, the no-sniffing posture).
- `to yaml` — the stream write already exists ([D:to-jsonl] ruled
  the asymmetry).
- Parser, grammars, adapter inventories — the member is `Yaml.`-
  qualified, no new adapter word, no manifest movement.

## Verification

- Unit pins: two-doc split (groups byte-exact, comments/blanks
  intact inside groups); leading/trailing/consecutive `---` drop
  empties; no-separator input → one group; whitespace-only input →
  empty; an indented `---` inside block-scalar content does NOT
  split (the column-0 law); eagerness (a counting source forces at
  the call); THE TWO IDIOM PINS — `from yaml {| kind: string |}`
  ignores extra fields, and peek-then-full-parse over one split
  group agrees with itself.
- e2e: a k8s-ish mixed bundle (Deployment + Service + ConfigMap)
  through the full idiom — split, peek, dispatch, one field printed
  per kind; the re-pointed teaching text pinned.
- skill-doc: the idiom block executes; skill-surface counts the new
  member (documented via builtinDocs, mentioned in SKILL).
- lsp: hover on `Yaml.splitDocs` shows signature + doc (derives from
  builtinDocs — the existing member machinery; one probe).
- Derived artifacts: site reference.json regenerates (docs-json
  gains a member); grammar manifest and lexical table UNTOUCHED
  (probe 4 — no new words).
- Oracle/fuzz: n/a — no F# counterpart, parser untouched.
- DECISIONS row `yaml-splitdocs` (the coherence-bug framing, the
  shared-boundary-law rule, both parks with named triggers);
  CHANGELOG v0.0.17 New features.

## Sizing

Small: one factored separator PREDICATE in Yaml.fs (parseDocs
splits FILTERED content, the member splits RAW text — the loops
differ, the boundary test must not) + one builtin registration +
the teaching string, then pins and the doc idiom. The risk
concentrates in boundary-law drift (two splitters disagreeing) —
closed by sharing the predicate, and pinned by a split-vs-parseDocs
agreement test (N groups ⇔ parseDocs sees N documents, over the
same fixtures). Half a session.
