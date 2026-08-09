# weir — rider: `vendor` becomes `restore`

Status: EXECUTED (2026-08-01; blessed same day). Rider, on the
unmerged contracts-spine branch — fixed before merge, so it lands as
one coherent change with no rename in its history.

## The ruling

**`weir restore`** and **`weir verify`**.

`vendor` was borrowed from Go and names the STORAGE STRATEGY, not the
act — backwards for a verb. `restore` is .NET's word for exactly this
operation (read what is declared, materialize it locally, write the
lock), which is the right meaning AND the familiar one for weir's
primary audience. `verify` stays as-is.

Alternatives weighed, recorded so the question stays closed: `fetch`
(accurate but sounds partial — git's fetch deliberately does not
apply); `sync` (right including the removal case, vaguer about
direction); `install` (implies system mutation; these are repo-local
files); `pull` (git-tainted); noun-scoped `weir contracts
restore/verify` (groups nicely and would make `list`/`clean` free
later — **rejected for now** as two words for a two-command family,
BUT see the note below).

## The note that outlives the rename

**If a third contracts command ever appears** (`list`, `clean`,
`outdated`), revisit the noun-scoped form — at three commands the
grouping earns its word. And **when signatures land, `verify`
unqualified becomes ambiguous** (verify what — the hashes or the tool
versions?). Today the answer is "everything declared, whatever kind",
which is coherent; if it stops being coherent, that is the trigger for
`weir contracts verify`. Record the trigger, do not pre-empt it.

## The command family, settled

| command | operates on | kind-aware? |
|---|---|---|
| `weir add <kind> …` | a KIND | yes — acquiring differs per kind |
| `weir restore` | the LOCKFILE | no |
| `weir verify` | the LOCKFILE | no |

**`restore` and `verify` are kind-agnostic BY CONSTRUCTION** — every
lock entry has a source, a hash, and a path. **`add` is kind-aware
because acquisition genuinely differs**: a schema is a URL fetch, a
signature is GENERATED from a locally installed binary, a module is a
repo at a ref.

    weir add schema <url> --as k8s-deployment
    weir add sig bicep                       -- generates from the installed tool
    weir add module <repo-url> --ref <sha>   -- v2

**This absorbs `weir sig generate`** from the signatures design.
**Kind names, ruled**: `sig`, `schema`, `module` (not `import` — the
statement consumes the artifact; the unit is a module).

**The lockfile is the manifest** — deliberate, not an omission:
manifests exist to hold RANGES a resolver pins; contracts have no
ranges, so the lock states everything (`add` writes it, `restore`
reads it, `verify` checks it).

## SESSION REPORT

Pure rename plus the `add` restructure, no behaviour change beyond
spelling and the subcommand split. Moved pins, named: the
never-implicit-fetch teaching SPLIT per the plan's ruling — the
checker tells restore from add apart BY THE LOCK (entry-but-no-file →
"the lock records it; run `weir restore`"; no entry → "add it: weir
add schema <url> --as <name>") — both e2e-pinned; the $ref teaching's
"vendor the STANDALONE variant" verb became "add the STANDALONE
variant" (unit pin moved with it); `added schema …` replaced
`vendored schema …` in the add report line. `weir add sig`/`add
module` exist as located teachings naming their pending customers
(bare `weir add` lists kinds). `weir sig generate` was never built —
nothing to absorb, noted. Internal names followed (`addFetched`,
`restore`). The word `vendored` REMAINS as the design's property-1
adjective (the storage property — the objection was to the verb).
Final grep: zero command-word residuals outside this file's own
reasoning, the DECISIONS row's parenthetical, one historical NOTES
sentence, and git-subrepo's unrelated example directory named
`vendor` in its own e2e fixture.

INCIDENTAL FIND, fixed here: the contracts e2e's port randomization
from the spine session was LOST before commit — the fixing compound
began with a `pkill -f "http.server 8931"` that matched its OWN
shell's command line and killed it (exit 144), so the edit never ran
and the battery kept a fixed port (a latent collision flake).
Randomized now, and the lesson is one line: never pkill a pattern
your own command line contains.
