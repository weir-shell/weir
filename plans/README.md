# Plans — the development history, one line each

Process artifacts, not documentation: each file is a blessed plan
with its session report. Status lives in each header; this index
is chronological (git first-commit date).

- 2026-07-11 [EXECUTED] [PLAN.md](PLAN.md) — PLAN
- 2026-07-12 [EXECUTED] [PLAN-command-mode.md](PLAN-command-mode.md) — process lifecycle fix + sh/cmd split + command mode
- 2026-07-13 [EXECUTED] [PLAN-prompt-ergonomics.md](PLAN-prompt-ergonomics.md) — prompt ergonomics: command-callable builtins, diagnostics, CI, complete/collect
- 2026-07-16 [EXECUTED] [PLAN-library.md](PLAN-library.md) — library phase: strings, head, generic unions, measure algebra
- 2026-07-17 [EXECUTED] [PLAN-modules-and-scripts.md](PLAN-modules-and-scripts.md) — proposal: builtin modules, then a script language
- 2026-07-17 [EXECUTED] [PLAN-read-booleans-overflow.md](PLAN-read-booleans-overflow.md) — consolidated read, booleans, overflow policy, data-range battery
- 2026-07-18 [EXECUTED] [PLAN-remove-measures.md](PLAN-remove-measures.md) — remove measures
- 2026-07-18 [EXECUTED] [PLAN-unit-and-print.md](PLAN-unit-and-print.md) — unit, print, and the statement rule
- 2026-07-20 [EXECUTED] [PLAN-assembler-formalization.md](PLAN-assembler-formalization.md) — assembler formalization: the text layer earns its structure
- 2026-07-20 [EXECUTED] [PLAN-child-env.md](PLAN-child-env.md) — child-env injection: the shEnv receipt
- 2026-07-20 [EXECUTED] [PLAN-command-sigils.md](PLAN-command-sigils.md) — command-mode sigils: !(...) and $(...)
- 2026-07-20 [EXECUTED] [PLAN-grammar-consolidation.md](PLAN-grammar-consolidation.md) — grammar consolidation: the grouping seam pays its debts
- 2026-07-20 [EXECUTED] [PLAN-sequencing-and-args.md](PLAN-sequencing-and-args.md) — block sequencing, the Seq access family, Args
- 2026-07-21 [EXECUTED] [PLAN-literals-thunks.md](PLAN-literals-thunks.md) — literal patterns, `()` thunks, and tuples
- 2026-07-21 [EXECUTED] [PLAN-lsp-chain.md](PLAN-lsp-chain.md) — the LSP chain: one pipeline, check --json, weir lsp
- 2026-07-21 [EXECUTED] [PLAN-type-classes.md](PLAN-type-classes.md) — inferred type classes: Eq, Show, Ord
- 2026-07-22 [EXECUTED] [PLAN-composition.md](PLAN-composition.md) — mini-plan: `>>` composition + the redirect hints
- 2026-07-22 [EXECUTED] [PLAN-corpus-remine.md](PLAN-corpus-remine.md) — the corpus re-mine
- 2026-07-22 [EXECUTED] [PLAN-fold.md](PLAN-fold.md) — `Seq.fold` + `fun a b ->` sugar
- 2026-07-22 [EXECUTED] [PLAN-raw-strings.md](PLAN-raw-strings.md) — raw strings: `@"..."` and `"""..."""`
- 2026-07-22 [EXECUTED] [PLAN-record-update.md](PLAN-record-update.md) — record update: `{ r with F = v }`
- 2026-07-22 [EXECUTED] [PLAN-regex.md](PLAN-regex.md) — regex: the `Regex` pattern and the `Str` match family
- 2026-07-22 [EXECUTED] [PLAN-small-items.md](PLAN-small-items.md) — the small-items sweep: elif, defaulting order, masking
- 2026-07-22 [EXECUTED] [PLAN-vscode.md](PLAN-vscode.md) — VS Code extension: client glue + TextMate grammar
- 2026-07-23 [EXECUTED] [PLAN-attributes.md](PLAN-attributes.md) — attributes: check-time, erased, consumers-registered
- 2026-07-23 [EXECUTED] [PLAN-blank-lines.md](PLAN-blank-lines.md) — blank lines inside brackets
- 2026-07-23 [EXECUTED] [PLAN-block-let-cmd.md](PLAN-block-let-cmd.md) — block-let command RHS: the uniformity fix
- 2026-07-23 [EXECUTED] [PLAN-body-blanks.md](PLAN-body-blanks.md) — blank lines inside blocks: the core reversal
- 2026-07-23 [EXECUTED] [PLAN-choose.md](PLAN-choose.md) — mini-plan: `Seq.choose` + the verbatim-highlighter fix
- 2026-07-23 [EXECUTED] [PLAN-exit-reifiers.md](PLAN-exit-reifiers.md) — exit-code reifiers: `| succeeds` and `| orFail "msg"`
- 2026-07-23 [EXECUTED] [PLAN-multiline-brackets.md](PLAN-multiline-brackets.md) — multiline type declarations + list literals
- 2026-07-23 [EXECUTED] [PLAN-paramful-rhs.md](PLAN-paramful-rhs.md) — param-ful command RHS: `let f r = git rev-parse $r | Seq.head`
- 2026-07-23 [EXECUTED] [PLAN-repl-color.md](PLAN-repl-color.md) — REPL syntax coloring
- 2026-07-23 [EXECUTED] [PLAN-seq-force-patterns.md](PLAN-seq-force-patterns.md) — `Seq.force` naming hygiene + seq patterns (design-on-file)
- 2026-07-23 [EXECUTED] [PLAN-typed-argv.md](PLAN-typed-argv.md) — typed argv: `Args.load` over records and unions
- 2026-07-24 [EXECUTED] [PLAN-argv-splat.md](PLAN-argv-splat.md) — argv splat: `$@xs` (N things, N words)
- 2026-07-24 [EXECUTED] [PLAN-arm-commit.md](PLAN-arm-commit.md) — mini-plan: arm-commit (the consumed-separator law, unified)
- 2026-07-24 [EXECUTED] [PLAN-default-attr.md](PLAN-default-attr.md) — `[<Default>]`: the resting point moves
- 2026-07-24 [EXECUTED] [PLAN-env-default.md](PLAN-env-default.md) — mini-plan: Env.load consumes `[<Default>]`
- 2026-07-24 [EXECUTED] [PLAN-fuzzer.md](PLAN-fuzzer.md) — the assembler fuzzer: generative line-shape testing
- 2026-07-24 [PROPOSED-AMENDED] [PLAN-miner-receipts.md](PLAN-miner-receipts.md) — plan proposal: the miner's receipts (`rmatchAll`, `feed`, `distinct` — and the last python retires)
- 2026-07-24 [EXECUTED] [PLAN-multiline-lambda.md](PLAN-multiline-lambda.md) — multiline lambdas: `(fun ... ->` opens a body block
- 2026-07-24 [EXECUTED] [PLAN-path-glob.md](PLAN-path-glob.md) — mini-plan: `Path.glob`
- 2026-07-24 [EXECUTED] [PLAN-reifier-family.md](PLAN-reifier-family.md) — mini-plan: the reifier family completes (`| exitCode`, orFail streams)
- 2026-07-24 [EXECUTED] [PLAN-script-path.md](PLAN-script-path.md) — mini-plan: `scriptPath` (the $0 gap)
- 2026-07-24 [EXECUTED] [PLAN-semantic-tokens.md](PLAN-semantic-tokens.md) — semantic tokens: the mode boundary made visible
- 2026-07-24 [EXECUTED] [PLAN-shared-flags.md](PLAN-shared-flags.md) — shared flags by containment: the union-typed field
- 2026-07-24 [EXECUTED] [PLAN-spawn-spec.md](PLAN-spawn-spec.md) — mini-plan: the spawn-spec refactor + `feed`
