# Named divergences from F# (machine-read by Weir.Fidelity)

One row per deliberate divergence. The oracle FAILS the build on any
fidelity pin whose F#/weir verdicts disagree without citing an id from
this table. Adding a row is cheap by intent — divergence staying cheap
is the argument that killed the subtractive fork. SEMANTICS.md remains
the prose home; the cross-reference column points there.

| id | shape | weir | fsharp | semantics ref |
|----|-------|------|--------|---------------|
| blank-line-ends-statement | blank line inside an indented block | reject | accept | Scripts: multi-line rules |
| double-equals | equality is == ; = is binding-only | == accepts / = rejects | == rejects / = accepts | Operators: why == and not = |
| no-tuples | tuple literals and types | reject | accept | Types: no product types |
| single-payload-unions | union case with a starred payload | reject | accept | Types: generic declarations |
| statement-rule | discarded non-unit statement | reject | accept (warning) | Scripts: the statement rule |
| line-comments-only | (* block comments *) | reject | accept | Scripts: comments |
| no-printf-family | printfn / sprintf / %d | reject | accept | Operators: interpolation |
| no-mutation | mutable / <- assignment | reject | accept | Evaluation: no mutation |
| no-let-rec | let rec bindings | reject | accept | Backlog: recursion |
| no-unary-minus | negative literals outside ranges | reject | accept | Operators: range literals |
| pipe-precedence-error | piping into an operator expression | reject (targeted) | accept ((|>) into bool = valid partial shapes differ) | Operators: precedence error |
| no-let-param-sugar | let f x = ... function definitions | reject (spell it let f = fun x -> ...) | accept | Operators: bindings |
| no-literal-patterns | int/string literal patterns in match | reject | accept | Types: match patterns |
| interp-scalar-only | non-scalar interpolation holes ($"{someFn}") | reject | accept | Operators: interpolation |
| no-format-specifiers | format specifiers in holes ($"{x:N2}") | reject | accept | Operators: interpolation |
