# weir — PLAN-pure Stage 2: expose labels + the ambient/mutation partition

Status: BUILT (2026-09-15, branch pure-stage2 off plan-apply-spec) —
both deliverables shipped: the ambient/mutation partition
(Effects.effectClass + the per-method net split, consultable at check
AND eval; [D:pure-stage2]) and the `deterministic` block (the
withinKinds union's second standalone head, enforced through the Stage 1
walk with ceiling = ambient-input). `--can` groups by class; pins,
docs, and the fuzz-generator extension landed. The tree-sitter-weir
grammar owes the `deterministic` keyword (grammar-currency red, the
accepted xml/pure posture — noted in NOTES-agent).
Was BLESSED (2026-09-14, designer — opened by call as the
prerequisite for [PLAN-plan-apply], which cannot reify effects the
language has not partitioned). Extends [D:pure-stage1] / [D:pure];
the internal label set has existed since Stage 0.

## Probe outcomes (2026-09-15, recorded before execution)

(1) THE LABELS AND WHERE — the internal effect labels are NOT a
materialized `label`-typed table. They live IMPLICITLY, two places:
`Purity.effectfulModules/effectfulQualified/effectfulBare` (the CLASSES,
a name → effectful boolean) and `Purity.effectPhrase` (the VOCABULARY,
a name → "'File.write' writes the filesystem" i.e. the fs.write/fs.read/
net/proc/env/clock label rendered as a phrase). "Stage 0's table" = these
functions. Stage 2 REFINES them: a new `effectClass : name -> EffectClass`
in Purity.fs reading the same dispatch effectPhrase already performs, plus
the per-method net split. No re-tag.

(2) EVAL-TIME CLASS RESOLUTION — CONFIRMED resolvable. At eval an
`Http.send` call reaches `Builtins.runRequest` with the request as
`VRecord("HttpRequest", fields)`; the `method` field is `VUnion(case,
None)` (Get/Post/…), already read by `httpMethodName`. So the class of a
builtin call — including Http.send's per-method class — is a pure function
of the runtime Value the interpreter holds at the call site, exactly where
plan/apply intercepts. Repro built (probe-http-class.weir) resolving
Http.send{post}=Mutation vs Http.send{get}=Ambient via
`Builtins.effectClassOfCall` at eval; both classes resolve. Http.fetch/
Http.query fixed-Ambient; the label→class map is total.

(3) FUZZ GENERATOR — CONFIRMED it does NOT emit `pure` (nor will it emit
`deterministic`): the pure-stage1 commit left the generator untouched
("`pure` ungenerable — names are v{n}/w{n}"). Extended here: a new
`SDeterministic` Stmt case renders a `deterministic` head + an ambient/
pure body (checks clean), so invariant 1 (generated programs check clean)
exercises the new parser+checker path.

## What already exists (do not rebuild)

Stage 0 tagged every builtin INTERNALLY with its real effect label —
`{ fs.read, fs.write, net, proc, env, clock }` — and Stage 1 enforces
a BOOLEAN over them (`pure == only ∅`). The labels are recorded, never
exposed. Stage 2 is a REFINEMENT, never a re-tag: it reads the labels
already in the table.

## The one new idea — the partition

Split the effect labels into two classes by a single principled line,
AMBIENT-INPUT vs EXTERNAL-MUTATION:

- ambient-input (reads the world, changes nothing): `fs.read`, `env`,
  `clock`, and the QUERY subset of `net` (GET/HEAD/OPTIONS/QUERY —
  idempotent by HTTP's own semantics).
- external-mutation (changes the world): `fs.write`, `fs.delete`,
  `proc`, and the MUTATING subset of `net` (POST/PUT/DELETE/PATCH).

This line is LOAD-BEARING TWICE, which is why it earns its own tier:
it is the `deterministic` split (a computation that only reads ambient
input is reproducible), AND it is exactly [PLAN-plan-apply]'s
reads-RUN / mutations-CAPTURE rule. One classification, two consumers.

`net` needs the method split because `Http.send` carries its method in
the request value: the classifier reads the `HttpMethod` case
(Get/Head/Options/Query → ambient; Post/Put/Delete/Patch → mutation).
`Http.fetch`/`Http.query` are ambient; `Http.send` is
method-dependent, resolved at the value.

## Deliverables

1. THE PARTITION as a consultable classification. `effectClass :
   label -> Ambient | Mutation` in the effect table, plus the
   per-method `net` resolution. This is the slice [PLAN-plan-apply]
   consumes at eval time; it is small and derives from the existing
   labels.
2. `deterministic` — the user-facing Stage 2 surface: a bare head +
   indented block (the `pure`/withinKinds precedent, a Standalone
   kind) asserting the body reaches NO external mutation — ambient
   reads are FINE. `deterministic == only ambient-input`, one tier up
   the lattice from `pure == only ∅`. A reachable mutation is a
   located check error naming the offender and its class ("this
   'deterministic' block forbids external mutation, but 'File.write'
   writes the filesystem — reads are allowed"). Opt-in only, effect-
   normal outside it, same as `pure`.
3. `--can` already lists labels; it now groups them by class in the
   report (ambient reads vs mutations) so "what does this script
   CHANGE" is answerable, not just "what can it touch".

## Explicitly still deferred (unchanged triggers)

- `only <effects>` arbitrary ceiling block + effect vocabulary
  granularity (coarse `fs`, an `io` bundle) — its own trigger (ported
  code wanting a specific ceiling); `deterministic` is the ONE named
  ceiling Stage 2 ships because it has two consumers.
- `within tmp` region discharge (scoped writes counted ambient) —
  region tracking, own trigger; v1 `within tmp` still colours
  `fs.write`.
- Effect-polymorphic signatures; Secret taint; `proc` sub-⊤ sandboxing
  — separate horizons.

## Footprint

- Check.fs / Purity.fs: `effectClass` over the existing label table;
  `deterministic` reachability (reuse the Stage 1 walk, ceiling =
  ambient-input instead of ∅); the located mutation teaching.
- Ast.fs: `deterministic` joins `withinKinds` as a Standalone kind
  (the [D:within-kind-union] table; `pure`'s precedent — grammar
  admits, a mutation refuses at check).
- Parser.fs: `deterministic` head (a keyword; the tree-sitter-weir
  ritual + temporary `grammar-currency` red, same posture as `pure`).
- Builtins.fs: the per-method `net` class resolution reads the request
  `HttpMethod`.
- Repl.fs `--can`: group the report by class.
- Tests: `effectClass` per label; `deterministic` admits a reading
  body, refuses a writing one, refuses `proc`, admits `Http.query`,
  refuses `Http.send {post}` and admits `Http.send {get}`; the badge
  interplay; `pure ⊂ deterministic` (a pure body is trivially
  deterministic).
- SKILL: a `deterministic` bullet beside `pure`; CHANGELOG; DECISIONS
  `[D:pure-stage2]` (cites [D:pure-stage1], states the partition is
  plan/apply's shared line); `[D:]` at code sites.
- Gates: build ×4, units, publish, skill-doc, e2e cell, and — a
  keyword landed — the tree-sitter-weir grammar ritual + fuzz 3×10k
  (parser moved).

## Relation

Prerequisite for [PLAN-plan-apply] (consumes the partition).
Refines [D:pure-stage1] / [D:pure]. Independent of the `only` tier
(that stays deferred on its own trigger).
