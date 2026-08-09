# weir — external contracts: the spine, with schemas as its first customer

Status: BLESSED (user 2026-08-01), IN EXECUTION same day. The first
implementation session of DESIGN-external-contracts. **Schemas go
first deliberately**: they exercise EVERY part of the spine
(discovery, declaration, `add`/`restore`, the lockfile, hashing,
`verify`-against-hash, subset-with-teaching-errors) where signatures
exercise only half — signatures need no vendoring at all in v1, so
building them first would mean designing the fetch machinery without
a customer, which is what the design's own principle warns against.

Two supporting reasons: **the corpus problem is already solved**
(SchemaStore, k8s's published OpenAPI — real artifacts to fetch on day
one, where signatures would need `weir sig generate` built first just
to produce something to consult); and **the harder validator lands
early**, so the JSON Schema subset question gets settled against real
k8s files rather than in the abstract. The honest cost: a network
fetch and a trust story on day one — unavoidable if the spine is to be
exercised, and better confronted with a real remote artifact than a
hypothetical one.

This session is large. It may split; the report says where.

## THE RULINGS (work item 1, written before building)

Corpus: six real k8s standalone-strict schemas (v1.28.0,
yannh/kubernetes-json-schema): deployment-apps-v1 (604KB),
statefulset-apps-v1 (649KB), cronjob-batch-v1 (714KB), service-v1
(42KB), ingress-networking-v1 (35KB), configmap-v1 (17KB).

1. **Layout**: as proposed — kind directories
   (`.weir/schemas/<name>.json`, `sigs/`, `modules/`), `lock.json` at
   the `.weir/` root. Kind dirs give each kind its own namespace; the
   lock sits at the root it governs.
2. **Names**: RE-RULED pre-merge by PLAN-restore-rename:
   `weir add <kind>` / `weir restore` / `weir verify`. (`vendor`
   named the storage strategy, not the act; `add` is kind-aware
   because acquisition differs per kind — and it absorbs the
   signatures design's `sig generate` verb; `restore`/`verify` are
   lockfile-shaped, kind-agnostic by construction.) Verify remains
   the environment check — the third instance of
   check-that-needs-the-environment-is-its-own-command.
3. **Lockfile**: `.weir/lock.json`, created by the first
   `add`, absent until then. Per artifact: kind, name, source
   url, sha256 (hex, of the file bytes), local path (relative to
   `.weir/`). Written with Utf8JsonWriter, read with JsonDocument
   (reflection-free — the AOT discipline). Checked in.
   **The discovered NINTH ruling — where name→URL lives**: the lock
   IS the standing record (and the MANIFEST — deliberate; no ranges
   means nothing else to hold). `weir add schema <url> --as <name>`
   fetches, writes the file and the entry; `weir restore`
   re-materializes anything in the lock missing on disk
   (hash-verified). Districts
   declare schemas by NAME; the lock maps names to sources. No
   separate manifest (the signatures design's rejected-manifest
   reasoning holds: nothing else to disambiguate).
4. **Fetch failure modes**, each its own message: unreachable host
   ("cannot reach <host> — <detail>"); non-200 ("<url> answered
   <status>"); restore hash mismatch ("fetched bytes hash <a> but the
   lock records <b> — the source changed; if intended, `add`
   again"). Redirects: followed silently (the platform default); the
   lock keeps the DECLARED url.
5. **The subset, corpus-measured** (keyword census over all six):
   IN — `type` (a string, or an ARRAY of strings: the nullable
   spelling, 143 corpus uses), `properties` (906), `required` (511),
   `items` (492), `additionalProperties` (906 `false` + 106 schema —
   both forms), `enum` (14), and `oneOf` RESTRICTED to scalar-type
   alternatives (all 143 corpus occurrences are the IntOrString
   idiom; general composition stays out).
   ANNOTATIONS, accepted and ignored, stated: `description`,
   `format`, `title`, `$schema`, and `x-*` vendor extensions.
   REJECTED with a teaching error naming the keyword and the JSON
   path: `$ref`, `$defs`/`definitions`, `allOf`, `anyOf`, `not`,
   `if`/`then`/`else`, `patternProperties`, `const`, and the
   numeric/string constraint family (`pattern`, `minimum`, …) — ALL
   measured ZERO in the corpus; each joins the subset when a corpus
   needs it, not before.
6. **Remote `$ref`**: MOOT BY CORPUS — standalone-strict schemas
   inline everything ($ref count: zero), so `$ref` itself is
   rejected, and its teaching names the way out: add the
   STANDALONE variant (kubernetes-json-schema publishes both).
7. **District spelling**: `yaml schema=<name>` on the marker line;
   names are `[a-z0-9-]+`. The marker classifier accepts the suffixed
   form; `to yaml`/`from yaml` exclusions unaffected. A district
   with no declaration is unvalidated, exactly as today.
8. **`verify` reporting**: per artifact, distinct — "absent — run
   `weir restore`" vs "modified — sha256 <a>, lock records
   <b>". Any finding exits 1.

## Part A — the spine (built once, three future customers)

**Discovery**: walk up from the declaring file to the first `.weir/`,
stop there (a `.weir/` lacking the artifact is the error — never
continue upslope), do not cross `.git` (nor reach the filesystem
root). Errors name the resolved path AND the `.weir/` searched from.

**`verify` is written for TWO check kinds from the start** — today
"does this artifact still match its hash", later "does this signature
still match its tool's `--version`". Structure it so the second is an
arm, not a rewrite.

**Property 3 is THE pin**: a script with and without its contracts
produces byte-identical output. Run it here; every future customer
re-runs it.

**`weir check` never fetches, never spawns, never touches the
network** — pin the negatives (a fixture whose schema URL would fail
loudly if contacted).

## Part B — schemas, the customer

**What is checkable, stated honestly** (the design's scoping):
- **structural validation ALWAYS** — unknown fields, missing required
  fields, misplaced nesting. These are the errors people actually hit
  (`apiVerison`, a `spec.template.spec` in the wrong place).
- **value validation WHERE TYPES PERMIT** — `replicas: $n` checks
  int-against-integer; a pattern or enum constraint on a `string`
  splice does not. `for`-generated content and key splices relax the
  unknown/required checks for the subtree they touch — stated.
- Say this in the docs. A user who thinks a schema validates
  everything will be surprised in the wrong direction.

**The k8s reality**: published schemas are per-kind-per-version
files, so a project vendors several and names them per district.
Tedious, predictable, and better than weir learning one ecosystem's
discriminator conventions.

## Bars

- **Zero movement on everything existing.** Schemas are additive:
  every district without a declaration behaves byte-identically.
- Every named error pinned with exact text; every rejection located.
- The subset STATED wherever schemas are mentioned.
- Fetch is explicit, never implicit: `check` with a missing schema
  errors telling you to run `restore` (locked) or `add` (undeclared),
  rather than fetching.
- **The security non-claim in SECURITY.md**: contracts constrain what
  weir ACCEPTS, not what runs.

## Work items

1. Rule the eight decisions in writing; then build. [DONE above]
2. Part A: discovery, layout, `add`/`restore`, `verify` (two-arm shaped),
   the lockfile, hashing, the never-fetch-during-check pins,
   property 3's pin.
3. Part B: the subset parser with located rejections, the district
   declaration, structural validation, the type-permitting value
   checks.
4. A real k8s fixture end to end: add a published schema, declare
   it on a district, catch an `apiVerison` typo and a misplaced
   nesting at CHECK time.
5. Docs: the contracts concept stated once, the subset, the honest
   scoping, the type-provider one-liner for the F# audience;
   DECISIONS rows for the rulings.
6. Report: the rulings, the corpus measurements behind the subset,
   what split out if the session did.

## SESSION REPORT (2026-08-01 — did not split)

All work items landed in one session. The rulings above held; the
ninth (the lock as the standing record) emerged and is in the
[D:contracts-spine] row. Corpus measurements that DECIDED the subset:
zero `$ref`/`$defs` (standalone variants inline — remote-$ref moot),
zero composition, zero constraint keywords, oneOf = IntOrString
143/143, additionalProperties false/schema = 906/106, enum = 14.

THE FIND: the machine boundary needed its third face — `yaml
schema=x` glued to the sentinel parsed as a COMMAND under check; the
head guard now refuses the ` schema=<name>`+sentinel shape (no user
argv is ever glued). A fixture-realism correction fell out: the
sibling-sentinel acceptance pin glued its sentinel where the
assembler spaces it — corrected, named.

Executable truth: 955 unit (+discovery/subset pins), the e2e
contracts battery (add/restore/verify lifecycle against a local server
serving the committed real configmap schema; apiVerison with
did-you-mean; misplaced nesting; property 3; never-fetch), full
ritual green. The in-session published-schema fetch (network) pulled
all six corpus files from yannh/kubernetes-json-schema@v1.28.0.

Deliberately NOT here: GUIDE/SKILL runnable doc blocks (the doc-test
cwd cannot safely host a `.weir/`; prose + the e2e battery instead,
stated); the `from yaml T`-vs-schema interaction (ignored in v1, per
the design — the record is the adapter path's contract); signature
and module customers (they inherit the spine; the signatures design
is this design's first FOLLOW-ON customer).
