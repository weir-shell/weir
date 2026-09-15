# weir — plan/apply: effects reified into an inspectable Plan

Status: BLESSED build spec (2026-09-14, designer — opened by call; all
design forks ruled below). Prerequisite: [PLAN-pure-stage2] (the
ambient/mutation partition this consumes). Receipt: the modernized
KSL port ([PLAN-ksl-migration]) — its `Tree.walk` fs interpreter is
the ready-made acceptance demo; hand-writing a second (dry-run)
interpreter is the duplication this feature removes.

## The idea

A `plan` block runs a computation but CAPTURES its external mutations
as data instead of performing them — yielding an inspectable,
diffable, testable `Plan` value you show, confirm, then `apply`.
Terraform's plan/apply loop as a language primitive, not a per-tool
reimplementation.

Scope honesty, up front: a FULL guarantee only for weir-NATIVE
mutation (`File`/`Dir`/`Http`). weir cannot see past a spawned binary,
so `proc` is REFUSED in plan scope in v1 (below) — this is dry-run for
file/config orchestration and generation, not "dry-run for any
script". Do not conflate it with the homepage's three-distance check
(narrower, already shipped).

## The rulings (2026-09-14)

1. SPELLING: bare `plan` + `apply`, NOT `within plan`. `plan` enters
   the `withinKinds` dispatch table as a Standalone kind (the `pure`
   precedent — a scope that changes WHETHER effects execute, not a
   scoped resource), spelled bare. Reserving `plan` costs ~nothing: in
   a language that HAS plan/apply, `let plan = …` is a confusing
   shadow anyway (the `let match =` class), unlike the common data
   noun `delay`. `apply`/`preview` stay ordinary members, no
   reservation. The pair is universal ops vocabulary (Terraform,
   Pulumi, kubectl) — recognizability is the feature's marketing; a
   synonym was rejected.
2. `Plan` IS an inspectable `seq<Op>` over a user-visible union `Op`,
   EQUATABLE and SHOWABLE — this is what makes mock-free testing exist
   (`plan <block> == [WriteFile("x", c)]`) and diff/preview fall out
   for free. An opaque Plan would kill the testing story.
3. READS RUN, MUTATIONS CAPTURE — [PLAN-pure-stage2]'s partition,
   double duty. During planning: ambient-input (`fs.read`, `env`,
   `clock`, query-`net`) EXECUTES (it informs the plan); external-
   mutation (`fs.write`, `fs.delete`, mutating-`net`) is CAPTURED as
   an Op. A script reads to decide what to write; deferring reads
   would make the plan uncomputable.
4. `proc` HARD-REFUSES in plan scope (check error, teaching): a
   spawned binary reads AND writes opaquely, uncapturable — plan over
   weir-native mutation only. (The parked alternative, opaque whole-
   capture, reopens on a receipt.)
5. KNOWN-AFTER-APPLY IS REFUSED: an intra-plan read of state an
   earlier captured mutation targeted (write A, then read A) is a
   located error — the read would see stale state, and resolving it is
   the graph-based IaC line weir does not cross. KSL's
   `NoYamlPath(onRemoved)` is this node; it must be restructured, not
   accommodated.
6. THUNKS FORCE AT PLAN TIME UNDER THE PLAN'S OWN PARTITION. A content
   thunk (KSL's `File(path, content: unit -> string)`) is evaluated
   while planning: pure or read-only content (the `deterministic`
   tier) forces into a DATA op; a thunk that would MUTATE refuses
   (mutation in content position is incoherent; `proc` already
   refused). Two consequences, both strictly better than deferring:
   - EVERY PLAN IS ALL-DATA by construction — no thunk survives into
     the value, so a Plan is ALWAYS renderable/diffable/storable. The
     old "only for all-data payloads" caveat DISSOLVES.
   - preview == apply, GUARANTEED. Content is snapshotted at plan time
     (its reads run then), so apply does exactly what preview showed —
     a file changing between preview and apply cannot make apply
     diverge. The Terraform-faithful property, and the same snapshot
     honesty as ruling 5.

## Why it belongs to weir

The value-level DUAL of [PLAN-pure]: purity asks statically "does this
reach `fs.write`?"; plan/apply reifies that same `fs.write` as a
pending Op. The effect labels ARE the Op constructors
(`fs.write → WriteFile`, mutating-`net → HttpSend`). weir already
sells "wrote nothing" (the homepage check); this makes the discipline
a primitive.

## Notation

    let changes =
        plan
            File.write "out.json" rendered
            Dir.copy src dst

    changes |> Plan.preview |> Seq.iter print   // render; wrote nothing
    if changes |> Plan.ops |> Seq.isEmpty then …  // inspect / test
    changes |> Plan.apply                        // now perform them

`plan` yields a `Plan`; it is an ordinary value — bind it, pass it,
store it, apply later. `apply` INSIDE a `plan` refuses (a mutation
cannot be coherently captured). Nested `plan` composes (each yields
its own value).

## The Op union (v1)

    Op =
        | WriteFile of string * seq<string>
        | DeleteFile of string
        | Copy of string * string        // File.copy / Dir.copy
        | Move of string * string
        | MakeDir of string
        | DeleteDir of string            // Dir.delete / deleteAll
        | HttpSend of HttpRequest        // mutating methods only

Equatable + showable (all-data by ruling 6). The exact arm set tracks
the `File`/`Dir` mutation surface + mutating-`Http`; a builtin that
mutates and has no arm is a build failure (the kind-first discipline).

## Surface

- `plan <block> : Plan` — the capturing scope.
- `Plan.ops : Plan -> seq<Op>` — the raw ops (test/inspect).
- `Plan.preview : Plan -> seq<string>` — human render.
- `Plan.apply : Plan -> unit` — perform, in order.
- `Plan.isEmpty`, `Plan == Plan` (the union is equatable) — testing.
- `confirm` is USER code (or a small helper), not built-in: a plan is
  a value, confirmation is an ordinary branch.

## apply — the non-claims (stated, not hidden)

Sequential, in capture order; STOPS at the first failing Op (prior ops
stay done); NO rollback — rollback is the IaC line. Cleanup leans on
`always`/`within`. apply is NOT transactional; the doc says so.

## Boundaries — where to STOP

- Imperative capture, NOT declarative convergence. weir records "the
  ops this script would do" — NOT `desired − current` with providers
  and state refresh. Cross that and you are building Terraform.
- DOMAIN-INTENT ops (KSL's typed `KustomizeResource` lowering to
  primitives — intent at the top, generic plan/diff underneath) are
  OUT of v1. v1 captures PRIMITIVE effects only. Parked; trigger: a
  real second domain wanting typed ops over the same Plan machinery.
- `proc`, known-after-apply, non-transactional apply — ruled above.

## Probes (run FIRST)

1. The partition ([PLAN-pure-stage2]) is consultable at EVAL time, not
   just check time — plan/apply intercepts at eval, so confirm the
   class of a builtin call is resolvable where the interpreter runs
   (esp. `Http.send`'s per-method class from the request value).
2. The KSL receipt, made concrete: take the modernized port's
   `Tree.walk` fs render and run it inside `plan` — every `File.write`
   / `Dir` op must capture, reads must run, and the resulting Plan
   must `apply` to a byte-identical tree vs the direct run. This IS
   the acceptance demo (fs-only, the full-guarantee case).
3. Known-after-apply detection: build the write-A-then-read-A repro;
   confirm it is a LOCATED refusal, not a silent stale read.
4. Thunk forcing: a pure content thunk and a read-only one force to
   data ops; a mutating one refuses — all at plan time; the Plan has
   no surviving thunks.
5. `apply` inside `plan`, and `proc` inside `plan` — both refuse with
   their teachings.
6. Fuzz: does the generator reach `plan` blocks? Extend it to emit
   plan-captured mutations, or the property never runs.

## Footprint

- Ast.fs: `plan` a Standalone `withinKinds` kind; `Op`/`Plan` types.
- Parser.fs: the `plan` head (keyword — grammar ritual + temp
  `grammar-currency` red).
- Eval.fs: a PLANNING MODE flag on the interpreter — inside `plan`,
  mutation builtins append an Op instead of performing; ambient-input
  builtins run; `proc` and known-after-apply refuse; thunks force
  under the partition. `Plan.apply` replays the ops through the normal
  (non-planning) builtins.
- Check.fs: `plan` yields `Plan`; `apply`-in-plan and `proc`-in-plan
  refusals; the Op union admission.
- Builtins.fs: `Plan.ops`/`preview`/`apply`/`isEmpty`; the Op arms per
  mutation builtin.
- Tests: the Op union round-trips (capture == expected ops); apply
  performs; equality/show; every ruling's refusal; the KSL-shaped
  fs demo as an e2e cell.
- SKILL/GUIDE: a plan/apply section (dry-run as a primitive, the
  testability pitch, the boundaries); CHANGELOG; DECISIONS
  `[D:plan-apply]`; `[D:]` at sites.
- Gates: build ×4, units, publish, skill-doc, e2e (the KSL-shaped
  demo + refusals), grammar ritual, fuzz 3×10k.

## Relation

Prerequisite [PLAN-pure-stage2] (the partition). Dual of [PLAN-pure].
Consumes [PLAN-structural-walk] (`Tree.walk` — the receipt's shape).
Composes with [PLAN-yaml-nodes] (a `Yaml.merge` write captures as
`WriteFile` of the rendered doc). The KSL port is the demo; its typed-
ops synthesis is the parked v2 trigger.
