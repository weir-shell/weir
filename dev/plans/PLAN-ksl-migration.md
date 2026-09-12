# weir — KSL migration exploration

Status: EXPLORATION (2026-09-11). Driver: porting KSL (the Arquidev
Kustomize/k8s scripting layer) to weir. KSL is also the concrete RECEIPT
for [PLAN-plan-apply] — its `RenderTo` is the plan/apply pattern
hand-rolled.

## What KSL already is

`m` is a union tree of intended filesystem/kustomize ops (`Many`, `File`,
`Dir`, `DirWithContext`, `NoPath`, `NoYamlPath`, `KustomizeResource`,
`MergeYzl`, `CreateEnv`, `MergeEnv`, `MergeYzlAt`, …); `RenderTo.fileSystem`
is a `let rec exec m pwd` interpreter that EXECUTES it against disk. That
is a reified Plan + an `apply` interpreter, hand-built and domain-specific.
Effect set is basically `{ fs.read, fs.write, console }` — no `net`, no
`proc` — the CLEANEST possible target for a weir plan/apply.

## Portable now (given [PLAN-structural-walk])

- `let rec exec m pwd` → `Tree.walk (root, targetDir) step` (effectful
  face), node = `(m, pwd)`, `step` does each node's effect and returns
  children (see [PLAN-structural-walk] for the concrete `step`). No
  recursion, bounded by construction.
- `File(path, content)` where `content : unit -> string` → a weir
  FUNCTION-FIELD (deferred, type-checked at check-time, run at apply).
  function-types is DONE (v0.0.23: the arrow production + refuse-and-call),
  so this field DECLARES today. Note the F2 consequence so it does not
  surface as a surprise: a function field is show/wire-refused ("show
  cannot render functions"; every wire rejects it) — fine for apply, a
  content thunk never serialises, but it is exactly what bounds a
  STORABLE plan later ([PLAN-plan-apply]).
- `MergeEnv`'s `Dictionary<string,string>` → `Map<string,string>`; the
  `k=v` parse → `Str.splitOnce`.
- `printfn "File --> …"` is a `console`/`log` effect ([PLAN-pure] label).

## The plan/apply opportunity (this is the receipt)

KSL has ONE interpreter (`apply`). Add `RenderTo.plan` / `.diff` over the
same `m` and you have plan/apply. But every op is a union case PLUS a match
arm in EVERY interpreter — the N-ops x M-interpreters matrix. Hand-writing
the second interpreter and duplicating ~18 arms is the trigger for
language-provided plan/apply ([PLAN-plan-apply]). The domain-intent of
`KustomizeResource`/`MergeYzlAt` is the argument for the "domain ops that
lower to primitive effects" synthesis there.

## Hard nodes / probes

1. `NoYamlPath(fp, jp, onRemoved)` — children known only AFTER the removal
   (a mutation-dependent branch). This is the "known after apply" node; it
   works only with the EFFECTFUL `Tree.walk` step (removal in `step`, then
   decide children). A pure plan interpreter cannot expand it without doing
   the mutation — the exact plan/apply limitation, in real code.
2. YAML IN-PLACE EDITING is the likely BLOCKER. KSL leans on
   `Yaml.editInPlace`, `Yaml.editInPlaceAtPath`, `Yaml.removeNode(s)`,
   `Yaml.merge` — STRUCTURAL edits of an existing YAML file at a JSON path.
   weir's `from yaml T` is a READ boundary into records and `to yaml`
   writes fresh; in-place node edit / merge-at-path is NOT a current
   capability. PROBE FIRST: can KSL's yaml edits be expressed as
   read-modify-write through records, or is a `Yaml`-node edit surface a
   prerequisite? Size this before committing to the port.
3. `Kustomize.modify` (edit kustomization.yaml resources/generators/
   patches/components/images) — domain logic riding the same yaml-edit
   surface as (2).
4. `DirWithContext` (children computed from a path-context record) — pure
   path math, ports fine; leans on [PLAN-path-type] if paths become typed.

## Next steps

1. PROBE (2): map every KSL yaml mutation to a weir capability; decide if a
   `Yaml`-node edit surface is owed. This gates the whole port.
2. Port `RenderTo.fileSystem` (the fs-only ops: `File`/`Dir`/`NoPath`/
   `CreateEnv`/`MergeEnv`) via `Tree.walk` — proves recursion→walk and
   function-field content thunks.
3. Add a `RenderTo.plan` interpreter over the same union — feel the N×M
   duplication; that experience feeds [PLAN-plan-apply].

## Depends on

[PLAN-structural-walk] (the walk) · function-types (DONE, v0.0.23 — the
content-thunk fields) · a YAML-edit capability probe (blocker) ·
[PLAN-plan-apply] (the second interpreter, later) · optionally
[PLAN-path-type] (`DirWithContext` path math).
