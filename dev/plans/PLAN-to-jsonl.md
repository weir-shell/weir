# weir — `to json` writes ONE document; `to jsonl` writes lines

Status: EXECUTED (landed 2026-09-04, proposed same day).

Completion addenda (2026-09-04):
- The checker/eval movement was exactly the predicted dispatch split;
  the renderer moved zero lines. The `->` of the whole feature:
  `TETo("jsonl")` is the old json arm verbatim, `TETo("json")` is
  `jsonLine` applied once.
- Top-level admission reuses jsonableElem unchanged — probed edges:
  a typed `Some 1 |> to json` → `1`, a `None` ELEMENT → `null`, and
  a bare `[] |> to json` still refuses on the unresolved element
  type (pre-existing, identical on main; the empty-seq pin uses a
  typed empty).
- THREE derived artifacts were stale, not two: grammar-manifest.json
  (its from/to inventories are direction-AWARE even though the
  grammar token is not), site reference.json (docs-json dump), and
  the lexical keyword table (gen-lexical) — each caught by its own
  e2e staleness gate, regenerated, folded in.
- The interop referee wrapped its payloads (`[{ A = … }] |> to
  json`) and failed 103/103 as the break predicts — unwrapped, it
  now exercises the one-document form against Python's json.
- Verification: unit 1428, e2e battery all green (to-jsonl cell +
  referee + inventories), skill-doc 243 blocks, lsp-e2e green incl.
  the two new probes, oracle 176, timing nominal. Parser untouched —
  no fuzz owed, none run.

The write-side mirror of [D:from-jsonl], arriving on its stated
trigger: that row parked `to jsonl` "waiting for a receipt", and the
receipt has now surfaced twice — the anon-literals session opened on
the `[{| … |}] |> to json` wrap, and the wrap question came back
independently a day later. The plain name carries the NDJSON behavior
today, exactly the backwardness the read side already fixed: one
document is the common case, the plain name should spell it.

## Probes — RAN 2026-09-04, against main (`8a24062`)

1. `to jsonl` today is a CHECKER refusal — "unknown output format
   'jsonl'; available: json, yaml" — not a parse error. The parser
   accepts any adapter word after `to`; the checker owns the set.
   Parser UNTOUCHED → no fresh fuzz seeds owed (the [D:yaml-seq]
   precedent).
2. All three grammars tokenize the adapter DIRECTION-AGNOSTICALLY:
   tree-sitter `(from|to) ws (jsonl|json|yaml)` (one token, grammar.js
   :167), micro and tmLanguage the same regex. `to jsonl` colours
   today; ZERO grammar movement, no regen, no zed ritual. The
   adapter-inventory e2e compares WORD sets — `jsonl` is already in
   all of them.
3. Today's element law is broad: `[1; 2] |> to json` → two scalar
   documents; `[[1; 2]; [3]] |> to json` → `["[1,2]"; "[3]"]` — the
   renderer already renders ANY admitted element as one minified
   token, arrays included. The one-document form is that same
   renderer applied once.
4. `to yaml` ALREADY has the scalar-in shape: `{| a = 1 |} |> to yaml`
   → one document, seq → `---`-separated stream (probed). The arity
   precedent is one adapter over; after this lands the two writers
   agree on arity and differ only in what a seq means at the top —
   each format's native plural.
5. Migration surface (grep): Tests.fs 31 mentions, SKILL 9, e2e 9,
   GUIDE 7, tools/jira-branch.weir 1 (`[issue] |> to json` — becomes
   SHORTER: `issue |> to json`), plus README/COMING-FROM/showcase to
   sweep on execution.

## The design

One sentence: today's `to json` renders each element as one document;
that behavior MOVES to the name `to jsonl`, and `to json` becomes
exactly ONE element rendered as ONE document. The element law and the
renderer do not move — the change is arity and dispatch only.

1. **`to json : 'a -> seq<string>`** — one document, one minified
   line, one seq element out. The top level admits exactly what an
   ELEMENT admits today (the recursive field law): record → object,
   `seq<T>` → array (`[{…},{…}]`), scalar → scalar document,
   top-level `None` → `null` (the element rule — a document slot
   cannot be omitted, [D:json-option]'s write law applied once).
   Building an array document FORCES the seq (one line cannot
   stream) — strict like the `Seq.force` family; the streaming form
   is `to jsonl`, stated in docs.
2. **`to jsonl : seq<'a> -> seq<string>`** — byte-identical to
   today's `to json`: one document per element, lazy, N elements → N
   lines, empty seq → zero lines.
3. **Checker** (Check.fs, ETo arm): the format dispatch gains `jsonl`
   (today's seq-demand path, renamed) and the `json` arm drops the
   seq demand to the plain jsonable demand. The refusal inventory
   updates: "available: json, jsonl, yaml".
4. **Eval** (Eval.fs): `jsonl` → today's per-element path verbatim;
   `json` → render the ONE value via the same jsonRow machinery,
   yield a single element.
5. **Surface derivation** [D:form-word-hover]: one builtinDocs entry
   ("to jsonl") and hover/completion/colorizer/inventory-gate all
   derive — no second list. `to json`'s doc rewrites to the
   one-document claim.
6. **THE BREAK, named**: every existing `xs |> to json` checks clean
   and CHANGES MEANING — N lines becomes one array document. This is
   the checks-clean-behaves-differently class, the changelog section
   that exists for it — first real entry of that class since
   publication. Accepted over a transitional teaching error because
   the teaching would refuse the array-document form, which has no
   other spelling; pre-1.0 is the cheapest this cut ever gets, and
   the read side survived the same rename post-publication
   ([D:from-jsonl] — "every one SHORTER").
7. **The yaml asymmetry is RULED, not touched**: `xs |> to yaml`
   keeps emitting a `---` stream. Stream WRITES are legitimate — the
   writer types every document at write time and homogeneous streams
   have real consumers (`kubectl apply -f`); stream READS died for
   heterogeneity ([D:yaml-seq] — a real bundle is
   Deployment+Service+ConfigMap, untypeable). The one-way door is
   correctly one-way: a written stream reads back per the split-on
   `---` teaching. Seq-at-the-top differs per format DELIBERATELY —
   array is JSON's native plural (the stream form is the thing
   literally named jsonl), the stream is YAML's. Recorded so no
   audit reads either as a gap.

## Roundtrips after (every adapter pairs with its own name)

- `x |> to json |> from json T` — one object document
- `xs |> to json |> from json seq<T>` — one array document
- `xs |> to jsonl |> from jsonl T` — NDJSON
- The crossed pairing `to json |> from jsonl` retires from docs.

## What does NOT move

- The renderer (jsonRow / formatFloat / escaping), the recursive
  field law, [D:json-option]'s omit-vs-null write rules, `Map`
  emission, `[<Wire>]` keys — all shared, byte-identical per
  document.
- `from json` / `from json seq<T>` / `from jsonl` — read side is
  already right.
- `to yaml` — ruled above.
- Http: `Json of seq<string>` still carries the caller's lines; the
  documented idiom SHORTENS (`body = Json (payload |> to json)`,
  wrap gone).
- Parser, grammars, zed pin — probes 1–2.

## Verification

- Unit pins: one-document object/array/scalar/null forms; array
  build is strict (a probed effect runs at the call); `to jsonl`
  byte-equals old `to json` on the same inputs (the migration pin);
  empty-seq pair (`[] |> to json` → `["[]"]`, `[] |> to jsonl` →
  zero lines); unknown-format inventory names all three; roundtrip
  trio above; existing ~31 Tests.fs sites swept to intent (NDJSON
  emitters → `to jsonl`, single-doc wraps unwrap).
- e2e: the Http body idiom unwrapped; an NDJSON pipeline via
  `to jsonl | jq`; adapter inventory stays green (word-set, probe 2).
- lsp-e2e: completion after `to ` offers jsonl; `to jsonl` word
  hovers its doc (both derive, still protocol-checked).
- skill-doc: SKILL's json bullet rewrites (the two-writer table +
  the strictness sentence); GUIDE/COMING-FROM/README/showcase sweep.
- Oracle/fuzz: n/a — F# has no adapters; parser untouched.
- DECISIONS row `to-jsonl` (cites from-jsonl's parked trigger, the
  yaml ruling, receipt-shape-rule compliance: "to json writes ONE
  minified document, one line; to jsonl one per element; pretty
  output unexamined").
- CHANGELOG v0.0.16: Breaking + the checks-clean-behaves-differently
  entry.

## Sizing

Small: the checker/eval movement is a dispatch split over machinery
that stays put (probe 3 — the renderer already does both jobs), one
builtinDocs entry, no parser/grammar/fuzz. The bulk is the migration
sweep (~60 sites, most shorter) and the docs rewrite. One session.
