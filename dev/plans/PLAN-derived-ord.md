# PLAN: derived Ord — `[<Ord>]` orders a record or union by declaration order

**Status: BUILT, then AMENDED (2026-10-26, branch feat/derived-ord) — built under R1–R10 as a type-level `[<Ord>]`; the user's ruling the same day replaced it with per-field / per-case `[<Ord n>]` before it shipped. The body below is the original plan and its session report; the amendment at the end is what landed.**

## The receipt

A SemVer module in a coming official library needs versions that
sort and compare:

```
[<Ord>]
type Pre = Pre of string | Release        // Pre before Release: 1.0.0-alpha < 1.0.0
[<Ord>]
type SemVer = { major: int; minor: int; patch: int; pre: Pre }
versions |> Seq.sortBy SemVer.parse |> Seq.last
SemVer.parse v < SemVer.parse "2.0.0"
```

Today every line after the declarations refuses: `Ord` is the scalar
set exactly, with no structural decomposition — the type-classes plan
parked "Ord on records/unions (lexicographic/declaration-order) — no
receipts" (PLAN-type-classes, Parked). This is the receipt. The
workaround (`Seq.sortBy (fun v -> v.major * 1000000 + …)`) is the
kind of key-packing the class was built to retire, and it has no
spelling at all for a union in the key.

The design, agreed with the user:

- **Opt-in by attribute, never inferred.** `Eq`/`Show` derive silently
  because field order is irrelevant to them; for `Ord` the declaration
  order BECOMES semantics, so a reorder for readability would silently
  change sort results. `[<Ord>]` above `type` declares the intent. It
  is a registered, position-scoped attribute like `[<Tag "kind">]`
  ([D:attr-positions]).
- **Lexicographic by declaration order** — the order `to json`/`show`
  already use for records ([D:record-order]). A union orders by case
  index (declaration order), then payload — F#'s rule: `Option` orders
  `None` first; a user union picks its order by how it lists cases.
- **Every field (record) / payload (union) must itself order, or the
  DECLARATION refuses naming the field** — never a sort site.
  Admitted: int, float, string (ordinal), bool, `Duration`, `Size`,
  `Instant`, `Uuid`, `Option<T>` of an admitted T, other `[<Ord>]`
  types. Refused: `seq`, `Map`, `Bytes`, `Secret`, functions, tuples,
  undeclared records/unions.
- **Admitted operations**: `Seq.sort`/`sortDescending`/`sortBy`/
  `sortByDescending` (an `[<Ord>]` value is a legal key), `min`/`max`/
  `minBy`/`maxBy`, and `<`/`>`/`<=`/`>=` on two values of one `[<Ord>]`
  type (a version is a quantity like an `Instant`, not an identifier
  like a `Uuid`). `==` already works via Eq.
- **Tuples stay unordered**; the teaching now has a target: name the
  parts in a record and add `[<Ord>]`.

## What the code says today (anchors, verified)

**The class.** `Cls = Eq | Show | Ord | Render` (Types.fs ~687);
`Scheme.Cs: Map<string, Set<Cls>>` (~698); `Pending = { Cls; Span;
Describe }` (Check.fs ~486). The solver is `demand` (Check.fs ~860–950):
a var or row var carries the pending forward, a concrete type runs the
predicate. Its Ord arms are exactly

```
| Cls.Ord, (TInt | TFloat | TStr | TBool | TDur | TSize | TInstant | TUuid) -> true
| Cls.Ord, _ -> false
```

— no decomposition of any kind. **The brief is wrong on one point:
`Option<T>` is NOT Ord today** (`[Some 1; None] |> Seq.sort` refuses).
Eq, by contrast, decomposes through `typeDefFor` for both records and
unions with a `seen` cycle guard (`decompose`, ~880–895) — the shape the
Ord arm will reuse, gated on the attribute.

**One Ord message, eight customers.** Every Ord demand comes from a
builtin scheme instantiated at the use (Builtins.fs ~8709–8740:
`sortByScheme`, `ordSeqToElem`, `ordSeqToSeq`, `ordByToElem`; table
~8788–8795 binds sortBy/sortByDescending/max/min/sort/sortDescending/
maxBy/minBy — precisely the brief's admitted-operations list). The
`Describe` for Ord is built once at `instantiate` (Check.fs ~836):
`cannot sort by this key: {T} cannot be ordered — keys are int, float,
string, bool, Duration, Size, Instant, or Uuid`, so `ls |> Seq.max`
says "cannot sort by this key" too (pinned loosely at Tests.fs ~23806
by "cannot be ordered"; the full tail is byte-pinned at ~17124). The
prefix is the diagnostic code's key: `codeOf` (Script.fs ~5929) maps
`cannot sort by this key` → `unsortable-key`.

**Comparison operators are NOT the class.** `typeBinOp` (Check.fs
~1379–1570) types `<`/`>`/`<=`/`>=` by per-type arms — Float×Float,
Size×Size, Dur×Dur, Instant×Instant, Int×Int — and defaults two unknowns
to int (~1427). **Strings and bools have no `<` at all** (only `+` on
TStr), and `Uuid` was ruled the same way: "Eq/Show/Ord admit
(sort/min/max/sortBy) but NO `<` operators — identifiers are not
quantities, the string precedent" ([D:uuid]); `Instant` is the other
side: "Ord/Eq admit (before/after IS comparison)" ([D:instant]). A
named type reaches `| _, ((TNamed _ | TSeq _ | TTuple _ | TUnit) as a), _
-> err "operator '{op}' is not defined for {T}"` (~1560), pinned at
B38 (`Some 1 < None` → `operator '<' is not defined for Option<int>`,
Tests.fs ~18311). A `TVar` left of a TNamed right falls through to the
generic `expected 'a, got T` mismatch — a new arm is needed so
`x < v` with `v: SemVer` binds `x`.

**The runtime compares scalars only.** `scalarCompare` (Builtins.fs
~1332) has one arm per scalar Value and `unreachable` otherwise; it is
the comparator of `sortByImpl`/`sortByDescImpl` (~1352/~1372),
`extremumImpl`/`extremumByImpl` (~2541/~2561) and `sortPlainImpl`
(~2585). `binOp` (Eval.fs ~2196–2265) evaluates `<` by Value shape with
the same per-scalar arms and an `unreachable` catch-all. Neither sees a
record or union today, by the checker's refusal.

**Values.** `VRecord of record: string * fields: (string * Value) list`
with fields in DECLARATION order by construction ([D:record-order]:
"declared records take SOURCE order everywhere — literals, from
json/yaml, Env/Args, builtins"; `{ r with F = v }` keeps F's position),
so a positional zip over two values of one declared record IS the
declaration-order comparison with no type information. `VUnion of case:
string * payload: Value option` (Eval.fs ~190) carries **no case index
and no type name** — the runtime cannot know a case's declaration
position from the value. Two unions may share a case name
([D:ambiguous-ctor] refuses only the bare USE); the write-side union
table ([D:wire-unions-u]) faced the same fact and ruled "two tagged
unions sharing a case name reachable from ONE serialized type refuse".

**A total order already exists — for a different purpose.** `Value` is
`[<CustomEquality; CustomComparison>]` (Eval.fs ~165); `CompareTo`
(~344–430) is the Map key order of [D:map-keys]: records field by field
in FIELD-NAME order, unions by case NAME (ordinal) then payload — the
row says, deliberately, "unlike F#'s declaration order — a runtime
union value carries only its case name, and name order needs no hidden
state". SKILL.md ~2065 teaches the same. The derived order of this
plan is a DIFFERENT order (declaration, not name) and must not reuse
`CompareTo`; the [D:record-order] row's "closed three deep: NoComparison
on Value, the class solver refuses record keys, scalarCompare never
sees a record" is stale in its first clause (map-keys opened
CustomComparison) and this plan deliberately opens the other two for
`[<Ord>]` types only — see R1.

**The `type` statement runs.** `Eval.declare` (Eval.fs ~7320) is the
runtime hook of a declaration: it binds constructors
(`constructorValues`, ~7308: `VUnion(case, None)` or a builtin
wrapping `VUnion(case, Some v)`) and registers a validated type's
`create` in a process-wide `validators` dictionary keyed by the
declaration's id (`UnionDef.Validated`, [D:validated-types]); a
module's declarations register when its body replays. This is the
precedent for a per-process registry filled at declaration time.

**The attribute registry.** `AttrPos = FieldPos | RecordDeclPos |
UnionDeclPos | CasePos` (Check.fs ~7837); `attrRegistry: Map<name,
(validator * AttrPos list)>` (~7850–7925) — `Tag` hosts `[UnionDeclPos]`,
`Other` `[CasePos]`, nothing hosts `RecordDeclPos` yet (checkDecl's
record arm calls `validateAttrsAt RecordDeclPos` only so that every
name teaches its home, ~8148). `validateAttrsAt` (~7965): duplicate,
unknown-with-did-you-mean, wrong-position ("'Tag' attaches to a union
declaration, not a record declaration"), per-name arg validation.
`attributeNames` feeds `Builtins.attrDocs` (~8289) which the LSP hovers
between `[<` and `>]` (Lsp.fs ~1502, [D:lsp-typename]) — a new name
owes a doc line. `Decl.Attrs: AttrSpec list` (Ast.fs ~364–371);
`typeDecl` (Parser.fs ~5571–5597) consumes `opt attrList` before
`type` and commits; an argless spec (`[<NoShort>]`, `[<Other>]`) is an
existing shape, so `[<Ord>]` lexes and parses today — it fails only at
`validateAttrsAt` as unknown.

**Where a declaration's facts live.** `RecordDef = { Name; Params;
Fields; Attrs (per FIELD); Docs }` (Types.fs ~768) — no type-level slot;
`UnionDef = { Name; Params; Cases; Tag; CaseWires; OtherCase; Validated;
Docs }` (~857). `checkDecl` (Check.fs ~8115): the record arm builds the
def at ~8165, the union arm reads `Tag` from `decl.Attrs` at ~8213 and
builds at ~8332. Construction sites a new field touches: RecordDef ×11
(Check 5, Builtins 4, Tests 2), UnionDef ×4.

**Tuple ordering.** No DECISIONS row carries the "No tuple ordering"
ruling by name; its texts are SKILL.md ~2797 ("No tuple ordering
(sortBy/sortByDescending keys stay scalar). Records remain the
spelling for anything with NAMES") and LEXICON ~86 ("Ord — int,
string, bool exactly; no structural decomposition (no
record/union/tuple ordering)" — stale since [D:duration]/[D:floats]/
[D:size]/[D:instant]/[D:uuid] widened the set). Pinned at Tests.fs
~16840 (`[(1, 2)] |> Seq.sortBy (fun x -> x)`) and ci/e2e.sh ~6197.
**docs/SEMANTICS.md does not exist** — the reference pages replaced it
([D:reference]); the home for the law is docs/reference/types.md
(`## Constraints` ~357, `## Records` ~129, `## Unions` ~160) and
docs/reference/lexical.md's attribute list (~365–380).

**Grammars.** The `[<…>]` token is name-agnostic in all three:
tmLanguage `#attribute` (editors/vscode/syntaxes/weir.tmLanguage.json
~200), micro `# rule: attribute` (editors/micro/weir.yaml ~99), Zed
`(attribute) @attribute` (highlights.scm:49, tree-sitter node). Fmt has
no attribute-specific code; the attrpos e2e cell (ci/e2e.sh ~7512)
already proves `fmt --check` on an attribute line above `type`. The
assembler's attr-only-line join ([D:attr-positions]) carries `[<Ord>]`
on its own line. **No grammar, assembler or editor change.**

**Oracle and fuzz.** tests/Weir.Fidelity/Pins.fs ~87 pins the sort
helper and the function-key rejection as `Same` against FCS. F#
derives structural comparison on records and unions in declaration
order by default — the `[<Ord>]` semantics are F#'s, so an F# twin
without the attribute referees accept/reject. Both test projects
reference Expecto.FsCheck; tests/Weir.Fuzz/Grammar.fs already generates
`STypeRec`/`STypeUnion`/`SRecLet`. The REPL needs nothing: `type`
echoes via Repl.fs ~2538 and values via `show`, neither reads the
order; only the runtime registry must tolerate REPL redeclaration
([D:dup-type-decl]).

## The shape

### A. The attribute and the declaration law (checker)

- Registry: `"Ord"`, argless (`None -> None | Some _ -> Some "takes no
  argument"`), hosts `[RecordDeclPos; UnionDeclPos]`. Wrong positions
  teach for free: `'Ord' attaches to a record declaration or a union
  declaration, not a record field`.
- Storage: `RecordDef.Ord: bool` and `UnionDef.Ord: bool` (set in
  `checkDecl` from `decl.Attrs`; `false` at every other construction
  site). Prelude's `Option` is not attributed — it is Ord by the
  builtin arm below (R3).
- One predicate, `ordAdmits (env) (seen) (ty) : Result<unit, Ty>`
  (the first offending type, innermost), reused by `demand`'s Ord arm
  AND by `checkDecl`:
  scalars as today; `TNamed("Option", [t])` → `t`; `TNamed(n, targs)`
  with an `[<Ord>]` def → every field / payload under `substParams`,
  `seen`-guarded (self-recursive `Node of Tree` admits, as Eq does);
  a `TVar` (a type parameter inside a generic `[<Ord>]` declaration) →
  admitted at the declaration and checked at the use when the
  argument is concrete (R5 — Eq's `Box<fn>` shape); everything else
  (`TSeq`, `Map`, `TBytes`, `TSecret`, `TFun`, `TTuple`, `TUnit`,
  undeclared records/unions) → the offender.
- `checkDecl`, when `[<Ord>]` is present, after `validateTy`: run the
  predicate per field (record) / per payload (union); the first
  refusal errors at `decl.Span` (fields carry no span in the `DRecord`
  triple) naming the member. Tuple payloads (`C of int * string`) are
  tuples — refused (R4).
- Union case-name collision law (R2): an `[<Ord>]` union may not share
  a case name with another `[<Ord>]` union visible to the checker
  (`env.Types`, module types, the prelude's `Some`/`None`), refused at
  the second declaration.
- Orthogonal to `[<Tag>]` and `validate` (both may combine; R10).

### B. Admission at the use (checker)

- `demand`: `Cls.Ord, TNamed("Option", [t]) -> ok seen t`; `Cls.Ord,
  TNamed(n, targs)` when the def has `Ord = true` → `decompose`; tuples
  and everything else stay `false`. Generic code is untouched — the
  constraint rides the var and discharges here.
- `typeBinOp`, new arms BEFORE the `TNamed … not defined` catch-all:
  `(">"|"<"|">="|"<="), (TNamed(n, _) as a), b` when `resolve b = a` and
  the def is Ord → `Ok TBool`; the same named type without `[<Ord>]`
  → the teaching below; `(">"|…), TVar _, (TNamed _ as t)` (and the
  mirror) when `t` is Ord → bind the var to `t` and retry (the scalar
  defaulting arms' shape). `Option`, tuples, seqs, strings, bools,
  `Uuid` keep today's refusals (R6).
- Messages (R7 keeps the `cannot sort by this key` prefix: it is the
  `unsortable-key` code's key):

| site | message |
|---|---|
| declaration, record field | `[<Ord>] on 'SemVer': field 'logs' is seq<string>, which cannot be ordered — {tail}` |
| declaration, union payload | `[<Ord>] on 'Pre': case 'Tagged' carries int * string, which cannot be ordered — {tail}` |
| `{tail}` for a record/union without the attribute | `` add [<Ord>] above `type Pre` to order it too `` |
| `{tail}` for a tuple | `tuples have no order; name the parts in a record and give it [<Ord>]` |
| `{tail}` otherwise | `only int, float, string, bool, Duration, Size, Instant, Uuid, an Option of those, and [<Ord>] types order` |
| declaration, case collision | `[<Ord>] on 'Pre': case 'None' is also a case of the ordered union 'Option' — a value carries only its case name, so two ordered unions cannot share one; rename it` |
| sort/min/max/sortBy key, record or union without the attribute | `` cannot sort by this key: SemVer cannot be ordered — add [<Ord>] above `type SemVer` to order it by its fields in declaration order `` (union: `by its cases in declaration order`) |
| sort key, tuple | `cannot sort by this key: int * string cannot be ordered — tuples have no order; name the parts in a record and give it [<Ord>]` |
| sort key, otherwise | `cannot sort by this key: int -> int cannot be ordered — keys are int, float, string, bool, Duration, Size, Instant, Uuid, an Option of those, or a type declared with [<Ord>]` |
| `<` on a record/union without the attribute | `` operator '<' is not defined for SemVer — add [<Ord>] above `type SemVer` to compare by its fields in declaration order `` |

The existing byte-pin on the key tail (Tests.fs ~17124) amends — the
[D:record-order] drive-by ("grew the word") is the precedent.

### C. The runtime comparison

- `scalarCompare` becomes `orderCompare` (same signature, same
  `unreachable` for shapes the checker refuses), iterative via a
  work-list like `Equals`/`CompareTo` ([D:eq-depth] — a recursive
  `[<Ord>]` type folded deep must not overflow):
  - scalars: as today;
  - `VRecord(n1, f1), VRecord(n2, f2)`: fields zipped POSITIONALLY in
    carried order, first nonzero wins; a name mismatch at a position
    is `unreachable` (two values of one declared record always carry
    source order — the tripwire pins the law, not a path);
  - `VUnion(c1, p1), VUnion(c2, p2)`: `compare (caseIndex c1)
    (caseIndex c2)`, then payloads (`None` vs `Some` cannot occur for
    one case — a case either carries or does not; `unreachable`).
- Case index (R2): a process-wide registry `ordCases: case -> (typeName
  * index)`, seeded with `None → 0`, `Some → 1`, filled by `Eval.declare`
  for `[<Ord>]` unions (`UnionDef.Ord` is check-time data; `declare`
  receives the `Decl`, whose `Attrs` carry the name). A re-registration
  from the same `typeName` overwrites (REPL redeclaration,
  [D:dup-type-decl]); from a different `typeName` with a different
  index it is `unreachable` — the collision law makes it so for every
  pair one checker can see, and the tripwire names the one it cannot
  (two modules with no import between them, both `[<Ord>]`, sharing a
  case name: the second module's `type` statement fails loudly rather
  than one sort silently misordering).
- `binOp`: `(">"|"<"|">="|"<="), ((VRecord _ | VUnion _) as a), b ->
  VBool (sign (orderCompare op a b))` — four arms.
- No `Value` shape change, no `TypedExpr` change, no Purity/Can/Effects
  change (a comparison is pure).

### D. Surfaces

- `Builtins.attrDocs`: `"Ord", "[<Ord>] — the type orders by its fields
  (record) or cases then payload (union) in declaration order: sort,
  min/max, sortBy keys and < > <= >= admit it"`.
- LSP type hover (Lsp.fs ~766 union, and the record twin): an `[<Ord>] `
  prefix beside the `[<Tag "…">] ` one, so the hover carries the fact
  the fields alone cannot.
- `weir fmt`: nothing — pinned, not assumed.

## Phases

| phase | content | effort |
|---|---|---|
| **P0 — the declaration law** | registry entry + `AttrPos` hosts; `Ord: bool` on both defs (15 sites); `ordAdmits`; `checkDecl` per-member refusal + case-collision law; the declaration messages; unit pins (each refused kind byte-pinned) | ~0.5 day |
| **P1 — admission** | `demand`'s Option + `[<Ord>]` arms; the four `typeBinOp` arms; the three use-site messages; the key-tail pin amended; pins: sort/sortBy/min/max/minBy/maxBy on records, unions, Option fields, nested `[<Ord>]`, generic `[<Ord>]` (admit at int, refuse at fn), `<` on records and unions, tuples still refused with the new teaching, Option `<` still refused (B38) | ~0.5 day |
| **P2 — runtime** | `orderCompare` (iterative), `ordCases` + `declare` registration + redeclare overwrite + tripwire, `binOp` arms; value pins; FsCheck property; e2e AOT cells | ~1 day |
| **P3 — surfaces + ledger** | attrDocs + hover prefix; SKILL (attributes bullet, the two blocks, the tuple line), reference types.md + lexical.md, LEXICON's Ord entry, Map-keys note, CHANGELOG `## v0.0.79`, DECISIONS row `derived-ord`, oracle pins, fmt pin, composition cells | ~0.5 day |

≈ 2.5 days, one session. P0–P1 are check-only and shippable with the
runtime still `unreachable`-guarded (nothing reaches it until P1
admits); P2 must land before any admitting pin runs a value.

## Tests

Unit (tests/Weir.Tests/Tests.fs, the `declare`/`checkErr`/`checkOk`/
`expectValue` idioms; a new `derivedOrdTests` list beside
`attributeTests` ~6426):

- **Declaration.** `[<Ord>] type P = { a: int; b: string }` declares,
  `def.Ord = true`; `[<Ord>] type U = A | B of int` likewise; an
  `Option<int>` field admits; a nested `[<Ord>]` record field admits;
  each refused member kind byte-pins its message: `seq<string>`,
  `Map<string, int>`, `Bytes`, `Secret`, `int -> int`, `int * string`,
  a record without the attribute (tail names `type R`), a union
  without it, `unit`; a tuple-payload case; the case-collision law
  (`[<Ord>] type R = None | X` names `Option`; two user unions); the
  wrong-position teaching on a field (`'Ord' attaches to a record
  declaration or a union declaration, not a record field`); `[<Ord;
  Ord>]` duplicates; `[<Ord 1>]` "takes no argument".
- **Admission.** `Seq.sort`/`sortDescending` on `seq<P>`; `Seq.sortBy
  (fun x -> x.key)` with an `[<Ord>]` record KEY; `min`/`max`/`minBy`/
  `maxBy`; the SemVer fixture: sorted order, `Seq.last`, `Pre "alpha"
  < Release`; `[Some 1; None] |> Seq.sort` → `[None; Some 1]`; a generic
  `[<Ord>] type Box<'a> = { v: 'a }` admits at `Box<int>` and refuses
  at `Box<int -> int>` naming the key; `[<Ord>]` through a generic
  helper (`let best xs = Seq.max xs` applied to records — the
  constraint rides and discharges); `[<Ord>]` through a row var (the
  "Ord x rows" pin's shape, now admitting); `<`/`>`/`<=`/`>=` on two
  records → bool, on a record vs another type → the mismatch; `x < v`
  with `x` unbound binds `x`.
- **Refusals at the use (byte-pinned).** `ls |> Seq.sort` (FileRow —
  the tail says `add [<Ord>] above `type FileRow``), `[(1, 2)] |>
  Seq.sortBy (fun x -> x)` (the tuple teaching), a function key (the
  "keys are …" tail), `Some 1 < None` unchanged, `"a" < "b"` unchanged,
  `p1 < p2` on an un-attributed record (the operator teaching).
- **Runtime.** `orderCompare` on records: lexicographic by position,
  first field decides, equal prefixes fall through; unions: index then
  payload; `None` before `Some`; a record holding a union holding a
  record; a self-recursive type 100k deep does not overflow
  ([D:eq-depth]'s fixture shape); redeclaring an `[<Ord>]` union in the
  REPL with cases reversed re-orders (the registry overwrites);
  stability: `sortBy` with equal keys keeps input order (the
  `sortByDescending` pin's twin).
- **Composition products** (one cell each, pinned or N/A'd): × `[<Tag>]`
  (declares; a tagged `[<Ord>]` union sorts and still crosses the
  wire), × `validate` (an `[<Ord>]` validated type orders by its
  payload), × `from json` into an `[<Ord>]` record then sort (wire
  order is source order), × `{ r with … }` (position kept, order
  stable), × anonymous records (never Ord — the sort refuses with the
  tail naming the shape), × imports (an `[<Ord>]` type declared in a
  module sorts in the importer; `Lib.T` values compare), × REPL
  redeclare, × `Map` keys (an `[<Ord>]` record as a Map key keeps
  name order — R8 pinned so drift is loud), × floats (an `[<Ord>]`
  record with a float field orders, `==` on it still refuses —
  Ord-not-Eq extends from floats to their containers).
- **Property** (Expecto.FsCheck in Weir.Tests): over generated values
  of a fixed `[<Ord>]` shape (ints, strings, an Option, a two-case
  union) — `orderCompare` is total, antisymmetric and transitive;
  `orderCompare a b = 0` iff `a = b` (consistent with Eq); `Seq.sort`'s
  output is sorted and a permutation of its input; and for the
  all-scalar record the order equals F#'s `compare` on the field tuple
  (an in-process oracle).
- **e2e (ci/e2e.sh, AOT binary).** The SemVer script: declare both
  types, sort a list of versions, print the last and the `<` result —
  expected bytes; the headline check-first cell (~6260) stays green;
  an `[<Ord>]` record with a seq field refuses at CHECK with the field
  named and zero effects (a `printerr "must-not-run"` first line);
  `[(1, 2)] |> Seq.sortBy (fun x -> x)` refuses with the tuple
  teaching; `fmt --check` accepts `[<Ord>]` on its own line above
  `type`; `check --json` codes the refusal `unsortable-key`.
- **Oracle (tests/Weir.Fidelity/Pins.fs).** `Same`: sort a record list
  (F#: default structural comparison; weir: `[<Ord>]`); min of a union
  list; `<` on two records. `Diverge` row `derived-ord-explicit`: F#
  orders an un-attributed record, weir refuses — the opt-in is the
  divergence, stated.
- **Doc tests.** The SKILL blocks below execute via ci/skill-doc.sh;
  the reference's new fences likewise.

## Perf

- Check time: the declaration predicate is O(members) once per
  declaration; `demand` on an `[<Ord>]` type is Eq's decompose cost
  (memoised by nothing today, bounded by `seen`) — the same class as
  `==` on records.
- Run time: `orderCompare` is O(width) per pair, allocation-free on
  the scalar path (the work-list allocates only when a record or union
  is reached); sort complexity unchanged; `ordCases` lookup is one
  dictionary read per union pair. No cost to scripts that never
  declare `[<Ord>]`: the new arms sit after the scalar arms.
- ci/timing.sh: no new budget; a 100k-element `[<Ord>]` record sort
  pinned under the existing sort tripwire's bound if one exists,
  else noted.

## Risks

1. **The union registry is hidden state** — exactly what [D:map-keys]
   refused for Map order. Contained: the collision law closes every
   pair one checker sees, the tripwire closes the rest loudly, the
   registry is keyed by case name only for `[<Ord>]` unions, and the
   structural fix (an index on `VUnion`, ~290 sites) is the recorded
   reopen if the tripwire ever fires in practice (R2).
2. **Two orders on one value.** `CompareTo` (name order, Map keys) and
   `orderCompare` (declaration order, sorts) disagree on `[<Ord>]`
   types. Stated in the row and in SKILL's Map bullet; pinned (R8).
3. **Message-pin churn.** The key-tail byte-pin and the `ls |>
   Seq.sort` pins amend; `codeOf`'s prefix is kept so `unsortable-key`
   is stable for `check --json` consumers.
4. **Positional record zip** assumes two values of one declared record
   carry one order — true by [D:record-order]'s construction law;
   `unreachable` on a name mismatch turns a future violation into a
   crash with a name, not a wrong sort.
5. **Generic `[<Ord>]` types** push the refusal to the use site
   (`Box<fn>`), the one place the brief's "never at a sort site" bends
   — the same bend Eq already has, and the message names the offending
   argument (R5).
6. **Doc blocks cost zero verification of OUTPUT** (the skill-doc gate
   runs for executability) — the SemVer ordering claim is pinned in
   e2e by bytes, not by the doc.

## Non-goals

- Ordering tuples, seqs, `Map`, `Bytes`, `Secret`, functions, `unit`,
  anonymous records; `<` on strings, bools, `Uuid`, `Option`, tuples.
- A user comparator (`Seq.sortWith`), a `compare` builtin, custom
  instances, descending-per-field attributes, case-insensitive string
  order (`sortBy Str.toLower` is the spelling).
- Changing Map key order, `show`, `to json`, `==` (stays
  order-insensitive), or hashing.
- Inferring Ord without the attribute.
- Any grammar, assembler, formatter or editor-extension change.

## The DECISIONS row (ready to paste)

```
| derived-ord | 2026-10-26 | `[<Ord>]` ON A RECORD OR UNION DECLARATION DERIVES AN ORDERING — lexicographic by DECLARATION order (the order `to json`/`show` already carry, [D:record-order]); a union by case index then payload (F#'s rule: `None` before `Some`, a user union orders as it lists its cases). OPT-IN, NEVER INFERRED: Eq/Show derive silently because field order is irrelevant to them, but for Ord the declaration order BECOMES semantics, so a reorder for readability would silently change sort results — the attribute declares the intent; it joins the position-scoped registry ([D:attr-positions]) at RecordDeclPos + UnionDeclPos, argless. THE DECLARATION LAW: every field/payload must itself order — int, float, string (ordinal), bool, Duration, Size, Instant, Uuid, `Option` of those, other `[<Ord>]` types — or the DECLARATION refuses naming the member (seq, Map, Bytes, Secret, functions, tuples, un-attributed records/unions); a generic `[<Ord>]` type is conditional (the argument is checked at the use, Eq's `Box<fn>` shape); tuple payloads refuse (tuples stay unordered — the teaching now names the repair: name the parts, add `[<Ord>]`). ADMITTED: sort/sortDescending/sortBy/sortByDescending (an `[<Ord>]` value is a legal KEY), min/max/minBy/maxBy, and `<`/`>`/`<=`/`>=` on two values of one `[<Ord>]` type — a declared type is a quantity by declaration (the Instant side of the Instant/Uuid split; strings, bools, Uuid, Option keep no operators). `Option<T>` joins Ord by decomposition everywhere (`None` first). RUNTIME: `orderCompare` (scalarCompare widened, iterative [D:eq-depth]) zips record fields POSITIONALLY in carried order; a union value carries only its case name, so case index comes from a process registry filled when the `type` statement runs (the validators precedent, [D:validated-types]) — hence the COLLISION LAW: two `[<Ord>]` unions visible to one checker (the prelude's Some/None included) may not share a case name, refused at the second declaration; the one pair no checker sees (two modules with no import between them) trips `unreachable` at the second module's replay, never a silent misorder. TWO ORDERS, stated: Map keys keep [D:map-keys]'s name order (no hidden state there); sorts use declaration order — they agree on scalars only. Fully erased at check time, as every class. THE RECORD-ORDER LAW AMENDED: "order is carried, never semantic" holds everywhere except under `[<Ord>]`, where the author made it semantic on purpose; `==` stays order-insensitive. | Check attrRegistry Ord + ordAdmits + checkDecl member law + collision law + demand Option/[<Ord>] arms + typeBinOp arms + messages; Types RecordDef.Ord/UnionDef.Ord; Builtins orderCompare + attrDocs; Eval ordCases + declare + binOp arms; Lsp hover prefix; Tests derivedOrdTests + FsCheck property + amended key-tail pin; e2e SemVer cell + check-first + tuple teaching + fmt; Fidelity 3 Same + 1 Diverge; SKILL/reference/lexical/LEXICON; CHANGELOG v0.0.79 |
```

## SKILL.md additions (the blocks execute in CI via ci/skill-doc.sh)

In the attributes bullet (~247–266), after the position sentence:

> `[<Ord>]` above a record or union declaration derives an ORDERING
> in declaration order (fields; cases then payload — `None` before
> `Some`, a union orders as it lists its cases) [D:derived-ord]: the
> type then sorts (`Seq.sort`, `sortBy` KEYS, `min`/`max`) and compares
> with `<`/`>`/`<=`/`>=`. Opt-in because the order is now semantics —
> every field/payload must itself order (scalars, `Duration`/`Size`/
> `Instant`/`Uuid`, `Option` of those, other `[<Ord>]` types) or the
> DECLARATION refuses naming the member; seqs, Maps, Bytes, Secrets,
> functions and tuples cannot order.

```weir
[<Ord>]
type Pre = Pre of string | Release

[<Ord>]
type SemVer = { major: int; minor: int; patch: int; pre: Pre }

let versions = [{ major = 1; minor = 0; patch = 0; pre = Release }; { major = 1; minor = 0; patch = 0; pre = Pre "alpha" }; { major = 0; minor = 9; patch = 2; pre = Release }]
let top = versions |> Seq.sort |> Seq.last
print $"{top.major}.{top.minor}.{top.patch}"
let older = top < { major = 2; minor = 0; patch = 0; pre = Release }
print $"{older}"
```

```weir-error
// every field must order: a seq has no order, so the DECLARATION refuses
[<Ord>]
type Run = { name: string; logs: seq<string> }
```

The casing-law bullet's line (~2797) becomes:

> No tuple ordering (a tuple sort key refuses) — name the parts in a
> record and give it `[<Ord>]`. Records remain the spelling for
> anything with NAMES.

```weir-error
// tuples have no order — name the parts in a record and add [<Ord>]
[(1, "b"); (2, "a")] |> Seq.sort |> Seq.iter (fun p -> print (show p))
```

The Map bullet (~2065) gains one clause after "NOT F#'s declaration
order": "(an `[<Ord>]` type SORTS in declaration order and is a Map
key in name order — the two agree on scalars only)".

## Docs touched

- skills/weir/SKILL.md — the three edits above; the `Seq.sort`
  (~1360) and `max`/`min` (~1424) parentheticals say "Ord — scalars,
  `Option`, `[<Ord>]` types".
- docs/reference/types.md — `## Records` and `## Unions`: one paragraph
  + fence each (the SemVer fences split: records sort, unions order by
  case); `## Constraints`: "ordering" names the attribute and the
  tuple teaching, with the `weir-error` fence.
- docs/reference/lexical.md — the attribute list (~365–380): `[<Ord>]
  on a record or union, deriving an ordering in declaration order`.
- docs/LEXICON.md ~86 — the Ord sentence: the scalar set as it is
  today, "plus `Option` and `[<Ord>]`-declared records/unions
  (declaration order); no tuple ordering".
- docs/DECISIONS.md — the row above (ci/decision-keys.weir requires
  it before any `[D:derived-ord]` comment lands).
- CHANGELOG.md — new `## v0.0.79` / `### Added` above v0.0.78:
  "**`[<Ord>]` derives an ordering.** Put `[<Ord>]` above a record or
  union and it sorts (`Seq.sort`, `sortBy` keys, `min`/`max`) and
  compares (`<`, `>`, `<=`, `>=`) in declaration order — fields for a
  record, cases then payload for a union (`None` before `Some`). Every
  field must itself order or the declaration says which one does not;
  tuples still refuse, and the error now says to name the parts in a
  record. `Option` values sort too."
- dev/NOTES.md — the story (the type-classes park's trigger fired; the
  union-index fact and the registry trade).
- site/src/data/reference.json — only if `weir docs-json` changes (it
  dumps `builtinDocs`, not `attrDocs`; the e2e currency gate at
  ci/e2e.sh ~13816 decides).

## Rulings

- **R1 — the record-order law and the type-classes park.**
  [D:record-order] rules "order is CARRIED, never SEMANTIC … Ord CANNOT
  regress … the class solver refuses record keys, scalarCompare never
  sees a record"; [D:inferred-type-classes]/LEXICON say "no
  record/union/tuple ordering"; PLAN-type-classes parked exactly this
  on "no receipts". The brief conflicts with all three. *Recommend:
  amend by the new row (append-only) — the law stands everywhere
  except under `[<Ord>]`, where the author made order semantic on
  purpose; the park's trigger (a receipt) has fired; `==` stays
  order-insensitive so the one custom Eq arm is untouched.*
- **R2 — where a union's case index lives at runtime.** (a) a process
  registry filled by `declare`, keyed by case name, with the
  check-time collision law and a replay tripwire; (b) an index field
  on `VUnion` (~290 construction/match sites, every `Some`/`None`
  included); (c) case-NAME order as Map keys use — but `Error < Ok`
  and `Pre < Release` only by alphabetical luck, and the user asked
  for declaration order. *Recommend (a); (b) is the recorded reopen
  if the tripwire fires in practice.*
- **R3 — `Option<T>` as an Ord member.** Everywhere (`[Some 1; None]
  |> Seq.sort` admits, `None` first) vs only as a field inside an
  `[<Ord>]` declaration. *Recommend everywhere — one decomposition
  rule, F#'s order; `<` on Option stays refused (B38 pin), operators
  are for declared quantities.*
- **R4 — tuples inside `[<Ord>]` declarations** (a tuple field, a
  multi-value case payload `C of int * string`). Refuse with the
  record teaching, or admit element-wise inside a declaration only.
  *Recommend refuse — one tuple rule everywhere; the repair is two
  lines.*
- **R5 — generic `[<Ord>]` types.** Conditional (params assumed at the
  declaration, the argument checked at the use — Eq's shape) vs
  refuse generics under `[<Ord>]`. *Recommend conditional; the use-site
  message names the argument.*
- **R6 — `<`/`>` on `[<Ord>]` types** against the Uuid/string
  precedent ("identifiers are not quantities"). *Recommend admit — the
  declaration is the opt-in and the user's brief says so; the
  precedent stands for undeclared types. Stated asymmetry: an
  `[<Ord>]` record holding one string compares with `<` while a bare
  string does not.*
- **R7 — the `cannot sort by this key` prefix on `min`/`max`/`sort`**
  (pre-existing: one Describe for eight customers). Keep (it is the
  `unsortable-key` code's key) vs an op-neutral prefix. *Recommend
  keep; logged as a message-ownership follow-up.*
- **R8 — Map key order for `[<Ord>]` keys.** Stay with `CompareTo`'s
  name order ([D:map-keys], no hidden state) vs follow declaration
  order. *Recommend stay; the row states the two orders and a pin
  holds it.*
- **R9 — the collision law's reach.** Visible unions only (env +
  module types + prelude) with the replay tripwire for the rest, vs a
  process-wide check at every `type` run (a runtime error for a
  check-time fact). *Recommend visible-only + tripwire.*
- **R10 — `[<Ord>]` with `[<Tag>]` and with `validate`.** Allow both
  (orthogonal: the wire reads the tag/payload, the order reads the
  declaration) vs refuse as [D:validated-types] does for `[<Tag>]`.
  *Recommend allow; both pinned as composition cells.*

## Session report (2026-10-26)

Executed under R1–R10 as recommended; every phase landed in one
session, two commits (the feature with its pins and the DECISIONS row;
then the docs and ledger).

- **P0 — the declaration law.** `attrRegistry` gains `Ord` (argless,
  RecordDeclPos + UnionDeclPos — the first RecordDeclPos host).
  `RecordDef.Ord`/`UnionDef.Ord` (11 construction sites, not 15: the
  Script/Eval `{ … with Docs }` copies need nothing). One predicate,
  `ordOffender env seen ty : Ty option` (the innermost offender),
  reused by `checkDecl`, the Ord `Describe`, and the declaration
  messages; `ordTail` renders the repair per offender; `authorDeclared`
  decides whether a repair may name an attribute. The def is
  registered in a local env BEFORE the member law runs, so a
  self-typed payload (`Node of OrdTree`) admits through `seen`. The
  collision law scans `env.Types` for ordered unions (Option counted
  by name — it is ordered by the solver's arm, not the attribute) and
  skips the key equal to the declaring name (the REPL's replace).
- **P1 — admission.** `demand`: `Option<t>` → `ok t`; an `[<Ord>]`
  TNamed → the existing `decompose` (so generic `[<Ord>]` types are
  conditional, R5, and vars pend as Eq's do). `typeBinOp`: five arms
  before the `TNamed … not defined` catch-all — Ord×TVar and
  TVar×Ord bind and retry, Ord×same-name binds the args and types
  bool, Ord×other is the mismatch, un-attributed author type teaches
  the repair; Option/strings/seqs/prelude types keep the bare refusal
  (B38 byte-pinned unchanged).
- **P2 — runtime.** `Eval.orderCompare` (public; Builtins' six
  `scalarCompare` call sites moved to it — Eval compiles first, and
  `binOp` needs it): scalar fast path, then a work-list; records zip
  positionally with an `unreachable` on a field-name mismatch; unions
  by `ordCases` index then payload. `ordCases` is a
  ConcurrentDictionary seeded `None → 0, Some → 1`;
  `registerOrdCases` (same owner or same index overwrites, different
  owner + different index is the tripwire) runs from `Eval.declare`
  when the Decl carries `Ord`; `forgetOrdCases` runs from the REPL's
  redeclare path, so a replaced union leaves no stale entries. Four
  `binOp` arms.
- **P3 — surfaces + ledger.** `attrDocs` line; `declHover` prefixes
  `[<Ord>] ` on both record and union hovers (unit-pinned via
  `hoverType`); SKILL (attributes bullet + the two fences, the
  `Seq.sort` and `max`/`min` parentheticals, the Map clause, the
  casing-law line + fence); reference types.md (Records, Unions,
  Option, Constraints — four fences) and lexical.md; LEXICON's Ord
  sentence; DECISIONS `derived-ord`; CHANGELOG `## v0.0.79`
  (`### Added` + `### Fixed`); NOTES story; PRODUCT-MATRIX's "Ord ×
  decomposition" cell amended.
- **Pins.** `derivedOrdTests` (25 tests: declaration law with every
  refused kind byte-pinned, collision law, attribute laws, admission
  incl. the SemVer receipt, Option, generic Box, generic helper + the
  rows pin's shape, the four operators, use-site refusals byte-pinned,
  `orderCompare` records/unions/nested/100k-deep, registry overwrite,
  stability, and the composition cells: Tag, from json, `with`, Map
  keys (R8), floats, hover); two FsCheck properties over a generated
  `Fx` record (total/antisymmetric/transitive/zero-iff-equal/equals
  F#'s `compare`; sort sorted + permutation + equals `List.sort`) —
  the suite's first `testProperty`. The key-tail byte-pin amended.
  e2e: eight cells (SemVer bytes with the own-line attribute above a
  MULTI-LINE record, fmt --check, check-first on a seq field, tuple
  teaching, `check --json` → `unsortable-key`, imports ×2, validate).
  Fidelity: 3 `Same` + 1 `Diverges "derived-ord-explicit"` with its
  divergences.md row.

### Deviations from the plan

1. **The repair tail never names a type the author cannot edit.** The
   plan's `ls |> Seq.sort` pin expected "add [<Ord>] above `type
   FileRow`"; FileRow is a built-in record with no declaration in the
   script. Built-in and prelude types (`builtinTypeNames`, which
   Prelude.extend already fills with every type at prelude close) get
   the admitted-set tail; an anonymous record gets "declare the shape
   as a record and give it [<Ord>]". `builtinTypeNames`' definition
   moved up the file unchanged so the helpers can read it.
2. **The parser needed nothing; the ASSEMBLER did.** The brief's "an
   attribute line above a record `type` is a parse error today" held
   only for a multi-line record body: the attr-only join produces a
   buffer starting `[<Ord>] type R = {`, and the record-body district
   keyed on `bufStartsWith "type "`, so the fields below were read as
   a literal's continuation. `Script.bufIsTypeDecl` reads through the
   join (two call sites); CHANGELOG `### Fixed` carries it. The
   one-line form always worked (the shipped binary reaches the
   position teaching).
3. **`Option` is ordered by the solver, not by attributing the prelude**:
   the prelude declares `Some of 'a | None`, so the attribute would
   order `Some` first; the registry seeds `None → 0, Some → 1` and the
   collision law counts Option by name.
4. **The REPL forgets, `declare` does not purge**: a purge in `declare`
   keyed by type name would drop a same-named module type's entries
   ([D:qualified-type-names]); the REPL is the one place a type is
   replaced, so `forgetOrdCases` runs there.
5. **ci/timing.sh has no sort tripwire** — noted, no budget added.
6. **`site/src/data/reference.json` unchanged**: `docs-json` dumps
   `builtinDocs`, not `attrDocs`; the e2e currency gate agrees.
7. **One pin reshaped**: the plan's "[<Ord>] through a row var" cell
   is pinned in the existing "Ord x rows" pin's shape (a plain param
   discharging to an `[<Ord>]` record, now admitting). A
   field-accessed row-typed key inside an EXPRESSION-level `let … in`
   strands ("an ordering requirement on a type nothing determines")
   on the shipped binary too — a pre-existing inner-let
   generalisation limit, out of scope; the top-level `let` form
   admits. Logged here, not fixed.

### Gates

- `dotnet test tests/Weir.Tests/Weir.Tests.fsproj -c Debug` —
  Passed 2588 / Failed 0.
- `dotnet test tests/Weir.Fidelity/Weir.Fidelity.fsproj` — Passed 198
  / Skipped 1 (pre-existing) / Failed 0.
- `dotnet publish src/Weir -c Release -r linux-x64` (in-worktree);
  `ci/skill-doc.sh` — all 375 blocks hold; `ci/decision-keys.weir` —
  every [D:key] resolves; `tools/dead-privates.weir` — 0 unreferenced;
  `tools/dx-message-census.weir` — at baseline (32/41).
- `dotnet test tests/Weir.Fuzz/Weir.Fuzz.fsproj` (WEIR_BIN set) —
  Passed 51 / Failed 0.
- `WEIR_BIN=… ci/e2e.sh` — stops at the pre-existing RLIMIT_NPROC cell
  ("a thread the limit refuses …", line ~4476) on this shared
  container: under its `ulimit -u`, even `timeout 5 true` dies and
  the shipped v0.0.78 binary fails identically (the limit counts the
  uid's tasks host-wide, `ps` sees one namespace). Everything before
  it: 299 ok. With that one block disabled in an untracked copy, the
  whole battery: 1101 ok, 0 FAIL, rc=0 — the eight derived-Ord cells
  and the reference-json currency gate among them.

## Amendment (2026-10-26, user ruling)

**What changed.** The type-level `[<Ord>]` is removed — deleted, not
deprecated; it never shipped. The only form is `[<Ord n>]` on a RECORD
FIELD or a UNION CASE: one positive int argument, registry positions
FieldPos + CasePos (the `[<Short "c">]` shape). A type with any
`[<Ord n>]` is Ord; its key is the numbered members by ascending `n`,
the next number breaking ties; gaps are allowed, equal `n` within one
type refuses at the declaration naming both members, `0` refuses at
the registry (`'Ord' expects a positive int, e.g. [<Ord 1>]`), a
negative is not an attribute literal. Declaration order means nothing.

**Why.** With the order read off the declaration, reordering a
record's fields for readability silently changed program behaviour —
the attribute flagged the trap without removing it. A spelled position
is reorder-immune: moving or adding a field cannot change the key.

**The laws that follow.**

- Records: UNNUMBERED fields are not part of the order (the partial-key
  law — SemVer's build metadata is the customer) and are unconstrained;
  a numbered field's type must order or the declaration refuses, naming
  the field and its position (the existing refusal, re-targeted to the
  attribute's span).
- Unions: every case numbered or none — a half-numbered union refuses
  naming an unplaced case; a numbered case's payload must order.
- Equality stays whole-value: two records equal on the key but
  different elsewhere are neither `<` nor `>` nor `==`; `Seq.sort` is
  stable so they keep input order; `min`/`max` return the first.
  Stated once in the attribute's doc and in SKILL.md; pinned.
- `Option` stays Ord by the solver's arm (unchanged).

**What moved in the code.** `RecordDef.Ord: bool`/`UnionDef.Ord: bool`
became `OrdKey: string list` (the key in order; empty = not ordered).
`checkDecl` gained `ordKey` (collects positions, refuses a shared one)
and the all-or-none union law; the member law runs over the numbered
members only; `ordOffender`/`demand` decompose through the key
(`ordKeyTys`), not every field. The runtime can no longer zip record
fields positionally — the carried order is not the key — so Eval
gained a record registry (`ordFields: name -> key`) beside the case
registry, both filled from `Eval.declare` and storing the LISTED
positions; `forgetOrd` drops both on the REPL's replace. A record
collision law joined the union one: a numbered record may not share
its name with a visible numbered record of a different key (a local
declaration beside an import of the same name). Hover: the type shows
`(ordered by a, b)`, a member its `[<Ord n>]`. Messages: "number its
fields with [<Ord n>]" replaces "add [<Ord>] above `type X`"; the
tuple teaching says "name the parts in a record and number them".
[D:record-order] stands whole again (order carried, never semantic).

**Pins re-pointed.** derivedOrdTests rewritten to the new spelling
plus: the key laws (shared position on fields and on cases,
half-numbered union, zero / no argument / a string, twice on one
field, both declaration positions), reorder immunity (`OrdRev`), the
partial-key law (operators, stable sort both ways, min/max first, the
key still decides, `==` whole-value), the record collision law, the
registries' listed positions and tripwires, hover on type / numbered
field / unnumbered field / case. Fidelity's three `Same` pins and the
`Diverge` row re-spelled. e2e: the SemVer cell carries an unnumbered
`build` field and pins the stable order by bytes; a key-laws cell; the
assembler fix is pinned by `[<Tag "kind">]` above a multi-line record
reaching the registry's teaching at `file:1:3`, never a parse error.
SKILL.md: the bullet, the SemVer fence (with `build`), a partial-key
`weir` fence, `weir-error` fences for an unordered numbered field, a
shared position and a half-numbered union.

**Follow-up.** /output/lib/semver.weir uses the type-level form and no
longer checks; a separate agent rewrites it.

