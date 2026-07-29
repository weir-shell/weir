# weir — the lexicon

A dictionary of the project's own vocabulary: type-theory terms weir
uses precisely, testing techniques invented or adapted here, the
corpus-mining regime, and the process idioms the ledgers speak in.

**What this file's job is.** The repo has four prose stores with strict
jobs — DECISIONS indexes rulings, PROCESS holds rules, SEMANTICS states
laws, NOTES is chronological archaeology. This is a fifth with a job
none of them do: it **defines the terms the other four use**. The
repo's "never a fourth prose store" rule was about a DEBT store, not
vocabulary. The lexicon honours it by construction: **it defines, it
does not decide.** Every entry that corresponds to a ruling points at
its `[D:key]`; every entry that corresponds to a rule points at
PROCESS. If you want to know what was decided, follow the pointer; this
file only tells you what the words mean.

Not a tutorial (that's GUIDE), not a spec (SEMANTICS), not a history
(NOTES). One paragraph per term, maximum. Terms weir uses differently
from the field state the difference — that is this file's highest-value
content.

---

## 1. Type theory and language design

**Module.** weir's unit of code sharing and namespacing. A builtin
namespace (`Seq`, `Str`, `Env`, `Args`, `Self`) and a user-authored
one (a file marked `module`) are the SAME concept with different
origins — both are a named bag of members reached by qualified access
(`Seq.map`, `Git.revParse`), both live in `TypeEnv.Modules`. "library"
and "package" are RETIRED as synonyms; "dependency" survives for the
RELATIONSHIP (the dependency graph), and "import" is the verb and the
statement. [D:modules-v1]

**Row polymorphism.** A record type is a set of named fields; a
function can accept "any record that HAS these fields" without naming
the whole type. weir uses open-row compatibility for the
declared-record-field completion fallback and for `Args.load` shapes —
a value typed by the fields it demands, not by a nominal name.
[D:open-row-compat] [D:declared-fields-fallback]

**Structural vs nominal typing.** Nominal: two types are the same only
if they share a name. Structural: same if they have the same shape.
weir records are structural for compatibility but carry a name for
diagnostics; attributes are erased so an attributed record is
STRUCTURALLY identical to a bare one. [D:attributes]

**Erasure.** A construct that exists only at check time and leaves no
trace at run time. In the field generally it means type erasure
(generics compiled away); in weir it also names ATTRIBUTES — `[<Short
"c">]` is check-time data, fully erased, so `cli` is indistinguishable
from a bare record at run time. [D:attributes]

**Bidirectional checking.** Type-checking that alternates two modes:
CHECK a term against a known expected type, or INFER a type when none
is given. weir's checker has three real check rules (lambda, let,
fallback-to-infer-and-compare); the complexity lives in infer's
per-node rules, not the discipline. (NOTES: Spike 1.)

**Unification.** Solving type equations by making two types equal,
binding type variables as needed. The engine under inference.

**Generalization.** Turning a type with free variables into a reusable
SCHEME (∀-quantified) at a let binding — the source of let-polymorphism.
weir runs splice-defaulting BEFORE generalization: a bare spliced param
defaults to string at the statement boundary, so it is not
prematurely generalized into an unusable type variable.
[D:splice-default-last]

**Damas–Milner.** The Hindley–Milner type system with let-generalization
(Algorithm W) — the theory weir's inference implements: principal types,
no annotations required, unification + generalization.

**Type class.** In Haskell, an open, global, dictionary-passing
interface. **weir's variant is closed, structural, and erased**: the
closed set is **Eq, Show, Ord** (compiler-owned, not user-extensible —
the door stays shut), instances are decided by structure, constraints
are inferred and checked at the USE site, and nothing is passed at run
time. The admission rules differ per class: **Eq** — equatable unless
the type contains a function or a seq, anywhere, recursively
(records/unions/tuples decompose). **Show** — showable unless it
contains a function; seqs DO show (rendered lossily, truncated) —
wider than Eq by exactly the seq rule, but the same KIND of failable
constraint. **Ord** — int, string, bool EXACTLY; no structural
decomposition (no record/union/tuple ordering) — narrower than both.
[D:inferred-type-classes]

**Constraint.** A demand attached to a type variable — "this must be
equatable". weir infers constraints (from `==`, `show`, `Seq.sortBy`)
and reports the violation at the use site, not the definition.
[D:inferred-type-classes]

**Scheme.** ⚠ Two meanings in the repo (see Findings): (1) a TYPE
SCHEME — a ∀-quantified type produced by generalization; (2) a checker
MECHANISM — the "sentinel scheme" that types `print`/`show` (a bespoke
∀ that a dedicated checker arm consumes). Meaning is context-clear but
the word is overloaded.

**Datatype-generic elaboration.** Deriving code from a type's STRUCTURE
at compile time — the `Args.load Cli` / `Env.load T` family reads a
record's fields and elaborates a parser. Neighbours: Rust serde/clap
`derive`, Haskell aeson `Generic`, F# type providers, Zig comptime
`@typeInfo`. weir's is closed (a fixed set of shapes), check-time, and
carries the field-type rules in the checker. [D:typed-argv] [D:typed-env]

**Type application.** Supplying a type argument explicitly. weir spells
it as a **bare name** (`Args.load Cli`, `Env.load T`), not angle
brackets — the type is an ordinary identifier in value position, read
by the bespoke checker arm; there is no `<...>` syntax. [D:typed-argv]

**Exhaustiveness.** A match must cover every case; a non-exhaustive
match is a HARD ERROR (not a warning), coverage recurses through
constructor payloads, and int/string literals never complete a match
alone (add a `_`). [D:exhaustiveness-hard-error]

**Refutability / irrefutable binder.** A pattern is refutable if it can
fail to match (a literal, a specific constructor); irrefutable if it
always matches (a var, `_`, a tuple of irrefutables). `let` binders and
lambda params must be irrefutable; a refutable pattern there is
rejected with the fix. [D:pattern-binders]

**Offside rule / light syntax.** Indentation determines structure
(the offside rule); "light syntax" is F#'s name for the
indentation-driven surface over an explicit core. weir's assembler
implements a subset: block `let`s take `in` by construction, blanks are
transparent while a statement pends, and the col-0 law is the sole
boundary. [D:block-let-cmd] [D:body-blanks]

**Committed choice vs speculative alternation.** Speculative: try an
alternative, backtrack on failure (FParsec's `attempt`/`<|>`).
Committed: once a marker is consumed, do NOT backtrack. weir's
**consumed-separator law** — a consumed `;`/`|`/record-`ident =` head
commits to its element — converts speculation to commitment exactly
where a rewind would otherwise manufacture a false-shallow parse.
[D:seq-commit] [D:arm-commit]

**Furthest-error merge.** FParsec reports the error at the furthest
STREAM POSITION reached. weir's diagnostics policy rests on this: "the
error at the furthest point the parser reached in your file" — furthest
REACHED, not latest-in-file (two problems in one statement report the
first). The commit law makes the furthest position the true cause.
[D:diag-arbitration]

**Anchor before the read.** A parser error's caret belongs on its
TRIGGER token, captured BEFORE the trigger is consumed — `failFatally`
fires at the CURRENT position, so a consume-then-fail site drifts past
its token (trailing ws even crosses physical lines in assembled
statements). Shape: `failFatallyAt`/`failFatallyAtCol` — consume the
trigger (which CLEARS the competing "expected" errors that sit there,
so the message does not bury), then Seek back to the anchor. Caveat:
clean ONLY where the anchor position has no surviving competitor;
where one remains (neg-int's `-` is the unary-minus operator's spot)
the expected-set re-merges. [D:anchor-before-read] [D:message-domination]

**A fatal inside an `attempt` is not a fatal.** FParsec's `attempt`
backtracks fatal errors too, so a `failFatally` inside speculative
`attempt`/`choice` is ADVISORY — it will be swallowed. A teaching error
that must survive needs one of: an anchor OUTSIDE the attempt (the
`letKeywordGuard` fires before topLet's attempt), an exception channel
(`DepthExceeded` throws past the protocol), or a commit point ahead of
it (the consumed-separator law). Three sightings: arm-commit, the depth
guard, the reserved-word gate. [D:anchor-before-read] [D:depth-guard]

**Lazy sequence / pull semantics / backpressure.** A `seq` computes
elements on demand (pull), not eagerly. Backpressure: a slow consumer
throttles the producer — a value-headed pipe's input pulls as the
child's pipe accepts, so `xs | head -1` over a million-line source
stops at the buffer, not the end. Proved by pull-count pins, never by
inspection. [D:value-headed-pipe]

**Memoization vs materialization.** ⚠ The pair that caused real
confusion. MATERIALIZATION (`Seq.force`) runs a lazy seq to a concrete
list ONCE and returns it — total, so an infinite source never returns.
MEMOIZATION (`Seq.cache`) wraps a seq so re-enumeration reuses computed
elements without re-running the generator. `force` changes the type
(seq→list); `cache` keeps it (seq→seq). Retired names (`toList`) teach
`force`. [D:seq-force] [D:seq-patterns]

**Totality.** A function returns a result on every input — never
crashes, hangs, or silently mis-executes. weir's checker/assembler are
total (a diagnostic on any input); the depth guard restored totality
after the safe-by-design review found stack-exhausting inputs.
[D:depth-guard]

**Check-time vs run-time.** weir's defining split: the checker runs
before ANY effect, so a script with an error in line 40 executes none
of lines 1–39. "Check-green must mean runnable modulo uninstalled
tools" is the contract the assume-resolver serves. [D:assume-resolver]

---

## 2. Testing and verification techniques

**The pin.** A test asserting an exact behaviour against the compiled
binary. Two roles: **pin-as-regression-guard** (this must not change)
and **pin-as-constitution** (this behaviour IS the decision — moving it
is a decision, recorded with archaeology). A "done-when boundary
behaviour" earns a pin. PROCESS: Behavioral pins over parse-shape pins.

**The oracle.** Differential testing against the real F# compiler (FCS)
— `tests/Weir.Fidelity` refs each fidelity case Same/Diverges against
`divergences.md`. WARNINGS count as ACCEPT (F# ran it). Catches: weir
accepting what F# rejects (the zero-gold claim). Cannot catch: bugs
where weir and F# agree wrongly. [D:tuples-reversal] (the oracle's
first live catch: `rec`/`mutable` had begun parsing as function names.)

**Probes-first / the folklore rule.** Establish the FCS/binary verdict
BEFORE implementing — no feature rests on a remembered belief about
what F# does. The folklore rule: a claim like "F# warns here" is probed,
not assumed (FS0058 folklore was corrected this way). PROCESS.

**Pin hygiene.** A probe that can fail for a reason OTHER than the one
under test proves nothing — e.g. a missing-type probe that read as "F#
tolerates literals" was a probe artifact. Also the pgrep variant (below).

**Claims-by-pointer.** A decision comment cites `[D:key]` plus the bare
local why — no history narration, no verification credits (pins live in
tests, history in NOTES). Keeps the four stores' jobs distinct.
(CLAUDE.md.)

**The product matrix.** Naming the cells of a feature-interaction space
(e.g. block-let-command-RHS × assume-resolver × bound-head-after-`;`) so
the untested combination has a name. The fuzzer owns the unnamed cells;
hand-pinned matrices keep the named ones. [D:fuzz-harness]

**Metamorphic testing.** Testing without a known-correct output by
asserting a RELATION: a semantics-neutral transform of a program must
produce byte-identical `(rc, stdout, stderr)`. weir's transform library
(district↔`!(...)`, bare-RHS↔`$(...)`, block-siblings↔`;`, Stroustrup↔
inline, and all composed) is the fuzzer's invariant 1. [D:fuzz-harness]

**Property-based generation.** Generating valid-by-construction programs
from a grammar and asserting invariants over all of them. The
denominator honesty rule: what the generator CAN'T produce is stated
(GRAMMAR.md), so "the fuzzer passed" has an honest scope. [D:fuzz-harness]

**Delta-debugging shrink.** On failure, shrink the counterexample to a
minimal reproducing program (delta debugging over top-level statements
with dependency closure) — the reported failure is small.

**Span soundness.** An injected bad token is diagnosed on its own
physical line, col within the line's extent — the fuzzer's invariant 3,
always-on (`WEIR_FUZZ_STRICT_SPANS`). Extended to arbitration: a deeper
second junk must not steal the first-reached error's site.
[D:diag-arbitration]

**Totality invariants.** The fuzzer's invariant 2: assemble→parse→check
returns a diagnostic on every generated program and mutated neighbour —
no exception, no hang. Gained a DEPTH axis after the safe-by-design
review. [D:depth-guard]

**Pull-count pins.** Laziness proved by COUNTING how many elements a
lazy source yields, never by inspecting internals — `first 2` over a
counted source must pull exactly to the second element. The standing
rule for every lazy surface. PROCESS.

**Effect-counted pins.** Correctness of effect ORDER/COUNT proved by a
counter (how many times a command ran, in what order) rather than
output shape.

**Graded positive control.** A deliberately-wrong input that MUST fail
— proving the DETECTOR fires, not just that the happy path passes. Its
dangerous inverse is the **manufactured failure**: an instrument that
can match itself (`pgrep -f "sleep 300"` matched the probe's own shell,
inventing phantom orphans). Count by name (`ps -C`), never by a pattern
the measuring command carries. PROCESS: Harness truth.

**The harness-truth class.** Failures of the test apparatus itself:
STALE ARTIFACTS (an outdated binary), MASKED FAILURES (a gate that
can't see what it claims — including a lying comment, the seventh
member), and MANUFACTURED FAILURES (the pgrep variant). Mechanised
away, not remembered. [D:masking-mechanized] PROCESS: Harness truth.

**Byte-identity pins vs invariant-by-architecture.** A byte-identity
pin asserts two spellings produce identical bytes (`xs | prog` ≡ `xs |>
feed`); invariant-by-architecture makes the equivalence hold BY
CONSTRUCTION (both hit one `Proc.linesWith`). The standing preference is
the latter — the pin then guards a property the code already
guarantees. [D:value-headed-pipe] [D:child-env-overlay]

**Doc-tests.** Every fenced `weir` block in SKILL/GUIDE runs clean
against the binary; every `weir-error` block must fail. A doc line that
stops being true fails the build. (`ci/skill-doc.sh`.)

**The freshness gate.** One shared check (`ci/check-fresh.sh`) that the
binary's stamp equals git HEAD AND no source is newer than it, run by
every consumer — stale results become impossible, not catchable. The
one window it can't see (a republish mid-run) is closed by the
deep-run lock. [D:masking-mechanized]

**Zero-pin-movement as a refactor contract.** A refactor's correctness
criterion: not one pinned behaviour changes. The regroup and hardening
sessions ran under it; any red is a finding.

**Failing-first ordering.** Write the pin so it FAILS before the fix
exists (spawn the failing pin first) — the hazard is the test that
passes for the wrong reason. PROCESS.

**Stop-and-report.** On a behaviour delta mid-refactor, a budget
overrun, or a precedence-class grammar change, STOP and report rather
than pressing on — the greedy-`;` and the value-headed-pipeline
scope-cut were stop-and-reports. PROCESS.

---

## 3. Corpus mining and the empirical regime

**The corpus.** The `dotnet/fsharp` ComponentTests F# source (@5928e91),
mined for triple-quoted snippets — real F# weir is measured against. Its
licensing posture: read-only reference, env-gated (`WEIR_CORPUS_DIR`),
never redistributed.

**Extraction vs keeping.** EXTRACTED = every `"""…"""` snippet found
(4253); KEPT = those the filter judges weir-plausible (base 76). The gap
is the point: most F# uses constructs weir bounds out.

**The reject list.** The named substrings/regexes that bound a snippet
out (`module`, `printfn`, `|>`, …). The filter DIFF reads as language
growth — WAVE_REJECTS are shapes the feature waves later admitted (base
mode rejects them, wide mode lifts them). (plans/PLAN-corpus-remine.md;
NOTES.)

**Base vs wide mode.** BASE reproduces the first mine's world (waves
rejected); WIDE lifts the four feature-wave rejects (tuples, literal
patterns, composition, raw strings). Wide's larger kept set (102) is
free fidelity verdicts on machinery that shipped.

**GOLD snippets.** Snippets weir ACCEPTS that F# REJECTS — the unsafe
direction. The prize number is ZERO GOLD: weir never accepts what F#
rejects, holding even over the widened set.

**Comparability / disagreement bucketing.** COMPARABLE = a snippet both
tools have a verdict on; DISAGREEMENTS are bucketed by cause (the 18
remaining, each named). Disagreements fell 24→18 while the set grew 26 —
the waves converted disagreement into agreement.

**The re-mine.** Running the miner again after feature waves landed. It
proves what the first mine cannot: that the shipped features moved real
snippets from reject→accept, measured against the same denominator.

**Receipt.** Evidence that a feature is genuinely NEEDED — a real script
that could not be written, or was awkward, without it. The **provenance
lens** (new): a receipt from MODEL-AUTHORED code may reflect the
training distribution's idioms rather than a wall the script hit —
distinguish a script that could not be written (demand) from one written
in an unfamiliar idiom (acclimation). PROCESS: Receipt provenance.

**Friction log / stranded log.** NOTES-agent.md ledgers from
dogfooding: FRICTION = agent-noticed awkwardness (roadmap input);
STRANDED = a script abandoned after 3 failed check iterations (appended
verbatim). (CLAUDE.md scripting policy.)

**Forward archaeology.** Recording, at a decision, the trigger that
would REOPEN it — so a future receipt finds the reasoning waiting rather
than re-derived. Parks carry reopen criteria this way.

**Prediction grading.** Stating a prediction (e.g. "the deep run will
find a bug") and later grading it FOUND/REVERSED against what happened —
the base-rate argument checked against reality. (NOTES: fuzzer Session 2.)

**The denominator honesty rule.** Any bounded coverage (top-N, a
generator's shape list, a sampled corpus) states what it EXCLUDES, so a
green result isn't read as "covered everything". GRAMMAR.md is the
fuzzer's denominator. PROCESS: Fuzzer grammar membership.

---

## 4. Process idioms

**Receipt.** (See §3.) The unit of feature justification.

**Park.** A deliberately-deferred feature, filed with a REOPEN
CRITERION (a.k.a. trigger) — not "no", but "not until X". Parks reopen
on a concrete receipt (Map/Set on a keyed-lookup receipt; positionals
on a hand-written-weir receipt). A park closed WITHOUT its criterion
firing is a squat (see drop-positional). [D:drop-positional]

**Bless.** User approval of a plan document, turning a PROPOSED design
into an executable session. A blessed plan is a DECISION, not a fact —
consuming plans gate on the SESSION REPORT, not the bless. PROCESS:
Dependency-gate rule.

**The advisor-error ledger.** A record of process-integrity errors the
machinery caught (a fabricated citation, a consumer written against a
never-executed plan) — filed so the catch is repeatable. PROCESS:
Dependency-gate rule.

**Design-on-file.** A design written and committed but not executed,
opened later by call or receipt (the seq-patterns and modules designs
sat on file). Distinct from a park: design-on-file is ready to build.

**The deferral regime (machine vs read).** Two kinds of "later": a
MACHINE deferral (the fuzzer will find it) vs a READ deferral (a human
must review this before it closes). The check/run verdict-split was
held for a human read; the unnamed-triple space was left to the machine.

**Rider.** A small, adjacent change bundled onto a session (the
`scriptPath` rider, the LICENSE/NOTICE rider) — sized in hours, not a
plan of its own.

**The zero-behavior contract.** A session that promises NO behaviour
change (a refactor, a docs sweep, a verification pass) — measured by
zero pin movement. Verification and hardening never share a session.

**Opened-by-choice / opened-by-sequencing.** Two ways a park opens:
by-CHOICE (a receipt fires) or by-SEQUENCING (a later feature's
customers need it first, so it lands ahead of its own plan —
`scriptPath` opened this way, recorded as such). [D:script-path]

**The paired precedent.** Measures REMOVED / classes BUILT — the twin
rulings that ripping a feature out and building one in are the same kind
of move, each with archaeology. The evidence-standard case study.

**Flip-with-archaeology.** When a pin flips from asserting X to asserting
not-X, the flip carries the reasoning (the district-wrap pin flipped from
open-bug marker to fixed-behaviour; the Positional pins flipped to
unknown-attribute). Never a silent edit.

**The docs-sweep.** Idioms rot, keywords do not — a sweep greps living
docs for stale IDIOMS (an example using a retired spelling) with hit
counts, leaving keyword mentions alone. The regroup's five zero-hit
greps + one real find. [D:masking-mechanized, via the sweep discipline]

**Teaching error / hints-name-the-spelling.** An error that names the
FIX, not just the fault ("`|` chains commands; pipe with `|>`";
"map show or interpolate per element"). One shared mechanism
(`Diagnose.hint`), not per-case hacks. [D:pipe-hint]

**Reject-don't-guess.** When input is ambiguous, REJECT with the fix
rather than guess an interpretation (complex range endpoints need
parens; `+` on two unknowns can't infer). The safe direction.

**The not-yet consumer.** A registered name with a deliberate
"not-yet-supported" error — reserving syntax while pre-answering a
park's hardest question. `[<Positional>]` was one; dropped when its only
receipt proved to be model idiom (a not-yet whose consumer never
arrives is a squat). [D:drop-positional]

**Legal-parse-wrong-meaning.** A parse that SUCCEEDS but builds the
wrong AST (the compound-paren-prune bug: a match in a closed lambda got
outer stages wrapped in). More dangerous than a parse error — caught by
metamorphic equivalence, not by "does it parse". [D:compound-paren-prune]

**Silent swallow.** The failure class where junk vanishes into a valid
interpretation with no diagnostic (the check/run verdict split: user
junk became a phantom command's argv). The totality floor and
consumed-separator law exist to make it impossible. [D:seq-commit]

**District / sigil / reifier / splat.** weir's command-mode vocabulary,
defined in SEMANTICS (pointers here): a DISTRICT is a line-end `!` block
of command lines; a SIGIL is `$(chain)` (capture) or `!(chain)`
(effect); a REIFIER (`complete`/`succeeds`/`orFail`/`exitCode`) turns a
command's run into a value where the meaning goes; a SPLAT (`$@xs`)
splices N argv words. [D:exit-reifiers] [D:argv-splat] (district & sigil:
SEMANTICS — no DECISIONS row, see Findings.)

---

## Findings

The sweep surfaced two classes, per the session's brief.

**Double-meaning terms (reported, disambiguation proposed — not silently
picked):**

- **scheme** — (1) a *type scheme* (∀-quantified type from
  generalization) vs (2) a *checker mechanism* (the "sentinel scheme"
  for `print`/`show`). Proposed disambiguation: keep "type scheme" for
  (1); rename the mechanism "sentinel arm" (it already reads as an arm)
  in future prose. Context disambiguates today; low urgency.
- **stage** — (1) a *pipeline stage* (one command segment in a chain)
  vs (2) a *proposal stage* (a phase in the plan lifecycle:
  PROPOSED/BLESSED/EXECUTED). Proposed: reserve "stage" for the
  pipeline sense (it is load-bearing in the parser); say "status" for
  the lifecycle sense (DECISIONS already uses "Status:").

**Previously-undefined / under-indexed (written here from code + NOTES,
flagged for their home):**

- **district** and **sigil** have no `[D:key]` in DECISIONS — they are
  described in SEMANTICS and NOTES but never got an index row (unlike
  their sibling `exit-reifiers`). Not a correctness gap; a
  completeness one. Proposed: a `[D:command-district]` and
  `[D:command-sigils]` row each, pointing at the SEMANTICS sections, so
  the index is whole. Reported, not added (this session is docs-only
  and DECISIONS rows are decisions, not definitions).

---

*Pointers are the contract: this file says what a word means; the
`[D:key]` says what was decided; PROCESS says what the rule is; SEMANTICS
says what the law is; NOTES says when and why it happened.*
