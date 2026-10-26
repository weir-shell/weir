# PLAN: lib — the official module library, and the bare-name add

**Status: BUILT (2026-10-26, branch feat/lib; library skeleton at /output/lib, unpushed) — R1–R6 as recommended; session report at the end.**

Sequencing: this plan first; PLAN-derived-ord next; PLAN-semver (the
library's first module) after both, gated on this plan's SESSION REPORT
(dependency-gate rule) — it consumes the repo skeleton and the bare-name
add this plan lands, and nothing here consumes it.

## The receipt

`weir add module` vendors one remote module per invocation, from a
host shorthand or a raw URL, locked by exact SHA and imported as
`import "weir:<name>" as X` ([D:add-module], [D:add-module-closure]).
What weir does NOT have is a place of its own for modules the project
itself stands behind: the starship port lives under
`github.com/weir-shell/ports`, examples live in `examples/`, and the
library of things every script eventually wants (a semver comparer
first, [PLAN-semver]) has nowhere to go that a user can name without
remembering an org, a repo, a path and the `//` separator.

Two things, then. One repo, `weir-shell/lib`, holding official modules
written in weir, each self-contained and tested, tagged `v0.x`. And one
spelling in the binary — `weir add module <name>@<tag>` — that resolves
a bare name to that repo and otherwise does exactly what `add module`
does today. The spelling is sugar over the existing source grammar; the
lock, `verify`, `restore` and the `weir:` import never see it.

## What the code says today (anchors, verified)

**The add handler.** `src/Weir/Program.fs` ~429–604,
`| "add" :: "module" :: spec :: flags when …`. The `when` guard admits
exactly three flag shapes: `[--as n]`, `[--as n; --path p]`,
`[--path p; --as n]`. Anything else — including a bare spec with NO
flags — falls to the usage branch at ~606 (`exit 2`). So `--as` is
REQUIRED today; the brief's `weir add module semver@v0.1.0` (no `--as`)
does not parse. The bare-name form must derive the name (R1).

Inside the handler, in order: `Contracts.plainName name` (~449; the
message at ~462: "'--as x' must be a plain name (a letter, then
letters/digits/_) — it becomes the vendored file and the import
alias"); the builtin-alias refusal at ~465–467
(`Map.containsKey derivedAlias Builtins.typeEnvStrict.Modules` →
"'{name}' derives the alias '{Alias}', a builtin module — pick another
--as"); then `Contracts.resolveModuleSpec spec entryPath` (~587);
`Root = None` → one raw file (`addLeaf`, validated by
`Script.checkVendoredModule` before any write, [D:add-validates]);
`Root = Some` → `Script.walkVendorClosure` → one file goes through
`addLeaf`, several through `addClosure`. Success prints
`added module {name} ({sha12}…, {n} member(s)) from '{url}'` and
`import it:  import "weir:{name}" as {Alias}`.

The builtin aliases that would clash with a lowercased bare name
(`Builtins.moduleTable`, Builtins.fs ~6539): Seq, Str, Args, Bytes,
Color, Dir, Duration, Env, File, Float, Frontier, Graph, Http,
Instant, Json, Log, Map, Net, Option, Path, Plan, Poll, Proc, Result,
Retry, Secret, Self, Server, Size, Table, Term, Tree, Uuid, Yaml. So
`retry`, `graph`, `tree`, `poll`, `log` are names the library can
never publish under the bare form (R2). `semver` is free.

**The source grammar.** `Contracts.resolveModuleSpec`
(`src/Weir/Contracts.fs` ~983–1097). `http(s)://` → the raw form (with
`--path` → root + entry). Otherwise the spec must contain `//`
(~1021: "the shorthand needs the // repo/path separator — …"), then
the last `@` splits path from ref (~1029/~1036: "an explicit @ref is
required — @main, @v1.2.0, or @<sha>; weir does not guess a default
branch"), then `host/repo` splits at the first `/`; `github.com`
resolves the ref to a full SHA through
`https://api.github.com/repos/{repo}/commits/{ref}` (~1053) and builds
`Url = https://raw.githubusercontent.com/{repo}/{sha}/{path}`,
`Root = Some(raw base, path)`; `gitlab.com` likewise; any other host
→ "unknown host". NOTE: `resolveModuleSpec` is NOT pure — the
host arms fetch. The unit tests at `tests/Weir.Tests/Tests.fs` ~13860
(`vendorClosureTests`) exercise only the raw-URL arms for that reason.
The bare-name expansion therefore lives in a NEW pure function in
front of it, not inside it.

A `//` in a bare name is impossible by construction (a bare name has
no `/`), so the three existing teaches at ~1021/~1029/~1036 keep
firing for every spec that is not library-shaped, byte-identical.

**The lock.** `Contracts.LockEntry` (~520): `Kind`, `Name`, `Url`,
`Sha256`, `Path`, `Version`, `Closure`. `vendorFile` (~1163) writes
`modules/<name>.weir` and the entry with `Url = Http.stripUserinfo url`
— for a github shorthand that is the raw URL AT THE FULL SHA.
`restore` (~2298) and `verify` (~2981) read entries only; the re-add
hint they print (~2359) spells the locked raw URL plus `--as`, which
is a valid `add module` spelling for a library module too. Nothing
here changes for a bare-name add: the lock entry is indistinguishable
from one made by the long spelling of the same module.

**The import.** `Script.resolveImportPathRaw` (`src/Weir/Script.fs`
~4839): `weir:<name>` → `Contracts.findWeirDir dir` (the upward walk
to the first `.weir/`, bounded by `.git` or the filesystem root,
Contracts.fs ~495) → `modules/<name>.weir` or `modules/<name>/`
(`vendoredFault`, ~4898). The missing-module teach at ~5173:
"no vendored module 'x' (…) — vendor it: weir add module
<host>/<org>/<repo>//<file>@<ref> --as 'x' (a generated types module
lands here via: weir gen types --schema 'x')". R5 asks whether it
also names the bare form.

**The self-contained bar, in code.** `Script.walkVendorClosure`
(~5633–5665) refuses a `weir:` import inside a vendored file:
"'{file}:{l}:{c}': imports 'weir:x' — a weir: import inside a vendored
module is not vendored with it (cross-remote vendoring is a current
boundary, not a refusal of the idea): inline the dependency, or vendor
both and import each from your own code". docs/tooling.md "Remote
modules" (~508–629) says the same in prose. [D:add-module-closure]
records it as a boundary, and dev/plans/DESIGN-external-contracts.md
~232–262 ("customer three") states the price of lifting it: a
per-module manifest, version identity beyond a SHA, MVS. The brief is
right that this bar is the library's boundary for now; the library's
CONTRIBUTING states it as its own rule so a module author never
discovers it at `add` time.

**The e2e battery.** `ci/e2e.sh` ~14094–14161 (`[D:add-module]`):
a `python3 -m http.server` on `127.0.0.1:$amport` serves `greet.weir`;
every deterministic pin vendors by RAW URL from it. The host shorthand
has exactly one probe and it is LIVE (~14158):
`github.com/weir-shell/weir//ci/release-published.weir@v0.0.5` →
"is not a module", tolerating "rate limit|answered 403". There is no
offline mechanism for the github.com arm (the API resolve has no seam,
and R3 recommends not adding one), so the bare-name positive path is
unit-pinned on the pure expansion and live-probed the way the shorthand
is; the refusals are offline e2e. `[D:add-module-closure]`'s block
(~14163–14290) uses `--path` against the same local server — the
closure form is irrelevant here, a library module is one file.

**Precedents the brief names, corrected.**

- There is no `[D:restore-rename]` row. The `add`/`restore`/`verify`
  re-ruling lives INSIDE the `contracts-spine` row (docs/DECISIONS.md
  ~147, "RE-RULED pre-merge (PLAN-restore-rename)"); the plan file is
  dev/plans/PLAN-restore-rename.md. Cite `[D:contracts-spine]`.
- `weir add sig <tool>` has no well-known SOURCE: it GENERATES from the
  installed binary (Program.fs ~411–424, `Script.SigGen.generateTo`;
  the lock's `Url` slot holds `generated:help`, Contracts.fs ~524,
  [D:command-signatures]). The precedent for "a bare name means one
  well-known place" is the `weir:` namespace itself — `weir:<name>` is
  shape-scoped and resolves to exactly one directory, never a fallback
  ([D:add-module]'s option (c)) — and `--schema <name>` for `gen
  types`. The bare-name add follows that shape: a distinct SPELLING
  (no `/`), one place, never a fallback for a spec that fails the long
  grammar.
- `[D:module-signatures]` (~409) is as the brief says: a `let name :
  <ty>` line with no `=` is the export; unsigned members are private.
  The library's "signed exports only" bar is therefore not a rule the
  library adds — it is what a `module` already is; CONTRIBUTING says
  it so authors expect it, and the `///` doc rule ([D:doc-comments]:
  the doc's one home is the signature) rides the same line.
- docs/tooling.md "Vendoring" under YAML schemas (~375) is the
  `add schema` section the brief points at; it is the shorter sibling
  of "Remote modules" and needs no change.
- dev/plans/README.md's index stops at 2026-08-04 (the LSP plans are
  not in it). The line this plan adds follows its format; back-filling
  the index is not this plan's job.

## The shape

### A. weir: the bare-name add

**One pure function, in Contracts before `resolveModuleSpec`:**

```fsharp
// the official library's short spelling [D:lib-short-name]: a spec
// with no `/` is `<name>@<tag>` and expands to the github shorthand at
// libRepo; everything with a `/` is the existing grammar, untouched
let libRepo = "github.com/weir-shell/lib"

type LibSpec =
    { Name: string          // the module and the default --as
      Tag: string
      Source: string }      // "github.com/weir-shell/lib//modules/<name>.weir@<tag>"

/// Ok None: not library-shaped (has a `/` or `:`) — the caller passes
/// the spec through unchanged. Ok (Some s): expanded. Error: library-
/// shaped but malformed, the message names the spelling.
let libSpec (spec: string) : Result<LibSpec option, string>
```

Rules, in order:

1. `spec` contains `/` or `:` → `Ok None`. (Every URL and every host
   shorthand has a `/`; `:` guards `weir:x` typed by habit, which then
   falls to the existing "needs the // repo/path separator" teach — no
   new message for it.)
2. Split at the LAST `@` (the shorthand's rule). No `@`, or an empty
   tag → `Error` (message 1 below).
3. The name must pass `Contracts.plainName` (it becomes the file and
   the alias; the library's file is `<name>.weir` so a `.weir` suffix
   or a hyphen refuses here) → else `Error` (message 2).
4. `Ok(Some { Name; Tag; Source = $"{libRepo}//{Name}.weir@{Tag}" })`.

Any ref the shorthand accepts is accepted (`@main`, `@<sha>` too — R4);
the messages and docs spell tags.

**Program.fs plumbing** (the handler at ~429):

- The guard admits `flags = []` as a fourth shape. Decode as today;
  `name` becomes `string option`.
- Before `plainName`: `match Contracts.libSpec spec with Error e ->
  "weir add module: {e}"; exit 1 | Ok None -> spec, name | Ok(Some l) ->
  l.Source, (name |> Option.defaultValue l.Name)`. A long spec with no
  `--as` → the existing usage text (exit 2), as today; a bare spec with
  `--path` → the existing "--path goes with a raw root URL" refusal
  fires from `resolveModuleSpec` on the expanded spec (no new message).
- Everything after — `plainName`, the builtin-alias refusal, resolve,
  fetch, validate, vendor, the two success lines — is untouched. The
  "added module … from 'https://raw.githubusercontent.com/weir-shell/
  lib/<sha>/semver.weir'" line already names the library; no echo of
  the expansion (R6).
- Usage text at ~608, ~613 and ~868 gains one line:
  `weir add module <name>@<tag> [--as <name>]   an official library module (github.com/weir-shell/lib)`.

**Messages** (the script author's language; no "resolve", "expand",
"shorthand grammar"):

1. No tag — `weir add module: 'semver' has no @tag — a library module
   is pinned by its release tag: weir add module semver@v0.1.0 (the
   tags: https://github.com/weir-shell/lib/tags)`. Fires for `semver`
   and `semver@`.
2. Not a name — `weir add module: 'semver.weir' is not a library module
   name — <name>@<tag>, the name a letter then letters/digits/_: weir
   add module semver@v0.1.0`. Fires for `semver.weir@v1`, `@v1`,
   `my-mod@v1`.
3. Builtin clash — the EXISTING message at ~467, unchanged: `weir add
   module: 'retry' derives the alias 'Retry', a builtin module — pick
   another --as`. "pick another --as" is already the fix for the bare
   form (`weir add module retry@v0.1.0 --as myretry`); R2.
4. The library's host answered — the EXISTING resolve error, unchanged:
   `weir add module: resolving @v0.1.0: 'https://api.github.com/repos/
   weir-shell/lib/commits/v0.1.0' answered 404 (NotFound) — or the repo
   is private: set WEIR_TOKEN_GITHUB_COM`. It names the library repo
   and the tag, which is what a typo in either needs.
5. The missing-vendored teach at Script.fs ~5173 gains the bare form
   first (R5): `no vendored module 'semver' (…) — vendor it: weir add
   module semver@<tag> (an official library module), or <host>/<org>/
   <repo>//<file>@<ref> --as 'semver' (a generated types module lands
   here via: weir gen types --schema 'semver')`.

### B. The library repo (`/output/lib`, a local git repo for the user to push)

Layout — modules at the ROOT, because the spelling expands to
`//<name>.weir`:

```text
lib/
  README.md
  CONTRIBUTING.md
  LICENSE               Apache-2.0, weir's text verbatim
  NOTICE                "Copyright 2026 the weir authors" (weir's first line)
  tools/test.weir       the runner (weir, not bash)
  tests/                one <name>.weir per module (PLAN-semver adds the first)
  .github/workflows/ci.yml
```

No module ships in this plan — the skeleton must be green with zero
modules (the runner says so and exits 0) and with one (PLAN-semver's
session proves it).

**README.md** — three short sections. *What this is*: official weir
modules, one file each, vendored by `weir add module <name>@<tag>`
(the long spelling beside it once, so a reader learns it is sugar),
imported as `import "weir:<name>" as X`, pinned by SHA in the
project's `.weir/lock.json`, `verify`/`restore` as for any module;
"not a package manager" in the same words as docs/tooling.md. *The
modules*: a table `name | since | one line` — empty row "none yet"
until PLAN-semver. *Versions*: the whole repo is tagged `v0.x`
(`v0.1.0`, `v0.1.1`, …); a tag pins every module at once; under
`v0.x` any module may change shape between tags, and a project moves
by re-running `add` with the new tag and reviewing the vendored
diff; the tag list is the changelog (each tag's GitHub release notes
name the modules it touched). No `latest`, no ranges, no branch to
track — point at a tag.

**CONTRIBUTING.md** — the module bar, as a checklist the runner
enforces where it can:

- one file, `<name>.weir`, a `module` (declarations only — no command
  or bare expression at import; weir refuses them, so this is stated,
  not policed);
- SELF-CONTAINED: no `import` at all — not `weir:`, not relative
  (`weir add module` refuses the first and would vendor the second as
  a closure; the library wants neither until the manifest/MVS design in
  weir's DESIGN-external-contracts lands). The runner greps for it so
  the refusal is here, not at the user's `add`;
- exports are signatures: `let name : <ty>` then the implementation;
  anything unsigned is private; every signature carries a `///` doc
  (the signature is the doc's one home);
- `let pure` where the body allows it, so hover shows the `(pure)`
  badge; effects where needed are fine (weir is effect-normal) and
  `weir check --can` on the module shows them — the review surface;
- a name that is not a builtin module's alias lowercased (the list in
  weir's docs; `retry`, `graph`, `tree`, `poll`, `log` are taken) —
  weir refuses the bare add otherwise;
- `tests/<name>.weir`: `import "../<name>.weir" as X`, asserts with
  `fail`, exits 0 on green; `weir check` clean, `weir fmt --check`
  clean, for both files;
- the PR adds a row to README's table; a release is a tag.

**tools/test.weir** — the runner, against the weir on PATH (CI
installs it). Outline (the implementer writes it from the skill and
`weir check`s it; the shape below uses only constructs the skill
documents — `Path.glob`, `for … do`, `| complete`, `| exitCode`,
`Str.*`, `fail`, `exit`):

```text
#!/usr/bin/env weir
// Runs every module's checks against the installed weir.
//   weir tools/test.weir

print (weir --version | line)
let modules = Path.glob "*.weir"               // root only — the modules
let tests   = Path.glob "tests/*.weir"
// per module: weir check, weir fmt --check, no `import ` line (self-contained),
//             a tests/<name>.weir exists
// per test:   weir check, then weir tests/<name>.weir exits 0
// count reds as you go (a mutable is not in the skill — collect
// (name, verdict) rows and Seq.where them at the end); print one line
// per check; fail with the red count at the end, else print "ok: N modules"
```

Zero modules → "ok: 0 modules", exit 0. The runner refuses a test
without a module and a module without a test by name.

**.github/workflows/ci.yml** — one job, ubuntu-latest: checkout;
`curl -fsSL https://weir.sh/install.sh | sh` (installs the current
release to `~/.local/bin`, [D:releases]); add it to `PATH`; `weir
tools/test.weir`. Triggers: push, pull_request, and `workflow_dispatch`.
No matrix, no cache. (The runner pins to whatever release the installer
serves; a module that needs a newer weir says so in its README row —
Non-goals.)

**LICENSE / NOTICE** — copied from weir's; NOTICE keeps only the first
block (no third-party notices apply to weir source files).

The skeleton is committed in `/output/lib` with the user's identity
(`-c user.name=queil -c user.email=…`, as the weir branch), on `main`,
one commit, NOT pushed and NOT tagged — the first tag is PLAN-semver's
(`v0.1.0`, with the first module), because a tag with no module is a
release of nothing.

## Phases

- **P0 — the pure function and its pins (small, ~0.25 day).**
  `Contracts.libRepo`, `LibSpec`, `libSpec`; unit pins (Tests);
  nothing else moves yet.
- **P1 — the handler, the messages, the usage lines (small, ~0.25
  day).** Program.fs's guard and the decode; the Script.fs ~5173 teach
  (R5); the e2e refusals and the live probe; the missing-vendored e2e
  grep at ~14145 updated for the new text.
- **P2 — docs and ledger (small, ~0.25 day).** docs/tooling.md
  (subcommand bullet ~79; "Remote modules" gains "### The library");
  CHANGELOG `## v0.0.79`; skills/weir/SKILL.md ~28; DECISIONS row;
  NOTES story. Rides P1's PR.
- **P3 — the library skeleton (small, ~0.5 day).** `/output/lib` as
  above; `weir check tools/test.weir` clean; the runner green on zero
  modules; one local commit. Independent of P0–P2 (it needs only the
  installed weir); do it last so CONTRIBUTING can quote the shipped
  messages.

Total ~1.25 days. One weir PR (commit areas `contracts,cli,docs`) and
one local repo.

## Tests

**Unit** (`tests/Weir.Tests/Tests.fs`, a `libSpecTests` list beside
`vendorClosureTests` ~13813, `[D:lib-short-name]`):

- `semver@v0.1.0` → `Some { Name = "semver"; Tag = "v0.1.0"; Source =
  "github.com/weir-shell/lib//modules/semver.weir@v0.1.0" }`.
- `semver@main`, `semver@0123abcd` → Some (R4); `a_b2@v1` → Some.
- `semver`, `semver@` → Error containing "has no @tag" and the spelling
  `weir add module semver@v0.1.0`.
- `semver.weir@v1`, `my-mod@v1`, `@v1`, `1x@v1` → Error containing "is
  not a library module name".
- Pass-through: `github.com/acme/lib//x.weir@v1`,
  `https://h/x.weir`, `weir:semver`, `./x.weir`, `sr.ht/a/b//x@v1` →
  `Ok None` — the long grammar's teaches are untouched (the existing
  e2e lines ~14140–14142 stay as the behavioral proof).
- Composition: `libSpec` of its own `Source` → `Ok None` (an expansion
  never re-expands).

**e2e** (`ci/e2e.sh`, in the `[D:add-module]` block, offline unless
marked):

- `weir add module semver` → "has no @tag", exit 1, and the tree
  byte-identical (no `.weir/`, the [D:add-validates] pin shape).
- `weir add module semver.weir@v1` → "is not a library module name".
- `weir add module retry@v0.1.0` → "derives the alias 'Retry', a
  builtin module — pick another --as" (the bare form hits the existing
  refusal; the name came from the spec, not `--as`).
- `weir add module semver@v0.1.0 --path x.weir` → "--path goes with a
  raw root URL" (the existing refusal, through the expansion).
- `weir add module github.com/acme/lib//x.weir@v1` (no `--as`) → usage,
  exit 2 (the long form still requires `--as`).
- `import "weir:nope"` → the ~14145 grep moves to the new teach text
  ("weir add module nope@<tag>").
- LIVE, beside the existing live probe and with the same tolerance
  posture: `weir add module nosuch@v0` → output contains
  `api.github.com/repos/weir-shell/lib/commits/v0` (a 404 and a
  rate-limit 403 both name the URL, so the assertion is the same under
  either; it proves the expansion reached the library's host and the
  default `--as` was derived — the handler got past the name checks).
  PLAN-semver replaces it with the positive probe (`semver@v0.1.0` →
  "added module semver") once the repo is pushed and tagged.

**The library repo:** `weir check tools/test.weir` clean; `weir
tools/test.weir` on the empty skeleton prints "ok: 0 modules" and
exits 0; a planted `tests/orphan.weir` makes it fail naming the
orphan; a planted module with `import "weir:x"` makes it fail naming
the line. These run in the implementation session by hand (the
weir battery does not reach into /output/lib); the lib CI runs the
same runner.

## Perf

None. `libSpec` is a string split; the fetch chain is the existing
one.

## Risks

1. **`--as` becoming optional for the bare form reads as "optional
   everywhere".** The guard admits `[]` only; a long spec without
   `--as` keeps hitting usage (pinned). The usage text shows `[--as
   <name>]` on the library line only.
2. **The live probe is network-shaped** — the same exposure the
   existing shorthand probe already carries, with the same tolerance.
   The deterministic truth is the unit pin on `libSpec`.
3. **A library name that clashes with a future builtin module.** A new
   builtin alias (the table grows — `Graph`, `Frontier`, `Plan` are recent) would make an existing
   library module un-addable by bare name; the long spelling still
   works, and the message says so. Low likelihood; noted in the
   library's CONTRIBUTING as the reason the name list is checked.
4. **The repo does not exist yet when P1 ships** — message 4 ("answered
   404") is honest about it; nothing is written. The user pushes the
   skeleton and PLAN-semver tags it.
5. **The re-add hints in `restore`/`verify`** spell the raw URL, not the
   bare name — correct, just longer. Non-goal below.

## Non-goals

- Bundling modules into the binary (zero-setup vs pinning — pinning
  wins while the library is young; stated in the DECISIONS row).
- A configurable library host (R3), a search, a list command, `latest`.
- Lifting the self-contained bar: cross-module `weir:` imports wait on
  the manifest/MVS design ([DESIGN-external-contracts] customer three).
- Teaching `restore`/`verify` to print the bare spelling in their
  re-add hints.
- A library CHANGELOG file; a weir-version floor per module; Windows
  or macOS legs in the library's CI.
- Any change to the lock schema, `verify`, `restore`, the `weir:`
  resolver, or the `module` grammar.

## Docs

- **docs/tooling.md** — the subcommand bullet (~79) gains the bare
  form; "Remote modules" gains a subsection `### The library` after
  the `import "weir:retry"` paragraph (~597): the spelling
  (`weir add module semver@v0.1.0`, "the same as
  `github.com/weir-shell/lib//modules/semver.weir@v0.1.0 --as semver`"), the tag
  rule (a tag, never a branch to track — the lock holds the SHA either
  way), `--as` to rename, and the bar in one sentence: every library
  module is a single file with no imports, because a vendored module
  cannot import another vendored module yet (link the paragraph above
  that says so). A pointer to the repo's README for the module list.
- **CHANGELOG.md** — under `## v0.0.79` (add the heading above
  `## v0.0.78` if the branch has none; [D:changelog] matches the
  heading exactly), `### Added`: "**`weir add module <name>@<tag>`
  vendors an official library module.** A bare name resolves to
  github.com/weir-shell/lib …" — one paragraph, the no-tag refusal
  named.
- **skills/weir/SKILL.md** ~28–33 — the vendoring sentence gains
  "an official library module by bare name: `weir add module
  semver@v0.1.0` (github.com/weir-shell/lib, tag required)". Inline
  code only; the fenced `weir` blocks there EXECUTE in CI
  (ci/skill-doc.sh), and an `add module` needs the network, so no
  fence — if one is ever wanted it is a `text` fence.
- **docs/DECISIONS.md** — the row below.
- **dev/NOTES.md** — a story "The official module library (2026-10-26)"
  : the two halves, the `--as` derivation, why the host is fixed, the
  bundling non-goal.

**DECISIONS row (append; key `lib-short-name`):**

```text
| lib-short-name | 2026-10-26 | THE OFFICIAL MODULE LIBRARY AND ITS SPELLING — `weir add module <name>@<tag>` (a spec with no `/`) is SUGAR for `github.com/weir-shell/lib//modules/<name>.weir@<tag> --as <name>`, expanded by a pure `Contracts.libSpec` in front of `resolveModuleSpec`; the lock, `verify`, `restore` and `weir:` never see the short form (the entry is the raw URL at the full SHA, as for the long spelling). The exact-version law stands: no `@tag` refuses naming the spelling (no latest, no ranges); any ref the shorthand accepts is accepted (the SHA is what locks). `--as` is OPTIONAL on the bare form only (the name IS the module) and still overrides; a derived alias that is a builtin module refuses as before, so the library never publishes `retry`/`graph`/`tree`/`poll`/`log`. The host is FIXED — a configurable registry is the manifest/MVS design's question, not a flag. THE LIBRARY (weir-shell/lib): one `module` file per name at the repo root, SELF-CONTAINED (no imports — a vendored module cannot `weir:`-import another until [DESIGN-external-contracts] customer three lands), signed exports with `///` docs, `let pure` where possible, a test per module and a weir-written runner (tools/test.weir) in CI; whole-repo `v0.x` tags. Bundling modules into the binary is a stated NON-GOAL (pinning beats zero-setup while the library is young). Pins: libSpecTests (expansion, refusals, pass-through, no re-expansion); e2e the four refusals offline, the host probe live. | NOTES "The official module library"; PLAN-lib |
```

## Rulings

- **R1 — `--as` on the bare form.** Today `--as` is mandatory (the
  guard). Require it on the bare form too, or derive it from the name?
  *Recommend: derive* — the whole point of the spelling is that the
  name IS the module; `--as` stays allowed to rename
  (`semver@v0.1.0 --as sv` → `sv.weir`, alias `Sv`). The long form
  keeps requiring `--as` (its path is not a name).
- **R2 — a bare name whose derived alias is a builtin module**
  (`retry@v0.1.0` → `Retry`). Refuse with the existing message, or
  auto-pick? *Recommend: refuse, message unchanged* — "pick another
  --as" is the fix and it works on the bare form; and the library
  CONTRIBUTING forbids publishing such a name, so the case is a user
  typing a name the library does not have.
- **R3 — a configurable host** (`WEIR_LIB` or a flag pointing the bare
  form at a mirror or a fork). *Recommend: not* — the bare form's
  value is that it means one thing; a fork is spelled with the long
  form today, at no loss; and a registry setting is the manifest
  design's question (DESIGN-external-contracts customer three), not a
  flag to pre-empt it with. Cost: the positive path has no offline
  e2e (unit pin + live probe instead, as the shorthand today).
- **R4 — refs on the bare form.** Tags only (`v…`), or any ref the
  shorthand accepts? *Recommend: any ref* — weir does not know what a
  tag is (the API resolves any ref to a SHA, and that SHA is what
  locks); refusing `@main` would mean inventing a tag-shape rule. The
  docs and every message spell tags; the exact-version law is the
  lock's, and it holds either way.
- **R5 — the missing-vendored teach** (`import "weir:x"` with nothing
  vendored, Script.fs ~5173): name the bare form first? *Recommend:
  yes* — the author who typed `weir:semver` is most likely after the
  library; the long form stays in the same message.
- **R6 — echo the expansion.** Print the long spelling before fetching
  so the user sees where the name went? *Recommend: no* — the success
  line already names `raw.githubusercontent.com/weir-shell/lib/<sha>/
  semver.weir`, and the resolve error names the API URL; a third line
  is noise.

## Session report (2026-10-26, feat/lib)

**weir.** `Contracts.libRepo`/`LibSpec`/`libSpec` (pure, in front of
`resolveModuleSpec`; rules 1–4 as planned). Program.fs: the guard
admits `[]` and `[--path p]` ONLY when `libSpec spec <> Ok None`, so
the long form without `--as` still falls to the existing usage branch
(no duplicated usage text); the library error rides the existing
`if/elif` chain ahead of `plainName`; `--as` derives from the name and
overrides; the three usage texts gain the library line. Script.fs: the
missing-vendored teach names `weir add module <n>@<tag>` first (R5).
Messages 1–2 as specified; 3–4 unchanged existing text. Pins:
`libSpecTests` (6 cases: expansion, R4 refs, no-tag ×2, not-a-name ×4,
pass-through ×5, no re-expansion); e2e cells in the `[D:add-module]`
block — no-tag (exit 1, the spelling named, `.weir/` tree hash
unchanged), not-a-name, `retry@v0.1.0` → the builtin refusal,
`semver@v0.1.0 --path x.weir` → the existing `--path` refusal, the long
form without `--as` → usage exit 2 naming the library line, the
missing-vendored grep moved to the new text, and the live probe
(`nosuch@v0` → output names
`api.github.com/repos/weir-shell/lib/commits/v0`). Docs: tooling.md
bullet + `### The library`; CHANGELOG `## v0.0.79` / `### Added`;
SKILL.md vendoring sentence (inline); DECISIONS `lib-short-name`;
NOTES "The official module library".

**Deviations.** (1) The plan expected `semver@v0.1.0 --path x.weir` to
reach the `--path goes with a raw root URL` refusal; the guard never
admitted `[--path p]` without `--as`, so it hit usage — the guard now
admits that shape for a library-shaped spec, and the refusal fires as
planned. (2) The sandbox turned out to have outbound network
(api.github.com answers), so the live probes ran here; the lib repo
does not exist yet, so the probe sees the honest 404 naming the URL
(Risk 4), which is the assertion. (3) Commit areas derive as
`cli,parser` (Script.fs maps to `parser` in ci/commit-area.weir), not
the plan's `contracts,cli,docs`.

**The library** (`/output/lib`, `main`, one commit as queil, no tag,
no remote): README, CONTRIBUTING (the bar incl. the full builtin-alias
list), LICENSE (weir's), NOTICE (first block), `tools/test.weir`
(`weir check`/`fmt --check` clean on v0.0.78; runs from any cwd via
`Self.scriptPath`), `tests/.gitkeep`, `.github/workflows/ci.yml`,
`.gitignore`. Verified by hand: zero modules → `ok: 0 modules` exit 0;
a planted `tests/orphan.weir` fails naming the orphan; a planted module
with `import "weir:x"` fails naming line 3 (and its check).

**Gates** (published binary `0.0.0-dev+59db64d-dirty`): unit 2568/2568
(the 6 lib cases listed by the verbose logger); decision-keys ok (821
rows); dead-privates 0; dx census 32/32 baseline; skill-doc 368 blocks
+ surface 300 members.
e2e: the full battery died at an unrelated cell (`a thread the limit
refuses is a weir error naming the limit`, ci/e2e.sh ~4476 —
`Resource temporarily unavailable (os error 11)`, the shared box
refusing a thread) before reaching the `[D:add-module]` block; the
block run in isolation (prologue + block) passes, live probes
included: `e2e ok: add module (… the library spelling's refusals,
live shorthand + library host)`.
