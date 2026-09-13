# weir — `pure`: an opt-in purity assertion in an effect-normal language

Status: STAGE 1 BUILT (2026-09-12, branches withinkinds-union +
pure-stage1; [D:within-kind-union] [D:pure-stage1]) — the prerequisite
and the enforcement stage both shipped. The withinKinds union landed
first (kind-first Check/Eval, FS0025 at every consumer for a new kind);
then the `pure` block (a bare head — the family's first standalone
kind, never `within pure`) and the `let pure` modifier, enforced in
Script.checkStatement's post-check layer through the classifier moved
to Purity.fs (Check.fs compiles before Builtins — the compile-order
trap the Footprint anticipated). DEVIATIONS from this plan's Stage 1
sketch, recorded: (1) the refusal is offender+span — "this 'pure'
block forbids effects, but 'File.write' writes the filesystem" — NOT
the full call-trace rendering sketched below; the trace is a v2
refinement. (2) HOF call-site resolution via the `--can` walk was NOT
built; enforcement reuses Stage 0's conservative isPureExpr (a
function-typed unknown refuses), so a HOF-heavy pure body may be
over-refused — the same conservatism the badge has. (3) `pure` rides
the withinKinds table with a Standalone flag (the manifest's
withinKinds list includes it; the `within` surfaces filter it).
grammar-currency red vs tree-sitter-weir until it learns `pure` — the
xml ritual. Fuzz seeds owed to CI (parser moved).

Previously: STAGE 0 BUILT (2026-09-11, branch pure-stage0; [D:pure]) — the
display stage shipped: classification in Can.fs (whole effectful modules
+ member exceptions + bare names + node kinds; console counts; `fail`
pure, `exit` not), conservative transitive `isPureExpr`/`pureTopBindings`
(unknown callables forfeit the badge — missing allowed, lying not), and
the `(pure)` hover badge on function-typed top-level lets. Two scope
notes against the original sketch: #sig turned out to be the
COMMAND-signature surface, so the badge is hover-only; and inner lets /
pattern-bound lets carry no badge yet (top-level named lets only — a
Stage 0.5 if wanted). Stage 1 (enforce) remains, behind the
[D:host-strictness] withinKinds prerequisite below.

Originally APPROVED same day. Ship Stage 0 (infer + display) first, then
Stage 1 (enforce). The four open questions below were approved as their
recommended answers.

## The driver, and the sizing argument

No hard external receipt — this is a positioning bet the designer chose,
out of the dbt-migration design chat (nested optionals → effect
visibility → trusting vendored automation). State that plainly rather
than manufacture a receipt: the *sizing* argument is what justifies it.
Stage 0 is nearly free — it reuses the reachability walk `--can` already
performs — and it is display-only, so it validates the whole analysis
(HOF behaviour, transitive vendored-source crossing) against real ported
code BEFORE a single keyword is added. The bet is the sentence no
shell-adjacent language can say: *in a language where effects run free,
you can carve out and verify a pure island exactly where it pays* — the
cacheable core, the trusted predicate, the dry-run-able transform.

## Governing principle — effect-normal, so `pure` is opt-in

weir is a shell: effects are the ambient NORM, purity the exception. This
is the inverse of Haskell, and it dictates everything:

- `pure` is strictly an OPT-IN assertion — never a default, never a gate.
  Effects run free and un-nagged; nothing is rejected for being effectful
  unless it sits inside a `pure` region the author explicitly wrote.
- Display is ASYMMETRIC: surface the rare `(pure)`; stay silent on the
  effectful norm. Painting an effect row on every function announces the
  sky is up. Effect LISTS live in `--can`, on demand — never in hover.
- There is no implicit purity inference that gates or warns. The default
  posture (effects are normal) is untouched; `pure` only does something
  where it is written.

## Effect model (v1 = binary)

One distinction: does the transitive reachable set contain ANY effect, or
not. Reuse the `--can` reachability walk — it already crosses vendored
source transitively, and vendored code is sha-pinned SOURCE (the
no-decompile rule guarantees source exists), so purity is a VERIFIED fact
about the pinned tree, not a trusted boundary declaration.

Implementation discipline that makes every later tier a refinement: tag
each builtin INTERNALLY with its real label (`fs.read`, `fs.write`,
`net`, `proc`, `env`, `clock`) but EXPOSE only the boolean in v1. Then
`only …` / `deterministic` later expose labels already recorded — never a
re-tag. `pure == only ∅`, so the bottom of the lattice shipped now is the
bottom of the lattice extended later. No corner painted.

## Stage 0 — infer + display (zero new syntax)

- Tag builtins pure/effectful (labels recorded, boolean exposed).
- Compute effect-emptiness of a function/expression via the `--can` walk.
  HOFs resolve at the CALL SITE (a HOF is pure iff its function args are —
  the existing walk captures this; NO effect-polymorphism).
- Surface `(pure)` in hover / `#sig` ONLY when it holds. Effectful
  signatures render exactly as today — no effect rows, no nag.
- Value: de-risks the analysis (HOF behaviour, vendored crossing) before
  any grammar change. `(pure)` on the `from xml`→record shapers is the
  demo that tells us Stage 1 earns its keep.

## Stage 1 — enforce (opt-in only)

- `pure` BLOCK — one new `withinKinds` sibling (indentation block, no
  argument). Body must have an empty effect set; a reachable effect is a
  check error naming the offender and the trace:
      type error: this 'pure' block forbids effects, but 'buildImage'
      spawns a process — reached via buildImage → runBicep → 'bicep lint'
      (a command) [vendored: acme.build]
- `let pure f = …` MODIFIER — weir's FIRST post-`let` modifier: weir has
  no `let rec` / `let inline` / `let mutable` (`rec` is reserved and
  teaches `'rec' is a keyword`), so there is NO existing modifier chain to
  slot into. This is a new let-head grammar production and is priced as
  such (parser production + assembler interaction + fmt + fuzz
  alternation) — not borrowed as F#'s apparent-zero slot cost. It takes
  F#'s modifier POSITION so any future modifier would chain naturally.
  Desugars to a body-spanning `pure` block. Single token, no list-parsing.
- `proc` reachable ⇒ not pure. The escape hatch is closed by
  construction; no special `proc` handling needed because there is no
  `only` yet.
- No implicit purity anywhere — nothing gated unless `pure` is written.

## Explicitly deferred (each with its trigger)

- `only <effects>` ceiling block + effect vocabulary / granularity
  (`fs.read` vs `fs.write`, a coarse `fs`, an `io` bundle) — TRIGGER:
  ported code keeps wanting "pure except it reads." Watch this frequency
  in the dbt port; it is the signal for the very next tier.
- `proc = ⊤` rules / refusing a vacuous `only … proc` (a ceiling that
  admits proc bounds nothing) — lands WITH `only`.
- `deterministic` tier (ambient-input vs external-mutation split) —
  TRIGGER: a cache / reproducibility need.
- `within tmp` region discharge (writes confined to a scoped, deleted dir
  counted pure) — TRIGGER: a pure transform wanting scratch space; needs
  region tracking, so v1 stays conservative (`within tmp` colours
  `fs.write`).
- Effect-polymorphic SIGNATURES (the HOF-exact arrow) — TRIGGER: HOF
  exactness demanded. Reachability mode carries v1.
- `let only …` / effect lists in a `let` head — NOT planned. The mouthful
  is the language telling us it is the wrong shape.
- Secret taint propagation; demoting `proc` below ⊤ via OS sandboxing
  (`proc.net-` / read-only-fs jails) — separate horizons, own receipts.

## Footprint (Stage 1)

- PREREQUISITE: take [D:host-strictness]'s deferred (b)+(c) — restructure
  Check/Eval kind-first and make `withinKinds` a union — BEFORE adding
  `pure` as the sixth kind, so the addition is a build failure everywhere
  it is unhandled rather than a silent gap (the table drifted three times
  across its consumers unguarded: the check direction missing `proc`,
  eval's wildcard `tmp` arm, Can.fs missing `lock`). This is owed
  regardless; `pure` is the trigger that finally prices it in.
  [PLAN-plan-apply] inherits it (`plan` would be the seventh kind).
- Check.fs: builtin effect-label table + reachability→emptiness (reuse
  `--can`); pure-region check + teaching error carrying the trace.
- Ast.fs / Parser.fs / Script.fs: one block head (`pure`) in the
  `withinKinds` family; `pure` let-modifier in the let-head grammar.
- Lsp.fs: `(pure)` badge in hover / `#sig` (asymmetric — pure only).
- Builtins.fs: effect labels on the builtin table.
- Docs/tests: SKILL/GUIDE bullet + runnable blocks; e2e cells (pure body
  accepted; effectful body + vendored-spawn trace rejected; `let pure`
  form; composition with `within proc`); unit pins; a `[D:pure]`
  DECISIONS row.
- Gate ripple: `pure` becomes a keyword AND a `withinKind` → regenerate
  editors/grammar-manifest.json + docs/reference/lexical.md, update
  editors/micro + tmLanguage, and `grammar-currency` goes RED until
  weir-shell/tree-sitter-weir learns `pure` (the split-repo ritual, as
  with `xml`).

## Positioning claim

"In a language where effects run free, you can carve out and verify a
pure island exactly where it pays." Surface the exception; stay silent on
the rule.

## Open questions — approved answers

1. Stage 0 (display-only) ships as its own increment first. YES — free,
   de-risks the analysis.
2. `pure` is a `withinKinds` sibling (reuses assembler / offside), not its
   own construct. YES.
3. `pure` becomes a reserved keyword (needed for the block head and
   `let pure`), accepting the tree-sitter-weir ritual + temporary red
   `grammar-currency` — same posture as `xml`. YES.
4. Internal effect label set = `{ fs.read, fs.write, net, proc, env,
   clock }`, exposed as a boolean in v1. Confirmed; a coarse `fs` is a
   later granularity call driven by the deferred `only` trigger.
