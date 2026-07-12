# weir — deliberate language rules

Rules that are design decisions, not accidents of implementation. Each is pinned
by a test (see `tests/Weir.Tests/Tripwires.fs` for the ones that double as
soundness shields). Convention: every rule states its soundness
cross-reference; keep that as rules accrete.

**Governing principle** (explains several restrictions below): *polymorphism
flows from typed builtins, not from user lambdas over operators.* A lambda
parameter gains a type either from a builtin's declared signature (check mode)
or from row/operator constraints in its body; where neither pins it down,
weir rejects rather than guesses.

## Types and inference

- **Base types and literals**: `int` (optionally measured), `string`, `bool`,
  `seq<T>`, functions, declared records and unions. **No floats yet.**
  Literals: unsigned digit runs (no negative literals — write `0 - 5`),
  `1<measure>`, strings with `\" \\ \n \t` escapes, `true`/`false`, and seq
  literals `[a; b; c]` (homogeneous; elements evaluate eagerly, once — unlike
  pipelines; `[]` is polymorphic `seq<'a>`).
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
- **Equality is never polymorphic** (interaction of two rules): `==` rejects
  unresolved variables, and generalization happens at `let` — so
  `let eq = fun a -> fun b -> a == b` is rejected *at the definition*, not
  instantiated per use. If you read only the generalization bullet you might
  expect `eq` to work; the governing principle above is why it doesn't.
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
- **No measure algebra** — measures are *preserved* through `+`/`-` and
  compared through `>`/`<` (same measure required on both sides), but never
  *computed with*: `*`/`/` are defined on unitless `int` only, so
  `f.Size * 2` and `f.Size / f.Size` are type errors. The missing piece of the
  minimum viable set is exactly scalar×measure (backlog #3 below).

## Operators and syntax

- Comparison/boolean surface: `==`, `<>`, `>`, `<`, `>=`, `<=` (precedence 4),
  `&&` (3), `||` (2, lowest above pipe), all left-associative; `not` is a
  builtin `bool -> bool`. `<>` shares `==`'s equatability rule in full.
  **`&&`/`||` short-circuit**: the right operand is not evaluated when the left
  decides — observable semantics, since the right side may spawn a process
  (pinned by tests using division-by-zero as the effect proxy).
- Pipe is `|>` only. Match arm bodies are full expressions; piping a whole
  `match` requires parens (arm bodies bind tighter), as does a nested `match`
  in an arm.
- `==`/`<>` unify their operands first, then require the resolved type to be
  equatable: no sequences or functions, checked recursively through records
  and unions. Unification means one-sided resolution is fine —
  `fun f -> f.Name == "tmp"` binds the field's type to `string` (this is the
  mechanism behind the §1.2 conflicting-demands rejection); only a type still
  unresolved *after* unification is rejected.
- Binary operators on two unresolved type variables are errors, with two
  deterministic exceptions: `*`/`/` bind both operands to unitless `int` — the
  only sound reading *because no measure algebra exists* — and `&&`/`||` bind
  both to `bool` (their only typing). When scalar×measure lands (backlog #2),
  `*` on unresolved operands has two readings again and its defaulting rule
  must be redesigned with it, not merely kept.
- `let ... in` is the expression-level binding form (single-line grammar; the
  offside rule is out of scope until multi-line input exists).
- `_.Field` is sugar for `fun x -> x.Field` (parser-level desugar; requires at
  least one field, like F#).
- Constructor names must start uppercase; that is what distinguishes
  constructor patterns from variable patterns in `match`.

## Processes and the session

- **`sh : string -> seq<string>`** is the deliberate POSIX escape hatch: the
  string goes to `/bin/sh -c`, so globs, pipes, `&&`, redirects, and `&` all
  work — and their consequences are the user's. In particular, backgrounded
  (`&`) processes are orphaned to init when sh exits and no tree-kill can
  reach them: the user owns them (see the Session-1 lifecycle tripwires in
  Tests.fs — removing sh backing changes their analysis).
- **`cmd : string -> seq<string> -> seq<string>`** is direct exec: weir owns
  (prog, args). No shell, zero expansion — every argument is one argv entry,
  so there is no injection class (`cmd "echo" ["; rm -rf x"]` prints the
  string). Programs containing `/` resolve against `Session.Cwd`; bare names
  resolve against PATH.
- **`Session.Cwd` is the only working directory.** Every spawn sets it as the
  child's working directory (read at force time, not bind time);
  `Environment.CurrentDirectory` is never touched (AOT/global-state hygiene,
  honest under future concurrency). `cd : string -> string` mutates it
  (handles `~`, `..`, relative; errors on nonexistent; returns the new cwd) —
  the one deliberately effectful builtin. `pwd : seq<string>` re-reads
  `Session.Cwd` per enumeration (same lazy-value pattern as `ls`); a plain
  `string` would go stale, since env values compute once.
- Nonzero exit raises when the stream is forced, not when constructed.
  Abandoning a stream early tree-kills and reaps the child.

## Evaluation

- Sequences are lazy end to end; re-enumerating a bound pipeline re-runs its
  effects (standard seq semantics), **including re-spawning external
  commands** — `let files = sh "find ..." in ...` used twice runs `find`
  twice, and the command may not be idempotent. Mitigation is backlog #2: a
  `collect` builtin (force once, materialize) as the standard escape hatch.
- Non-exhaustive matches are warnings at check time and `match failure` at
  runtime — the one deliberate runtime failure class besides boundary
  validation (`from json`/`from porcelain` reject malformed lines per line) and
  arithmetic (division by zero).

## Backlog (ordered by day-one impact)

1. **`collect` builtin**: force-once materialization of a stream. Pure
   interpreter work; closes the re-enumeration surprise above.
2. **Measure algebra** (scalar×measure): reopens checklist §4.2 (unit equality
   must become normalization-based) *and* the `*`/`/`-defaulting rule above.

(Done: comparison/boolean completeness — landed with `<>` inheriting `==`'s
equatability and short-circuit `&&`/`||`, as pre-committed.)
