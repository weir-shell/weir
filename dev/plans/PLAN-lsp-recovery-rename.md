# PLAN: LSP recovery + rename — typed trees for broken statements, and the park opens

**Status: RULED** (user, 2026-10-26) — R1–R8 all as recommended; see Rulings.

## The receipt

Two findings, both verified over the wire against v0.0.78.

**A. One type error blanks a whole statement.** A module's `let sub … =`
spanned lines 74–90 with one type error on line 81. Every line 74–90
lost hover and go-to-definition; statements after it still answered. The
LSP's typed knowledge is per top-level statement: a statement that fails
to check yields `Error d` and no tree, so inside it there is nothing to
answer. A 17-line function with one wrong line is the common shape of
a file being edited — this is the state the editor spends most of its
time in.

**B. No rename, references or highlight.** `initialize` (Lsp.fs, the
capabilities literal in `run`) advertises hover, completion, definition,
formatting, codeAction, semanticTokens. [D:lsp-requests] parked rename /
references / full-definition on one prerequisite — binder spans in the
checker — with the reopen criterion "a real user demands one of the
three". The user asked for F2 in VS Code. The criterion has fired.

## What the code says today (anchors, verified)

**Statement granularity.** `Script.analyzeLinesAs` (Script.fs) folds the
file's logical lines; per line it calls `checkStatement` →
`checkStatementCore` → one of `Check.typecheckWith` (SLet),
`Check.typecheckBinder` (SLetPat), `Check.typecheck` (SCmd/SExpr),
`Check.typecheckAgainstSig` (signed lets). The result is
`Result<CheckedStatement, StmtDiag>`; `CheckedStatement = { Kind:
CheckedKind; Env; Warnings }` and `CheckedKind`'s `KLet/KLetPat/KCmd/KExpr`
carry the `TypedExpr`. The `Error d` branch of the fold adds the
diagnostic, poisons the whole-file trackers, re-parses the line to warn
missing command heads, and hole-binds the statement's `let` names
(`holeScheme`, `TVar "__hole"`) so later statements do not cascade
([PLAN-diagnostics-arc B5+B6], the comment is at the branch). Nothing
typed survives.

**There is no "fold over the body's lines" in Check.** The parser does
the folding: a block's lines become a right-nested chain —
`Parser.foldSeqExpr` (ESeq over statement-position elements),
`letInBody` (`ELet(name, nameSpan, curryParams ps value, body)` where
`body` is the REST of the block), `ELetPat` likewise; `nodeTooDeep`
treats that continuation as width [D:block-width]. The checker recurses
node by node — the arms that matter: `infer` ESeq / `check` ESeq, `infer`
ELet / `check` ELet, `infer` ELetPat, `ELambda`/`ELambdaPat` via
`lambdaCore`, `EMatch` (arms = (Pattern, guard, body)), `EWithin` via
`withinContracts`, `ERetry`, `EIf`. All Result-threaded through the
`result` CE; the one exception is `BudgetExceeded` (private), caught by
`catchBudget` at the four `typecheck*` entry points. So the recovery
hook is the ESeq/ELet/ELetPat arms, in both the infer and check twins:
every block boundary at every depth passes through them.

**The LSP walk.** `toLogical` (physical → (LogicalLine, CheckedStatement,
joined col)), `teOf` (the statement's TypedExpr), `nodeAt` (smallest
span-containing node), `localDef` (lexical scope walk to the TEVar at
the column: TELet nameSpan, TELetPat/TELambdaPat/match-arm `patScope`
over `Pattern.PSpan`, TELambda paramSpan), `letSiteIn` (top-level
binder: last `let` above, textual column bounded by the first `=`),
`definitionTarget` / `definitionFor` (the pure pin surface), `hoverAt` /
`hoverType`. All read `analyzeCached` [D:lsp-analysis-cache].

**Binder spans — the brief is partly stale.** Session C of
[PLAN-diagnostics-arc] (2026-07-27) already made params, inner lets and
pattern payload binders resolve (pins: "param use -> param",
"inner-let use -> its binder", "payload use -> the binder"). What is
still missing, from `Check.TypedKind`:

- `TEWithin … binder: string option` — the Ast's `(string * Span)
  option` loses its span at `TEWithin(kind, binder |> Option.map fst, …)`
  (three construction sites in Check).
- `TERetry … until: (string * TypedExpr) option` — the Ast's `((string *
  Span) * Expr)` loses its span.
- `localDef` has no TEWithin/TERetry arm. A use of a `within tmp d`
  binder therefore reads as "top-level territory" and falls to
  `letSite`: null, or — when a top-level `let d` exists — the WRONG
  jump. Latent definition bug, derived from the code (not yet probed
  over the wire); P0 fixes and pins it.
- A top-level `KLet` carries no binder span; three places re-derive it
  from the same regex (`checkStatementCore`'s `nameSpan`,
  `UnusedTracker.LetBinder`, `Lsp.letSiteIn`/`binderCol`). Exact, by
  [D:squiggle-on-binder]; the plan reuses `letSiteIn` rather than
  widening `KLet`.

**Use sites carry no binder identity.** `TEVar of string`; resolution is
post-hoc and lexical (`localDef`). This is sound because the typed tree
mirrors the scoping the checker applied, and it is the cheap route:
stamping TEVar with a binder id would touch every TEVar match in Eval.
The plan keeps post-hoc resolution and makes it a single, complete pass.

**The ledger row is stale.** [D:lsp-requests] still says "params/match
binders/block-lets carry no binder spans"; Session C executed and the
row was never amended. The new row names it (append-only).

## The shape

### A. Block-level recovery — a second pass, tooling-only

Same discipline as [D:assembly-recovery] and the top-level hole-binding,
one level down and recursive: an inner statement that fails becomes a
hole, its `let` names bind to the hole scheme, and the block continues,
so the top-level statement yields a typed tree with only the broken
line missing.

**Second pass, not a changed first pass.** `checkStatement` stays
exactly as it is — same `Result`, same first error, same purity layer,
same discard gate — for the runner, the REPL, `-e`, modules, `weir
check`. Recovery runs ONLY in `analyzeLinesAs`'s `Error d` branch: after
the diagnostic is recorded and the names hole-bound, the statement is
re-checked once in recovery mode purely to obtain a tree. The
diagnostic is the first pass's by construction, so the "same errors,
same exit codes" invariant needs no audit of error-rewriting arms
(`Result.mapError`/`nameHoleDefault` sites in Check) — nothing they do
changes, because the pass that reports never recovers. Cost: an
erroring statement checks twice, bounded by `workBudget`; it pays only
on a cache miss.

**Checker surface.** `Ctx` gains `mutable Recovered: (TypeError * Span)
list option` (`None` = today's behaviour everywhere). In recovery mode
the ESeq, ELet and ELetPat arms (both twins) catch a failed child:

- ESeq `first` fails → record `(e, first.Span)`, substitute a hole node
  for `first`, continue `rest` under the same demand.
- ELet `value` fails (or `checkBinderName` fails) → record, hole node for
  the value, bind `name` to the hole scheme (`generalizeLet` of a fresh
  var yields `∀a.a`), continue `body` — the body is the rest of the
  block, so this is the "later lines still type" case.
- ELetPat value/binder fails → record, hole value, bind every
  `patNameSpans` leaf to the hole scheme, continue `body`.

A failing child that is itself a single expression (a one-line lambda
body, a match arm whose body is one expression, an `if` branch) has no
inner ESeq/ELet to stop at, so the failure propagates to the nearest
enclosing ESeq/ELet, which holes the whole construct. That is the
granularity: a statement, at any depth. Expression-level holes are the
later step (below).

**The hole node.** R8 decides between (a) a new `TEHole of Span`
TypedKind case — honest, self-describing, every exhaustive matcher gains
a one-line unreachable arm (Eval 1, Purity 5, Can 1, Check 2, by the
`| TEUnit` count), caught by FS0025 under [D:host-strictness]; eval
never sees one (recovery is tooling-only) — or (b) a `TEUnit`-kinded node
typed `TUnit` (statement position) / a fresh `__hole` TVar (let value)
plus a side list of hole spans the LSP consults. Recommend (a).

**Boundary in recovery mode.** `resolvePendingSplices`, the stranded
check, `warnings`, the hole-default provenance all run as today but
their failures are ignored (the tree is the product; the diagnostic
already exists). `finalizeExpr` runs so hover types are resolved.
`BudgetExceeded` in the recovery pass → no tree (as today).

**Entry point.** `Check.typecheckRecovering (env) (expr) : (TypedExpr *
(TypeError * Span) list) option` beside `typecheckWith`; `Script`'s
recovery helper dispatches on the parsed `Stmt` as `checkStatementCore`
does (SLet → the value; SLetPat → `typecheckBinder`'s shape; SCmd/SExpr
→ the expression; signed lets → plain `typecheckRecovering`, the
signature's types are not needed for a tree) and builds a
`CheckedStatement` whose `Env` is the hole-bound `tenv` the Error branch
already computed (so nothing downstream reads a partial env).

**Where partials ride.** `analyzeLinesAs` returns a fifth product,
`partials: (LogicalLine * CheckedStatement) list` — statements that did
NOT check, with their recovered trees. They never enter `stmts`, so
`schemaDiagnostics`, `sigCmdDiagnostics`, `continuedCommandDiagnostics`,
`effectBeforeArgs`, the trackers, Can's `ofChecked` and the REPL's
`#save` keep seeing exactly what they see today — the one-pipeline
parity (`check --json` = runner = LSP publish, the lsp-e2e parity probe
[D:walk-findings]) holds by construction. The four `let diags, _, _, _ =`
callers (Can, Fmt, Repl, Lsp) take the fifth. The alternative — a
`Partial` flag on `CheckedStatement` and a filter in every consumer —
puts the invariant on discipline instead of on the type; declined.

**The cache.** `analysisCache` stores the whole return value per (file,
init mode, exact text); the fifth product rides inside it. No new
invalidation: any document event drops everything, as today. Hover,
definition, references and rename all read one cache entry.

**LSP.** `toLogical` and the completion-env scan work over `stmts ∪
partials` ordered by `ll.Head`. Inside a partial statement every
existing answer works where a node exists; a position inside a hole
span answers null (hover, definition, references — the honest answer,
[D:hover-silence]'s rule). A hole-bound inner binder hovers null (its
type is a `__hole` var — the existing `__hole` prefix convention). `/// doc`
reads, form-word hovers and the unused/row machinery are untouched.

**Why block-level first.** (1) It is the granularity the receipt needs:
the lost lines were sibling statements of the broken one. (2) It reuses
the top level's own discipline (hole scheme, Error-branch placement)
and lands in three arms. (3) Expression-level holes mean substituting a
fresh-typed hole for the failing SUBEXPRESSION and continuing
unification — which changes the types the rest of the statement
resolves to and so changes what hover SHOWS (an `'a` where the user
expects `int`), and the failure point in an expression is wherever
`bind` was when it refused (`ctx.WorkSpan`), not a syntactic unit. That
is a design of its own (which node, which type, how to keep hover
honest) and belongs after the block level has been lived with.

### B. Occurrences → references, highlight, rename

**One resolver.** `Check.occurrences (te: TypedExpr) : Occurrence list`,
the sibling of `bindingUsage` (same walk, same scope removal, same
`patNameSpans`, same yaml-template coverage), where

```
Binder     = Local of Span | TopLevel of name   // TopLevel = free in this statement
Occurrence = { Name: string; Span: Span; Binder: Binder; IsBinderSite: bool }
```

Every binding form is a scope: `TELet` (nameSpan), `TELetPat` /
`TELambdaPat` / match-arm patterns / `TYtForEntries` / `TYtForItems`
(pattern `PSpan` leaves — PVar inside PTuple, PRecord values, PCase
payloads, PRegex binder, PCons/PSeqList), `TELambda` (paramSpan),
`TEWithin` binder and `TERetry` `until` binder (spans from P0). Match
guards scope under the arm's pattern. `localDef` becomes a consumer of
this walk (or is deleted) so there is ONE scope walk, not three.

Cross-statement: a top-level binder's references are the `TopLevel`
occurrences of its name in the statements after it, up to (excluding)
the next top-level statement that binds the same name; there is no `let
rec`, so a use of the name in its own statement belongs to the previous
binder. `letSiteIn`/`binderCol` give the KLet binder site; KLetPat's
sites come from `PSpan`. A signed member (`KSig`, `SSig.nameSpan`) adds
the signature line as an occurrence of the same binder.

**Spans → edits.** A TEVar's span may include a sigil: `splicePath` is
`spanned (sigil >>. rawWord)`, so `$x` spans the `$` and `$@xs` spans
`$@`. The edit range is `[span.End.Col - name.Length, span.End.Col)` —
one rule for plain words, splices, splats and `$x.field` heads. Each
occurrence's logical column maps to physical with `Script.translate`
(identifiers never cross a physical line; the recovery re-indent is
handled there [D:recovery-physical-indent]).

**Weir-specific occurrences, decided:**

| form | node | v1 |
|---|---|---|
| plain use `x`, `x.f` head | TEVar | occurrence |
| interpolation hole `$"{x}"`, `{x.f}`, `$<<<` holes, `$$<<<` `$x`/`${e}` | TEInterp → IExpr → TEVar (span inside the string) | occurrence (definitionFor already resolves these) |
| argv splice `$x`, `$x.f` | TEVar with sigil span | occurrence, sigil-stripped edit |
| splat `$@xs` | TESplat(TEVar) | occurrence, sigil-stripped edit |
| yaml district `$x`, `$(e)`, `$x:` key, `for p in xs` | TYtSplice / TYtKeySplice / TYtFor* binder | occurrence / binder site |
| `_.field` shorthand | `ELambda("_", …)` + `EVar "_"` | never (wildcard; silent) |
| `with { x = … }` / `{ r with … }` | TEUpdate source + field paths | source is an occurrence; fields are not v1 |
| pattern binders (`let (a, b)`, `\| Push n`, `fun (a, b)`, `for (k, v)`, regex binder) | PVar PSpan | binder sites |
| `within K b`, `until b` | TEWithin / TERetry (P0 spans) | binder sites |
| module-qualified `Lib.f` | TEVar containing `.` | not a binder; rename refused; references per R2 |
| record fields, union cases, type names | TEField / TERecord / PCase / KType | rename refused (fields are wire keys for `from json`/`to json`/`Args.load`/`Env.load`; cases are wire tags; types ride `from json T` and sigs); references not v1 |
| builtins / prelude names (`print`, `Seq.map`) | TEVar, no binder | rename refused; references per R2 |
| synthetic names (`_chain`, `\|print`, `\|seqIter`, `"()"`, reifier vars) | — | filtered: `Types.isUserName` and no `_`/`\|` prefix |
| comments, `///` docs, string literals | no node | never (R7) |

**Requests.**

- `textDocument/references`: the binder at the position (binder site or
  use), then every occurrence of that binder across the file, as
  `Location[]`; `includeDeclaration` honoured.
- `textDocument/documentHighlight`: the same set, `kind` = Write (3) at
  binder sites, Read (2) at uses. Free.
- `textDocument/prepareRename`: `{ range, placeholder }` for a renamable
  position; otherwise a `ResponseError` (`RequestFailed`, -32803) whose
  message is the reason — VS Code surfaces it under the F2 box; a null
  shows nothing, so the error is the kinder answer where the protocol
  allows one.
- `textDocument/rename`: re-validates (clients may skip prepare), then
  `WorkspaceEdit { changes: { uri: TextEdit[] } }`, the document's own
  URI string [D:lsp-uri-spelling], edits sorted by position,
  non-overlapping by construction.
- Capabilities: `"referencesProvider": true, "documentHighlightProvider":
  true, "renameProvider": { "prepareProvider": true }`.

**Refusals (prepareRename error, rename error), v1:**

1. Not a value binder's occurrence: builtins, `Lib.f`, fields, cases,
   type names, `_`, synthetic names, keywords/form words, positions in
   strings or comments.
2. Any statement in the binder's scope extent is a partial (A): "a
   statement in this name's scope has an error — fix it first" (a hole
   may hide an occurrence; a rename that misses one is the one failure
   that must not happen). R5.
3. The new name: not an identifier (`isIdentStart`/`isIdentCont`), a
   keyword (`Parser.isKeyword`), uppercase-initial (the casing law
   `checkBinderName` enforces for value binders), a reserved binder name
   (`Check.reservedBinderNames`), or `_`.
4. Collision (R4, conservative): the new name is bound in `env.Values`
   at the binder's statement (a builtin, a prelude name, an earlier
   top-level), OR occurs as any identifier — binder or use — anywhere in
   the renamed binder's scope extent (its scope subtree for a local; the
   binder statement through the statement before the next top-level
   rebinding for a top-level). Refuses some legal renames (a same-named
   binder in a disjoint inner scope); the message names the first
   colliding site.
5. Init-marked files (`#session`/`#alias` rewrites bind synthetic lets)
   and sig contract files under `.weir/sigs/` (R6).

**Cross-module rename — later, its own plan.** Needs: the importer set
(the server knows open docs only; a workspace scan of `rootUri` for
`import "…"` lines resolving to this module, buffer-over-disk through
`importSourceOverride`), `Lib.f`/alias occurrences in importers (the
`TEVar` containing `.`, cross-file `definitionTarget` already maps them),
signed-member sig lines in the module, `WorkspaceEdit` entries for
unopened files (clients apply to disk), and a refusal when an importer
is partial. Single-file rename of an exported member stays allowed in
v1 (R3): the importers' diagnostics show the breakage on their next
refresh.

## Phases

- **P0 — binder spans complete (small, ½ session; shared by A and B).**
  `TEWithin binder: (string * Span) option`, `TERetry until: ((string *
  Span) * TypedExpr) option` (the Ast already has both); construction
  sites in Check (3 + 4), matchers in Eval/Purity/Can/Script (FS0025
  lists them); `localDef` gains both arms. Pins: definition on a `within
  tmp d` binder use and an `until done` use; the shadowing case (a
  top-level `let d` plus `within tmp d` — the use inside jumps to the
  within binder). Fixes the latent wrong-jump.
- **P1 — block-level recovery (medium, 1 session).** `Ctx.Recovered`, the
  three arms × two twins, the hole node (R8), `typecheckRecovering`,
  Script's recovery helper in the Error branch, the fifth product and
  its four callers, `toLogical`/completion over `stmts ∪ partials`, the
  hole-span null gate. Pins below.
- **P2 — occurrences, references, documentHighlight (medium, 1 session).**
  `Check.occurrences`, `localDef` folded into it, `Lsp.referencesFor` /
  `Lsp.documentHighlightFor` (pure), the two handlers, capabilities.
- **P3 — prepareRename / rename (medium, 1 session).** `Lsp.renameFor`
  (pure: `Result<(line * col * len) list, string>`), the refusal set, the
  collision check, the sig-line occurrence, the two handlers, the
  `RequestFailed` error path. Editor rigs as [D:lsp-requests] did (nvim
  headless `vim.lsp.buf.rename`; VS Code is the receipt's client and
  has no headless rig — the e2e speaks its request shapes).
- **P4 — docs and ledger (ride each phase).** editors.md bullets
  ("Find references", "Rename", highlight); CHANGELOG; DECISIONS rows
  `block-recovery` and `lsp-rename` (the latter naming `lsp-requests`'s
  park as fired and its stale scope line); NOTES story; the
  diagnostics-arc Session C note gains the within/until gap.
- **Later, not here:** cross-module rename (own plan, above);
  expression-level holes (own plan, above); references on fields/cases/
  types.

A and B share P0 and the scope-walk; P1 and P2 are independent after P0
and can land in either order. Everything is check-side: no runtime, no
grammar, no editor-extension change (clients read capabilities).

## Tests

Repo practice: unit pins through the PURE entry points in
tests/Weir.Tests/Tests.fs (the `hoverType`/`definitionFor` precedent —
handlers are not unit-testable), wire probes in tests/lsp/lsp-e2e.py
(one server per section, `expect`, request ids, the parity probe
pattern), the Fuzz project for properties.

**P0 pins.** `definitionFor` on a within binder use → the binder; on an
`until` binder use → the binder; the shadowing case above.

**P1 pins (recovery).**
- Invariant: recovery changes no `weir check` output. The receipt shape
  (a multi-line `let` with one bad inner line, then statements using
  it) through `Script.analyzeLines`: diags are exactly one error at the
  bad line (byte-pinned message), `stmts` excludes the statement,
  `partials` includes it. The runner on the same file reports the same
  first error (the existing one-pipeline parity probe covers check
  --json / runner / LSP publish; it gains this file).
- Fuzz property: for every corpus script with a check error, the
  diagnostics of `analyzeLines` are identical whether `partials` is
  consulted or not (structurally trivial, pinned so it stays trivial),
  and recovery never throws (budget raise → no partial, no crash).
- `hoverType` inside the broken statement: a sibling line above the bad
  line answers its type; a sibling line below answers; the bad line
  answers null; a hole-bound inner binder's use answers null;
  `definitionFor` from a line below the bad line → the binder above it.
- A statement whose body is one failing expression stays treeless (no
  false tree).
- Over the wire: didOpen the receipt file → one diagnostic; hover on
  line 74-equivalent and 90-equivalent both answer; definition inside
  jumps; publish count unchanged (the squiggle-blink pin's shape).

**P2 pins (references/highlight).**
- `referencesFor` on a top-level binder: declaration + every use,
  stopping at the next top-level rebinding; on the rebinding's use
  set, the earlier binder is not included (shadowing touches only its
  scope).
- A param shadowing a top-level: inside the lambda → the param's set
  only; outside → the top-level's.
- Interpolation-hole occurrences included (`$"{x}"`, `$<<<`, `$$<<<`);
  argv splice `$x` and splat `$@xs` included with sigil-stripped
  columns; yaml district splice and `for` binder included; `_.field`
  yields nothing; `with { … }` source included; a comment mentioning
  the name is NOT included.
- Pattern binders: `let (a, b)`, `| Push n`, `fun (k, v) ->`, `for (k, v)
  in`, a regex binder — each binder site + uses.
- `documentHighlightFor` kinds: Write at the binder, Read at uses.
- R2's answer pinned either way (builtin → uses-in-file or null).
- Wire: references on the fixture → `Location[]` with the client's URI
  string; `includeDeclaration: false` drops the binder.

**P3 pins (rename).**
- `renameFor` of a shadowed name edits only its scope (both binders in
  one file; rename the inner → outer untouched; rename the outer →
  inner untouched).
- Edits cover: binder, plain uses, hole uses, splice `$x` → `$y`, splat,
  yaml splice, the sig line of a signed member.
- Refusals, each byte-pinned: builtin, `Lib.f`, a field, a case, a type
  name, `_`, keyword target, uppercase target, reserved target,
  collision (new name bound in env; new name used inside the scope;
  new name bound in an inner scope), a partial statement in scope,
  init-marked file.
- Round trip (Fuzz): for every corpus script and every value binder,
  rename to a fresh name, `analyzeLines` the result → the same
  diagnostics modulo the name, and `occurrences` of the new name equals
  the old set shifted. The oracle for "no occurrence missed".
- Wire: prepareRename → `{range, placeholder}`; rename → `WorkspaceEdit`
  with the document's URI; a refused prepareRename → a JSON-RPC error
  with the reason; rename after a refused prepare also errors.

## Perf

- The 20k-line file: one cached analysis per (file, text)
  [D:lsp-analysis-cache]; references = cache hit + ONE `occurrences`
  walk over every statement (the cost class of `semanticTokensFor`,
  which already walks every statement per request); rename = that walk
  + one collision walk + edit construction; prepareRename + rename =
  two requests, both cache hits. No per-occurrence re-analysis, no
  per-request re-check. Budget: references ≤ the hover bound on the
  same file (the cache decision's ~0.1s on JIT); lsp-e2e gains a
  generous tripwire in the recheck-latency style (a generated 20k-line
  doc, references under 2s AOT).
- Recovery: one extra `typecheck` per erroring statement per cache
  miss, under `workBudget`; a file with N broken statements pays N
  extra statement checks once per edit. Measured in the session; if a
  pathological statement doubles the recheck latency, the recovery
  pass gets its own smaller budget before shipping.
- Applying a rename is a didChange: full re-analysis, as any edit.

## Risks

1. **A missed occurrence is a silent wrong rename** — the one failure
   that matters. Mitigation: the round-trip Fuzz property over the
   corpus, the per-form pins, and refusal 2 (partials in scope).
2. **Three scope walks drift** (`localDef`, `bindingUsage`,
   `occurrences`). Mitigation: `localDef` consumes `occurrences`;
   `bindingUsage`'s free set and `occurrences`'s `TopLevel` set are
   pinned equal on the corpus.
3. **TypedKind shape change churn (P0)** across Eval/Purity/Can/Script
   matchers — mechanical, FS0025 lists every site.
4. **Recovered types are more general than the fixed code's** (a
   hole-bound name unifies with anything, so a use three lines below
   may hover `'a`). Honest for a broken statement; hole-descended vars
   hover null; documented in editors.md's one line about errors.
5. **Partials leaking into a diag consumer** — prevented by the type
   (fifth product), pinned by the parity probe.
6. **Conservative collision rule refuses legal renames** — the message
   names the colliding site; precise capture analysis is a follow-up
   if it bites.
7. **Column convention** — edits inherit the server's existing
   character-column convention (hover/definition already use it;
   identifiers are ASCII by `isIdentStart`).

## Non-goals

- Expression-level holes; recovering a one-expression statement.
- Publishing the recovery pass's further errors (R1 recommends not).
- Cross-module rename, workspace-wide references, renaming in unopened
  files.
- Rename of record fields, union cases, type names, `#sig` flags,
  module aliases; references on them.
- Text-based rename (comments, docs, strings).
- Any runner/REPL/`weir check` behaviour change; any grammar or
  editor-extension change.

## Rulings

All eight ruled as recommended (user, 2026-10-26): R1 first error only, everywhere; R2 references/highlight list this file's uses, rename refused; R3 exported members rename single-file; R4 conservative collision rule; R5 rename refuses under partials, references answer what is visible; R6 init-marked and sig files refuse rename; R7 comments and docs untouched; R8 `TEHole`.

- **R1 — the recovery pass's further errors.** Inference continues past
  the first inner error and may find more. Publish them in the LSP (as
  extra diagnostics, never in `weir check`), or report only the first
  everywhere? *Recommend: first only, everywhere* — the one-pipeline
  parity pin stays byte-exact, and later inner errors are often echoes
  of the hole ("one real error beats N echoes", [PLAN-diagnostics-arc
  B6]).
- **R2 — references/highlight on names with no binder in the file**
  (builtins, `Lib.f`, imported members): list this file's uses, or
  null? *Recommend: list the uses* — cheap (the same walk, keyed by
  name), useful ("where do I call `Seq.map`"), and highlight on a
  builtin is what every editor does; rename stays refused.
- **R3 — renaming an exported module member** (a top-level `let` in a
  `module` file, signed or not) as a single-file edit: allow, or refuse
  until cross-module rename lands? *Recommend: allow* — the module's
  author is the one renaming, the sig line renames with it, and open
  importers show the breakage as diagnostics on their next refresh.
- **R4 — collision rule strictness.** Conservative (any occurrence of
  the new name inside the scope extent refuses) vs precise (only
  occurrences that would actually be captured or capture). *Recommend:
  conservative in v1* — a refusal with a named site is recoverable; a
  capture is not.
- **R5 — partials in scope.** Rename when a statement in the binder's
  scope extent failed to check: refuse, or rename the visible
  occurrences? And references in the same state: answer what is
  visible, or refuse? *Recommend: rename refuses, references answer
  what is visible* — rename must be complete or not happen; references
  are a reading aid.
- **R6 — init-marked and sig contract files.** Refuse rename there in
  v1? *Recommend: refuse* — `#session` rewrites bind synthetic lets, and a
  sig file's members are a contract the loader reads by name.
- **R7 — comments and `///` docs.** Should rename also rewrite the name
  where it appears in comments/docs? *Recommend: no* — rename is
  semantic; a text sweep is the editor's search-and-replace.
- **R8 — the hole node.** A new `TEHole of Span` TypedKind case (every
  exhaustive matcher gains an unreachable arm) vs a `TEUnit`-kinded
  node plus a side list of hole spans. *Recommend: `TEHole`* — the tree
  says what it is, the LSP's null gate is one arm, and FS0025 proves
  every consumer saw it.
