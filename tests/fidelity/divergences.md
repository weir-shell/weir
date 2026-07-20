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
| no-imperative-loops | rejected | for / while | reject (pipelines + ranges are iteration) | accept | SKILL: iteration; comprehension-for is separately pending |
| no-tuples | pending | tuple literals and types | reject | accept | deferred at review pending prior-bleed evidence; destructuring is the real scope |
| single-payload-unions | pending | union case with a starred payload | reject | accept | record-wrapping today; reopens with tuples |
| no-literal-patterns | pending | int/string literal patterns in match | reject | accept | corpus-mined; when-guard is the spelling |
| no-let-rec | pending | let rec (reserved word) | reject | accept | recursion unserved; pipelines cover iteration |
| no-unary-minus | pending | negative literals outside ranges | reject | accept | 0 - x idiom; ranges have the literal |
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
