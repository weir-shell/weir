# Standing process rules

The rules the project has learned, one place. Each rule cites its
naming incident. Plans and sessions follow these unless a blessed
plan explicitly overrides. The vocabulary these rules use — receipt,
park, pin, harness-truth, graded positive control, stop-and-report —
is defined in [LEXICON.md](../docs/LEXICON.md).

## Messages speak the user's language

An error message speaks the SCRIPT AUTHOR's language; SEMANTICS,
DECISIONS, and NOTES may speak the implementation's. A reader of
SEMANTICS opted into the theory; a reader of an error did not. So
compiler-writer vocabulary — "scrutinee", "unify", "row variable",
"desugar", "arity" — belongs in the docs, never in a message the
binary emits. Two corollaries: an internal or synthetic name
(`__hole1`, a `|`-prefixed desugar key, a raw generated tyvar) must
never reach a message [D:user-language-messages]; and where a message
CAN name the repair rather than the category, it should (the
did-you-mean family) — a precedence surprise like `code, _ :: rest`
against a seq (`,` groups looser than `::`) names the grouping fix,
because the grouping IS the repair. This line is what makes the
vocabulary sweep decidable rather than a matter of taste.

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

## Anchor-before-the-read rule

A parser error's caret must anchor on the position of its TRIGGER
token, captured BEFORE the trigger is consumed — never wherever the
stream drifted after. `failFatally`/`err` fire at the CURRENT stream
position, so a consume-then-fail site reports past its token (the
trailing whitespace even crosses physical lines in assembled
statements). The shape: capture the position first (or seek back to
it after consuming — `failFatallyAt`/`failFatallyAtCol`, which
consume to clear the competing "expected" errors at the spot, then
Seek to the anchor). Any NEW located parse diagnostic states its
anchor and pins the exact line:col, not a contains-check. Naming
incidents: caseDecl (lowercase union case, 39:5 not 38:7) and the
bare-pipe caret (`|`+1 everywhere) — the two instances that earned
this bar. Standing caveat: seeking to an anchor that a sibling
parser also contests re-merges its expecting-list (the
message-domination class) — anchor there only once that class is
closed.

Corollary — **a fatal inside an `attempt` is not a fatal**: FParsec
backtracks fatals too, so a teaching `failFatally` in speculative
`attempt`/`choice` is silently swallowed (the worst failure mode — it
looks implemented and passes any test that only checks the message is
not worse). A teaching that must survive needs an anchor OUTSIDE the
attempt, an exception channel, or a commit point ahead of it. So a new
guard states its enclosing attempt boundary, and its pin asserts the
teaching REACHES the user (text present + neither burial marker), not
just "no crash". Sightings: arm-commit, the depth guard, the
reserved-word gate (LEXICON).

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
  escape hatch (from json, Env.load, Env.fromFile).
- Receipts before features: parked items reopen on recorded
  triggers; user fiat can override, and the override is recorded as
  such (env-sugar layers).
- Laziness claims get pull-count pins, never inspection: instrument
  a counted source and assert the forcing bound (third instance made
  it a rule — fold's strictness sentence, choose's infinite-source
  pin, the echo's double-force caught at 22 pulls where the property
  allows 11, before the pin was an hour old).

## Editor-grammar drift rule (engine-capability amended)

A highlighting rule is added to BOTH the micro `.yaml` (the spec) and
the VS Code tmLanguage, or NEITHER — a rule in one only is DRIFT (the
inventory e2e proves rule PRESENCE, keyed by `# rule:` / repository
keys). AMENDMENT [D:micro-exempt]: the drift rule prevents NEGLECT, not
capability gaps — where a grammar's ENGINE cannot express a rule (micro
is Go RE2: no lookaround), the shortfall is STATED in that grammar's
header as `# micro-exempt: <key> (<reason>)`, and the inventory allows
it. The reason is per-key, so the allowlist is DOCUMENTATION, not a
hole (a reader of the micro file learns what it can't do rather than
assuming completeness). Pretending a capability boundary is a
maintenance question would either hold the rich editors down or ship a
micro rule that is actively wrong (mis-painting `sh -c '…'` as a type
var is worse than mis-painting `'a` as a string). The worked example:
`type_param` — tree-sitter (external scanner) and TextMate (lookahead)
distinguish `'a` from `'x'`; micro's RE2 cannot peek for the closing
quote, so it is exempt with that reason.

## Form-word hover rule; form-words are a colour list, not a lexical shape

Two rulings that stop being re-derived per keyword [D:form-word-hover].

**Hover.** A keyword that names a FORM answers a hover — its meaning,
and for a family the members it heads (`within` names its kinds;
`from`/`to` name their adapters). A keyword that is punctuation-in-word
form stays silent, under the hover-silence guard [D:hover-silence]: a
fallback type on `let`/`in`/`=`/`;`/`then`/`do`/brackets teaches
nothing and risks teaching a wrong type. The line is weir's NOVELTY:
the forms carrying it (`within`, `from`, `to`, `retry`, `poll`,
`until`, the district markers) are what a reader hovering wants
explained; the grammar scaffolding is not. This is the same
distinction that settled the operators question, stated once so the
next form-keyword is not decided from scratch.

**Colour.** A weir form-word is lexically an ordinary lowercase
identifier — `cd`, `json`, `configmap` — so a grammar
colouring by lexical SHAPE cannot tell it from a binding. Only an
explicit list can. That is structural, not per-feature: every future
closed set of form-words arrives with the same colour gap, and the fix
is always the same — the set added to each grammar's list (and the
REPL colorizer's), pinned against its source by an inventory e2e. The
three closed sets to date (`yaml schema=<name>`, `within` kinds,
`from`/`to` adapters) sit in three different syntactic contexts, so
they are three small rules rather than one unified "form-word" rule —
each grammar hard-codes the set in its own idiom (tree-sitter widened
its existing `adapter` token; TextMate and micro got a two-word rule),
verified per engine, never assumed symmetric.

## Batched edits are all-or-nothing — in BOTH directions

A batched edit (several replaces guarded by asserts, one write at the
end) discards EVERYTHING on a failed assert — including the replaces
that succeeded. That is the virtue (nothing half-written; the Size
session's lesson) and the trap (the Http `insecure` session's): a
batch that aborts on the SECOND replace's assertion silently drops the
FIRST, and per-edit stdout confirmations are not receipts — "printed"
is not "landed" for a sibling edit in the same batch. The symptom
arrives far away: a runtime "key not present" for a field one replace
added to the type and the aborted batch never added to the value. So:
check the FILE after a failed batch, not the exit code or the
printout, and re-apply the whole intent, never the "missing half".

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

The same class, Windows spelling [D:windows-s2]: **`Start-Process`
launches DETACHED** — the child is not in the caller's process tree,
so no tree-walk can ever reach it. Every lifecycle fixture built on
it measures PowerShell's detachment, not the kill under test; the
Windows spike drew THREE false findings from one such fixture
(natural-exit orphans, Ctrl+C inert, BCL-kill short reach) and sized
a ~150-line job-objects session that a correct fixture
(`powershell -c "& tool"` — a genuine tree child) dissolved. A
Windows lifecycle fixture must use `&` or direct invocation, never
`Start-Process`.

**A normalisation's consumers are ENUMERATED, not remembered**
[D:windows-s3]. When a normalisation is introduced (the REPL dedent,
a mask, a decode), list every reader of the normalised thing and wire
each through the ONE function — the two you were thinking about is
how the third breaks. The dedent's pair (bufferComplete, submission)
was recorded as "must agree" and the COLORIZER — the third consumer —
shipped un-wired, losing head verdicts on leading-space lines. Same
shape as content-is-bytes (three sites found by collision, four more
by looking) and districtContentMask's one-mask-three-walkers. The pin
for a normalisation is an N-TUPLE across all consumers, not a pair.

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

**Duplication censuses hunt BLOCKS as well as records.** The regroup
census matched duplicated record/type SHAPES and missed a verbatim
×3 statement block (the ELet scheme-scooping sequence) and a ×2
word-search loop — found later by a token-window hash (6 normalized
lines) that a twenty-line script provides when no tool does. Two
distinct hunts, both owed: duplicated shapes AND duplicated
statement sequences; deliberate divergences found by either get an
adjacent comment stating why, never a merge.

## Content is bytes (versus every layer that may normalize text)

A byte-exact region — a block scalar today; a future here-doc, raw
block, or embedded language — collides with EVERY pass that
reasonably normalizes text elsewhere. The block-scalars session hit
three instances by collision rather than audit: `parseDocs` dropped
blank/`#` lines and stripped trailing comments before the block
parser ran; the assembler's total blank transparency would have
silently dropped blanks from content; the directive scan claimed
`#!/bin/sh` as a misplaced directive. The content-bytes audit found
four more the deliberate way: `stripComment`/the comment-only filter
vanished `//`-shaped content, the tab rejection reached inside
blocks, the sentinel split and fmt trimmed trailing whitespace, and
the doc-attachment/lint/canonicalization trio treated `///`-shaped
content as doc syntax. The question for every site — present and
future: does it run on lines that could be byte-exact content, and
if so does it preserve them byte-for-byte? The standing guard is the
hostile-byte fixture in ci/e2e.sh ([D:content-bytes]): one block
scalar carrying every hostile class, asserted byte-exact through
check and run, generated by printf because a checked-in literal with
trailing spaces, tabs, or CRLF invites the very mangling it guards
against. A new byte-exact region extends the fixture before it
ships; a new normalizing pass breaks the fixture rather than
shipping. The mask (`Script.districtContentMask`) is the shared
answer for line-walking passes that must not read content as syntax.

## Desugars never reference user-namespace names

A rewrite that inserts a NAME resolves that name in the user's scope —
so every desugar-inserted reference is capturable by an ordinary
declaration. Two instances proved it before the rule was written: the
reifiers were designed against it from day one (`|`-prefixed
un-typeable keys), and retry/poll was not — a user union case named
`Retry` captured `Retry.defaults` and the error talked about records
on a type the failing line never mentioned. The audit then found the
same class in every Seq-referencing rewrite (for/do, comprehensions,
ranges, indexers, splats — capturable by `type T = Seq of int`) and in
the arming pipe's `print` (shadowing print changed what BARE COMMANDS
meant). The rule: a desugar targets an internal `|`-key registered
with the SAME scheme and value objects as the public member
(reference-equality pinned), and the public spelling stays the user's
own — their shadow, their ordinary error. The TYPE half has its own
face: built-in type names refuse redeclaration (a silent retype of
`Retry`/`Option`/`Yaml` re-broke the sugar through the type after the
value was fixed).

## A checker walking source shapes needs a pin per DESUGARED shape

A desugar is exactly where the shape a checker was written against
disappears. Signature checking shipped walking `TECmd` — bare
commands and pipes, the shapes scripts use LEAST — and silently
missed every reified chain (`git … | succeeds`, the idiomatic form)
for two sessions, because the reifier desugar replaces the `ECmd`
with a builtin application spine. The rule: when a checker consumes a
source shape, enumerate every rewrite that transforms that shape
(reifiers, arming, splats, for/within/retry bodies, districts) and
pin each one — the pin battery is the enumeration, and a new desugar
joins it on arrival.


## A change that makes a spelling redundant sweeps for that spelling

Not merely "update the docs you touched": grep the superseded form
across every surface, INCLUDING prose, at the moment it is superseded.
The archaeology, two instances of the same disease:

- The `cmd | File.write` bullet recurred verbatim twice in unexecuted
  prose before anyone caught it — the prescription then was *a
  spelling that fails twice in prose gets a fenced twin*.
- The interp-show rider made `print (show r)` redundant and left THREE
  stale sites behind (the showcase's pair, `show` calls inside
  interpolation holes, and the docs framing show as the primary
  rendering path) — found one live sitting later.

This rule is the same prescription one step earlier: the sweep happens
in the session that supersedes, not the session that trips over it.
And the sweep REPORTS what it deliberately keeps — this one kept the
Secret renders (interpolation refuses; show masks) and a row-typed
hole (`$"{show c.port}"` — a bare hole defaults an unresolved type to
string), both now documented as show's niche rather than left to look
like misses.

## The bare partition is derived; consult the right gate

A member of an allowlisted module (`Seq`, `Str`) is bare iff its name
is SINGLE-HOME among those modules [D:bare-partition]; a two-home name
is qualified-only on both sides. A new MODULE contributes no bare
names unless `bareAliasModules` is deliberately widened — a pinned
set. So: when adding a MEMBER, check the two-home scan (a collision
DEMOTES the existing bare name — the pinned collision set fails until
someone decides); when adding a MODULE, check the allowlist pin. The
gate enforces both; knowing which one will fire saves the session
that discovers it. Naming incidents: bare `contains` resolving
silently to Str's while Seq's hot path errored "expected string", and
`Secret.map`/`Http.head` each stealing a bare slot for 22 unrelated
test failures before the allowlist inversion.
