# weir — dbt migration exploration

Status: EXPLORATION (2026-09-11). Driver: rewriting parts of dbt (the
Arquidev project-selection tool) in weir. Not a build plan — a map of
what `from xml` unblocked, what is portable now, and what is still owed.

## What `from xml` (v0.0.30) unblocked

The audit's one hard "BLOCKED on XML" item — the dotnet selectors — is now
green. Both `System.Xml.XPath` sites port to `from xml`:

- `DotnetProject.hasProperty` (project.fsx) — XPath
  `/Project/PropertyGroup[*]/IsPublishable[text()='true']` becomes a typed
  read over `seq<{ IsPublishable: Option<string>; … }>` with an
  `== Some "true"` check. Dynamic property name collapses to the three
  known probes (publishable/packable/test) — no real loss.
- `makeDependencyTree.getProjReferenceDeps` (solution.fsx) —
  `//ProjectReference` + `@Include` is exactly the pinned csproj shape
  (`[<Attr>] Include`, `[<Elem "ProjectReference">]` under `[<Elem
  "ItemGroup">]`).

Bonus: `.slnx` is XML, so `from xml` reads it directly — dropping the
`Ionide.ProjInfo` dependency IF you standardize on `.slnx`.

## Portable now (given [PLAN-structural-walk])

- `solution.fsx` dependency traversal (`findLeafDependants` /
  `findAllLeafDependants`) — pure, cyclic, accumulate → `Graph.reach`
  (see [PLAN-structural-walk] for the one-expression form). The hand-rolled
  `visited: Set` and the two `let rec find` disappear.
- The selector predicates (`isPublishable`/`isPackable`/`isTest`) — read a
  csproj (`fs.read`), compare — a natural [PLAN-pure] "pure except reads"
  case; watch that frequency, it feeds the pure read-tier trigger.

## Still owed

- Classic `.sln` (the `Project("{GUID}") = … EndProject` TEXT grammar) is
  NOT XML — the lone format holdout. Either a text parser (regex/line) or
  keep Ionide for that one format. `.slnx` needs neither.
- The `selector { … }` builder CE (plan.builder.fsx — Yield/Zero/Combine/
  Run + CustomOperations) → records + copy-update, per the builder-CE-vs-
  records ruling. Largest single chunk; unrelated to XML.
- `Set`/`HashSet`/`IDictionary` → `Map` (string-keyed): `visited` →
  handled by `Graph.reach`; the dep dict → `Map<string, seq<string>>`.
- `types.fsx` type-cycle blocker from the audit — revisit against current
  weir (anon-nesting and recursive-field rules may have moved).

## Probes / next steps

1. Port `dotnet/project.fsx` (the two selector predicates) via `from xml`
   — smallest, fully unblocked, proves the shape end-to-end.
2. Port `dotnet/solution.fsx` dep graph via `Graph.reach`
   ([PLAN-structural-walk] prerequisite) — proves the recursion answer.
3. Reframe `plan.builder.fsx`'s `selector` CE as records — proves the
   builder-CE-to-records path on a real DSL.
4. Measure: how often selector logic wants "pure except it reads a file"
   — the signal for [PLAN-pure]'s deferred read-permitting tier.

## Depends on

`from xml` (DONE, v0.0.30) · [PLAN-structural-walk] (dep graph) ·
[PLAN-pure] (predicate purity, later). Constraint from the maintainer:
weir-side code/docs never name "dbt" — csproj/.slnx traversal is the
generic receipt.
