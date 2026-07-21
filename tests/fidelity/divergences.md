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
| exhaustiveness-hard-error | different | a non-exhaustive match is a HARD ERROR (F# warns and accepts) | reject | accept + warning | SEMANTICS: branching — user decision 2026-07-18; surfaced as an oracle divergence by the literal-pattern pins (2026-07-21) |
| unreachable-arm-hard-error | different | an arm after an unguarded catch-all is a HARD ERROR, located at the catch-all with a constructor hint for variable binders (F# warns FS0026 on the dead arm and accepts) | reject | accept + warning | SEMANTICS: branching — user decision 2026-07-21; coverage's dual, and the casing-law footgun (a typo'd constructor becomes a catch-all binder) caught at its source |
<!-- no-literal-patterns RETIRED 2026-07-21: literal patterns landed (int/string/(); literals never complete a match — F#'s rule, oracle Same). The guard idiom remains legal, no longer the only spelling. -->
| no-let-rec | pending | let rec (reserved word) | reject | accept | recursion unserved; pipelines cover iteration |
<!-- no-unary-minus RETIRED 2026-07-21: prefix minus landed on the loc.weir friction receipt (descending sort spelled `0 - n`). F#'s adjacency rule exactly — and the oracle corrected the folklore mid-landing: `f -1` is APPLICATION of -1 in F# (pinned Same), `x-1`/`x - 1` stay subtraction, `1 -2` is int-applied-to-int (both reject). Negative literal patterns already worked. -->

| no-format-specifiers | pending | format specifiers in holes ($"{x:N2}") | reject | accept | decided out of interp v1; no demand logged |
| block-comments | pending | (* ... *) | reject | accept | // decided over #; (* *) never decided, no demand |
| no-floats | pending | float literals and arithmetic | reject | accept | SEMANTICS: "no floats yet" |
| no-chars | pending | char literals | reject | accept | no demand |
| no-exceptions | pending | try/with/finally, raise | reject (fail exists; no catching) | accept | expected-findings cluster: error-handling-as-value |
| no-type-ascription | pending | (e : ty) annotations | reject | accept | checklist 2.3: must re-verify, never relabel, when it lands |
| no-user-modules | pending | module M = ... and imports | reject | accept | parked with trial-resolution design on file |
| no-anonymous-records | pending | {| A = 1 |} and undeclared record literals | reject (exact declared field set) | accept | one telemetry hit; SEMANTICS: rows close on discharge |
| no-destructuring-binders | pending | let (a, b) = ... , fun (x, y) -> | reject | accept | tied to the tuples decision |
| no-elif | pending | elif keyword | reject (else if chains) | accept | trivial; no demand |
| semicolon-command-argv | different | `;` inside a command line (bash chains; weir passes literal argv + warns) | argv word + warning | n/a (bash prior, not F#) | SEMANTICS: sequencing; the no-injection pin |
| semicolon-greedy-bodies | different | single-line-TYPED `if c then a ; b` groups INSIDE the body (F# verbose groups outside); multi-line siblings group F#-faithfully since the offside close (2026-07-20) | body-scoped | trailing | SEMANTICS: sequencing — greedy survives only where the sigil-era continuation join needs it; the assembler paren-wraps compounds at same-level siblings |
| record-fields-ignore-indent | different | inside an open `{ }` weir is indentation-blind (col-0 fields legal); F# offside rejects | brace mode | none | SEMANTICS: records — record continuations are expression context, the assembler tracks braces not columns |
| bang-sigil | different | `!(cmd chain)` runs-and-streams (bash: extglob/history `!`) | effect sigil, unit | n/a (bash prior; invisible to the F# oracle) | SEMANTICS: sigils |
| capture-sigil-aligns | different | `$(cmd chain)` captures output — the bash prior HELPS here (recorded per the == archaeology precedent: priors that help get named too) | capture, typed seq<string> | n/a (bash prior) | SEMANTICS: sigils |
| comment-boundary | different | // mid-token is NOT a comment (https://... barewords); comment needs line start or preceding whitespace | url survives | 1// c is a comment | SEMANTICS: comments; nuget receipt |
