# weir — unused bindings refuse (the strictness family grows a member)

Status: BLESSED (designer, 2026-09-13). One arc: the law in the check
pipeline, the fuzz generator taught, pins/docs/scripts swept, ledger
rows landed. Decision key `[D:unused-bindings]`; divergence row id
`unused-binding`.

## The receipt

`let r = cmd | complete` never read is a SWALLOWED FAILURE. Binding is
exactly how weir silences raise-on-nonzero — the discard family already
hard-errors a bare `cmd | complete` statement ("bind it or read a
field"), so binding-and-forgetting is the same mistake one binder away,
and today it is invisible. An unread `Completed` is the
silent-wrong-meaning class the strictness family exists to kill
(statement rule, exhaustiveness-hard-error, unreachable-arm-hard-error,
pipe-alignment — no warnings exist in weir, so this is a HARD CHECK
ERROR or nothing). Go is the precedent: an unused variable is a
compile error there, and a decade of Go practice says the `_` escape
plus the error is livable and pays.

Beyond the reifier receipt the law buys a real class for free: a
module's UNSIGNED member that the module itself never reads is dead
private code — unreachable by importers (unsigned = private
[D:module-signatures]) and unread at home. The checker now says so.

## Scope (v1): let-bound NAMES, each row with its why

| binder class | verdict | why |
|---|---|---|
| top-level `let x = …` in a script | ERROR when never read | the receipt class lives here (`let r = … \| complete`) |
| block-local `let` (`ELet`/`ELetPat` in bodies) | ERROR when never read in its body | same law one scope down; the body is the whole scope, so the judgement is local and exact |
| irrefutable destructuring (`let (a, b) = …`, record patterns) | each bound NAME judged separately | a tuple part you never read is a binding like any other; `_`/`_name` per position is the discard spelling |
| an earlier binding rebound without a read (shadowing) | ERROR at the EARLIER binder | DECIDED YES — the classic copy-paste bug: `let cfg = …` re-`let cfg = …` with nothing reading the first; the error sits on the first binder and names the rebind line |
| module UNSIGNED member unused by its own module | ERROR | unsigned = private [D:module-signatures]; private + unread = dead code nothing can reach |
| module SIGNED member | EXEMPT | the signature IS the use — it exports; import sites are outside the module's own text |

## Exemptions, each with its why

- **Function params** (let-sugar params, lambda params, `()`): an
  unused param is API SHAPE, not a swallowed value — a callback that
  ignores its argument is normal, and nothing raised was silenced by
  naming it. (`fun _ -> …` and `_`-prefixed params already exist for
  the deliberate spelling; no new rule needed.)
- **Match-arm pattern binders** (arms, `function`, Regex binders,
  guards): an arm names what it matched; a named-but-unread capture is
  documentation of the shape, and the arm's alternative (`_`) is
  already one keystroke away. Out of scope v1.
- **`for` / `until` / `within` binders**: the form REQUIRES a binder —
  there is no value being silenced, and `until`/`within` binders are
  plain names by ruling [D:record-patterns]. The yaml district's `for`
  binders ride the same exemption.
- **SIGNED module members**: the signature is the use (above).
- **`_`-prefixed names**: the escape (below) — never errors, anywhere.

## The escape

A `_`-prefixed NAME (`_keep`, `_r`) is deliberately-unused and never
errors — identifiers may already start with `_` (parser: `isIdentStart`
includes `'_'`; the casing law tests `IsUpper`, so `_x` passes today,
probe-verified). A `_`-name remains readable — reading one is legal,
the prefix only switches the unused judgement off.

**Bare `_` as the whole let binder is REFUSED** (new). Probe: `let _ =
1` and `let _ = sh -c "exit 3" | complete` are ACCEPTED today
(`SLetPat` + wildcard, binds nothing) — an anonymous swallow that
silences the statement rule with zero record of what was discarded.
One escape spelling, not two: name the discard (`let _r = …`) so
intent survives grep. Wildcards INSIDE destructuring stay legal
(`let (k, _) = pair` — positional discard is honest there). Repo
blast radius: exactly one site (tools/dx-repro.weir:136).

## Where the law runs (and where it deliberately does not)

The usage core lives in Check (a scoped walk of the TYPED tree — the
env plumbing already resolves every `TEVar`, and the typed tree keeps
every binder: `TELet`/`TELetPat`/`TELambda`/`TEMatch`/`TERetry`/
`TEWithin`/yaml `for`). Cross-statement threading is a whole-file law,
applied post-fold exactly where the sig-orphan law sits, in EVERY
consumer that folds a whole file:

1. `Script.analyzeLines` — `weir check`, `--json`, `--can`, the LSP.
2. `Script.run` — the runner's check phase gates eval (check error =
   zero side effects; a law only in check would let `weir run` execute
   what `weir check` refuses).
3. the module loader (`loadModuleCached`) — an imported module with
   unsigned-unused members refuses at the import, same as direct check.
4. the fidelity mirror (`Oracle.weirVerdict`) — the mirror replicates
   the runner's check phase; leaving the law out re-opens the
   mirror-drift incident class [D:one-pipeline].

NOT covered, deliberately: the REPL (interactive bindings pend by
nature) and `-e` (the echo regime, `gateExprs=false` — its bindings
exist to feed the final echoed expression, and a lone declaration is
already refused [D:e-programs]). The builtinDocs example harness is
untouched by construction — it folds `Check.typecheck` per statement
and never runs whole-file laws; doc examples stay hover-teaching
snippets.

SUPPRESSION RULE: the law fires only when every statement in the file
checked. Any parse/type-errored statement poisons the unused pass for
that file — one real error beats N echoes (the [D:modules-v1]
hole-scheme precedent), and half-typed LSP buffers don't sprout
transient unused squiggles.

## Error voice

Located at the BINDER, one error per unused binder, collected (the
check path reports all, sorted by position; the run path renders the
first as usual). Code `unused-binding`.

- plain: `'tmp' is bound but never used — read it, or name it '_tmp'
  to keep it deliberately`
- shadowed: `'cfg' is bound but never used before being rebound at
  line 12 — read it, or name it '_cfg' to keep it deliberately`
- module unsigned: `'raw' is bound but never used — an unsigned module
  member is private dead code: use it, export it with a signature
  (let raw : …), or name it '_raw'`
- bare `_`: `a bare '_' binder discards its value anonymously — name
  the discard (let _r = …) so the intent survives a grep`

## Blast-radius inventory (probed 2026-09-13, grep-approximate; the
## real checker re-counts at the sweep)

- **Repo .weir files**: 30 files; 2 approximate violations —
  tools/dx-message-census.weir (`saysAny` @106),
  tools/jira-branch.weir (`app` @40) — plus the one bare-`_` site
  (tools/dx-repro.weir:136).
- **Fuzz generator** (tests/Weir.Fuzz + tools/fuzz.weir): EMITS unused
  bindings today — `genExpr` consumes scope names as one
  `Gen.frequency` branch among literal atoms, so nothing guarantees a
  binder is read; the validity oracle ("base program rejected —
  generator claims validity", invariant 1) breaks the moment the law
  lands. THE FIX: render-time escape — `renderTagged` prefixes `_` on
  every def-site whose name is referenced nowhere in the program
  (binder names are globally unique via `Fresh`, so
  referenced-anywhere equals referenced-in-scope; transforms are
  render configs over the same `Stmt` tree and invariant-2 mutations
  are totality-only, so the rename is deterministic across every
  invariant, and the shrinker's statement drops re-derive it at the
  next render by construction). GRAMMAR.md gains the coverage line.
- **Fidelity pins** (tests/Weir.Fidelity/Pins.fs): 187 pins, ~180
  carry the deliberate "value results are let-bound so the statement
  rule never muddies the pin" convention. Every pin whose WEIR verdict
  is Accept renames its result binders to `_`-prefixed on BOTH sides
  (F# accepts `_x` — probe-verified; verdicts unchanged, each pin's
  SUBJECT intact). Tagging ~180 pins `Diverges unused-binding` was
  REJECTED: it would replace every pin's subject with this law's.
  Weir-rejects pins need no touch (still reject, reason unchanged is
  not a pin claim). NEW pins land the border: unused top-level
  rejects (Diverges unused-binding), `_x` escape accepted (Same),
  bare `_` rejects (Diverges, same row), shadow-without-read rejects
  (Diverges, same row).
- **F#'s verdict, probed honestly**: `fsy build` on `let x = 5` +
  `printfn` is a clean accept, ZERO warnings emitted — FS1182 exists
  but is opt-in (`--warnon:1182`), so the row reads: weir rejects,
  F# accepts silently by default (warns only with flags).
- **Doc fenced blocks** (skill-doc executes them): 184 executable
  `weir` blocks across SKILL/GUIDE/COMING-FROM/reference; 7
  approximate violations — SKILL.md:995 (`linked` — honest discard,
  `_linked`), GUIDE.md:92/466/836/926/1811, reference/boundaries.md:71.
  SPECIAL: the SKILL msig-lib module example declares `let raw n = n +
  1` unsigned AND unused by the module — under the module law the
  fixture module itself refuses to load, breaking the NEXT block. The
  example changes so `grade` USES the private member — which is the
  honest teaching of what private members are for; the
  unsigned-import weir-error block keeps its subject.
- **e2e inline scripts** (ci/e2e.sh heredocs): 221 blocks with lets,
  ~38 approximate violations, including module fixtures whose
  deliberately-private members must become used-or-`_` (approx
  over-counts sig+impl pairs; the run re-counts).
- **builtinDocs examples**: unaffected (harness shape, above).
- **Site**: no independently-executed weir blocks (the site renders
  the docs; site-anchors checks headings, not code).

## Mechanics

1. **Check.fs** — the usage core `[D:unused-bindings]`:
   - a scoped walk of `TypedExpr` returning (free names used, unused
     LOCAL binders with spans). Binding forms: `TELet`/`TELetPat`
     TRACK (error class); `TELambda`/`TELambdaPat` params,
     `TEMatch` arm patterns (incl. guards), `TERetry` `until`,
     `TEWithin` binders, yaml `for` binders SHADOW-ONLY (exempt).
     Free names propagate minus each form's bound set; `let x = x + 1`
     reads the OUTER x (no `let rec` exists — the RHS walks before
     the binder registers).
   - the bare-`_` refusal at the binder-checking seam (a wildcard as
     the WHOLE `let` binder pattern; nested wildcards untouched).
2. **Script.fs** — the whole-file threading: a pending-binder tracker
   fed per checked statement (marks free uses against pending; emits
   block-local unused; registers new top-level binders, erroring an
   unread same-name pending binder at rebind), flushed at end-of-file
   with the module's Signed set exempted. Wired into `analyzeLines`
   (collected diags, code `unused-binding`), `run`'s check fold
   (post-fold gate before eval), and the module loader's post-fold
   (beside sig-orphan).
3. **Oracle.weirVerdict** — same tracker over its fold; nonempty =
   Reject.
4. **Fuzz Grammar** — `usedAnywhere` over the `Stmt` tree (the
   shrinker's `stmtUses` machinery, un-filtered and recursive);
   `renderTagged` renames unreferenced def-sites `_`-first.
5. The sweep + artifacts (below).

## Work items

1. Plan committed (this file).
2. Check.fs usage core + bare-`_` refusal; Script.fs threading into
   analyzeLines / run / module loader; Oracle mirror. `[D:]` comments
   at each site.
3. Fuzz generator taught; GRAMMAR.md line.
4. The sweep: repo .weir ×2 (+ dx-repro's bare `_`), SKILL/GUIDE/
   reference blocks ×7 + msig-lib reshape, e2e heredocs (~38, real
   count from the run), pins (~180 mechanical `_`-renames, exact set
   from the failing suite). Prefer making the binding USED where the
   binding was a real smell; `_`-prefix only where discard is the
   honest intent.
5. Artifacts: unit tests (unused top-level / block / destructured /
   shadowed each error; params / arms / for / until / within / yaml
   binders each accepted; `_x` accepted; bare `_` rejected; module
   unsigned-unused errors; signed member exempt — pinned); new
   fidelity pins + divergences row `unused-binding` (status
   `different`); SKILL.md section (law + escape + a weir-error
   block); CHANGELOG `## v0.0.33` opened, bullet leads "Breaking:";
   DECISIONS row `[D:unused-bindings]`; `weir ci/decision-keys.weir`.

## Gates

Build all four projects; full unit suite; `./publish.sh`;
`ci/skill-doc.sh`; `ci/e2e.sh` (REAL=0 captured); deep fuzz
`weir tools/fuzz.weir --count 10000` THREE times serially, all clean
(the generator work makes this non-optional), never concurrent with
e2e. Commit per stage; subjects via `ci/commit-area.weir --commit
HEAD`.

**Done when:** an unread `let` binder is a located hard check error in
scripts and modules under check, run, import, LSP, and the fidelity
mirror; every exemption above is pinned; `_name` escapes; bare `_`
teaches; the repo, docs, pins, and generator are clean under the law;
all gates green.
