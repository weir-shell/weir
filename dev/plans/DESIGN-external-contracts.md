# weir — external contracts: one concept, three customers

Status: PROPOSAL for design bless (2026-07-31); BLESSED 2026-08-01 by
blessing PLAN-contracts-spine, whose session landed the spine and the
SCHEMA customer (e09d659). Updated post-landing (this revision): the
command family settled as `weir add <kind>` / `weir restore` /
`weir verify` — the landed `weir vendor [add]` spelling is the
pre-rename form, retired by PLAN-restore-rename (referenced below;
pending) — and the versioning-asymmetry section states customer
three's honest cost. At proposal time nothing here was implemented —
command signatures, remote imports, and YAML schemas were all
unbuilt, which is exactly why the unification was worth doing THEN:
three features designed separately would arrive as three mechanisms,
and the third would be a bolt-on. This file proposes the shared
concept and rewrites the three as its customers.

## The observation

Weir is about to grow its third "declare an external contract and
check against it" feature:

| customer | the contract | what it buys |
|---|---|---|
| **command signatures** | a tool's CLI surface | `bicep buidl --outfil` is a check error |
| **YAML/JSON schemas** | a document's shape | `apiVerison:` in a district is a check error |
| **remote modules** (parked) | weir code | shared code across projects |

All three are: **an artifact weir did not author, obtained from
outside, pinned, vendored, consulted at CHECK time, contributing
NOTHING at run time.** They want the same lifecycle — acquire,
vendor, pin, verify, consult — and if they get three lifecycles the
project ends up with three commands, three directory conventions,
three staleness stories, and three security postures.

## THE CONCEPT

**An external contract is a vendored, pinned artifact that constrains
what the checker accepts and contributes nothing at run time.**

Four properties, each load-bearing:

1. **Vendored** — it lives in the repo under `.weir/`, checked in,
   so a clone checks identically offline. Never fetched during
   `check`.
2. **Pinned** — its identity is recorded exactly (a version string, a
   content hash, a SHA), never a range. The no-ranges prohibition
   from the signatures design generalizes here verbatim: these are
   pairwise comparisons, not a dependency graph, so there is nothing
   for a solver to solve.
3. **Check-time only** — deleting every contract leaves every script
   running identically, just checked less. **This is the property
   that makes them safe and it must be pinned**: a script with and
   without its contracts produces byte-identical output.
4. **Declared, not discovered** — a file states which contracts
   govern it. A `.weir/` directory's mere existence never changes how
   a file checks.

## The shared machinery (build ONCE, three customers)

**`.weir/` is the home**, discovered by walking up from the declaring
file to the first `.weir/`, bounded by `.git` (the signatures
design's rule, generalized). Layout:

    .weir/
        sigs/<tool>.weir          -- command signatures
        schemas/<name>.json       -- document schemas
        modules/<ns>/<name>/…     -- remote modules (v2)
        lock.json                 -- what was fetched, and its hash

**Acquisition and restoration, settled** (see
`PLAN-restore-rename.md` for the full reasoning):

- **`weir add <kind> …`** — acquire ONE new contract and record it.
  Kind-aware, because acquiring genuinely differs per kind:
  `add schema <url> --as <name>` fetches; `add sig <tool>` GENERATES
  from the installed binary; `add module <repo> --ref <sha>` clones
  at a ref. Different acts producing the same shape of lock entry.
- **`weir restore`** — re-materialize everything the lock records.
  **Kind-agnostic by construction**: every entry is source + hash +
  path, so restoring needs to know nothing about the artifact.
- **Never implicit**, never during `check`, never during completion.

**The lockfile IS the manifest** — a deliberate choice, not an
omission. Ecosystems carry both because a manifest holds RANGES a
resolver turns into pins; contracts have no ranges, so the lock states
everything exactly.

**One verify step**: `weir verify` checks that vendored contracts
still match reality — a signature against its installed tool's
`--version`, a fetched artifact against its lock hash. CI runs it.
**The rule this instantiates for the third time**: *a check that
needs the environment is a verification and deserves its own
command* (`weir fetch` outside check; the freshness gate outside the
compiler; now this).

**One lockfile format** recording, per contract: kind, identity
(version string / URL + SHA), content hash, and where it came from.

**One staleness story**: `verify` fails; the LSP publishes the same
finding as a background diagnostic (the signatures design's
async-diagnostic move, now shared).

**One security posture, stated once as a non-claim**: contracts
constrain what weir ACCEPTS; they do not constrain what runs. A
hostile `bicep` on PATH still runs; a lying schema cannot execute
code but can grant **wrong confidence**, which for a
check-before-effects language is the specific poison. Hence: pinned,
hashed, vendored, reviewable as source.

## The declaration surface — ONE directive family

The signatures design settled `#sig bicep` (declared per tool,
default resolution with an override, file-local, non-inherited).
Generalize the shape, not the keyword:

    #sig bicep                     -- .weir/sigs/bicep.weir
    #sig bicep "../vendor/b.weir"  -- override
                                   -- (modules keep `import`; see below)

    yaml schema=k8s-deployment     -- schemas attach PER DISTRICT,
        apiVersion: apps/v1        --   not per file (see Schemas)

- **Same resolution rule** for every kind: walk up to the first
  `.weir/`, stop there (a `.weir/` lacking the artifact is the error,
  no continuing upslope), do not cross `.git`, override with a path.
- **Same errors**: missing artifact names the resolved path AND the
  `.weir/` searched from.
- **Same non-inheritance**: a module's contracts do not reach its
  importers.
- **Same inertness**: declaring a contract you never use is legal.

**Why separate keywords rather than one `#contract`**: the kinds bind
to different things (a tool NAME, a schema alias, a module PATH) and
the error messages differ entirely. One keyword would need a kind
discriminator anyway; separate keywords in one family, sharing
resolution, is the honest factoring.

**Remote modules stay `import`**, and that is deliberate: a module
contributes CODE the program uses, which fails property 3. It shares
the vendoring, pinning, lockfile, and fetch machinery — but it is not
a contract, and conflating them would let a "contract" execute. **The
line to state: contracts are inert; modules are code.**

## How each customer specializes

**Command signatures** — as designed (`DESIGN-command-signatures.md`
becomes this design's first customer rather than a standalone).
Specialization: identity is the tool's whole `--version` output;
`verify` spawns the tool; generation is `weir sig generate`.

**Schemas** — the new one. Specialization:
- **Source**: JSON Schema, which for k8s/Actions/CI formats ALREADY
  EXISTS, versioned and hosted (SchemaStore, k8s OpenAPI). **The
  corpus problem that makes signatures hard is already solved here** —
  the strongest argument for doing schemas early.
- **The subset must be as explicit as YAML's.** JSON Schema is big:
  `$ref`, `oneOf`/`anyOf`/`allOf`, `if/then`, `patternProperties`,
  `additionalProperties`, `$defs`, recursion. K8s leans on `$ref`
  heavily, so `$ref` and `$defs` are NOT optional; the composition
  keywords probably are. **Name the supported subset before
  implementing**, and reject the rest with located teaching errors —
  the owned-parser dividend again. [Landed: the corpus measurement
  inverted the `$ref` guess — standalone-strict variants inline
  everything, so `$ref` rejects with a teaching that names them; see
  PLAN-contracts-spine's rulings.]
- **What is checkable, honestly**: a district's literal keys and
  structure are static, so unknown fields, missing required fields,
  and misplaced nesting are check-time catchable — and those are the
  errors people actually hit (`apiVerison`, `spec.template.spec`
  misplacement). **Spliced values are known only by TYPE**, so
  `replicas: $n` checks int-vs-integer but a pattern or enum
  constraint on a `string` splice does not — unless the splice's type
  is a weir enum, where it might. `for`-generated content multiplies
  the uncertainty. **Scope: structural validation always, value
  validation where types permit** — stated, not discovered.
- **Where it attaches — RESOLVED: the district names its own schema,
  the author names it, weir never infers it.**

      yaml schema=k8s-deployment
          apiVersion: apps/v1
          kind: Deployment
          ...

  (Exact marker spelling is a session decision — `schema=` on the
  marker line, or a directive-per-district; the RULE is that the
  declaration is per district and written by the author.)

  **Why not read a discriminator from the document**: the clever
  option was to read `kind` and pick the sub-schema. `kind` is a
  KUBERNETES convention, not a YAML or JSON Schema one — GitHub
  Actions has no discriminator (schema is per file location),
  docker-compose is whole-file, most CI formats are just "this file
  is that shape". Inferring would mean weir knowing about one
  ecosystem, which the subset discipline refuses, and it would break
  declared-not-discovered besides. [Landed, and the dumb option costs
  NOTHING: the schema itself constrains `kind` (k8s publishes it as a
  one-element enum), so a pasted Service under `schema=configmap` is
  caught as an identity mismatch — the convention stays in the
  ecosystem's data where it belongs.]

  **Per district rather than per file** because that is what makes
  the multi-shape case work without cleverness: a file with a
  Deployment district and a Service district names each. It also
  keeps the declaration next to the thing it governs.

  **The k8s note, since it is the motivating case and it is hostile
  to this**: published k8s schemas are per-kind-per-version files
  (`deployment-apps-v1.json`), so a k8s project vendors several and
  names them per district. Slightly tedious, entirely predictable,
  and the alternative is weir learning one ecosystem's conventions.

  A district with NO schema declaration is unvalidated — exactly as
  today. Schemas are additive, per property 3.
- **`from yaml T` interaction**: a schema and a weir record are two
  descriptions of the same shape. Do they compose, conflict, or
  ignore each other? Probably ignore in v1 (the record IS the
  contract for the adapter path; the schema is for the district
  path), but say so.

**Remote modules** — the parked arc, now inheriting `.weir/modules/`,
the lockfile, `weir restore`, and `weir verify` instead of inventing
them. Its own prerequisites (pinning, integrity, never-implicit) are
already this design's properties 1 and 2.

**The versioning asymmetry, and the honest cost of customer three.**
Sigs and schemas have **no transitive dependencies**: a schema is one
file, a signature describes one tool, and both are compared pairwise
against an installed reality. Nothing composes, so there is genuinely
nothing for a range to negotiate — the prohibition is absolute there
and always will be.

**Modules form a graph, and remote modules REQUIRE a manifest.** Not
conditionally, not once a diamond fires: the moment modules come from
a registry, module authors declare their own dependencies (a shared
`Git` module importing a shared `Paths` module is the ordinary case),
so the graph exists on day one. Exact-SHA-only would force the
consuming project to hand-pin every transitive module it never heard
of — unusable beyond depth one.

So remote modules bring, unavoidably:

- **a per-module manifest**, published WITH the module, declaring its
  own requirements — format, location, and versioning all new;
- **version identity beyond a SHA** (a tag, or semver), because a
  requirement has to reference something a resolver can order;
- **a selection rule** producing the lock. **MVS (Go's minimum
  version selection) is the target**, and the reason is that it is a
  selection rule over EXACT versions, not a constraint language — so
  it satisfies the no-ranges prohibition rather than overturning it.
  No SAT, deterministic, and where the industry converged after
  ranges proved miserable.
- The lock stays exactly as it is: the resolved, hashed, pinned
  OUTPUT. **Manifest and lock cannot collapse into one** — the
  manifest is distributed across modules you do not own and is INPUT
  to the algorithm; the lock is local and is its output. (Go needs
  both `go.mod` and `go.sum` for precisely this reason.)

**Stated plainly, because a design that undersells its third
customer's cost gets found out mid-arc**: remote modules share
vendoring, hashing, and the lock's storage with the contracts spine —
but they add a resolver, a manifest format, and a version-identity
scheme that sigs and schemas will never need. The unification is real
for customers one and two and THINNER for customer three. Local
modules (the landed arc) need none of this; it is remote distribution
that brings the graph.

## What this buys, concretely

- **One `.weir/`, one lockfile, one fetch, one verify** instead of
  three of each.
- **The third feature is cheap.** Schemas become "a new artifact kind
  plus a validator", not a new subsystem — which is the whole reason
  to do this before any of them ship.
- **One security story to state and defend**, not three.
- **The user learns one model.** Add → restore → verify, for
  everything external.

## Open decisions (for the bless)

1. **`.weir/` layout** as proposed, or flat with kind-tagged
   filenames.
2. **Ordering**: signatures first (proven model, no fetch needed —
   generation is local), then schemas (needs fetch, but the corpus
   exists), then remote modules (needs the most trust machinery).
   **Proposed: build the shared spine WITH signatures**, so it is
   exercised by a real customer rather than designed in the
   abstract — then schemas prove it generalizes, then modules.

*(Resolved since drafting: schema attachment — per district,
author-named, no discriminator inference (see Schemas above); the
command family — `weir add <kind>` / `restore` / `verify` (see the
shared machinery above and `PLAN-restore-rename.md`); the ordering —
schemas first, since they exercise the whole spine; the layout — kind
directories, landed with the spine.)*

## The type-provider question, answered once

F# hands will ask this in the first minute, so the answer belongs in
the design and in LEXICON: **contracts resemble type providers only at
the "compile-time knowledge from an external artifact" level, and
nothing below it.**

- **Providers GENERATE types; contracts CONSTRAIN uses.**
  `JsonProvider` gives you `row.Name`; a signature gives you nothing
  to name — it only makes a bad invocation an error. That is why
  property 3 holds and contracts are purely additive; a provider you
  delete breaks compilation everywhere.
- **Providers EXECUTE arbitrary code at compile time** — a provider
  is an assembly the compiler loads and runs, so opening a file in an
  editor can hit a database or the network. Contracts are inert data
  read by weir's own code. That difference is the entire reason the
  security story is statable.
- **Providers have no vendoring or pinning story** —
  `JsonProvider<"http://…">` fetches from whoever answers, every
  build. The vendoring, lockfile, hash, and explicit fetch step are
  weir's answers to what providers left open.

One honest concession: for the SCHEMA customer a provider is the
closer analogue and does more (typed access to the document, not just
validation). Weir's answer to typed access is `from yaml T` with a
declared record — the same information supplied by the user rather
than derived from a sample.

**The one-liner**: *contracts are type providers minus the execution
and plus the pinning.*

## Bars for the whole family

- **Property 3 is THE pin**: with and without contracts,
  byte-identical runtime output. Every customer re-runs it.
- `weir check` never fetches, never spawns, never touches the
  network. Pin the negatives.
- Every contract kind states its SUBSET (signatures: which CLI
  grammar features; schemas: which JSON Schema keywords) and rejects
  the rest with located teaching errors.
- No range syntax anywhere, ever, for any kind.
