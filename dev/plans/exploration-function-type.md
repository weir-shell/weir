# Exploration — a written function type

Status: EXECUTED (2026-09-09) [D:function-types] — both F1 (the arrow
production) and F2 (refuse-and-call) shipped together, as this doc
predicted: construction/calling fell out free, and F2 was a propagation
check the leaf refusals already satisfied (the one fix was threading the
field name through `to yaml` to match json). The costing below held.

A COSTING and DESIGN exploration, not an implementation. Output is a
shape and a cost. This is FEATURE 1 of two — the SYNTAX (an arrow in
the type grammar). FEATURE 2 — whether a function-bearing type may be
CONSTRUCTED and what `to json`/`show`/`==`/the row story do with one —
is explicitly OUT OF SCOPE; it is named, not decided.

## The gap, as three reproducible probes (against 0965961)

weir has no arrow production in the type-expression grammar, ANYWHERE:

    type Z = B of (unit -> string)   -> Expecting: identifier, ' or {|
    type R = { f: unit -> bool }     -> Expecting: '*', ';' or '}'
    [] |> from json seq<unit -> string>  -> the arrow will not parse in the arg

The checker INFERS function types (the HOF-inference fix proved it)
and RENDERS them (`(unit -> int) -> 'a1` in `#help`/errors), it just
has no way to READ one from source. The gap is a read path for a
shape the checker already represents and prints.

## Phase 0 — the type grammar, mapped

- There is ONE type parser: `tySyn` (Parser.fs, a
  `createParserForwardedToRef`). Every position uses it — union case
  payloads (`of tySyn`), record field types, generic arguments
  (`seq<tySyn>`, `Name<tySyn, …>`). Q3 answered: single, not copied —
  the arrow lands everywhere at once.
- It is `deepen`-wrapped [D:depth-guard] (line 3596), so a recursive
  production (an arrow is one) inherits depth coverage for free.
- Its shape today: `deepen ( choice[ anonShape ; 'a var ; scalar
  keywords (int/string/unit/…/seq<…>) ; Name<args> ] |> sepBy1 "*" )`.
  So the ATOMS (names, vars, `{|…|}`, generics) sit under a top-level
  `*` that folds a tuple type [D:tuples-reversal]. Type-level `*`
  EXISTS (`type P = C of int * string` parses to a `TTuple` payload —
  verified). There are NO parenthesised type expressions (`(int)` in
  type position → `Expecting: identifier, ' or {|`) and NO arrow.

## The round-trip constraint — the single most important finding

`formatTy` already renders a function type as `dom -> cod`,
right-associated, and parenthesises a function DOMAIN: weir prints
`(unit -> int) -> 'a1` (verified). For the written form to round-trip
with the printed one — the `to yaml`/`from yaml` asymmetry weir must
not repeat — the parser must accept exactly that string. That needs
BOTH the arrow AND a parenthesised-type atom. Given both, round-trip
is free (reader matches writer by construction).

## Q1/Q2/Q3

- **Q1 associativity/precedence.** `->` is RIGHT-associative
  (`a -> b -> c` = `a -> (b -> c)`) — non-negotiable, it must match the
  render path the checker already uses. Precedence, loosest to
  tightest: `->` (arrow) looser than `*` (tuple) looser than
  generics/atoms — so `int * string -> bool` is `(int * string) -> bool`
  and `seq<int> -> string` is `(seq<int>) -> string`, F#'s answers.
  Mechanically: wrap the existing tuple level in a right-assoc arrow
  fold (`chainr1 tuple (str_ws "->" >>% fun a b -> TFun(a, b))`),
  outermost inside the `deepen`.
- **Q2 parenthesisation.** `(unit -> string) -> string` must parse (a
  function taking a function) and it is also the round-trip form.
  Parenthesised type expressions DO NOT EXIST today — so a
  `between (pchar '(') (pchar ')') tySyn` atom is a PREREQUISITE. It is
  tiny (one atom alternative) and conflict-free (`(` is unused in type
  position today), NOT a separate large feature.
- **Q3 admission.** One `tySyn` → the arrow is admitted in payloads,
  fields, and generic args the moment it is added, and inherited by
  annotations if they ever land.

## Cost — split

- The arrow production: a right-assoc fold over the tuple level —
  ~2–4 lines.
- The prerequisite it needs: a parenthesised-type atom — ~1–2 lines,
  conflict-free.
- deepen coverage, round-trip, and cross-position admission: FREE
  (already wrapped, reader matches writer, one `tySyn`).
- Checker/eval for PARSING: NONE — `TFun` already exists and is
  inferred, rendered, and unified everywhere. Parsing an arrow just
  hands the checker a `Ty` it already handles.

**Total for feature 1 (grammar): ~0.25–0.5 session.** It is cheap and
round-trips for free — the shared-prerequisite case the rider named,
amortised across union payloads, record fields, and parked
annotations. It does NOT drag a `typeExpr` consolidation (there is
one already); the only rider it carries is parenthesised type
expressions, which it wants anyway for round-trip.

## Parse-without-construct — is it a coherent first landing?

Yes, but with a seam to name. Adding the arrow to `tySyn` lets
`type Z = B of (unit -> string)` PARSE and REGISTER a `TFun` payload.
Because the checker infers function types, CONSTRUCTION
(`B (fun () -> "x")`) would very likely typecheck for free — which is
feature 2's territory (may a function-bearing type be constructed?).
So "grammar alone" is coherent only if feature 1 STOPS at the data
boundary: the type is writable and (probably) constructible and
callable, but `to json`/`to yaml`/`show`/`==` on a function-bearing
value must be REFUSED pending feature 2's ruling. Those refusals are
mostly already there in spirit (the recursive jsonable field law
rejects a non-scalar/non-record field), so the seam is small — but it
IS feature 2's, and this exploration does not decide it. The honest
first landing: ship the grammar (writable types), let construction and
calling work if they fall out soundly, and hold the DATA guarantees
(json/yaml/show/==/row) for feature 2 with a plain refusal until then.

## The receipt, honest about demand

The driver is a data structure carrying a CALLBACK — a union case or a
record field whose value is a function the surrounding code calls:

    type Handler = Named of string | Custom of (string -> bool)
    type Rule = { matches: string -> bool }

The general shape is an EXTENSIBLE STRATEGY — a closed set of common
cases as data plus one open case carrying arbitrary logic. A
closed-union-only alternative cannot be extended by a library's
CONSUMERS; a module-per-strategy cannot be collected into a value
(modules are not first-class). So a strategy registry genuinely needs
a function inside a union or record, and both need this syntax first.

But the immediate demand is NARROW — no repo code hits it, because the
language has never let anyone write it. Classification:

- feature 1 is CHEAP and round-trips for free, so it is worth having
  as the shared prerequisite it is — its cost amortises across three
  consumers, not charged to one. Recommend building it when the first
  concrete consumer (the bicep selector's `Custom` case, or
  annotations) actually lands, not speculatively.
- it does NOT drag a large prerequisite (only the small paren atom),
  so the "park with a big cost" branch does not apply.

## Recorded regardless — the limitation is a fact today

Independent of whether the feature is built: `weir cannot write a
function type in any type position` is demonstrable and reproducible,
so it is a `divergences.md` limitation (`no-function-type-syntax`,
with a fidelity pin) — a reader who tries `B of (unit -> string)`
deserves better than an `Expecting: identifier` list. Feature 2 (the
data-guarantee ruling) and annotations (their own exploration) are
named here and decided elsewhere.
