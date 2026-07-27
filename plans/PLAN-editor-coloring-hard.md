# weir — editor coloring: the hard parts

Status: DRAFT — awaiting bless. The three items from PLAN-editor-
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

## Done when

Attribute arg literals color; type params stop painting as strings in
the editors that can express it (with micro's shortfall stated if RE2
blocks it); binder names color in VS Code (+ Zed already); D-MICRO is
ruled and the inventory e2e reflects it; every fix tool-verified; no
parse regressions.
