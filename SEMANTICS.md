# fslite — deliberate language rules

Rules that are design decisions, not accidents of implementation. Each is pinned
by a test (see `tests/FsLite.Tests/Tripwires.fs` for the ones that double as
soundness shields).

## Types and inference

- **Generalization regime**: Damas-Milner-style. `let`-bound values generalize
  (minus variables free in the environment, reached transitively through row
  constraints); every use instantiates freshly, including a deep copy of row
  constraints. REPL bindings generalize fully across lines. This supersedes the
  original "monomorphic, frozen at definition" v0.1 rule — deliberate upgrade,
  decided during the row-polymorphism work.
- **No higher-order inference on variables**: a type variable never unifies with
  a function type at application. `fun f -> f 1` does not type-check;
  higher-order functions flow from typed builtins (which push parameter types
  into lambdas). Consequence: the standard occurs-check cycle constructions are
  unreachable. Changing this rule reopens soundness checklist §1.1.
- **Rows are records-only and close on discharge**: field access on an unknown
  accumulates row constraints; meeting a nominal record type validates all
  demanded fields and permanently resolves the variable to that record. There
  is no width subtyping — a record literal must match a declared record's field
  set exactly.
- **No type ascription syntax**. When it lands it must re-verify (check mode),
  never relabel (checklist §2.3).

## Units of measure

- Measures are **nominal tags** (`int<mb>`), compared by name, erased at
  runtime. Exact-match only: no implicit conversion (`int<mb>` never meets
  `int<gb>` or bare `int`).
- **No measure algebra**: `+`/`-`/`>`/`<` require identical measures and
  preserve them; `*`/`/` are defined on unitless `int` only. `f.Size * 2` and
  `f.Size / f.Size` are type errors. Known ergonomic cliff; the minimum viable
  algebra (scalar×measure, same-measure sum already works via `+`) is the top
  post-review backlog item and reopens checklist §4.2 (unit normalization) when
  built.

## Operators and syntax

- Pipe is `|>` only. Match arm bodies are full expressions; piping a whole
  `match` requires parens (arm bodies bind tighter), as does a nested `match`
  in an arm.
- Equality is `==` (and only `==`: no `<>`, `>=`, `<=`, `not` yet — queued).
  `==` requires equatable types: no sequences, functions, or unresolved
  row/type variables, checked recursively through records and unions.
- Binary operators on two unresolved type variables are errors, except `*`/`/`
  which bind both operands to unitless `int` (the only sound reading).
- `let ... in` is the expression-level binding form (single-line grammar; the
  offside rule is out of scope until multi-line input exists).
- `_.Field` is sugar for `fun x -> x.Field` (parser-level desugar; requires at
  least one field, like F#).
- Constructor names must start uppercase; that is what distinguishes
  constructor patterns from variable patterns in `match`.

## Evaluation

- Sequences are lazy end to end; re-enumerating a bound pipeline re-runs its
  effects (standard seq semantics), including re-spawning external commands.
- External command failure (nonzero exit) raises when the stream is forced, not
  when it is constructed. Abandoning a stream early kills the child process.
- Non-exhaustive matches are warnings at check time and `match failure` at
  runtime — the one deliberate runtime failure class besides boundary
  validation (`from json`/`from porcelain` reject malformed lines per line) and
  arithmetic (division by zero).
