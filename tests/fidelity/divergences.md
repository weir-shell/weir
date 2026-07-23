# Named divergences from F# (machine-read by Weir.Fidelity)

One row per divergence or absence. The oracle FAILS the build on any
fidelity pin whose F#/weir verdicts disagree without citing an id here.
Adding a row is cheap by intent — divergence staying cheap is the
argument that killed the subtractive fork.

**status** classifies the border (added 2026-07-20):
- `different` — weir has an equivalent, deliberately different; the
  rationale lives at the SEMANTICS ref.
- `rejected`  — absent BY DESIGN; a rejected row without a rationale
  ref is a bug in this file.
- `pending`   — absent, undecided; may land on evidence (reopen
  criteria live in the referenced plan/park). Agents: treat as absent.

| id | status | shape | weir | fsharp | ref |
|----|--------|-------|------|--------|-----|
| blank-line-ends-statement | different | blank line inside an indented block | reject | accept | SEMANTICS: multi-line rules |
| double-equals | different | equality is == ; = is binding-only | == accepts / = rejects | == rejects / = accepts | SEMANTICS: why == and not = |
| statement-rule | different | discarded non-unit statement | reject | accept (warning) | SEMANTICS: the statement rule |
| pipe-precedence-error | different | piping into an operator expression | reject (targeted) | accept | SEMANTICS: precedence error |
| interp-scalar-only | different | non-scalar interpolation holes | reject (show is the renderer) | accept | SEMANTICS: interpolation, show |
| no-printf-family | rejected | printfn / sprintf / %d | reject (interpolation is the mechanism) | accept | SEMANTICS: interpolation |
| no-mutation | rejected | mutable / <- assignment (reserved word) | reject | accept | SEMANTICS: evaluation |
| no-hof-inference | rejected | applying a bare parameter as a function | reject | accept | SEMANTICS: governing principle |
| no-operator-defaulting | rejected | + on two unresolved params | reject (int-or-string guess refused) | accept (defaults int) | SEMANTICS: var-var operators |
| no-oo | rejected | classes, interfaces, members, inheritance | reject | accept | a typed shell, not an object language |
| no-computation-expressions | rejected | builder blocks (seq { }, async { }, custom CEs) | reject | accept | pipelines are the composition story; comprehension sugar, if ever, is parser-only — not CE machinery |
| no-async-concurrency | rejected | async/task/await machinery | reject | accept | a scripting shell does not need it: processes and pipelines ARE the concurrency model (data-parallel combinators Seq.pmap/piter exist — parallelism as a library detail, never language machinery); wanting async is the graduation signal — go to full F# |
| no-imperative-loops | rejected | for / while | reject (pipelines + ranges are iteration) | accept | SKILL: iteration; comprehension-for is separately pending |
| no-tuple-ord | different | tuples are not orderable (F# compares lexicographically); sortBy keys stay scalar | reject | accept | SEMANTICS: tuples — same narrowness family as Ord itself; with record ordering if ever |
| tuple-exhaustiveness-bounded | different | only an all-irrefutable tuple arm completes a match (F# does per-component product analysis) | catch-all required | accepts e.g. (true,_)/(false,_) | SEMANTICS: tuples — bounded rule; widen on receipts |
| no-pattern-binders | different | REFUTABLE patterns in binding position (F# warns-accepts incomplete binders; weir hard-errors "this pattern can fail; use match"). Irrefutable binders SHIPPED 2026-07-21 — destructuring lets (bare-comma and parenthesized, nested), tuple lambda/sugar params — completing the arc the retired no-tuples row opened with "destructuring is the real scope" | reject refutable | accept + warning | SEMANTICS: binders — the warning-vs-error strictness family (statement rule, exhaustiveness) |
<!-- no-tuples RETIRED 2026-07-21: tuples landed (types, literals, patterns, arity 2+; multi-payload constructors un-restricted). The reversal archaeology is in NOTES. -->
<!-- single-payload-unions RETIRED 2026-07-21: multi-payload constructors landed with tuples (the corollary retired with its rule). -->
| lowercase-binds | different | binding names must start lowercase (F# accepts uppercase value bindings, style-discouraged) | reject | accept | SEMANTICS: the casing law — fourth member of the warning-vs-error strictness family and honestly its first STYLISTIC member: no silent-wrong-meaning bug is prevented at the binder itself; the payoff is disjoint name classes (pattern-position determinism, unshadowable modules by construction) |
| uppercase-pattern-is-ctor | different | an uppercase identifier in a PATTERN is always a constructor (unknown = hard error + hint); F# resolves by scope and falls back to a VARIABLE binding with warning FS0049 | reject unknown | accept + warn (binds a var) | SEMANTICS: branching — the FS0049 trap (typo'd case silently becomes an irrefutable catch-all) made unrepresentable; the strictness family again |
| exhaustiveness-hard-error | different | a non-exhaustive match is a HARD ERROR (F# warns and accepts) | reject | accept + warning | SEMANTICS: branching — user decision 2026-07-18; surfaced as an oracle divergence by the literal-pattern pins (2026-07-21); corpus: 5928e91 |
| unreachable-arm-hard-error | different | an arm after an unguarded catch-all is a HARD ERROR, located at the catch-all with a constructor hint for variable binders (F# warns FS0026 on the dead arm and accepts) | reject | accept + warning | SEMANTICS: branching — user decision 2026-07-21; coverage's dual, and the casing-law footgun (a typo'd constructor becomes a catch-all binder) caught at its source |
<!-- no-literal-patterns RETIRED 2026-07-21: literal patterns landed (int/string/(); literals never complete a match — F#'s rule, oracle Same). The guard idiom remains legal, no longer the only spelling. -->
<!-- no-record-update RETIRED 2026-07-22 (PLAN-record-update): copy-and-update landed on the re-mine's receipts — flat, multi-field, nested I.X sugar, general-expression sources (unparenthesized application incl., FCS-probed), row-typed updaters that GENERALIZE (result type is the source's own row variable). The corpus snippets run verbatim as e2e. -->
| attributes-registered | different | `[<Name arg>]` on record fields is a CLOSED registry (Short/NoShort/Doc/Positional) — unknown names are check errors with didYouMean; F# resolves attribute names as types (accepts any in-scope attribute, rejects unknowns) | accept registered, reject the rest | reject Short (no such type), accept System.Obsolete | SEMANTICS: attributes — check-time, fully erased; consumers registered; validation at attachment, binding at consumption |
| update-path-plain | different | update paths are FIELD paths only; F# name resolution captures a type named like the path head (`type I` + `{ o with I.X = v }` rejects in F#, accepts in weir) | accept | reject (type-name capture) | FCS-probed 2026-07-22; the probe's naming collision found it |
| ctor-pattern-scrutinee | different | constructor patterns need an already-resolved scrutinee type; F# infers a param's type FROM the pattern (`let f x = match x with | A -> ...`) | reject | accept | corpus: 5928e91; live: git-subrepo (a standalone result-dispatcher fn — match moved to call sites); the pattern face of the no-annotations/funParams inference bound |
| column-zero-statements | different | statements start at column 0; F# accepts uniformly-indented fragments | reject (continuation without a statement) | accept | corpus: 5928e91 (x5); the assembly law — blank-line-ends-statement's family |
| record-field-comma-trap | different | `{ Name = "x", Age = 21 }` — F# silently makes the FIELD a tuple (the classic trap); weir rejects the shape | reject | accept (trap semantics) | corpus: 5928e91; strictness family, weir-safe direction |
| no-arrays | pending | `[\| ... \|]` array literals | reject (seqs are the collection) | accept | corpus: 5928e91 |
| no-access-modifiers | pending | `let internal/private ...` (the corpus hit also carried fsi's `;;`, an artifact not a feature) | reject | accept | corpus: 5928e91 |
| no-auto-members | pending | compiler-generated union testers (`.IsA`/`.IsCaseB`) — the no-OO bound's corpus face | reject | accept | corpus: 5928e91 (x2) |
| no-let-rec | pending | let rec (reserved word) | reject | accept | recursion unserved; pipelines cover iteration |
<!-- no-unary-minus RETIRED 2026-07-21: prefix minus landed on the loc.weir friction receipt (descending sort spelled `0 - n`). F#'s adjacency rule exactly — and the oracle corrected the folklore mid-landing: `f -1` is APPLICATION of -1 in F# (pinned Same), `x-1`/`x - 1` stay subtraction, `1 -2` is int-applied-to-int (both reject). Negative literal patterns already worked. -->

| no-format-specifiers | pending | format specifiers in holes ($"{x:N2}") | reject | accept | decided out of interp v1; no demand logged |
| block-comments | pending | (* ... *) | reject | accept | // decided over #; (* *) never decided, no demand |
| no-floats | pending | float literals and arithmetic | reject | accept | SEMANTICS: "no floats yet"; corpus: 5928e91 |
| no-chars | pending | char literals | reject | accept | no demand |
| no-exceptions | pending | try/with/finally, raise | reject (fail exists; no catching) | accept | expected-findings cluster: error-handling-as-value |
| no-type-ascription | pending | (e : ty) annotations | reject | accept | checklist 2.3: must re-verify, never relabel, when it lands |
| no-user-modules | pending | module M = ... and imports | reject | accept | parked with trial-resolution design on file |
| no-anonymous-records | pending | {| A = 1 |} and undeclared record literals | reject (exact declared field set) | accept | one telemetry hit; corpus: 5928e91 (x2) |
| no-destructuring-binders | pending | let (a, b) = ... , fun (x, y) -> | reject | accept | tied to the tuples decision |
<!-- no-elif RETIRED 2026-07-22 (small-items sweep): elif landed as pure spelling over else-if (the precondition probe confirmed chains already worked single-line AND offside multi-line, so no hidden second gap). Demand receipts: corpus x2 + agent friction. -->
| semicolon-command-argv | different | `;` inside a command line (bash chains; weir passes literal argv + warns) | argv word + warning | n/a (bash prior, not F#) | SEMANTICS: sequencing; the no-injection pin |
| redirect-argv | different | `>` / `>>` inside a command line (bash redirects; weir passes literal argv + warns with the File.write/File.append spelling) | argv word + warning | n/a (bash prior, not F#) | SEMANTICS: command mode — the streams stance; the semicolon row's family |
| raw-single-line | different | `@"..."` and `"""..."""` raw strings are SINGLE-LINE (F# spans physical lines) — the assembler, fmt's refuse-on-mismatch argument, and the highlighter's swallow analysis all rest on strings closing before EOL | reject multi-line | accept | blank-line-ends-statement's family; PLAN-raw-strings |
| no-interpolated-raw | pending | `$@"..."` / `$"""..."""` interpolated-raw (F# accepts) — parked as one decision; regex-with-holes is a computed pattern, the expression side's territory | reject | accept | PLAN-raw-strings park; reopens on raw-with-splice receipts |
| regex-pattern | different | the bespoke `Regex "lit" binder` match pattern — literal-only, check-time compiled, arity-typed binders (F# has no built-in regex pattern; ParseRegex is userland active patterns, whose group/binder mismatch is a silent runtime non-match) | weir-only match form, accept | reject (unknown pattern) | SEMANTICS: patterns — the FIRST weir-only match form; user active patterns stay parked, stated so the door is closed, not ajar |
| no-heredoc | different | `<<` is backward composition in expressions and a literal argv word in commands (bash: heredoc); feeding stdin is a seq piped into a command (`xs \| into "cmd"`) | composition / argv word | n/a (bash prior, not F#) | SEMANTICS: pipes — stdin feeding |
| semicolon-greedy-bodies | different | single-line-TYPED `if c then a ; b` groups INSIDE the body (F# verbose groups outside); multi-line siblings group F#-faithfully since the offside close (2026-07-20) | body-scoped | trailing | SEMANTICS: sequencing — greedy survives only where the sigil-era continuation join needs it; the assembler paren-wraps compounds at same-level siblings |
| record-fields-ignore-indent | different | inside an open `{ }` weir is indentation-blind (col-0 fields legal); F# offside rejects | brace mode | none | SEMANTICS: records — record continuations are expression context, the assembler tracks braces not columns |
| bang-sigil | different | `!(cmd chain)` runs-and-streams (bash: extglob/history `!`) | effect sigil, unit | n/a (bash prior; invisible to the F# oracle) | SEMANTICS: sigils |
| capture-sigil-aligns | different | `$(cmd chain)` captures output — the bash prior HELPS here (recorded per the == archaeology precedent: priors that help get named too) | capture, typed seq<string> | n/a (bash prior) | SEMANTICS: sigils |
| comment-boundary | different | // mid-token is NOT a comment (https://... barewords); comment needs line start or preceding whitespace | url survives | 1// c is a comment | SEMANTICS: comments; nuget receipt |
