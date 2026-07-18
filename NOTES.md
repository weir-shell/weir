# Spike Notes

## weir fmt — the canonical formatter, v1 (2026-07-18)

User hit `weir fmt file` falling through to "no such script: fmt"
(dispatch had only --qualify) and asked for an actual formatter.
Scoped honestly: v1 normalizes leading indentation (4 per structural
depth, derived from the same pending-let stack the assembler tracks)
and strips trailing whitespace; comments and token spacing are
verbatim (respacing/re-flowing needs trivia-preserving parsing — the
AST has no comments — parked). Column-0 pipe continuations keep their
shell style. `--check` gates CI.

The safety property is the design's spine: format, re-assemble both,
compare logical-line texts — refuse to write on any mismatch. It
tripped on its FIRST real input: original trailing spaces joined into
the logical text (`sum   in` vs `sum in`) — a false positive fixed by
TrimEnd-normalizing both comparison passes (sound: trailing whitespace
cannot sit inside a string at line end; strings are single-line and
close with a quote). A formatter that can prove it didn't change the
parse is the claim-vs-behavior discipline applied to itself.

417 tests; battery +1 pin; skill line added; timing holds.


## Example modernization catches a let-RHS regression (2026-07-18)

Modernizing examples/repo-report.weir to current idioms (let-RHS
porcelain binding, a when-guarded status tier, a streaming command
section, workdir interpolated from cd's return) immediately exposed a
regression the let-RHS session shipped: **`let workdir = cd target`
silently changed meaning** — command-callable `cd` won the RHS head
decision and `target` became a bareword (literal "target"), where the
line had always been expression mode (cd applied to the binding). The
old test pin covered known-name heads (`let x = ls`) but commandCallable
outranks the known-check in the head decision — exactly the shadowing
subtlety the sh-removal amendment warned about, on the other builtin.

Fix: command-callable builtins stay ordinary functions on a let RHS
(builtinHeads flag threaded through the command grammar); only genuine
externals enter command mode there. Old meaning restored exactly;
regression pin added (cd applied to the binding, by AST shape).
411 tests. Lesson recorded: every mode-decision change needs pins for
ALL THREE head classes (external, known, command-callable), not just
the two that seem relevant.


## Part 3: overflow policy + data-range battery; collect renamed (2026-07-18)

Branch overflow-policy, gate waived by the owner (Part 2 pattern).
410 tests; battery +4 AOT pins; timing holds. PLAN-read-booleans-
overflow is now complete except the human READ.md.

- **collect → Seq.toList** (rides ahead of Part 3 as its own commit):
  F#'s Seq.collect is flatMap — a direct agent prior-bleed collision;
  toList is the F# name whose muscle memory matches (weir has no list
  type, so the seq return is the only reading). Frees Seq.collect for
  evidenced flatMap.
- **int literals were silently int32 while runtime was int64** — found
  as an AOT hard CRASH on `weir -e 9223372036854775807` (ParseInt32 in
  the literal path; F# 6 implicit widening had hidden the mismatch at
  compile time). Literals now parse as int64 with a parse-time range
  error beyond 64 bits. The data-range battery's first catch, before
  it was even written.
- Checked +,-,*,/ and Seq.sum raise "integer overflow" (uniform text;
  the Min/-1 division edge rides the same path). Ranges TERMINATE at
  the Int64 boundary instead of raising — yielded values are all
  correct, so termination is honest semantics; pinned.
- DataRange.fs is the permanent layer (boundaries, big strings,
  billion-element laziness); e2e adds >2GB sparse file, 0-byte file,
  Max-literal, and overflow-raise pins against the AOT binary.


## Ledger round 2 — fail, printerr, precedence hint (2026-07-18)

Second same-day fix round from the dogfood ledger. 401 tests;
skill-doc extended (fail/printerr blocks); timing holds.

- **`fail : string -> unit`** — located runtime error, exit 1;
  `if bad then fail $"..."` closes the checking-script gap (the
  cmd-sh-exit-1 workaround retires). Typed unit, not F#'s `'a`:
  weir statements want unit and a bottom-typed var would trip the
  statement rule; match-arm positions restructure instead (documented
  divergence). Joins the runtime-failure inventory in SEMANTICS.
- **`printerr`** — print's stderr twin, revived from parked on the
  ledger evidence; same sentinel scheme and argument rule (the print
  family generalized to two names, one shared checker path).
- **Pipe-into-operator is a targeted error** — operators yield values,
  never functions, so `xs |> f == v` is always wrong; the error now
  says so and names the fix ("parenthesize the pipeline").
- `in` question resolved at review: the token cannot leave the grammar
  (it is the assembler's join token) and -e/REPL need the one-liner
  form; user-typed `in` stays legal, scripts steer to block lets.


## Fix the findings — let-RHS commands, Seq.pairwise, blank-line attribution (2026-07-18)

Same-day fixes for the protocol's day-zero friction trio. 398 tests;
skill-doc blocks extended and green; timing holds.

- **Top-level let RHS admits command mode** (`let files = git ls-files`;
  `| complete` chains bind the record). The probe battery caught a
  cliff the naive version shipped with: the command grammar ate
  `in h` as argv in `let h = git log in h` — the exact in-eating
  hazard that keeps `let ... in` expression-only. Fixed by an
  `in`-bareword stop in the let-RHS arg parser (quote "in" to pass the
  word); statement-head commands keep bareword `in` untouched. The
  splice-defaulting soundness note re-derived and updated (commands
  can now sit under a generalizing let; eager splice binding keeps it
  sound — no lambda can enclose a command line).
- **Seq.pairwise** returns `Pair<'a>` records ({ Fst; Snd }), the
  Group<'k,'v> precedent. TUPLES were considered and deferred at user
  review: tuples without destructuring are worse than records, and
  destructuring means pattern binders in let/lambda — its own plan,
  to be fed by the prior-bleed catalog (the protocol will count
  `(a, b)` attempts). Pair does not foreclose it.
- **Blank-line-in-block error names its cause** ("a blank line ends
  the statement; keep the block's lines contiguous").
- Live task rewritten to its original ask (deltas via pairwise +
  let-RHS command): green in 3 iterations — the two new stumbles were
  anonymous-record prior-bleed (exact error, instant fix) and bareword
  word-splitting (bash-identical, quoted). Both logged.


## Agent dogfooding protocol — setup session (2026-07-18)

PLAN-agent-dogfooding executed on branch agent-dogfooding (off the
unpushed bool-branching tip, per user). No checker surface — runs
during the read week by design.

- **skills/weir/SKILL.md**: ~30 divergence rules, six executable
  fenced blocks (3 must-run + 3 must-fail), ALL verified
  against the AOT binary. Draft corrections found by verification:
  the "bare hot path in scripts" line was WRONG (strict mode has no
  bare aliases anywhere, including command-mode stages — probe:
  `git branch | map trim` errors in a script); `cmd ... |> complete`
  was invented syntax (the expression form is the `completed` builtin);
  the booleans [VERIFY] resolved to landed-reality including the
  non-exhaustive-is-a-hard-error rule.
- **The doc-test harness caught the author's own prior-bleed on its
  first run**: `let files = git ls-files` (the let-RHS command-mode
  gap) written reflexively INTO the skill file whose own commands
  section says it doesn't work. Strongest possible validation of both
  the doc-test discipline and the parked let-RHS extension.
- ci/skill-doc.sh wired into ci.yml and ci/local.sh. Scripting policy
  in repo-level CLAUDE.md (the shared /output/CLAUDE.md is
  workspace-agnostic by its own rule, so the policy lives in the repo —
  a placement deviation from the plan's letter, recorded).
- **Live task ran end to end** (tools/test-counts.weir): first check
  failed on a blank line inside a lambda block (the F#-divergence
  biting the agent immediately; error accurate but doesn't name the
  blank line — friction-logged); self-corrected in one iteration, no
  doc re-read, no fallback. Metric 1 day-zero: 1/1.
- == archaeology backfilled into SEMANTICS.
- Day-zero telemetry vs the plan's expected-findings list: none of the
  effectful-edge cluster hit yet; actual first hits were let-RHS
  command mode, Seq.pairwise, and blank-line error attribution — the
  last being adjacent to the plan's wildcard prediction (multi-line
  attribution stranding).

## Amendment: exhaustiveness is a hard error (2026-07-18, same session)

User decision at review: non-exhaustive match = type error, always.
390 tests; battery reshaped. Two things the upgrade forced:

- **Coverage went RECURSIVE, not shallow.** As a warning, shallow
  payload coverage (Some true does not cover Some) was fine; as a hard
  error it would reject genuinely-total nested matches — the existing
  nested-Option test failed immediately and proved it. `missingCases`
  now recurses through union payloads; `Some (Some x) | Some None |
  None` is exhaustive. Precision must match severity.
- **The match-failure runtime class is GONE.** Guarded arms never count
  toward coverage, so every accepted match is total; eval's no-arm case
  is now an unreachable, and SEMANTICS' runtime-failure inventory
  shrank by one. The old "still evaluates when an arm hits" and "fails
  at runtime" pins were deleted with it — this NOTES line is their
  tombstone.
- The "match on X needs a catch-all" error arm turned out defensively
  dead: refutable patterns on non-union/non-bool scrutinees are all
  rejected earlier by checkPattern. Kept as defense, noted here.
- Warnings channel now carries advisory findings only (unreachable
  arms); the -e/runner surfacing from earlier today pins on those.

## Bool branching — if/then/else, bool patterns, when-guards (2026-07-18)

Part 2 of PLAN-read-booleans-overflow, executed with the READ.md gate
explicitly waived by the gate owner (user go on 2026-07-18; the gate
remains theirs — flagged, not forgotten). 391 tests; battery +9 pins;
timing holds; tripwires ran with the suite (checker-touching session).

- **EIf is a dedicated checker arm**, not a match desugar — chosen for
  error quality: "an if without an else is unit-valued; this
  then-branch is string — add an else" beats a synthesized-unit-arm
  mismatch. Row merge across branches is the match-arm discipline
  (else checks against then's inferred type) — pinned including the
  subtlety that a branch-merged row conflict (Name vs Bytes at one row
  var) is LEGAL pre-discharge and errors at discharge; my first pin
  expected the error too early and the checker was right.
- **Guards**: arms are (pattern, guard?, body); guard checks bool under
  the arm's bindings; guarded arms never count toward exhaustiveness or
  reachability. Grammar: `-` gained notFollowedBy '>' so guard
  expressions can precede `->` — a latent OPP collision that only
  guards exposed.
- **Bool patterns** pre-bind an unresolved scrutinee (defaulting
  precedent); exhaustiveness knows true/false coverage.
- **Found: warnings were silently dropped by -e AND the script
  runner** — only the REPL surfaced them. Exposed by a warning-less
  non-exhaustive bool match in -e. Fixed in-session: both surfaces
  print located warnings to stderr; warnings never block execution.
  e2e-pinned. (Pre-existing since scripts landed.)
- **Shadowed-cd hint fixed** (the Part 2 rider): command-callable heads
  never fall to expression mode, so the hint now excludes them instead
  of claiming "expression mode" for cd lines.

**Dogfood ledger** (user directive: weir over python for session
tooling; friction = findings):
- Used weir scripts for: the bool-branching smoke battery, and a
  doc-staleness probe (File.read |> where (Str.contains ...) — reads
  naturally). Both worked first try apart from findings below.
- FRICTION 1 — precedence trap: `if xs |> Seq.length == 2 then` parses
  as `xs |> (Seq.length == 2)` (`==` binds tighter than `|>`,
  F#-consistent). The error surfaces as "expected seq<'a> -> int" at
  the pipe — correct but puzzling. Candidate: a targeted hint when a
  pipe RHS is a comparison of a function. Filed, not fixed.
- FRICTION 2 — no `fail`/`exit` builtin: a checking script cannot
  deliberately exit nonzero with a message; the workaround
  (`if bad then cmd "sh" ["-c"; "exit 1"] |> print`) is a paragraph
  where `fail "reason"` is the want. First-order candidate for the
  next library session; `fail : string -> unit` raising a located
  runtime error would also serve `printerr`'s parked use case.
- FRICTION 3 — python still required for: multi-line context-sensitive
  file surgery (no multi-line strings, no regex — regex is already
  parked as its own plan). Single-line filters/maps are fully covered.

## Remove measures — the evidence-standard case study (2026-07-18)

Units of measure are removed from weir. This entry is written BEFORE
the deletions (plan order) so every deletion references it; it is the
project's best case study of the evidence standard and should be
readable in one place.

The full arc, dated:
- Spike 2: measures land; `f.Size > 1<mb>` is the acceptance test and
  the UoM showcase. Advisor calls extensible measures "the single most
  compelling reason to do this over Nushell" — a claim the record never
  backed; logged here as advisor error.
- Rules doc: measures are nominal exact-match tags; no algebra;
  `f.Size * 2` a type error, flagged as "known ergonomic cliff, top
  backlog item."
- Measure algebra CANCELLED (library-phase review): zero dogfood
  findings in the ledger; the cliff never materialized. The
  `no_unit_algebra` tripwire made permanent (now retired below).
- ls-Size-always-0 bug: the `int<mb>` field truncates sub-megabyte
  files to zero — the measure *causes* a wrong-answer incident and
  forces the two-field Bytes/Size workaround. First shape change driven
  by dogfooding, and it cut against the feature.
- `Seq.sum` ships bare-int-only (no measure variables exist); measured
  ints silently excluded from aggregation.
- Range literals session, stop-and-report: `[1<mb>..3<mb>]` needs a
  Ty-level measure variable — a new variable kind through every audited
  checker arm. Third checker customer for machinery serving a feature
  with zero organic usage.
- 2026-07-18: grep standard applied — measure literals outside tests
  approximately zero — REMOVED. The queued measure-variables
  re-blessing is cancelled with a pointer here.

What this entry is the tombstone for (retire-loudly rule): the
`no_unit_algebra` tripwire, the gb-vs-mb conflict pin, every §4
checklist row and its tests, the measured-range pins from the range
session, and FileRow.Size (the truncating field the incident created —
`Bytes`, bare int, is the survivor; field names now carry quantity
semantics).

The named successor question: `f.Bytes > 1048576` will feel worse to
write than `f.Size > 1<mb>` — that feeling is DATA, the display/
conversion want surfacing honestly as an ergonomic gap instead of
hiding behind a type tag. Evidenced answers are cheap and type-free
when dogfooding demands them: multiplier builtins (`mb : int -> int`)
or underscore literals (`1_048_576`). Neither ships with the removal.

Consequences named: the pitch loses "extensible typed measures" and
keeps the stronger claim (sound static HM+rows, zero runtime type
checks, in a shell); `sortBy`'s runtime scalar-key check is again the
ONLY qualified-types customer, and the type-class conversation returns
to parked-until-customers with this arc as the precedent for
speculative checker machinery; the read anchor moves to the
post-removal commit and TRANSCRIPTION.md regenerates once, smaller
(§4 retires; measure cases vanish from bind; the splice rule
simplifies).

## Range literals (2026-07-18)

Mini-plan session (ranges taken from the comprehension decomposition;
for-yield sugar and Seq.collect stay parked with reopen criteria in the
plan). 375 tests; battery +3 pins; timing holds. No checker arm added —
the literal desugars to a `Seq.range` application before checking, as
the plan modeled.

**Deviation, stop-and-reported mid-session**: the DECIDED
measured-ranges bullet ("measure-polymorphic via the same scheme shape
as other Seq members") assumed machinery that does not exist. A `∀a`
scheme would also accept strings — weir has no measure variables or
int-constrained type variables — and the actual library precedent is
`Seq.sum : seq<int> -> int`, bare-only for exactly this reason. Shipped
the sound subset: `Seq.range` is monomorphic bare int; `[1<mb>..3<mb>]`
is the ordinary measure-mismatch error, pinned as a named limitation.
Measured ranges need re-blessing with either a Ty-level measure
variable or a checker arm.

Session decisions the plan left open, as resolved:
- Range-vs-list disambiguation is a bounded backtrack: `attempt` over
  the first simple term + `..` — one term deep, not exponential; noted
  per the plan's lookahead-vs-backtrack question.
- Zero literal step errors at PARSE time (failFatally in the desugar),
  slightly earlier than the plan's "check-time" — same guarantee
  (before anything runs), reported for precision.
- The negative literal needed for descending steps exists ONLY in
  range positions (`negIntLit` in rangeTerm) — no unary minus leaked
  into the general grammar.
- `..` never touches command mode (expression-land parser only);
  `cd ..` composed with a range in the same script is e2e-pinned.
- Field access in endpoints wraps fieldSuffix in `attempt` so the
  first dot of `..` is never eaten as a field dot — the `1.` /
  `1..` boundary the plan flagged (`1.` remains an error shape,
  pinned by the F#-negatives).

## Fix: |-inertness is statement-level only (2026-07-18)

Corrective session per the blessed fix plan. 358 tests; battery +2
pins; timing holds. Not a checker change (assembler only) — no tripwire
re-run needed; standard suite + battery + timing are the coverage. The
assembler diff came in at 4 lines against the plan's ~2-line model
(the `|` branch needs its own two-case match: error at-or-left, plain
when deeper) — within the ≤10 stop-and-report budget.

Decision archaeology, as blessed: the block-lets session shipped
`|`-headed lines as unconditionally inert to the pending-let stack,
justified by "match-as-let-body works with canonical arm style for
free." The justification rested on an invalid example — arms dedented
to or past their binding's indent, a shape F# *rejects*. The valid F#
shape has every arm deeper than the pending `let`, which the plain
indent rule already handles with zero special-casing: block bodies
never needed `|`-inertness at all. As shipped, the rule over-accepted a
join F# would reject at parse — cutting against the F#-fidelity
direction chosen in the same session. Caught by the user knowing F#'s
actual grammar; advisor drift, not implementation drift.

Corrected rule: `|` is inert only while the stack is empty (the two
statement-level customers: column-0 pipeline continuations under a
command line, column-0 match arms outside any binding). With a binding
pending: deeper = plain continuation; at-or-left = needs-a-body naming
the binding line.

**Process rule generated**: pin batteries for grammar/assembler rules
must include F#-rejects-this NEGATIVE cases, not only weir-accepts-this
positives — invalid-example drift is invisible to positive-only
batteries. The six pins of this session are the template.

## Block lets — F# light syntax at the assembly layer (2026-07-18)

User decision closing the `let ... in` thread (opened during the spikes,
kept then; reopened at the interpolation review): **do it like F#** —
implicit `in` at the offside boundary, not the austere removal. 353
tests; battery +2 pins; timing holds.

- **Zero parser/checker/evaluator changes.** F# implements light syntax
  by token insertion in the lexer; weir's logical-line assembler is the
  same layer. A continuation line starting with `let ` pushes a pending
  binding (indent, line); the next line at the SAME indent closes it by
  joining with " in " instead of " ". The single-line grammar sees the
  explicit form; ELet and envFreeVars serve unchanged — the austere
  option (kill ELet, kill envFreeVars) was assessed and set aside in
  favor of F# fidelity; that analysis is on record in the conversation
  and the read shrinkage it promised is forgone knowingly.
- Stack invariant: pending indents strictly increase, so each closing
  line pops at most one binding; a dedent past a deeper pending let is
  the "needs a body" error naming the deepest line — same verdict F#
  gives the shape.
- `|`-headed lines were shipped unconditionally inert to the stack —
  corrected the same day (see the fix entry above): inertness is
  statement-level only; block bodies are handled by the plain indent
  rule.
- **Span translation needed nothing**: Segments already carry
  per-segment joined offsets; " in " just makes the offset arithmetic
  4 instead of 1 (`typo at 2:24` pins it).
- Explicit `let ... in` stays legal — F#'s verbose-syntax analog and the
  only binding form in the line-based REPL/-e. Blocks are bindings + one
  result expression; effect-sequencing inside blocks (ESeq, unit-checked
  non-final lines) is backlog #0, revive on dogfood demand.
- Blank-line-ends-statement is retained and now documented as a named
  F# divergence (a blank inside a block is a "needs a body" error).

## Review amendments: [ heads nothing; the sh builtin is gone (2026-07-18)

Two user decisions at review of the unit-print session. 346 tests;
battery reshaped (+3 pins, −1 obsolete); timing holds.

- **`[` never heads a command.** The capture bug (line-head string list
  → bare `[` token → /usr/bin/[) is fixed at the head rule: `[`-initial
  words fail to expression mode; `^[` is a hard error naming
  `cmd "[" [...]`. `[` stays ordinary inside arguments (`[m]arker`).
  Pinned with realResolver (real PATH) and in e2e with the natural
  spelling that used to break.
- **The `sh` builtin is removed.** The statement rule had exposed it:
  a library function pretending to be a shell — bare effect lines
  needed `|> print`, `| complete` could never reach it, and it deferred
  resolution past check-everything-first. The external `/bin/sh` does
  everything with zero special-casing: `sh -c "..."` is command mode
  (exempt, streaming, completable — `sh -c "exit 7" | complete` works
  now, which the builtin structurally never could); expression
  positions use `cmd "sh" ["-c"; "..."]`. 27 test sites migrated
  mechanically; the parked "effect-only sh ergonomic" demand dissolves
  (effect lines are command mode now). One migration self-inflicted
  wound: the sed-style regex mangled `./build.sh \"--flag\"` inside an
  *expectation string* — the sed-migration lesson from the module
  session, relearned on a smaller stage.
- SEMANTICS updated with decision archaeology in place (hatch bullet,
  dead completed-boundary bullet removed, statement-rule clause
  superseded-noted, `[` rule added).

## Unit, print, and the statement rule (2026-07-18)

PLAN-unit-and-print Session 1, complete. 346 tests; battery +9 pins;
timing pins hold (7/20ms medians).

- **`unit` cost what the plan predicted**: a leaf. TUnit/VUnit/`()`/
  `unit` tySyn, equatable, invisible interactively (REPL, `-e`, and
  `let x = ()` all show nothing — pinned in e2e). No read-path arm
  touched; TRANSCRIPTION got the addenda.
- **The statement rule**: parser reifies its mode decision as
  `SCmd`/`SExpr` (new Stmt case — the classifier is a pattern match, so
  the removed form-2 exemption *cannot* be reintroduced without a
  parser change), and the runner's `discardError` gates pure statements
  on unit. `| complete` chains classify as commands for free because
  classification happens at parse time, before the desugar to
  `completed` application.
- **print**: sentinel-scheme guard (∀__print. __print → unit is
  unforgeable — ctx names are aN/rN) gives bespoke typing in applied/
  piped positions and defaulted `string -> unit` as a bare value;
  shadowing falls through to normal rules by construction. Renderer
  byte-identity with the retired statement printer is by shared
  function (`Eval.writeLines`), pinned by the adversarial e2e case
  (empty strings, embedded newline).
- **`File.write`/`append` → unit**; the path-return stopgap retired.
- **Found, not fixed (pre-existing, out of session scope): line-head
  string lists enter command mode.** `["a"; "b"] |> print` at a line
  head: quotes end the bareword, the head token is bare `[`, and
  `/usr/bin/[` is a PATH hit → ECmd. Ints escape (`[2;` fails the PATH
  probe). Migration workaround: `let`-bind the list. Needs a decision:
  may `[` head a command? (bash's `[ -f x ]` idiom vs list literals.)
- Effect-only `sh` lines (`sh "mkdir x" |> print`) read as predicted —
  noted in the plan as the first candidate demand for the discard
  hatch; no dogfood complaint yet.

## String interpolation (2026-07-18)

User-requested off-plan feature (dogfood follow-up to the example script's
`echo tracked changes: (...)` bareword question). `$"... {expr} ..."`,
F#-style. 331 tests.

- **One rule, two splice kinds**: holes reuse the command-splice typing rule
  verbatim — str/int/bool, unresolved defaults to string — via a shared
  `checkScalarSplice` extracted from the ECmd arm (behavior unchanged there).
  Eval-side the same consolidation: one `scalarString` renderer now serves
  both command argv and holes (was duplicated at two TECmd sites).
- Works as a command argument (`echo $"n={x}"` is one argv entry, never
  re-split) — cmdArg tries `interpLit` before `spliceVar` so `$"` isn't eaten
  by the `$name` path. `{{`/`}}` escape braces; no format specifiers.
- `$"{n}"` closes the filed int→string gap; the example script now uses
  interpolation throughout (including a `Dirty of int` payload rendered in a
  match arm).
- Checker arm added post-read-anchor: TRANSCRIPTION.md gained a "post-anchor
  addenda" section so the pending READ.md scope stays exactly d12aefd.
- e2e battery: interpolation + brace escapes, and the one-argv-entry pin,
  against the AOT binary.

## Read prep — transcription, read order, composition probes (2026-07-17)

Part 1 prep of PLAN-read-booleans-overflow, complete. 317 tests.

- **TRANSCRIPTION.md**: judgment-form rule per checker arm, anchored to d12aefd file:lines. Five arms flagged as resisting single-rule transcription — these are pre-read findings per the plan: (1) `instantiate` does two jobs (rename + Rows installation); (2) `dischargeRow`/`mergeRows` substitute-before-recurse is load-bearing for termination and gained a substParams premise in the generics session; (3) `checkSpine` braids three jobs around the piped-first semantic core; (4) the EField TVar-upgrade arm has a side-effectful premise; (5) the generalization computation exists at two code sites (infer-ELet and check-ELet — drift risk, not unsoundness).
- **READ-ORDER.md**: the a–g path with per-step purpose, the three formally-reopened checklist items (§1.1 occurs-through-TNamed, §1.5 the == fix as rule-not-patch, §3.1/§3.3 freshening now covering ctor schemes and module members), and the verdict protocol ending in READ.md.
- **Composition probes** (six, all green): generic-ctor-in-row discharge + conflict; envFreeVars through TNamed-inside-row (the audited×audited composition); occurs through two constructor layers under a row field; module-member freshening in one expression; Option-field row deep-copy across sibling discharges; and the advisor-flagged short-circuit gap resolved — pins existed (div-by-zero proxy, operators session) and a real-process spawn-count pin is now added beside them.

The gate is now the human's: Part 2 (booleans) starts when READ.md exists.

## Dogfood sweep — assistant-driven error hunt (2026-07-17)

Four probe batches against the AOT binary. Fixed this session:

- **int is now int64 end-to-end** (the big one): weir ints were int32, so `ls` on a >2GB file reported negative `Bytes` and `where (fun f -> f.Bytes > 0<b>)` silently returned zero files — wrong answers on exactly the data the filter exists for. Verified fixed: 3GB file → `3221225472<b>`, filter finds it, `2147483647 + 1 = 2147483648`. Sweep was mechanical (F# literal inference absorbed nearly all sites).
- Nested union display gains parens: `Some (Some (Some 1))`, not `Some Some Some 1`.
- `cmd "" []` reports "cmd: empty program name" instead of leaking a raw .NET exception.
- The fmt e2e entry asserted on filesystem enumeration order (`.gitignore` first) — latent flake exposed by adding files; now deterministic.

**Filed, not fixed (language decisions, not sessions)**:
- **No boolean branching exists**: no `if`/`then`/`else`, and `true`/`false` are not legal patterns (`match b with | true -> ...` is a parse error). There is currently no way to branch on a bool. Biggest known language gap; needs a chosen design (if-expression vs bool patterns vs both) — top of the next plan's input.
- Shadowed-`cd` diagnostic claims "expression mode" on a line that actually took the command-callable arm — minor hint inaccuracy.
- `Str.split "" s` returns `[s]` (.NET semantics) — fine, but undocumented.

Verified healthy: unicode strings, CRLF scripts, `/dev/stdin` as a script, `stdin` builtin one-shot, empty-everything edges, negative take, match-arm shadowing of builtin names, `Seq.groupBy` over row projections, `into`, porcelain garbage errors, `cd`-to-file error.

## Dogfood fix — ls Size was always 0 (2026-07-17)

Diagnosis: `Size : int<mb>` = bytes ÷ 1,048,576 truncated — the Spike-2 decision that made `f.Size > 1<mb>` work against real files; sub-megabyte files (i.e. nearly everything in a source tree) round to 0.

Fix shape, constrained by the measure system: conversion doesn't exist (measure algebra dropped), so a field's unit is permanent. Switching Size to bytes would kill the UoM showcase filter. Instead FileRow gains `Bytes : int<b>` as ground truth alongside the coarse `Size : int<mb>`; `ls` populates both from the real file length. Field-set-sensitive fixtures updated (from-json fixtures, to-json output, completion expectations — record literals and adapters match exact field sets). 308 tests, e2e green.

First shape-change driven purely by dogfooding — and a note for the future measure-conversion discussion: this two-field workaround is exactly the pattern conversion would delete.

## Modules Session 3 — the multi-line gate: CONTINUE, and shipped (2026-07-17)

308 tests; e2e + timing green. Gate verdict in DESIGN-multiline.md; the kill criteria were beaten so thoroughly the implementation shipped in the gate session.

- **The reframe that won**: no expression-level offside machinery — weir's line-oriented grammar means indentation-based multi-line is *logical-line reconstruction*: a ~90-line script-runner pre-pass joins continuations, and the existing parser consumes each logical line unchanged. Kill criteria results: parser-lines changed **0** (criterion: <150); expression suite green by construction; mode-decisions-in-continuations structurally impossible; timing unchanged.
- **Gate finding from the first live script**: the indent-only rule missed F#-canonical match arms (`| Some n -> ...` at column 0). Fixed on principle, not special case: no statement can begin with `|`, so pipe-headed lines are unambiguously continuations at any indentation — which also admits shell-style unindented pipeline continuations.
- **Error mapping is the real feature**: per-segment source tracking translates type-error spans to physical `file:line:col` — `multibad.weir:3:18: type error: FileRow has no field 'Nmae'. Did you mean 'Name'?` points at the continuation line, not the joined blob. Parse errors attribute to the head line (documented limitation, parked with REPL continuation prompts).
- Documented non-features: in-less nested `let`, indentation-as-scope, multi-line REPL. `let x = <command>` remains expression mode — orthogonal decision, unchanged.
- Session 4 as planned is dissolved: its scope was consumed here; the residue (parse-error column mapping, REPL continuation) sits on the parked list for dogfooding to prioritize.

## Modules Session 2 — script runner, strict-by-default, fmt --qualify (2026-07-17)

303 tests; tripwires green; e2e battery grown by five script entries, all green on the AOT binary; timing unchanged (7ms/18ms). weir runs shebang scripts.

- **Check-first works and is pinned**: touch-then-type-error script leaves no file, exits 1. The install-then-use divergence and its sh escape hatch are in SEMANTICS.
- **Strict by default** via a second builtin TypeEnv (bare aliases removed; modules/session/process names stay); `#loose` opts out; misplaced directives error with location. The multi-home moved-name hint ("use Option.map or Seq.map") landed as part of strict-mode ergonomics.
- **In-session decisions, documented**: shell-shaped statement output (strings/string-seqs raw — scripts compose with pipes; let/type silent); stdin inherit-unless-consumed (already cmd's behavior — stated, not built); `args`/`stdin` script-only.
- **fmt --qualify**: span-precise AST rewrite (EVar spans, right-to-left per line), splices and fields untouched, `#loose` dropped on success — the single-home guarantee (trial resolution deferred, Option excluded) makes it a table lookup, no type direction needed. Live: 8 names qualified across expression and command-mode segments, output runs strict-clean.
- Comment stripper is string-aware (`//` inside quotes preserved) and unit-tested; applied at the script boundary.
- Fmt splice guard was off by one on first cut (spanned wraps the `$`); caught by the battery.

## Modules Session 1 — builtin modules (2026-07-17)

297 tests; tripwires explicit-green; e2e + qualified-pipeline entry green on the AOT binary. `Seq`/`Str`/`Option` live; the migration commit landed in one dose.

- Mechanism as designed: `TypeEnv.Modules` (member schemes), one `EField` arm (value-shadow checked first, instantiate on hit), mangled flat names at runtime — eval untouched. Bare-module and unknown-member errors carry guidance; exact-name moved members hint their qualified home.
- **Caught my own version of the deferred conflict**: the bare-alias derivation initially collected from all modules, so `Option.map` silently overwrote bare `map` (Map.ofList last-wins) — precisely the collision the plan deferred trial resolution to avoid. The Option-qualified-only rule now applies to the derivation itself; the failure mode was 31 red tests, instantly visible.
- Three-way precedence pinned exactly as the advisor specified (`let Seq = {...} in Seq.map` → ordinary field error). Completion gained the module branch (`Seq.<TAB>`); resolvers/diagnostics treat module names as known (mode decision: `Seq` at line head is expression mode).
- Bonus from the smoke: qualified members work as command-mode pipe segments (`git branch | map trim | ... | Seq.length`) — the dot makes the head non-ident-like, falling through to the expression segment and the module arm. Free, but now observed and welcome.
- `Seq.length` added (didn't exist); `length` qualified-only per the interim rule.

## Library Session 3 — the Option sweep (2026-07-17)

288 tests; e2e + timing green on the AOT binary. Quick session, as planned.

- `tryHead : seq<'a> -> Option<'a>` and `tryToInt : string -> Option<int>` — the breaking change taken cleanly (dogfood history had zero uses; the two test sites migrated).
- Deferred Option customers landed: `tryFind` (data-last), `tryIndexOf`, `substring start len subject` (raising, bounds in the message).
- Helpers per the plan's recommendation: `defaultTo` and `mapOption` — the idiom's other half. Pinned verbatim: `ls |> tryFind _.ReadOnly |> mapOption _.Name |> defaultTo "none"`.
- SEMANTICS partiality convention flipped from INTERIM to FINAL: raising name / try-prefixed Option sibling. The 0-or-1-seq idiom retired without ever becoming case law — the interim marker did its job.
- Note for the record: session based on the local session-2 tip because this container has no ssh (origin/main updates have always come from the user's shell); squash-merge content-equivalence makes the PR diff come out right.

## Library Session 2 — generic unions and records (2026-07-16)

281 tests; tripwires explicit-green; e2e + timing green on the AOT binary (7ms — generics cost nothing at startup). Option/Result live as prelude declarations; `groupBy` landed as the generic-records validation customer.

**The finding that matters for the read**: the adversarial battery caught `==` violating its own spec. SEMANTICS has said "unifies operands first" since the operator session — but the implementation compared resolved types syntactically (`a = b`), which passed monomorphically because top-level vars got pre-bound by the retry cases. `Option<'a> == Option<int>` exposed it instantly. Fix: genuine unify-then-equatable. Fourth claim-vs-behavior instance, and the first where the claim was in our own spec rather than a plan — the doc was ahead of the checker.

Design decisions (per plan, plus in-session):
- Representation unified: `TNamed of name * args` (no parallel TApp); `seq` stays structural-builtin. Defs gain `Params`; one `substParams` helper feeds patterns, fields, discharge, and equatability.
- **Records came along** — Session 1's groupBy deferral was the demonstrated need; record literals freshen params per literal and infer args by unification.
- **Deviation from the plan's illustrative grammar**: no tuple payloads (`Case of 'a * 'b`) — weir has no product types; single payload, wrap in a record. Documented.
- Constructor schemes = ordinary `Scheme` with Forall = params; instantiation is the existing freshen-on-use machinery, so the §3 reopening rides audited code (pinned: `let s = fun x -> Some x` used at int and string; polymorphic `None` at two instantiations; occurs through constructor args → infinite-type).
- Prelude = weir source strings through the normal decl path, embedded in the binary (files would break the 6ms single-file story).
- `from json`/`to json` guard: monomorphic records only.

**Human-read targets, unchanged**: the `TNamed` pairwise arm in `bind` + `substParams` call sites, and the constructor-scheme construction in `checkDecl` — judgment-on-paper per plan.

## Library Session 1 — strings, tryHead, sortBy (2026-07-16)

265 tests; e2e battery + timing guard green on the AOT binary; the done-when dogfood task runs natively: `git branch | map trim | where (startsWith "feature") | join ","` — point-free, exactly the data-last payoff the plan pinned.

- 14 string builtins, all data-last (needle first, subject last), `strLen` per the collision decision. `split` keeps empty entries (documented).
- Seq additions: `tryHead` (interim 0-or-1 seq, marked for Option migration in SEMANTICS), `isEmpty`, `sortBy` (lazy via Seq.delay; scalar keys only, enforced at runtime and documented — no comparability constraint exists in the type system).
- **Scope finding: `groupBy` deferred with reason** — its return shape `{ Key: 'b; Items: seq<'a> }` requires generic records (Session 2 machinery); RecordDef fields are concrete types today. A string-keyed fake was rejected as case law in the wrong direction.
- Zero checker changes, as planned. Member-access-on-primitives logged as a candidate, not built.

## Session 3 addendum — advisor probes + institutionalized e2e rule (2026-07-15)

- **Deadlock probe (highest-value check available): PASSED** — 100KB stderr before stdout closes, under `| complete` → returns promptly. The concurrent drain (stderr Task starts before the stdout loop in `Proc.complete`) is now empirically confirmed and pinned in ci/e2e.sh with a timeout guard.
- **Composition probe: clear error, not silent** — `yes hi | grep hi | complete` hits the marker rule's parse error. Erroring over wiring stays the choice; pinned for the ext→ext shape specifically.
- **Standing rule institutionalized** (three claim-vs-behavior gaps: porcelain quoting, ext→ext piping, stderr passthrough): every done-when grammar shape or boundary behavior gets an eval test in ci/e2e.sh against the AOT binary. Five new e2e entries this session.
- **Doc lines added**: complete/collect force completion (`yes | complete` hangs by design); sh-streams can't be completed (sh = POSIX semantics at the price of POSIX error opacity; `shc` is the future shape if demanded); commit-to-command-mode as a stated grammar rule.
- **Process note for the record**: this addendum's first probe run tested the wrong code — the working tree had silently switched to main (session-2 state) between turns, and `publish.sh` faithfully installed it. Both probe failures were phantoms. Cost: fifteen minutes; lesson: `git log -1` before trusting any installed-binary probe.
- Also discovered while probing: `\0` isn't a weir string escape, so shell one-liners embedding octal escapes need restructuring — fine, but the parse error could name the offending escape. Micro-item for the dogfood list.

## Ergonomics Session 3 — complete, collect, and the stdin gap (2026-07-15)

251 tests, tripwires explicit-green. Three findings beyond the plan's scope, all fixed:

1. **ext→ext piping never type-checked** — command-mode session 3's battery pinned `git log | grep x`'s *parse shape* but nothing ever checked or evaluated one; `ECmd` had no stdin path ("right side of a pipe must be a function"). Fixed: dedicated `EPipe`-into-`ECmd` rule (left stream must be `seq<string>`), eval wires it into the child's stdin. `yes hi | cat | first 2` now works. Lesson repeated from the depth audit: parse-shape tests are not behavior tests.
2. **stderr was never passthrough** — the plan said "keep passthrough as default," but the implementation redirected stderr and only read it after stdout EOF: swallowed on success, and a latent deadlock (chatty-stderr child fills the pipe weir isn't reading). Now genuinely passthrough; failure messages lost the stderr suffix (it went to the terminal live instead — better).
3. **`failFatally` inside `attempt` is a no-op** — the marker-misuse diagnostic vanished because `stmtWith` wrapped the command line in `attempt`. Fix doubled as UX: once the first segment parses as a command, the line commits to command mode (head-decision fallback still backtracks cleanly), so command-line errors read as command-line errors.

The headline features:
- **`complete`/`completed`**: fallback design chosen and documented — command-suffix desugar to a plain builtin call (`grep x f | complete` → `completed "grep" ["x"; "f"]`), NOT a type-level process-backed-stream distinction (which wouldn't survive `where`/`first`). Zero checker changes. `Completed = { ExitCode; Stdout; Stderr }`, never raises; `grep nomatch f | complete |> _.ExitCode` → 1. Stderr captured only here.
- **`collect`**: eager materialization at application; pinned by the inverted liveness test (pwd snapshot survives cd) and a spawn-count test (1 spawn with collect, 2 without).

Backlog after this session: measure algebra is the last standing item.

## Ergonomics Session 2 — command-callable builtins + cliff diagnostic (2026-07-15)

`cd /work`, `cd ..`, `cd ~`, bare `cd` all work at the prompt; `ls -la` now yields a targeted hint instead of a bare subtraction error. 239 tests, tripwires re-run explicitly (green).

- **Design delta vs plan, in weir's favor**: the plan expected the checker to gain a builtin-desugar arm; in our architecture the desugar lives in the *parser* (builtin head + barewords → ordinary `EApp` with string literals), so splice typing and checking are inherited for free and the checker's only change is the arity-message improvement ("'cd' takes at most 1 argument(s), but got 2" — computed generically from the head's type, so `double 1 2` got better too; adversarial test updated).
- Mode decision gains the one arm, ordered before the known-name check (cd IS a builtin): forced → external; command-callable → builtin segment; known → expression; PATH hit → external; else fall through. Conservative-by-construction preserved.
- Bare `cd` desugars to `cd "~"` (case law while the set = {cd}); `^cd` verified absent as an external on Ubuntu → parse-time command-not-found, pinned.
- **Cliff diagnostic** (`Diagnose.fs`, pure, unit-tested): fires only on parse/check failures — the first smoke run exposed a false positive on `cd /wrok` (a *runtime* error on a valid command-mode line claiming "this line is expression mode"); gated on span presence. Bareword tails only hint when the head also exists in PATH (so `where p` stays quiet, a shadowed `git` doesn't).
- Human-read target: the head-decision function in `commandSegment` (Parser.fs) — one new arm.

## Ergonomics Session 1 — CI + hint hygiene (2026-07-13)

- **CI workflow** at `.github/workflows/ci.yml` (GHA syntax; Codeberg's Forgejo Actions reads `.github/workflows/` as fallback — move to `.forgejo/workflows/` if the runner wants it): test suite twice (flake detection — the workload that caught the Session-3 Session.Cwd race), AOT publish via `publish.sh`, then `ci/e2e.sh` + `ci/timing.sh` against the native binary.
- **`ci/e2e.sh`**: the Session-4 battery as a script (expression eval, argv-literal, splices, cd+porcelain+staged on a temp repo, `^ls`, forced-unknown rejection). Shared by CI and local runs; verified green locally.
- **`ci/timing.sh`**: pins the Session-4 medians (expression ~6ms, spawn ~14ms) with thresholds 18/42ms (>2x pinned + CI-runner headroom, env-overridable). Done-when verified both directions: green on the real binary, **trips on a deliberate 20ms-wrapper slowdown** (33ms > 18ms → exit 1).
- **Did-you-mean cap**: audited — all seven hint sites (fields, ctors, unbound vars, unknown types x2, decl types, PATH) route through the single `Types.didYouMean` with the <=2 filter; pinned by two new tests (checker-side and PATH-side distance-3 names get no hint).

224 tests. CI itself can't be exercised from this container — first push will tell; the scripts it runs are verified.

## Command-mode Session 4 — integration, timing, dogfood re-entry (2026-07-12)

**E2E on the AOT binary**, all green: `cd` into a temp repo; `git status --porcelain | from porcelain | where _.Staged | map _.Path` → exactly the staged file; `^ls` forces past the builtin; `let pattern = "more"` then `grep -l $pattern a.txt b.txt` splices a REPL binding into argv.

**Timing pinned — and one real cost found and removed**: the naive PATH check enumerated every PATH directory on any line whose head wasn't a known name, which taxed even `1 + 2` (head "1" isn't ident-like) at +10ms. Fixed: mode decision now uses `File.Exists` probes per PATH entry (microseconds); the full inventory is enumerated only for did-you-mean hints. Post-fix on the AOT binary: expression lines 6ms median (parity with pre-command-mode), command-mode `echo hi | first 1` 14ms median — spawn-dominated, weir overhead is noise.

**First dogfood finding** (queued as SEMANTICS backlog #3): "nonzero exit raises" collides with grep's no-match-exits-1 convention — a zero-hit filter is currently a runtime error. Policy needed (allowlist / try-combinator / exit-code-as-value), chosen not improvised.

PLAN-command-mode.md complete: all four sessions done. 222 tests.

## Command-mode Session 3 — the mode decision and command grammar (2026-07-12)

Bare `cd` (via `cd "path"`... the *original* two lines) and `git status` now work at the prompt; `git status --porcelain | from porcelain | where _.Staged` is one typed line. 222 tests, expression mode zero regressions (old parseStmt = parseLine with a no-externals resolver — behavior identical by construction).

**Architecture — the parser stays pure**: `parseLine` takes an injected `Resolver` (`IsKnown` from the live env, `IsExternal`/`ExternalNames` from the `Extern` PATH cache), so the mode decision — the new security boundary — is one small `commandSegment` head function over injected facts, unit-tested against a fake PATH. REPL calls `Extern.refresh()` per submission (mid-session installs visible); completion reuses the cache (no per-keystroke stat).

**Mode decision is conservative by construction**: only a PATH *hit* enters command mode; keywords/bindings/builtins shadow PATH (`ls -la` parses as subtraction and fails in the checker — pinned as the shadow demonstration); every other shape falls back to expression parsing, so weird heads (`[1]`, `1 + 2`, quotes) can never accidentally exec. `^prog` forces PATH; a forced miss is a parse-time "command not found" with a PATH-based did-you-mean (cap ≤2 was already in place — the plan's one-liner was a no-op, verified rather than fixed).

**Grammar**: segments split on `|`/`|>` (both = pipe in command mode), each segment re-entering the mode decision — `git log | grep x | first 2` flows external→external→expression. Expression segments parse with a pipe-free OPP (the dual-OPP pattern returns, but without the match-arm ambiguity this time — segment pipes and expression pipes mean the same thing, so the split is semantically invisible). ECmd desugars to a checked node typed `seq<string>`, evaluated via Session 2's `Proc` machinery (extracted from Builtins to break the Eval→Builtins cycle) — lifecycle guarantees inherited, pinned by a command-mode survivor test.

**Splice typing rule** (the other human-read spot): args infer, then must resolve to string/int-any-measure/bool; unresolved defaults to string; rendered as single argv entries. Records/seqs/functions rejected with "command arguments must be strings, ints or bools". Real-exec pinned: `echo hi (1 + 2) true` → "hi 3 true"; `echo ; rm -rf x` emits the string.

**Flake found and fixed**: `Session.Cwd` is global mutable state and Expecto parallelism raced cwd-mutating tests (a parallel finally-reset landing between another test's `cd` and its spawn) — exactly the concurrency seam the plan flagged when it said mutate `Session.Cwd`, not `Environment.CurrentDirectory`. The cwd-mutating and process-census test groups are now `testSequenced`; suite ran 3× green.

**Tripwire suite re-run explicitly** (first checker change since the audit — the ECmd rule): green.

SEMANTICS.md: new "Command mode" section — mode-decision algorithm, grammar, splice rule, PATH staleness rule, and the exclusions list (globs/redirects/env-prefix/chaining pass through literally; `let`-lines are expression-only; expression mode never flows back into command mode).

## Command-mode Session 2 — sh/cmd split, Session.Cwd, cd/pwd (2026-07-12)

- `sh : string -> seq<string>` (renamed escape hatch, 17 test sites migrated) vs `cmd : string -> seq<string> -> seq<string>` (direct exec, argv verbatim, no injection class — done-when pinned: `cmd "echo" ["*"]` literal, `sh "echo *"` globs, injection arg inert).
- **Plan-vs-language deltas, resolved in the plan's spirit**: (1) `cmd`'s arg vector required *seq literals* — added `[a; b; c]` (homogeneous, eager-once evaluation unlike pipelines, `[]` polymorphic). (2) `pwd` can't be a plain `string` (env values compute once — stale); it's `seq<string>` via `Seq.delay`, same lazy-value pattern as `ls`; laziness pinned by test (`let p = pwd in let d = cd "/tmp" in p` → `/tmp`). (3) `cd : string -> string` returns the new cwd (no unit type); bare `cd` → HOME deferred to command mode (Session 3). (4) No List type — `List<string>` in the plan is `seq<string>`.
- `Session.Cwd` (module-level mutable, initialized once from the process cwd) is the single working-directory authority: spawn audit confirms exactly one `ProcessStartInfo` site, `Session.Cwd`-set at force time; `ls` migrated off `GetCurrentDirectory`; `Environment.CurrentDirectory` never touched.
- Direct-exec lifecycle duplicates green (no sh in front — tree-kill holds on its own), with the plan's comment noting the exec-optimization analysis doesn't apply.
- `cmd` not-found is a runtime error this session ("command not found or not executable"); check-time PATH lookup is Session 3's mode-decision work.
- SEMANTICS.md: new "Processes and the session" section (sh/cmd ownership line, `&` orphan rule, Session.Cwd rule, pwd/cd shapes, tripwire cross-ref); literals bullet gains seq literals.

203 tests.

## Command-mode Session 1 — process lifecycle (2026-07-12)

Plan's reproduce came up GREEN, for a documented reason: the prescribed fix (`Process.Kill(entireProcessTree: true)`) has been the implementation since Spike 5. The compound test (`yes | grep` under `first 3`) confirms tree-kill reaches sh's forked pipeline children; zombie tests (50 completed + 50 killed streams) confirm no `<defunct>` accumulation. First red was a probe bug, not a weir bug: `pgrep -f MARKER` matched the probe's own `sh -c` wrapper — fixed with the `[m]arker` bracket trick.

Real changes shipped:
- Teardown hardened: unconditional `Kill(true)` attempt (swallowing already-exited) + `WaitForExit()` reap — reaping is now deterministic instead of relying on the .NET runtime's SIGCHLD reaper, and the `HasExited` guard race is gone.
- Tripwire pair kept with the plan's comment: simple case passes without tree-kill (sh execs single commands); the compound test is the real guard; Session 2's sh-removal changes the analysis.
- Known unreachable case, documented not fixed: `sh`-backed `cmd "daemon &"` — sh exits, the orphan reparents to init, and no tree-kill can reach it. That is `&` semantics (user owns backgrounded processes); becomes a Session-2 rules-doc line for `sh`.

No CI exists yet ("run in CI" done-when clause pending infra). 189 tests.

## Rename: fslite -> weir (2026-07-12)

Full content rename: `Weir` namespace/projects (`src/Weir`, `tests/Weir.Tests`, `weir.slnx`), `weir>` prompt, `~/.weir_history`, `usage: weir`, docs. Historical NOTES entries below renamed too (codename swap, not history rewrite). Zero `fslite`/`FsLite` residuals; 185 tests; caret alignment unaffected (derived from `prompt.Length`). AOT binary name follows the fsproj (`Weir`) - republish on next release-shaped work.

## Operator completeness — backlog #1 landed (2026-07-12)

`<>`, `>=`, `<=`, `&&`, `||` as operators; `not` as a `bool -> bool` builtin. 185 tests. Both pre-commitments honored and pinned:
- `<>` inherits `==`'s full equatability path (one rule pattern `("==" | "<>")` — `nats <> nats` rejected with the same message shape).
- `&&`/`||` short-circuit: dedicated eval cases *before* the generic binop case (which evaluates both sides); pinned with division-by-zero as the effect proxy (`false && (1/0 == 1)` → `false`; `true && (1/0 == 1)` → raises).
- Precedence: `||` (2) < `&&` (3) < comparisons (4); all left-assoc. FParsec longest-match handles the `|>`/`||` and `<`/`<=`/`<>` prefix families; the measure-literal `attempt` still wins (`1<mb> <= 2<mb>` parses).
- Var-var `&&`/`||` bind both operands to `bool` (their only typing) — same deterministic-defaulting family as `*`/`/`, noted in SEMANTICS.md.
- The day-one filter shape now works: `ls |> where (fun f -> f.Name <> "tmp" && not f.ReadOnly)`.

SEMANTICS.md updated: operator surface stated as complete, short-circuit promoted from pre-commitment to rule, backlog renumbered (`collect` is #1, measure algebra #2 — still flagged as reopening §4.2 and the `*`-defaulting rule).

## Tripwires, semantics doc, and the two re-aimed read questions (2026-07-12)

Response to the advisor's second pass. Three deliverables:

**1. `Tripwires.fs`** — tests named for the incidental protections, with comments stating which checklist item reopens if the named mechanism changes: funParams-shields-occurs (§1.1, reopens with arrow-var unification), no-unit-algebra (§4.2, reopens with measure arithmetic), no-annotation-syntax (§2.3, reopens with ascription), plus the two generalization pins below. Confirmed empirically along the way: `f.Size * 2` rejects ("expected int<mb>, got int") — the day-one ergonomic cliff is real; measure algebra is the top post-review backlog item.

**2. `SEMANTICS.md`** — the accidental-looking rules written down as language rules: the HOF-inference restriction, the generalization regime (deliberately upgraded from the v0.1 "frozen at definition" rule during the rows work — the advisor is right that this happened without a decision point; it's now a documented decision), measure exactness, `|>`-only, `==`-only equality, laziness/re-enumeration semantics.

**3. The two re-aimed read questions, pre-answered with pins**:
- *instantiate × Rows aliasing (new #1)*: **deep copy, not aliasing.** `instantiate` renames every quantified var — row names included — recursing into the row snapshot (field types renamed first), then installs a fresh `ctx.Rows[r']` entry per use site from the scheme's snapshot, which is an immutable `Ty` inside the env map and is never written after generalization. Sibling instantiations use distinct keys; discharge writes `Subst[r']` only. Pinned by a tripwire whose comment states the failure mode (sibling poisoning), in the dangerous order (use A fully discharged before use B instantiates).
- *envFreeVars transitive reachability*: **covered, structurally.** `envFreeVars` collects vars from `finalTy` of each env entry, and `finalTy` expands row constraints (deep) before `tyVars` runs — so a var reachable only through an env-free parameter's row constraints is still subtracted from the quantifier set. Pinned by a tripwire where `'a` occurs in the enclosing param's type *only* inside its row constraints and a second contradictory use must (and does) error.

172 tests. The human line-read now has its two hardest questions answered-with-evidence and its remaining scope: `bind`/`dischargeRow`/`mergeRows` (verify substitute-before-recurse is structural), then judgment-on-paper for `infer`'s EField/ELambda and `check`'s lambda rule.

## Row-soundness checklist — pre-read probes + implementation map (2026-07-12)

Ran the advisor's checklist probes before the line-read; all pass (167 tests). Map of checklist → implementation for the read:

**§1 Row unification**
- 1.1 occurs/self-application: `fun f -> f.x f` rejects (no hang) — but note *why*: weir never unifies a TVar with a function type at application (`funParams` on an unresolved var → "not a function"), which blocks the standard cycle constructions before `occurs` is even consulted. `occurs` (TVar case in `bind`) covers var-mediated cycles; rows enter `Subst` only via occurs-checked TVar bindings or `dischargeRow`/`mergeRows`, both of which substitute the row var *before* recursing into constraints — that ordering is what makes potential cycles terminate in `bind`. `finalTy` additionally carries a seen-set now (defensive; a cyclic row prints `{ .. }` instead of hanging the formatter).
- 1.2 var-var merge: `mergeRows` binds shared field types (`bind ft2 ft`), never name-unions. Probe: two lambdas' rows merged through a shared arg, conflicting `A` demands → "expected int, got string". Pinned.
- 1.3 intra-lambda: same code path as 1.2 — `EField` on a row var returns the *existing* constraint's type var, so a second conflicting demand collides on that var. Pinned.
- 1.4 closed rows: `dischargeRow` sets `Subst[r] := TNamed n` — after that, `resolve` yields the nominal type and field access takes the nominal path, so the row is genuinely closed. Pinned (`Nonexistent` after a discharged stage → nominal rejection).
- 1.5 stale-compare: `bind` shallow-resolves at the top and re-resolves in structural recursion; the `e = a` shortcut is safe because equal-but-unresolved compares only misfire toward the *structural* case, which resolves. Binop operands are atomic types. Good-code sanity pinned.

**§2 Propagation** — 2.1: a discharged row can't be written to (its var is substituted away; `Rows` entries go stale-but-unread). 2.2: all argument positions go through `check` (uniform since the rows rewrite); record literals push declared field types; the exact-field-set rule means no subset-leniency. 2.3: N/A — no annotation syntax exists.

**§3 Generalization** — regime is *generalize at let/REPL, freshen per use* (Damas-Milner style), not freeze: pinned by 3.1 test (one `map _.V` used at `int` and `string` field types in one line, both accepted). Soundness edge (generalizing an enclosing lambda's live var) excluded by `envFreeVars` subtraction — pinned since the rows commit. 3.2 value restriction: no purchase — the language has no mutable bindings, and data sources (streams) are concretely typed; only functions are polymorphic. 3.3: types are erased at runtime; closures carry no row-store references; cross-line types are baked snapshots re-instantiated per use, and per-line fresh-name collisions are impossible because REPL-stored types are fully generalized (every var renamed at instantiation).

**§4 UoM × rows** — 4.1: field demands are measure-*exact* (`f.Size > 1<mb>` demands `int<mb>`, no conversion, measures nominal by name); discharge against `int<byte>` would reject — pinned by the gb-vs-mb conflict test. 4.2: N/A by construction — no unit algebra exists (measures are `string option`; `*`/`/` are unitless-only), so there's no non-normalized representation to mis-compare. 4.3: no dimensionless collapse hole — `f.Size / f.Size` is *rejected* (division on measured ints unsupported; `int<1>` inexpressible). Pinned.

**§5** — 5.1: constructor patterns on unsolved rows reject; var/wildcard arms bind (harmless). 5.2: shadow binds a fresh var; constraints are keyed by var id, not source name — pinned. 5.3: constraint spans travel with each field demand; discharge errors point at the demanding span (or use site across a generalization boundary — deliberate, documented in the rows entry). 5.4: `unreachable` inventory re-probed with row-typed code; field-on-missing-VRecord requires a 1.4 leak, which is pinned shut.

**Read order for the human pass** (matches advisor's §1→§2→§3): `bind`/`dischargeRow`/`mergeRows` (~60 lines), then `checkSpine`+`check`, then `instantiate`/`envFreeVars`/`ELet`. The judgment-on-paper exercise applies mainly to `infer`'s EField/ELambda rules and `check`'s lambda rule.

## Depth audit — poking each spike where it's most likely hollow (2026-07-12)

Ran the adversarial probe list against the row-poly branch. Results:

- **Spike 1 (checker)**: 9 new adversarial tests, all rejected at check time with correct messages/spans — wrong arity, UoM mismatch both directions, shadowing with a different type, element type contradicting use two stages later, row constraint vs declared measure conflict, lambda/constructor piped as data, field access on a union. **The line-read debt remains open** — these tests raise confidence but only a human read of Check.fs rules out unsoundness that green tests can't see. The read target is the post-row-polymorphism Check.fs.
- **Spike 2 (unreachable arms)**: every attempted source-level route to an `unreachable` arm is blocked at check time (now pinned by tests). None reached.
- **Spike 4 (process lifecycle)**: `cmd "yes" |> first 3` and a truncated print of unforced infinite `cmd "yes"` both terminate; `pgrep` confirms zero leaked children in both cases (the `seq{} try/finally + Kill` path works under partial consumption). Pull-count tests were already in the suite.
- **Spike 5 (porcelain)**: **HOLE FOUND, exactly where predicted** — not the space itself but git's C-quoting it triggers: `"spaced name.txt"` and `"qu\"ote.txt"` passed through with quotes and escapes intact. Fixed: `unquoteGitPath` (full C-style unquote incl. octal escapes for unicode under default `core.quotePath`) + quote-aware rename-target splitting. Live retest on a repo with rename + space + quote + untracked: clean paths. Regression test covers all cases incl. `caf\303\251.txt` → `café.txt`.
- **Spike 7 (honest numbers)**: the 6ms was already the `-c`-path measurement (`-e "1 + 2 |> double"` = parse+check+eval+exit), warm cache. Full typed pipeline `-e 'ls |> where (fun f -> f.Size > 1<mb>) |> first 5'`: **7ms median** (min 6, max 15). No suppression flags anywhere in the fsproj (`NoWarn`/`TrimmerSingleWarn` absent) — the 3 dependency-aggregate warnings are surfaced and empirically triaged, not silenced.
- **Cross-cutting integration on the AOT binary**: declare `type Pkg = { Name: string; Size: int<mb> }` at the prompt → `cmd` emitting NDJSON → `from json Pkg` → `where (fun p -> p.Size > 2<mb>)` → `map _.Name` → `first 3` → `["big"; "huge"; "mid"]`. The skeleton threads end to end natively.

159 tests. Verdict: one real hole (porcelain quoting), found and closed; everything else held at depth. Outstanding: the human line-read of Check.fs.

## Row polymorphism + |> only (2026-07-11, session 1) — first parked item, unparked

**Two changes**, committed separately on `row-poly`:

**1. Dropped bare `|`** (user decision). Single operator table again, `armExpr` deleted — match arm bodies are now full expressions (`| Running n -> n |> double` works without parens). Piping a whole match now needs parens (`(match ...) |> f`) because arm bodies are greedy — same as F#. Nested match in an arm still needs parens.

**2. Row polymorphism** — the predicted "biggest checker-complexity jump", and it restructured Check.fs into a miniature Damas-Milner with rows:
- `TRowVar of name * fields` in `Ty`: a record type with *at least* these fields, displayed `{ Size: int<mb>; .. }`. `Scheme = { Forall; Ty }` replaces bare `Ty` in `TypeEnv.Values` — proper generalization, so the classic unsoundness (generalizing a variable free in the environment, e.g. an enclosing lambda's parameter) is excluded by construction and pinned by a test.
- Per-line mutable `Ctx` (fresh counter, substitution, row-constraint store with **spans per constraint**). `bind` is one-way-matching upgraded to unification-lite with an occurs check. Lambda params get fresh vars; field access on an unknown *upgrades* it to a row var and accumulates constraints; constraints discharge nominally when the var meets a `TNamed` (wrong field → "FileRow has no field 'Sze'. Did you mean 'Size'?" at the constraint's span; through a let-generalization the error lands at the use site, which is the right model for multi-line REPL sessions).
- Instantiation is freshen-on-use from schemes — let-bound row values are genuinely polymorphic: one `map _.X` reuses across two record types (tested), and `sizes = map _.Size : seq<{ Size: 'a; .. }> -> seq<'a>` is polymorphic in the row *and* the field type, so measures flow through.
- **Net simplification in places**: the two special-case lambda rules (EApp-of-lambda, pipe-into-lambda) are gone — bare lambdas just infer (`fun x -> x : 'a -> 'a`). checkSpine's two-pass argument dance collapsed to uniform `check` calls. The Spike 5 casualty (`let staged = where (fun f -> f.ReadOnly)`) is un-killed.
- Deliberate limits: binops on two unknowns stay errors (except `*`/`/` which bind to unitless int — the only sound reading); no higher-order inference (`fun f -> f 1` rejected); constructor patterns need concrete scrutinees; rows are records-only. Adapters/decls unchanged; runtime untouched (types erase).

**Numbers**: 149 tests; AOT still clean (same 3 dependency-aggregate warnings), cold start unchanged ~6–8ms with row-polymorphic expressions.

**Review note**: Check.fs is a full rewrite of the inference core — this is a gate-grade review, bigger than Spike 5's. Reading order: `Ctx`/`resolve`/`finalTy` → `instantiate`/`envFreeVars` (the generalization pair) → `bind`/`dischargeRow`/`mergeRows` (the heart) → `infer`'s `ELambda`/`ELet`/`EField` rules → `checkSpine`/`check`. The soundness-critical spots: occurs check, `envFreeVars` subtraction in `ELet`, and discharge-before-recurse ordering in `dischargeRow`.

## Spike 7 — AOT reality check (2026-07-11, session 1) — TARGET MET

**Result: 6ms median cold start** (min 6 / max 9 over 20 runs of `Weir -e "1 + 2 |> double"`), vs the 5–20ms target and ~70–135ms for the same dll under JIT `dotnet`. Binary: 5.5MB self-contained. The no-FCS, no-reflection, no-printf discipline paid off in full.

**Setup**: `PublishAot=true`, `InvariantGlobalization=true`, `StripSymbols=true`, `OptimizationPreference=Speed`; `dotnet publish -c Release -r linux-x64` (needs clang + zlib1g-dev + binutils; container is Ubuntu 26.04 — note: the `.fc44` kernel string is the Fedora *host* kernel, containers share it).

**Warnings**: zero from weir's own code. Three aggregate dependency warnings — FSharp.Core (IL2104 trim + IL3053 AOT) and FParsecCS (IL2104) — from reflection fallback paths (structural equality/printf in FSharp.Core, FParsec's dynamic bits). Empirically benign: every feature exercised against the native binary works — FParsec parsing, checker, declarations, match, UoM errors, streaming with process spawn/kill (`cmd "yes" | first 3`), porcelain and JSON adapters, roundtrips. Custom equality on `Value`, hand-rolled `formatTy`/`formatValue`, and interpolation-only output mean the flagged paths are never hit.

**Also built**: `weir -e "<expr>"` eval-and-exit mode (the honest thing to measure, and a real shell wants it) — `value : type` on stdout, errors to stderr, exit codes 0/1/2.

**Verdict**: the plan's last hard question answered yes. All 8 spikes done (0–7) in one day against a 12–20 session estimate. What remains is the parked list: row polymorphism (now concretely motivated by mono-builtins strain + the `where`-lambda-standalone casualty), adapter automation, LSP, daemon (moot — 6ms needs no daemon).

## Spike 6 — REPL ergonomics (2026-07-11, session 1)

**Built**: line editor (ReadLine nuget — history, tab completion), checker-powered completion (`Complete.fs`, pure + unit-tested), `_.Field` lambda shorthand, string escapes (`\" \\ \n \t`), history persisted to `~/.weir_history`. 136 tests.

**Completion design**: `Complete.suggest : TypeEnv -> text -> wordStart -> string list`, pure so it's testable without a terminal.
- Dot-completion resolves the target: env-bound record vars directly; unbound names (lambda params) fall back to the *pipeline element type* — parse+typecheck everything before the last `|`, take the seq element. So `ls | where (fun f -> f.<TAB>` offers FileRow fields, and after `| from porcelain |` the same keystroke offers Change fields. Field chains resolve through nested records.
- `from json <TAB>` completes declared record names. Otherwise: values in scope + keywords.
- The REPL runs completion against the live TypeEnv (a ref updated per loop), so user-declared types/lets complete immediately.

**`_.Field`**: parser-level desugar in `postfixAtom` — `_.A.B` becomes `fun _ -> _.A.B` (the param is literally named `_`). Zero checker changes; rides the lambda rules including pipe-directed instantiation: `ls | where _.ReadOnly`, `ls | map _.Size` both work. Bare `_` stays an unbound-variable error, as in F#.

**Bug found by the escapes**: lazy adapter errors (e.g. invalid JSON in `from json`) escaped the REPL's try — eval returns an unforced seq, and the throw happened at `formatValue` time, crashing the process (SIGABRT). Fix: force/format inside the guard. Lesson filed: with lazy values, *printing is evaluation* — any REPL boundary must treat formatting as effectful.

**Piped-stdin fallback**: when input is redirected the REPL bypasses ReadLine (it needs a real terminal) and reads plainly — keeps automated smoke tests working.

**Open, deliberately**: the spike's real question — does checker-powered completion feel like the payoff? — needs the user's hands on an interactive terminal; unit tests can't answer it. Also pending the user's `|` vs `|>` verdict (drop bare `|` and the dual-OPP grammar simplifies; keep it if the shell feel wins).

**Verdict**: build complete; experience verdict pending user. → Spike 7 (AOT) is the last planned spike.

## Spike 5 — External command boundary (2026-07-11, session 1)

**Built**: `cmd`/`into` process builtins, `from json <Record>` / `from porcelain` / `to json` syntax forms, `Change` record, real-git acceptance test — **plus pipe-directed parametric instantiation in the checker**, which the acceptance forced. 121 tests.

**Done-when verified**: `cmd "git status --porcelain" | from porcelain | where (fun c -> c.Staged)` works on a real repo (temp-repo test + live REPL). One deviation from the plan's literal expression: commands are `cmd "..."` strings, not bare words — bare-command syntax is command-position parsing (a frontend question for Spike 6+), not a typed↔bytes question.

**The forced checker change (REVIEW THIS)**: the acceptance pipes `seq<Change>` into `where`, which was FileRow-mono — unmeetable without polymorphic combinators. Added the minimal version: `TVar` in `Ty`, and spine-directed instantiation (`checkSpine` in Check.fs). The pipe rule now infers the piped value FIRST, binds the combinator's type variables from it (one-way matching, no unification variables), and only then checks lambda arguments — whose parameter types are concrete by that point. Two-pass argument checking (non-lambdas bind first, lambdas after) makes full application `where p ls` work too. No generalization, no let-polymorphism, no row polymorphism — those stay parked; `didYouMean`-quality errors preserved.

**Casualty**: `let staged = where (fun f -> ...)` no longer checks (lambda in polymorphic position, no data to instantiate from; error hints "pipe the data in first"). Partial application with inferable args still works and stays polymorphic (`let firstTwo = first 2` : `seq<'a> -> seq<'a>`). Pipe-first is the shell idiom anyway.

**Typed↔bytes verdict (the spike's question)**: less painful than feared, with clear division of labor. The checker guarantees everything inside the pipeline; the adapter validates at the boundary and fails loudly per line (`from json: missing field 'Size' in: {...}`). Runtime boundary errors are honest — bytes are untyped, so check-at-the-edge is the contract. `from`/`to` as syntax (not builtins) works because a format+record isn't a value — and `from porcelain` still first-classes fine (`let p = from porcelain in ...`).

**Mechanics that mattered**:
- `TEFrom` carries the `RecordDef` (not just the name), so eval needs no TypeEnv — checker resolves, runtime trusts.
- Process streams: `seq {}` with `try/finally` kills the child when the consumer stops early — `cmd "yes" | first 3` terminates and reaps. Nonzero exit raises at stream end with stderr. `into` writes stdin from a background task (no deadlock on full pipes).
- JSON via `JsonDocument`/`Utf8JsonWriter` — no reflection, AOT-safe. Serialization is value-driven (VRecord knows its shape); only parsing needs the def.
- weir string literals have no escapes, so you can't type JSON at the prompt — roundtrip demos via `ls | to json | from json FileRow`. Escape syntax → Spike 6.

**Surprised**: how little the poly machinery needed to be — ~100 lines, no unification state, because bidirectional + pipe-first gives instantiation order for free. Dunfield & Krishnaswami would call this a degenerate special case, and it's exactly enough for a shell.

**Verdict**: continue. → Spike 6 (REPL ergonomics) or the parked polymorphism/adapter work.

## Spike 4 — Streaming pipelines (2026-07-11, session 1)

**Built**: infinite `nats` builtin, lazy `map`/`take`/`sum` (int-mono), `==` equatability check in the checker, pull-count acceptance tests. 93 tests.

**Acceptance verified**: infinite source `| first 5` terminates; a counting source proves `first 5` pulls exactly 5 elements, `where ... | first 2` pulls exactly what the filter examined (4), and an unforced pipeline pulls 0. Laziness survives eval boundaries — including weir lambdas as filter/map stages (closures apply per-pull inside Seq.filter/Seq.map).

**The honest finding**: Spike 2's architecture had already answered this spike's question. `VSeq` wraps .NET `seq<Value>` (an enumerator factory), and `where`/`first` were built on `Seq.filter`/`Seq.truncate` from day one — nothing in the eval path materializes. This spike was proof + hardening, done in a fraction of the estimate.

**Hardening that was real**:
- Spike 2's flagged footgun closed: `==` on a seq would have hung on infinite input (Value equality materializes both sides). Fixed at the type level — `isEquatable` recursively rejects `==` on seqs, functions, and any record/union that transitively carries one (cycle-safe via a seen-set). Runtime equality on seqs is now unreachable through checked code.
- `formatValue` already truncated at 20 elements, so the REPL prints `nats` (an infinite value) safely.

**Naming pressure**: `map`/`take`/`sum` carry generic names but int-mono types, while `where`/`first` are FileRow-mono. Two element types now exist and the builtin table is already showing the strain — this is the concrete motivation for the parked polymorphism work, on schedule (revisit after Spike 5).

**Caveat noted**: `let s = ls | where p in ...` re-enumerates per use (standard seq semantics) — side-effecting sources run again. Fine for now; caching combinators are a product question, not a spike question.

**Verdict**: continue. → Spike 5 (external command boundary).

## Spike 3 — Type declarations (2026-07-11, session 1)

**Built**: `type X = { ... }` / `type X = A of t | B` statements at the prompt, record literals, `match` with constructor/var/wildcard patterns (nested allowed), exhaustiveness + unreachable-arm warnings, session persistence. 83 tests.

**Done-when verified at the REPL**: declare `type Proc = Running of int | Stopped`, construct (`Running 42`), match, get span-underlined exhaustiveness warnings.

**Design decisions**:
- `TRecord` → `TNamed`: the parser can't know record-vs-union when reading a type name, so `Ty` holds just the name and `env.Types` maps to `TypeDef = Record | Union`. Mechanical rename through checker/builtins.
- Constructors enter `Values` as ordinary typed entries (`Running : int -> Proc`, `Stopped : Proc`), so construction is just application — no new checker rule, and constructor typos get did-you-mean hints for free. Runtime counterparts built by `Eval.constructorValues`; a redeclared union shadows its constructors, but old values still match (pattern checking resolves cases via the scrutinee's type def, not a global ctor table).
- Case identifiers must start uppercase (F# convention) — that's what disambiguates `PCase` from `PVar` in patterns.
- **The `|` ambiguity**: match arms vs pipe. Resolution: arm bodies parse with a second OPP that omits the `|` operator (`|>` stays legal); a failed arm parse backtracks, so a trailing `| double` after the last arm becomes a pipe of the whole match — coherent and tested. Arm bodies containing `let`/`fun`/nested `match` need parens. Real fix is the offside rule — Spike 6 question at the earliest.
- Record literals resolve nominally by exact field-name set; ambiguity (two records, same fields) is an error. Type ascription syntax is the eventual disambiguator if needed.
- Exhaustiveness is a separate pure pass (`Check.warnings : TypeEnv -> TypedExpr -> Warning list`) walking the typed tree — zero signature churn on infer/check, no writer-monad plumbing, trivially testable. Top-level coverage only: a case counts as covered when some arm has its constructor with an irrefutable argument; nested refutations are conservatively "not covered". Proper usefulness matrices parked.
- Non-exhaustive match is a warning, not an error (per plan) — so `match failure` at runtime is reachable and is a `failwith`, not an `unreachable`.

**Surprised**: constructors-as-env-entries made construction genuinely free — the entire "constructor table" is `checkDecl` extending Values. The `|` grammar collision was the only real fight, and backtracking arms turned it into a feature (pipe-after-match without parens).

**Verdict**: continue. → Spike 4 (streaming pipelines).

## Spike 2 — Typed interpreter over checked AST (2026-07-11, session 1)

**Built**: `Eval.fs` rewritten over `TypedExpr` — untyped Spike 0 eval deleted. Value domain grown: `VRecord` (name + field map), `VUnion` (shape only, constructed in Spike 3), `VSeq`. All type-impossible arms are `unreachable` calls. New `Builtins.fs`: each builtin is one `(name, Ty, Value)` entry, so the TypeEnv and value env derive from a single list and can't drift. `ls` is real (`Seq.delay` over cwd — fresh listing per enumeration), typed `seq<FileRow>`. 52 tests.

**Checker→interpreter handoff**: holds. Spike 1's acceptance expression evaluates over records (fake `ls` fixture in tests, real one in the REPL). All former "fails at runtime" tests are now "rejected at check time" tests — the runtime error class they covered is unreachable through the checked pipeline.

**Learned**:
- The gate exposed a Spike-0-era fixture as untypeable: `let add = fun a -> fun b -> ...` — let-bound bare lambdas can't infer in bidirectional checking without annotations. Not a bug; the idiomatic replacement is partial application of typed functions (`let staged = where (fun f -> ...)`), which infers fine and is more shell-like anyway. Parameter annotation syntax is the eventual fix if the limitation bites.
- Runtime type errors did disappear. What remains at runtime is honest: division by zero, IO failures. Those are not the checker's job.
- `VSeq` equality materializes both sides — fine for tests, will be a footgun with infinite seqs in Spike 4 (flagged there).
- `unreachable` messages name the checker guarantee they rely on — each one is a soundness assertion; if one ever fires, it points at the checker rule that lied.

**Surprised**: how mechanical this spike was after Spike 1 — the typed eval is *simpler* than the untyped one (no defensive error paths, just `unreachable`).

**Verdict**: continue. → Spike 3 (type declarations) or Spike 4 (streaming).

## Spike 1 — Bidirectional checker, nominal only (2026-07-11, session 1)

**Built**: spanned AST (`Expr = { Kind; Span }`), `Ty` (int-with-optional-measure/str/bool/fn/seq/record-by-name), `TypeEnv` (Values + Types), `infer`/`check` pair in `Check.fs`, typo hints via edit distance, REPL now typechecks before eval and prints caret-underlined span errors. 43 tests.

**Acceptance**: `ls | where (fun f -> f.Size > 1<mb>) | first 5` checks to `seq<FileRow>`; `f.Sze` rejected with span exactly on `Sze` + "Did you mean 'Size'?". Perf: ~µs per check, 10ms bound trivially met.

**Design decisions**:
- Binops promoted from desugared builtins to `EBinOp` — overloading (`+` on int/str, measure-preserving arithmetic) doesn't fit monomorphic env entries. `typeBinOp` is the single overload table.
- Builtins are monomorphic (`where : (FileRow -> bool) -> seq<FileRow> -> seq<FileRow>`); polymorphism deliberately absent, revisit with row polymorphism (parked).
- UoM = `TInt of string option`, equality by name, erased at runtime. `+`/`-`/comparison require same measure; `*`/`/` unitless only (no measure algebra).
- Lambdas don't infer, but two refinement rules cover the shell idioms: lambda applied to a known arg, and pipe-into-lambda (arg type flows into the param).
- `EField` carries the field's own span so typo errors point at `Sze`, not all of `f.Sze`.
- `==` not `=` for equality (avoids let ambiguity). Composite spans are unions of child spans; leaf tokens capture position before ws-skip.
- Spans compose via `Span.union`; retrofitting confirmed as the right fear — touching every parser production once was enough, but only because the AST was 8 cases.

**Surprised**: how little the bidirectional core is — `check` has 3 real rules (lambda, let, fallback-to-infer-and-compare). The complexity lives in `infer`'s per-node rules and error message quality, not the discipline itself.

**Verdict (provisional)**: checker felt tractable to write. GATE CONDITION: user line-by-line review of Check.fs pending — spike isn't closed until then.

## Spike 0 — Toy interpreter (2026-07-11)

**Built**: `Expr` DU (int/str/bool/var/let/lambda/app/pipe), FParsec parser, tree-walk eval/apply, REPL with persistent top-level `let`. 23 tests. `1 + 2 |> double` → 6 end to end.

**Learned**:
- FParsec's `OperatorPrecedenceParser` handles the whole binop/pipe layer; binops desugar to `EApp(EApp(EVar "+", l), r)` against builtin env entries, so eval has no operator special cases.
- Lambda/let-in must be *terms* of the OPP (not alternatives outside it) or they can't appear on a pipe RHS (`5 |> fun x -> x * x`). Greedy lambda body = F# semantics for free.
- `Value` can't derive structural equality once `VBuiltin of (Value -> Value)` exists — custom equality (structural for data, reference for functions) needed for test assertions. Will matter again for `VSeq` in Spike 4.
- FParsec error messages come with line/col and a caret out of the box — good omen for Spike 1 span work.

**Surprised**: nothing structural. Keyword-vs-identifier ambiguity (`true`, `fun`) needed the usual `attempt` + `notFollowedBy` dance.

**Verdict**: continue. Eval/apply shape clicks, FParsec workable. → Spike 1.
