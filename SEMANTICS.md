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

- **Base types and literals**: `int`, `float`, `string`, `bool`,
  `Duration`, `seq<T>`, functions, declared records and unions.
  Literals: digit runs with prefix minus at operand positions
  (F#'s adjacency rule [D:prefix-minus] — `-5`, `2 * -3`; `x-1` and
  `x - 1` stay subtraction),
  strings with `\" \\ \n \t` escapes — plus RAW strings, F#'s two
  kinds [D:raw-strings], oracle probe-pinned BEFORE implementation:
  `@"..."` verbatim (backslashes literal, `""` = one embedded quote)
  and `"""..."""` (no escapes at all; closes at the FIRST `"""`, so a
  trailing extra quote is an error — FCS's verdict on the quad edges).
  Both are SINGLE-LINE (divergence row raw-single-line: the assembler,
  fmt's refuse-on-mismatch argument, and the highlighter's swallow
  analysis rest on strings closing before EOL). Rawness is a property
  of the literal KIND, never of position. `true`/`false`, and seq
  literals `[a; b; c]` (homogeneous; elements evaluate eagerly, once — unlike
  pipelines; `[]` is polymorphic `seq<'a>`).
- **Copy-and-update** [D:record-update] (2026-07-22, the re-mine's
  headline receipt): `{ r with F = v }` — multi-field `;`-separated,
  nested `I.X` sugar (paths are FIELD paths only; F#'s type-name
  capture is the update-path-plain row), general-expression sources
  (unparenthesized application included; bare match/if need parens —
  all FCS-probed before code). Update never ADDS fields. The result
  type IS the source's type: nominal stays nominal, and a row source
  keeps its OWN row variable — identity, not a fresh row — which is
  what makes `let bump r = { r with N = r.N + 1 }` generalize. The
  source evaluates ONCE (the plan's parser-desugar clause hit its
  stop-and-report: a parser desugar duplicates the source expression,
  so paths live in the AST and the checker walks them).
- **Field alignment** [D:field-alignment]: sibling bracket entries —
  record and type-decl fields, list elements — align EXACTLY with
  the FIRST entry (the opener line's content for inline openers;
  the first continuation entry for dangling Stroustrup openers;
  update headers anchor on their first FIELD, never the source).
  F#-parity throughout (probed: F# errors on off-by-one fields in
  decls, literals, and lists alike). Value continuations, closers,
  and explicit trailing-`;` lines stay alignment-free; fmt
  canonicalizes to the measured anchor, never offset arithmetic —
  fmt and the assembler agree by construction.
- **The one-glyph inventories** (each glyph's FULL role set, stated
  once — the fuzzer's `;`-probe found roles the docs implied but
  never listed as a unit):
  `;` has four roles — expression sequencing (`a ; b`, committed
  after consumption [D:seq-commit]), a literal argv word in command
  lines (warned), the list/record entry separator (its own grammar
  inside brackets), and the transform-eligibility boundary (only
  print-shaped siblings single-line with `;`; lets spell `in`).
  `|` has four — the match/union arm separator (committed
  [D:arm-commit], alignment-grouped [D:pipe-alignment]), the
  command pipe (stages are expressions; reifiers end chains), the
  bare-`|`-after-expression teaching fatal, and NOT an or-pattern
  (rejected, divergences row or-patterns).
  `$` has four — the binding splice `$x` (one word), the argv splat
  `$@xs` (N words [D:argv-splat]), interpolation `$"..."`, and the
  capture sigil `$(...)` (with `$e(...)` env and the parked `$@"`
  interp-verbatim opener holding its lookahead boundary).
- **The argv word law** [D:argv-splat]: `$x` is one word; `$@xs` is
  N words; nothing ever re-splits. The one-splice-one-word rule loses
  its silent asterisk — many things get the `$@` spelling (bash's
  `"$@"` semantics: each `seq<string>` element one argv word, never
  re-joined; the `$*`-class re-joining stays unrepresentable). Splats
  live in every command-arg position, enumerate ONCE at spawn (finite
  argv forces the seq — no explicit force at the splice), and vanish
  to zero words when empty (replacing bash's conditional-flag idiom).
  A splat cannot head a command (N heads is incoherent — the
  computed-head park) nor build a word mid-construction (N words
  cannot live in one). In a reified chain the splat RIDES, by desugar
  [D:splat-reifier-chains]: the segment's mixed argv becomes
  list-literal chunks folded with `Seq.append`, each splat's seq
  spliced whole — element-is-one-word carries through the builtin's
  argv (the same boundary as spawn-argv-build), and ESplat still never
  leaves argv in the AST (the confinement pin holds literally). The
  safety story GAINS: adversarial elements
  stay single words by the same law that made scalar splices safe.
- **The reifier output law** [D:exit-reifiers]: output goes where the
  meaning goes. Predicates and inspectors are quiet/captured because
  their output IS the result (`succeeds` → bool, `complete` → the
  Completed record); asserts and control-flow STREAM because their
  output is for the human — the result travels separately (`orFail` →
  unit/raise with the message, `exitCode` → the code as int, never
  raising). Four cells, one rule; a proposed reifier is tested
  against the law, not appended to a zoo. Streaming reifiers refuse
  capture/discard contexts at parse (`$(… | exitCode)`,
  `!(… | exitCode)`, district lines, bare statements — each error
  teaches the spelling that fits); `complete` remains the
  captured-code cell. As a BARE STATEMENT the discard dimension
  completes the table: a non-unit result thrown away is the discard
  family, erroring at the reifier stage — `succeeds`→bool ("use it in
  a condition"), `exitCode`→int (the `set +e; rc=$?` teaching),
  `complete`→Completed ("read a field"); `orFail`→unit is EXEMPT
  because unit IS the statement idiom (the assert), not by oversight.
  Four cells, four stated positions.
- **The consumed-separator law** [D:seq-commit] [D:arm-commit]: a
  consumed separator COMMITS to its continuation — `;` to its next
  element, an arm's `|` to its arm, a record literal to its fields
  once the `ident =` head discriminates it from an update. A failing continuation reports at
  its own site; the parse never silently backs out past a consumed
  separator into an alternative that re-reads the tail in the wrong
  scope (the verdict-split mechanism) or manufactures a
  completed-expression shape for the bare-pipe fatal (the span
  misfire). Committed-choice at exactly the separators whose silent
  stop had receipts.
- **Multiline lambdas** [D:multiline-lambda]: a `(fun ... ->` whose
  paren is unclosed at EOL opens a BODY BLOCK closed by its own `)`.
  Ordinary block rules apply inside (block lets, sibling `;`,
  compounds, districts, blank transparency); body lines sit at or
  right of the OPENER line's indent (left of it is a named leak
  error — weir-stricter than F#'s enclosing-context floor, the
  lambda-body-offside row; body AT the opener's indent is a
  continuation, F#-parity). The closer attaches to the last body
  line or stands alone at any indent (the col-0 law suspends while
  the lambda is open); EOF with the paren open errors naming the
  opener. On pop the statement level RESTORES to where the lambda's
  statement started — a following block sibling sequences with `;`
  while a deeper application argument (a fold's init under its
  `(fun` line) still continues the application. Lambda bodies
  inherit the letCmdOk spine (command block-lets legal on a let-RHS
  spine) and lambda params extend the ambient resolver
  (params-shadow-PATH at check time too). Sigil interiors were
  already physically spannable by continuation — the
  single-LOGICAL-line law holds unchanged.
- **Pipe alignment** [D:pipe-alignment]: sibling `|` lines — match
  arms, union cases, chain stages — must align EXACTLY on their
  group's column (the first pipe after a non-pipe line sets it);
  off-by-one is a hard error naming the sibling column where F#
  merely warns (the warning-vs-error strictness family). Arms must
  sit at or right of their match head (F# errors there too), and a
  SHALLOWER arm offside-closes nested compounds — the nested-match
  return F# reads from the columns assembles correctly instead of
  flat-joining every arm to the innermost match. Groups may sit
  deeper than the head uniformly (fmt canonicalizes them to the m).
- **Bracket continuation** [D:multiline-brackets]: inside an open
  `{` or `[`, line breaks separate entries — F# light's own rule,
  one assembler mechanism (a bracket STACK, kind + opening line;
  the innermost bracket picks the separator rule). Record literals
  key on `Ident =` field starts (a value may continue), TYPE
  declarations key on `Ident :` / `[<` (so type decls wrap, and a
  preceding-line attribute joins its field with no separator),
  lists treat every line as a new element unless the previous line
  dangles an opener, separator, or operator (wrapped elements
  continue). A mismatched closer errors naming BOTH sides; a blank
  inside an open bracket is TRANSPARENT [D:blank-in-brackets] —
  the comment-line class's second member (outside brackets, blank
  still ends the statement — the divergence row's core). The
  statement-head guard bounds the unclosed-bracket runaway: a
  col-0 `let `/`type ` while a bracket is open errors naming the
  opener and its line. House style is
  Stroustrup [D:fmt-stroustrup] (dangling opener, entries one level
  in, closer at the opener line's indent); the aligned style stays
  accepted — fmt canonicalizes indentation within either, never
  moving brackets between lines. Brackets never
  engage inside strings (the scanner guarantee) or districts
  (command text). Parens spanning lines stay parked.
- **Seq patterns** [D:seq-patterns]: F#'s LIST-pattern spelling on
  weir's one sequence type — `[]`, `x :: rest` (right-associative
  chains; cons binds tighter than comma, looser than constructor
  application), `[a; b]` fixed arity; element positions nest full
  patterns. FORCING is statically bounded (maxArity+1 probes,
  knowable before anything runs) and MEMOIZED ONCE per match
  (Seq.cache): arms probe one buffer, `rest` binds the buffer
  suffix plus the untouched tail, effects run once TOTAL, infinite
  seqs are safe. Exhaustiveness: `[]` + irrefutable-cons complete
  (F#'s list rule, flat v1 — chained-cons completeness wants a
  wildcard); fixed-arity literals never complete alone. All seq
  patterns are refutable → banned in binders. Scrutinees must
  RESOLVE to seq (params ride the ctor-pattern-scrutinee bound).
  F# rejects the spelling on seq — the seq-patterns divergence row
  (the param-ful-RHS class: existing law reaching a scope F# left
  empty).
- **Attributes** [D:attributes]: record fields carry `[<Name arg>]`
  lists (F#'s attachment syntax — FCS parses the adopted shapes;
  `;`-separated, literal args only; same-line or on the line above
  the field — the F# house style, riding the bracket continuation
  [D:multiline-brackets]). The
  semantics diverge invisibly from F#: no reflection, no runtime
  metadata — attributes are CHECK-TIME data on the record
  declaration, and erasure is absolute (no attribute reaches eval,
  Value, show, json, or equatability; an attributed record unifies,
  compares, and prints exactly as a bare one). The name set is a
  closed registry (`Short`/`NoShort`/`Default`); an
  unknown name is a check error with a did-you-mean — no silent
  decoration, ever. Validation happens at ATTACHMENT (name
  registered, args well-formed, explicit shorts collision-checked
  across fields), binding happens at CONSUMPTION — an attribute no
  consumer reads is legal-and-inert, like a comment. Help text is NOT
  an attribute: `--help` reads a field's `///` doc first line
  ([D:doc-help]), so `[<Doc>]` retired from the registry. Consumers
  (typed argv reading Short/NoShort/Default) shipped;
  `Positional` was registered as a not-yet, then DROPPED (the rider —
  one receipt, contract-mimicry, nothing blocked). Attachment beyond
  record fields rejects with "attributes attach to record fields".
- **Indexers** (2026-07-20): `xs[i]` desugars to `Seq.item i xs`
  (raising; `tryItem` is the safe sibling). The F# 6 dotless-indexing
  whitespace rule applies verbatim: NO space = indexing, a space =
  application of a list literal (`Seq.sum [1; 2]` unchanged) —
  immediacy is decided by span adjacency, so the atoms' whitespace
  handling is untouched. Chains (`m[1][0]`), field composition
  (`row[0].name`), sigil composition (`$(git branch)[0]`), and the
  underscore shorthand (`_[0]` = `fun x -> x[0]`, extending `_.Field`)
  all follow from the desugar. weir note vs F#: F#'s `seq` has no
  indexer (only list/array) — weir's one sequence type takes it,
  a permissiveness in weir's favor recorded here rather than in the
  divergence table (verdict-invisible: both languages accept the
  translated shapes).
- **Range literals**: `[a..b]` inclusive ascending, `[a..step..b]` stepped;
  descending only via an explicit negative step (`[10.. -1 ..1]`;
  range positions predate general prefix minus [D:prefix-minus] and
  additionally allow the SPACED negative that adjacency rejects
  elsewhere). Empty when `a > b` in the ascending form. Pure
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
- **Equality IS polymorphic through inferred constraints** (SUPERSEDED
  2026-07-21, type classes Session A — the original rule read "`==`
  rejects unresolved variables", making `let eq a b = a == b` a
  definition-time error): the constraint now RIDES the variable
  (`Eq a => a -> a -> bool`) and rejection moves to the USE site
  (functions/seqs) or to statement end if nothing determines the type.
  The reject-don't-guess posture survived; only its timing moved. See
  the type-classes section.
- **Generic declarations**: `type Option<'a> = Some of 'a | None` and
  `type Pair<'a> = { Fst: 'a; Snd: 'a }` — unions and records both take type
  parameters. Cases carry one payload, which may be a TUPLE
  (`Case of int * string` — legal since the tuples reversal,
  2026-07-21; the original single-payload rule was the no-tuples
  rule's corollary and retired with it). Applied types unify argument-wise with
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
- **The prelude** is plain weir source (Option) evaluated through the
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
error ("units of measure are not supported"); the recognizer retires at the
1.0 grammar freeze. `FileRow.Size` (truncated megabytes) was deleted
with the measures; `bytes : int` is the survivor — field names carry
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
  total — the match-failure runtime class no longer exists.
  Reachability is coverage's dual and the same severity (2026-07-21,
  upgraded from a warning on a live footgun): an arm below an unguarded
  catch-all is a HARD error, reported AT the catch-all — under the
  casing law a typo'd constructor (`| zClean ->`) silently becomes a
  variable binder that swallows the match, so the variable form hints
  against the scrutinee's cases. Grammar note: `-`
  no longer matches when followed by `>`, so guard expressions sit
  naturally before `->`. Keywords if/then/else/when joined the reserved
  set — and therefore can never be command heads. Warnings surfacing:
  the runner and `-e` print check warnings to stderr (found during this
  session — they were silently dropped before; the REPL always showed
  them); warnings never block execution.
- **The Regex pattern** [D:regex-pattern]: `| Regex "lit" binder ->` —
  the first weir-only match form. Literal-only (computed patterns are
  `Str.isMatch`/`Str.rmatch` on the expression side); compiled at
  CHECK time against a literal-keyed cache shared with eval, so an
  invalid regex is a check error and the binder arity is verified
  against the ENGINE's capture count (non-capturing `(?:...)`
  excluded): `()` for 0, one name for 1, a tuple of names for n.
  Groups bind `string`. Refutable — never completes a match, banned
  in binders. The literal is RAW-ONLY [D:raw-strings] — `@"..."` or
  `"""..."""`; an ordinary escaped string in Regex position is a
  check error with the hint. A KIND is rejected at one position,
  casing-law-style — no string's MEANING varies by position, so the
  strings-uniform law holds. (Archaeology: the original landing
  shipped a positionally-raw lexer; the shout-if flag on that
  unstated decision drew the review that concluded rawness is a
  STRING property, and PLAN-raw-strings retired the positional rule
  the same week — the clause worked, one exchange late.) Explicitly
  NOT active patterns: one bespoke checker arm, and the
  user-active-pattern door stays closed.
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
  `fun f -> f.name == "tmp"` binds the field's type to `string` (this is the
  mechanism behind the §1.2 conflicting-demands rejection); only a type still
  unresolved *after* unification is rejected.
- Binary operators on two unresolved type variables: every operator with
  a UNIQUE typing defaults its operands — `*` `/` `-` `>` `<` `>=` `<=`
  to `int`, `&&`/`||` to `bool` (extended 2026-07-20 when the parameter
  sugar made var-var operands common; `-` and the comparisons had been
  left out of the family by accident of history). `+` alone stays an
  error: int-or-string is a guess weir refuses to make — anchor one
  side. Named as an F# divergence (F# defaults `+` to int).
- **The command district** (2026-07-20, sequel plan): line-end `!`
  announces that the indented lines below are COMMAND LINES, one per
  line — the assembler wraps each as `!(line)` and joins with `;`
  ("the marker distributes itself"); everything downstream is shipped
  machinery. District lines are command-mode text exactly (splices,
  quotes, in-line pipes; leading-`|` lines continue the previous
  command); expressions/`let`/nested sigils inside are errors with
  named messages; extent is strictly-deeper, closing at-or-left of the
  marker (a dedented `else` rejoins its `if`; blank lines end the
  statement as everywhere). Budget note: shipped at 2× the assembler
  line budget on human review — the metric lesson is recorded in
  NOTES (line count proxies the real target, parallel-invariant
  logic; the district REPROCESSES closing lines through the one rule
  set instead of duplicating rules per mode). **The sugars ledger** —
  command-in-expression has exactly these spellings: `$()`/`!()`
  atoms (anywhere an expression goes) and bare-command let-RHS
  (capture, least ink where legal). The `!`/`!name` DISTRICTS RETIRED
  [D:district-retirement] — the arming rule made the mode gate
  unnecessary and `within env` covers the overlay; the retired
  spelling is a TEACHING error, and the $e()/!e() sigils STAY
  (fragment and single-command uses have no block spelling).
**`within` — scoped resources** [D:within-scopes]:
  `within <kind> <binder>` + an indented block, an ordinary
  EXPRESSION: the body is a plain expression block (statements
  sequence, commands arm, the last expression is the value), so
  MODE-FROM-POSITION needs no rule — expression position yields the
  body's value; statement position demands unit through the existing
  discard rule, and a commandish tail arms through it. THE KINDS are
  asymmetric by design — `tmp` PRODUCES (a binder, no arg); `cd` and
  `env` CONSUME (one ATOM argument, no binder; parenthesize a
  compound). `tmp` binds a fresh unique directory (platform-native
  string), REMOVED on every managed exit — normal and raise alike
  (pinned); a hard interrupt is the stated gap (SIGINT terminates
  without running cleanup — the `weir-tmp-` prefix keeps leftovers
  identifiable; the job-objects park's reasoning). `cd <path>`
  resolves against the current cwd (nested relative scopes COMPOSE),
  errors at entry naming the resolved absolute path when missing (the
  block never runs), restores on every managed exit — the interrupt
  gap is HARMLESS here (the process is gone; nothing observes its
  cwd), stated because it makes tmp's gap the real one. Inside
  pmap/piter workers a cd scope nests within the worker's own forked
  cwd. `env <vars>` (seq<EnvVar>) pushes an ambient overlay CHILD
  SPAWNS see — weir's own Env.load is untouched, so a scope around
  pure expression work is legal and pointless (allowed, not an
  error); layers apply outer-first under any explicit sigil env, so
  on collision the inner key wins and outer keys survive (pinned).
  Nested scopes of every kind restore inner-first. The
  binder joins bindings-beat-PATH for the block (the patLeafNames
  family's fifth site). The keyword-over-sigil reasoning lives in
  DECISIONS: $/! encode a TOTAL two-way distinction (value/effect),
  so scope KINDS want names, not a third glyph.
**When are sigil wrappers mandatory?** — every row OBSERVED, not
  predicted [D:within-scopes]:

  | position | bare command | `$()` | `!()` |
  |---|---|---|---|
  | top-level statement | effect (streams, raises) | discard ERROR + "drop the $( )" | unit effect |
  | interior statement | effect (armed) | seq-unit ERROR + "drop the $( )" | unit effect |
  | final expression of a block | capture value | same (redundant) | unit (MEANING-CHANGING) |
  | plain let RHS | capture | redundant | unit (meaning-changing) |
  | record field value | ERROR (DECIDED no: a field's RHS is newline-bounded and a bare command would silently extend argv across the wrap — the dedent-swallow class multiplied) | works | works |
  | function argument | "unbound variable" ERROR | works | works |
  | interpolation hole | "unbound variable" ERROR | works | works |
  | list/seq element | "unbound variable" ERROR | works | works |
  | splat interior | "unbound variable" ERROR | works | — |
  | `if` condition | parse ERROR at the bare pipe | ERROR too — BIND FIRST (`let ok = … \| succeeds`; inline wants a then-stop argv context, reported with size) | — |
  | `within` body interior / final | effect / capture | as any block | as any block |

  THE RULE the observations support (the draft's whole-value-slot rule
  died on the record-field row): **bare commands are legal at
  STATEMENT positions and at a let-RHS/block-final value; inside any
  expression FRAGMENT — an argument, a hole, an element, a field, a
  condition — the wrapper is mandatory**, and the error a bare
  command produces there is the unbound-variable teaching on its head
  word PLUS the wrap-it repair ("wrap it: $(git …)") when the head is
  applied [D:district-retirement]. REDUNDANT ($() on a let-RHS) is allowed with no lint;
  MEANING-CHANGING (!() gives unit where bare gives seq<string>) is a
  choice, not redundancy. `$()` is now POSITION-INDEPENDENT: it means
  CAPTURE everywhere, and a discarded `$(cmd)` names the repair (drop
  the wrapper) [D:district-retirement]. The if-condition takes NEITHER
  spelling inline — bind the verdict first.

  **The interior-arming rule** [D:interior-arming], ONE rule with one
  carve-out: a command line in INTERIOR-STATEMENT position (a
  non-final statement of any block — if bodies, lambda bodies,
  block-lets, for bodies) is an EFFECT (streams, raises on nonzero,
  unit); a command in FINAL/value position stays CAPTURE-typed (the
  block's value) — and when the context demands unit of that final
  position (an if body, an iterated lambda), the check direction arms
  it too. Top-level statements are an INSTANCE of the interior row,
  not a separate rule. THE CARVE-OUT: a script's body is a statement
  sequence, never a block with a value — every top-level command is
  non-final by construction, so a script's last line still streams
  and raises. Plain parens stay expression territory ($()/!() are the
  command parens); the statement rule is unchanged for non-command
  lines (a non-unit value still seq-unit-errors). Raise timings
  differ observably and are pinned: armed raises immediately,
  capture raises at force.
  Every command head is a LITERAL program name, resolved at check
  time — there is no computed-head tier [D:drop-command-builtins]. To
  swap tools by a runtime condition, branch the whole command line
  (`if hot then rg pat else grep pat`). Computed ARGV is not computed
  head [D:keep-reify-builtins]: the check-time-resolution law is about
  heads and remains total — a command whose argv is a runtime value
  spells `$author(git $@argv | complete)` [D:splat-reifier-chains],
  literal head, splatted argv, sigil env. The reifier builtins
  (`completed` family) are RETIRED [D:drop-reify-builtins] — redundant
  once the splat rode the chain; the desugar targets live on as
  un-typeable `|`-prefixed keys the chain grammar alone can reach.
- **Command-mode sigils** (2026-07-20): `$(chain)` captures a command
  chain's value in expression position; `!(chain)` desugars to
  `(chain) |> print` — eager, streaming, raising, unit. DESUGAR-ONLY:
  zero new AST nodes, zero checker surface. Interior grammar is
  IDENTICAL to a statement-level chain (segments, splices, pipes,
  `| complete`, command-callable heads — the explicit sigil makes
  intent unambiguous where the bare let-RHS could not). Heads resolve
  at CHECK time — a typo'd program inside `!()` fails before line one,
  which bash's `$()` cannot do. Nesting rides splices (balanced
  parens; depth unrestricted). Bash priors: `$()` ALIGNS (recorded —
  priors that help get named too); `!()` diverges (bang-sigil row;
  invisible to the F# oracle, carried by behavioral pins). Computed
  program names have no spelling — every head is a literal
  [D:drop-command-builtins] (spliced heads parked). The
  eager-unit anti-idiom (`let cleanup = if ...`) is replaced by bare
  `if` statements.
- **Block effect sequencing** (2026-07-20, PLAN-sequencing-and-args
  Session 2): `e1 ; e2` is an expression — every element but the last
  must be UNIT (hard error, the statement rule's discipline inside
  expressions). The assembler joins same-indent block siblings with
  `;` (non-pipe, non-let lines; let-closure `" in "` takes priority and
  sequencing resumes after) — F# light syntax's other half, same
  token-insertion technique as block lets. **Stop-and-report
  amendment**: the blessed lowest-precedence `;` collided with the
  flat-join model at the flagship shape — `if c then run1 ; run2`
  would have sequenced the runs OUTSIDE the if, silently
  unconditional. `;` is therefore GREEDY in body positions (then/else,
  arm and lambda bodies, let-in bodies, parens): it binds into the
  block, matching the source shape it assembles from; sequencing after
  an `if` requires parenthesizing the if. Named divergence vs F#
  verbose grouping (semicolon-greedy-bodies); the oracle referees the
  block shapes as Same (F# light accepts them natively — the fidelity
  gain the plan predicted). The other boundary: `;` in a COMMAND line
  stays a literal argv word (the no-injection pin) with a check-time
  warning naming the fix (semicolon-command-argv row — a bash-prior
  divergence, invisible to the F# oracle by construction).
- **The offside close** (2026-07-20, PLAN-grammar-consolidation — the
  bicep translation's bite fired the greedy-`;` revisit metric): the
  assembler tracks open `if`/`match`-headed pieces as an offside
  stack; a sibling arriving at a head's indent or SHALLOWER closes
  that compound by paren-wrapping it (a balanced, line-structural
  unit), so same-level siblings sequence AFTER the conditional while
  deeper lines still join into its body, where greedy grouping is
  exactly right. `else` and `|` pieces extend a compound instead of
  closing it (same-indent `else` used to be a parse error — the fmt
  refusal's root). The review's discovery: the bite class had a
  SILENT member — a same-level sibling after `if c then eff` was
  swallowed into the then-branch, conditional execution the user
  never wrote. Multi-line shapes now group F#-faithfully
  (oracle-pinned); greedy `;` survives only in single-line-typed
  bodies and continuation joins (semicolon-greedy-bodies row amended).
  Candidate (b) — reverting to lowest-`;` — died on layer separation:
  it needs parens INSIDE pieces at `then`/`else`/`->` positions,
  grammar-interior surgery the assembler must never do.
- **Multi-line record literals** (2026-07-20, same session, receipt
  count 2): inside an open `{ }` the assembler is in record-
  continuation mode — the separator inserts before FIELD-START lines
  (`Ident =`), so a field's value may open on the next line and a
  trailing `;` stays legal (refined 2026-07-20, the fixture sweep's
  catch); the sibling/let/district rules are inert (records are
  expressions, not effect blocks), and closing `}` may sit at any
  column including 0. Blank line or EOF with the
  brace open is a located error naming the brace. The brace counter
  is string-aware (stripComment's scanner rules), so interpolation
  holes and quoted command args never count. Weir is
  indentation-blind inside braces where F# still applies offside
  (record-fields-ignore-indent row). RECORDS ONLY — list literals
  stay single-line until their own receipt arrives.
- **`exit n` propagates an exit code** (2026-07-20, bicep
  receipt: the F# original's `exit code`): `int -> unit`, typed like
  `fail` (no checker surface); raises an intentional-exit carrier the
  runner returns SILENTLY — `fail` keeps exit-1 with a located
  message, `exit` is the propagation spelling
  (`if r.exitCode <> 0 then exit (r.exitCode)`).
- **`let f x y = e` defines a curried function** (2026-07-20 — the
  corpus-mining session's top yield became a feature the same day, on
  agent-prior evidence: F#'s most common line shape). Pure parser
  desugar to nested lambdas, both let forms; generalization and the
  HOF restriction flow through unchanged. Params are plain idents —
  unit/pattern params and annotations stay rejected — and a param-ful
  let takes an expression RHS only (a command line under a lambda
  would break the splice-defaulting soundness invariant). `rec` and
  `mutable` became reserved words with the sugar: the oracle caught
  `let mutable x = 1` silently parsing as a function named `mutable`
  minutes after the sugar landed — F#-binding forms must fail loudly,
  not bind strangely.
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
- **Literal patterns and () params** (2026-07-21, PLAN-literals-
  thunks Session 1): int and string literal patterns (`| 0 ->`,
  `| "yes" ->`, nested in constructors) with F#'s completion rule —
  literals never exhaust a match; a var/wildcard arm closes it (the
  severity stays weir's hard error: exhaustiveness-hard-error row).
  The guard idiom remains legal, no longer the only spelling
  (no-literal-patterns row RETIRED). `()` is a unit PARAM
  (`let cleanup () = ...`, `fun () -> ...`) that PINS its type —
  checker arm, not sugar: an unconstrained param would generalize
  and `cleanup 5` would typecheck (tripwired). `()` is also an
  irrefutable match pattern. Params are plain idents or `()`;
  pattern params stay out.
- **Tuples** (2026-07-21 — the REVERSAL of records-are-the-product;
  archaeology in NOTES): types `int * string`, literals `(a, b)`,
  patterns `| (x, y) ->`, arity 2+, nested. Componentwise Eq and
  Show through the classes; NO ordering (no-tuple-ord row — sortBy
  keys stay scalar). Multi-payload constructors are legal (`Case of
  int * string` — the single-payload rule was the corollary and
  retired with it). NOT spliceable, NOT Env.load field types, NOT
  json (the field whitelist already rejects — reject-don't-guess).
  Exhaustiveness is bounded: only an all-irrefutable tuple arm
  completes a match (tuple-exhaustiveness-bounded row). Params stay
  idents-or-`()`: `fun (a, b) ->` is a named divergence
  (no-pattern-params); components come out via match. STYLE (the
  reversed decision's rationale, surviving as a rule): records for
  anything with NAMES; tuples for transient pairs — returns, zip,
  pairwise, patterns.
- **The casing law: lowercase binds, uppercase declares** (2026-07-21,
  mini-session). Binders start lowercase at EVERY position — both let
  forms, lambda params, param sugar, pattern-binder components — with
  the hint naming the record-field escape (`let region =
  cfg.AWS_REGION`; fields keep free casing for the Env.load verbatim
  contract). Match patterns are deliberately untouched (uppercase
  remains a constructor there — the law's mirror half, standing since
  generics; module names complete the triple). Value-shadows-module
  is now GRAMMAR-DEAD (`let Seq = ...` rejects at the binder; the
  EField precedence logic stays as defensive depth), and the
  binders-session PCase fall-through hack became unrepresentable. An
  unknown uppercase name in a binder pattern gets the casing hint; a
  KNOWN constructor gets "this pattern can fail; use match" (intent-
  aware diagnosis). Honesty note, in the divergence row's own words:
  this is the strictness family's first STYLISTIC member — the
  payoff is disjoint name classes, not a prevented bug class. The
  constructor-vs-module collision (type T = Env of int) remains
  DECLARABLE — the Env.load unshadowed-guard stays for exactly that
  case (the sentinel guards were never this law's payoff; they
  defend LOWERCASE shadowing, which stays legal).
- **Irrefutable-pattern binders + bare comma** (2026-07-21,
  PLAN-pattern-binders): destructuring lets (`let x, y = ...`,
  `let (x, y) = ...`, nested with parens, `_` and `()` components,
  `let _ =` explicit discard) and PARENTHESIZED pattern params
  (`fun (k, v) ->`, `let dist (x, y) =` — parens required, as F#).
  Refutable patterns in binder position are a HARD ERROR ("this
  pattern can fail; use match") where F# warns-accepts — the
  no-pattern-binders row's remaining content; its shipped shapes are
  Same, completing the arc the retired no-tuples row opened
  ("destructuring is the real scope"). One statement binds MULTIPLE
  names; generalization is PER NAME (constraints scooped per name;
  one component can be polymorphic while another is ground — pinned).
  BARE COMMA amendment: `,` is the tuple constructor at F#'s
  precedence — below `|>`, and (weir-only cell, decided) ABOVE `;`,
  so `a, b ; c` is `(a, b) ; c`. The tuples session's parens-only
  rule is AMENDED with its two original reasons addressed: command
  argv is untouched by construction (barewords keep commas, pinned
  both sides), and the `f x, y` precedence footgun is imported
  KNOWINGLY, F#-faithful. Bare commas build tuples in BINDER, RHS,
  SCRUTINEE, and PATTERN positions — one rule, four positions, no
  exceptions (`match a, b with | Some d, _ when g -> …` is the
  four-in-one spelling; the guard sits outside the tuple). REPL: one
  destructuring line reports each binding on its own line.
- `_.Field` is sugar for `fun x -> x.Field` (parser-level desugar; requires at
  least one field, like F#).
- Constructor names must start uppercase; that is what distinguishes
  constructor patterns from variable patterns in `match`.
- **The encoding law** [D:encoding-law]: weir encodes and decodes
  UTF-8 at EVERY boundary — what gets read, written, hashed, and
  base64'd is UTF-8 bytes. The law is about boundaries, not
  representation (in-memory strings are the runtime's business). BOM
  detection applies where a stream may declare otherwise: capture
  decoding strips a UTF-8 BOM, switches on a UTF-16/32 BOM, and maps
  an invalid byte to one U+FFFD (the capture rules, pinned). Strict
  contexts (`Str.fromBase64`) reject invalid UTF-8 as an ERROR
  rather than manufacturing replacement characters — corruption must
  not wear a success.
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
- **Command lines** run a program from STATEMENT position: stream,
  raise on nonzero, unit. The former `run`/`cmd` expression-position
  builtins are RETIRED [D:drop-command-builtins] — a program in
  expression position is captured with `$()`/`!()` or a reifier.
  `| complete` remains the spelling when the exit code is data.
  **`Args.flag`/`Args.value`** are script-only argv
  scanners (empty-string short form for long-only flags, pinned);
  `Seq.contains/exists/forall/item/tryItem/skip` complete the access
  family — `contains` requires equatable elements (the sentinel
  ledger is CLOSED: retired into constrained schemes, PLAN-type-classes
  executed; print alone stays a sentinel by design
  three; ledger in NOTES).
- **`show : 'a -> string`** (2026-07-20; resolves the collision parked
  in the unit-print plan, choosing the builtin over widening `print` —
  print's data-plane contract stays intact). The debugging renderer:
  any value renders REPL-shaped (the SAME `formatValue` the REPL uses —
  one renderer, deliberately lossy: strings quoted, seqs truncated at
  20). Showable = no function anywhere in the type, checked recursively
  through records/unions/seqs/rows ("show cannot render functions" —
  a `Some (fun ...)` is caught in the payload). Bespoke checker arm on
  the print-family sentinel discipline; bare-value `show` defaults to
  `string -> string`; a `let show = ...` shadows it entirely.
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
  re-enters the mode decision. **The pipe rule [D:pipe-rhs-decides] — the
  RIGHT-HAND side decides the glyph:** `|` when the stage is a program or a
  reifier (command grammar), `|>` when it is a function. That is the whole
  table (`cmd|cmd`, `expr|cmd`, `cmd|reifier` take `|`; `cmd|fn`, `expr|fn`
  take `|>`), symmetric, no exceptions. So `git log | grep x |> first 2`
  flows external→external (`|`)→function (`|>`). A mismatch teaches both
  ways: `cmd | fn` → "'|' chains commands; pipe expressions with '|>'";
  `expr |> cmd` → "'|>' applies functions; feed a program with '|'", each
  anchored on the glyph/program. Adapters (`from json`/`from porcelain`/`to
  json`) are functions → `|>`.
- **Arguments**: barewords run until whitespace, `|`, `(`, `)`, quotes, `$`, or
  end of line — `/`, `.`, `-`, `=`, `%` are ordinary characters. `"..."`
  (with escapes) and `'...'` (raw) produce single args. `$name` splices a
  binding; `(expr)` splices an expression result. **Splice typing rule**:
  arguments must be strings, ints, or bools — rendered as single
  argv entries, never re-split (no injection class; same ownership line as
  a command line); an unresolved argument type defaults to `string`. No adjacent-token
  concatenation: `foo$bar` is two args.
- **`[` never heads a command** (decided 2026-07-18): quotes end a
  bareword, so a line-head string list (`["a"; "b"] |> ...`) would
  otherwise tokenize to bare `[` and PATH-hit `/usr/bin/[` — discovered
  as a capture bug during the unit-print session. The head rule excludes
  `[`-initial words in both the bare and `^`-forced paths (forced is a
  hard error naming the alternative); `/usr/bin/[` stays reachable by
  its full path (only `[`-initial barewords are excluded), and `[` remains an ordinary character inside command
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
  literal argument today, it does not error): no glob *expansion*
  (discovery is the FUNCTION `Path.glob` [D:path-glob] — typed,
  visible, outside this exclusion's rationale), no
  redirects (`>`/`>>` — argv words with a warning naming the File
  spelling), no env-var assignment prefix (`FOO=1 prog`), no `&&`/`;`
  chaining in command mode. Weir routes streams by application: `>`
  means comparison and `>>` means composition everywhere they mean
  anything; redirection is `File.write`/`File.append` at the end of a
  pipe [D:composition-operators]. Also: `let`-headed lines are always expression
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
  when done; splices and field accesses untouched. **`weir fmt <script>`**
  (2026-07-18) is the canonical formatter, v1: structural indentation
  normalized to 4 spaces per block depth (computed from the same
  pending-let structure the assembler tracks), trailing whitespace
  stripped, comments and token spacing verbatim, column-0 pipe style
  respected; `--check` exits 1 for CI gating. Safety property: the
  formatted body must re-assemble to identical logical lines or fmt
  refuses to write (trailing-whitespace-normalized comparison — never
  significant, strings are single-line). V2 [D:fmt-respace]
  (2026-07-22, on the update-example receipt): bounded intra-line
  respace — collapse space runs, pad record braces, tidy `;` — under
  a PARSE-SHAPE guard: each statement is parsed before and after
  under Script.assumeResolver and must sexpr-match, or that statement
  reverts to its pre-respace text. The guard is what makes command
  argv sacred (`echo {a}` reverts; `"x" ; echo` tidies because quoted
  tokenization provably keeps `;` separate). String interiors (all
  four kinds), leading indent, and pre-comment alignment gaps are
  untouched by construction. Re-FLOWING (line breaking) stays parked.
- **The REPL is for EVALUATING weir** [D:repl-quality], not a login
  shell. In scope, deliberately: Tab completion (command heads from
  PATH + bindings, filesystem paths, module members/fields, keywords —
  all from `Complete.fs`, the one brain; single-Tab completes a unique
  match else lists; it NEVER executes a command to compute a
  suggestion), Ctrl+R history search (fzf when present, a minimal
  built-in reverse-search otherwise), persistent per-line history
  (`$XDG_STATE_HOME/weir/history`, `0600`, consecutive-dup dedup, a
  size cap front-truncated at load), and an inert JSON config
  (`$XDG_CONFIG_HOME/weir/config.json`: history size/dedup/path and the
  finder's flags; unknown keys rejected with did-you-mean). OUT of
  scope, deliberately (the login-shell competition, not a matter of
  effort — each pushes toward the dynamism that check-before-effects
  forbids): job control (`&`/`fg`/`bg`/Ctrl+Z), prompt customization
  beyond a static string, aliases, `cd -`/dirstack, keybinding config.
  The config FORBIDS code, aliases, prompt *commands* (a static string
  is fine; one that runs `git branch` is not), imports, and env
  manipulation. **Scripts never read the config** — that is what keeps
  `weir script.weir` meaning the same thing on two machines, and the
  reason weir has no bashrc; the config lives in the REPL module, so a
  script provably cannot reach it. **The buffer is two-dimensional**
  [D:repl-multiline]: lines + a (row, col) cursor, the horizontal
  machinery per line unchanged. **Enter submits exactly when the
  statement is COMPLETE** (the assembler answers structure — open
  brackets, pending bindings; a parse failure at the very end means
  more input wanted — where other shells approximate, weir asks its
  own parser) and inserts a newline when it is not; Alt+Enter/Ctrl+J
  force a newline (formatting — an entry stays ONE statement). Up at
  the first line reaches history, Up elsewhere moves the cursor (the
  fish convention; Ctrl+R is the explicit path from anywhere); Down at
  the last line goes forward only while already browsing history —
  a fresh buffer's last line is a no-op. Esc/Ctrl+C abandon the whole
  buffer. History entries are LOGICAL statements: a multi-line match
  recalls whole and re-edits; on disk one entry per line
  (newline-escaped, the cap counts entries); search UIs show a
  one-line ⏎-joined display form mapped back to the full entry.
  A submitted multiline entry runs through the same assembler a
  script uses — identical lines mean identical statements. Piped
  (non-tty) input never enters the editor.
- **Multi-line statements via logical-line reconstruction** (scripts
  only): a statement head starts at column 0; indented lines continue it
  and join with a single space; a blank line ends the statement; tabs in
  indentation are errors. **A dedenting
  line resolves to the enclosing statement level whose column it
  matches — innermost first** [D:dedent-join]: a statement after a
  `within`/`for`/`if`/`match` body continues at the level it aligns
  with (the block closes, paren-wrapped, exactly as conditionals
  always did — within/for heads joined the same compound machinery,
  no new stack); a column matching NO enclosing level is a located
  teaching error (the dedent floor — the silent argv-absorption this
  replaced cannot return). Open lambdas/brackets/parens still take
  dedented continuations (the exemption class, unchanged).
  **Comment-only lines are transparent**
  (F#-faithful; fixed 2026-07-20 — they used to strip to blank and end
  the statement, breaking any block with an interior comment; oracle
  pin). **Parse errors translate to physical `file:line:col`** through
  the same segment table type errors always used (fixed 2026-07-20 —
  they attributed to the head line, the agent-stranding wildcard the
  read plan predicted). **`|` can never begin a statement** — a named
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
- **Comments are `//` to end of line** (string-aware), starting only at
  line start or after whitespace (2026-07-20, the nuget receipt: a
  bareword `https://...` in a command line must survive; F# divergence
  row comment-boundary — `1// c` is a comment in F#, a parse error in
  weir). **Trailing comments hold everywhere [D:trailing-comments]** —
  `let x = 5 // note` — stripped per PHYSICAL line in the assembler
  (scripts, the REPL, and `-e` share the one scanner). The
  three-region table:

  | region | `//` means |
  |---|---|
  | inside any string form (incl. interpolation, holes and all) | data |
  | command argv, GLUED (`http://a`, `--format=a//b`) | data |
  | anywhere else, whitespace-preceded | comment to end of line |

  A command line's own trailing comment is therefore legal
  (`git clone $url // the mirror` — yaml's whitespace-preceded `#`
  rule, one convention twice); the cost is that a bare unquoted `//`
  argv word reads as a comment — quote it (`"//"`) to pass it. Holes
  do not host comments (a to-EOL construct cannot nest in a delimited
  one — `$"{1 // 2}"` is a located parse error). A trailing `///` is
  an ordinary comment; docs stay attachment-only. Yaml district
  content is BYTES and never scanned — and a comment on the district
  HEAD line still opens the district. fmt preserves trailing comments
  and their alignment verbatim. **`Env.get : string -> Option<string>`** reads the process
  environment (same receipt — the long-predicted gap's arrival);
  there is deliberately no `$NAME` expansion in command text
  (splices and interpolation are the typed spellings).
- **`Env.load T`** (2026-07-20) — the third typed-boundary instance
  (porcelain, from json, env): declare a monomorphic record whose
  field names are env-var names VERBATIM (no case mapping —
  conventions guess at deployments; verbatim is inspectable), fields
  scalars or Option-of-scalar (bool is EXACTLY true/false — 1/yes
  rejected; Option: absent = None, garbage = still an error). Reads
  are a SNAPSHOT at force; the record is plain data after (no
  pwd-style liveness). All problems collect into ONE boundary error —
  the existing runtime failure class, no new member. `Env.vars` is
  the untyped floor (builtin-owned EnvVar rows). `Env.set` DISSOLVED
  (2026-07-20): the bicep receipt's shape was child-env injection,
  not session mutation — see the child-env entry below; no ambient
  member ships. Line one `#!` is skipped by the runner; `#` at line head is
  reserved for directives.
- **`Args.load T`** [D:typed-argv] — the sixth typed-boundary
  instance (porcelain, from json, env, dotenv; http parked — the park's AOT leg resolved 2026-08-01 [HttpClient verified on the published binary, HTTPS included] and its trigger named: a typed REQUEST body cannot reach the wire through curl without silently-mangling flag arcana; draft at plans/PLAN-http-builtin.md): the
  script's own front door was the last unchecked boundary in a
  fail-before-effects language. Two declared shapes: a monomorphic
  RECORD of flags, or a UNION of record-payload cases as
  subcommands (first token vs constructor names lowercased —
  collision-free by the casing law; single-record payloads only;
  bare cases are bare words; unknown/missing first token errors
  with did-you-mean/the case list). Field typing is the Env.load
  scalar rule's argv face: `bool` = presence (valued booleans
  rejected — presence IS the semantics; `Option<bool>` rejected at
  check with the presence explanation), `string`/`int` = required
  valued, `Option<string|int>` = optional valued; other shapes
  reject in the Env.load message family. Field names derive
  kebab-case flags (lower→upper splits + acronym-run tails:
  `useHTTPSNow` → `--use-https-now`); hump-style variance collapses
  (`dryRun`/`DryRun` collide — check error). Shorts: first-letter
  derivation IFF unambiguous — contested letters derive for NOBODY
  and error with candidates at invocation; `[<Short>]` beats
  derivation (the derived short retires; --help is the truth);
  `[<NoShort>]` suppresses; `h` never derives and `-h` is help.
  STRICT: no positionals (`[<Positional>]` is not a registered
  attribute — dropped; operands spell as flags), unconsumed
  tokens/unknown flags/repeats/missing
  requireds/garbage values collect into ONE boundary error.
  `--help`/`-h` short-circuits BEFORE validation to derived usage
  (short truth + each field's `///` doc first line [D:doc-help]),
  stdout, exit 0. Script-only (`args`'
  scope); the untyped floor (`args`, `Args.flag`, `Args.value`)
  remains, exactly Env.get under Env.load.
- **Modules and imports** [D:modules-v1] (arc session 1; design in
  plans/DESIGN-modules-and-imports.md) — the unit of sharing is a
  FILE marked `module` (or `module Name`), top-level, no `=`/body;
  `module`/`import` are reserved words. A module is
  DECLARATION-ONLY (`type`/`let`; a `let` whose RHS runs a command is
  rejected — the weak-purity rule, walked to lambda boundaries so a
  param-ful `let f r = git …` is a deferred function), so it is
  checkable in isolation and `weir check lib.weir` enforces exactly
  what an import would. `import "path" [as Name]` takes a LITERAL
  string path, resolved at CHECK time by one function to an absolute,
  normalized path (symlinks unresolved, the `Path.glob` precedent);
  a missing file names the resolved path. Nothing loads at runtime —
  check-before-effects survives the file boundary. Access is ALWAYS
  qualified (`X.member`, `X.Ctx`): the module's values (and union
  ctors) join `Modules[alias]`, its types join the flat type pool by
  plain name (so signatures, field access, and bare `{ … }` literals
  resolve unchanged), and a new `ModuleTypes` map records provenance
  for the qualified literal `X.Ctx { … }` (which, with the unqualified
  `Ctx { … }`, always resolves regardless of field-set ambiguity — a
  checker interception of `EApp(typeName, ERecord)`, so a constructor
  applied to a record like `Some { … }` is untouched). Named errors:
  running a module ("a module declares; it does not run"), importing
  a non-module, a self-import, and a cross-module type-name clash (a
  session-1 limit). Import is script-only (not `-e`/REPL). A
  module-CONTENT error reports at the module's OWN file:line with an
  "imported here" `note` at the import line (decision 13, session 2);
  `check --json` carries a `file` per diagnostic. THE GRAPH (session
  3): imports are TRANSITIVE (a module may import); a shared module is
  checked ONCE (a cache keyed by normalized absolute path, so diamonds
  collapse); an import CYCLE is a check error naming the loop by file
  name (decision 9). A module exports only its OWN types (no
  re-export, decision 3) — it uses an imported type internally freely,
  but an importer must import that module to name the type. THE LSP
  (session 4): diagnostics publish PER URI (a module's error lands on
  its own file, even unopened); an open dependency's unsaved buffer
  wins over disk; editing a dependency re-checks its importers
  (decision 14). Deferred in-arc: multi-error-per-module recovery, the
  decl-above-import ordering error (decision 5).
- **Child-env injection** (2026-07-20, the shEnv receipt — the bicep
  translation's strongest): via the env sigils `$e(...)`/`!e(...)`
  (bind the `seq<EnvVar>`, glue it to the sigil; the `cmdEnv`/`runEnv`
  builtins RETIRED [D:drop-command-builtins]). OVERLAY
  semantics: injected vars sit on top of the inherited environment —
  set/override those names, inherit the rest, parent process
  untouched (pinned). Removing a var has no spelling (no receipt;
  empty-string value is the documented workaround, pinned). One spawn
  path by construction: `Proc.lines` IS `linesWith []`.
  `Env.fromFile : string -> seq<EnvVar>` parses the DOTENV SUBSET
  only — KEY=VALUE, optional single/double quotes (single is
  shell-literal, `$` allowed; double and bare reject `$`/backtick),
  `#` full-line and trailing comments, blank lines, empty values. No
  `export`, no expansion, no substitution: sourcing is shell
  EVALUATION, Env.fromFile is a parser, and every rejected line says
  so by naming the escape (`sh -c "set -a; . file; ..."`). It feeds
  the env sigil, NOT Env.load (process-env snapshot, unchanged);
  file-to-typed-record is parked (Env.load over an arbitrary source —
  real design weight, no receipt). The SUGAR STORY is layered: Layer
  0 was partial application (`let az = runEnv ...`) — RETIRED with the
  builtin [D:drop-command-builtins]; the env sigil (Layer 1) is now
  the sole spelling. Layers 1 and 2 SHIPPED together (2026-07-20,
  user-opened rather than receipt-triggered — the trigger discipline
  was overridden by choice, on record): Layer 1 is the sigil env slot
  `$e(...)`/`!e(...)` — an identifier GLUED to glyph and paren (a
  space falls back to the old parses); the env applies to EVERY spawn
  in the interior chain, `| complete` included (routed through the
  env-twin desugar target, the same family). Layer 2 is the district
  header — line-end `!name` distributes `!name(...)` over the block's
  lines; implemented entirely in the assembler (a MarkerKind
  classifier variant + parameterized district joins) and reparsed by
  Layer 1's grammar. The shared line-end seam is DECIDED: `!name` at
  line end IS a district header — a final command argument spelled
  `!word` must be quoted (classifier-pinned). Bare `!(...)`,
  bare districts, and bareword command lines stay env-less. Layer 3
  (ambient/scoped env, Session-carried) remains REJECTED,
  tombstone-style: the premise — injection, not session mutation —
  came from the receipt's own shape analysis; if deep-threading
  friction arrives, the answer is still Layer 1 (bind the env value,
  glue it to the sigil). Re-askable only against this entry.
- **Multi-value CLI options — parked, idiom documented** (2026-07-20
  disposition of the bicep `App of stack * env` finding): one
  occurrence, and the two-flag reshape (`--stack X --env Y`) is clean
  and cost nothing — `Args.pair`-style API stays unbuilt. GUIDE
  carries the idiom ("one flag per value"). Reopen criterion:
  receipts where the reshape is NOT available — mimicking an external
  tool's fixed CLI contract. Ranked below everything with receipts.
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
- **`for`/`do` — the general effect loop** [D:for-do]: F#'s own forms,
  desugared AT PARSE (the reifier precedent — the typed tree never sees
  `for`, so checking, hover, and eval ride existing machinery).
  `for <binderPat> in <expr> do <body>` ⇢ `xs |> Seq.iter (fun p ->
  body)`, a `unit` expression, eager; the body must be `unit` (the
  statement rule's discipline, via `Seq.iter`'s type). A BARE COMMAND
  body is implicit `!(…)` — `for f in files do git add $f` streams and
  raises per iteration, the natural shell shape (a `for`-specific rule:
  `do` is a statement position; `then` bodies stay expression-only).
  `do` at line end opens an indented body block (the `then` layout rule,
  word-boundary guarded — `sudo` at EOL does not dangle); `do !` arms a
  command district. `[for p in xs -> e]` ⇢ `xs |> Seq.map (fun p -> e)
  |> Seq.force` — the comprehension BYPASSES the list literal entirely
  (no interaction with empty-list inference; `Seq.force` keeps the
  literal's eagerness contract). No guard syntax — filtering is
  `Seq.where` upstream or `if` in the body (a `when` clause would be a
  third filtering spelling). THE LAW, stated once: **pipelines
  transform; `for` effects** — `map`/`where`/`fold` chains produce
  values, `for … do` does things N times; the desugar makes "same
  machine" true by construction.
  The rule is script-only: the REPL and `-e` keep `it`-style auto-print
  (ephemeral lines are not the PS output-pollution bug class; durable
  scripts are).
- **Script inputs — the `Self` module** [D:self-module]: a script's own
  facts group under `Self`, script-only (absent in the REPL and `-e`,
  which refuse the members by name). `Self.args : seq<string>` (argv
  after the script name), `Self.stdin : seq<string>` (lazy, one-shot —
  `Seq.force` it if reused), `Self.pid : int` (the process id), and
  `Self.scriptPath : string` (the absolute path, resolved at startup
  before any `cd`). With modules [D:modules-v1] `Self.scriptPath` is the
  FILE'S OWN path — a module sees its own — while `Self.entryPath :
  string` is the INVOKED script's, a process fact like `args`/`stdin`
  (the same in every module); in the entry the two coincide. Children
  inherit the process stdin unless a value is
  piped into them; `Self.stdin` reads the same underlying stream, so
  consuming it both ways is user error, as in any shell. Bare `args`/
  `stdin`/`scriptPath` were RETIRED into `Self` (freeing the names for
  users); a bare use is an unbound-var error whose did-you-mean names the
  `Self` member.
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
  capture with `$(sh -c "...")`. Consequences of a shell string remain
  the user's — backgrounded (`&`) children are orphaned to init when sh
  exits and no tree-kill can reach them (Session-1 lifecycle tripwires
  keep that analysis, now via the `$()` spelling).
- **Command lines** are direct exec: weir owns
  (prog, args). No shell, zero expansion — every argument is one argv entry,
  so there is no injection class (`echo "; rm -rf x"` prints the
  string). Programs containing `/` resolve against `Session.Cwd`; bare names
  resolve against PATH.
- **Splice-defaulting soundness condition** — RESTATED under
  [D:paramful-rhs] (2026-07-23), its THIRD edition; the old premise
  ("command segments never sit under a lambda, so no param variable
  is in scope to default") RETIRES, because commands now do:
  `let f r = echo $r` is legal and `$r` splices a lambda parameter.
  The argument no longer needs the premise: defaulting is a
  finalization step at the statement boundary [D:splice-default-last]
  — it runs pre-generalization in the same ctx, so a defaulted param
  is monomorphic BEFORE the binding generalizes (`let f r = echo $r`
  types `string -> seq<string>` deterministically), and inference-
  resolved params are never defaulted at all. This is the first
  feature enabled by a bug fix: the defaulting-order wrong-rejection,
  fixed properly two days earlier, WAS the load-bearing wall.
- **Data parallelism, not concurrency machinery**: `Seq.pmap` /
  `Seq.piter` (2026-07-20) fan a function out over a seq —
  ProcessorCount degree, EAGER, results in input order, first worker
  error rethrown. Parallelism is an implementation detail of a
  combinator (Array.Parallel precedent; xargs -P with types): no new
  types, no coloring, blocking semantics — which is exactly why it
  does not contradict the async rejection below. **Workers fork the
  session** (2026-07-20, same day the guard shipped — user question
  upgraded it to semantics; session-as-value arriving incrementally):
  each worker inherits the parent cwd at fan-out, `cd` inside a worker
  is worker-local, and the fork dies at the join — the root session is
  untouched. Named caveat (read-at-force-time is unchanged): a lazy
  stream built in a worker but forced after the join resolves against
  the JOINER's session — force inside the worker (`Seq.head`,
  `Seq.force`) when the worker's cd matters. Interleaved worker output
  is line-atomic and owned by the user, as with any parallel tool.
- **Async/task machinery is REJECTED, permanently** (2026-07-20, user
  decision): a scripting shell's concurrency model is processes and
  pipelines — spawn, stream, complete. Weir will not grow async/await,
  tasks, or their scheduling machinery; the moment a script genuinely
  needs them is the graduation signal to full F#, and weir says so
  rather than growing toward it. (Border row: no-async-concurrency.)
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
- **`complete`** (the command-mode pipe suffix; its former
  expression-mode builtin twin `completed` is retired
  [D:drop-reify-builtins]): run an
  external command to completion and reify the outcome as
  `Completed = { exitCode: int; stdout: seq<string>; stderr: seq<string> }` —
  **never raising on nonzero exit; the exit code is data**. This is the
  chosen exit-code policy (closes backlog: grep's no-match exit 1 is now
  `grep pat file | complete |> _.exitCode`); a per-command allowlist was
  rejected (grep's 1 is no-match but its 2 is a real error). `| complete`
  must directly follow a single external command segment (parse error
  otherwise) — it consumes the process, not the lines; the design is the
  command-suffix fallback from the plan, chosen over a type-level
  process-backed-stream distinction, which would not survive ordinary
  combinators (`where`/`first` return plain seqs). Splices in a completed
  command must be strings (the arg vector is a `seq<string>` literal).
- **`complete` and `Seq.force` force their source to completion** — on a
  non-terminating source they do not return (`yes hi | complete` hangs by
  design; the user owns it, exactly as with `yes hi |> Seq.force`).
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
  why `git status |> first 1 | complete` reports the marker rule instead of a
  generic expression error).
- **External-to-external pipes feed stdin**: `git log | grep x` wires the
  left stream (which must be `seq<string>`) into the right command's stdin.
  Piping into the shell is just `xs | sh -c "..."` now; `into` remains
  the expression-position spelling.
- **Neighboring asymmetry, named so it does not read as caprice**
  (2026-07-20): `Seq.skip` RAISES past the end (at enumeration) while
  `Seq.first`/`take` TRUNCATE — both F#-inherited behaviors.
- **Partiality convention (FINAL)**: a raising name plus a `try`-prefixed
  sibling returning `Option<'a>`. Pairs: `head`/`tryHead`, `toInt`/`tryToInt`;
  Option-native: `tryFind`, `tryIndexOf`; raising-only (documented bounds):
  `substring start len subject`. The idiom's other half:
  `Option.defaultValue` (eager; `Option.defaultWith` takes a thunk) and
  `Option.map`, so an Option in a pipeline does not force a match —
  `ls |> Seq.tryFind _.readOnly |> Option.map _.name |> Option.defaultValue "none"`. The
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
  plans/PLAN-modules-and-scripts.md (trial resolution deferred, design on file).
- **The yaml boundary** [D:yaml-v1] — `from yaml T` / `to yaml`, the
  typed-boundary family's TREE member (json is a row stream; yaml is a
  document format, so its law is recursive): fields are scalars,
  nested monomorphic records, `seq<elem>`, `seq<string * elem>` (an
  open mapping — labels), and `Option` anywhere (missing/null reads
  `None`; a `None` FIELD omits its key on write, a `None` ELEMENT
  renders `null` — the json-option split). `from yaml` takes
  `seq<string>` lines and yields `seq<T>`, one element per `---`
  document; a null where a collection sits is the EMPTY collection.
  Reading is STRICT: bool is exactly `true`/`false` (the Env.load law
  — the Norway problem cannot fire), a quoted scalar is a STRING even
  where int/bool is expected, extra keys are ignored (the from-json
  precedent), duplicates error. THE OWNED SUBSET (no YamlDotNet — the
  config-format spike's receipt: +677 KB for a subset weir hand-rolls
  on ~300 lines with its own error POSITIONS): scalars, block maps,
  block sequences, `#` comments, `---` multi-doc, and literal block
  scalars `|`/`|-` [D:block-scalars]; anchors/aliases, tags, flow
  style, directives, complex keys, folded scalars (`>`), `|+`, and
  explicit indentation indicators are teaching errors naming the
  subset. BLOCK SCALARS have no chomping policy — chomping is YAML's
  spelling for a distinction weir strings already have: `|` MEANS the
  string ends with exactly one newline, `|-` means it ends with none,
  read and write agree by construction (the form follows the value,
  both directions). Content is bytes: blank lines are newlines, ` #`
  is not a comment, more-indented lines keep their extra indentation. `to yaml` accepts a single
  yamlable value (ONE document) or a seq (`---`-separated documents;
  a top-level pair-seq is ONE mapping document); record fields render
  alphabetically (the VRecord law, as json), and the prelude `Yaml`
  union (`YStr/YInt/YBool/YNull/YSeq/YMap`) renders directly — `YMap`
  preserves ITS entry order, the user-controlled escape. THE QUOTING
  LAW (reverse-Norway): a string renders plain only when no YAML
  reader could mis-TYPE it — `"no"`, `"007"`, `"1e5"`, `"true"`,
  mid-line ` #`, and `: ` all double-quote. A MULTILINE string is a
  block scalar, not a quoted one [D:block-scalars]: one trailing
  newline renders `|`, none renders `|-`, and two or more fall back
  to the quoted-with-escapes spelling (that is `|+`'s territory,
  rejected — the fallback is valid, exact, and round-trips, so every
  legal string stays renderable);
  the quoting law governs single-line strings only.
  Value-domain answers, probed then pinned: Show renders the recursive
  union; Eq REFUSES it via the existing no-seq rule's own teaching.
- **The `yaml` district** [D:yaml-district] — a CHECKED block literal:
  a line ending in the word `yaml` (except `to yaml`/`from yaml`) opens
  an indented block of the strict-YAML subset, parsed AT CHECK TIME
  into a typed `Yaml` node expression. Structure errors (bad indent,
  mixed map/sequence, duplicate literal keys, anchors/flow/tags) are
  CHECK errors with positions. SPLICES: `$name` / `$(expr)` in value
  position take string/int/bool, a `Yaml` node (subtrees compose
  through functions), `Option` of one (a `None` splice OMITS its
  entry/item — the json-option omit in template form), or a seq of
  those (spliced as sequence items); a `$name` KEY splice is
  string-typed; an unresolved template parameter defers to the
  value-driven lift at eval (the sortBy posture — no type-class
  constraint exists, concrete violations still check-error). The `for`
  directive is the general form's district SPECIALIZATION (no `do`, the
  body is the indented sub-template): under a SEQUENCE it yields items
  per element, under a MAPPING it yields entries — dynamic keys
  (`for (k, v) in pairs` → `$k: $v`), the open-map answer. Runtime
  duplicate keys (for-generated) are eval errors — invalid YAML never
  renders silently. THE INJECTION LINE: you cannot write a YAML
  injection in weir, for the same reason you cannot write an argv
  injection — spliced values are NODES, never text; quoting and
  indentation belong to the renderer. Mechanism: the assembler carries
  block lines VERBATIM with relative indentation behind the sentinel
  (a command head GLUED to the sentinel is impossible from user text —
  the machine-boundary guard), and fragment parses run PADDED so every
  splice span lands at its true column. Mid-text `$` in a scalar is
  LITERAL (compute with `$(...)`). THE `#` TABLE [D:district-hash],
  stated once — `#` in a district means: full line at structure level
  = comment; mid-line after whitespace at structure level = comment
  (YAML's own rule — `from yaml` and the district AGREE, so a pasted
  `image: nginx:latest # pinned` yields `nginx:latest` on both
  paths); not preceded by whitespace (`a#b`) = part of the scalar;
  inside a quoted scalar (`"a # b"`) = data; inside a splice hole
  (`$(Str.join "#" xs)`) = expression text; anywhere in block-scalar
  content = bytes. The scan is ONE machine with two faces (the yaml
  comment scanner, hole-aware on the district face — never a second
  quote machine). BLOCK SCALARS IN DISTRICTS
  [D:block-scalars]: a `key: |`/`|-` header opens literal content —
  NO splices, no `for`, no interpretation of any kind inside it. The
  rule is one sentence: a block scalar's content is bytes; templated
  content comes from splicing a whole value (interpolate upstream,
  then `setup.sh: $script`). The reason is the footgun: embedded
  scripts are full of `$VAR`, and silently substituting weir values
  into them would be the `sh -c` fixture bite writ large. The content
  lines are consumed as TEXT before the splice and `for` scanners
  run — order is load-bearing, pinned. Blank lines inside an active
  district ride the sentinel as empty verbatim lines (bytes in block
  content, structure-transparent elsewhere), and full-line `#` lines
  inside districts are yaml comments at structure level, bytes inside
  a block (the directive scan is COLUMN-0 only — `#!/bin/sh` in a
  block scalar is content, not a misplaced directive).
- **External contracts** [D:contracts-spine] — a vendored, pinned
  artifact that constrains what the CHECKER accepts and contributes
  NOTHING at run time. Four properties, each load-bearing: VENDORED
  (checked in under `.weir/`, discovered by walking up from the
  declaring file to the FIRST `.weir/`, bounded by `.git`; never
  fetched during check), PINNED (exact identity + sha256 in
  `.weir/lock.json` — no ranges, ever: pairwise comparisons, not a
  dependency graph), CHECK-TIME ONLY (deleting every contract leaves
  every script running identically — pinned byte-identical), DECLARED
  not discovered (a `.weir/` directory's mere existence never changes
  how a file checks). `weir add <kind> …` acquires ONE
  contract, kind-aware (a schema fetches from a url; a signature will
  GENERATE from the installed tool; a module will clone at a ref);
  `weir restore` re-materializes everything the lock records and
  `weir verify` reports ABSENT vs MODIFIED per artifact (exit 1 on
  either) — both KIND-AGNOSTIC by construction (every entry is
  source + hash + path). THE LOCKFILE IS THE MANIFEST (deliberate:
  no ranges means nothing for a separate manifest to hold). `weir
  check` never fetches, never spawns, never touches the network — a
  locked-but-missing schema teaches `weir restore`, an undeclared one
  teaches `weir add schema`. For the F#
  audience: contracts are type providers minus the execution and plus
  the pinning. The first customer:
- **YAML schemas** [D:yaml-schemas] — `yaml schema=<name>` on the
  district marker line attaches `.weir/schemas/<name>.json` to THAT
  district (per district, author-named, never inferred — `kind` is a
  Kubernetes convention, not weir's). THE SUBSET, corpus-measured
  against six real k8s standalone-strict schemas: `type` (string or
  array-of-strings), `properties`, `required`, `items`,
  `additionalProperties` (bool or schema), `enum`, and `oneOf` over
  scalar type alternatives only (the IntOrString idiom — its every
  corpus occurrence); `description`/`format`/`title`/`$schema`/`x-*`
  are accepted annotations; everything else — `$ref` (add the
  STANDALONE variant), composition, `patternProperties`, `const`, the
  constraint family — rejects with a teaching error naming the
  keyword and its JSON path. WHAT IS CHECKED, honestly: STRUCTURAL
  validation always (unknown fields with did-you-mean, missing
  required fields, misplaced nesting — at the KEY's own span); VALUE
  validation where types permit (a `$n : int` splice checks against
  `integer`; enum/pattern constraints on splices do not check);
  `for`-generated entries and key splices relax the unknown/required
  checks for the map they touch. THE STRICT-VARIANT
  DEPENDENCY IS LOAD-BEARING: unknown-field checking fires only where
  the schema says `additionalProperties: false` — plain `-standalone`
  k8s variants omit it, making the check silently inert, so `weir add
  schema` WARNS when a fetched schema contains none (the vacuous-pin
  class) and the docs name `-standalone-strict`. Messages carry the
  FIELD and its PATH always (root renders without a suffix; enums cap
  at 6 shown values, a one-element enum states its value plainly);
  the `/null` in `integer/null` is kept — k8s marks most fields
  nullable, and hiding it would make a legitimate `replicas: null`
  unexplainable. A district with no declaration is
  unvalidated, exactly as before — schemas are additive.
- **`sortBy : ('a -> 'b) -> seq<'a> -> seq<'a>`** — the key must evaluate to
  an int, string, or bool; anything else is a runtime error (the type system
  has no comparability constraint — same check-at-the-boundary posture as
  `from json` field types, which are a scalar or `Option` of one
  [D:json-option]: a missing key or explicit `null` reads as `None`, a
  `null` in a required field errors naming the fix, and `to json` omits a
  `None` field's key so the roundtrip holds). **`groupBy` is deferred to the generics session
  with a reason**: its honest shape `{ Key: 'b; Items: seq<'a> }` requires
  generic records, which do not exist yet; a string-keyed fake would be case
  law in the wrong direction. `isEmpty : seq<'a> -> bool` completes the set.
  (`groupBy` has since landed on generic records:
  `groupBy : ('a -> 'b) -> seq<'a> -> seq<Group<'b, 'a>>` with builtin-owned
  `Group<'k, 'v> = { Key: 'k; Items: seq<'v> }`; keys share `sortBy`'s
  scalar-only runtime rule.)
- **Friction landings from tools/loc.weir** (2026-07-22):
  `Seq.sortByDescending` (sortBy's twin — same Ord constraint, stable,
  reversed comparison); `fst`/`snd` (pair-only projections — wider
  tuples are unification errors, F#'s rule); the `Path` module
  (`extension`/`fileName`/`stem`/`dir`/`combine` over System.IO —
  `combine`, not `join`, because bare `join` is Str.join's alias and
  the alias-home map is last-wins).
- **`Seq.fold`** [D:seq-fold] (2026-07-22, the git-subrepo receipt —
  the strongest on file): `('a -> 'b -> 'a) -> 'a -> seq<'b> -> 'a`,
  F#'s argument order FCS-probed before code (state-first folder;
  data-last source pipes). STRICT — the running-total operator
  consumes its source; an infinite source does not return (the
  collect/complete family). Constraint-free by construction. The
  landing surfaced and fixed a check-mode ordering loss (a NESTED
  lambda against a function cod now pushes through instead of the
  hasVars infer fallback — the piped element type reaches the inner
  param). `fun a b ->` sugar [D:fun-sugar] rides: one rule, two
  positions with let-param sugar (same pattern set, same
  curryParams), and the probe caught let-sugar ACCEPTING duplicate
  params where F# rejects — both positions now reject.
- Deferred with intent: `substring`/`indexOf` (they want Option — Session 3
  customers), padding, regex (its own design — match vs captures vs typed
  groups; a backlog entry, not a builtins-session improvisation).
- **`Seq.force : seq<'a> -> seq<'a>`** (RENAMED from `collect`,
  2026-07-18 — agent-era decision: F#'s `Seq.collect` is flatMap, a
  direct prior-bleed collision for F#-trained authors; `force` is the
  F# name whose muscle-memory semantics match, and weir has no list
  type so the seq return is the only reading. The rename frees
  `Seq.collect` for flatMap if the bleed catalog demands it.)
  It materializes eagerly at application:
  effects run exactly once, re-enumeration replays values with no re-spawn.
  Live queries (`pwd`, `ls`, command streams) bind the *query*, not the
  answer; `Seq.force` is the snapshot operator.
- **`File.read`/`File.write`/`File.append`/`File.exists`** (qualified-only,
  data-last, eager): the library-owned alternative to shell-redirect
  idioms. `write`/`append` return `unit` (their path-return was an
  explicit no-unit stopgap, retired the day unit landed). All relative
  paths resolve through the
  single shared helper `Session.resolve` — the same one used by spawns'
  working directories, `cd`, and PATH probes, so every filesystem touch
  agrees on what "relative" means.
- **The root session's cwd is the only PERSISTENT working directory**
  (worker forks above are ephemeral). Every spawn sets it as the
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
  `Seq.force` builtin (force once, materialize; né `collect`) as the
  standard escape hatch.
- **Overflow policy (Part 3, 2026-07-18)**: `int` is 64-bit end to end —
  literals parse as int64 (beyond-range literals are parse errors:
  "int literal out of range (64-bit)") — and arithmetic is CHECKED:
  `+`/`-`/`*`/`/` and `Seq.sum` raise "integer overflow" instead of
  wrapping (the bash-calculator bug class; joins the runtime failure
  inventory below). Ranges TERMINATE at the type boundary rather than
  raise ([...Int64.Max] yields its elements and stops — the yielded
  values are all correct, so termination is the honest semantics).
  Pinned by the permanent data-range battery (DataRange.fs + e2e:
  >2GB sparse file, 0-byte file, Int64 boundaries, megabyte strings,
  laziness under a billion-element range).
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

## Floats — finite-only [D:floats]

**The law: a weir float is always finite.** `checkedFloat` raises on
any non-finite result — overflow to infinity, `x / 0.0`, `0.0 / 0.0`
— exactly as `checkedInt` raises on integer overflow and `1 / 0`
raises today. NaN and Infinity are UNREPRESENTABLE: every boundary
parse rejects their text, `-0.0` normalizes to `0.0` at
construction. This is not an accommodation of IEEE, it is the
existing checked-arithmetic law applied to a new type — weir exposes
IEEE semantics nowhere else.

- **Literals**: `0.5`, `1.0`, `1e5`, `1.5e-3`. Digits are required
  on both sides of the point — `1.` and `.5` are located teaching
  errors naming the full spelling (`.5` collides with the accessor
  glyph; `1.` is a typo more often than an intent). `1..10` stays a
  range; a letter after an exponent backtracks (`2e` is an ident
  tail, never a trap). Out-of-range literals (`1e999`) reject at the
  literal. Prefix minus FOLDS into the literal (`-0.5`; the `0 - e`
  desugar would mix types — the same fold fixed `-30s`).
- **No implicit widening — the one deliberate footgun**: `3 / 2` is
  `1` (int division, unchanged), `3.0 / 2.0` is `1.5`, and
  `3 / 2.0` is a type error naming the spelling: wrap the int with
  `Float.ofInt`. This is the no-implicit-conversion posture applied
  to the most-used operator, and the located message is the
  mitigation.
- **Classes**: `Show` yes (holes and `print` follow — landing floats
  confirmed the interp-show zero-edit property before `Size` did);
  `Ord` yes (genuinely total BECAUSE finite-only — no `<`-vs-sort
  divergence); **`Eq` NO** — `0.1 + 0.2 == 0.3` is false by
  representation, and having excluded NaN to keep equality
  reflexive, admitting a member whose `==` still surprises would be
  inconsistent about what Eq promises. `==`, `Seq.distinct`, and
  `Seq.contains` on floats are teaching errors naming the idiom:
  `Float.near a b eps`, or compare after `Float.round`. Floats are
  the first Ord-but-not-Eq type.
- **Rendering**: shortest round-trippable, and an integral float
  keeps a visible decimal (`show 1.0` is `"1.0"`) so a float never
  renders identically to an int. `Float.parse` reads exactly what
  `show` writes (and rejects `NaN`/`Infinity` text); the round-trip
  holds both directions.
- **The module**: `Float.ofInt` / `toInt` (truncates toward zero,
  raises outside the 64-bit range) / `round` (halves away from
  zero) / `abs` / `near` / `parse` / `tryParse`. `Float` members
  never flatten to bare aliases (`toInt` stays `Str`'s).
- **`Duration.toSeconds`** is float-returning and lossless (`2500ms` is
  `2.5`) — the member the truncation kept unshipped. Duration's own
  parse/render path stays integer.
- **The boundaries admit floats [D:floats-boundaries]** (session 2):
  - **JSON**: a `float` field reads JSON's number type, and an
    integer-shaped number WIDENS (`{"rate": 3}` reads as `3.0`) —
    **this is a parse, not weir arithmetic**: the no-implicit-widening
    rule governs the language's operators, not what a boundary can
    read from a wire format with one number type. The reverse — a
    decimal into an `int` field — teaches "declare it float" (no
    silent truncation). `to json` emits `1.0` as `1.0` (the show
    shape; a float field is not an integer field), and CANNOT emit
    `NaN`/`Infinity` — the finite-only law means no such value
    exists to serialize. The schema validator needed no change:
    `integer ⊆ number` was already encoded.
  - **YAML**: a float value renders UNQUOTED (`rate: 1.5`) while a
    float-lookalike string renders QUOTED (`label: "1.5"`) — the
    reverse-Norway law was built for this adjacency and both
    directions round-trip. `.inf`/`.nan` are REJECTED on the way in
    (there is no value to read into); a quoted scalar at a float
    field is a string, the quotedness law unchanged. District float
    literals self-type (`cpu: 1.5` is a number, not a quoted
    string) and float splices lift via the new `YFloat` node.
  - **`Args.load`/`Env.load`**: float fields parse via the
    `Float.parse` shape, `[<Default 0.5>]` is a legal resting point
    (`--help` renders it), and the parse/render pair agrees —
    `--rate 0.5` round-trips through `show`.

## Parallel fan-out — sized for I/O-bound arms [D:tasks-underneath]

weir orchestrates processes, so a `pmap`/`piter` arm typically
SPAWNS and WAITS — I/O-bound, not CPU-bound. The workers are
dedicated threads up to a ceiling of **64 concurrent arms** (not
`ProcessorCount`): the ceiling exists for RESOURCE protection (an
unbounded fan-out over 10k items is a fork bomb with good manners),
not CPU sizing. `Seq.pmapWith n` / `Seq.piterWith n` set it
explicitly; degree < 1 raises naming the constraint. The contracts:
results preserve INPUT ORDER; every arm runs even when one fails,
and the first error BY INPUT ORDER rethrows after the join
(deterministic — data parallelism does not half-finish). Session
scopes are keyed to the LOGICAL context (`AsyncLocal`), so a scope
established inside an arm belongs to that arm wherever it runs —
the cwd/env fork discipline is unchanged and holds at the ceiling.

## Duration — time as a type [D:duration]

**The storage law: a `Duration` is an integer count of milliseconds.**
No float exists anywhere in the implementation — decimals appear only
at the TEXT edges (parse and render), where they are digit
manipulation, not arithmetic. This is what makes the no-floats decline
a position rather than a gap: the decimal cases that actually arrive
in shell work are time, and time has a unit.

- **Literals are single-unit**: `500ms`, `30s`, `2m`, `1h` — a digit
  run with a unit suffix, expression position only. In command
  position `30s` stays an argv WORD (`sleep 30s` passes the text to
  coreutils sleep — the literal never captures a command argument).
  Four spellings teach instead of parsing: decimal-with-unit (`2.5s`
  names the ms form), bare decimal (no float literals; decimals live
  in Duration text), `d` (a day is not a fixed length; write hours),
  and compound (`1m30s` is text — add single units or `Duration.parse`).
- **The algebra is closed**: `+`/`-` over two durations; `*`/`/`
  against an `int` (ms division truncates, the int rule); comparisons
  over two durations. `Duration / Duration` is REJECTED naming the
  spelling for a ratio (`Duration.toMillis d1 / Duration.toMillis d2`) —
  a unitless quotient is not a duration and weir does not guess.
  Negative durations are legal (`10s - 30s` is `-20s`); nothing
  converts implicitly (`30s + 5` is a type error, not seconds, not ms).
- **`Duration` is in Eq, Show, and Ord** — the first widening since
  the class set closed. `show` renders the Go shape: largest unit
  first, no zero components, one leading sign, decimals only below
  the seconds (`90500ms` shows `1m30.5s`; zero is `0s`; sub-second
  is `5ms`). `Duration.parse` reads exactly what `show` writes (plus
  compound and signed text); the round-trip holds in both directions.
  Sub-millisecond text (`1.0001s`) is a parse error naming the floor.
- **The module**: `Duration.ms/s/m/h` (int -> Duration),
  `Duration.toMillis` (int ms) and `Duration.toSeconds` (float seconds,
  lossless — [D:floats]), `Duration.parse` / `tryParse`
  (raise / Option — the X/tryX rule), and `Duration.sleep` —
  module-qualified ON PURPOSE: a bare `sleep` binding would shadow
  the coreutils command and flip the meaning of `sleep 5`.
- **Boundaries**: `Args.load` and `Env.load` parse duration text into
  `Duration` fields (`--timeout 90s`, `TICK=1h30m`), `[<Default 30s>]`
  is a legal resting point, and `--help` renders the default in the
  Show shape. JSON is PARKED: a `Duration` field at the json boundary
  is rejected naming the conversion (`Duration.toMillis` into an int
  field) — ms-int vs ISO-8601 both defensible, so the choice waits
  for a receipt. Interpolation holes render a `Duration` directly
  (`$"took {elapsed}"` — holes consult `Show` [D:interp-show]);
  command-argument splices do NOT — the argv form is the program's
  business, and the rejection names the deliberate spellings
  (`Duration.toMillis d` or `show d`).

## Type classes — Eq (Session A, 2026-07-20)

Closed, compiler-owned, structural, INFERRED — qualified types over
the existing Damas-Milner machinery, no user instances, no syntax:
constraints attach silently from use (`let same x y = x == y` gets
`Eq a =>` and works at any equatable type), ride generalization,
freshen per instantiation, and are FULLY ERASED after checking.
"Instances" are the promoted shape predicates (Eq = no function or
seq anywhere, recursively). Failures locate at the DEMANDING site;
concrete failures keep the pre-class message families verbatim. A
constraint left on a type nothing determines is an ambiguity error
(no defaulting; the reject-don't-guess posture one step later than
the old at-the-operator rule). Rows x classes: Eq rides a row var
and discharges when the row does. `Seq.contains` is now an ordinary
constrained scheme — sentinel customer three RETIRED. Session B
(2026-07-21): Show and Ord landed; `show : Show a => a -> string`
(sentinel retired; Show is WIDER than Eq — seqs render, functions do
not; bare-value show no longer defaults to string, it stays generic
with Show riding) and `Seq.sortBy : Ord b => ...` (Ord = int | string
| bool EXACTLY, no decomposition — a record of orderable fields is
still not orderable, tripwired; Duration widened the class set —
[D:duration]). THE SOLE RUNTIME TYPE CHECK DIED
with sortBy's static constraint: "zero runtime type checks" is fully
true for the first time (check-first e2e proves a bad key runs zero
effects). The splice family split [D:interp-show]: interpolation
holes consult Show — a hole renders exactly what `show` renders,
except that a bare top-level string stays RAW (the value, not its
quoted rendering) — while command-argument splices are NOT Show and
stay scalar-exact: an argv word is a bet on the child's parser that
weir refuses to make (`sleep 1h30m` works on GNU sleep and fails on
plenty else); a spliced Duration teaches the deliberate spellings.
The law lives ONCE (`spliceAdmit`) for both the eager check and the
deferred-var discharge, and the hole side asks the CLASS — a new
Show-admissible type needs no edit there.

## The F# border — rejected vs pending (2026-07-20)

The single enumeration of weir's border with F# is
`tests/fidelity/divergences.md` (machine-read by the oracle; this
section is the pointer, not a copy). Every row carries a **status**:
`different` (weir has a deliberate equivalent — the fidelity
divergences proper), `rejected` (absent BY DESIGN, rationale
required), or `pending` (absent and undecided — reopens on evidence,
usually the agent telemetry; agents treat pending as absent). The
discipline: moving a row between statuses is a decision and gets
archaeology; a rejected row without a rationale ref fails review; a
pending row that accumulates telemetry hits is the roadmap asking.
Precedents through the statuses already: no-let-param-sugar went
pending→fixed-in-weir (retired from the table); no-mutation went
implied→rejected when the sugar made `let mutable` parse strangely.

## Diagnostics — which error you see [D:diag-arbitration]

When a line has more than one thing wrong, one error reaches you. The
rule, in your terms: **weir reports the error at the furthest point the
parser reached in your file.** Two consequences worth knowing:

- **Furthest REACHED, not latest in the file.** With two problems in
  one statement the parse stops at the first, so that is what you see —
  a later mistake downstream can't hide an earlier one. Across separate
  statements each failing statement reports its own error, so you get
  one per broken statement.
- **The position is always physical — a real spot in your file.** The
  assembler wraps districts and compounds in synthetic text to parse
  them; an error never anchors there. This holds by construction, not
  by cleanup: the consumed-separator law (a consumed `;`/`|`/record
  head COMMITS to its element) removes the backtracks that used to
  rewind a deep failure into a shallow, position-inflated one. The
  furthest point reached is therefore the true cause, on the offending
  token.

Two mechanisms serve this, and they are deliberately distinct — the
"who wins" question has one answer, not a per-error ambition contest:

- **Teaching hints override the message at their own position, never
  the position.** `x = 1 | 2` reports "'|' chains commands; pipe with
  '|>'" exactly where the `|` sits — the hint replaces the generic
  "expecting…" list at the furthest point, it does not move the caret.
  A hint that once landed on an innocent line did so only because a
  backtrack carried it there; the commit law closed that, so the hint
  is correct at its position, not a workaround.
- **One weir-owned escape, reserved for checks that fire inside
  discardable speculation.** The depth guard throws past the parser's
  error channel because it fires deep inside speculative parsing that
  would otherwise BACKTRACK AND DISCARD it. Teaching hints never need
  this — they fire at committed points where an ordinary fatal already
  wins. So the channel is not generalized: overriding a message
  (hints) and escaping a doomed speculation (depth) are two different
  needs, and conflating them would risk throwing out of a branch that
  should retry.

One pipeline, one policy: `check`, `check --json`, and the LSP all read
this same choice from the one `analyzeLines` path.

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
(Done: backlog #1 and the exit-code policy — old #3 — landed as
`Seq.force` (né `collect`) and `complete`; see "Processes and the
session".)

(Done: comparison/boolean completeness — landed with `<>` inheriting `==`'s
equatability and short-circuit `&&`/`||`, as pre-committed.)
