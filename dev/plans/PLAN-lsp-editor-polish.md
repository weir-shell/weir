# PLAN: LSP editor polish — the "sweet" editor session

**Status: DRAFT** (2026-10-26) — awaiting rulings

Sibling: PLAN-lsp-recovery-rename (parallel branch) owns block-level
error recovery, binder spans for every binding form, references,
documentHighlight, prepareRename/rename. This plan does NOT re-plan
those; where a feature here needs them it says so and waits.

## The receipt

`weir lsp` today answers hover, completion (`.`/`-` triggers),
definition, formatting, one code-action family, command-mode semantic
tokens and diagnostics — each a PURE function in src/Weir/Lsp.fs
(`hoverAt`, `definitionTarget`, `semanticTokensFor`) with a thin
handler in `run`, all reading one cached analysis
(`analyzeCached`, [D:lsp-analysis-cache]). What the editor does NOT do
is everything a user of F#/Rust/TS takes for granted and misses in the
first minute: no outline, no folding, no inferred types shown inline,
no "add the missing arms" lightbulb, nothing on a space after
`Seq.fold`, unused binders not faded, an `import` not clickable.

The user asked for the session to feel "sweet". The checker already
knows every fact these features render; this plan is the rendering —
eight features, each the same shape as the existing ones: a pure
function with unit pins, a thin handler, a wire probe in
tests/lsp/lsp-e2e.py, one capability line in `initialize`.

## The shape

One discipline for every feature, lifted from what is there:

- **Pure function first.** `xFor (path) (lines) …` beside `hoverAt`;
  the handler only decodes params, calls it, writes JSON. Unit pins go
  through the function (the `semanticTokenTests` / `lspCrossFileTests`
  idiom, tests/Weir.Tests/Tests.fs ~8790 and ~11607); wire pins through
  lsp-e2e.py (`send`/`read_msg`/`expect`, URI `file:///t.weir`).
- **One analysis.** Every function reads `analyzeCached path lines`
  (Lsp.fs ~186) — `(diags, stmts, env0, lls)` from
  `Script.analyzeLinesAs` (Script.fs ~10569). No request re-analyzes;
  the key is exact text, every document event drops the cache.
- **Physical positions via `Script.translate`.** The checker works on
  joined logical lines (`Script.LogicalLine`: `Text`, `Head`,
  `Segments = (joinedStart, physLine, indent) list`, `Ends`);
  `toLogical` (Lsp.fs ~552) maps physical→logical, `Script.translate ll
  col` (Script.fs ~2614) maps back. The synthetic-span rule of
  [D:semantic-tokens] holds for every new emitter: emit only where the
  logical slice sits VERBATIM at its translated physical home (the
  `emitSpan` check in `semanticTokensFor`, Lsp.fs ~219). Extract it as
  `verbatimAt (raw: string[]) (ll) (startCol) (len) : (int * int) option`
  so hints, links and symbols share one check — and compare against the
  RAW buffer lines, not `effectiveLines`, so an init file's rewritten
  `#session` fields (`let _nv = v`, [D:init-marker]) never show a
  synthetic binder.
- **Partial truth in error state.** A statement that failed to check
  has no `CheckedStatement`; it yields nothing, the rest of the file
  still answers (the tokens rule). Recovery (the sibling plan) widens
  this for free.
- **Never wrong beats sometimes right** ([D:hover-silence],
  [D:lsp-requests]'s definition v1): null over a guess, everywhere.
- **No evaluation, no spawn** ([D:check-lsp-chain]): nothing here runs
  a script or a tool; the VS Code client runs `weir run` in its own
  terminal.

## Features

### 1. Inlay hints

**Protocol.** `inlayHintProvider: { resolveProvider: false }`;
`textDocument/inlayHint` with `range`. No `workspace/inlayHint/refresh`:
the server has no state that changes hints outside document events, and
the client re-requests on change.

**Pure function.**

```fsharp
type InlayHint =
    { Line: int; Col: int           // 1-based physical insertion point
      Label: string                 // ": seq<int>"
      Tooltip: string option        // the full type when Label is truncated
      Kind: int                     // 1 = Type, 2 = Parameter
      PaddingLeft: bool; PaddingRight: bool }

let inlayHintsFor (opts: InlayHintOptions) (path: string) (lines: string list)
                  (fromLine: int) (toLine: int) : InlayHint list
```

`InlayHintOptions = { BindingTypes: bool; ParameterTypes: bool;
PipelineTypes: bool; MaxLabel: int }`, defaults `true, true, false, 40`.
Handler: decode `range`, call, convert to 0-based, write
`{position, label, kind, tooltip, paddingLeft, paddingRight}`.

**Where hints go** (data the checker already has):

| site | source | hint | anchor |
|---|---|---|---|
| top-level value `let x = …` | `KLet(name, scheme, te)`, te not a lambda | `: T` after the binder | `binderCol name ll.Text` (Lsp.fs ~678) → `translate` |
| top-level function `let f x y = …` | `KLet` with te = `TELambda(p, pspan, body)` chain (`lambdaParamNames`, Lsp.fs ~820) | `: dom` after each param (Kind 2); `: ret` after the last param, before `=` (Kind 1) | each `pspan`; `te.Ty` peeled by `TFun` |
| pattern let `let a, b = …` | `KLetPat(pat, schemes, te)` | `: T` after each `PVar` | `patScope` (Lsp.fs ~683) for PSpans; schemes by name |
| inner let | `TELet(name, nameSpan, value, body)` | `: value.Ty` after `nameSpan` | — |
| lambda / `for` / `fun` params | `TELambda(p, pspan, _)` with `te.Ty = TFun(dom, _)` (what `paramTypeAt`, Lsp.fs ~670, already reads) | `: dom` (Kind 2) | `pspan` |
| `validate s =` binder | `KType(decl, Some(binder, te))` | `: payload` | textual `wordFind` after `validate` |
| pipeline stage (opt-in) | `TEPipe(arg, fn)` whose span end translates to a physical line other than `arg`'s | ` : T` at the physical line end, Kind 1, PaddingLeft | `translate` |

**Rules.**

- Never on a signed module member: a `KSig(name, _)` anywhere in
  `stmts` suppresses every hint on `let name …` (the signature is the
  annotation, [D:module-signatures]). Never on `()` params, `_`
  binders, `TELambdaPat`/`TELetPat` (no per-binder types until the
  sibling plan's binder spans; match-arm binders likewise — Later).
- Never where the type is not the user's: a `TVar` named `__hole…`
  ([PLAN-diagnostics-arc B6]) or `__…` sentinel → no hint. The label is
  `formatTy` of exactly what hover shows for that binder — pin
  "hint == hover" (invariant 1 below).
- Position: `verbatimAt` on the binder/param word; if the word is not
  verbatim at its translated home (join text, init rewrite) → no hint.
- Long types: `Label` is cut at `MaxLabel` chars with a trailing `…`
  and the full `formatTy` rides in `Tooltip` (anonymous records
  `{| ip: string; n: int |}` and `Result<seq<FileRow>, string>` are the
  customers); untruncated labels carry no tooltip.
- Range: `stmts |> List.filter (fun (ll, _) -> overlaps ll fromLine toLine)`
  BEFORE walking — per visible range on a 20k-line file (the cache
  already made the analysis free; the walk is now proportional to the
  viewport). `overlaps` reads `ll.Head` and the last `Segments` line.
- Pipeline stage hints are off by default: in a long `|>` chain every
  stage's type is interesting once and noise forever; opt-in via
  `weir.inlayHints.pipelineTypes`, and only for multi-line chains (a
  single-line pipe gets nothing — the hint would land mid-line).

**Settings transport.** VS Code sends
`initializationOptions: { inlayHints: {...} }` at `initialize` and the
whole `weir` section on `workspace/didChangeConfiguration`
(`synchronize.configurationSection: "weir"` in extension.ts). The
server keeps one mutable `InlayHintOptions` (parsed by a pure
`inlayOptionsOf (json: JsonElement) : InlayHintOptions`); editors that
send nothing get the defaults. The client never filters hints itself.

**Pins.** Unit: `let x = [1]` → one hint `(1, 6, ": seq<int>", Kind 1)`;
`let f x y = x + y` → two Kind-2 hints and one return hint; inner let +
`fun r ->` over `ls` → `: FileRow`; a signed member in a module → no
hints; a broken statement → none, its neighbours still hint; range
filter excludes a statement outside; a 60-char anonymous record →
truncated label + tooltip equal to `formatTy`; a multi-line `let x =`
with the RHS on the next line → hint on the binder's physical line;
hint label == hover text at the same position (invariant). Wire:
capability present; one request with a range → labels and 0-based
positions; the init.weir probe (POSIX only, as today) asserts NO hints
on `#session` field lines.

**Effort.** ~1.5 days (server 1, client settings + pins 0.5).

### 2. Document symbols

**Protocol.** `documentSymbolProvider: true`;
`textDocument/documentSymbol` → hierarchical `DocumentSymbol[]` when
the client declared `hierarchicalDocumentSymbolSupport` (VS Code,
Neovim, Helix, Zed all do), else the flat `SymbolInformation[]`
(flatten children, `containerName` = parent).

**Pure function.**

```fsharp
type DocSymbol =
    { Name: string; Detail: string option; Kind: int
      Range: int * int * int * int           // 1-based physical (l1, c1, l2, c2)
      Selection: int * int * int * int
      Children: DocSymbol list }

let documentSymbolsFor (path: string) (lines: string list) : DocSymbol list
```

**Mapping** (LSP `SymbolKind` numbers):

| statement | kind | name / detail | children |
|---|---|---|---|
| `KModule(name, kwSpan)` | Module 2 | name or `module`; detail "module header" | none |
| `KImport lm` | Module 2 | `lm.Alias`; detail = the path as written (quoted string in `ll.Text`) | none |
| `#sig tool [path]` (not a statement — `Script.sigInfosForFile path lines`: `SigInfo.Tool`, `DeclLine`, `SigPath`) | Interface 11 | tool; detail = sig file relative to the script dir | none |
| `KType(decl, validator)` record (`Ast.DRecord`) | Struct 23 | `decl.Name`; detail = `type` params if any | fields → Field 8, detail `formatTy` |
| `KType` union (`Ast.DUnion`) | Enum 10 | name | cases → EnumMember 22, detail = payload `formatTy` or none; `validate` → Method 6 |
| `KLet(name, scheme, te)` | Function 12 when `scheme.Ty` is `TFun` (or te is a lambda), else Variable 13 | name; detail = `sigOrFlat name (lambdaParamNames te) scheme.Ty` (Lsp.fs ~829 — the hover's own text, so detail == hover first line) | nested `TELet` whose value is a lambda → Function 12 (inner value lets: none) |
| `KLetPat(pat, schemes, te)` | Variable 13, one per name | detail `formatTy` | none |
| `KSig(name, ms)` with an impl `KLet name` later in the file | — | attached: the impl symbol's `Range` starts at the sig line, its `Detail` is the declared `formatTy ms.Ty` | — |
| `KSig` orphan (`missing-implementation`) | Function/Variable by type shape | detail "signature — no implementation" | none |
| `KCmd`, `KExpr` | none | | |
| a FAILED statement whose `ll.Text` starts `let <name>` / `type <Name>` | by head word | no detail (textual fallback until recovery lands) | none |

Ranges: a statement spans `ll.Head` .. last `Segments` physical line
(end col = that line's length); `Selection` is the name via
`binderCol`/`typeSiteIn` (Lsp.fs ~863) + `translate`; fields/cases via
`wordFind` after the first `=` (the `typeSiteIn` member rule).

**Pins.** Unit: one fixture with a module header, an import, `#sig`,
a record, a union with `validate`, a signed function (sig + impl), a
value let, a pattern let, a nested function, a broken `let` — assert
the tree (names, kinds, details, nesting, ranges, the sig attachment,
the fallback symbol). Detail == hover for the lets (invariant 2). Wire:
capability; one request → names and kinds.

**Effort.** ~0.75 day.

### 3. Diagnostic tags + relatedInformation

**Protocol.** No new capability. Read the client's
`textDocument.publishDiagnostics.tagSupport.valueSet` and
`relatedInformation` at `initialize`; emit each only when declared.

**Tags.** Codes today (Script.fs `codeOf` ~5909 by message prefix, plus
the producers at the tail of `analyzeLinesAs` ~11080–11260 and
`sigCmdDiagnostics`/`continuedCommandDiagnostics`/`docMisalignments`/
`schemaDiagnostics`/`effectBeforeArgs`): parse, check, unbound,
undetermined-type, lowercase-binding, discard, non-unit-statement,
refutable-binder, non-exhaustive, unreachable-arm, regex,
regex-binder-count, unsortable-key, not-equatable,
unrenderable-function, unused-binding, re-enumeration (warning),
temp-dir-cleanup (warning), effect-before-args (warning), cmd-not-found
(warning), continued-command (warning), doc-align, imported-here
(note), missing-implementation, doc-on-implementation,
module-declares-only, dup-type, sig, schema, layout,
session-names-declaration.

`DiagnosticTag.Unnecessary (1)` qualifies for exactly two:

- `unused-binding` — but NOT its bare-`_` variant ("a bare '_' binder
  discards its value anonymously", `UnusedTracker.bareMsg`, Script.fs
  ~4478): that binder is not unnecessary, it is misnamed. The plain,
  shadow-rebind and module variants all start with `'{name}'` — a
  structured marker is better than a prefix test (R6).
- `unreachable-arm` — the arm's span.

`Deprecated (2)`: none. The retired-name teachings (`retiredMember`,
Check.fs ~40) are hard errors on removed names, not deprecations.

**relatedInformation** (file, line, col, message) — the sources:

| primary | related | where the data is |
|---|---|---|
| signature mismatch at the impl ("the implementation of 'f' does not match its signature: the signature (line N) declares …", Script.fs ~4093, also `SigPinned`/`SigMerged`) | the signature line: "the signature declares `T`" | `sd.Line`, `sd.Col` (`MemberSig`) at the error site |
| a module-content error (File = module path) | the entry's import line: "imported here" | `StmtDiag.Note` (Script.fs ~4297) — today emitted as a separate `imported-here` note diag |
| `imported-here` note | the module's error site | the primary diag |
| `doc-on-implementation` (at the doc run) | the signature line: "the doc's one home" | `sc.SigLines` in `analyzeLinesAs` ~11119 |
| `unused-binding` shadow variant ("…before being rebound at line N") | the rebinding binder | `UnusedTracker.Binder` has `(l, c, e)` |

Mechanism: `Script.Diagnostic` gains `Related: (string * int * int * string) list`
(R6, together with `Fix`), filled at the producers above; `StmtDiag`
gets a `Related` list beside `Note`. The LSP publish writes
`relatedInformation` from it. The `imported-here` note diag KEEPS being
published (R13): `check --json` / the runner / the LSP must stay
position-identical ([D:one-pipeline], the lsp-e2e three-consumer parity
probe), so this is additive only.

Pure surface: `diagnosticTags (d: Script.Diagnostic) : int list` and the
`publishFor` writer (Lsp.fs ~2189) extended; pins through
`Script.analyzeLines` on fixtures (the `Related` content) and
`diagnosticTags` (the matrix incl. the bare-`_` exclusion). Wire:
`tags: [1]` on an unused binder; `relatedInformation` on a sig mismatch
in a module fixture (the `withTree` idiom's lib.weir).

**Effort.** ~1 day (0.5 LSP, 0.5 Script.fs plumbing; commit areas
`parser,lsp`).

### 4. Folding ranges

**Protocol.** `foldingRangeProvider: true`; `textDocument/foldingRange`
→ `{startLine, endLine, kind?}` (0-based, whole lines; no
`startCharacter`/`endCharacter`).

**Pure function.** `foldingRangesFor (lines: string list) : (int * int * string option) list`
— 1-based `(start, end, kind)`, kind ∈ `comment | imports | None`.
Reads the 4th analysis component `lls` (every ASSEMBLED logical line,
checked or not — folding needs no types) plus the raw lines.

**Rules.**

1. **Statement fold** — a `LogicalLine` whose `Segments` touch ≥2
   physical lines folds `Head .. lastSegLine` (trailing blank lines
   excluded). Covers let bodies, pipelines, `match`, `if/else`,
   `within`, `for`, `type` declarations, yaml/heredoc districts (the
   district body is joined into the statement's segments — verify;
   fallback is rule 2 over the raw lines).
2. **Inner blocks by indentation** — inside each statement's physical
   range, a line with indent `i` followed by a line with indent `> i`
   folds to the last line with indent `> i`. Gives `then`/`else`
   bodies, `fun x ->` bodies, `always`/`until` sections, nested yaml
   maps.
3. **Match arms** — a line whose code starts with `|` (arms sit at the
   `match`'s indent under `weir fmt`, so rule 2 cannot separate them)
   folds to the line before the next `|` at the same indent or the
   block end, when that is ≥1 line below.
4. **Comment runs** — ≥2 consecutive `///` lines, or ≥2 consecutive
   `//`-only lines (`Script.classifyLine` = `CommentOnly`), kind
   `comment`; the two kinds never merge. Lines under
   `Script.districtContentMask` are bytes, never comments
   ([D:content-bytes]).
5. **Imports** — ≥2 consecutive `import` statements, kind `imports`;
   the `#sig` head run (`SigDecl.Line`s) likewise.
6. **Assembly failure** (no `lls`) — rules 2–5 over the raw lines
   only, so a broken buffer still folds.

No `#region` — weir has none; nothing to invent.

**Pins.** Unit fixture: a multi-line let with a match (statement +
arm folds), `///` and `//` runs (two comment folds, not merged), three
imports (one `imports` fold), a yaml district (one fold), a heredoc
containing a `//` line (no comment fold inside), a mis-indented file
that fails assembly (indent folds still). Wire: capability + request →
the expected `(start, end, kind)` triples present; latency `< 500ms`
on the FIXTURE (the tokens idiom).

**Effort.** ~0.5 day.

### 5. Document links

**Protocol.** `documentLinkProvider: { resolveProvider: false }`;
`textDocument/documentLink` → `{range, target, tooltip}`.

**Pure function.** `documentLinksFor (path) (lines) : (int * int * int * string * string) list`
— `(line, startCol, len, targetPath, tooltip)`, 1-based; the handler
maps `targetPath` to a URI through the client's own spelling when the
target is an open doc, else `pathToUri` ([D:lsp-uri-spelling], exactly
as the definition handler does, Lsp.fs ~2603).

**Links** — only RESOLVED targets (a link to a missing file is a
lie; the diagnostic already says what is wrong):

| text | target | resolution reused |
|---|---|---|
| `import "./x.weir"` / `import "weir:name"` | `lm.AbsPath` | `KImport lm` exists only when resolution succeeded; range = the quoted path (the `q1/q2` scan of `definitionTarget`'s import arm, Lsp.fs ~1170) → `translate` |
| `schema=<name>` on a yaml marker | the vendored file | `Script.resolveSchemaFile path name` (Script.fs ~6043) `Ok(_, file)`; range = the name after `schema=` (`onSchemaToken`'s index, Lsp.fs ~1054) |
| `#sig tool` / `#sig tool "path"` | `SigInfo.SigPath` | `Script.sigInfosForFile` (loads via `resolveSigFile`, ~6427); range = the tool word (and the quoted override) on `DeclLine` |
| `File.read "literal"` and other path literals | — | NO: a path literal is relative to the run's cwd / `within cd`, so the link would be wrong half the time |
| URLs in strings/comments | — | NO: editors link `http://` text themselves |

**Pins.** Unit via the `withTree` temp-dir idiom: an import → lib
path; `#sig mytool` → the sig path; `schema=` with
`.weir/schemas/x.json` present → that file; a missing import → no
link; a `schema=` not vendored → no link. Wire: capability + one
request → `target` ends with `lib.weir`.

**Effort.** ~0.5 day.

### 6. Signature help

**Protocol.** `signatureHelpProvider: { triggerCharacters: ["(", " "], retriggerCharacters: [" "] }`
(R11); `textDocument/signatureHelp` → `{signatures: [{label,
documentation, parameters: [{label: [start, end], documentation}]}],
activeSignature: 0, activeParameter}`. Always one signature.

**Pure function.**

```fsharp
type SigHelp =
    { Label: string                               // "Seq.fold (f: 'a -> 'b -> 'a) (init: 'a) (xs: seq<'b>) : 'a"
      Doc: string option                          // reflowDoc of the /// or builtin doc
      Params: (int * int * string option) list    // label offsets + per-param doc
      Active: int option }

let signatureHelpAt (path: string) (lines: string list) (line: int) (col: int) : SigHelp option
```

**Expression application.** Signature help fires mid-typing, when the
statement is usually BROKEN — so the typed tree is the bonus, not the
base:

1. Locate the statement text up to the cursor: `logicalAt allLls`
   (Lsp.fs ~541) when assembly holds, else the completion handler's
   "assemble from the nearest column-0 head" fallback (Lsp.fs
   ~2757–2779) — extract it as `statementUpTo (lines) (line) (col)`
   shared by completion and signature help.
2. Gate: `inStringOrComment` → None (a space inside a string must not
   pop anything).
3. Textual spine: scan left from the cursor at the current paren depth
   (quotes and parens are one atom, the `SigComplete.tokens` rule)
   to the head word of the innermost application; the atoms between
   head and cursor are the supplied args. Flat calls only — the stated
   subset of [D:path-param-completion]; a head that is an operator,
   keyword or form word → None.
4. Resolve the head: the env in scope (the last statement above the
   line, as completion does) — `env.Values` / `env.Modules` scheme for
   the type; parameter names from `Builtins.builtinDocs[name].Params`
   (302 members carry names, [D:annotated-signature]) for builtins,
   `lambdaParamNames te` from the top-level `KLet` (via `letSiteIn`,
   Lsp.fs ~887) or the imported module's `lm.Body` `CLet` (the hover's
   module-member arm, Lsp.fs ~1851) for user functions; a union case
   → one param, the payload. Doc: `///` via `Script.docAttachments` at
   the declaration (position-keyed, as hover) or `renderBuiltinDoc`,
   through `reflowDoc` ([D:hover-reflow]).
5. Label: `formatSignature name params ty` — the hover's text, built
   piecewise so each `(p: t)` group's `[start, end]` is exact (a
   `formatSignatureParts` beside `formatSignatureWith`, Types.fs ~575,
   returning the pieces; never a second formatter). When the typed tree
   exists for the statement, the instantiated node type replaces the
   scheme (so `Seq.map (f: FileRow -> 'b)` instead of `'a -> 'b`).
6. **Active parameter** in a curried, pipeline-heavy language:
   - `active = supplied` (0-based); the cursor inside an atom → that
     atom's index; `active ≥ n` → None (over-applied, say nothing).
   - **Pipe rule**: when the application is the stage after the last
     depth-0 `|>` left of the cursor (`Complete.lastStage` is the `|`
     scanner; a `|>` twin), the piped value fills the LAST parameter:
     explicit slots are `0 .. n-2`; the last param's doc gets
     "— filled by the piped value", and once all explicit params are
     supplied `active = n-1` points at it.
   - `|` command chaining, `retry`/`poll`/`repeat` keys, record
     literals, lambdas: None in v1 (form keys are a Phase 5 candidate:
     the prelude option records already drive hover,
     [D:hover-residue]).

**Command position** — the sig world. `Complete.commandArgvAt env before`
(Complete.fs ~574) says the cursor is in a command's argv and gives
`(head, argText)`; `Script.sigInfosForFile path lines` finds the
`SigInfo`; `SigComplete.split argText` (Script.fs ~7763) gives the words
so far; `Script.sigSurfaceAt si key` (~6806) the case's surface (the
case key from the words, the checker's own rule); each flag's field,
type and `///` doc via `sigFlagField` (Lsp.fs ~1002) and the hover's
flag-doc read (~1900) — extract `sigFlagDoc` so hover and signature
help share it. Shape (R10): the signature is the TYPED LINE normalised
— `kubectl get --namespace <string> --output <string>` — with one
parameter per flag present (plus the one being typed), its doc the
field's `///`; `active` = the flag under the cursor or whose value is
being typed. Discovery of all flags stays with completion
([D:sig-arg-complete]); this answers "what does THIS flag take and
mean" without a 50-flag label. Unsigned head, builtin head (`cd`),
dynamic head → None. No spawn, ever ([D:sig-dynamic-complete] is the
REPL's).

**Pins.** Unit: `let r = Seq.fold ` → label with three named params,
`Active = Some 0`; `… Seq.fold f 0 ` → 2; `xs |> Seq.fold f ` → 1 and
the last param's doc marks the pipe; `xs |> Seq.fold f 0 ` → 2;
`let add a b = a + b` then `add 1 ` → label from `lambdaParamNames`
with the `///` doc, active 1; `print (Str.replace "a" ` → `Str.replace`
active 1 (depth scan); inside a string → None; the `withTree` sig
fixture: `mytool --dry-run ` → one param with doc "run without side
effects" (the tool is not installed — no spawn); unsigned head → None.
Wire: capability with both triggers; one request → `signatures[0].label`
contains `Seq.fold`, `activeParameter` right.

**Effort.** ~2 days.

### 7. Quick-fix coverage

**Inventory.** One family: bare-name qualification — `Qualify: Seq.where`
(quickfix) and `Qualify all bare names in file` (`source.fixAll`),
produced INLINE in the handler (Lsp.fs ~2469–2565) by a regex over
`lastPublished` messages ("is a bare module member — spell it" / "is
module-qualified; use", [D:bare-partition]). No pure function, no unit
pin, no lsp-e2e probe (zero `codeAction` in the harness).

**Structured, not regex (R6).** Nothing structured exists: `codeOf`
itself prefix-matches messages ("structured codes at error origin are
the parked upgrade", Script.fs ~5897), and Check.fs's error channel is
`TypeError = { Span; Message; Origin }` (Check.fs 9) with the suggestion
baked into `Message` by `Types.didYouMean` (Types.fs ~1048, one
function, one format: `. Did you mean 'X'?`). The shape:

```fsharp
// Script.fs, beside Diagnostic
type FixEdit = { Line: int; Col: int; EndLine: int; EndCol: int; NewText: string }  // 1-based, in Diagnostic.File
type Fix = { Title: string; Edits: FixEdit list; Family: string option }           // Family: source.fixAll grouping key

type Diagnostic = { …; Related: (string * int * int * string) list; Fix: Fix option }
```

`Fix` is computed at the ONE conversion point in Script.fs (`fixOf`
beside `codeOf`) from `(Code, Message, span)`, with the message
inverses living beside their producers (`Types.didYouMeanTarget msg :
string option` next to `didYouMean`; the qualify regex moves next to the
bare-partition messages). Producers that live in Script.fs
(`UnusedTracker`, the sig rules in `analyzeLinesAs`, `discardError`)
attach `Fix` directly. Threading structure through `TypeError` is the
parked upgrade, still parked — but `fixOf` is the one place that would
read it when it lands. `check --json` writes `fix` when present
(additive; `writeDiag`, Script.fs ~5950) — the agent-facing diagnostics
core ([D:check-lsp-chain]) gains machine-applicable fixes for free.

**The LSP side.** `codeActionsFor (uri) (diags: Script.Diagnostic list) (range: (int * int) option) : CodeAction list`
— pure: a quickfix per diagnostic with a `Fix` in range (carrying the
diagnostic in `diagnostics`), plus one `source.fixAll` per `Family`
present (R8). The handler keeps reading `lastPublished` (no
re-analysis) and serialises. Qualify migrates into this with no
behaviour change.

**The batch** (code → trigger → edit):

| fix | code / message | edit | needs |
|---|---|---|---|
| Replace with 'X' | any message carrying `. Did you mean 'X'?` — unbound variable, no field, no case, no module/member, record update, unknown attribute (Check.fs ~7979), unknown flag (`dym`, Script.fs ~6955 — its own helper, same `Did you mean` tail) | replace `d.Line,d.Col .. d.EndLine,d.EndCol` with X | `EndCol` present (`StmtDiag.PhysEnd`); no span → no fix |
| Qualify / Qualify all | today's | today's | migration only |
| Add missing arms: A, B | `non-exhaustive` "match is not exhaustive; missing: A, B" (`missingCases`, Check.fs ~3229) | after the match's end (the diag's `EndLine`): multi-line → one `\|  A -> fail "todo"` line per case at the last arm's indent; single-line → appended ` \| A -> fail "todo"` | R7 (placeholder) |
| Add else | "an if without an else is unit-valued; this then-branch is T — add an else" (Check.fs ~6158) | insert `else fail "todo"` after the then-branch (own line at the `if`'s indent when multi-line) | R7 |
| Rename to '_x' | `unused-binding`, all `'{name}'` variants (span = the binder) | insert `_` at `(Line, Col)` | — (never in fixAll) |
| Rename to 'foo' | `lowercase-binding` (`let Foo = 1`) | lowercase the binder's first letter — binder only | — |
| Align doc comment | `doc-align` | replace the `///` line's leading whitespace with the declaration's | — |
| Move doc to the signature | `doc-on-implementation` | two edits: delete the doc lines at the impl, insert them above `sc.SigLines[name]` | `Fix` attached in `analyzeLinesAs` (~11119) |
| Call it: `f ()` | NEW teaching: `discardError` (Script.fs ~3229) on `TFun(TUnit, _)` today says "computes a unit -> unit and discards it — bind it, or pipe it to print" — the wrong lesson for an uncalled thunk (the REPL echoes a function as mini-help, [D:repl-fn-echo]; the editor should offer the call) | append ` ()` at the statement's span end | R9 |
| Unwrap with `\|> orFail` | NEW teaching on `mismatch` (Check.fs ~279): `expected T, got Option<T>` / `Result<T, _>` → "— `\|> orFail` unwraps it (raising on None/Error)" | append ` \|> orFail` to the offending span | R9; the fix rides `fixOf` on the new message |
| Add a stub implementation | `missing-implementation` | insert `let name = fail "todo"` under the sig | R7 (nice-to-have) |

Not now: `re-enumeration` (`|> Seq.freeze` at the LET — the diag sits on
the second pull and does not carry the let site), export-with-signature
for a module's unused member (needs the scheme), retired-member
renames (the message formats vary per entry; wants the `TypeError`
upgrade), `temp-dir-cleanup`/`effect-before-args`/`continued-command`
(structural rewrites). All in Later.

**Pins.** Unit, per fix: `Script.analyzeLines` on a fixture → the exact
`Fix` edits; `Lsp.codeActionsFor` → titles, kinds, range filter, fixAll
grouping; and the invariant "a fix fixes" — apply the edits to the
fixture text, re-analyze, the diagnostic with that code is gone (for
`fail "todo"` placeholders: no NEW error either). Parity: `check --json`'s
`fix` on a fixture equals the LSP's edit. Wire: one `codeAction`
request → titles; one apply-via-`didChange` round trip → the publish
clears.

**Effort.** ~2.5 days (0.5 shape + migration, 1.5 the batch, 0.5 the
two teachings + json + parity pin). Commit areas: `parser,checker,lsp`.

### 8. Code lens — recommend commands and buttons instead

A lens is a server-emitted line above code whose `command` only a
client that REGISTERED that command can run. `run`/`check`/`check --can`
lenses would (a) sit above line 1, competing with the shebang and
shifting the text, (b) be dead text in Neovim/Helix/Zed unless gated
on an `initializationOptions.codeLens.commands` flag the VS Code client
sets, and (c) tempt a `workspace/executeCommand` fallback that would
have `weir lsp` run a script — refused: the server never evaluates
([D:check-lsp-chain]) and the formatting row's security non-claim
([D:lsp-requests]) stays exact.

**Recommendation (R12):** no code lens in this plan. The VS Code
client gets three CLIENT commands (`weir.run`, `weir.check`,
`weir.checkCan`) that save the document and send `"<resolvedBinary>"
run|check [--can] "<file>"` to a reused integrated terminal named
`weir` (the binary from `resolveServer`, extension.ts ~16, so the
terminal runs the same weir the server is), surfaced in the command
palette and as two `editor/title` buttons (`$(play)` run, `$(check)`
check) under `when: resourceLangId == weir`. No default keybindings.
Zero server work.

`N references` on top-level lets is a real lens (it opens the
references list, a client-standard command) — it depends on the
sibling plan's references and lands after it, behind
`weir.codeLens.references` (Later).

**Effort.** ~0.5 day, client only (`editors/` is a `docs` commit area).

## Later (non-goals here)

- **Richer semantic tokens**: params/locals (`variable` + `declaration`
  modifier), module aliases (`namespace`), `Secret`-typed values (a
  `weirSecret` type so secrets read differently). Needs the sibling
  plan's binder spans; the legend change is client-visible
  (package.json `semanticTokenTypes`, the Neovim `nvim_set_hl` links in
  docs/editors.md).
- **Workspace symbols** (`workspace/symbol`) — wants an index of
  unopened files; the server is stateless by design beyond open text.
- **Cross-file references / rename** — the sibling plan + an importer
  index.
- Inlay hints on match-arm and pattern binders (pattern typing via the
  sibling plan's spans); signature help for form keys and reifier
  messages.
- `N references` code lens; the `re-enumeration`, export-with-signature
  and retired-member fixes; the `TypeError` structured-fix channel.
- `workspace/didChangeWatchedFiles` so a module edited on disk outside
  the editor refreshes before the next document event.

## Phases

| phase | content | effort |
|---|---|---|
| **P1 — cheap wins** | folding ranges (4), document symbols (2), document links (5), diagnostic tags + related (3), VS Code commands + title buttons (8), `verbatimAt` / `statementUpTo` / `sigFlagDoc` extractions, `initializationOptions` plumbing | ~3 days |
| **P2 — inlay hints** | binder / param / return hints, options, truncation + tooltip, range filtering, init-file guard, settings UI | ~1.5 days |
| **P3 — quick fixes** | `Fix`/`Related` on `Diagnostic`, `fixOf`, `codeActionsFor` (qualify migrates), the batch, the two checker teachings, `check --json` `fix`, the parity pin | ~2.5 days |
| **P4 — signature help** | expression application (textual spine, env, builtin params, `///`), the pipe rule, command position via sigs | ~2 days |
| **P5 — polish + docs** | pipeline-stage hints (opt-in), `validate` binder hints, docs/editors.md + editors/vscode/README.md feature lists, CHANGELOG, DECISIONS rows (`inlay-hints`, `document-symbols`, `diagnostic-tags`, `folding-ranges`, `document-links`, `signature-help`, `structured-fixes`, `no-code-lens`) | ~0.5 day |

≈ 9.5 days. P1 and P2 are independent of P3/P4 and of the sibling plan;
P3's `Related` plumbing and P4 share nothing with it either. Merge
order with the sibling: either first — the only shared file is Lsp.fs's
`initialize` capabilities string and the dispatch `match`, a trivial
rebase.

## Test strategy

Unit pins through the pure functions (fixtures as string lists; the
`withTree` temp-dir idiom for imports/sigs/schemas), wire probes in
lsp-e2e.py (one per method, plus the capabilities object). The
invariants, named:

1. **hint == hover** — an inlay hint's label at a binder equals the
   hover's type there (`formatTy` of one scheme/node).
2. **detail == hover** — a document symbol's detail for a let equals
   the hover's first line (`sigOrFlat`).
3. **partial truth** — a failed statement yields no hints / detail /
   links / signature help; its neighbours still do.
4. **verbatim anchors** — nothing lands on join text or a rewritten init
   line (`verbatimAt` pinned on a multi-line let and the init fixture).
5. **a fix fixes** — apply a `Fix`, re-analyze: the code is gone, no
   new error.
6. **tags and related** — `unused-binding` tagged, the bare-`_` variant
   not; the sig mismatch's related points at the sig line.
7. **range discipline** — inlay hints honour the range; folding and
   symbols cover the whole file.
8. **parity** — `check --json`, the runner and the LSP publish stay
   position-identical ([D:one-pipeline]); `fix` in `--json` equals the
   LSP's edit.
9. **no spawn** — signature help and links on a `#sig`'d tool that is
   not installed still answer (the existing `mytool` fixture).
10. **latency** — folding/hints/symbols on the FIXTURE under the
    `< 500ms` wire bound (the tokens idiom; the JIT-build spelling).

## Perf notes

- Every request hits `analyzeCached` on the same text: zero
  re-analysis per request; new work is a walk over `stmts`/`lls`.
- Inlay hints are per visible range (filter statements by physical
  overlap before walking). Folding, symbols and links walk the whole
  file but do no typing — O(statements).
- `Script.docAttachments lines` (O(lines)) is recomputed per hover
  today; symbols and signature help add callers — make it a `Lazy` in
  the cache entry (the tuple grows by one lazy field; `hoverAt` moves
  onto it). Same for `Script.sigInfosForFile` (reads sig files from
  disk per call; completion already calls it per keystroke).
- Signature help on the space trigger is chatty: the null path
  (`inStringOrComment`, no head) runs before any walk.
- Cross-file reads (`targetStmts`) are cached per module after the
  first request, as for definition.

## Risks

- **Hint noise** — mitigated by defaults (no pipeline hints), the
  signed-member rule, truncation, and the settings; the user can turn
  each class off.
- **Wrong signature mid-typing** — the textual spine misreads nested
  calls and operators; the flat-call subset plus "null over a guess"
  keeps it honest; the typed tree improves it when present.
- **Message-format coupling in `fixOf`** — the same fragility `codeOf`
  has; contained by the inverse living beside each producer and
  invariant 5. The real cure is the `TypeError` upgrade (Later).
- **`Diagnostic` shape change** — additive fields; `--json` consumers
  must tolerate `fix`/`related` (document in docs/tooling.md's `--json`
  section); the parity probe guards position identity.
- **Folding over districts** — assumes district bodies are segments of
  their statement; if not, rule 2 covers them textually. A `//` inside
  heredoc content must not fold (mask).
- **Client capability gaps** — tags/related/hierarchical symbols gated
  on declared capabilities; Neovim/Helix users get defaults for the
  settings (document `init_options` in docs/editors.md).
- **Commit areas** — P3 touches Script.fs/Check.fs/Types.fs (`parser`,
  `checker`) besides `lsp`; `editors/` rides `docs`.

## VS Code client changes (editors/vscode)

package.json `contributes`:

- `configuration`: `weir.inlayHints.bindingTypes` (bool, true),
  `weir.inlayHints.parameterTypes` (bool, true),
  `weir.inlayHints.pipelineTypes` (bool, false),
  `weir.inlayHints.maxLabelLength` (int, 40).
- `commands`: `weir.run` "weir: Run script", `weir.check` "weir: Check
  script", `weir.checkCan` "weir: Check script (--can)".
- `menus.editor/title`: `weir.run` (`$(play)`) and `weir.check`
  (`$(check)`), `when: resourceLangId == weir`, group `navigation`.
- (Later) `weir.codeLens.references`; `semanticTokenTypes` additions.

extension.ts: `initializationOptions: { inlayHints: cfg }` and
`synchronize: { configurationSection: "weir" }` in the client options;
`registerCommand` × 3 with a reused terminal (`weir`), save before run,
the resolved binary path quoted. README feature list; docs/editors.md
(the "In any editor" list gains outline, folding, inlay hints, links,
signature help, quick fixes; a note on `init_options` for Neovim).

## Rulings needed

- **R1 — inlay hint set and defaults.** Binder types, parameter types
  and return types on by default; pipeline-stage types off; no hints at
  all on signed module members. *Recommend as stated.*
- **R2 — long type labels.** Truncate at 40 chars with `…`, full type in
  the hint tooltip; or never truncate. *Recommend: truncate + tooltip.*
- **R3 — settings transport.** `initializationOptions` +
  `workspace/didChangeConfiguration` (server-side options, other editors
  get defaults); or the client filters hints by kind. *Recommend:
  initializationOptions.*
- **R4 — signature lines in the outline.** Attach to the implementation
  (range extends to the sig line, detail = declared type) or own
  symbol. *Recommend: attach; an orphan sig keeps its own symbol.*
- **R5 — outline depth and broken statements.** Nested FUNCTIONS as
  children (inner value lets not); a failed `let`/`type` statement gets
  a textual fallback symbol until recovery lands. *Recommend: both.*
- **R6 — structured `Fix` and `Related` on `Script.Diagnostic`.**
  Computed at the one conversion point in Script.fs, emitted in
  `check --json` when present (additive); versus LSP-only regex
  derivation (today's shape); versus threading through Check.fs's
  `TypeError` now. *Recommend: structured at conversion, in `--json`;
  the `TypeError` channel stays the parked upgrade.*
- **R7 — placeholder body for inserted arms / else / stubs.**
  `fail "todo"` (diverges, so it types in any branch; the todo is
  greppable) or no fix for `else`. *Recommend: `fail "todo"`.*
- **R8 — `source.fixAll` membership.** Qualify + doc-align only; never
  `_`-prefix in bulk (it hides the swallowed-failure receipt of
  [D:unused-bindings]). *Recommend as stated.*
- **R9 — two checker teachings ride this plan.** The `unit -> X`
  statement arm ("call it: `name ()`") and the Option/Result mismatch
  `|> orFail` hint; both are checker/parser-area message changes with a
  `Fix`. *Recommend: yes, both.*
- **R10 — command-position signature help shape.** The typed line
  normalised with one parameter per flag PRESENT (+ the active one),
  docs from the sig's `///`; versus the whole case surface as
  parameters. *Recommend: typed-flags-only.*
- **R11 — signature help triggers.** `(` and ` ` (retrigger on space)
  versus `(` only. *Recommend: both; the null path is cheap.*
- **R12 — code lens.** None in this plan; client commands + editor-title
  buttons for run/check; `N references` deferred behind the sibling
  plan, default off when it lands. *Recommend as stated.*
- **R13 — notes versus relatedInformation.** Keep publishing the
  `imported-here` note diagnostic AND add `relatedInformation` on the
  primary (additive, parity-safe); versus folding the note into the
  primary. *Recommend: additive.*
