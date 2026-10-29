# PLAN: the hand-written happy-path parser — a lexer and a recursive-descent parser for the statements that parse, FParsec for everything that does not

**Status: PROPOSED** (2026-10-29) — the successor to
[PLAN-parse-fast-path.md](PLAN-parse-fast-path.md), whose "Not here"
named this redesign as the thing it avoided. That plan's law is kept
unchanged and applied one level up: the unchanged FParsec grammar is
the only source of every diagnostic; the new parser returns only
successes the grammar would return, and anything else is not its
business.

## The receipt

After the 2026-10 allocation work (perf/survivors through
perf/atom-dispatch: starship import 246 → 169 ms pinned, allocation
~290 → 129 MB) a CPU profile of the AOT binary (perf on the host,
9,300 samples over 20 checks) splits the import's CPU as:

| phase | own CPU | its GC, write barrier, allocation | total |
|---|---|---|---|
| parse | ~33% | ~18% | ~50% |
| check / inference | ~17% | ~11% | ~28% |
| line assembly and scanning | ~7% | ~2% | ~9% |
| purity, merge, process startup | ~5% | | ~5% |

Inside the parse half, FParsec's combinator closures are 20% of all
CPU on their own, and no single weir function is above 1.3%: the cost
is the grammar's shape (a `choice`/`attempt`/`many` closure per
grammar node, a Reply and merged error list per step), not any one
rule. The next-character dispatch (P0 of the fast-path plan) was the
last cheap fix; applied to the openers it was exponential, and what
survived is worth ~2 ms.

The corpus census (the repo's 80 files plus the starship port, 2,253
logical statements, `Script.assemble` + `parseLineFull` under the
assume resolver): `let` 36% (80% of those bind a lambda — module code
is function definitions), `type` 17%, command lines 17%, signatures
14%, bare expressions 4%; 30% of statements contain a command
somewhere; of 41,920 expression nodes, seven kinds (`EVar`, `EApp`,
`EStr`, `EField`, `ELambda`, `EPipe`, `EBinOp`) are 85%; the deepest
expression nests 35 (the guard's ceiling is 500).

## The idea

A tokenizer and a recursive-descent parser, hand-written, that parse
the statements real scripts are made of — and refuse the rest.
`parseLineFull` tries it first; a refusal (any unknown shape, any
doubt, any depth past a conservative bound) costs nothing but the
tokens read, and FParsec parses the statement exactly as today. No
diagnostic, position, Expecting list or first-error choice changes:
the new parser never reports; it only answers "here is the tree the
grammar would have built" or "not mine".

The law, then, is the fast-path law with a stronger equality: for
every statement the fast parser accepts, its `Stmt` must be
STRUCTURALLY EQUAL to FParsec's (spans included, since fmt, the LSP
and every diagnostic anchor read them), its parse-time side effects
must be the same (the anonymous shapes it pushes to
`Types.pendingAnonDefs`, in the same order — the one effect of a parse
that the checker consumes; `grantedRhsRanges`/`chainTeach` are
parser-internal memos), and it must accept nothing FParsec rejects.
The equality is CHECKED, not argued: an oracle mode runs both parsers
on every statement and fails loudly on any difference, and CI runs the
whole battery in that mode (see Pins).

What this is not: a faster FParsec (a token stream cannot feed its
character-level combinators), and not a second source of errors.

## Anchors

- `Parser.fs` (6,283 lines, 310 parsers): `parseLineFull` (~6212) is
  the single entry — Script, Fmt, Repl, Prelude and Complete all parse
  through it or `parseLine`/`parseStmt`/`parseExpr` (Complete's two
  `parseExpr` calls are the only non-statement entry). `keywords` (line
  10) is the one keyword list; `isIdentStart`/`isIdentCont`/`isDigit`
  and `Scan.fs`'s string rules are the character classes the lexer
  must agree with.
- The operator table (`opp`, ~2242): `|>` `>>` `<<` `<|` at 1, `||` 2,
  `&&` 3, comparisons at 4 (`=` only when not `==`), `+`/`-` at 6
  (`-` only when not `->`), `*` `/` `%` at 7, all left-associative;
  `binOp`/`pipeOp` build the nodes.
- The desugars both parsers must produce bit-identically:
  `indexDesugar`/`fromEndIndexDesugar` (~1685), `curryParams`
  (~1988), `tildeExpr` (~4375), the pipe/compose desugar in `pipeOp`,
  `buildBuiltinApp` (inside `commandSegment`) and `foldChain` (~5050) on the command side, `armSeq`'s pure
  part, `Span.union` and the span rule of `spanned` (End is the
  position BEFORE trailing whitespace). These move to a shared
  module (`Desugar.fs`, before both parsers) so there is one
  implementation, not two that must agree.
- Context the grammar carries in thread-locals (`letCmdOk`,
  `letCmdStmt`, `exprParen`, `ifCondOk`, `ifBodyOk`, `inHole`,
  `matchArmDepth`): the hand parser carries the same facts as
  parameters; the list is the checklist of context-dependent rules.
- The depth guard [D:depth-guard]: `deepen` counts nesting per guarded
  nonterminal and raises `DepthExceeded` past 500 or when the stack
  probe fails. The hand parser does not reproduce the count; it bails
  at a conservative nesting bound (64 — the census max is 35) and on
  the same stack probe, so every input it accepts is one FParsec
  accepts, and every deep input reaches FParsec's guard unchanged.
- `tools/check-parity.weir`, the mutant corpora under /output/tmp/gcm
  (960 random, 6,480 scanning-specific, 510 atom-opener), and
  `tests/Weir.Fuzz/Grammar.fs`'s program generator — the acceptance
  instruments; `Fmt.formatFile` output is the second parity surface.
- The measurement discipline: AOT binary, alternating A/B
  (/output/tmp/gcm/ab.py), medians of 21, the three benchmarks
  (starship import pinned and unpinned, `sys.weir`, bare startup).

## Shape

- `Lex.fs` (new, after Scan.fs): tokens with `Pos` spans — word
  (identifier or keyword, decided against `Parser.keywords`), number
  (int, float, the duration and size suffixes exactly as `intLit`
  takes them), string (plain, verbatim, triple, interpolated: the
  holes are lexed recursively, `{{`/`}}` per the interp rules),
  operator, punctuation, and the sigil/opener classes the parser
  dispatches on. The lexer is mode-free: it tokenizes expression
  text; command-line text is tokenized by the P2 command lexer, which
  knows barewords and dashes. Anything the lexer cannot classify is
  a refusal of the whole statement.
- `Fast.fs` (new, after Parser's shared pieces): `tryStmt: Resolver ->
  string -> Stmt option`. Precedence climbing over the operator table;
  the suffix loop (field, index, slice, with the immediacy rule);
  application chains; lambdas, `let … in`, `if`/`elif`/`else`,
  `match`, lists, tuples, records and anonymous records, parens and
  unit; type syntax for `type` and signature statements. Every
  construct is an explicit function; every "I don't know this" is
  `None`, never a guess.
- `parseLineFull`: `match Fast.tryStmt r input with Some s -> Ok s |
  None -> (today's path)`. The post-parse depth probe (`nodeTooDeep`)
  runs on both paths unchanged.
- Oracle mode: `WEIR_PARSE_ORACLE=1` (read once at startup) makes
  `parseLineFull` run BOTH parsers on every statement and raise on any
  difference — tree, side effects, or a fast success where FParsec
  fails. Zero cost when off (one static bool).
- Coverage counter: in oracle mode, the share of statements the fast
  parser took, printed at exit — the number that says whether a phase
  earned its speed.

## Phases

- **P1 — expression statements.** The lexer; the fast parser for
  `let` with an expression RHS (params and lambdas included), bare
  expressions, and the type grammar (`type`, signatures); refuse
  commands, sigils, `within`, yaml/heredoc districts, attributes,
  patterns beyond names and tuples. The oracle mode and the coverage
  counter. BAR: oracle-clean over the unit suite, the fuzz suite, the
  e2e battery, the parity corpora and the mutants; fast-path coverage
  ≥ 50% of corpus statements; starship import ≥ 15 ms faster pinned.
  Otherwise the plan closes with the numbers, as the fast-path plan
  did.
- **P2 — command lines.** The command lexer (barewords, dashes, `~`,
  splices, `|` vs `|>`, the stop words `in`/`then`/`else`) and the
  resolver protocol of `litHead` reproduced call for call (alias,
  builtin, known, external — the same `Resolver` members in the same
  order, so a resolver's answers decide the same way). Sigils and
  interpolation holes follow, since both re-enter the command
  grammar. BAR: coverage ≥ 90%; combined gain ≥ 40 ms pinned.
- **P3 — the long tail, by census.** `within`, full patterns, `match`
  guards, the remaining literal forms — each only if the coverage
  counter names it; whatever stays rare stays on FParsec forever,
  which is allowed by construction.
- Ledger: DECISIONS row `hand-parser` (the equality law and the
  oracle-mode-in-CI rule); CHANGELOG under the release the phase
  lands in; this plan's status and session reports per phase.

## Pins

- Oracle mode is a permanent CI job, not a development aid: the unit
  suite, the fuzz suite and the e2e battery each run once more with
  `WEIR_PARSE_ORACLE=1`. Two parsers that must agree drift by
  default; the job is what makes drift a red run instead of a bug.
- An FsCheck property: every generated program (Grammar.fs), rendered,
  statement by statement, parses to equal trees — or the fast parser
  refuses. The mutant corpora pin the other direction: the fast parser
  accepts nothing FParsec rejects.
- `tools/check-parity.weir` byte-identical on every corpus, and fmt
  parity (`weir fmt` output) on the same files — fmt reads spans.
- A width/depth pin: nested and wide inputs (the depth-guard fixtures,
  the 20k-element tuple, 20k-deep parens) take the same path and the
  same time as today — the fast parser refuses past its bound, never
  overflows, never doubles.
- The keyword list, the character classes and the desugars are
  imported from one place; a grammar change that forgets the fast
  parser costs coverage, never correctness, and the oracle job says
  which statements regressed.

## Not here

- Any change to error text, position, which error is first, or the
  Expecting lists — FParsec stays the oracle in full.
- A token stream for FParsec, the LSP colorizer, or the REPL (they
  keep their own scanners).
- Reordering or editing the FParsec grammar beyond extracting the
  shared desugars.
- An on-disk cache of parsed or checked modules.
- Trading binary size for speed.
