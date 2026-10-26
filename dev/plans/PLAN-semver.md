# PLAN: `semver` — the library's first module, ordered by `[<Ord>]` over a normalised prerelease key

**Status: BUILT, then AMENDED (2026-10-26, branch feat/semver; module committed in /output/lib, untagged, unpushed) — built under R1–R8 as recommended; the user's `[<Ord n>]` ruling the same day (PLAN-derived-ord's amendment) re-spelled the key on the members and moved build metadata INTO the record as an unnumbered field, superseding R1. The module and test blocks below are the SHIPPED text; the prose around them is the original plan; the session report and the amendment at the end say what landed. The weir side is docs and ledger only.**

Siblings (parallel branches, planned the same day): PLAN-derived-ord
(feat/derived-ord) owns the `[<Ord>]` attribute; PLAN-lib (feat/lib)
owns the library repo, its runner and `weir add module <name>@<ref>`.
This plan does NOT re-plan either; it names what it needs from each
and gates on their SESSION REPORTS (dev/PROCESS.md, dependency-gate
rule). Neither plan file existed in its worktree when this was
written — the dependency statements below are from the briefs, and
the implementation session re-reads both plans first.

## The receipt

Version strings are the one piece of structured text every release
script reads and none parses correctly: `examples/bump-versions.weir`
reads `<VersionPrefix>(\d+)\.(\d+)\.(\d+)` and bumps with `Str.toInt`
(a textual sort would put `1.10.0` before `1.9.0`); a `deps.weir`-style
consumer reads a major with `^\[?\s*(\d+)` (NuGet range notation, not
semver). The library needs a first module that is small, pure, spec-
shaped and exercises the whole module law (signed exports, `///` docs,
a test script, vendoring) — semver is that module, and it is also the
first consumer of `[<Ord>]` on a user record.

The spec (Semantic Versioning 2.0.0, semver.org): `MAJOR.MINOR.PATCH`,
no leading zeros; an optional `-prerelease` of dot-separated
identifiers (`[0-9A-Za-z-]+`, numeric ones without leading zeros); an
optional `+build` of identifiers (`[0-9A-Za-z-]+`, leading zeros fine)
that NEVER takes part in precedence (§10). Precedence (§11): major,
minor, patch numerically; a prerelease below its release; prerelease
identifiers one by one — numeric compared as numbers and below
alphanumeric, alphanumeric in ASCII order, a shorter list below a
longer one when every shared identifier is equal. The spec's own
example: `1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-alpha.beta < 1.0.0-beta
< 1.0.0-beta.2 < 1.0.0-beta.11 < 1.0.0-rc.1 < 1.0.0`.

## What the code says today (anchors, verified against v0.0.78)

- **Ordering.** `Seq.sort` on strings is ORDINAL: `["b"; "B"; "a";
  "-"; "0"; "."] |> Seq.sort` gives `-` `.` `0` `B` `a` — byte order,
  uppercase first, never the locale (the `ls`/`Dir.list` law). `<` is
  NOT defined on strings (`operator '<' is not defined for string`),
  nor on a union (`… not defined for Pre`), and `Seq.sort` over a union
  refuses with the key list (`int, float, string, bool, Duration, Size,
  Instant, or Uuid`). So every ordered claim on `SemVer` values —
  `Seq.sort`, `<`, `min`/`max` — is PLAN-derived-ord's; today only
  strings sort, which is exactly what the key design leans on.
- **Attributes.** `[<Ord>]` on a union declaration is `unknown
  attribute 'Ord'` (the registry is closed, the position exists);
  `[<Ord>]` on a RECORD declaration is a PARSE error (`Expecting: ';',
  '*', '->' or '}'`) — attributes attach to record FIELDS, union cases
  and union declarations, never to a record declaration. PLAN-derived-
  ord must add that position; this plan's `type SemVer` depends on it.
- **Conversion is lenient; the grammar must gate.** `Str.tryToInt`
  accepts `"007"`, `"+1"` and `" 1"` (all `Some`), refuses 20 digits
  (`None`). Every number in the module is matched by a `Regex` arm
  (`^0$|^[1-9][0-9]{0,17}$`) BEFORE `Str.toInt` touches it.
- **No zero-padding member.** `Str.padLeft w s` pads with SPACES
  (`"   42"`); the key's zero-padding is `Str.replicate (digits -
  Str.length id) "0" + id` [D:port-members].
- **Splitting.** `Str.split "." "1..3"` yields `1`, ``, `3` (empties
  kept — the empty-identifier refusal rides on it); `Str.trySplitOnce
  "-" "1.0.0-alpha-1"` is `Some ("1.0.0", "alpha-1")` (first separator,
  tail intact [D:split-once]) — the prerelease cut; `+` is cut first so
  a `-` inside build metadata stays there.
- **`Regex` arms.** Raw-only literals, check-time compiled, `()` for
  zero captures; `^#0*([0-9]+)$` on `#000` backtracks to `0` (the
  decode of a padded zero). Regex arms never complete a match — every
  arm list ends in `_`.
- **Modules.** [D:module-signatures]: a signature line exports, `///`
  docs live on it, `type` declarations auto-export (so `Pre` and
  `SemVer` are public by law — R4), unsigned members are private, an
  unused private member is an error, an imported union's cases are
  matched bare in the importer (`| Pre k ->` on `v.pre` works) and
  never collide. A module file named `semver.weir` declaring `module
  SemVer` imports as `SemVer` by default; the type `SemVer` inside
  module `SemVer` is unambiguous (types and modules are different
  namespaces; a sig names the type bare). Verified: the test script
  below imports it as `SemVer` and calls `SemVer.parse`.
- **`|> try`.** `parse s |> try` is `Option<SemVer>` (None on any
  raise) [D:try-form]; `|> try result` is `Result<_, string>` whose
  `Error` carries the BARE `fail` message (`Error "empty version"`, no
  location prefix) — the refusal pins compare against it.
- **Purity.** `let pure f x = …` is the binding spelling of the purity
  assertion [D:pure-stage1]; it is accepted on a SIGNED member's impl
  (verified: `let g : int -> int` + `let pure g n = n + 1` checks).
  `fail` is pure. ONE CHECKER BUG, found here: a `function | Regex …
  ->` lambda inside a `let pure` body is refused as `this 'pure' block
  forbids effects, but this runs a command` (the `fun x -> match x
  with` spelling of the same lambda passes). Logged under
  dev/NOTES-agent.md `## friction`; the module uses the passing
  spelling (R7).
- **`--can` on a pure module** is not empty: it lists every `fail`
  site under `mutations: terminates: fail × N`. The purity pin is
  therefore "no `fs`/`proc`/`net`/`env`/clock line — only
  `terminates`", not "reports nothing" (the brief's wording corrected).
- **Vendoring.** docs/tooling.md "Remote modules": `weir add module
  <host>//<path>@<ref> --as <name>`, `import "weir:<name>" as M`; a
  vendored file may not `weir:`-import (so the module is one self-
  contained file); there is NO local-path form of `add module`, so a
  doc-tested page cannot vendor offline. ci/skill-doc.sh EXECUTES the
  `weir` fences of SKILL.md, GUIDE.md, COMING-FROM.md, FAQ.md and
  docs/reference/*.md; docs/tooling.md is not in that list.

## The shape

### Types

```
[<Ord>] type Pre = Pre of string | Release          // Pre first: a prerelease sorts below its release
[<Ord>] type SemVer = { major: int; minor: int; patch: int; pre: Pre }
```

Derived order (PLAN-derived-ord): records lexicographic by field
declaration order, unions by case index then payload. So `SemVer`
orders by major, minor, patch, then `pre` — `Pre k` (index 0) below
`Release` (index 1), two `Pre`s by their string payload. The spec's
numeric/alphanumeric/length rules cannot be expressed by string
order on the TEXT, so `Pre` carries a KEY whose ordinal order IS the
spec's order; `toString` decodes it back. Build metadata is not a
field: equal versions with different build metadata are `==` (the
spec says they have equal precedence) and `SemVer.build` reads it from
the text (R1).

### The key, byte by byte

Three marker bytes, chosen so the proof needs two facts only:

| byte | role | value |
|---|---|---|
| `,` (0x2C) | identifier separator | below every identifier byte — `-` (0x2D) is the lowest of `[0-9A-Za-z-]` |
| `#` (0x23) | numeric identifier marker | below the word marker |
| `@` (0x40) | alphanumeric identifier marker | above the number marker |

`key(pre) = ident(id₁) , ident(id₂) , …` where `ident(numeric) = "#" +
zero-pad-to-18(id)` and `ident(alphanumeric) = "@" + id` (verbatim).
Each identifier is matched first, so a numeric identifier is
`^0$|^[1-9][0-9]{0,17}$` exactly — a leading zero or a 19th digit
refuses at parse (R3).

Why ordinal order on keys is the spec's order (§11.4):

1. **Two keys first differ at a marker, a body byte, a separator, or
   an end.** Positions align: every key starts with a marker; a
   marker only ever faces a marker (after a separator, both keys hold
   a marker; after a body, a shorter body faces `,` or end, never a
   marker — because numeric bodies are all 18 wide and an
   alphanumeric body that is a prefix of another is followed by `,`
   or end).
2. **Numeric below alphanumeric (§11.4.3):** decided at the marker,
   `#` 0x23 < `@` 0x40, whatever the bodies.
3. **Numeric vs numeric (§11.4.1):** same width, zero-padded — byte
   order on 18 digits is numeric order.
4. **Alphanumeric vs alphanumeric (§11.4.2):** verbatim ASCII, ordinal
   = "lexically in ASCII sort order"; a prefix is smaller — and when
   `alpha` is followed by `,` (next identifier) while the other key
   continues `alpha-1`, the `,` 0x2C < `-` 0x2D rule puts `alpha.1`
   below `alpha-1`, which is the spec (`alpha` < `alpha-1` as first
   identifiers). THIS is why the separator cannot be `.` (0x2E > `-`):
   with `.`, `alpha.1` would sort ABOVE `alpha-1`.
5. **Fewer identifiers below (§11.4.4):** equal shared identifiers
   means the shorter key is a prefix of the longer; a prefix sorts
   first.
6. **Prerelease below release:** not in the string — `Pre` is case
   0, `Release` case 1, so the union's derived order places every
   `Pre _` below `Release`.

The spec's list, keyed (verified: `Seq.sort` of these eight strings
returns them in this order — the executable form of the proof is in
the test, `Seq.sort` over parsed records):

| text | key | first difference from the row above |
|---|---|---|
| `alpha` | `@alpha` | — |
| `alpha.1` | `@alpha,#000000000000000001` | longer, prefix equal (5) |
| `alpha.beta` | `@alpha,@beta` | col 7: `#` < `@` (2) |
| `beta` | `@beta` | col 1: `a` < `b` (4) |
| `beta.2` | `@beta,#000000000000000002` | longer, prefix equal (5) |
| `beta.11` | `@beta,#000000000000000011` | col 23: `0` < `1` (3) |
| `rc.1` | `@rc,#000000000000000001` | col 1: `b` < `r` (4) |
| (release) | `Release`, case 1 | the union's case order (6) |

The key is injective (markers and padding decode uniquely), so equal
keys mean equal texts, `==` on `SemVer` is text equality modulo
build, and `toString` recovers the text exactly: `#0*([0-9]+)` strips
the padding, `@(.*)` strips the marker, `Str.join "."` restores the
dots.

### The width limit (R3): 18 digits

`int` is 64-bit; `Str.toInt` holds every 18-digit number and not every
19-digit one (int64's max is 19 digits, `9223372036854775807`). One
law for the core numbers AND the prerelease numerics: at most 18
digits, refused at parse naming the limit (`major
'1234567890123456789' is longer than 18 digits`). Real prerelease
numerics are counters and timestamps (`20130313144700` is 14 digits).
The `{0,17}` in the regex literals duplicates `digits = 18` (a regex
literal is check-time; nothing splices into it) — pinned by the
18-parses / 19-refuses pair in the test.

### Members (all signed, all `(pure)`)

| member | type | law |
|---|---|---|
| `parse` | `string -> SemVer` | raises naming the part and the identifier; build validated and dropped |
| `tryParse` | `string -> Option<SemVer>` | `parse s \|> try` — one grammar, by construction |
| `toString` | `SemVer -> string` | `toString (parse s) == s` for every `s` without build |
| `build` | `string -> Option<string>` | the `+build` text; the text must parse (R1) |
| `isPrerelease` | `SemVer -> bool` | |
| `core` | `SemVer -> SemVer` | the prerelease dropped — "the release this precedes or is" |
| `bumpMajor`/`bumpMinor`/`bumpPatch` | `SemVer -> SemVer` | lower parts reset, prerelease dropped (R5) |

No `key` (R6), no `leadingMajor` (R2), no ranges/`satisfies`
(non-goal). The strict spelling of "the major of a version string" is
`SemVer.tryParse s |> Option.map _.major`.

**Refusal messages** (each a `fail`, each byte-pinned in the test):
`empty version`; `'<text>' is not MAJOR.MINOR.PATCH`; `<part> '<s>' is
not a number` / `has a leading zero` / `is longer than 18 digits` for
`major`/`minor`/`patch`; `prerelease: empty identifier`; `prerelease:
numeric identifier '<id>' has a leading zero` / `is longer than 18
digits`; `prerelease: identifier '<id>' has a character outside
[0-9A-Za-z-]`; `build: empty identifier`; `build: identifier '<id>' has
a character outside [0-9A-Za-z-]`. The first problem found is the one
named, left to right: core, then prerelease, then build.

**Purity.** Every impl is `let pure` — the checker refuses the module
if an effect ever reaches a member (the pin that cannot drift), and
every member carries the `(pure)` hover badge. `weir check --can
semver.weir` lists `terminates: fail × 12` and NOTHING else (no `fs`,
`proc`, `net`, `env`, clock) — the receipt the lib runner can grep.

### Where the files live

- `/output/lib` (PLAN-lib's repo, local path): `semver.weir` and
  `semver.test.weir` beside it, at whatever layout PLAN-lib fixes
  (the test imports `"./semver.weir"` — a relative sibling, which the
  layout must keep). Vendored as `weir add module semver@v0.1.0`
  (PLAN-lib's short form), imported as `import "weir:semver" as
  SemVer`.
- This repo (feat/semver): this plan, the dev/plans/README.md index
  line, the DECISIONS row, the NOTES friction line, and the docs
  example in **docs/tooling.md** (PLAN-lib's "The library"
  subsection under "Remote modules"), as `text` fences — tooling.md is
  not in ci/skill-doc.sh's DOCS, and a `weir` fence there would LOOK
  executed; GUIDE.md and SKILL.md fences DO execute, and `weir add
  module` has no local-path form, so no doc-tested page can vendor
  the module offline. The executed twin of the example is
  `semver.test.weir` in the lib repo (same calls, same expected
  strings).

## The module (`semver.weir`)

Checked today with the two `[<Ord>]` lines removed (scratch copy under
/output/tmp/semver-scratch): `weir check` clean, `weir fmt --check`
clean, `--can` = `terminates: fail × 12` only, and the test below runs
green against it under the stop-gap. With the attribute lines present
it refuses today (`unknown attribute 'Ord'` at the union, the parse
error at the record) — the dependency, stated.

```weir
module SemVer

// Semantic Versioning 2.0.0 (semver.org), precedence included. Pure:
// no file, command, env or clock reaches any member.

/// The prerelease. `Pre` carries the ORDER KEY built by `parse` (not the
/// text — `toString` decodes it); `Release` is no prerelease. Numbered so
/// a prerelease sorts below its release.
type Pre =
    | [<Ord 1>] Pre of string
    | [<Ord 2>] Release

/// A parsed version. Ordered by major, minor, patch, then the prerelease
/// key — the spec's precedence. `build` is unnumbered: build metadata
/// is ignored when determining precedence (§10), so two versions that
/// differ only in build metadata have the same precedence — neither is
/// `<` the other and `Seq.sort` keeps their input order — but they are
/// not `==`: equality is whole-value.
type SemVer = {
    [<Ord 1>] major: int
    [<Ord 2>] minor: int
    [<Ord 3>] patch: int
    [<Ord 4>] pre: Pre
    build: Option<string>
}

// the key alphabet [D:lib-semver]: every marker sorts below every
// identifier byte (`-` 0x2D is the lowest), and the number marker sorts
// below the word marker, so ordinal order on keys is the spec's order
let digits = 18
let sep = ","
let numMark = "#"
let wordMark = "@"

// one identifier -> its key, or the refusal naming the identifier
let pure identKey what id =
    match id with
    | "" -> fail $"{what}: empty identifier"
    | Regex @"^0$|^[1-9][0-9]{0,17}$" () -> numMark + Str.replicate (digits - Str.length id) "0" + id
    | Regex @"^[0-9]+$" () ->
        if Str.startsWith "0" id then fail $"{what}: numeric identifier '{id}' has a leading zero"
        else fail $"{what}: numeric identifier '{id}' is longer than {digits} digits"
    | Regex @"^[0-9A-Za-z-]+$" () -> wordMark + id
    | _ -> fail $"{what}: identifier '{id}' has a character outside [0-9A-Za-z-]"

let pure preKey text =
    text |> Str.split "." |> Seq.map (identKey "prerelease") |> Str.join sep

// a key -> the text it was built from
let pure preText key =
    key
    |> Str.split sep
    |> Seq.map (fun k ->
        match k with
        | Regex @"^#0*([0-9]+)$" n -> n
        | Regex @"^@([0-9A-Za-z-]+)$" w -> w
        | _ -> fail $"not a prerelease key: '{k}'")
    |> Str.join "."

let pure coreNum what s =
    match s with
    | Regex @"^0$|^[1-9][0-9]{0,17}$" () -> Str.toInt s
    | Regex @"^[0-9]+$" () ->
        if Str.startsWith "0" s then fail $"{what} '{s}' has a leading zero"
        else fail $"{what} '{s}' is longer than {digits} digits"
    | _ -> fail $"{what} '{s}' is not a number"

let pure buildCheck text =
    text
    |> Str.split "."
    |> Seq.iter (fun id ->
        if id == "" then fail "build: empty identifier"
        elif not (Str.isMatch @"^[0-9A-Za-z-]+$" id) then fail $"build: identifier '{id}' has a character outside [0-9A-Za-z-]")

// the text's three parts: core, prerelease (after the first `-`), build
// (after the first `+`) — `+` is cut first, so a `-` inside build stays
let pure parts s =
    let (version, build) =
        match Str.trySplitOnce "+" s with
        | Some (v, b) -> (v, Some b)
        | None -> (s, None)
    let (core, pre) =
        match Str.trySplitOnce "-" version with
        | Some (c, p) -> (c, Some p)
        | None -> (version, None)
    (core, pre, build)

let pure core3 text =
    match Str.split "." text with
    | [a; b; c] -> (coreNum "major" a, coreNum "minor" b, coreNum "patch" c)
    | _ -> fail $"'{text}' is not MAJOR.MINOR.PATCH"

/// Parse a version; raises naming what is wrong — the part first (core,
/// then prerelease, then build), then the identifier.
let parse : string -> SemVer

let pure parse s =
    if s == "" then fail "empty version"
    let (c, p, b) = parts s
    let (major, minor, patch) = core3 c
    b |> Option.iter buildCheck
    let pre = match p with | Some t -> Pre (preKey t) | None -> Release
    { major = major; minor = minor; patch = patch; pre = pre; build = b }

/// `parse` as an Option: `None` for anything `parse` refuses.
let tryParse : string -> Option<SemVer>

let pure tryParse s = parse s |> try

/// Render `MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]`; `toString (parse s) == s`.
let toString : SemVer -> string

let pure toString v =
    let core = $"{v.major}.{v.minor}.{v.patch}"
    let tagged =
        match v.pre with
        | Release -> core
        | Pre k -> $"{core}-{preText k}"
    match v.build with
    | Some b -> $"{tagged}+{b}"
    | None -> tagged

/// True when the version has a prerelease.
let isPrerelease : SemVer -> bool

let pure isPrerelease v =
    match v.pre with
    | Pre _ -> true
    | Release -> false

/// The release this version precedes or is: the prerelease and the
/// build metadata dropped.
let core : SemVer -> SemVer

let pure core v = { v with pre = Release; build = None }

/// Next major: minor and patch reset, prerelease and build metadata
/// dropped.
let bumpMajor : SemVer -> SemVer

let pure bumpMajor v = { major = v.major + 1; minor = 0; patch = 0; pre = Release; build = None }

/// Next minor: patch reset, prerelease and build metadata dropped.
let bumpMinor : SemVer -> SemVer

let pure bumpMinor v = { v with minor = v.minor + 1; patch = 0; pre = Release; build = None }

/// Next patch: prerelease and build metadata dropped.
let bumpPatch : SemVer -> SemVer

let pure bumpPatch v = { v with patch = v.patch + 1; pre = Release; build = None }
```

**The stop-gap, if PLAN-derived-ord has not landed** (R6 — not
shipped otherwise): append this signed member, and the test's three
Ord-dependent sites become `Seq.sortBy SemVer.key`, `Seq.minBy`/
`maxBy SemVer.key`, and the `<` check becomes "the two keys differ and
`Seq.sort` of the pair puts `a`'s first". Verified green today in that
form.

```weir
/// STOP-GAP until [<Ord>] lands: a string whose ordinal order is the
/// version's precedence, for `Seq.sortBy`/`minBy`/`maxBy`.
let key : SemVer -> string

let pure key v =
    let pad n =
        let s = $"{n}"
        Str.replicate (digits - Str.length s) "0" + s
    let tail = match v.pre with | Pre k -> "-" + k | Release -> "~"
    $"{pad v.major}.{pad v.minor}.{pad v.patch}{tail}"
```

(`-` 0x2D below `~` 0x7E puts a prerelease below its release; the
three padded numbers decide before the tail.)

## The test (`semver.test.weir`)

A plain script: exit 0 is the pass, the first mismatch `fail`s with
`name: got …, want …`. The runner contract assumed from PLAN-lib's
brief: run every `*.test.weir`, nonzero exit = failure — if the
runner wants a different shape (a per-case report), the `expect`
helper is the one seam to change. The ordering section is the
spec's list sorted with `Seq.sort`, `<` across every adjacent pair,
`min`/`max` — all PLAN-derived-ord's; the rest runs today.

```weir
#!/usr/bin/env weir
// tests/semver.weir — the library runner runs it; exit 0 is the pass
import "../semver.weir" as SemVer

let expect name got want =
    if got <> want then fail $"{name}: got {got}, want {want}"

// parse and render round-trip, build metadata included
let table = [
    "0.0.0"
    "1.2.3"
    "10.20.30"
    "1.0.0-alpha"
    "1.0.0-alpha.1"
    "1.0.0-0.3.7"
    "1.0.0-x.7.z.92"
    "1.0.0-x-y-z.--"
    "1.0.0-alpha-a.b-c-somethinglong"
    "1.0.0-rc.1"
    "999999999999999999.0.0"
    "1.0.0-123456789012345678"
    "1.0.0+20130313144700"
    "1.0.0-alpha+001"
    "1.0.0-beta+exp.sha.5114f85"
    "1.2.3-rc.1+exp.sha"
]
for s in table do expect $"round-trip {s}" (SemVer.toString (SemVer.parse s)) s

let v = SemVer.parse "1.2.3-beta.4"
expect "major" v.major 1
expect "minor" v.minor 2
expect "patch" v.patch 3
expect "isPrerelease" (SemVer.isPrerelease v) true
expect "isPrerelease release" (SemVer.isPrerelease (SemVer.parse "1.2.3")) false

// build metadata: kept in the value, ignored for precedence (§10) — the
// same precedence, not the same value
expect "build" (SemVer.parse "1.0.0+20130313144700").build (Some "20130313144700")
expect "build with dash" (SemVer.parse "1.0.0-beta+exp.sha.5114f85").build (Some "exp.sha.5114f85")
expect "no build" (SemVer.parse "1.0.0").build None
let ba = SemVer.parse "1.0.0+a"
let bb = SemVer.parse "1.0.0+b"
expect "same precedence: not <" (ba < bb) false
expect "same precedence: not >" (bb < ba) false
expect "same precedence: <=" (ba <= bb && bb <= ba) true
expect "not ==" (ba == bb) false
expect "sort keeps input order" ([bb; ba] |> Seq.sort |> Seq.map SemVer.toString |> Str.join " ") "1.0.0+b 1.0.0+a"
expect "min is the first" (Seq.min [bb; ba] |> SemVer.toString) "1.0.0+b"

// precedence: the spec's own list (semver.org §11), ascending
let spec = [
    "1.0.0-alpha"
    "1.0.0-alpha.1"
    "1.0.0-alpha.beta"
    "1.0.0-beta"
    "1.0.0-beta.2"
    "1.0.0-beta.11"
    "1.0.0-rc.1"
    "1.0.0"
]
let versions = spec |> Seq.map SemVer.parse |> Seq.freeze
let shuffled = [versions[7]; versions[2]; versions[5]; versions[0]; versions[6]; versions[1]; versions[4]; versions[3]]
let sorted = shuffled |> Seq.sort |> Seq.map SemVer.toString
if not (Seq.equal sorted spec) then fail $"sort: {sorted}"
versions
    |> Seq.pairwise
    |> Seq.iter (fun (a, b) -> if not (a < b) then fail $"order: {SemVer.toString a} < {SemVer.toString b}")
expect "min" (Seq.min versions |> SemVer.toString) "1.0.0-alpha"
expect "max" (Seq.max versions |> SemVer.toString) "1.0.0"

// the core orders numerically; a prerelease sits below ITS release only
let more =
    ["2.0.0-0"; "1.10.0"; "1.0.1-alpha"; "1.0.0"; "1.9.0"; "1.0.0-rc.1"; "1.0.0-alpha.1"; "1.0.0-alpha-1"]
    |> Seq.map SemVer.parse
    |> Seq.sort
    |> Seq.map SemVer.toString
let moreWant = ["1.0.0-alpha.1"; "1.0.0-alpha-1"; "1.0.0-rc.1"; "1.0.0"; "1.0.1-alpha"; "1.9.0"; "1.10.0"; "2.0.0-0"]
if not (Seq.equal more moreWant) then fail $"sort: {more}"

// bumps clear the lower parts, the prerelease and the build metadata;
// core is the release
let pre = SemVer.parse "1.2.3-rc.1+exp.sha"
expect "bumpMajor" (SemVer.toString (SemVer.bumpMajor pre)) "2.0.0"
expect "bumpMinor" (SemVer.toString (SemVer.bumpMinor pre)) "1.3.0"
expect "bumpPatch" (SemVer.toString (SemVer.bumpPatch pre)) "1.2.4"
expect "core" (SemVer.toString (SemVer.core pre)) "1.2.3"
expect "core of release" (SemVer.core (SemVer.parse "1.2.3")) (SemVer.parse "1.2.3")

// refusals, byte-pinned; tryParse is None for each
let refuses s want =
    match SemVer.parse s |> try result with
    | Ok v -> fail $"'{s}' parsed as {SemVer.toString v}; want the refusal: {want}"
    | Error msg -> expect $"refusal '{s}'" msg want
    expect $"tryParse '{s}'" (SemVer.tryParse s) None
refuses "" "empty version"
refuses "1.2" "'1.2' is not MAJOR.MINOR.PATCH"
refuses "1.2.3.4" "'1.2.3.4' is not MAJOR.MINOR.PATCH"
refuses "v1.2.3" "major 'v1' is not a number"
refuses "1.2.3 " "patch '3 ' is not a number"
refuses "01.2.3" "major '01' has a leading zero"
refuses "1.02.3" "minor '02' has a leading zero"
refuses "1.2.03" "patch '03' has a leading zero"
refuses "1.2.-3" "patch '' is not a number"
refuses "1.2.3-" "prerelease: empty identifier"
refuses "1.2.3-alpha..1" "prerelease: empty identifier"
refuses "1.2.3-alpha.01" "prerelease: numeric identifier '01' has a leading zero"
refuses "1.2.3-al_pha" "prerelease: identifier 'al_pha' has a character outside [0-9A-Za-z-]"
refuses "1.2.3-ä" "prerelease: identifier 'ä' has a character outside [0-9A-Za-z-]"
refuses "1.2.3+" "build: empty identifier"
refuses "1.2.3+a..b" "build: empty identifier"
refuses "1.2.3+a_b" "build: identifier 'a_b' has a character outside [0-9A-Za-z-]"
// the width limit: 18 digits parse, 19 refuse — core and prerelease alike
refuses "1234567890123456789.0.0" "major '1234567890123456789' is longer than 18 digits"
refuses "1.0.0-1234567890123456789" "prerelease: numeric identifier '1234567890123456789' is longer than 18 digits"
expect "18-digit prerelease" (SemVer.tryParse "1.0.0-123456789012345678" |> Option.map SemVer.toString) (Some "1.0.0-123456789012345678")
expect "tryParse ok" (SemVer.tryParse "1.2.3" |> Option.map SemVer.toString) (Some "1.2.3")

print "semver: ok"
```

## Phases

- **P0 — gate (minutes).** Read PLAN-derived-ord's and PLAN-lib's
  SESSION REPORTS. Confirm: `[<Ord>]` on a RECORD declaration parses
  and derives; union payload strings compare ORDINALLY under the
  derived order (the `Seq.sort`-on-strings law, never a locale); `<`,
  `Seq.sort`, `min`/`max` admit a derived type; the lib repo exists at
  /output/lib with its layout and runner, and `weir add module
  semver@v0.1.0` resolves to it. Any miss → stop-and-report, or ship
  under the stop-gap (R6) if only Ord is missing.
- **P1 — the module (½ session).** Copy the source above into the lib
  repo verbatim (it is the checked artefact, not a sketch); `weir
  check`, `weir fmt --check`, `weir check --can` = `terminates` only.
- **P2 — the test (same session).** Copy the test above beside it;
  run under the lib runner; every pin green. If the runner contract
  differs from "exit 0 = pass", adapt `expect` only.
- **P3 — this repo (same session).** DECISIONS row `lib-semver` (text
  below); the docs/tooling.md "The library" example (text fences);
  the README index line flips to EXECUTED with the session report;
  CHANGELOG entry under the lib's own version if PLAN-lib keeps one
  (the weir binary is unchanged — no weir CHANGELOG line).
- **P4 — vendoring receipt (same session).** In a scratch project:
  `weir add module semver@v0.1.0`, a three-line script that imports
  `weir:semver`, parses two tags and prints the `Seq.max` — the
  docs example's own lines, run once by hand (it cannot run in CI:
  network). The session report records the output.

## Tests

All in `semver.test.weir` (the lib's runner is the harness; nothing
in tests/Weir.Tests or ci/e2e.sh — the weir binary does not change):

- Round-trip table (12 texts, the spec's §9 examples among them),
  `toString (parse s) == s`.
- Field reads; `isPrerelease` both ways.
- Build: dropped from the value, read by `build` (with a `-` inside,
  with digits-only), `None` when absent, `==` modulo build.
- The spec's list: a shuffled copy `Seq.sort`s back to it; `<` over
  every adjacent pair; `Seq.min`/`Seq.max`. A second list proves the
  numeric core order (`1.9.0 < 1.10.0`), prerelease-below-its-release-
  only (`1.0.0 < 1.0.1-alpha`), `alpha.1 < alpha-1` (the separator
  rule) and `2.0.0-0`.
- Bumps and `core`, from a prerelease.
- Refusals byte-pinned (19 texts): empty, wrong arity, non-number,
  trailing space, leading zeros in each core part, empty core part,
  empty / leading-zero / bad-character / non-ASCII prerelease
  identifiers, empty / bad-character build identifiers, the 19-digit
  limit in core and prerelease; `tryParse` is `None` for each and
  `Some` for the 18-digit and plain cases.
- Purity: `let pure` on every impl (the module refuses to check if
  an effect appears) and `--can`'s `terminates`-only report, grepped
  by the lib runner if PLAN-lib's runner has a `--can` step
  (otherwise recorded in the session report).

## Perf

Nothing to measure: every member is a handful of regex matches over a
string shorter than a line; the only forcing members are `Seq.sort`
on the caller's side. The regex timeout [D:regex-timeout] cannot fire
on any input under 1M chars; `tryParse` of a megabyte string returns
`None` by that timeout (the one way `tryParse` and "malformed" differ).

## Risks

1. **The derived Ord compares strings by something other than
   ordinal** (a locale, a case fold). Mitigation: P0 confirms; the
   `beta.2 < beta.11` and `alpha.beta < beta` pins fail loudly if not.
2. **PLAN-derived-ord lands union-only** (the record position is a
   parse error today). Mitigation: P0; the stop-gap `key` (R6).
3. **`{0,17}` drifts from `digits`.** Mitigation: the 18/19 pins.
4. **The `function`-under-`let pure` bug** bites a future edit that
   reintroduces the spelling. Mitigation: the friction line names it;
   the module's check refuses, which is the right failure.
5. **A hand-built `Pre "text"`** (the key field is public, R4) renders
   through `preText`, which `fail`s on a non-key (`not a prerelease
   key: '…'`) — loud, never a silent wrong order.
6. **The lib runner's contract differs** from exit-0 = pass.
   Mitigation: `expect` is the one seam; P2 adapts.
7. **`tryParse` swallows every raise** (`|> try`), so a bug in the
   module would read as "malformed". Mitigation: `parse` is the
   tested path (byte-pinned messages); `tryParse` is pinned `None`
   only where `parse`'s message is pinned.

## Non-goals

- Ranges, `satisfies`, caret/tilde operators, NuGet/npm range notation
  (the `^\[?\s*(\d+)` read stays in its consumer — R2).
- Leniency of any kind: a leading `v`, whitespace, `1.2` as `1.2.0`
  (the consumer slices or `Str.trim`s before `parse`).
- A `compare` member (`<`/`Seq.sort` are the spelling once Ord lands).
- Ordering build metadata (the spec forbids it).
- Any change to the weir binary, SKILL.md, or ci/ — the `function`
  purity bug is logged, not fixed here.
- Doc-tested examples that vendor (no offline `add module`).

## Rulings

- **R1 — build metadata home.** (a) `parse` validates and DROPS it;
  `build : string -> Option<string>` reads it from the text. (b)
  `parseFull : string -> SemVer * Option<string>`. (c) a wrapper record
  `{ version: SemVer; build: Option<string> }`. *Recommend (a)* — the
  common consumer compares and bumps and never reads build; (b)
  makes every consumer destructure a pair; (c) mints an unordered
  record whose `==` differs from `SemVer`'s (equal precedence,
  unequal values), the spec's own trap. `build` re-parses the text
  (validation shared by construction) — the cost is one extra parse
  of a 20-byte string on the rare path.
- **R2 — the lenient major read.** Ship `SemVer.leadingMajor : string
  -> Option<int>` (optional `[`, whitespace, the first digit run), or
  leave it out? *Recommend: out* — that regex reads NuGet RANGE
  notation (`[1.2.3, 2.0.0)`), a non-goal's syntax, and a strict
  module offering a lenient reader invites it where `parse` was meant
  (reject-don't-guess). The consumer keeps its one-line `Regex` arm;
  the strict spelling is `SemVer.tryParse s |> Option.map _.major`.
- **R3 — the width limit.** 18 digits (every value an `int` holds, one
  law for core and prerelease) vs 19 (int64's digit count, but
  `9999999999999999999` overflows) vs unbounded for prerelease
  numerics (padding width unbounded = no fixed-width key).
  *Recommend 18.*
- **R4 — is `Pre` public?** It is, by law (`type` declarations auto-
  export; there is no private type). The choice is the NAME: `Pre of
  string` (the brief; reads naturally in `match v.pre with | Pre _`)
  vs `PreKey of string` (self-describing payload). *Recommend `Pre`*
  with the `///` doc stating the payload is the key, `toString` as the
  decoder, and `preText`'s `fail` on a non-key as the guard — the
  importer's natural use is `isPrerelease`/`match`, never
  construction.
- **R5 — bumps from a prerelease.** Arithmetic (`bumpPatch 1.2.3-rc.1
  = 1.2.4`, the brief) vs npm's `inc` (`1.2.3`: a prerelease's next
  patch is its own release). *Recommend arithmetic* — one rule, no
  special case; `core` is the explicit "its release" spelling, one
  call away.
- **R6 — the stop-gap `key`.** Ship `key : SemVer -> string` if
  PLAN-derived-ord has not landed when the module ships; otherwise
  never. *Recommend: sequence so it never ships* — a lib module is
  versioned, and removing a signed member later is a breaking change
  for a one-session gap. If it must ship, it stays (a `sortBy` key is
  harmless beside `Seq.sort`).
- **R7 — the `function`-under-`let pure` checker bug.** The module
  spells `fun k -> match k with` (passes); the bug goes to NOTES
  friction and gets its own one-line fix wherever the purity walk
  misreads the `function` form as a command head — in weir, not in
  this plan. *Recommend exactly that.*
- **R8 — the purity pin.** `let pure` on every impl (checker-enforced,
  zero cost, survives any edit) plus `--can`'s `terminates`-only
  report as the receipt — vs the hover badge alone (display-only,
  nothing gated). *Recommend both; the badge is the by-product.*

## DECISIONS row

`| lib-semver | 2026-10-26 | THE LIBRARY'S FIRST MODULE IS `semver`,
ORDERED BY `[<Ord>]` OVER A NORMALISED PRERELEASE KEY — `[<Ord>] type
Pre = Pre of string | Release` (case order = a prerelease below its
release) and `[<Ord>] type SemVer = { major; minor; patch; pre }`
(declaration order = the spec's precedence); `Pre` carries a KEY, not
the text: identifiers joined by `,` (0x2C, below every identifier
byte — `.` would put `alpha.1` above `alpha-1`), numerics as `#` +
18-digit zero-pad, alphanumerics as `@` + verbatim, so ordinal order
on keys is semver §11's order (proved on the spec's list; `toString`
decodes). 18-digit limit on every number (core and prerelease — every
value an int holds), refused at parse naming it. Build metadata is
NOT a field (equal precedence = `==`): `parse` validates and drops it,
`build` reads it. Every member `let pure`; `--can` = `terminates`
only. No ranges, no leniency (`leadingMajor` declined: the `^\[?\s*`
read is NuGet range notation), no `key` member unless Ord is late.
Found on the way: `[<Ord>]` on a record declaration is a PARSE error
today (the Ord plan adds the position); `function` inside `let pure`
is misjudged as a command (friction). | dev/plans/PLAN-semver.md |`

## Docs touched

- dev/plans/README.md — the index line (this commit).
- dev/NOTES-agent.md `## friction` — the `function`/`let pure` line
  (this commit).
- docs/DECISIONS.md — the `lib-semver` row (P3).
- docs/tooling.md — "The library" (PLAN-lib's subsection): the semver
  example in `text` fences, P3:

```text
weir add module semver@v0.1.0
```

```text
import "weir:semver" as SemVer

let strip t = if Str.startsWith "v" t then t[1..] else t
let latest =
    git tag
    |> Seq.map strip
    |> Seq.choose SemVer.tryParse
    |> Seq.where (fun v -> not (SemVer.isPrerelease v))
    |> Seq.max
print (SemVer.toString (SemVer.bumpMinor latest))
```

  (`strip` is the consumer's leniency — the `v` of a git tag — named
  where it lives; the module has none. `Str.trimStart` takes no
  argument, whitespace only, so the slice is the spelling.)

## Effort

½ session after both gates (the module and test are the checked
artefacts above — P1/P2 are copy, run, read the runner's output);
+½ session if the stop-gap ships (R6) or the runner contract differs
(P2). P0 is the reality check the dependency-gate rule demands.

## Session report (2026-10-26, feat/semver)

Executed under R1–R8 as recommended, on the feat/semver worktree
(stacked on feat/lib on feat/derived-ord) with the binary published
from it (`0.0.0-dev+c9e15ff`); the shipped v0.0.78 on PATH was used
only to confirm the dependency (`weir check semver.weir` there:
`9:3 unknown attribute 'Ord'`, `20:5 Expecting: ';', '*', '->' or '}'`).

**P0 — gate.** Both session reports read. `[<Ord>]` sits on its own
line above the union and above the multi-line record (the form
PLAN-derived-ord's assembler fix admits); union payload strings order
ordinally (the `beta.2 < beta.11`, `alpha.beta < beta` and
`alpha.1 < alpha-1` pins hold); `<`, `Seq.sort`, `Seq.pairwise`+`<`,
`Seq.min`/`max` all admit the record. The library is /output/lib
(`main`): modules at the ROOT as `<name>.weir`, tests under
`tests/<name>.weir` importing `"../<name>.weir"`, `tools/test.weir`
the runner (exit 0 = pass — the plan's assumed contract, so `expect`
needed no change).

**P1/P2 — the library** (`/output/lib`, one commit `d991ca0` as queil,
`semver: the first module — parse, order, bump`; no tag, no push):
`semver.weir` (153 lines: `[<Ord>] Pre`, `[<Ord>] SemVer`, the four
key constants, seven private helpers, nine signed `///`-documented
members: `parse`, `tryParse`, `toString`, `build`, `isPrerelease`,
`core`, `bumpMajor`, `bumpMinor`, `bumpPatch` — every impl `let pure`),
`tests/semver.weir` (the plan's test; `Seq.sort`/`<`/`min`/`max` over
the derived order, 12 round-trips, 19 byte-pinned refusals), and the
README row (`semver | v0.1.0 | …`, CONTRIBUTING's PR rule). Verbatim:

- `weir check semver.weir` → rc 0; `weir fmt --check semver.weir` → rc 0;
  `weir check --can semver.weir` → `mutations: terminates: fail × 12
  semver.weir:36:13, 39:39, 40:14, 42:12, 55:16, 62:38, 63:14, 64:12,
  70:26, 71:59, 89:12, 96:21` and nothing else (R8's receipt).
- `weir check tests/semver.weir` → rc 0; `weir fmt --check
  tests/semver.weir` → rc 0; `weir tests/semver.weir` → `semver: ok`, rc 0.
- `weir tools/test.weir` (from the root and from /tmp) → `weir
  0.0.0-dev+c9e15ff`, six `ok` lines (check/fmt/self-contained/test-for
  on the module, check/run on the test), `ok: 1 modules`, rc 0.
- The `(pure)` badge, read through `weir lsp` (a stdio JSON-RPC
  `textDocument/hover` on each signature line): all nine members hover
  as `<name> : <sig>  (pure)`; `Pre` hovers `[<Ord>] Pre of string |
  Release` and `SemVer` `[<Ord>] { major: int; minor: int; patch: int;
  pre: Pre }`, each followed by its `///` doc.

**P4 — the vendoring proof, offline.** The plan's P4 wanted `weir add
module semver@v0.1.0` in a scratch project; the tag does not exist, so
the same path ran through the raw-root form the e2e battery uses: a
`python3 -m http.server` over /output/lib, `git init`, then `weir add
module http://127.0.0.1:<port>/ --path semver.weir --as semver` →
`added module semver (12f0656c9346…, 11 member(s)) from
http://127.0.0.1:<port>/semver.weir` / `import it:  import "weir:semver"
as Semver`; the lock holds the URL and sha256 at schemaVersion 1. In
that project: the docs/tooling.md example VERBATIM (`git tag` over
`v1.9.0 v1.10.0 v1.11.0-rc.1 junk`) → `1.11.0`, rc 0; `check --can` on
it carries the module's twelve `fail`s at
`.weir/modules/semver.weir:36:13, …`; the test with its import line
rewritten to `weir:semver` → `semver: ok`, rc 0; `weir verify` →
`module semver: ok`. By hand only — the library has no CI step for
it (its CI is the runner), and the weir battery cannot depend on a path
outside the repo.

**P3 — this repo.** docs/tooling.md "The library": the example as a
`text` fence after the module-list sentence (tooling.md is not in
ci/skill-doc.sh's list, and the tag it vendors does not exist yet; no
comment in the page — the plan placed none); DECISIONS `lib-semver`;
CHANGELOG `## v0.0.79` `### Added`; dev/plans/README.md BUILT; this
report. No weir code changed.

### Deviations from the plan

1. **The test lives at `tests/semver.weir`, not `semver.test.weir`
   beside the module** — PLAN-lib's layout; the import is
   `"../semver.weir"`.
2. **A weir CHANGELOG line exists** (the brief asked for one under
   `## v0.0.79`; the plan said "no weir CHANGELOG line" because the
   binary is unchanged). The library keeps no CHANGELOG (PLAN-lib
   non-goal), so the line here is the one record a user sees.
3. **The DECISIONS row's "found on the way" tail** drops the record-
   position parse error (it is [D:derived-ord]'s, fixed there) and
   keeps the `function`-under-`let pure` friction, which still stands
   (R7: logged in dev/NOTES-agent.md `## friction` by the planning
   commit; the module spells `fun k -> match k with`). Not fixed here.
4. **The README row** was added to the library commit (CONTRIBUTING:
   "the PR adds a row"); the plan's file list did not name it.
5. **`add` derives the alias `Semver`**, so its import-it line says
   `as Semver`; the docs and the test import `as SemVer` — the alias
   is the author's, and both work. Noted, not a change.

### Gates

- `dotnet build src/Weir/Weir.fsproj -c Debug` — 0 errors;
  `dotnet test tests/Weir.Tests/Weir.Tests.fsproj -c Debug` — Passed
  2594 / Failed 0 (a sanity run; no src change).
- `ci/decision-keys.weir` — 823 rows, every [D:key] resolves.
- `ci/commit-area.weir --range main..HEAD` — run after the commit;
  result in the session's hand-off.
- The library: runner green, test green alone, vendoring proof green
  (above).

### Follow-ups

- **The tag.** The user tags `v0.1.0` in /output/lib and pushes; until
  then `weir add module semver@v0.1.0` answers 404 at
  `api.github.com/repos/weir-shell/lib/commits/v0.1.0`.
- **The live probe.** ci/e2e.sh's `[D:lib-short-name]` host probe
  (`nosuch@v0` → the URL named) can become the positive path
  (`semver@v0.1.0` → `added module semver`) once the tag exists; not
  done now.
- **R7's checker bug** (`function | Regex …` under `let pure` misread as
  a command head) — a one-line fix in the purity walk, in weir, its own
  commit.

## Amendment (2026-10-26, user ruling)

**What changed.** PLAN-derived-ord's amendment removed the type-level
`[<Ord>]` before it shipped; the only form is `[<Ord n>]` on a record
field or a union case, and UNNUMBERED record fields take no part in
the order (the partial-key law). The module was rewritten to it and
re-committed in /output/lib (`1f2633f`, as queil, on top of `d991ca0`;
no tag, no push):

- `type Pre = | [<Ord 1>] Pre of string | [<Ord 2>] Release` — the
  prerelease still sorts below its release, by number instead of by
  declaration order.
- `type SemVer = { [<Ord 1>] major; [<Ord 2>] minor; [<Ord 3>] patch;
  [<Ord 4>] pre; build: Option<string> }` — build metadata REJOINS the
  record as the unnumbered field. The partial-key law is §10 verbatim
  ("build metadata MUST be ignored when determining version
  precedence"), so R1's reason for keeping it out of the value — a
  field that is in `==` but not in the order was a trap the type
  could not express — is now the type's own law, stated in its `///`
  doc: two versions differing only in build metadata have the same
  precedence (neither `<` the other, `Seq.sort` keeps input order,
  `min`/`max` the first) and are not `==` (equality is whole-value).
- `parse` keeps the metadata (validation unchanged, `buildCheck`);
  `toString` renders it back, so `toString (parse s) == s` holds for
  EVERY `s` (`1.2.3-rc.1+exp.sha` round-trips).
- `build : string -> Option<string>` is REMOVED — unreleased, nothing
  to deprecate; the field is the reader (`(parse s).build`), and a
  member that re-parsed the text to read what the value now carries
  would be two homes for one fact. None of R1–R8 gives a reason to
  keep it: R1 chose (a) over (c) only because (c) minted a record
  whose `==` differed from `SemVer`'s, which is exactly what `SemVer`
  itself is now.
- `core` and the three bumps drop the metadata with the prerelease
  (`build = None`): the metadata names a build of the version it was
  parsed with, and a derived version is a different version — carrying
  `+exp.sha` onto `1.2.4` would label a build that never happened.
  Documented on each member.
- Eight signed members (was nine), every impl `let pure`; the R7
  spelling (`fun k -> match k with`) stays; `--can` is still
  `terminates: fail × 12` only.

**The test.** The round-trip table gains four build-metadata texts
(`1.0.0+20130313144700`, `1.0.0-alpha+001`,
`1.0.0-beta+exp.sha.5114f85`, `1.2.3-rc.1+exp.sha`); the build block
reads the field, then pins the partial-key law on `1.0.0+a`/`1.0.0+b`:
`<` false both ways, `<=` true both ways, `==` false, `Seq.sort` of
`[b; a]` keeps `b a`, `Seq.min` is the first; the bump block starts
from `1.2.3-rc.1+exp.sha` and expects the same four results as before.
`eq modulo build` (which asserted `==` true) and `build dropped` are
gone with the law they pinned. Everything else is as it was.

**Verified** (the binary published from this tree, `0.0.0-dev+458b8c0`):
`weir check`/`weir fmt --check` clean on both files; `weir
tests/semver.weir` → `semver: ok`; `weir tools/test.weir` → six `ok`
lines, `ok: 1 modules`; hover through `weir lsp`: all eight members
`(pure)`, `SemVer` hovers `{ major: int; minor: int; patch: int; pre:
Pre; build: Option<string> }  (ordered by major, minor, patch, pre)`,
`Pre` `(ordered by Pre, Release)`. The vendoring proof re-run by hand
(http.server over /output/lib, a scratch git project, `weir add module
http://127.0.0.1:8731/ --path semver.weir --as semver` → `added module
semver (c1dadf9c61e2…, 10 member(s))`, the docs/tooling.md example over
`v1.9.0 v1.10.0 v1.10.0+build.7 v1.11.0-rc.1 junk` → `1.11.0`, the
test with `import "weir:semver"` → `semver: ok`, `weir verify` →
`module semver: ok`).

**Weir side.** docs/tooling.md's sentence, the `lib-semver` DECISIONS
row (rewritten in place — this branch's own, unreleased), the CHANGELOG
bullet, dev/plans/README.md's line, this file. No weir code.

**Follow-ups** unchanged: the `v0.1.0` tag and push in /output/lib;
ci/e2e.sh's `[D:lib-short-name]` probe to the positive path once the
tag exists; R7's purity-walk fix.
