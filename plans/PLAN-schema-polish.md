# weir — rider: schema-validation polish

Status: EXECUTED (2026-08-01; blessed same day). Written as a
pre-merge rider; the branch merged first, so it landed as
`schema-polish` off main instead — same content, one commit later.

## What landed, per work item

1. **Every message names its field, paths ALWAYS** (items 1+2 merged:
   threading the path gives the field name free). Root renders
   without a suffix, so the shallow acceptance messages kept their
   exact text. The six shapes re-pinned VERBATIM in e2e:
   unknown+did-you-mean (root), missing-required, literal type with
   full path (`field spec.ports.port expects integer, got string
   ('nope')`), splice type with path, structure mismatch with path,
   and the enum.
2. **The enum `…` was the suspected bug**: `List.truncate 4` plus an
   UNCONDITIONAL ellipsis — a one-element enum (k8s `kind` is
   exactly that; corpus: const 0, enum 14, single-valued) rendered
   `(ConfigMap…)`. Now: one value states plainly (`expects
   'Service', got 'Deployment'`); ≤6 list all; >6 show six with an
   honest `(+n more)`.
3. **The strict-variant dependency, stated and guarded**: mechanism
   CONFIRMED — unknown-field checking is exactly
   `additionalProperties: false` → `Closed`; plain `-standalone`
   lacks it and the check is silently inert. `weir add schema` now
   WARNS when a fetched schema contains no
   `additionalProperties: false` anywhere, naming
   `-standalone-strict` (e2e-pinned); GUIDE and SEMANTICS name the
   variant with the reason.
4. **The `/null` noise: KEPT, ruled** — accurate (k8s marks most
   fields nullable), and hiding it would make a legitimate
   `replicas: null` unexplainable. It is the COMMON case in k8s;
   if it reads badly in practice the fix is presentational, not
   correctness. Not to be re-litigated.
5. **Honest scoping beside the success**: GUIDE's district passage
   now states the boundary next to the schema mention (spliced int
   checks; string-vs-pattern/enum does not; for-content unchecked).
6. **1b recorded**: the enum catch means IDENTITY mismatches are
   caught (pasted Service under `schema=configmap`), which proves
   the rejected-inference choice costs nothing — the schema itself
   constrains `kind`. Sentence added to the design's paragraph.
7. **Grammars + REPL**: already handled in the spine session
   (tree-sitter marker token extends through `schema=<name>`,
   TextMate begin widened, both engine-verified there; micro
   exempt). The REPL classifier handles the suffix and the tint
   covers the whole marker — now PINNED in repl-color.py (the
   schema-bearing marker tints whole).
8. **Completion, ruled MARKER-LOCAL**: `schema` is deliberately NOT
   a `Parser.keywords` member (a keyword would reserve the
   identifier `schema`). The district marker context offers
   `schema=` after `yaml `, and the vendored schema NAMES from
   `.weir/schemas/` after `schema=`; adapters (`to yaml `) offer
   nothing. The shared marker predicate moved to Parser
   (Script aliases it — one classifier, three consumers). Unit-pinned
   including the not-a-keyword negative.

## Findings — filed with sizes

**F1 — path-aware did-you-mean** (`unknown field 'containers' — did
you mean spec.template.spec.containers?`). A schema-TREE name
search, not spelling distance: walk the loaded schema for property
names matching the unknown key exactly (then fuzzily), report the
nearest path(s). SIZE: small-medium — one recursive index over the
parsed Schema (name → paths, built once per load), a lookup in the
unknown-field arm, a cap for ambiguity (a name at 14 paths teaches
nothing); the interesting decision is ranking when both a spelling
match and a placement match exist. ~60–100 lines + pins. Genuinely
useful for the most common k8s mistake; not built here.

**F2 — the `$ref` divergence, corrected + the `$defs` distinction
stated.** The plan's charter text said `$ref`/`$defs` are NOT
optional; what shipped REJECTS both, because the corpus measurement
inverted the guess (standalone-strict inlines everything; zero refs
in six real schemas). The plan's rulings already record this; the
DESIGN's schemas section carries the [Landed] bracket. THE
DISTINCTION NOT MEASURED, now stated: the corpus contained zero
refs of ANY kind, so self-contained `#/$defs/...` internal refs
were never separately measured. Supporting them is much cheaper
than remote refs (a two-pass parse: collect `$defs`, resolve
internal pointers at load — no fetching, no recursion beyond a
visited-set) and would widen the usable corpus to most SchemaStore
entries, which are generally self-contained. SIZE: medium —
~80–120 lines in the subset parser + cycle guard + pins; the
teaching error for REMOTE refs stays. Filed for a corpus-driven
session (measure SchemaStore first, the discipline holds).

## Bars held

Message text changed only where named; the moved pins are the six
e2e re-pins (five gained field/path text, the enum reworded) and
nothing else; behaviour changes are exactly the add-time warning
and the always-path.
