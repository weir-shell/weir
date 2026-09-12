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
   Under [PLAN-yaml-nodes] the removal is a tombstone merge, which is
   TOTAL (no removed?-signal) — this site uses the interim
   render-and-compare spelling, and is the receipt for the parked
   `Yaml.mergeReport`.
2. YAML IN-PLACE EDITING — probe RESOLVED (2026-09-11): the blocker is
   REAL and now SIZED as [PLAN-yaml-nodes]. Typed RMW cannot carry it
   (`from yaml T` drops undeclared fields on write-back; the merge ops
   target arbitrary YAML), so the port needs the typeless node read —
   exactly [D:yaml-seq]'s park, un-parked by this stronger receipt. The
   designed surface: `Yaml.parse` + `Yaml.merge` + a `yaml patch [by=key]`
   district kind + the `$-` tombstone sigil — the patch's STRUCTURE is the
   address (strategic-merge model), no path language anywhere. KSL's
   comments-lost/CRLF-normalizing serialization means structural fidelity
   is the bar — weir's renderer normalizing is NOT a regression. Every
   KSL op maps (the table lives in [PLAN-yaml-nodes]).
3. `Kustomize.modify` (edit kustomization.yaml resources/generators/
   patches/components/images) — domain logic riding the same yaml-edit
   surface as (2).
4. `DirWithContext` (children computed from a path-context record) — pure
   path math, ports fine; leans on [PLAN-path-type] if paths become typed.

## Next steps

1. ~~PROBE (2)~~ DONE — resolved into [PLAN-yaml-nodes] (build it first;
   it is this port's last capability gap).
2. Port `RenderTo.fileSystem` (the fs-only ops: `File`/`Dir`/`NoPath`/
   `CreateEnv`/`MergeEnv`) via `Tree.walk` — proves recursion→walk and
   function-field content thunks. Unblocked NOW ([D:structural-walk]
   shipped); the yaml-backed ops join once [PLAN-yaml-nodes] lands.
3. Add a `RenderTo.plan` interpreter over the same union — feel the N×M
   duplication; that experience feeds [PLAN-plan-apply].

## Depends on

[PLAN-structural-walk] (DONE — [D:structural-walk]) · function-types
(DONE, v0.0.23 — the content-thunk fields) · [PLAN-yaml-nodes] (DESIGNED
— the yaml-backed ops) · [PLAN-plan-apply] (the second interpreter,
later) · optionally [PLAN-path-type] (`DirWithContext` path math).
