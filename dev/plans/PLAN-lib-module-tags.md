# PLAN: per-module tags in the library, and the bare add picks the latest

**Status: BUILT** (2026-10-27) — ruled by the user ("I like that");
the open details below were assumed as stated, flagged for the user.
Session report at the end.

## The receipt

Two questions, 2026-10-27, after the first module landed in
weir-shell/lib.

**A. "When using lib, can `weir add` figure out the latest tag?"** Not
today: [D:lib-short-name] requires `<name>@<tag>`. But the ref step
[D:ref-resolve-smart-http] already reads the repo's whole ref
advertisement, every tag included, so "latest" is a pure pick over
bytes the add already fetches — no extra request, no API.

**B. "Tags on the repo are meaningless as far as multiple modules are
concerned."** A repo-wide `v0.3.0` says nothing about which module
changed: every module's version bumps whenever any one does, and a
project pinned to `semver@v0.1.0` is pinned to the whole repo's state,
not to semver. The library's own README ("one tag pins every module at
once") describes the defect as a feature.

## The shape

**Per-module tags**, Go's multi-module convention: `<name>/v<semver>` —
`semver/v0.1.0`, `yaml/v0.2.3`. A tag belongs to one module; the
release step in the library is "change `modules/x.weir`, tag `x/vN`".
Repo-wide `v*` tags retire (the existing `v0.1.0` becomes
`semver/v0.1.0` — a new tag name on the same commit, the user's push).

**The add, three spellings, one listing.**

| spelling | meaning |
|---|---|
| `weir add module semver` | the highest STABLE `semver/v*` tag by semver precedence; prereleases (`semver/v1.0.0-rc.1`) are skipped unless no stable tag exists, then the highest prerelease; no tag at all is an error naming the tags page |
| `weir add module semver@v0.1.0` | the tag `semver/v0.1.0` exactly (the `v` is part of the version; `@0.1.0` is an error naming the spelling) |
| `weir add module semver@<40-hex>` | the sha, as today — passes the listing |

The `--as` name derives from the module name as today. A bare add
PRINTS what it picked, with the other add messages (stdout):
`semver: latest is semver/v0.1.0`. The github shorthand
(`<src>//<file>@<ref>`) and the raw-URL form are untouched: there
"latest" is ambiguous (default branch or a tag) and the ref stays
required.

**Semver precedence in weir's own code.** `Contracts` gains a pure
`semverKey : string -> (int * int * int * string option) option` and
`latestModuleTag : (sha * refname) list -> name -> (tag * sha) option`
(peeled `^{}` entries preferred as `pickRef` does): parse `MAJOR.MINOR.
PATCH[-pre][+build]` after `<name>/v`, order by the spec (core numeric;
a prerelease below its release; prerelease identifiers compared
dot-wise, numeric before alphanumeric, numerically then ordinally;
build ignored); a tag that does not parse is not a candidate. This is
the second implementation of semver precedence (the library's
`modules/semver.weir` is the first); it is in F# because the resolver
runs before any module is vendored. The pins hold both to the spec's
ordered list (semver.org §11's example sequence).

**The lock.** `LockEntry.Url` records the raw URL at the full sha as
today (restore and verify are byte-exact and unaffected). The lib
expansion's `Source` records `<repo>//modules/<name>.weir@<tag>` with
the TAG the pick resolved (never the bare name), so the lock's
provenance line reads `…@semver/v0.1.0` and a diff of two adds reads
as a version bump. Whatever the lock stores for the spec today (read
`Contracts` — `LockEntry` has no spec field; if the spec is only in
the add's output and the Url, then the tag rides in the add's printed
line and nowhere else — say so in the session report rather than
adding a lock field).

**The lib side** (/output/lib, main):
- README: the Versions section rewritten for per-module tags (the tag
  is the version, bare `add` = latest stable, `@vX.Y.Z` pins, the tag
  list per module is its changelog); the module table's tag column
  reads `semver/v0.1.0`; the usage lines drop `@v0.1.0` from the first
  example and keep one pinned example.
- CONTRIBUTING: the release step — tag `<name>/v<semver>` on the commit
  that changes the module; a tag names one module; never a repo-wide
  tag.
- CI: on a tag push matching `*/v*`, a check that the tag parses as
  `<name>/v<semver>` and `modules/<name>.weir` exists at that commit
  (`tools/tag-check.weir`, weir, run by the workflow with the tag name
  from `GITHUB_REF_NAME`); the existing test job unchanged.
- No `[D:…]` citations and no upper-case emphasis in lib files
  (user rule, 2026-10-27).

**The weir side**:
- `Contracts.libSpec`: `Tag` becomes `string option`; `Source` is
  completed by the resolver after the pick. Error messages: a `@` with
  nothing after it; `@0.1.0` (missing `v`); unchanged name rule.
- `resolveModuleSpec`: the lib arm lists refs (as the shorthand does),
  picks `latestModuleTag` when the tag is None, else resolves
  `refs/tags/<name>/<tag>` through `pickRef`; a `@<40-hex>` skips the
  listing. The message when nothing matches names the per-module tag
  shape and the tags page filtered to the module
  (`https://github.com/weir-shell/lib/tags?q=semver/`).
- `weir add module` prints the picked tag line for a bare add.
- docs/tooling.md usage line and the add-module paragraph; the
  `--help` text of `add module` (one source — read how tooling.md's
  usage block is checked against `weir --help` by e2e: the two must
  match byte-for-byte).
- CHANGELOG `## v0.0.80` (create the section above `## v0.0.79` if
  absent; another branch may add it too — keep-both on merge): Added
  the bare add and per-module tags; Changed: `semver@v0.1.0` now means
  `semver/v0.1.0` (the repo-wide tag spelling no longer resolves).
- DECISIONS row `lib-module-tags` (2026-10-27) superseding the
  tag-required and repo-tag clauses of [D:lib-short-name]; cites
  [D:ref-resolve-smart-http].
- Pins (unit): `semverKey` on the spec's sequence; `latestModuleTag`
  with mixed modules, prereleases, peeled/unpeeled pairs, non-parsing
  tags; `libSpec` three spellings and both errors. e2e: the live add
  probes (read ci/e2e.sh for the existing lib add cell) move to the
  per-module tag — they need the lib retagged first, so write the
  cell against `semver/v0.1.0` and say in the report that it goes
  green after the user's retag.

## Assumed, not asked (flag if wrong)

1. Prerelease tags are skipped by a bare add unless nothing stable
   exists.
2. The tag shape carries the `v`: `semver/v0.1.0`, not `semver/0.1.0`.
3. `@v0.1.0` is the user's spelling of the version; the module prefix
   is never typed after `@`.
4. The github shorthand keeps its required ref.
5. Repo-wide tags retire outright; nothing in weir reads them.

## Phases

- **P1 — pure core**: `semverKey`, `latestModuleTag`, `libSpec` with an
  optional tag; unit pins.
- **P2 — the resolver and the add**: the lib arm's list-pick-resolve,
  the printed line, the messages; docs/tooling.md + `--help`; e2e cell.
- **P3 — the library**: README, CONTRIBUTING, `tools/tag-check.weir`
  and the workflow job; lib commit on main. Ledger: CHANGELOG,
  DECISIONS row, this plan BUILT with a session report.

## Session report (2026-10-27)

- P1: `Contracts.semverKey`, `semverCompare`, `latestModuleTag`;
  `LibSpec` is `{ Name; Tag: string option }` (the `Source` field is
  gone — `libSource name ref` builds the shorthand once the ref is
  known, which is what "completed by the resolver" came to). Pins:
  `libSpecTests` (three spellings, the empty `@`, the missing `v`,
  the name rule, pass-through) and `semverTagTests` (§11's two
  sequences sorted and pairwise, build ignored, non-versions refused,
  mixed modules, prereleases, peeled pairs, non-version tags).
- P2: `Contracts.resolveLibSpec` — a sha skips the listing, a version
  resolves `refs/tags/<name>/<tag>` through `pickRef`, a bare name
  takes `latestModuleTag`; the listing fetch is one `listRefs` shared
  with the shorthand's host arm. The add prints `semver: latest is
  semver/v0.1.0` with its other messages (stdout) for a bare add. Usage and `--help` read
  `<name>[@<tag>]`; docs/tooling.md's block matches byte-for-byte.
- The lock records nothing but the raw URL at the sha (no spec or
  tag field exists in `LockEntry`), so the picked tag rides in the
  add's printed line and nowhere else — no lock field was added.
- P3 (lib, committed on main): README Versions, module table and
  examples; CONTRIBUTING's release step; `tools/tag-check.weir`
  (`--tag` or `GITHUB_REF_NAME` via `Env.need`); the `tag` job on
  `*/v*` pushes. `weir tools/test.weir` green.
- Not changed: the runner's missing-vendored teach still spells
  `weir add module <name>@<tag>` (Script.fs; the bare form is now
  the shorter teach — a follow-up).
- Live e2e: the library is not retagged yet, so the positive cells
  (`semver@v0.1.0`, the bare `semver`) fail until `semver/v0.1.0`
  exists; the negative live cells (`nosuch@v0`, `nosuch`) and every
  offline refusal pass.
