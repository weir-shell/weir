# PLAN: the syntax DX batch — eight findings plus the reversed operators

**Status: APPROVED 2026-10-06 — all four ⚖ rulings accepted as recommended (line-end `\` is an error; or-patterns v1 binder-free; `<<` removed and `<|` refused; holes generalize under Show-minus-Secret).** Source: the
2026-10-06 DX survey (`/output/wt/DX-REPORT.md`, probes in
`/output/wt/probes/`), each finding backed by a probe or a file:line
receipt. Lands after the boundary stack (wire-table → wire-codecs →
print-canonical) reaches main; branches off main then.

## The items

### 1. A line-end `\` in a command line teaches; indented continuation is documented
- **Today:** `cmd a \` + an indented `b` checks clean and passes a literal
  `\` to the child (`a|\|b|`); `find … -exec … \;` checks clean and `find`
  fails. Indentation already continues a command line (pipes included) but
  no doc says so; the corpus carries 156–297-char command lines instead.
- **Change:** a check error at a line-end `\` word ("weir continues a
  command line by indentation — drop the `\`; a literal backslash argument
  is `"\\"`") and at a bareword starting with `\` (`\;` → "no backslash
  escapes in argv — write `";"`"). Docs: SKILL command section,
  `docs/reference/commands.md`, COMING-FROM's bash table, and a reflow of
  the longest corpus lines as the worked example.
- ⚖ **Error, not warning** (recommended): a line-end `\` has no plausible
  literal intent, unlike `;`/`>` which keep their warnings. A lone mid-line
  `\` word is included (its only literal spelling is `"\\"`); measured
  first by a corpus run, as [D:argv-concat] did.
- Size: assembler/parser teaching + docs.

### 2. Or-patterns in `match` arms
- **Today:** `| "a" | "b" -> …` is a raw dump. Receipt: `ci/commit-area.weir`
  (17 arms repeating the arm above) and four `p == "x" || …` chains. The
  divergence row `or-patterns` says "reopen on a receipt" — met.
- **Change:** F#'s spelling as a parse-time desugar,
  `| p1 | p2 [when g] -> e` ≡ `| p1 [when g] -> e | p2 [when g] -> e`;
  exhaustiveness, guards and arm commands inherited unchanged; a duplicated
  body's diagnostics deduplicated by span.
- ⚖ **v1 = binder-free alternatives only** (literals, nullary cases,
  wildcard payloads — every receipt); an alternative that binds a name
  refuses with a teaching until a receipt asks for F#'s
  same-names-same-types rule.
- Ledger: new row; the divergence row flips to converged.
- Size: parser desugar.

### 3. Reflex teachings, and the reversed operators `<|` / `<<`
- **Today:** each is a raw "Expecting:" dump or a wrong teaching
  (`let b = !ok` is told about the retired line-end `!` district,
  `Script.fs:789`).
- **Change** (the [D:dx-message-families] mechanism: parse the reflex at
  the right precedence, the checker teaches):
  - `a != b` → "weir's inequality is `<>` (equality is `==`)";
  - `!x` in expression position → "weir has no `!` negation — `not x`";
    `retiredDistrictMarker` declines when `!ident` follows an
    expression-position token;
  - `(x: int)` / `let x: int =` in a script → "weir infers types in
    scripts — drop `: int`" (a module names its signature form);
  - `for i in 1..3` → "a range is a list: `[1..3]`";
  - `\ls` → "no `\`-escape for commands; `^ls` forces the PATH binary";
  - `f <| x` → "weir has no `<|` — write `f (x)`, or pipe `x |> f`".
  - `f << g` → "weir composes left to right — write `g >> f`".
- ⚖ **The reversed operators (user-raised):** weir keeps the left-to-right
  forms `|>` and `>>`; the reversed spellings refuse with a teaching.
  - `<|` was absent by accident (no row); refused by decision now: it is
    `|>` reversed, with no receipt, and F#'s left-associativity gotcha
    (`f <| g <| x`).
  - `<<` is REMOVED: `f << g` is exactly `g >> f`, a second name for one
    operation ([D:first-retired]); zero uses in any weir code in the repo;
    one character from the `<<<` text-block marker. Breaking in principle,
    mechanical with the teaching. The `(<<)` operator value goes too.
  - One ledger row for both; divergence rows `no-back-pipe` and
    `no-backward-composition` (the fidelity pin "backward composition"
    flips from Same to the recorded divergence); docs drop `<<` (SKILL,
    GUIDE, `docs/reference/lexical.md`'s precedence table); check the
    editor grammars (grammar-currency) for an `<<` operator entry.
- `!=` is **not** a synonym ([D:first-retired]: one name per operation;
  the glyph law: `!` means "do it").
- Census: each case joins `tools/dx-message-census.weir` with its bucket.
- Size: parser + messages.

### 4. Interpolation holes generalize like `show`
- **Today:** `let addr s = $"{s.name}:{s.port}"` fails at its caller — a
  still-free hole defaults the type to `string` at the let. The corpus
  wraps holes in `show` and documents why
  (`examples/anysync-config.weir:69-75`); SKILL teaches the workaround.
- **Change:** at a generalizing `let`, a still-free hole variable is
  quantified under a compiler-owned class instead of bound to `string`
  (`Check.resolvePendingSplices`). Non-generalizing positions keep the
  default; argv splices stay scalar-exact ([D:interp-show]). The runtime
  already renders by value tag, so no eval change.
- ⚖ **The class is "renders in a hole" = Show minus `Secret`**, closed and
  structural ([D:closed-classes]), so a `Secret` still refuses at the hole
  while `show` masks it. This re-weighs [D:splice-default-last]'s string
  default — a semantic change, needs an explicit yes.
- Size: checker — the one large item; last in the batch.

### 5. Command `let`s in a multi-line lambda body
- **Today:** `[1] |> Seq.iter (fun _ ->` + `let r = cmd | complete` is
  refused, though the same body works in `for` (documented to BE that
  `Seq.iter`), on a `let` spine and in a function body.
  [D:statement-lets] parked the lambda boundary "reopening only as its own
  ruling"; its cited hazard did not reproduce.
- **Change:** a dangling `(fun … ->` body block gets the statement grant
  (`withStmtLetCmd`); single-line bodies and `let … in` stay expression
  territory.
- Size: parser (move the grant).

### 6. Field splices `$cli.tag` and `^$cli.bin`
- **Today:** `$cli.tag` gets the "argv words do not concatenate" teaching;
  13 `(cli.tag)`-style workarounds in 6 files; a dynamic head has no field
  spelling (15 `sh -c $"'{cli.bin}' …"` sites).
- **Change:** `$ident(.ident)+` (and `$@`, `^$`) splices the field path as
  one word; `$root/*` and `--flag=$x` keep refusing. Bash's `$file.bak`
  becomes the type error with a `$"{file}.bak"` teaching — refused either
  way, never silent.
- Size: parser.

### 7. A `let` that fails to parse still binds its name
- **Today:** a parse error leaves the name unbound, so the follow-on is
  "unbound variable 'x'. Did you mean 'cd'?"; type-errored statements
  already bind hole schemes (`Script.fs:6039`).
- **Change:** harvest the binder lexically (`let NAME`, `let NAME p… =`,
  `type NAME`) at a statement head and bind the same hole scheme.
- Size: small (Script.fs).

### 8. Text blocks after `| pat ->` on the same line
- **Today:** `then <<<` works; `| 1 -> <<<` dumps; `File.write p <<<`
  dumps.
- **Change:** the arm arrow takes a block marker as `then` does; a block as
  a trailing argument teaches the documented composition (`<<<` … then
  `|> File.write p`).
- No corpus receipt — consistency only; lowest priority.
- Size: parser + a teaching.

## Branches (each off the one before, like the earlier stacks)

1. `dx/teachings` — #1, #3 (with `<|`), #7: diagnostics only, lowest risk.
2. `dx/or-patterns` — #2.
3. `dx/lambda-lets` — #5.
4. `dx/field-splices` — #6.
5. `dx/arm-blocks` — #8.
6. `dx/hole-generic` — #4 (checker; its own ledger re-weighing).

Each branch: unit tests, an e2e cell, a ledger row, a CHANGELOG entry, the
census where it applies, docs (SKILL/GUIDE/reference/COMING-FROM), the
docs dump if builtin docs move, three fresh fuzz seeds for any parser or
assembler change, and the full AOT e2e.

## Also (no language change)
- A corpus sweep using the new spellings once they land: reflow the long
  command lines (#1), or-patterns in `ci/commit-area.weir` (#2), drop the
  `show` hole wrappers (#4), field splices in `tools/` (#6), and fix the
  stale comment at `tools/multi-repro.weir:34` (a `within` yields its
  body's value).

## Interaction with the parked within/always plan
`PLAN-within-always-any.md` is parked to resume after the boundary stack;
it can run beside this batch (it touches the `within` parser only).
