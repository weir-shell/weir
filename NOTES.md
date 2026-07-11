# Spike Notes

## Spike 1 — Bidirectional checker, nominal only (2026-07-11, session 1)

**Built**: spanned AST (`Expr = { Kind; Span }`), `Ty` (int-with-optional-measure/str/bool/fn/seq/record-by-name), `TypeEnv` (Values + Types), `infer`/`check` pair in `Check.fs`, typo hints via edit distance, REPL now typechecks before eval and prints caret-underlined span errors. 43 tests.

**Acceptance**: `ls | where (fun f -> f.Size > 1<mb>) | first 5` checks to `seq<FileRow>`; `f.Sze` rejected with span exactly on `Sze` + "Did you mean 'Size'?". Perf: ~µs per check, 10ms bound trivially met.

**Design decisions**:
- Binops promoted from desugared builtins to `EBinOp` — overloading (`+` on int/str, measure-preserving arithmetic) doesn't fit monomorphic env entries. `typeBinOp` is the single overload table.
- Builtins are monomorphic (`where : (FileRow -> bool) -> seq<FileRow> -> seq<FileRow>`); polymorphism deliberately absent, revisit with row polymorphism (parked).
- UoM = `TInt of string option`, equality by name, erased at runtime. `+`/`-`/comparison require same measure; `*`/`/` unitless only (no measure algebra).
- Lambdas don't infer, but two refinement rules cover the shell idioms: lambda applied to a known arg, and pipe-into-lambda (arg type flows into the param).
- `EField` carries the field's own span so typo errors point at `Sze`, not all of `f.Sze`.
- `==` not `=` for equality (avoids let ambiguity). Composite spans are unions of child spans; leaf tokens capture position before ws-skip.
- Spans compose via `Span.union`; retrofitting confirmed as the right fear — touching every parser production once was enough, but only because the AST was 8 cases.

**Surprised**: how little the bidirectional core is — `check` has 3 real rules (lambda, let, fallback-to-infer-and-compare). The complexity lives in `infer`'s per-node rules and error message quality, not the discipline itself.

**Verdict (provisional)**: checker felt tractable to write. GATE CONDITION: user line-by-line review of Check.fs pending — spike isn't closed until then.

## Spike 0 — Toy interpreter (2026-07-11)

**Built**: `Expr` DU (int/str/bool/var/let/lambda/app/pipe), FParsec parser, tree-walk eval/apply, REPL with persistent top-level `let`. 23 tests. `1 + 2 |> double` → 6 end to end.

**Learned**:
- FParsec's `OperatorPrecedenceParser` handles the whole binop/pipe layer; binops desugar to `EApp(EApp(EVar "+", l), r)` against builtin env entries, so eval has no operator special cases.
- Lambda/let-in must be *terms* of the OPP (not alternatives outside it) or they can't appear on a pipe RHS (`5 |> fun x -> x * x`). Greedy lambda body = F# semantics for free.
- `Value` can't derive structural equality once `VBuiltin of (Value -> Value)` exists — custom equality (structural for data, reference for functions) needed for test assertions. Will matter again for `VSeq` in Spike 4.
- FParsec error messages come with line/col and a caret out of the box — good omen for Spike 1 span work.

**Surprised**: nothing structural. Keyword-vs-identifier ambiguity (`true`, `fun`) needed the usual `attempt` + `notFollowedBy` dance.

**Verdict**: continue. Eval/apply shape clicks, FParsec workable. → Spike 1.
