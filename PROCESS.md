# Standing process rules

The rules the project has learned, one place. Each rule cites its
naming incident. Plans and sessions follow these unless a blessed
plan explicitly overrides. The vocabulary these rules use — receipt,
park, pin, harness-truth, graded positive control, stop-and-report —
is defined in [LEXICON.md](LEXICON.md).

## Composition-product rule

A new grammar/assembler/checker decision ships with pins for its
products against the EXISTING decisions — the matrix row, written
before the pins. Cells are pinned, N/A'd with a reason, or triaged
red. Naming incident: the greedy-`;` silent swallow (conditional
execution the user never wrote, alive across sessions with a green
suite) — the rule postdated the decision that needed it; the
retroactive sweep (tests/PRODUCT-MATRIX.md) paid that debt.

## Behavioral pins over parse-shape pins

Where execution order or effect COUNT is the invariant, the pin is
effect-counted e2e on the AOT binary — a parse-shape pin cannot see
a legal-parse-wrong-meaning bug. Naming incidents: ext→ext piping
(the original), the silent swallow (the reminder).

## Fixture shape diversity

Every grammar construct's battery includes at least one fixture per
structural context it can occupy: headed (under if/let/match),
standalone (top-level), nested (inside another compound), and
at-boundary (first line, last line, adjacent to blank). Naming
incident: the standalone-marker district bug — every district
fixture was if-headed, and the compound supplied the sibling level
by accident until the first standalone marker appeared. The rule's
first enforcement (this sweep's backfill) immediately caught the
field-value-on-next-line record bug.

## Position-matrix rule

New expression forms and tokens sweep the expression-position
inventory (tests/POSITIONS.md) — pin or explicit exclusion per
position, enumerated in session notes. Naming incident: the let-RHS
sequencing miss. The inventory is maintained, not re-derived.

## Done-when intent rule

Done-when clauses bind to INTENT. When a session's work dissolves a
clause's premise, the report says so explicitly and the pin moves to
where the behavior is real. Grafting complexity into an example to
satisfy a checkbox's letter is the named anti-pattern. Naming
incident: "Exit.code propagates in the bicep translation" — the F#
original's exit-code plumbing dissolved under raise-at-force, so the
propagation shape is pinned in e2e where it is real, and the example
correctly carries none.

## Dependency-gate rule

Plans that consume another plan's outputs name the dependency in
their header and gate on its SESSION REPORT, not its bless — a
blessed plan is a decision, not a fact. The LSP chain did this
correctly ("three sessions in hard dependency order"); the
attributes plan wrote its consumers into Args.load, machinery from
a blessed-but-never-executed plan, and the session's opening
reality-check caught it as a stop-and-report. Advisor-error ledger:
filed next to the fabricated-citation correction the same plan
paid for — two process-integrity errors in one plan, both caught
by the machinery working as designed.

## Receipt provenance

A friction entry or feature receipt from MODEL-AUTHORED code may
reflect the training distribution's idioms rather than a wall the
script hit. Distinguish a script that COULD NOT BE WRITTEN (real
demand) from a script written in an unfamiliar idiom that FELT
AWKWARD (acclimation). The positionals park died on this: its sole
receipt was git-subrepo's `config <key> [<value>]`, a port
reproducing git's positional CLI — and positional CLIs are what
argparse/click/getopt/every man page overwhelmingly contain, so the
"receipt" was distribution echo, not demand (no weir script was
blocked; `config` was skipped and nothing broke). A registered name
held for such a receipt is squatting, not pre-scoping. This is a
lens, not distrust — cc's frictions have been overwhelmingly good;
but a receipt whose shape matches the training prior deserves the
question "could this have been written another way, and would that
have felt fine?" before it justifies reserving syntax. Older
friction entries deserve a re-read under it.

## Established rules (index; full text in their origin archaeology)

- Stop-and-report on behavior deltas mid-refactor, on budget
  overruns, and on precedence-class grammar changes (NOTES: district
  session, greedy review).
- Verify rule: run the battery for its EXIT CODE first, then grep
  the log (e2e masking, x4).
- New line-shape logic goes in classify/scanner/Join — a StartsWith
  or quote-state loop in the assembler fold is a review flag
  (PLAN-assembler-formalization).
- Claim-vs-behavior: features ship with pins that would fail if the
  claim drifts; doc examples are executed (skill-doc).
- Reject-don't-guess at typed boundaries; every rejection names its
  escape hatch (from porcelain/json, Env.load, Env.fromFile).
- Receipts before features: parked items reopen on recorded
  triggers; user fiat can override, and the override is recorded as
  such (env-sugar layers).
- Laziness claims get pull-count pins, never inspection: instrument
  a counted source and assert the forcing bound (third instance made
  it a rule — fold's strictness sentence, choose's infinite-source
  pin, the echo's double-force caught at 22 pulls where the property
  allows 11, before the pin was an hour old).

## Fuzzer grammar membership

New assembler/grammar features add their line shapes to the fuzzer's
generator (tests/Weir.Fuzz/Grammar.fs + the coverage denominator in
tests/fuzz/GRAMMAR.md) and their equivalence claims to the transform
library — the metamorphic law is part of the feature, not a follow-up.
"The fuzzer passed" means exactly what the denominator says it means.
Origin: the silent-swallow postmortem's widen-the-net park, closed by
PLAN-fuzzer — every recent assembler incident lived in an unnamed
product triple; generation probes the space nobody enumerates, but
only over the shapes it is told exist.

## Harness truth (stamps + waitpid)

Harness assertions are claims too — a test harness that can report
against a stale or lying substrate is a masked-failure factory.
Mechanisms, not memory: the publish path stamps the git hash into the
binary (`weir --version`), every executing harness gates on stamp ==
HEAD plus source-mtime freshness before running anything, and process
census uses waitpid-truth (a zombie is dead; `kill(pid, 0)` lies).
Naming incidents: five members of the stale-artifact/masked-failure
class, the fifth committed WITH the verify rule already on this page —
a rule that depends on remembering fails; that is the definition of a
rule that must become a mechanism.

A seventh member is a DIFFERENT shape — a guard whose documentation
drifted from the guard: the Python `assert_fresh` copy claimed
"mtime gates still apply" while checking only the stamp. A comment
promising a protection that isn't there is worse than no comment; the
fix was one gate all consumers call (`ci/check-fresh.sh`), so the
doc and the code cannot diverge again.

An instrument whose pattern can match the instrument is not a
measurement — the harness-truth class's most dangerous variant,
because it MANUFACTURES failures rather than masking them.
`pgrep -f "sleep 300"` matched the probe's own shell (its command
line contained the pattern), inventing phantom orphans and self-kills
that would have sent a hardening session hunting a tree-kill bug that
does not exist. Count processes by name (`ps -C sleep`), never by a
pattern the measuring command also carries. The positive-control
sibling of the stamp gate: make the instrument incapable of reading
itself.

**The vacuous-probe bar [D:vacuous-probe-audit]: any probe that
shells out is portable, LOUD, and positive-controlled.** The worst
genus found so far broke NOTHING — the zombie pin used GNU
`ps --ppid`; BSD ps errored, `grep -c Z` read the empty stream as
zero, and the pin passed from birth without ever measuring. Loud is
the non-negotiable half: an instrument's own failure must be a
NAMED test failure, never a benign-looking value (`|| true`,
`grep -c` on a dead pipe, and count-equals-zero assertions are the
signatures — zero must never be both the pass and the probe's
failure mode). Positive-controlled: the probe is shown to FAIL on a
deliberately-bad input, committed next to its zeros (a forked
unreaped zombie counts 1; a garbage lockfile exits 3; a stale stub
trips the gate); where a control is not cheap, the probe is flagged
"loud but uncontrolled" in writing rather than presumed. Corollary
for the instrument shelf: **cross-platform execution is a
verification instrument, not a portability chore** — changing OS
perturbs environmental assumptions the way metamorphic transforms
perturb syntactic ones (one macOS run found this genus AND the depth
guard's unstated 8MB-stack premise in a day).
