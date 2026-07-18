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

- **Base types and literals**: `int`, `string`, `bool`,
  `seq<T>`, functions, declared records and unions. **No floats yet.**
  Literals: unsigned digit runs (no negative literals — write `0 - 5`),
  strings with `\" \\ \n \t` escapes, `true`/`false`, and seq
  literals `[a; b; c]` (homogeneous; elements evaluate eagerly, once — unlike
  pipelines; `[]` is polymorphic `seq<'a>`).
- **Range literals**: `[a..b]` inclusive ascending, `[a..step..b]` stepped;
  descending only via an explicit negative step (`[10.. -1 ..1]` — the
  range positions are the one place a negative int literal exists; weir
  has no unary minus). Empty when `a > b` in the ascending form. Pure
  parser sugar over `Seq.range : int -> int -> int -> seq<int>`
  (start/step/stop, qualified-only — computed ranges spell it out).
  **Named asymmetry**: *bracketed semicolon lists are eager values;
  bracketed ranges are lazy generators* — `[1..1000000] |> first 3`
  never materializes; re-enumeration re-runs the generator (pure, so
  the collect caveat does not bite). Zero step: parse-time error for a
  literal step, runtime "range step is zero" when computed. Endpoints
  are simple expressions only (literals, idents, field access,
  parenthesized anything) — `[x..f y]` is rejected with an error naming
  the parens fix. Int only (once a "bare int only" limitation vs measured
  ranges — dissolved when measures were removed, 2026-07-18). No float
  or char ranges (no floats or chars).
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

## Units of measure — REMOVED (tombstone)

Weir had nominal measure tags on int (`1<mb>`, exact-match equality, no
algebra) from Spike 2 until 2026-07-18, when they were removed entirely.
The arc — landed on a showcase claim, algebra cancelled for zero dogfood
evidence, the `int<mb>` truncation causing the ls-Size-always-0
wrong-answer incident, `Seq.sum`/ranges shipping bare-only because no
measure-variable machinery existed — is recorded in full in NOTES.md
("Remove measures — the evidence-standard case study"), which is the
mandatory prior reading if quantities-with-conversion ever returns as an
evidenced plan. Old scripts using `1<mb>` or `int<m>` get a transition
error ("measure literals were removed"); the recognizer retires at the
1.0 grammar freeze. `FileRow.Size` (truncated megabytes) was deleted
with the measures; `Bytes : int` is the survivor — field names carry
quantity semantics now.

## Operators and syntax

- **Boolean branching** (Part 2 of the read/booleans/overflow plan,
  landed 2026-07-18 with the READ.md gate explicitly waived by the gate
  owner): `if cond then a else b` is an expression; branches unify (row
  constraints merge across them and conflicts surface at discharge, as
  with match arms). **Else is optional only when the then-branch is
  unit** — F#'s rule, riding on the unit type as pre-committed in
  PLAN-unit-and-print — so `if c then print "x"` is a valid unit
  statement and `if c then "x"` is the tailored error "add an else".
  `else if` chains; no `elif` (parked). Bool patterns
  (`match b with | true -> .. | false -> ..`) participate in
  exhaustiveness and default an unresolved scrutinee to bool (the
  operator/splice defaulting precedent). **`when` guards** on match
  arms: the guard checks bool under the arm's pattern bindings; a
  guarded arm never counts toward exhaustiveness or terminal
  reachability (it can fail at runtime); failed guards fall through in
  arm order. **Non-exhaustive matches are HARD ERRORS** (decided
  2026-07-18, upgraded from warnings the same day they gained bool
  coverage): coverage is recursive through union payloads
  (`Some (Some x) / Some None / None` is exhaustive), only unguarded
  arms count, and the precision matters because a hard error must not
  reject genuinely-total matches. Consequence: every accepted match is
  total — the match-failure runtime class no longer exists. The
  warnings channel keeps advisory findings only (unreachable arms). Grammar note: `-`
  no longer matches when followed by `>`, so guard expressions sit
  naturally before `->`. Keywords if/then/else/when joined the reserved
  set — and therefore can never be command heads. Warnings surfacing:
  the runner and `-e` print check warnings to stderr (found during this
  session — they were silently dropped before; the REPL always showed
  them); warnings never block execution.
- Comparison/boolean surface: `==`, `<>`, `>`, `<`, `>=`, `<=` (precedence 4),
  `&&` (3), `||` (2, lowest above pipe), all left-associative; `not` is a
  builtin `bool -> bool`. `<>` shares `==`'s equatability rule in full.
  **`&&`/`||` short-circuit**: the right operand is not evaluated when the left
  decides — observable semantics, since the right side may spawn a process
  (pinned by tests using division-by-zero as the effect proxy).
- Pipe is `|>` only. Match arm bodies are full expressions; piping a whole
  `match` requires parens (arm bodies bind tighter), as does a nested `match`
  in an arm.
- **Why `==` and not `=`** (archaeology backfilled 2026-07-18; the rule
  predates the decision-record convention): `=` already serves `let`
  and record fields, and a dual-role `=` needs contextual
  disambiguation — contrary to the LL-simple, reject-don't-guess
  grammar posture. `==` additionally matches C-family/bash priors, now
  strategically relevant for agent authorship (skills/weir/SKILL.md).
- `==`/`<>` unify their operands first, then require the resolved type to be
  equatable: no sequences or functions, checked recursively through records
  and unions. Unification means one-sided resolution is fine —
  `fun f -> f.Name == "tmp"` binds the field's type to `string` (this is the
  mechanism behind the §1.2 conflicting-demands rejection); only a type still
  unresolved *after* unification is rejected.
- Binary operators on two unresolved type variables are errors, with two
  deterministic exceptions: `*`/`/` bind both operands to unitless `int` — the
  only sound reading — and `&&`/`||` bind both to `bool` (their only
  typing). (The old caveat that scalar×measure would give `*` two
  readings again retired with the measures.)
- **Expression-level `let` is F#-shaped** (decided 2026-07-18, replacing
  the earlier keep-`in` decision): in scripts, a continuation line
  beginning with `let` opens a binding closed implicitly by the next line
  at the same indentation — F# light syntax, implemented exactly as F#
  implements it, by token insertion at the assembly layer (the joined
  logical line carries an explicit ` in `, so the single-line grammar,
  checker, and evaluator are untouched; ELet and its generalization
  machinery serve unchanged). Explicit `let ... in` remains legal as the
  single-line form — F#'s verbose syntax analog, and the only form
  available in the REPL and `-e` (both line-based). Blocks are
  *bindings + one result expression*: a second non-`let` line at the
  same indentation is not sequencing (parked below). `|`-headed lines
  are inert to the pending-let stack only while it is EMPTY (the two
  statement-level customers above); with a binding open they follow the
  plain indent rules — arms deeper than the pending indent are ordinary
  continuations (which is all the valid F# shape ever needed), and an
  arm at or left of it is the needs-a-body error, the same verdict F#
  gives (corrected 2026-07-18: the initial unconditional inertness
  over-accepted a dedented-arm shape F# rejects). A `let` whose body
  never arrives (dedent or statement end) is an assembly error naming
  the line. Blank lines
  still end the statement (named divergence from F#, inherited from the
  multi-line rules).
- `_.Field` is sugar for `fun x -> x.Field` (parser-level desugar; requires at
  least one field, like F#).
- Constructor names must start uppercase; that is what distinguishes
  constructor patterns from variable patterns in `match`.
- **String interpolation**: `$"... {expr} ..."`, F#-style, usable anywhere an
  expression is (including as a command argument, where it stays one argv
  entry). Holes follow the **command-splice typing rule** — string, int (any
  or bool, rendered the same way (int as digits, bool as
  `true`/`false`); an unresolved hole type defaults to `string`. One rule for
  both splice kinds, by design (one shared checker helper, `checkScalarSplice`).
  `{{`/`}}` escape literal braces; no format specifiers. `$"{n}"` is also the
  sanctioned int→string conversion — the previously-filed gap.
- **`unit` is a real type, F# semantics**: `()` literal, `unit` in type
  syntax, trivially equatable, ordinary leaf everywhere (rows, generics,
  generalization see just another ground type). Excluded from the splice
  family — command args and interpolation holes stay str/int/bool.
  Invisible interactively: the REPL and `-e` show nothing for a unit
  result (no `() : unit` trailer after `print`), F# FSI's `it` manner.
- **`print`** is the typed output builtin (bespoke checker rule, same
  species as `to json`): argument is a splice-family scalar — rendered by
  the same shared renderer as command splices — or `seq<string>`,
  streamed line-per-element with strict enumeration; returns `unit`;
  pipeable (`xs |> print`). As a bare value (`Seq.iter print`) it is the
  defaulted `string -> unit`. Not command-callable: `echo` owns bareword
  ergonomics in command mode. A `let print = ...` shadows it entirely
  (values shadow builtins, the standing rule). `Seq.iter` is the strict
  effectful traversal, qualified-only in both modes.

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
  arguments must be strings, ints, or bools — rendered as single
  argv entries, never re-split (no injection class; same ownership line as
  `cmd`); an unresolved argument type defaults to `string`. No adjacent-token
  concatenation: `foo$bar` is two args.
- **`[` never heads a command** (decided 2026-07-18): quotes end a
  bareword, so a line-head string list (`["a"; "b"] |> ...`) would
  otherwise tokenize to bare `[` and PATH-hit `/usr/bin/[` — discovered
  as a capture bug during the unit-print session. The head rule excludes
  `[`-initial words in both the bare and `^`-forced paths (forced is a
  hard error naming the alternative); `/usr/bin/[` stays reachable as
  `cmd "[" [...]`, and `[` remains an ordinary character inside command
  *arguments* (`pgrep -f [m]arker`).
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

## Scripts

- **Execution model — check everything first**: a script parses and
  typechecks completely (PATH lookups included) before any statement
  evaluates; a type error anywhere means nothing runs (pinned in e2e by a
  touch-then-error script whose file never appears). Named divergence from
  every shell users know: install-then-use is a check-time
  "command not found" — declare dependencies, don't install mid-script; the
  escape hatch is running the POSIX shell as an ordinary external
  (`sh -c "thing ..."`): the head resolves at check time, the string's
  contents at runtime. Errors report `path:line: [line:col] ...`.
- **Strict by default**: scripts resolve module members qualified-only;
  `#loose` at file head (line one, or two after a shebang) opts into
  REPL-style bare names. Any other `#`-directive placement is an error. The
  REPL is always loose. Rationale: bare-name resolution is a moving target
  and scripts are durable artifacts — qualified names mean the same thing
  forever. `weir fmt --qualify <script>` is the graduation bridge: a
  span-precise AST-driven rewrite of bare names to their homes (single-home
  guarantee holds while trial resolution stays deferred), dropping `#loose`
  when done; splices and field accesses untouched.
- **Multi-line statements via logical-line reconstruction** (scripts
  only): a statement head starts at column 0; indented lines continue it
  and join with a single space; a blank line ends the statement; tabs in
  indentation are errors. **`|` can never begin a statement** — a named
  invariant with exactly two dependents, both statement-level:
  shell-style unindented pipeline continuations (`| where ...` at column
  0 under a command line) and column-0 match arms outside any pending
  binding. Inside an open block let, `|`-headed lines get no special
  treatment (corrected 2026-07-18; see the block-lets bullet). The single-line grammar then
  consumes each logical line unchanged, so mode decision and every
  existing rule apply per logical line. Type errors map back to physical
  `file:line:col` via per-segment source tracking; parse errors attribute
  to the head line (documented limitation). Not provided, deliberately:
  in-less nested `let` (still `let ... in` inside expressions),
  indentation-delimited scope, multi-line REPL input. Full design and
  gate verdict: DESIGN-multiline.md.
- **Comments are `//` to end of line** (string-aware; applies to script
  lines). Line one `#!` is skipped by the runner; `#` at line head is
  reserved for directives.
- **The statement rule**: *command-mode lines stream; every expression
  computes a value; values are bound or printed.* A pure expression
  statement must have type `unit` — anything else is a check error before
  line one runs ("this statement computes a `<ty>` and discards it — bind
  it, or pipe it to print"; `seq<unit>` gets the targeted lazy-effects
  text pointing at `Seq.iter`; `seq<FileRow>` names `^ls`). Command-mode
  statements are the single exempt form, `|`-chains included: they keep
  shell-shaped streaming output through the same renderer `print` uses
  (byte-identity pinned in e2e). The exemption is the parser's mode
  decision reified (`SCmd` vs `SExpr`) — syntactic, never name- or
  type-directed. Decision archaeology: a second exempt form (bare
  `sh`/`cmd` applications) was in the blessed draft and removed at
  proposal stage — deciding it required resolving `sh` to the real
  builtin inside a rule that must stay syntactic (the shadowing cliff
  `let sh = fun s -> s in sh "hi"` was the proof); bare `sh "x"` became
  the same discard error as any value. Superseded one review later
  (2026-07-18) by removing the `sh` builtin outright — see "Processes
  and the session" — after which POSIX one-liners are command-mode
  `sh -c "..."` lines: exempt, streaming, `| complete`-able.
  `let`/`type` statements print nothing, as before. `#loose` does not
  loosen this — resolution mode and output semantics are different axes.
  The rule is script-only: the REPL and `-e` keep `it`-style auto-print
  (ephemeral lines are not the PS output-pollution bug class; durable
  scripts are).
- **Script inputs**: `args : seq<string>` (argv after the script name) and
  `stdin : seq<string>` (lazy, one-shot — `Seq.collect` it if reused) exist
  only in scripts, not the REPL (the REPL owns its own stdin). Children
  inherit the process stdin unless a value is piped into them; the `stdin`
  binding reads the same underlying stream, so consuming it both ways is
  user error, as in any shell.
- **Exit codes**: 0 on success; 1 on check errors (before any effect) and
  on runtime errors (at the fault, prior effects done); 2 for CLI misuse.
  A raising external maps to generic 1 — the child's code does not
  propagate; use `complete` if the code matters.
- **CLI is unambiguous**: a positional argument is a script path, always;
  `-e` is an expression, always; `weir run <script>` is the explicit form.
  No content sniffing.

## Processes and the session

- **There is no `sh` builtin** (removed 2026-07-18; it shipped in the
  command-mode sessions as the blessed POSIX escape hatch). Decision
  archaeology: the statement rule exposed it as a stringly parallel
  surface — a library function pretending to be a shell. Bare effect
  lines needed `|> print`, `| complete` could never reach it (it was an
  expression, not a command), and it deferred resolution past
  check-everything-first. The external `/bin/sh` does everything it did
  with zero special-casing: command mode `sh -c "glob* && stuff"`
  (streams, completes, pipes like any command); expression positions
  use `cmd "sh" ["-c"; "..."]`. Consequences of a shell string remain
  the user's — backgrounded (`&`) children are orphaned to init when sh
  exits and no tree-kill can reach them (Session-1 lifecycle tripwires
  keep that analysis, now via the cmd spelling).
- **`cmd : string -> seq<string> -> seq<string>`** is direct exec: weir owns
  (prog, args). No shell, zero expansion — every argument is one argv entry,
  so there is no injection class (`cmd "echo" ["; rm -rf x"]` prints the
  string). Programs containing `/` resolve against `Session.Cwd`; bare names
  resolve against PATH.
- **Splice-defaulting soundness condition** (why "unresolved argv-position
  types default to string" is harmless) — justification updated when
  let-RHS command mode landed (2026-07-18; commands CAN now sit under a
  generalizing top-level `let`): splices bind their variable to string
  EAGERLY at check time, and command segments still exist only at line
  top level — never under a lambda — so no monomorphic parameter
  variable is in scope to default, and a freshly-instantiated
  outer-binding variable defaulted by a splice is local to the line's
  ctx. Nothing defaulted survives to be generalized at another type.
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
- **A top-level `let` RHS admits command mode** (2026-07-18; agent
  dogfooding produced the second independent hit of the gap within
  hours of the protocol starting): `let files = git ls-files` binds
  `seq<string>`; `|` chains and `| complete` work
  (`let r = grep -c x f | complete` binds the Completed record). Same
  conservative head decision as line heads. Expression-level
  `let ... in` stays expression-only, and the let-RHS command grammar
  STOPS at a bareword `in` — otherwise `let h = git log in h` would
  silently pass `in h` as argv (the cliff that kept `let...in`
  excluded); quote `"in"` to pass the word to a command from a let RHS.
- **A command-headed line commits to command mode**: once the first segment
  parses as a command, there is no backtrack to expression parsing for the
  rest of the line — errors after that point are command-line errors (this is
  why `git status | first 1 | complete` reports the marker rule instead of a
  generic expression error).
- **External-to-external pipes feed stdin**: `git log | grep x` wires the
  left stream (which must be `seq<string>`) into the right command's stdin.
  Piping into the shell is just `xs | sh -c "..."` now; `into` remains
  the expression-position spelling.
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
- **Builtin modules**: `Seq`, `Str`, `Option` — resolved by a checker arm on
  `Module.member` syntax; members are schemes instantiated per use; runtime
  sees mangled flat names. Resolution precedence on the shared syntax:
  value shadow, then module, then ordinary field access (`let Seq = ...`
  wins and behaves as a record — pinned). Bare aliases exist in loose mode
  for the pipeline hot path and common string ops; `Option` members are
  qualified-only in both modes (bare names are the data plane, Option is
  the control plane); `length` is qualified-only in both homes
  (`Seq.length`, `Str.length` — the old `strLen` collision resolved by
  qualification, superseding the strLen decision). Retired flat names:
  exact member names hint their home ("use 'Seq.groupBy'"); renamed ones
  (`strLen`, `substring`, `mapOption`, `tryIndexOf`) are plain unbound —
  accepted. Member-access-on-primitives (`s.Length`) stays a logged
  candidate. Strict/loose script modes and trial resolution:
  PLAN-modules-and-scripts.md (trial resolution deferred, design on file).
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
- **`File.read`/`File.write`/`File.append`/`File.exists`** (qualified-only,
  data-last, eager): the library-owned alternative to shell-redirect
  idioms. `write`/`append` return `unit` (their path-return was an
  explicit no-unit stopgap, retired the day unit landed). All relative
  paths resolve through the
  single shared helper `Session.resolve` — the same one used by spawns'
  working directories, `cd`, and PATH probes, so every filesystem touch
  agrees on what "relative" means.
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
  commands** — `let files = cmd "find" [...] in ...` used twice runs
  `find` twice, and the command may not be idempotent. Mitigation is backlog #2: a
  `collect` builtin (force once, materialize) as the standard escape hatch.
- Non-exhaustive matches are hard errors at check time (2026-07-18;
  they were warnings, and `match failure` was a deliberate runtime
  class — both retired together, see the booleans bullet). The
  deliberate runtime failure classes: boundary validation
  (`from json`/`from porcelain` reject malformed lines per line),
  arithmetic (division by zero), and **user-raised `fail "reason"`**
  (added 2026-07-18 from the agent-dogfooding ledger: `string -> unit`,
  halts with a located error and exit 1 — the checking-script idiom is
  `if bad then fail $"..."`). `printerr` (the stderr twin of `print`,
  same argument rule, revived from parked on the same evidence) keeps
  diagnostics off the data stream. Piping into an operator expression
  (`xs |> f == v`) is a targeted check error naming the precedence fix
  — operators yield values, never functions, so the shape is always
  wrong.

## Backlog (ordered by day-one impact)

0. **Block effect-sequencing** (`print "a"` mid-block — F#'s other half of
   light syntax): needs an ESeq node checked `unit` in non-final
   positions, the statement rule's discipline applied inside blocks.
   Revive on dogfood demand; until then a block is bindings + one result
   expression.
1. ~~**Measure algebra**~~ — superseded: **measures were removed
   entirely** (2026-07-18; see the tombstone section and the NOTES arc).
   The 2026-07-17 drop decision and the `no_unit_algebra` tripwire
   retired with them *and* the `*`/`/`-defaulting rule above.
(Done: `collect` — backlog #1 — and the exit-code policy — old #3 — landed
as `collect`/`complete`; see "Processes and the session".)

(Done: comparison/boolean completeness — landed with `<>` inheriting `==`'s
equatability and short-circuit `&&`/`||`, as pre-committed.)
