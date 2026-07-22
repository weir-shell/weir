# Decision index

Keyed one-liners. The STORY lives where the pointer says — NOTES.md
(section title), SEMANTICS.md, tests/fidelity/divergences.md (row id),
PROCESS.md — this file never duplicates it. Code comments cite keys as
`[D:key]` plus a half-line local why; dates live here, not in comments.
F#-border decisions REUSE their divergence-row id as the key (one name
per decision). Append-only: a reversed decision gets a new entry naming
the old key, never an edit-in-place.

| key | date | decision | story |
|---|---|---|---|
| one-pipeline | 2026-07-21 | every consumer (runner, REPL, -e, check, lsp) dispatches through Script.checkStatement; consumers render, never re-derive | NOTES: "One pipeline — the mirror incident's fix" |
| check-lsp-chain | 2026-07-21 | weir check [--json] is the agent-facing diagnostics core and the LSP's payload generator; no evaluation by construction | NOTES: "weir check + weir lsp — the chain lands" |
| lsp-v1 | 2026-07-21 | stdio JSON-RPC on System.Text.Json (DOM reader / Utf8JsonWriter, AOT-safe); whole-file recheck inside the 10-11ms license | NOTES: "weir check + weir lsp — the chain lands" |
| assume-resolver | 2026-07-21 | check-only consumers assume command-shaped heads (cmd-not-found WARNINGS); the runner keeps hard resolution; the parse resolver is built per-statement from the current env | NOTES: "Live-testing receipts: check assumes commands; the resolver goes per-statement" |
| clean-parse-dump | 2026-07-21 | parse errors show the ORIGINAL source line + caret, never assembled text; FParsec snippet blocks stripped, embedded positions translated to physical | NOTES: "Parse errors show the unassembled source" |
| missing-command-diagnosis | 2026-07-21 | on parse failure, retry under the assume-resolver; if it parses, name the missing command heads precisely instead of dumping the parser | NOTES: "Runner missing-command diagnosis; a masking confession" |
| assembly-recovery | 2026-07-21 | tooling paths drop the line that breaks assembly (≤10, each kept as an `assembly` diag) instead of erasing document knowledge | NOTES: "fmt field-drift + assembly recovery" |
| repair-completion | 2026-07-21 | completion REPAIRS the broken statement (blank filler for a dangling prefix, closers appended) and typechecks the repair for cursor-exact results | NOTES: "Error-recovery completion — the park opens on a user push" |
| hole-completion | 2026-07-21 | unbound lowercase names bind as fresh TVars before inference, so pipelines-with-holes still type and `t.` completes | NOTES: "Error-recovery completion — the park opens on a user push" |
| open-row-compat | 2026-07-21 | an open row whose fields fit inside a declared record completes the record's FULL field set | NOTES: "Open rows meet nominal records; cursor-local repair" |
| declared-fields-fallback | 2026-07-21 | an UNRESOLVABLE `x.` head offers every declared record's fields rather than nothing | NOTES: "Completion for params — the declared-fields fallback" |
| completion-textedit | 2026-07-21 | completion items carry explicit textEdit ranges (bare labels double-inserted after dots and were prefix-filtered inside parens) | pinned in tests/lsp/lsp-e2e.py |
| json-relaxed-escaping | 2026-07-21 | JSON payloads use UnsafeRelaxedJsonEscaping — LSP/CLI output, never HTML; default " escapes tripped micro's plugin | pinned at frame level in tests/lsp/lsp-e2e.py |
| owned-line-editor | 2026-07-21 | the ReadLine package is replaced by an owned editor: bash key semantics (Ctrl+C cancels line, Ctrl+D EOF, word nav) | NOTES: "Three out-of-band asks: exit, Ctrl+D, usage" |
| exit-rename | 2026-07-21 | Exit.code -> `exit n` (F#-parity); `fail` keeps message-carrying exit 1 | NOTES: "Three out-of-band asks: exit, Ctrl+D, usage" |
| squiggle-on-binder | 2026-07-21 | binder-name spans are re-derived from statement text (SLet carries a bare string); casing squiggles sit on the name, not the RHS | pinned in ci/e2e.sh |
| colored-diagnostics | 2026-07-21 | errors/warnings colored per-stream on TTY only; NO_COLOR and TERM=dumb respected — pipes/CI see plain text by construction | pinned by every e2e capture being non-TTY |
| lowercase-binds | 2026-07-21 | the casing law: binding names start lowercase; uppercase is types/modules/constructors | divergences.md row; NOTES: "The casing law — lowercase binds, uppercase declares" |
| exhaustiveness-hard-error | 2026-07-18 | a non-exhaustive match is a hard error (F# warns) | divergences.md row; NOTES: "Amendment: exhaustiveness is a hard error" |
| unreachable-arm-hard-error | 2026-07-21 | an arm below an unguarded catch-all is a hard error AT the catch-all, with a constructor hint for variable binders | divergences.md row; SEMANTICS: branching |
| tuples-reversal | 2026-07-21 | tuples land (literals, types, patterns, binders); Pair {Fst;Snd} retired; `*` claims tuple type syntax | NOTES: "REVERSAL: tuples land; \"records are the product\" retires" |
| bare-comma | 2026-07-21 | comma is the tuple constructor at F#'s precedence; weir-only cell: below `;` | NOTES: "Pattern binders + the bare-comma amendment" |
| pattern-binders | 2026-07-21 | irrefutable patterns destructure in let/params (parens required on params); refutable binders are check errors | divergences.md row no-pattern-binders; NOTES: "Pattern binders + the bare-comma amendment" |
| literal-patterns | 2026-07-20 | int/string/() literal patterns; literals never complete a match alone (F#'s rule) | NOTES: "Literal patterns + () thunks" |
| let-param-sugar | 2026-07-20 | `let f x y = e` desugars to nested lambdas — the corpus mining's top yield | NOTES: "let f x = ... parameter sugar" |
| inferred-type-classes | 2026-07-20 | closed {Eq, Show, Ord}, compiler-owned, machine-regime-only, fully erased; sentinels retired (Sessions A-C), the sole runtime type check died | NOTES: "Type classes Session A/B/C" entries |
| child-env-overlay | 2026-07-20 | cmdEnv/runEnv inject per-child env as an OVERLAY (set those names, inherit the rest, parent untouched); one spawn path via Proc.linesWith | NOTES: "Child-env injection — the shEnv receipt lands" |
| env-sugar-layers | 2026-07-20 | sigils take an env slot glued to the glyph ($e(...) / !e(...)); line-end `!name` makes an env district | NOTES: "Env sugar Layers 1+2 — the seam pays out" |
| one-scanner | 2026-07-20 | foldOutsideStrings is the ONE string-state primitive; a second inline quote machine is a review flag | NOTES: "Assembler formalization — the boundary question" |
| structured-parse-failure | 2026-07-20 | parse positions travel as DATA (ParseFailure.Col), never regexed out of FParsec message text | NOTES: "Assembler formalization — the boundary question" |
| comment-transparency | 2026-07-20 | comment-only lines are transparent to assembly (F#-faithful; they used to end statements) | NOTES: "Fix round: transparent comments, parse-error attribution" |
| fmt-v1 | 2026-07-18 | fmt normalizes leading indent + trailing ws ONLY; the result must re-assemble identically or fmt refuses to write | NOTES: "weir fmt — the canonical formatter, v1" |
| fmt-depth-model | 2026-07-20 | indent = open levels, deepest first (any deeper line opens, same-level returns, col-0 resets) — preserves every assembler comparison | NOTES: "Hardening sweep — the postmortem pays out" |
| fmt-brace-plus-2 | 2026-07-21 | record fields align at open-brace column + 2 (house style; the depth model had drifted them to depth*4) | NOTES: "fmt field-drift + assembly recovery" |
| bracket-heads-expression | 2026-07-18 | `[` never heads a command (a line-head list would resolve to /usr/bin/[) | NOTES: "Review amendments: [ heads nothing; the sh builtin is gone" |
| typed-env | 2026-07-20 | the Env module: Env.get / Env.vars / Env.load Config (typed, one aggregated error) | NOTES: "Typed Env — Env.load Config" |
| prefix-minus | 2026-07-21 | prefix minus with F#'s adjacency rule; the oracle overturned the `f -1` subtraction folklore mid-landing; retires no-unary-minus | divergences.md retirement note; oracle pins in tests/Weir.Fidelity/Pins.fs |
| composition-operators | 2026-07-22 | `>>`/`<<` compose functions at the PIPE's precedence (the oracle refuted the tighter-than-pipe folklore: `xs \| f >> g` needs parens, F#'s rule); command-line `>`/`>>` stay argv words WITH File.write/File.append hint warnings | divergences.md rows redirect-argv + no-heredoc; oracle pins in Pins.fs |
