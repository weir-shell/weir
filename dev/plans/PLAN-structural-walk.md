# weir — structural walk combinators: bounded recursion without `let rec`

Status: PROPOSED (2026-09-11). The answer to two `let rec` receipts (dbt
dependency-graph traversal; KSL `RenderTo.fileSystem` tree walk) that keeps
weir's bounded-iteration identity.

## The finding that shapes this

`rec` is already a reserved keyword; the ONLY thing blocking recursion
today is one checker choice — the binding name is kept out of its own RHS
(`let f x = … f …` → "unbound variable 'f'"). Flipping that is
mechanically small. But it reverses weir's stated identity — bounded
iteration only (`retry`/`poll` bounded, `Seq.fold`/`iter` input-bounded,
no unbounded control flow) — and drags four risks: nontermination, AOT
host-stack overflow (tree-walk interpreter, no TCO), recursive-binding
inference with NO annotation escape hatch, and cycles in the call graph
the `pure`/`--can` analysis walks.

The receipts do NOT need any of that: BOTH are STRUCTURAL recursion over
FINITE data that shrinks (a finite `m` tree; a finite project graph) —
terminating by construction. So this is an ERGONOMICS gap in the
fixpoint-fold, not an expressiveness gap. Answer it with bounded
combinators, keep `rec` reserved for a genuinely-unbounded receipt that
has not appeared.

## Design — one engine, two faces

The engine is the frontier/visited fold, hand-written today, lifted to a
builtin that owns cycle-safety and a fuel bound:

    Frontier.fold : (n -> string)          // keyOf — dedup key ("" disables, for trees)
                 -> a                        // seed accumulator
                 -> (a -> n -> a * seq<n>)   // step: fold acc, emit children (may be effectful)
                 -> seq<n>                    // initial frontier
                 -> a

Fuel overflow is a LOCATED teaching error ("exceeded N steps — is the
graph finite, or is keyOf too coarse to dedup?"), so nontermination
becomes a bounded, teachable failure, never a hang. Two ergonomic faces:

    Graph.reach : (n -> string) -> (n -> seq<n>) -> n -> seq<n>   // pure, cycle-safe -> reachable nodes
    Tree.walk   : n -> (n -> seq<n>) -> unit                       // effectful; step does its effect + returns children

They differ on exactly the axes the receipts differ on — pure-accumulate
+ cyclic (fold-shaped) vs effectful + tree (iter-shaped) — the same split
as `Seq.fold` vs `Seq.iter`.

## Receipts

dbt (pure, cyclic) — both findLeafDependants and findAllLeafDependants,
the hand-rolled `visited: Set` gone:

    let leaves start =
        start
        |> Graph.reach id (fun p -> if isLeaf p then [] else Map.tryGet p deps |> Option.defaultValue [])
        |> Seq.filter isLeaf
        |> Seq.distinct

KSL (effectful, tree, context-threaded) — `n = (m, pwd)`, `step` does each
node's effect and returns children; a faithful port of `let rec exec`:

    let step (m, pwd) =
        match m with
        | Many ms        -> ms |> Seq.map (fun c -> (c, pwd))
        | Dir (p, ms)    -> let full = joinPwd pwd p in Dir.ensure full; ms |> Seq.map (fun c -> (c, full))
        | File (p, cnt)  -> let full = joinPwd pwd p in (if not (File.exists full) then File.write full (cnt ())); []
        | NoYamlPath (fp, jp, onRemoved) ->
            if Yaml.removeNode (joinPwd pwd fp) jp then [ (onRemoved (), pwd) ] else []   // known-after-apply
        // …other cases: effect, return []
    Tree.walk (root, targetDir) step

KSL's `NoYamlPath` (children known only after a mutation) is why KSL needs
the EFFECTFUL face — expansion cannot be separated from execution there.
dbt has no such node, so it gets the pure face. That is the principled
reason one combinator does not cover both.

## Footprint

- New builtins: `Frontier.fold` + the `Graph`/`Tree` faces (ordinary
  higher-order builtins, like `Seq.fold` — function args, no special
  form). NO parser change, NO checker change, NO grammar/manifest ripple.
- Effect analysis (see PLAN-pure): `Graph.reach` is pure iff its neighbor
  fn is; `Tree.walk` carries its step's effects. They compose with `pure`
  for free — no call-graph cycle risk, because the loop is in the builtin,
  not in weir's own call graph.
- Termination: fuel cap (overridable) + the visited set; bounded by
  construction, so weir's no-unbounded-control-flow identity is intact.

## Open questions

1. Expose the general `Frontier.fold`, or only the two faces? (Rec: ship
   both faces; keep `Frontier.fold` as the documented power tool.)
2. `keyOf = ""` as the "no dedup / this is a tree" convention, or a
   separate `Tree.walk` with no keyOf at all? (Rec: separate face, no
   keyOf — clearer.)
3. Fuel: a default cap with an optional override arg, or always explicit?
   (Rec: default + override, `retry`/`poll` precedent.)
4. Keep `rec` reserved (do NOT add `let rec`) until an honestly-unbounded,
   non-structural receipt appears. (Rec: yes.)

## Relation to other plans

Prerequisite for [PLAN-dbt-migration] (the dep graph) and
[PLAN-ksl-migration] (the render tree). Independent of [PLAN-pure], but
composes with it (pure/effectful walks).

Docs cross-link: this ruling answers COMING-FROM's F# `while`/`let rec`
row, which now states neither exists and quotes the actual teaching
(`'rec' is a keyword`) rather than implying `let rec` is a thing you
translate away. (The rendered page deliberately does NOT cite this file —
dev/plans names client projects and rendered surfaces must not point
there; the client-name gate in ci/e2e.sh enforces the surface split.)
