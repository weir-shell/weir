# weir — anonymous record literals: `{| key = key; value = value |}`

Status: EXECUTED (landed 2026-09-03, proposed same day).

Completion addenda (2026-09-03):
- All FCS probes ran FIRST; one verdict flipped the plan (empty
  accepts in F# — the fence text dropped its F# claim and the edge
  joined the divergence row) and a late probe flipped a second claim
  before it could land in the row: `{| r with a = 2 |}` ACCEPTS in
  F# (widening included) — copy-update is a weir-rejects edge, not
  an agree-reject.
- The divergence is NOT a new row: `no-anonymous-records` NARROWED
  in place (the house pattern — record-fields-ignore-indent,
  no-record-patterns), surviving at three edges: generic, empty,
  copy-update. The plan's `anon-literal-mono` pre-name dissolved
  into that row; fidelity pins use the row id.
- The stop-and-report clause did NOT fire: the typeDefFor sweep was
  23 mechanical sites, behavior-identical for user-spellable names.
- One seam the plan missed, found by the multi-line probe: the
  ASSEMBLER's bracket scanner anchored the sibling-entry column on
  the `|` of a dangling `{|` (entryCol pointed at the opener's own
  second character — "siblings sit at column 9"). Fixed in
  bracketFold (`{|` is one opener token) plus an explicit
  `prev.EndsWith "{|"` dangle arm — the operator-dangle clause that
  happened to cover expression lines excludes `type` lines, where a
  nested anon TYPE can dangle the same token.
- A RIDER FIX the registration design forced into view: the bare
  `{ a = 1 }` candidate scan iterated ALL of env.Types, so a drained
  same-shape hidden def (adapter shapes could do this since
  [D:anon-records]) would make a declared record look ambiguous —
  the scan now filters isUserName, unit-pinned.
- Wire order ruling made explicit: a literal writes its fields in
  WRITTEN order (a reader's canonical order is the reader's);
  deterministic both ways, stated in the ledger row.
- The tree-sitter probe resolved by construction: the grammar is
  purely lexical with a stray fallback (nothing ERRORs) and `{|`/`|}`
  were already single tokens — doc examples landed with the feature.
- LSP labels follow the whole-dotted-word convention (`anon.ip`, not
  `ip`) — the harness pin matches it.

Originally proposed: Origin: the REPL receipt — writing a
two-field JSON object required `Map.ofPairs` ceremony, and the Map
spelling silently forces every value to ONE type (`{"key": "x",
"count": 3}` is unwritable that way). This is the WRITE-SIDE MIRROR of
[D:anon-records]'s read receipt (the throwaway `type IPResult` that
motivated `from json {| ip: string |}`); the divergence cell in
COMING-FROM already names literals as the pending half.

## The form (all there is)

```
{| field = expr; … |}
```

Expression position, F#'s exact spelling (`=`, `;`-separated, at least
one field). The literal's type is the synthetic nominal canonical name
the machinery already mints (`Types.anonRecordName`, fields sorted):
`{| key = k; value = v |} : {| key: string; value: string |}`.

Everything else falls out of [D:anon-records]'s one mechanism:

- same-shape literal and adapter-slot type UNIFY (same canonical
  name) — `from json {| ip: string |}` output and a hand-built
  `{| ip = "x" |}` are the same type;
- a declared record with identical fields stays a DIFFERENT type
  (nominal law untouched — `expected T, got {| ip: string |}`);
- display/echo/hover/type errors render free (the name IS the form);
- `[{| … |}] |> to json` writes free (the recursive record writer
  never sees a new value kind — see decision 1).

## Pre-made decisions

1. **The TYPED node is `TERecord`.** Only the untyped AST gains a
   node (`EAnonRecord of (string * Span * Expr) list`); the check arm
   returns `TERecord(canonicalName, tfields)` with
   `Ty = TNamed(name, [])`. Eval (`VRecord`), the json/yaml writers,
   wireRenames, LSP's typed-tree walkers — ZERO new arms downstream.

2. **Monomorphic at the site.** Field types must RESOLVE TO GROUND at
   the literal (the canonical name cannot contain a unification
   variable). An unresolved field refuses with a teaching: annotate,
   or declare a record — declared records serve the polymorphic case.
   NAMED DIVERGENCE: F# admits generic anonymous records
   (`fun x -> {| a = x |}` typechecks there) — pre-name it
   `anon-literal-mono` for the oracle, sibling to update-path-plain.

3. **Registration is check-time, through two EXISTING seams plus one
   helper.** The parse-time `pendingAnonDefs` push cannot serve
   literals — the parser sees values, not types; the name exists only
   after inference. So:
   - *within-statement* (the flagship `[{| … |}] |> to json` runs
     `jsonableElem` in the SAME statement; `.field` access and record
     patterns likewise): `TypeEnv` gains a reference-shared mutable
     `AnonLitDefs` dictionary (created at typecheck entry; `{ env
     with … }` copies share it — record copy is shallow, so it
     reaches every recursion branch for free). A `typeDef env name`
     helper — Map first, dict second — replaces the ~23
     `Map.tryFind … env.Types` def-resolution sites (mechanical
     sweep; one lookup path, the [D:anon-records] ethos).
   - *cross-statement*: the check arm ALSO calls `Types.pushAnonDef`;
     the NEXT statement's `withAnonDefs`/`checkDecl` drains persist
     the def into real `env.Types` — zero new seams.
   Stop-and-report clause: if the helper sweep turns up a lookup site
   where dict-fallback changes behavior for NON-anon names (it must
   not — '{' keeps canonical names un-typeable and out of user
   space), stop and report before landing.

4. **Fences, each a parse-time teaching** (first-reached beats the
   expecting-list, [D:label-leaks]):
   - punning `{| key; value |}` → "anonymous records take field =
     value — write {| key = key |}" (F# refuses punning too; the
     record-patterns fence stays symmetric);
   - `{||}` / `{| |}` empty → refuse (F# refuses; a record with no
     fields names nothing);
   - `{| r with a = 1 |}` → refuse toward the park: anonymous
     copy-and-update stays parked per PLAN-record-update ("with
     anonymous records, if ever" — the trigger is a receipt, not this
     plan);
   - `{| a: int |}` in EXPRESSION position → "that is the type
     spelling — a literal takes field = value" (the `:`-vs-`=`
     confusion is predictable, name it);
   - `keywordFieldGuard` reused verbatim (a keyword field name is the
     same mistake in either brace form).

5. **Duplicate fields refuse at check**, same site-shape as
   `ERecord`'s (span on the LAST duplicate, message-identical).

6. **What does NOT move**: the adapter slot and type-position nesting
   rules ([D:anon-nesting]); Args.load/Env.load exclusions (a
   contract's shape is documentation); record PATTERNS (`{| |}` has
   no pattern form — that fence is [D:record-patterns]'s, untouched);
   bare `{ a = 1 }` nominal inference (the anon literal never joins
   its candidate set — `{|` decides before `{`'s arms are reached);
   `show`'s nameless record render (already the rule).

## Probes first (FCS, house order) — RAN 2026-09-03, verdicts in

1. `{| a = 1; a = 2 |}` — REFUSES (FS3522 "appears multiple times"),
   as expected.
2. `{| a; b |}` — REFUSES (FS0609 "Field bindings must have the form
   'id = expr;'"), as expected — the fence text echoes the shape.
3. `{| |}` / `{||}` — **F# ACCEPTS** (`val x: {| |}`) — the plan's
   guess was WRONG. Weir refuses anyway: no receipt, and the empty
   object is already writable (`Map.ofPairs []` → `{}`). The fence
   cannot cite F#; the refusal is a NAMED divergence edge in the
   COMING-FROM cell, trigger for lifting: a real `{}` receipt the
   Map spelling cannot serve.
4. `{ | a = 1 | }` (spaced) — REFUSES (FS0010), one token confirmed.
5. `fun x -> {| a = x |}` — ACCEPTS (`x: 'a -> {| a: 'a |}`, generic)
   — the `anon-literal-mono` divergence holds as pre-named.
6. `{| a = 1 |} = {| a = 1 |}` — true; weir's `==` follows whatever
   declared records do today, pinned on the weir side.
7. `{| a = {| b = 1 |} |}` — ACCEPTS, renders nested canonical form
   (`val x: {| a: {| b: int |} |}`) — matches weir's rendering.

## Mechanism (by file)

- **Parser.fs** — one atom arm BEFORE `recordLit`, opening on
  `pstring "{|"` (no ws between the chars), committing on the head
  per the consumed-separator law [D:arm-commit]:
  `keywordFieldGuard <|> sepBy1 fieldAssign (str_ws ";") .>> "|}"`,
  plus the four fences. `fieldAssign` reused as-is.
- **Ast.fs** — `EAnonRecord` + the walker arms (exprChildren, sexpr,
  span plumbing — the record-patterns sweep found ~12; expect
  similar).
- **Check.fs** — the infer arm: check fields in order → dup refusal →
  resolve each field type, ground-check (decision 2's teaching) →
  `anonRecordName` → register (dict + `pushAnonDef`) → `TERecord`.
  Plus the `typeDef` helper sweep (decision 3).
- **Types.fs** — `AnonLitDefs` field on TypeEnv; `anonRecordName`
  reused untouched.
- **Eval.fs / Fmt.fs** — expected ZERO (decision 1 / fmt normalizes
  indent only); roundtrip pin proves fmt, an eval e2e proves the
  writer.
- **Lsp.fs** — expected zero code; hover on the literal and `.`
  completion on its binding ride `TNamed` + the registration seams —
  pin both in lsp-e2e.
- **Grammars** — in-repo micro/tmLanguage are position-blind and
  already tokenize `{|`/`|}` (the anon-record rule): zero movement.
  tree-sitter is EXTERNAL: probe the pinned grammar with an
  expression-position literal; if it ERRORs, gated corpora (skill
  blocks, examples/) stay literal-free until the grammar repo follows
  at its next pin bump — the doc examples land in the same commit
  ONLY if the probe is clean. Manifest: no keyword/blockHead
  movement, no manifest change.

## Verification

- Unit pins (~12): flagship type + value; unify-with-adapter-shape;
  nominal-law refusal against a same-shaped declared record; seq
  homogeneity `[{| a = 1 |}; {| a = 2 |}]` and the mismatch error
  naming both canonical names; nested literal; dup field; the four
  fences; ground-check teaching; cross-statement field access
  (`let x = {| a = 1 |}` then `x.a` NEXT statement); within-statement
  access (`{| a = 1 |}.a`).
- e2e: the user's transcript as the flagship cell — heterogeneous
  fields, `|> to json |> File.write`, cat the file; a record-pattern
  destructure over a literal; REPL echo shape.
- Oracle: the accept rows agree with FCS free (same spelling); the
  mono refusal lands as the named divergence row.
- Full ceremony: fantomas → unit → publish → e2e → skill-doc →
  **3 fresh 10k seeds (the parser moved)** → oracle → timing.
- Docs: SKILL (the literal beside the type form, one placement rule);
  GUIDE's when-to-reach gains the write side (anonymous = a foreign
  shape read OR WRITTEN once; declared = your own data and anything
  reused); COMING-FROM divergence cell NARROWS (literals exist;
  punning/generic/copy-update remain fenced, each named); DECISIONS
  row `anon-literals`; CHANGELOG v0.0.14.

## Parked (with triggers)

- Punning — trigger: a receipt plus appetite for a from-F#
  divergence; the pattern fence moves with it or not at all.
- Anonymous copy-and-update — stays with PLAN-record-update's park.
- Generic anonymous literals (lifting decision 2) — trigger: a real
  polymorphic-builder receipt the annotate teaching cannot serve.
