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
- **Generic declarations**: `type Option<'a> = Some of 'a | None` and
  `type Pair<'a> = { Fst: 'a; Snd: 'a }` — unions and records both take type
  parameters. Cases carry a **single payload** (no tuple payloads — weir has
  no product types; wrap in a record). Applied types unify argument-wise with
  an occurs check through arguments; arity is validated at declaration.
- **Constructors are generalized schemes** (`Some : forall 'a. 'a ->
  Option<'a>`), instantiated fresh per use with the same deep-copy discipline
  as generalized lets — see the generalization bullet; the §3 checklist items
  apply to constructors identically and are pinned by the generics battery.
- **`==`/`<>` truly unify** (the implementation caught up with this doc):
  operands unify — including variables nested in constructor arguments, so
  `None == Some 1` instantiates and binds — then the resolved type must be
  equatable, recursively through applied constructors (`Option<int>` yes,
  `Option<int -> int>` no).
- **The prelude** is plain weir source (Option, Result) evaluated through the
  ordinary declaration path at session start — no host-registered special
  types — and embedded in the binary, so the single-file story holds.
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

## Command mode

- **Mode decision, at line head and per pipe segment** (this is the security
  boundary between weir semantics and PATH execution): a head token that is a
  known name (binding, builtin, or keyword) → expression mode, today's path
  unchanged — bindings and builtins shadow PATH. Unknown head → PATH lookup;
  hit → command mode; miss → fall back to expression parsing, which yields the
  standard unbound-variable error (did-you-mean capped at edit distance ≤ 2).
  Only a PATH *hit* can enter command mode — every ambiguous shape falls back
  to expression semantics. `^prog` forces PATH even when shadowed; a forced
  miss is a parse-time "command not found" with a PATH-based hint.
- **Command-callable builtins**: a flagged subset of builtins may head a
  command-mode line; the set is exactly `cd` and grows one member per
  demonstrated need, never wholesale. The head desugars to the builtin's
  ordinary application with barewords as string literals (`cd /work` =
  `cd "/work"`), so splices and checking are inherited — command-callability
  is a *head-position privilege only* and never leaks into expression
  checking. Bare `cd` desugars to `cd "~"`; over-application is a check-time
  arity error naming the builtin ("'cd' takes at most 1 argument(s)").
  `~`/`~/...` are expanded **by the cd builtin itself** — cd-local behavior,
  NOT general tilde expansion, which stays excluded (`echo ~` passes a
  literal `~`). `cd` on a missing directory fails at runtime showing the
  resolved absolute path. `^cd` is a parse-time command-not-found on systems
  without an external cd (verified, pinned). *Case-law note: the
  command-callable set, cd-local expansion, and `|` aliasing are case law —
  if the set grows past a handful, stop and write the general line-head
  grammar philosophy as a rules section instead of accreting cases.*
- **Cliff diagnostic**: when a line fails at parse or check time (never at
  runtime), its head is a known binding, and the tail looks command-invoked
  (a `-flag`, a path token, or a bareword while the head also exists in
  PATH), the error carries a hint: use `^head ...` for the external, pipe the
  binding, or quote arguments. One shared mechanism (`Diagnose.hint`), not
  per-case hacks.
- **Command grammar**: `head bareword* ((| or |>) segment)*`; each pipe segment
  re-enters the mode decision, so `git log | grep x | first 2` flows
  external→external→expression. `|` is accepted as `|>` in command mode only;
  expression mode remains `|>`-only.
- **Arguments**: barewords run until whitespace, `|`, `(`, `)`, quotes, `$`, or
  end of line — `/`, `.`, `-`, `=`, `%` are ordinary characters. `"..."`
  (with escapes) and `'...'` (raw) produce single args. `$name` splices a
  binding; `(expr)` splices an expression result. **Splice typing rule**:
  arguments must be strings, ints (any measure), or bools — rendered as single
  argv entries, never re-split (no injection class; same ownership line as
  `cmd`); an unresolved argument type defaults to `string`. No adjacent-token
  concatenation: `foo$bar` is two args.
- A command line's type is `seq<string>`; evaluation reuses the direct-exec
  machinery (`Proc`, `Session.Cwd`, tree-kill lifecycle — see the tripwires).
- **PATH resolution** happens per submission: mode decision uses existence
  probes (one `File.Exists` per PATH entry — microseconds, so unknown heads
  cost nothing measurable); the full name inventory is enumerated only for
  did-you-mean hints and cached per line (a mid-session install is visible on
  the next line; completion reuses the cache rather than re-scanning per
  keystroke).
- **Deliberately excluded, chosen not improvised** (each passes through as a
  literal argument today, it does not error): no glob *expansion*, no
  redirects (`>`), no env-var assignment prefix (`FOO=1 prog`), no `&&`/`;`
  chaining in command mode. Also: `let`-headed lines are always expression
  mode (no command mode on the right of a top-level `let`), and expression
  mode never flows back into command mode (`ls |> git log` is an unbound
  variable, not a command).

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
- **Splice-defaulting soundness condition** (why "unresolved argv-position
  types default to string" is harmless): command segments exist only at line
  top level and can never occur under a generalizing `let` — guaranteed by
  the "expression mode never flows back into command mode" exclusion — so a
  defaulted variable can never be generalized and instantiated elsewhere at
  a different type.
- **The session is single-threaded.** One session per process; `Session.Cwd`
  is mutated only from the REPL/eval thread (the sole background thread, the
  stdin writer, never touches it). It is deliberately *not* synchronized: the
  invariant that matters — "cwd is stable between my `cd` and my spawn" — is
  transactional, not atomic, so a lock adds nothing. When real concurrency
  arrives (parallel pipelines, multi-session daemon), the fix is structural:
  `Session` becomes a value threaded through evaluation, not a locked global.
  Tests that mutate the session run sequenced for the same reason (two
  parallel tests sharing the global are two sessions pretending to be one) —
  a symptom-level fix; any future daemon/concurrent story reopens this seam.
- **stderr passes through to the terminal by default** — it is never part of
  the typed stream, and weir does not buffer it (which also removes a
  deadlock class: a chatty-stderr child can never fill a pipe weir isn't
  reading). The opt-in capture is `complete`.
- **`complete`** (command-mode pipe suffix) and **`completed`** (its
  expression-mode builtin, `string -> seq<string> -> Completed`): run an
  external command to completion and reify the outcome as
  `Completed = { ExitCode: int; Stdout: seq<string>; Stderr: seq<string> }` —
  **never raising on nonzero exit; the exit code is data**. This is the
  chosen exit-code policy (closes backlog: grep's no-match exit 1 is now
  `grep pat file | complete |> _.ExitCode`); a per-command allowlist was
  rejected (grep's 1 is no-match but its 2 is a real error). `| complete`
  must directly follow a single external command segment (parse error
  otherwise) — it consumes the process, not the lines; the design is the
  command-suffix fallback from the plan, chosen over a type-level
  process-backed-stream distinction, which would not survive ordinary
  combinators (`where`/`first` return plain seqs). Splices in a completed
  command must be strings (the arg vector is a `seq<string>` literal).
- **`complete` and `collect` force their source to completion** — on a
  non-terminating source they do not return (`yes hi | complete` hangs by
  design; the user owns it, exactly as with `yes hi |> collect`).
- **sh-backed streams cannot be completed**: `| complete` is command-mode
  desugar and `sh "..."` is an expression. This is the boundary, documented:
  `sh` buys POSIX semantics at the price of POSIX error opacity (exit codes
  raise, stderr passes through, no reification). If dogfooding demands it, a
  `shc` variant is the shape — not a type distinction.
- **A command-headed line commits to command mode**: once the first segment
  parses as a command, there is no backtrack to expression parsing for the
  rest of the line — errors after that point are command-line errors (this is
  why `git status | first 1 | complete` reports the marker rule instead of a
  generic expression error).
- **External-to-external pipes feed stdin**: `git log | grep x` wires the
  left stream (which must be `seq<string>`) into the right command's stdin.
  Piping into `sh`-strings stays unsupported — that is what `into` is for.
- **Partiality convention (FINAL)**: a raising name plus a `try`-prefixed
  sibling returning `Option<'a>`. Pairs: `head`/`tryHead`, `toInt`/`tryToInt`;
  Option-native: `tryFind`, `tryIndexOf`; raising-only (documented bounds):
  `substring start len subject`. The idiom's other half: `defaultTo` and
  `mapOption`, so an Option in a pipeline does not force a match —
  `ls |> tryFind _.ReadOnly |> mapOption _.Name |> defaultTo "none"`. The
  interim 0-or-1-seq idiom is retired (it never became case law, as
  intended). The singleton extraction is `pwd |> head : string`.
- **String builtins are data-last, curried — needle/pattern first, subject
  last** (`contains : string -> string -> bool`): partial application yields
  point-free pipeline predicates — `where (contains "error")`,
  `where (startsWith "fix:")`, `map trim` — no lambda. This is the decision
  that compounds; no string builtin ships data-first. Set: `contains`,
  `startsWith`, `endsWith`, `trim`/`trimStart`/`trimEnd`, `toLower`,
  `toUpper`, `split` (separator first; empty entries kept), `join`,
  `replace` (pattern, replacement, subject), `strLen`, `toInt`/`tryToInt`.
- **`strLen`, not polymorphic `length` and not member access.**
  Member-access-on-primitives (`s.Length`, `map _.Length`) is a logged
  candidate design — it rides EField and reads like F# — but it is a checker
  change and stays uncoupled from builtins work. Logged, not built.
- **`sortBy : ('a -> 'b) -> seq<'a> -> seq<'a>`** — the key must evaluate to
  an int, string, or bool; anything else is a runtime error (the type system
  has no comparability constraint — same check-at-the-boundary posture as
  `from json` field types). **`groupBy` is deferred to the generics session
  with a reason**: its honest shape `{ Key: 'b; Items: seq<'a> }` requires
  generic records, which do not exist yet; a string-keyed fake would be case
  law in the wrong direction. `isEmpty : seq<'a> -> bool` completes the set.
  (`groupBy` has since landed on generic records:
  `groupBy : ('a -> 'b) -> seq<'a> -> seq<Group<'b, 'a>>` with builtin-owned
  `Group<'k, 'v> = { Key: 'k; Items: seq<'v> }`; keys share `sortBy`'s
  scalar-only runtime rule.)
- Deferred with intent: `substring`/`indexOf` (they want Option — Session 3
  customers), padding, regex (its own design — match vs captures vs typed
  groups; a backlog entry, not a builtins-session improvisation).
- **`collect : seq<'a> -> seq<'a>`** materializes eagerly at application:
  effects run exactly once, re-enumeration replays values with no re-spawn.
  Live queries (`pwd`, `ls`, command streams) bind the *query*, not the
  answer; `collect` is the snapshot operator.
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

1. **Measure algebra** (scalar×measure): reopens checklist §4.2 (unit equality
   must become normalization-based) *and* the `*`/`/`-defaulting rule above.
(Done: `collect` — backlog #1 — and the exit-code policy — old #3 — landed
as `collect`/`complete`; see "Processes and the session".)

(Done: comparison/boolean completeness — landed with `<>` inheriting `==`'s
equatability and short-circuit `&&`/`||`, as pre-committed.)
