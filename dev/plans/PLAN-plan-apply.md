# weir — plan/apply: effects reified into an inspectable Plan

Status: EXPLORATORY, deferred behind a receipt (2026-09-11). Depends on
[PLAN-pure] (you cannot reify effects you have not named). The KSL port
([PLAN-ksl-migration]) is the likely receipt.

## The idea

A `plan` block runs a computation but CAPTURES its external effects as
data instead of performing them — yielding an inspectable, diffable `Plan`
value you can show, confirm, then `apply`. Terraform's plan/apply loop as
a general language primitive, not a per-tool reimplementation.

Scope honesty, stated in the pitch and not only the boundaries: this is a
FULL guarantee only for weir-NATIVE mutation (`File`/`Dir`/`Http`). In a
shell most scripts are mostly `proc`, and a plan cannot see past
`bicep deploy` or `kubectl apply` — a `proc`-heavy script gets a PARTIAL
plan with opaque process nodes, covering the minority of what it does.
KSL is the ideal receipt precisely BECAUSE it is fs-only, and that is
unusual. This is dry-run for weir-native effects — file/config
orchestration, generation — NOT "dry-run for any script", and the
homepage's three-distance check is a different (narrower, already-shipped)
thing; do not conflate them.

## Why it belongs to weir specifically

- It is the value-level DUAL of [PLAN-pure]. #1 asks statically "does this
  reach `fs.write`?"; plan/apply reifies that same `fs.write` as a pending
  op. The effect labels ARE the Plan's op constructors:
  `fs.write -> WriteFile(path, content)`, `proc -> RunProcess(argv)`,
  `net -> HttpSend(request)`. `Plan` is essentially `seq<Op>` over the
  effect vocabulary. Build #1 first; plan/apply is its runtime payoff.
- weir already sells "wrote nothing" (the homepage three-distance check).
  plan/apply turns that discipline into a primitive.

## Mechanism

A `plan` block intercepts the effectful builtins for the scope — same
SHAPE as `within` (a scope that changes how effects behave), except
`within` changes lifetime/env and `plan` changes WHETHER they execute.
Inside `plan`, `File.write "x" c` appends `WriteFile("x", c)` and returns.

The crucial split — READS RUN, MUTATIONS CAPTURE. A script reads to decide
what to write; if reads were deferred the plan could not compute the
writes. So during planning: `fs.read`/`env`/query-`net` execute (they
inform the plan); `fs.write`/delete/`proc`/mutating-`net` are captured.
That is exactly the ambient-input vs external-mutation partition floated
for the `deterministic` tier of [PLAN-pure] — the same line, double duty.

## Notation (indentation blocks, weir-shaped)

    let changes =
        plan
            File.write "out.json" rendered
            Dir.copy src dst

    changes |> preview     // render the ops, wrote nothing
            |> confirm     // interactive, or a CI gate
            |> apply       // now perform them

A `plan` block's result type can carry which effects it captured
(`Plan[fs.write]`) — the value-level echo of #1's signature.

## What it buys

- Dry-run as a uniform GUARANTEE, not per-tool code.
- Testing effectful code with NO mocks: `plan { thing } == expectedOps` —
  assert on the plan value, no filesystem stubbing. For an automation
  language this is the feature that makes ops code testable.
- Audit/preview: the plan is a renderable, diffable, storable change log —
  BOUNDED by function-types' refusals (shipped v0.0.23): a STORABLE plan
  cannot contain thunks. An op with a function payload (a deferred content
  thunk, KSL's `File(path, content: unit -> string)`) inherits "show
  cannot render functions" and every wire's function rejection — such a
  plan can apply, but not serialize or fully render. Either force thunks
  at capture time (evaluate `content ()` into a data op while planning) or
  accept preview-shows-the-op-name-only; the
  renderable/diffable/storable claim holds only for all-data payloads.

## Boundaries — where to STOP (feature, not a product)

- Imperative capture, NOT declarative convergence. weir's plan records
  "the ops this script would do"; it is NOT `desired - current` with
  providers and state refresh. Cross that line and you are building
  Terraform. Keep `plan` as reified imperative effects.
- `proc` is the black-box edge (⊤, per [PLAN-pure]): a spawned binary
  reads AND writes and weir cannot see past it, so `proc` in a plan can
  only be captured OPAQUELY (deferred whole) — and a later op depending on
  its output is uncomputable at plan time. v1: plan cleanly over
  weir-native mutations (`File`/`Dir`/`Http`); `proc` captures opaque or
  is refused in plan scope.
- Intra-plan write->read does not compose: write A, read A, write B from A
  — the deferred write means the read sees OLD A ("known after apply").
  KSL's `NoYamlPath(onRemoved)` is this exact node in real code. Disallow
  or clearly document; going graph-based is the IaC-engine line.
- Apply atomicity: partial failure mid-`apply` leans on `always`/`within`.

## The domain-intent tension (from KSL)

A generic effect-plan captures `WriteFile` — it does not know that is a
"kustomize resource add" or a "yaml merge at a path". KSL's
`KustomizeResource`/`MergeYzlAt` carry INTENT a raw `fs.write` log loses.
Synthesis worth chasing: DOMAIN OPS as user-defined types that LOWER to
primitive effects — intent at the top, generic plan/diff/test underneath.

## Receipt / trigger

Port KSL's `RenderTo.fileSystem` ([PLAN-ksl-migration]) as a weir union +
`Tree.walk` interpreter; then find yourself hand-writing a SECOND
interpreter (`RenderTo.plan`) and duplicating all ~18 op arms. That
duplication — the N-ops x M-interpreters matrix — is the trigger for
language-provided plan/apply.

## Open questions

1. `plan` a `within`-family block, or its own construct? (Rec: family —
   and it then inherits [PLAN-pure]'s prerequisite: the `withinKinds`
   kind-first restructure + union [D:host-strictness]; `plan` would be the
   SEVENTH kind entering that table.)
2. Does `Plan` carry its captured-effect set in the type? (Rec: yes, once
   [PLAN-pure] lands the labels.)
3. `proc` in a plan: opaque-capture or hard refuse? (Defer to the receipt.)
