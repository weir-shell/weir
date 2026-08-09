# weir — editor coloring: five highlighting gaps

Status: BLESSED (user 2026-07-27), EXECUTING. RULINGS: D1 palette as
proposed + check micro for module/member; D2 issue 4 = STATED FINDING
(undecidable at the grammar layer — needs the resolver); D3 issue 6 =
the (a) type-param token, but FIRST check if single quotes are
command-mode only (scope it → steal≈0), all THREE grammars (Zed +
TextMate + micro, micro first as SPEC). Six highlighting issues across
Zed (tree-sitter), VS Code (TextMate + LSP semantic tokens), and micro
(the .yaml spec).

## The two highlighting paths (context)

- **Zed / Helix**: the tree-sitter grammar
  (`editors/tree-sitter-weir/grammar.js`) + `queries/highlights.scm`.
  DELIBERATELY LOOSE and token-based (a `stray` fallback so nothing
  becomes ERROR) — attributes and sigils are ATOMIC tokens; everything
  else is flat siblings. No semantic/resolver info.
- **VS Code**: the TextMate grammar
  (`editors/vscode/syntaxes/weir.tmLanguage.json`) PLUS LSP semantic
  tokens (legend: weirCommandHead, weirArgv, weirSplice). The
  semantic layer is why VS Code colors command heads and env sigils
  that tree-sitter cannot see.
- Standing discipline: the editors were "verified in a container"
  (tree-sitter CLI highlight; the TextMate engine, vscode-textmate +
  oniguruma). Every fix here is verified the same way, not by eye. The
  TextMate maintenance rule (micro `.yaml` is the SPEC; a rule in one
  grammar only is DRIFT) and the inventory e2e stay honored.

## The five, diagnosed

| # | issue | editor | root | fix |
|---|---|---|---|---|
| 1 | attribute literals share the attribute's color | Zed | `attribute` is one atomic `token([<…>])` | GRAMMAR: split into name + arg nodes; regen; scm |
| 2 | record field types share the binder-name color | Zed + VS Code | field type is a bare `identifier` after `:` (flat siblings) | Zed: QUERY (anchor on `:` → @type). VS Code: TextMate scope |
| 3 | module names & members share a color | VS Code | TextMate paints `Seq` and `.head` the same | TextMate: a member scope (Zed already differs — `Seq`=@type, member=plain) |
| 4 | command heads uncolored | Zed | `az login` is flat `identifier identifier`; no head node, and tree-sitter has no resolver | THE HARD ONE — see decision D2 |
| 5 | env sigils plain | Zed | `$e(` is one atomic `sigil` token, already `@special` | needs a highlight-output check — capture-name/theme, or split the env name out |
| 6 | type params `'a` painted as STRING (Zed to next `'`, VS Code to EOF) | Zed + VS Code | `'` opens a single-quote string in BOTH — tree-sitter `raw_string` `'…'`; TextMate command-single-quote on space-preceded `of 'a` | GRAMMAR (both) — a type-param token that beats the string opener; see D3 |

## OPEN DECISIONS (for the user/advisor)

### D1. capture-name / scope choices
Each new element needs a capture (Zed) and a scope (VS Code). Proposed,
all standard so themes actually color them:
- field type → `@type` / `entity.name.type.weir`
- attribute arg literal → `@string`/`@constant.numeric` per kind /
  `string`/`constant.numeric` inside `meta.attribute`
- module member → (Zed already fine) / `variable.other.member.weir` vs
  the module's `entity.name.type` in VS Code
Confirm or adjust the palette; these are the names, not colors.

### D2. issue 4 — command heads in Zed: heuristic, or stated finding?
The honest constraint: VS Code colors `az`/`bicep` via LSP SEMANTIC
TOKENS (the checker's resolver knows they're external). tree-sitter
has NO resolver — it cannot know `az` is a command and `f` is an
application. Options, none free:
- (a) a GRAMMAR HEURISTIC — a "command line" rule (an identifier at
  statement head followed by bareword args), colored `@function`. Risk:
  the loose grammar can't cleanly separate `az login` (command) from
  `f x` (application) without context; false positives on ordinary
  applications are the failure mode. Prototype and MEASURE before
  committing — a heuristic that miscolors applications is worse than
  nothing.
- (b) STATED FINDING — Zed's tree-sitter path structurally cannot match
  VS Code here without semantic tokens (which Zed's LSP support could
  provide someday — a separate, larger effort). Record it with the
  reason.
RECOMMEND deciding after the (a) prototype's false-positive rate is
measured. Lead with a stated finding if the heuristic paints
applications.

### D3. issue 6 — type-param `'a` vs single-quote string `'x'`
The hard disambiguation: a type var `'a` and a single-char raw string
`'x'` differ ONLY by a closing quote, and the string rule greedily
eats to the next `'` (Zed) or EOF (VS Code). Mature grammars (Rust:
lifetime vs char) use an EXTERNAL SCANNER for exactly this. Options:
- (a) a higher-precedence `type_param` token (`'` + lowercase ident) —
  fixes `'a`/`'b` cleanly, but STEALS short raw strings like `'x'`
  (they'd tokenize as a type param + a dangling `'`). Weir's single
  quotes are COMMAND-mode and usually longer/spaced (`'some text'`), so
  the stolen case is rare — but it is a real tradeoff, not free.
- (b) an external scanner (tree-sitter C scanner) that peeks for the
  closing `'` — the clean solution, but adds a scanner to a grammar
  that has none (build + maintenance cost).
- (c) scope `type_param` to type contexts only (after `<`/`,`/type
  names) — reduces false steals, but the loose flat grammar makes
  "type context" hard to express.
RECOMMEND (a) as the pragmatic fix with the tradeoff MEASURED against a
corpus (how many real `'x'` raw strings break), escalating to (b) only
if the steal rate is unacceptable. State the tradeoff in the report.

## Grammar-change risk (issues 1, 5 & 6)

Splitting `attribute` and `sigil` from atomic tokens into structured
rules is the session's real risk: the grammar's looseness is
load-bearing (the `stray`/`prec(-2)` fallback keeps malformed command
lines from becoming ERROR). Each split must preserve that — a
malformed `[<…` or `$x(` must still degrade gracefully, not ERROR the
whole file. Pin: a corpus of real + malformed snippets parses with NO
ERROR node before AND after (the fuzzer-of-the-grammar discipline).

## EXECUTION LOG (2026-07-27)

INCREMENT 1 (query/scope fixes — DONE, tool-verified):
- **Issue 5** — RECLASSIFIED to a capture-name fix (no grammar change):
  `$e(` was already captured, but as `@special`, which Zed's theme does
  not map — renamed the sigil family (`sigil`/`splat`/`splice`/
  `bang_sigil`/`district_marker`) to `@punctuation.special` (a themed
  capture). Verified via `tree-sitter query`.
- **Issue 2** — DONE all three: Zed via a `:`-anchored query
  (`(identifier)` after `:` → `@type`, verified — `string`/`bool` now
  capture as type); TextMate + micro via a `field-type` rule
  (`: <lowercase>` → the type scope). Inventory drift check green.
- **Issue 4** — STATED FINDING (D2), recorded below. No heuristic
  shipped: it would miscolor every user-defined function call at
  statement head, the exact shadowed-binding case semantic tokens exist
  to get right — undecidable at the static-grammar layer.

FINDING [issue 4, command heads in Zed]: Zed's tree-sitter path has no
resolver, and command-vs-application is a RESOLVER question in weir
(bindings-beat-PATH: `cat x` is a command or an application by scope).
A context-free grammar cannot approximate an undecidable-at-its-layer
distinction without miscoloring the interesting case. VS Code colors
command heads via LSP semantic tokens (weirCommandHead) precisely
because the checker's resolver knows. The boundary is the same one the
REPL-coloring plan drew (static grammar vs the LSP). Zed gains this
only if/when Zed's LSP path grows semantic-token support — a separate,
larger effort.

INCREMENT 2 (member scope + a bug fix — DONE, tool-verified):
- **Issue 3** — the VS Code symptom was diagnosed with the real
  TextMate engine + the LSP: the grammar ALREADY scoped `Args`/`Cli` as
  `entity.name.type` and emitted NO semantic tokens — "uniform except
  let" was Nord under-distinguishing PLUS members being unscoped. Fix:
  module members / field access (`.member`) now scope
  `variable.other.member` (TextMate + micro) / `@property` (Zed), so
  `Seq.head` is module(type) + member(member). Verified via the
  TextMate engine and `tree-sitter query`.
- **BUG FIX** (caught here): increment 1's micro field-type used
  `(?<=:)` — micro is Go RE2, which has NO lookbehind. Rewrote it
  RE2-clean (`:` included in the type match). micro now lookbehind-free
  (grep-confirmed); inventory synced (23 rules).

INCREMENT 3 — the DIFFICULT items, DONE via PLAN-editor-coloring-hard.md
(blessed + executed 2026-07-27; see its EXECUTION LOG): D-MICRO ruled
(stated-shortfall fork, PROCESS `[D:micro-exempt]`); issue 1 (attribute
split — tree-sitter-only, delimiters-only, `a329acb`); binder names
(TextMate captures + micro-exempt, `ffa81b9`); issue 6 (type-param
external scanner + TextMate lookahead + micro-exempt, `c97809c`). All six
issues now resolved. All tool-verified; inventory 23 rules + 2 stated
micro-exempt; 0 ERROR; full e2e green.

REMAINING (superseded — see the hard-parts plan):
- **Issue 1** — split the atomic `attribute` token into name + arg
  nodes (regen), then color arg literals; the no-ERROR corpus pin.
- **Issue 6** — the type-param token per D3 across all three grammars
  (the hard disambiguation; steal-rate measured).
- **Issue 3** — needs the EXACT VS Code symptom: on paper both TextMate
  and micro already differ (`Seq`=`entity.name.type`, member=plain), so
  what renders "the same" needs a look before the member scope lands.

## Work items

1. Issue 5 first — CHEAPEST diagnosis: run `tree-sitter highlight` on
   `$e(git)` and see the actual color. If it's a capture-name/theme
   miss, fix the scm name (no grammar change) — reclassifies the issue.
2. Query-only wins: issue 2 (Zed `:`-anchored type query); issue 3 +
   issue 2 (VS Code TextMate member + field-type scopes). Verify with
   the TextMate engine and `tree-sitter query`.
3. Grammar changes: issue 1 (attribute arg nodes), issue 5 if it needs
   the env-name split, issue 6 (type-param token per D3, both grammars)
   — regen parser (`tree-sitter generate`, node 22 + CLI 0.25.6
   present), update scm + TextMate, the no-ERROR corpus pin, and issue
   6's steal-rate measurement.
4. Issue 4: the (a) heuristic prototype + false-positive measurement,
   then fix-or-finding per D2.
5. Verification pins wired into the e2e/editor checks; docs/editors.md
   updated per verified result; the TextMate inventory e2e stays green.

## Bars

- Verified, not eyeballed — tree-sitter CLI + the TextMate engine, per
  the standing container-verification discipline.
- No parse regressions: the no-ERROR corpus pin before/after any
  grammar change.
- Both grammars move together where an issue spans them (the drift
  rule); an issue that lands in one editor only says why.
- Message/behavior of the LANGUAGE unchanged — this is editors only.

## Done when

Field types, attribute literals, and module members are distinctly
colored in the editors that own them; env sigils color in Zed; type
params `'a` stop painting as strings in both (with the steal-rate
tradeoff stated); command heads either color in Zed (heuristic proven
not to paint applications) or carry a stated finding with the
semantic-token reason; every fix is tool-verified; no parse
regressions; docs match the verified result.
