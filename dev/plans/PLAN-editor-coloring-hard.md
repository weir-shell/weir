# weir — editor coloring: the hard parts

Status: DONE (blessed 2026-07-27, executed same day). Commits: D-MICRO+C
`ffa81b9`, A `a329acb`, B `c97809c`. Order ran D-MICRO → C → A → B as
blessed. EXECUTION LOG at the foot.

The three items from PLAN-editor-
coloring that need grammar surgery or run into a structural limit,
split out after the easy increments (issues 2/3/4/5) shipped
(`ce64b1a`, `f3a375d`). Editors only.

## The cross-cutting constraint (read first)

**micro is Go RE2 — NO lookbehind, NO lookahead.** The existing micro
grammar avoids them by construction (`(^|[ \t])` alternation instead of
`(?<=…)`). This is the binding limit under all three items below:
type-param scoping, binder-name isolation, and clean field-type
coloring all WANT lookaround. Tree-sitter (queries + optional C
scanner) and TextMate (Oniguruma, full lookaround) can express them;
micro cannot. So each item hits the same fork:

- keep the DRIFT RULE (all three grammars in lockstep, micro is spec) →
  the fix is only as good as RE2 allows in all three; or
- ADMIT a documented micro shortfall (a rule TextMate/tree-sitter do
  richly, micro does coarsely or not at all), amending the drift rule's
  "add to both or neither" with an "or micro-can't, stated" clause.

DECISION D-MICRO (spans all three): which fork? RECOMMEND the second —
micro's LSP-token support is already uninvestigated (it renders lexical
colors only, per its own header), so a stated RE2 shortfall is honest
and keeps the richer editors rich. The inventory e2e would need a
`micro-exempt` allowance for such keys.

## Item A — issue 1: attribute arg literals

`[<Default "prod">]` colors as one blob because the attribute is atomic
in all three: tree-sitter `token(seq('[<', /[^>]*/, '>]'))`; TextMate/
micro a single `[<`…`>]` match. Split so the arg literals color.

- **tree-sitter**: promote `attribute` from a token to a RULE —
  `seq('[<', field('name', $.constructor), repeat($._attr_arg), '>]')`
  with `_attr_arg` = string / number / boolean / `,`. Regen. scm colors
  the args `@string`/`@number`/`@boolean`, the name `@attribute`. RISK:
  the looseness is load-bearing — a malformed `[<…` must degrade, not
  ERROR the file. PIN: the no-ERROR corpus (real + malformed) before/
  after.
- **TextMate/micro**: turn the attribute MATCH into a BEGIN/END region
  (`[<` … `>]`) with inner rules for the literals — RE2-clean (no
  lookaround needed). Both can do this; drift rule holds here.
- SIZE: small-medium. The tree-sitter rule + regen is the real work;
  the no-ERROR pin is the guard.

## Item B — issue 6: type params `'a` painted as string

The hard disambiguation (confirmed: single quotes are COMMAND-mode
only; `'a` is a type var). A type var `'a` vs a single-char raw string
`'x'` differ only by a closing quote; the string rule eats to the next
`'` (Zed) / EOF (VS Code/micro).

- **tree-sitter**: precedence CANNOT fix it (a high-prec `type_param`
  token steals `'echo $PPID'`; prec beats length in the lexer). Clean
  fix = an EXTERNAL C SCANNER (`src/scanner.c`, `externals`) that peeks
  for the closing `'` — the Rust lifetime-vs-char precedent. Honest
  cost: a scanner where there is none (build + maintenance). D3 already
  ruled "escalate to (b) if steal unacceptable"; the steal (common
  `sh -c '…'`) IS unacceptable, so (b) it is — unless a partial
  (constrain raw_string to stop at `>`/`=`, accepting it breaks quoted
  redirects like `'a > b'`) is deemed good-enough. DECIDE: external
  scanner, or the stated partial.
- **TextMate**: Oniguruma lookahead CAN distinguish — a `type_param`
  rule `'[a-z_]\w*` guarded so a closing `'` doesn't follow, ordered
  before the single-quote region. Verify against `sh -c '…'` and `'a'`.
- **micro**: RE2 — NO lookaround. Cannot distinguish. This is D-MICRO's
  sharpest instance: micro either mis-paints type params as strings
  (status quo) or mis-paints short command strings as type vars. A
  stated shortfall (leave micro coarse) is likely the honest call.
- SIZE: large (the scanner). The single most expensive item here.

## Item C — binder-name scoping (`let cli` → variable)

The user asked for it; Zed ALREADY has it (`(let_head name:
(identifier) @variable)`). The gap is VS Code + micro.

- **TextMate**: `(?<=\blet\s)[a-z_]\w*` → `variable` (Oniguruma
  lookbehind) — clean. Also `fun` params, `let`-pattern binders (a
  scope sweep, not just the one form).
- **micro**: RE2 — cannot isolate the name after `let` without
  recoloring `let` itself. D-MICRO again: TextMate-only (stated micro
  shortfall) or skip.
- SIZE: small — but gated on D-MICRO (does a TextMate-only rule get an
  inventory exemption?).

## Bars

- Tool-verified: `tree-sitter query`/`highlight` + the vscode-textmate
  engine (installed at /tmp/tmtest; fold into a repo check).
- No parse regressions: the no-ERROR corpus pin before/after every
  grammar change (item A, and item B if the scanner lands).
- The drift rule holds where RE2 permits; where it does not, D-MICRO's
  stated-shortfall clause governs and the inventory e2e is amended to
  allow it — explicitly, per rule, not silently.
- Language behavior unchanged — editors only.

## Recommended order

C (small, gated on D-MICRO) → A (self-contained grammar change) → B
(the scanner, the big one; do last so the D-MICRO precedent from C is
set). Resolve D-MICRO first — it decides how far A/B/C reach in micro.

## EXECUTION LOG (2026-07-27)

**D-MICRO** (`ffa81b9`) — took the second fork (stated shortfalls),
amending the RULE not just the e2e per the bless. PROCESS.md now carries
`[D:micro-exempt]`: add to both grammars or neither, UNLESS a grammar's
engine cannot express it — then a stated `# micro-exempt: <key>
(<reason>)` in that grammar's header, and the inventory allows it, reason
per-key = documentation. The inventory e2e (`ci/e2e.sh`) parses those
lines: an exempt key present as a `# rule:` too is a lie (redundant), a
tm rule with neither micro rule nor exemption is drift (missing). micro's
header explains the mechanism to a reader.

**Item C — binder names** (`ffa81b9`) — the first real exemption. Zed
already had `(let_head name: (identifier) @variable)`. TextMate got a
`binder` rule via CAPTURES (`\b(let|fun)\s+(name)` → keyword + variable),
NOT lookbehind — so `let cli` colours `cli` apart from `let`. micro
exempt: RE2 has no per-capture-group colours (it paints a whole match one
colour), so it cannot split `let cli`. Verified on the TextMate engine.

**Item A — attribute arg literals** (`a329acb`) — the plan feared a
structured tree-sitter rule (ERROR risk on malformed `[<…`). Reality:
micro AND TextMate already matched only the `[<`/`>]` DELIMITERS (content
coloured normally) — issue 1 was tree-sitter-ONLY, whose `attribute` was
the atomic whole-token. Fix: `token(choice('[<', '>]'))` — delimiters
only, so `[<Default "prod">]` colours `Default`=type, `"prod"`=string by
the normal rules; loose, no closing required → 0 ERROR clean+malformed.
No structured rule, no TextMate/micro change (already correct), no drift.

**Item B — type params `'a`** (`c97809c`) — external C scanner, no
partial, done last, per the bless. `src/scanner.c` emits `type_param` for
`'ident` UNLESS a REAL closer appears before EOL — a real closer being a
`'` NOT followed by a word char (type-param quotes are always followed by
a word char). So `type B<'a> = S of 'a | Te` has two quotes both followed
by `a` → no closer → two type params, not one fake string spanning them
(the reported bug). `'echo x'`, `'x'`, `'a > b'` keep their closers → raw
strings. TextMate uses the equivalent Oniguruma lookahead
`'[a-zA-Z_]\w*(?![^\n]*'(?![A-Za-z0-9_]))`, ordered before the
command-single-quote region. micro exempt: RE2 cannot peek for the closer
— the WORKED EXAMPLE for D-MICRO. Both rich editors verified (tree-sitter
CLI: two type_params on the flagship line; TextMate engine: both `'a` →
entity.name.type, all command strings preserved). 0 ERROR clean+malformed.

**Follow-up — field-declaration names** (`5e0e45d`, user-reported after
the six): a record's field NAMES rendered in the TYPE colour (all
`entity.name.type` / `@type`) because they are Pascal-case and the casing
law paints uppercase as type — so the whole record body was monochrome,
in ALL THREE grammars (not VS-Code-only; the LSP legend is command-only,
emits nothing on a record decl). Fix: field-decl names → `@property` /
`variable.other.property` (matches the member scope, so fields and member
access read alike), a deliberate exception to "uppercase = type" for the
field-declaration position. tree-sitter: a name-before-`:` query placed
BEFORE field-type (both anchor the `:`; the pattern that captures `:` LAST
owns the type-after-`:` render — a highlight-crate quirk found via
`tree-sitter highlight`, last-wins confirmed the same way). TextMate: a
`^`-anchored lookahead rule (`^\s*(name)(?=\s*:)`) — the lookahead leaves
the `:` for field-type, the `^` anchor keeps `let x: int` binders out.
micro-exempt: field-name (RE2 no lookahead + whole-match single colour).
Inventory now 23 rules + 3 stated micro-exempt (binder, type-param,
field-name). Verified on `tree-sitter highlight` (field names cyan, types
red, `let x:` binder untouched, member access intact) and the TextMate
engine. NOTE for future rig use: `tree-sitter highlight` needs a
`tree-sitter.json` with `file-types:["weir"]` + a HOME with a theme config
(`$HOME/.config/tree-sitter/config.json`); create the json temporarily
(it flips generate to ABI15 — remove before committing).

VERIFICATION: tree-sitter CLI (captures + 0-ERROR corpus, clean AND
malformed) and the vscode-textmate + oniguruma engine, per the standing
container-verification discipline — never by eye. Inventory e2e green (23
rules, 2 stated micro-exempt: binder, type-param). Full e2e battery green
(freshness gate re-stamped after republish). Editors only — no language
change (git touched only `editors/`).

## Done when

Attribute arg literals color; type params stop painting as strings in
the editors that can express it (with micro's shortfall stated if RE2
blocks it); binder names color in VS Code (+ Zed already); D-MICRO is
ruled and the inventory e2e reflects it; every fix tool-verified; no
parse regressions.

## FOLLOW-UP — record-CONSTRUCTION field names (user-found 2026-07-28, FIXED 2026-07-28)

The field-name fix (`5e0e45d`, above) reclaims a name as `@property`
ONLY at the DECLARATION site — every grammar anchors the reclaim on a
following `:`. A record CONSTRUCTION expression uses `=`, not `:`, so
its field names fall through to the casing law and render as `@type`.

Repro (token inspector confirms `entity.name.type.weir` on `BicepPath`):

```
let toTarget a = {
    BicepPath = $"bicep/apps/stacks/{a.stack}/{a.env}.bicepparam"
    Stack = a.stack
    Env = a.env
    Name = $"{a.stack}-{a.env}-apps"
}
```

Same root cause in ALL THREE grammars (micro already field-name-exempt):
- TextMate `field-name`: `^\s*(Ident)(?=\s*:)` — colon-only lookahead.
- tree-sitter: `((identifier)/(constructor) @property . (punctuation)
  (#eq? ":"))` — colon-only anchor.
- micro: field-name is RE2-exempt regardless.

DISAMBIGUATION TRAP (why this is not a one-line lookahead widen to
`[:=]`): "uppercase ident before `=`" is ALSO the type-definition head.
`type Target = { … }` must stay `@type`; `{ BicepPath = … }` must become
`@property`. tree-sitter has no distinct record-construction node today
(no `record`/`field_init` in `grammar.js`), so disambiguating there
likely needs a small grammar node, not just a query — otherwise a `=`
query recolors `type T =` heads. TextMate can lean on a safer heuristic
(reclaim `^\s*[A-Z]\w*(?=\s*=)` scoped inside a `{…}` block, or accept
that `type`/`let` lines never START with a bare capitalized ident and
widen the line rule) — but that heuristic itself is a decision to bless.

Lowercase construction fields (`stack = …`) are NOT visually wrong today
(the casing law only fires on `[A-Z]`), so the bug is scoped to
Pascal-case field names — which is the common case for records.

FIX SHIPPED 2026-07-28 (both follow-ups, one increment): TextMate broadened
`field-name` lookahead `(?=\s*:)` -> `(?=\s*[:=])` — the `^`-anchor keeps a
let-binding's last param OFF (a param is never at line start; `let` is), so
`let statusRefs ctx =` leaves `ctx` unscoped, VERIFIED. tree-sitter added
`((constructor) @property . (operator) @_eq (#eq? @_eq "="))` — CONSTRUCTOR
form only (uppercase, the casing-law victims; the identifier form would
reclaim a param before `=`), and the anchored adjacent-sibling operator
excludes `type X =` / `let x =` because their name sits INSIDE a
type_head/let_head node, NOT a sibling of the `=` (confirmed by
`tree-sitter parse`). micro stays field-name-exempt (lookahead), exemption
reason widened to `:`/`=`. LIMITATION (stated): the `^`-anchor / line-start
reliance means only MULTILINE (one-field-per-line) records colour; inline
`{ A = 1; B = 2 }` fields stay type-coloured — but that was ALREADY true for
the declaration form, so no regression, just an unclosed edge. Verified on
the vscode-textmate+oniguruma engine and `tree-sitter query` (captures
dumped, last-wins confirmed), 0 ERROR, inventory e2e green (24 rules).

## FOLLOW-UP — lowercase types outside annotation position (user-found 2026-07-28, FIXED 2026-07-28)

Sibling of the construction-fields gap, dual axis. Lowercase type names
get `@type` ONLY after a `:` — the `field-type` rule is colon-anchored in
every grammar. So a lowercase type in ANY other type position is
unscoped and renders in default foreground.

Repro (inspector shows `string` with NO selector, only `source.weir`):

```
type PullOutcome =
    | Pulled
    | UpToDate
    | JoinConflict of string string
```

The `string` after `of` is uncolored. UPPERCASE payloads (`of Foo`) DO
color — the casing-law `types` rule (`\b[A-Z]…`) fires anywhere — so the
gap is specifically LOWERCASE types (`string`, `int`, `bool`, `unit`, …)
away from a `:`.

Second sighting (generic args), same root cause: `branch: Option<string>`
— the inner `string` is "Other". `field-type` grabs only the FIRST
lowercase ident IMMEDIATELY after `:`; here `:` is followed by `Option`
(uppercase, colored by `types`) and the nested `string` sits after `<`,
unreachable by any colon-anchored rule. So even a colon-present
annotation leaves nested/generic lowercase types uncolored
(`Map<string, int>`, `List<string>` all the same).

The three type-color rules and their reach:
- `types` — `\b[A-Z]…` — uppercase, any position (OK).
- `field-type` — `(:)\s*([a-z_]…)` — lowercase, ONLY after `:`.
- `type-param` — `'a`.

Nothing covers lowercase types after `of` (union-case payload), after
`->` (function signatures `string -> int`), or inside generic args
(`List<string>`). Same in tree-sitter (lowercase → `@type` only via the
colon-anchored field-type query; `(constructor) @type` is uppercase-only)
and micro (field-type colon-anchored). All three share the assumption
"lowercase type ⇒ preceded by `:`", which only holds for annotations.

FIX SHIPPED 2026-07-28: the cleaner key than "positional introducers" —
weir has EXACTLY FOUR lowercase types (`int`/`string`/`bool`/`unit`, from
Types.fs), because the casing law makes every OTHER type uppercase. So a
CLOSED WORD LIST is the whole lowercase-type universe and colours them in
every position (of/-> /generics/tuples), no lookahead, no positional
gymnastics — expressible in ALL THREE engines (micro RE2 included, so NOT
exempt). New rule `builtin-types` = `\b(int|string|bool|unit)\b` ->
entity.name.type in TextMate + micro (+ interp holes for parity); tree-sitter
`((identifier) @type (#any-of? @type "int" "string" "bool" "unit"))` placed
EARLY so later position overrides (field-name/member) win a rare collision
(a field/member named like a builtin). Inventory grew 23 -> 24 rules, still
matched. `field-type` is now effectively redundant with builtin-types (both
only ever hit the four builtins after `:`) but LEFT IN — removing it is
spec/inventory churn for zero visible gain. Verified both engines, 0 ERROR.
