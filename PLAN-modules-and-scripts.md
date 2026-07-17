# weir — proposal: builtin modules, then a script language

Status: BLESSED (advisor pass + user amendments 2026-07-17). All decisions
DECIDED. User amendments: `//` comments (F# feel), indentation-based
scripts as the chosen target (gate retained as tractability checkpoint),
broad bare string ops in normal mode, strict mode for scripts,
qualification required on cross-module name conflicts.

Driven by two forces. First, the flat namespace is creaking: `mapOption`,
`strLen`, `tryIndexOf` are poor-man's namespacing, and the `strLen`-vs-
`length` collision decision was forced by flatness, not chosen on merits.
Second, the script direction: weir one-liners already beat bash one-liners
on types; the gap is files. Modules are sequenced first because scripts are
read later while REPL lines are typed now — a stable qualified vocabulary
(`Seq.map`, `Str.trim`) pays in files, and landing names in their canonical
homes *before* anyone writes a `.weir` file means scripts never accrete the
flat names as legacy.

Also recorded here: **measure algebra is dropped for the foreseeable
future** (library-plan Session 4 cancelled). The `no_unit_algebra` tripwire
becomes a permanent guard rather than a pending reopening; measures remain
nominal tags with preservation-only arithmetic. If it ever returns it
returns as its own plan, re-reading checklist §4.2 from scratch.

## Pre-made decisions

- DECIDED — **Module inventory**: `Seq`, `Str`, `Option` (`Result` when it
  has members). NOT modularized: command-mode-critical names (`cd`, `sh`,
  `cmd`, `ls`, `pwd`, `into`, `completed`) and the adapters (`from`/`to` are
  syntax anyway).
- DECIDED — **Two resolution modes; scripts are STRICT BY DEFAULT.**
  *Loose mode* (the REPL, always — no directive exists there): common
  operations are available bare — the seq pipeline set AND all common
  string operations (`contains`, `startsWith`, `endsWith`, `trim` family,
  `split`, `join`, `replace`, `toLower`, `toUpper`, `toInt`, `tryToInt`)
  — every bare name doubly registered with its canonical `Seq.*`/`Str.*`
  home. *Strict mode* (scripts, by default): bare aliases off, module
  members qualified-only. `#loose` at file head (line one, or two after a
  shebang) opts a file into REPL-style bare names for quick glue — the
  directive names the concession, so it is `#loose`, not `#normal`.
  **The stability argument, which decides the default**: bare-name
  resolution is a moving target (the bare table grows), and scripts are
  durable artifacts — a script must not change meaning or die because
  weir gained a builtin. Qualified names mean the same thing forever;
  strict scripts are immune by construction. REPL lines are ephemeral;
  resolution drift there costs a retype. Pins: `#loose` anywhere but
  file-head is an error; any `#`-directive at the REPL is rejected with
  "directives are for scripts; the REPL is always loose."
- DECIDED — **Option members are qualified-only, in both modes** ("bare
  names are the data plane; Option is the control plane"): `Option.map`,
  `Option.defaultTo`, `Option.tryHead`-consumers etc. never register bare
  names. Stated as a rule so the day someone adds bare `Option.map` "for
  consistency" is recognized as reopening a decision, not tidying.
- DECIDED — **Trial resolution is DEFERRED; the design is on file.**
  With scripts strict by default and Option qualified-only, the launch
  bare table has exactly one prospective conflict (`length`, if
  `Seq.length` ever joins `Str.length`) — one launch customer does not
  justify the checker's first backtracking. Interim rule: `length` is
  qualified-only in both homes (the collision that once forced `strLen`
  gets its honest answer: qualification). The mechanism is retained here
  as design-on-file for when user modules create the second conflict:
  type-directed trial unification over the closed candidate set at the
  application site, pipe-argument-first (the Spike-5 ordering); trials
  run under the site's bidirectional mode with pushed expected types
  participating (else `where (contains ...)`-shaped idioms break the day
  their name conflicts); each trial against a Ctx snapshot — Subst AND
  Rows restored, fresh counter monotone, **and no diagnostics: a failed
  trial's warnings must not survive the rollback**; snapshots nest for
  nested conflicted names; zero-fit errors show best-per-candidate
  reasons, not just names. Those five clauses are the adversarial floor
  when it is built. Unique-fit wins; zero or multiple fits errors naming
  all candidates; no priority, ever.
- DECIDED — **Retirements the namespace enables**: `mapOption` →
  `Option.map`, `defaultTo` → `Option.defaultTo` (bare alias retired),
  `strLen` → `Str.length` (superseding the collision decision properly),
  `tryIndexOf` → `Str.tryIndexOf`, `substring` → `Str.sub`, `split`/`join`/
  `replace`/`trim`-family/`contains`/`startsWith`/`endsWith`/`toLower`/
  `toUpper`/`toInt`/`tryToInt` → `Str.*`, `tryHead`/`tryFind`/`isEmpty`/
  `sortBy`/`groupBy` → `Seq.*`. Pre-1.0: flat originals are dropped, not
  aliased, EXCEPT the hot-path aliases above. One migration commit, history
  grepped.
- DECIDED — **Shadowing**: values shadow modules (`let Seq = 1` wins),
  same rule-shape as builtins-shadow-PATH. Documented, tested.
- DECIDED — **Mechanism is namespace resolution, not builtin magic**: a
  `Modules` map resolved in the checker, designed so user modules
  (`module X = ...` declarations, later file imports) are additive entries,
  not a redesign. Members are *schemes* (instantiated per use — this is why
  modules cannot be record values: record field access returns a type,
  module member access must freshen).
- DECIDED — **Comment syntax: `//` to end of line** (F# feel wins, per
  user). The shebang line (`#!`) is special-cased by the script runner:
  line one starting `#!` is skipped. **`#` at line head is reserved for
  directives** — the first being `#strict` (strict resolution mode,
  above); shebang is then just the OS's directive. Comments are never
  `#`.
- DECIDED (by architecture) — **Whole-file check before any effect**: a
  script typechecks completely — including command-not-found via PATH
  resolution — before line one executes. This is the pitch against bash;
  it falls out of the check/eval split and is non-negotiable in the runner.
- DECIDED — **Indentation-based multi-line is the chosen target** (F#
  feel, per user): offside-rule scripts, not delimiters. Session 3 remains
  a *tractability* gate, not a direction gate — the design is decided, the
  gate proves it implementable against the kill criteria (expression-suite
  green, command-mode interaction sane); a kill outcome falls back to
  single-line statements and schedules a second attempt, it does not
  reopen the direction. Gate evidence includes the Session-2 e2e script
  itself — the first honest data on single-line statement pain — alongside
  the REPL wish-list.
- DECIDED — **CLI is unambiguous, no sniffing**: a positional argument is
  a script path, always; `-e` is an expression, always. File-vs-expression
  sniffing is a bash-ism that ends in CVEs.

## Session 1 — builtin modules

1. `TypeEnv.Modules : Map<string, Map<string, Scheme>>`. Checker: one new
   `EField` arm — target is `EVar m`, `m` in Modules and not value-shadowed
   → look up member scheme, instantiate (the existing freshen-on-use
   machinery). One new `EVar` arm: bare module name → "Seq is a module; use
   Seq.map". Runtime: members stored under mangled flat names
   ("Seq.map"); eval untouched.
2. Builtins regrouped per the inventory + retirement table. Hot-path
   aliases double-registered.
3. Completion: module branch in the dot-completion (`Seq.<TAB>` → members);
   resolver/Diagnose `IsKnown` includes module names.
4. Tests: qualified use at two types (scheme freshening through module
   access — the inference-relevant bit), the three-way resolution
   precedence pinned exactly — `let Seq = { ... } in Seq.map` must take
   the value-shadow arm and behave as ordinary field access (same syntax
   now resolves value → module → row-field, in that order; the failure
   mode is not unsoundness but a record named `Seq` silently switching
   arms), bare-module error,
   retired names produce unbound-with-hint (did-you-mean should suggest the
   qualified home — check the hint helper reaches module members),
   completion. Tripwires re-run explicitly (checker change). E2E: one
   qualified pipeline on the AOT binary.
5. SEMANTICS: modules section (inventory, aliases, shadowing, mechanism
   note), measure-algebra-dropped recorded, `strLen` bullet superseded.

Human read: the `EField` module arm — instantiation site (§3 discipline)
plus the three-way resolution seam above, precedence order verified.
(The trial-resolution read target left with the deferral.)

Budget note: the retirement list breaks the pinned e2e battery
(`map trim`, `startsWith` in the git-branch task) and several SEMANTICS
examples — the hot-path aliasing above keeps the battery text valid, but
every example touching a retired name gets migrated in the same commit.

**Done when:** `ls |> Seq.sortBy _.Size |> Seq.map _.Name` and
`Str.split "," "a,b"` work; retired names hint their new homes; suite +
tripwires + e2e green.

## Session 2 — script runner (single-line statements; no new grammar)

1. `weir run script.weir` (and `weir script.weir` if unambiguous with `-e`):
   statements executed top to bottom with persistent bindings — the REPL
   loop's semantics, batched. `//` comments stripped; `#!` line one
   skipped; `#strict` directive (line one, or two after a shebang) flips
   the file to strict resolution — bare aliases off, module members
   qualified-only.
2. **Check-everything-first**: parse and typecheck every statement (PATH
   lookups included) before evaluating any. Errors report `file:line` using
   the spans that have carried line numbers since Spike 1.
   **Named divergence from every shell users know**: install-then-use
   (`sh "cargo install thing"` on line 2, `thing ...` on line 10) is a
   check-time "command not found" — the script that bash would have run
   fails before line one. This is the pitch, stated positively: declare
   dependencies, don't install mid-script. Rules-doc line required, and
   the escape hatch named explicitly: `sh "thing ..."` defers resolution
   to runtime because sh takes a string.
3. **`weir fmt --qualify`** (promoted from parked — it ships WITH the
   strict default, not after it): the graduation bridge for the
   paste-from-REPL-history workflow. The checker knows every bare name's
   canonical home, so the rewrite is mechanical and type-directed;
   `--qualify` converts a loose file to strict-clean and reports the
   rewrites. Without this, strict-by-default taxes exactly the
   prompt-to-file flow scripting exists to serve.
4. Script inputs: `args : seq<string>` (post-script-name argv), `stdin :
   seq<string>` (lazy line stream). **Flagged as a decision, not
   plumbing**: when the script never touches `stdin`, do child commands
   inherit the terminal's stdin (interactive ssh mid-script works) or the
   script's? Inherit-unless-consumed is what users expect and is fiddly —
   decide in-session, document either way. Exit code: 0, or 1 on runtime
   error, or the checker error path with nonzero before any effect.
   **Decided now**: a raising external maps to generic exit 1 — the
   child's code does not propagate (bash's `$?` reflex will ask; the
   answer is documented: use `complete` if the code matters).
4. E2E (per the standing rule): a `.weir` script exercising bindings, a
   command-mode line, a type declaration, and `args` — run via shebang on
   the AOT binary; plus a script with a type error on line N proving
   nothing executed (touch-a-file-then-error shape).
5. SEMANTICS: script execution model (check-first guarantee stated as the
   headline rule), args/stdin, comment syntax.

**Done when:** a real dogfood script (the git-branch-cleanup task as a
file) runs via shebang; the type-error-runs-nothing guarantee is pinned in
e2e.

## Session 3 — multi-line design gate (design, prototype, kill/continue)

Not an implementation session. Deliverable is a design doc + throwaway
prototype answering:

1. Offside rule vs explicit delimiters (`end`, braces) vs hybrid
   (offside for `let`/`match` bodies only). FParsec indentation machinery
   prototyped on the two forms that hurt most: multi-line `match` and
   `let` without `in`.
2. Interaction table with existing grammar: match-arm `|`, command-mode
   line heads mid-script, the commit-to-command-mode rule, lambda
   greediness, seq literals spanning lines.
3. Kill criteria, written before prototyping: if the prototype cannot keep
   the expression suite green with < N parser-lines changed (pick N in
   session), if command-mode interaction demands mode decisions inside
   continuation lines, or **if the timing guard trips** (indentation-
   sensitive lexing sits on the -e/script startup path; the 6ms number
   has a CI tripwire that must not meet a surprise), STOP — ship scripts single-line-statement-only
   (they are already useful) and revisit with dogfooding data.

**Done when:** decision documented either way; if continue, Session 4 is
specced with pre-made decisions; if kill, the exclusion is recorded with
reasons in SEMANTICS.

## Session 4 — multi-line implementation (contingent on the gate)

Scoped entirely by Session 3's output. Not specced here on purpose.

## Parked (recorded, not forgotten)

- User modules (`module X = ...`) and file imports — additive on Session
  1's mechanism; wait for a script that wants them. **User modules are
  also the trigger for building the deferred trial resolution** (they
  create the second bare conflict).
- `Result` members, `Option.bind`/chaining — wait for the Option idiom to
  demand them in dogfooding.
- Measure algebra — dropped (see header).
- Regex — unchanged: own design, own plan.

## Hygiene

- One session per branch; Session 1 merges alone (it breaks names —
  dogfooding wants the rename shock in one dose).
- Tripwires re-run explicitly in Session 1 (checker) and Session 4 (parser
  + whatever the gate demands).
- E2E rule stands: every done-when behavior gets a ci/e2e.sh entry against
  the AOT binary — scripts make this cheaper, not optional.
- The dogfooding week continues through Sessions 1–2 and its pain list
  feeds the Session 3 gate directly: every multi-line expression you
  wished you could write is gate evidence.
