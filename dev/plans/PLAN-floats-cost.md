# weir — floats: the cost analysis

Status: REPORT (analysis session 2026-08-04, zero code). The
deliverable of the blessed analysis plan: every decision priced with
options, blast radius, and a citation; the minimum-viable subset
priced separately; both DECISIONS row texts ready for a one-line
ruling.

Calibration point (bless: use, don't re-estimate): the `Duration`
widening measured the mechanical cost of a `Ty` addition — one arm
per class table plus every exhaustive `Ty`/`AttrArg` match across
~10 files, all compiler-found via FS0025 **except `tyVars`
(src/Weir/Types.fs:82), which has no wildcard and dies at STARTUP,
not at build** (Builtins' static init generalizes schemes). One
Duration-sized session covered type + literals + algebra + classes +
both arg/env loaders + docs + pins. The mechanical part is known and
small; everything priced below is the semantic part.

---

## 0 · A structural finding that reshapes the pricing: finite-only floats

The worst costs in sections 1 and 3 are NaN and Infinity, and weir
already owns the precedent that removes them: **`checkedInt`
(src/Weir/Eval.fs:196) raises on integer overflow at eval** — weir
arithmetic is already checked, not wrap-silent. A `checkedFloat` twin
that raises on a non-finite result (`0.0 / 0.0`, `1.0 / 0.0`,
overflow to `inf`, `Float.parse "NaN"` rejected at the boundary)
makes every float in the value domain FINITE by construction:

- `Eq` loses the NaN ≠ NaN trap (equality stays reflexive — the
  property `Seq.distinct`/`contains` silently assume).
- `Ord` becomes genuinely total — no "NaN sorts first but
  `NaN < x` is false" divergence between `<`
  (src/Weir/Eval.fs:204-217 binop arms) and `Seq.sortBy`'s
  `scalarCompare` (src/Weir/Builtins.fs:621).
- `show` never faces `NaN`/`Infinity`/`-Infinity` (section 3 shrinks
  to one decision).
- JSON cannot emit the non-JSON tokens `NaN`/`Infinity` (a real
  serializer failure class — System.Text.Json throws on them).

Cost: one guard per float op (the checkedInt shape, ~identical
lines), div-by-zero raises exactly as int division already does
(src/Weir/Eval.fs:217 — `1 / 0` raises today). What it does NOT fix:
`0.1 + 0.2 == 0.3` is still false — that is representation, not
NaN, and is priced in section 1.

Every recommendation below assumes finite-only. If IEEE-full floats
are preferred instead, add: the Eq reflexivity hole, the Ord
totality choice (runtime NaN check inside an erased compile-time
class — the erasure property [SEMANTICS "Type classes"] is exactly
what a runtime check breaks), and three special-value renderings.

---

## 1 · The class system

**Eq** (arm: src/Weir/Check.fs:390; value equality:
src/Weir/Eval.fs:29-33; hash: ~48)

| option | cost | blast radius |
|---|---|---|
| (a) admit, IEEE `==` | `0.1 + 0.2 == 0.3` false — a silent footgun in a language selling check-time honesty | every `==`, `Seq.distinct`, `Seq.contains`, pattern guards |
| (b) exclude | `==` on floats is a located teaching error naming the idiom (`Float.abs (a - b) < eps`); `distinct`/`contains` reject float keys | surprising to a nushell/pwsh newcomer; consistent with Ord already excluding records (Check.fs:400-404 "no receipts; the message names it") |

**Recommended: (b) at first landing.** The teaching-error culture
makes the rejection a lesson; admission is one arm later if a
receipt arrives (the widening path is measured and cheap). Note
`Cls.Eq, (TFun _ | TSeq _) -> false` (Check.fs:391) is precedent
for a type the class refuses while `show` renders it.

**Ord** (arm: src/Weir/Check.fs:404; scalarCompare:
src/Weir/Builtins.fs:621)

| option | cost | blast radius |
|---|---|---|
| (a) admit (finite-only makes it total) | one arm each in the class table and scalarCompare | `Seq.sortBy` on timings/ratios works — half of what floats are FOR |
| (b) exclude | sorting computed rates needs an int projection detour | kills the sort-by-rate shape |

**Recommended: (a)** — under finite-only it is genuinely total and
free of the NaN caveats; without finite-only it drags the whole
NaN-ordering question in. Note the asymmetry this creates (Ord
admits what Eq refuses) is new — no current type is Ord-but-not-Eq —
and needs one SEMANTICS sentence. If that asymmetry is judged too
odd, (b)+(b) is coherent and costs the sort shape.

**Show** — not optional. Since [D:interp-show], holes consult the
class (spliceAdmit, src/Weir/Check.fs — "a new Show-admissible type
needs no edit"); `print` follows the scalar family
(src/Weir/Builtins.fs:2384). A float that is not Show cannot be
printed or interpolated — the type would be unusable. Show admission
is part of the type's landing, and floats become the property's
first confirmation (before `Size`): `$"{pct}"` must render with
zero interpolation edits.

## 2 · Division and the numeric tower

Today `/` is unambiguous (src/Weir/Check.fs:697-701 — closed
per-type arms; the `TDur` rows are the template). The shape with
floats, keeping weir's no-implicit-conversion posture ([D:duration]
"nothing converts implicitly" — `30s + 5` rejects):

- `3 / 2 = 1` (unchanged), `3.0 / 2.0 = 1.5`, `3 / 2.0` a type
  error with a teaching naming `Float.ofInt`. One new footgun class
  in the most-used operator — priced as: the error is LOCATED and
  teaches, so it is a speed bump, not a trap. The implicit-widening
  alternative contradicts the posture everywhere else (the Duration
  row's law) and poisons inference (is `x / 2` int or float?) — not
  recommended, priced only to be declined.
- `Float.ofInt` / `Float.toInt` (truncating, stated — the int-division
  precedent) / `Float.round`. Explicit, greppable, matches
  `Duration.toMs` / `Str.toInt` naming.
- **Duration interaction — the concrete win**: `Duration / int`
  stays integer ms (unchanged). `Duration.toS` becomes landable and
  honest: `TDur -> TFloat`, `2500ms |> Duration.toS = 2.5`. (It was
  proposed as int and DECLINED 2026-08-04 precisely over silent
  truncation — the decline is the "cannot be honest" horn of the
  bless text, resolved by the type.) `Duration ÷ Duration` could
  return float ratio — but the existing rejection naming
  `Duration.toMs d1 / Duration.toMs d2` (Check.fs:698) still gives
  an INT ratio; whether it re-points at a float spelling is a
  one-line message edit, not a semantics change.
- Pattern literals: **decline float patterns** (`| 1.5 ->`). F#
  itself warns on float equality patterns; with Eq excluded (1b)
  they are unrepresentable anyway. Match via guards. Pointer:
  tryBind PInt (src/Weir/Eval.fs:~650).

## 3 · `show`

Under finite-only, the special values vanish and ONE decision
remains: the rendering of ordinary floats.

- **Recommended: .NET shortest-round-trip (the default `ToString`
  since Core 3.0), invariant culture, with one adjustment — an
  integral float renders with a visible decimal (`show 1.0` =
  `"1.0"`, not `"1"`)**, so a float never renders identically to an
  int (type-visibility, the REPL-echo distinction, and the
  Go-shape precedent of rendering carrying the type's identity).
  Exponent form for extremes as .NET emits (lowercased `e`), e.g.
  `1e-07`.
- Round-trip law: `Float.parse (show f) == f` — shortest-round-trip
  guarantees it by construction; pinned both directions exactly as
  Duration did ([D:duration] Show ↔ parse). `Float.parse`/`tryParse`
  follow the X/tryX rule; parse REJECTS `NaN`/`Infinity` text
  (finite-only at every boundary).
- `-0.0`: normalize to `0.0` at construction (one guard in
  checkedFloat) — otherwise it is a value that is Ord-equal and
  Eq-distinguishable-by-rendering; normalizing deletes the question.
- Doc examples printing computed floats inherit shortest-round-trip
  determinism — deterministic, so doc-runnable (the suite-visible
  commitment the bless names is safe under this choice).

## 4 · The four boundary loaders

**from json** — the strongest single argument FOR floats: JSON's
number type is floating; weir's int-only read
(src/Weir/Eval.fs:273 `GetInt64`) errors on real payloads with
decimals. Admitting: `TFloat` field reads via `GetDouble` (reject
non-finite — cannot occur in valid JSON), int fields unchanged
(1.5 at an int field stays an error — no silent truncation at the
boundary). Field law: jsonFieldOk (src/Weir/Check.fs:750).
**Schema interaction is CLEAN**: tyKind maps `TFloat -> "number"`
(src/Weir/Contracts.fs:568) and kindOk already encodes
integer ⊆ number (Contracts.fs:575) — the distinction the bless
worried about maps one-to-one.

**to json** — `WriteNumberValue(double)` (src/Weir/Eval.fs:232
precedent) emits shortest-round-trip; `1.0` emits as `1` in .NET,
and per JSON Schema an integral number IS a valid `integer`, so
validity is unaffected; if emission fidelity is wanted, the show
adjustment (integral-with-`.0`) can be mirrored — one decision,
state it either way.

**YAML** — the costliest loader. `YFloat of float` is a case in the
PRELUDE union (src/Weir/Prelude.fs:20), i.e. a declared-union change
that ripples ctors/Show/Eq-laws automatically (the Option/Yaml
precedent) — but the yaml-v1 row explicitly recorded "no float case
(weir has no float)", so the union, both shape laws
(yamlShape/yamlableOut), the reader's scalar typing (quotedness law:
unquoted `1.5` at a float field = number, quoted = string — the
Norway machinery already carries quotedness) and the writer all
move. The reverse-Norway quoting law is ALREADY float-defensive:
`looksNumeric` (src/Weir/Yaml.fs:546,584) quotes `"1.5"`/`"1e5"`
strings today, so admitting real floats does not weaken it — the
law was built for exactly this adjacency. Cost: a half-session on
its own, fully independent.

**Args.load / Env.load** — the measured-cheap one: field laws
(src/Weir/Argv.fs:130,172; Check.fs:1579), parse arms
(src/Weir/Eval.fs:936,1401), `[<Default 0.5>]` needs `AFloat` in
attrArgLit (src/Weir/Parser.fs:~2395 — a SEPARATE tiny parser from
intLit, the Duration session's recorded gotcha). Exactly the shape
Duration priced: ~6 sites, FS0025-guided.

**Lexer interplay (part of any landing):** two teaching errors
REVERSE POLARITY (src/Weir/Parser.fs:269,271): "weir has no float
literals" becomes a float literal, and the decimal-duration teaching
("decimals are a rendering; write the ms form") keeps rejecting
`2.5s` but must re-word — decimal DURATION literals stay illegal,
`2.5` alone becomes legal. Both messages are pinned
(durationTests "the teaching rejections") — two pins retarget.
Command-argument splices: today's scalar list admits int; GNU
`sleep 0.5` is a real receipt for admitting float argv words
(rendering is the unambiguous shortest form) — one arm in
spliceAdmit's CmdArg side, or keep the rejection; small, decide at
landing.

## 5 · What it buys, priced honestly

- **`Duration.toS`, lossless** — the internal counterexample
  resolved; the decline reverses into a float-returning member.
- **Percentages / rates / averages** — the corpus TODAY has zero:
  swept examples/*.weir, tools/*.weir — all `Seq.length` counts and
  integer ms (`ci/timing.sh` divides ns→ms in shell); no script
  computes a ratio. Per the bless's own caveat this is the
  family-shaped hole: nobody computes a pass-rate in a language
  whose `/` would floor it, so absence of receipts here is not
  evidence of absence of want. The concrete shapes when they
  arrive: pass-rate (`passed / total` as %), timing deltas
  ("1.4× slower"), size in MB (`bytes / 1048576` currently floors).
- **Peer parity** — zsh/pwsh/nushell all have floats; a newcomer
  probing weir's numerics hits a teaching error today. After MVP
  they hit a working float and (under 1b) a teaching error only on
  `==` — a defensible surprise instead of a category absence.

## The minimum-viable subset, priced separately

**MVP = the type + arithmetic + Show + the Float module. No Eq, no
Ord, no JSON, no YAML, no loader fields.**

Contents: `TFloat`/`VFloat`, literal lexing (the two reversed
teachings), checkedFloat finite-only arithmetic, Show admission +
shortest-round-trip rendering, `Float.ofInt/toInt/round/parse/
tryParse/abs`, `Duration.toS`, holes render (free — the
interp-show property's first confirmation), fuzz float production +
fresh seeds, docs.

- Buys: toS, computed-and-printed percentages (`$"{100.0 *
  Float.ofInt p / Float.ofInt t}%"`), peer parity.
- Does not buy: sorting by rate (no Ord), float JSON payloads,
  float yaml, float flags.
- Cost: **one Duration-sized session** (the calibration point,
  minus the loader sites it skips, plus the checkedFloat guards —
  it nets out to the same size).
- Risk: LOW — every excluded surface rejects with today's messages
  (the field laws and class tables simply don't list TFloat), so
  nothing is half-admitted.

MVP is a genuine resting point: each exclusion is a one-arm widening
later, independently blessable, in any order.

## The table

| # | decision | options | recommended | cost | blast radius |
|---|---|---|---|---|---|
| 0 | value domain | IEEE-full vs finite-only (checkedFloat) | **finite-only** (Eval.fs:196 precedent) | ~10 guard lines | deletes NaN from §1 and §3 entirely |
| 1a | Eq | admit-IEEE / exclude | **exclude**, teaching names the eps idiom | 1 arm + 1 pin | ==, distinct, contains reject floats |
| 1b | Ord | admit / exclude | **admit** (total under finite-only) | 2 arms (Check.fs:404, Builtins.fs:621) | sortBy rates works; first Ord-not-Eq type — 1 SEMANTICS sentence |
| 1c | Show | mandatory | admit | 1 arm + rendering (§3) | print/holes work; interp-show property confirmed |
| 2a | `/` | per-type arms / tower | **per-type, no tower** (Check.fs:697 template) | ~8 arms | `3 / 2.0` errors w/ teaching — new speed bump, located |
| 2b | widening | Float.ofInt / implicit | **explicit ofInt** | 1 member | verbose mixed math; posture-coherent |
| 2c | Duration.toS | float-returning | land with MVP | 1 member | the declined member returns honest |
| 2d | float patterns | admit / decline | **decline** (F# warns; Eq excluded anyway) | 0 | match via guards |
| 3 | show | shortest-round-trip + forced `.0` | as stated | rendering fn + round-trip pins | doc examples deterministic |
| 4a | from/to json | admit TFloat fields | admit **later** (own bless) | ½ session | jsonFieldOk, GetDouble, tyKind→"number" (clean: Contracts.fs:575) |
| 4b | yaml | YFloat prelude case | admit **later** (own bless) | ½–1 session | prelude union + both shape laws + quotedness; Norway law already defends |
| 4c | args/env | TFloat fields + [<Default 0.5>] | with 4a or MVP+1 | ~6 sites (measured) | field laws + attrArgLit AFloat |
| 5 | lexer teachings | — | reverse two messages | 2 pins retarget | Parser.fs:269,271 |

## The total

- **MVP: 1 session** (Duration-sized; independently shippable and a
  stable resting point).
- **Eq/Ord ruling refinements: rider-sized** if the recommendation
  changes on receipt.
- **JSON (+args/env if not in MVP): ½ session, independently
  blessable.**
- **YAML: ½–1 session, independently blessable, last** (costliest,
  least demanded).
- **Full build: 2–2.5 sessions total**, strictly orderable
  MVP → json → yaml, each a working stop.

## The DECISIONS row, both ways (one line of choosing)

**If DECLINED:**

> | no-floats-priced | 2026-08-04 | FLOATS DECLINED WITH PRICED COSTS (analysis: plans/PLAN-floats-cost.md). The old "shells don't have floats" argument is RETIRED — true of the POSIX lineage, false of weir's capability peers (zsh/pwsh/nushell all float); the decline rests only on: (1) the corpus computes zero ratios today (family-shaped hole acknowledged — absence of receipts is weak evidence), (2) every arriving decimal case so far was time and Duration owns it, (3) the priced cost is real: Eq's 0.1+0.2 footgun or its exclusion, a new mixed-arithmetic speed bump in `/`, a rendering law, four boundary surfaces. Duration.toS stays unshipped (declined over silent truncation — the honest horn of the analysis); the seconds spelling remains Duration.toMs d / 1000. REOPENS on: a corpus/user receipt computing a ratio/percentage/average, or a JSON payload with decimal fields hitting the int-only read. The MVP path (finite-only checkedFloat, no Eq, Ord admitted, 1 session) is pre-priced in the plan and is the reopening's starting point. | plans/PLAN-floats-cost.md; divergences no-floats row updated |

**If BUILDING:**

> | floats-mvp | 2026-08-04 | FLOATS, THE MVP (analysis: plans/PLAN-floats-cost.md; the "shells don't have floats" argument retired — capability peers all float). THE LAW: finite-only — checkedFloat raises on any non-finite result (the checkedInt precedent, Eval.fs:196); NaN/Infinity are unrepresentable, -0.0 normalizes at construction. Literals 1.5/0.5/1e-7 (the two Parser teaching errors reverse polarity; decimal DURATIONS stay text). Arithmetic per-type, NO tower, no implicit conversion (Float.ofInt/toInt-truncating/round explicit); 3/2=1 unchanged, 3/2.0 a located teaching error. CLASSES: Show admitted (shortest-round-trip, integral floats render with .0, parse↔show pinned both ways — and holes render via [D:interp-show] with zero edits, the stated property's first confirmation); Ord admitted (total under finite-only — the first Ord-not-Eq type, stated); Eq EXCLUDED with a teaching naming the eps idiom (0.1+0.2 is representation, not a class member weir vouches for). Duration.toS lands float-returning (the declined-as-int member, now honest: 2500ms→2.5). BOUNDARIES all EXCLUDED from MVP and independently blessable in order json → yaml (each pre-priced in the plan; jsonFieldOk/tyKind→number clean, YFloat is a prelude-union change the Norway law already defends). Float patterns declined. | plans/PLAN-floats-cost.md; Types/Parser/Check/Eval/Builtins per plan; divergences row moves pending→different |

Whichever way: the divergence row's rationale cell must drop
"shells don't have floats" and cite this plan.
