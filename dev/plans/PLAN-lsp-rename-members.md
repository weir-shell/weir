# PLAN: rename members — types, record fields and union cases

**Status: BUILT** (2026-10-27, feat/lsp-rename-members) — R1–R4 as recommended; see Rulings and the session report.

## The receipt

Two findings, 2026-10-27, VS Code against v0.0.79, in a script that
declares `type Pkg = { id: string; requestedVersion: string; … }`.

**A. F2 on `Pkg` refuses: "'Pkg' is a type or a union case — only value
bindings rename (fields, cases and type names are wire names)".** By
design per [PLAN-lsp-recovery-rename] (scope table: "record fields,
union cases, type names — rename refused"). The reason given for TYPES
is the thinnest of the three: a type name is not on the wire at all —
its occurrences are all in-file and typed (other declarations, the
adapter slot, generic instances, literal inference), and sig files
declare their own types. The refusal was a v1 scope cut, not a safety
rule.

**B. F2 on `id` says "nothing to rename here — put the cursor on a value
name bound in this file".** Wrong twice: there is a name there, and
nothing says why it refuses. The fallback detects fields and cases by
the word's FIRST LETTER being uppercase, so a lowercase field drops to
the generic arm, and an uppercase field (`Include`, `IsPackable`) gets
"is a type or a union case", which it is not.

**The user's case that reverses the wire argument.** "What if I made a
mistake in the name, had a coding session for 5 minutes, haven't run
it, and realize the name is different in the json/yaml/xml — then what?
I rename all manually?" The wire is the truth; the field name is the
thing you adjust to match it. That is exactly when F2 is wanted, and
"fields are wire names" is the reason to rename, not to refuse. The
v1 worry (a silent wire break) is visible the first time the script
runs, and the user chose the rename in an editor dialog; the refusal
does not prevent the mistake, it makes the user do by hand what the
tool does better.

## Anchors

- `Check.occurrences` (Check.fs, after `bindingUsage`) — the one scope
  walk for VALUE binders [D:lsp-rename]; `Lsp.fileOccurrences` folds it
  across statements into `FileOcc { Key: LocalKey | TopKey; OName;
  OLine; OCol; OStmt; OBinderSite }`; `renameTarget` / `renameFor` /
  `prepareRenameFor` / `occurrencesFor` consume the table.
- `Lsp.definitionTarget` already RESOLVES every member use the editor
  jumps from: `TEField(target, f)` via `target.Ty = TNamed(tn, _)`,
  `TERecord(recName, _)` via `node.Ty`'s env key and `wordAt`, union
  cases via `unionOf env ctor` (expression-position uppercase `TEVar`
  and `PCase` in match arms, `patCaseAt`), `TEFrom`/`TEFromYaml`/
  `TEFromToml`/`TEEnvLoad`/`TEArgsLoad` type names, and the word-level
  fallback for a type name in a declaration [D:lsp-typename]. The
  declaration site is textual: `typeSiteIn` finds a member word after
  the decl's first `=`, the type name before it.
- `Ast.Decl { Name; Params; Body = DRecord fields | DUnion cases; Attrs;
  Validate; Span }` — fields `(name, Ty, attrs)`, cases `(name, Ty
  option, attrs)`; NO spans per member or per type reference (hence the
  textual site search). `Script.KType(decl, validator)` is the checked
  statement; a `validate` block is a typed expr walked like any other.
- `Ast.PRecord` carries `(string * Span)` per field; `TEUpdate(src,
  updates)` carries field PATHS (`string list`) without spans;
  `TEField` carries no span for the field word (the word follows
  `target.Span.End`).
- The casing law: fields admit either case (wire), cases and types are
  uppercase-initial (`checkDecl`); `[D:ambiguous-ctor]`: a case name
  declared by two in-scope unions is a check error at a bare use.
- Row polymorphism: `let label r = r.id` types `r` as a row variable
  unless a call unifies it; after `finalizeExpr` the target's `Ty` is
  `TNamed` only when the record is known.

## Shape

One more occurrence table, beside the value one: **member occurrences**,
keyed `MemberKey of typeName * member option` (`None` = the type name
itself). Built per statement from the typed tree, plus the declaration's
own words, and consumed by references, highlight, prepareRename and
rename exactly as the value table is. Nothing else moves: `weir check`,
the runner, the REPL, hover and definition are untouched.

**Occurrence forms, by key.**

| key | form | how found |
|---|---|---|
| type `T` | the declaration's name | `typeSiteIn stmts T None` (textual, before the first `=`) |
| type `T` | a type reference inside ANY declaration (`seq<T>`, `Option<T>`, `of T`, a generic arg, a function type) | word-bounded search of `T` in each `KType` statement's joined text, SKIPPING a word that is a field (followed by optional space and `:`) or a case (the first word after `=` or `\|`, before `of`/`\|`) — those are members, not references |
| type `T` | adapter slots `from json T` / `from json seq<T>` / `Map<string, T>`, `from jsonl`/`yaml`/`toml` | `TEFrom` (via `keyOf node.Ty`), `TEFromYaml`, `TEFromToml` — the word `T` in the node's span (definitionTarget's `wordAt` test, generalised to a word search in the span) |
| type `T` | `Env.load T` / `Args.load T` | `TEEnvLoad def` / `TEArgsLoad target` — the word in the span |
| type `T` | `to json` on a `T` value | no word; not an occurrence |
| field `T.f` | the declaration's field word | `typeSiteIn stmts T (Some f)` |
| field `T.f` | access `p.f`, `p.a.f` (nested paths), `_.f` | `TEField(target, f)` with `target.Ty = TNamed(T, _)` (after finalize); the word `f` found after `target.Span.End` inside the node's span |
| field `T.f` | literal `{ f = … }` typed `T` | `TERecord(_, fields)` with `node.Ty = TNamed(T, _)`; each field's word found in the literal's span as the word followed by optional space and `=` (never the value side: `{ id = id }` renames the key only — the value is a value occurrence) |
| field `T.f` | update `{ r with f = … }`, nested `{ r with a.f = … }` | `TEUpdate(src, ups)`: the path's record types resolve step-wise from `src.Ty`; the word found in the update's span the same way |
| field `T.f` | pattern `{ f = x }` | `PRecord` field spans, the scrutinee's type resolving `T` |
| field `T.f` | `[<Ord n>]`, `[<Wire>]`, `[<Short>]`, `///` docs | ride with the field (attributes and docs attach by position; nothing to edit) |
| case `U.C` | the declaration's case word | `typeSiteIn stmts U (Some C)` |
| case `U.C` | expression position `C`, `C x` | `TEVar C` uppercase, no `.`, `unionOf env C = Some U` |
| case `U.C` | pattern `\| C ->`, `\| C (a, b) ->`, nested, in `let` patterns and lambdas | `PCase(C, _, None)` reached through `patNameSpans`-style walking of every pattern position (match arms, `TELetPat`, `TELambdaPat`, yaml `for` binders); the scrutinee's or the pattern's type resolves `U` |
| case `U.C` | a qualified `M.C` / pattern `M.C` | an imported case — refused (cross-module) |

**Row-typed accesses (R1).** A `TEField` whose target type is not
`TNamed` after finalize — a row variable (`let label r = r.id`), or an
anonymous record — is NOT an occurrence of any named field. When the
file contains such an access of the SAME field name, rename of `T.f`
REFUSES, naming the first site: "`r.id` at line N is a row-typed access
— it could serve `Pkg` or any record with an `id`; this rename cannot
tell which, so it renames nothing. Annotate the call or rename by hand."
Conservative, recoverable, never wrong — the R4 precedent. An anonymous
record's own field (`{| id = 1 |}.id`, target `TNamed` of an anon key)
is a different type and is simply not an occurrence; it does not refuse.

**Scope extent.** Members are file-wide: the declaration's statement
through the end of the file. R5 applies as written: any error diagnostic
from the declaration's line to the end refuses ("a statement after
'Pkg's declaration has an error at line N — fix it first").

**The new name (per kind).**
- field: an identifier, not a keyword, not `_`, either case (the wire
  may be `Include`); NOT an existing field of `T` by weir name OR by
  wire key (`[<Wire>]` of another field equal to the new name refuses:
  "'kind' is already a wire key of Pkg (the field 'k')"). A
  `[<Wire "…">]` on the renamed field itself keeps the wire key, so the
  wire is untouched (the protocol carries no note; the docs say it).
- case: uppercase-initial identifier; NOT a case of ANY union in
  `env.Types` at the declaration (the ambiguous-ctor law would make the
  new name a check error at every bare use); not a builtin case
  (`Some`/`None`/`Ok`/`Error` — whatever `env.Types` lists as builtin
  unions covers them).
- type: uppercase-initial identifier; NOT in `env.Types` (user or
  builtin — `Completed`, `FileRow`, …), NOT a module name
  (`env.Modules`: `Seq`, `Str`, …, and import aliases), NOT a type
  parameter name.
- collision walk: the new name must not occur as a member word of the
  same kind anywhere in the extent — the value table is a different
  namespace (a field `name` and a value `name` coexist today).

**Edits.** One `TextEdit` per occurrence, sorted, non-overlapping by
construction (a word is one occurrence). A field whose attribute line
or doc sits above it needs nothing. `Args.load` flags and `Env.load`
variable names DERIVE from the field, so they change with it — intended
(the wire argument, applied).

**Refusals that stay, now with the right words.**
- the cursor on a builtin type or its field (`r.exitCode`, `Completed`):
  "'exitCode' is a field of the builtin Completed — nothing in this file
  declares it"
- an imported type/field/case (`M.T`, `p.f` with `p : M.T`): "'f' is a
  field of M.T — rename it in that module"
- a sig file, an init file: as today
- the position is a value: the value path answers (unchanged)
- anything else: "nothing to rename here — put the cursor on a value,
  a type, a field or a case declared in this file"
- a partial statement under the cursor: as today (inHole)

**References / highlight** answer the same table (R2's cheapness
argument): references on `id` lists the declaration and every typed
occurrence; `includeDeclaration` honoured; highlight Write (3) at the
declaration, Read (2) elsewhere. A row-typed access is NOT listed (it is
not an occurrence of the named field), consistent with rename.

## Phases

- **P1 — the member table (Lsp.fs; Check.fs if the pattern walk needs a
  helper).** `memberOccurrences stmts : MemberOcc list` with the forms
  above; `memberTargetAt` = the occurrence under the cursor. Pins: one
  per row of the forms table, each asserting the full (line, col, len)
  set from a small file; the `{ id = id }` key-only pin; the
  nested-path pins (`p.a.f`, `{ r with a.f = … }`); the anonymous-record
  non-occurrence pin.
- **P2 — rename and prepareRename over members.** `renameTarget` tries
  the value table first (unchanged), then the member table; the
  per-kind new-name rules; the row-typed refusal; the extent/R5 check;
  the reworded fallback messages (B above). Pins: the refusals each with
  their reason (field/case/type × existing-name, wire-key, casing,
  keyword, builtin, imported, row-typed, error-in-extent); the
  round-trip property extended: `binderSitesFor` gains the member
  declaration sites, and renaming each to a fresh name re-analyzes to
  the same (empty) diagnostics; a `[<Wire>]`-bearing field renames with
  its attribute line untouched and `to json` output unchanged (an eval
  pin on the renamed text).
- **P3 — references/highlight over members; wire; docs; ledger.** The
  handlers try the member table when the value table has nothing;
  docs/editors.md (the Rename and References bullets); CHANGELOG under
  `## v0.0.80` (Added: rename of types, fields and cases; the wire note;
  the message fix); DECISIONS row `lsp-rename-members` reversing the
  wire-name clause of [D:lsp-rename] and naming R1's refusal; this
  plan's README line; `tools/dead-privates.weir`, `tools/dx-message-census.weir`,
  decision-keys, unit, e2e green.
- **Not here:** cross-module member rename (the importer set, as the
  value plan parks it); rename of `#sig` flags, module aliases, type
  parameters; a precise row-typed analysis (R1 chooses conservative).

## Rulings

All four ruled as recommended (user, 2026-10-27: "plan and execute all
now", after the row-typed hazard and the conservative refusal were put
to them).

- **R1 — row-typed accesses of the renamed field.** Rename them too
  (they might serve another record), leave them (the function breaks
  for this caller), or refuse naming the site? *Recommend: refuse,
  conservative, naming the first site* — the R4 precedent; a refusal is
  recoverable, a wrong edit is not. A later plan may do the precise
  analysis (which records reach the access) when a user hits the
  refusal on a real script.
- **R2 — references and highlight on members.** Add them now (same
  table), or rename only? *Recommend: add* — free, and "where is this
  field read" is the question a wire mismatch asks.
- **R3 — the collision rule for a new field name against wire keys.**
  Check weir names only, or wire keys too? *Recommend: wire keys too* —
  two fields with one wire key is a check error at the declaration
  ([D:wire-keys]); the rename must not manufacture one.
- **R4 — `Args.load` / `Env.load` fields.** Refuse (the flag/variable
  name changes), or rename (that is the point)? *Recommend: rename* —
  the derived name is the field's spelling; the user renaming the field
  is renaming the flag.

## Session report (2026-10-27, feat/lsp-rename-members)

Built as planned, P1–P3 in one pass; [D:lsp-rename-members] is the
ledger row.

- **The table.** `Lsp.memberOccurrences` returns the member table
  (`FileOcc` rows keyed `MemberKey(typeKey, member option)`) and the
  row sites; `fileOccurrences` = value table @ member table, so
  `occurrencesFor`/`referencesFor`/`documentHighlightFor` serve
  members with no handler change (a position is a value's before it is
  a member's). `unionOf` moved out of `definitionTarget` to module
  level — the one piece of resolution both walks share; the rest of
  definitionTarget's arms are per-position lookups and stayed.
- **Textual sites.** A field is the word before `:`, a case the word
  after `|`, `=` or an attribute's `]`, a type reference any other word
  match of a declared type outside string literals (`[<Wire "Pkg">]`
  is masked), outside the type's parameters and not an imported
  `Lib.T`'s member; the search stops at a `validate` keyword. A
  module's `let f : T -> …` signature line is searched the same way
  (not in the forms table; found while pinning modules).
- **Deviations, stated.** (1) The row-typed message says "is row-typed
  … any record with a field `id`" rather than the plan's "is a
  row-typed access … with an `id`" — the article broke on `port`, and
  the same message serves a row-typed record pattern (`fun { id = x }
  -> x`). (2) The type-parameter collision rule was dropped: parameters
  are spelled `'a` and a new type name must be uppercase, so the rule
  could never fire. (3) A `_`-led new name is refused for values only:
  `_id` is a legal field (a wire name — MongoDB's `_id`), so the fuzz
  round trip applies the `_` check to value sites and
  `binderSitesFor` now returns `(line, col, isValue)`. (4) The
  collision walk for a field starts at the record's declaration
  statement, so a field word in a declaration ABOVE it is not a
  collision (the pin says so).
- **Pins.** `lspRenameMemberTests` (9 tests): one per forms-table row,
  `{ id = id }` key-only, nested paths both ways, anonymous
  non-occurrence, the generic record, every refusal with its reason,
  the `[<Wire>]` eval pin (`{"ID":"a"}` before and after), the
  `binderSitesFor` shape; three [D:lsp-rename] pins updated to the new
  behaviour. The fuzz round trip renames member declaration words with
  a case-preserving fresh name. lsp-e2e.py gained member probes
  (prepareRename on an access, the three-site WorkspaceEdit,
  references without the declaration, either-case field, the row-typed
  refusal over the wire).
- **Gates.** unit 2679 green; decision-keys, dead-privates (0),
  dx-message-census (32/41, at baseline) green on the published
  binary; lsp-e2e green; a bash-framed wire probe confirmed the
  WorkspaceEdit covers `type Pkg = { id: string }` / `{ id = "a" }` /
  `p.id`.
