# weir — the assembler fuzzer: generative line-shape testing

Status: BLESSED (user 2026-07-24). Two sessions. Origin: the cost
signal — "most of the time on indentation bugs" — plus the ledger
agreeing: the assembler seam owns the majority of recent incidents
(compound-paren, district-dedent, type-decl continuation, fmt
drift), and EVERY one lived in an unnamed product triple. The
product matrix pins pairs someone named; this harness probes the
space nobody enumerated. It is also the widen-the-net answer the
silent-swallow postmortem raised and parked: the measurement net
extends over the grammar seam by GENERATION, not by more hand
pins. Prediction, on record: the first deep run finds at least one
real bug — the base rate says so.

## The design: metamorphic properties over generated programs

Not random bytes — a GENERATOR of valid-by-construction programs
from a combinator grammar of weir's line shapes: compounds
(if/then/else, match+arms), block-lets (nested), lambdas (single-
line; multiline joins the grammar WHEN that feature lands — the
harness is its acceptance rig), brackets ({} record literals/type
decls, [] lists, Stroustrup and inline styles), districts (plain,
env-parameterized, standalone and headed), sigils, pipelines (|
chains, |> stages, column-0 continuations), siblings, comments,
blank lines, attributes — composed at randomized depth/order/width
with expression bodies kept TRIVIAL (print markers, int literals):
the subject is line-shape composition, not type complexity.

The invariants, in value order:

1. **Metamorphic equivalence (the crown — the silent-swallow
   killer)**: for program P and a semantics-NEUTRAL transform
   T(P), the AOT binary's output must be byte-identical. The
   transform library IS the language's own equivalence claims,
   each already asserted somewhere in the ledger:
   blank-insertion inside blocks/brackets (the transparency law);
   comment-insertion anywhere transparent; whole-block re-indent
   by +k (offside is relative); district ↔ explicit !(...) lines
   (the marker's desugar claim); bare command RHS ↔ $(...)
   (the pinned equivalence, now property-tested at scale);
   single-line `a ; b` ↔ block siblings (the assembler's join
   claim); Stroustrup ↔ inline bracket style.
   Effect ORDER is part of output identity (print markers make
   order visible). Every transform pair is a law the docs state —
   the fuzzer checks the laws hold under composition, which is
   exactly where they have failed.
2. **Total assembly**: assembler/parser/checker return
   Result-or-diagnostic on EVERY generated program and every
   MUTATED one (line deletion, indent perturbation — the invalid
   neighbors): no exception, no hang (timeout), no
   assembled-text panic. The never-crash floor.
3. **Span soundness**: inject a known-bad token at a chosen
   physical position in a generated program; the reported
   location must be that line (col within the line's extent) —
   translate() audited generatively instead of by the hand-picked
   multibad cases.
4. **fmt roundtrip at scale**: fmt(P) parses to the same sexpr
   shape (the respace guard's own predicate, reused — one shape
   language); fmt idempotent (fmt∘fmt = fmt); fmt(P) re-satisfies
   invariant 1 against P.

## Pre-made decisions

- DECIDED — **FsCheck, test-side** (the FCS precedent: test deps
  are unrestricted; AOT discipline is binary-only). Custom
  generators over the combinator grammar; FsCheck's shrinking
  augmented with domain shrink (line-block removal — delta
  debugging on lines is natural here and produces the MINIMAL
  REPRO the incident protocol wants). If FsCheck's model fights
  the generator [possible — stateful indent contexts], the
  fallback is a hand-rolled seeded generator + line-delta shrink;
  report which.
- DECIDED — **Determinism and budget**: seeded; CI runs a small
  smoke (seed pinned, ~200 cases per invariant — the suite must
  stay fast per the timing discipline); a DEEP run (10k+, fresh
  seeds) is a manual/nightly command (tools/fuzz.sh) whose
  failures commit their shrunk repro + seed. CI never runs fresh
  seeds (flaky-CI is a masked-failure factory — the harness
  gates apply to the harness).
- DECIDED — **Finds are pins, triaged per the incident protocol**:
  every failure ships as (a) the shrunk repro committed as a
  named pin, (b) a ledger entry classifying it
  (silent-swallow-class? span? crash?), (c) the postmortem
  question ("could a named product cell have caught it?") —
  building the evidence for whether hand-pinned matrices remain
  worth their maintenance once the generator exists.
- DECIDED — **The generator grammar is a committed artifact**
  (tests/fuzz/GRAMMAR.md or the combinator file's doc header):
  which shapes it can produce, which it cannot yet (multiline
  lambdas until they land; multiline strings never), so "the
  fuzzer passed" has a stated denominator — the coverage-claim
  honesty rule from the corpus work, applied here.
- DECIDED — **Two sessions**: Session 1 = generator core +
  invariants 1 (three transforms: blank, comment, re-indent) and
  2; the first deep run's findings triaged. Session 2 = the
  remaining transforms (district↔sigil, bare↔$(), sibling↔`;`,
  bracket styles) + invariants 3 and 4; the harness wired into
  CI smoke + tools/fuzz.sh; PROCESS.md gains the line ("new
  assembler features add their shapes to the fuzzer grammar +
  their equivalence claims to the transform library — the
  metamorphic law is part of the feature").
- DECIDED — **Multiline lambdas sequencing settled by this plan**:
  they land AFTER Session 1 at the earliest, and their plan's
  acceptance includes fuzzer-grammar membership — the hardened
  layer is the prerequisite the review named; the harness is the
  hardening.

## Work items

Session 1: generator combinators + trivial-body emission; runner
against the AOT binary (stamp-gated per the standing mechanism);
invariant 2; invariant 1 with three transforms; shrink; the deep
run + triage; GRAMMAR.md.
Session 2: the transform library completed; invariants 3–4; CI
smoke + fuzz.sh; PROCESS line; NOTES (the widen-the-net park
formally closed with this as its answer — the postmortem's owner
question resolved).

**Done when:** CI smoke green and fast; the deep run executed with
findings triaged (or the boring outcome recorded with its
denominator); every ledger equivalence claim in the transform
library or named as excluded; the grammar artifact states
coverage; PROCESS carries the new-feature obligation; the
prediction graded.

## Parked

- Fuzzing the CHECKER (generated well-typed/ill-typed terms) — a
  different generator, a different plan; the deferral regime's
  machine net could want it someday, noted for the experiment's
  owner.
- Coverage-guided (AFL-style) fuzzing — the metamorphic approach
  is the right first instrument; feedback-guided is a later
  escalation if the boring outcome arrives suspiciously fast.

## Session 2 report (2026-07-24)

Executed: the four spelling transforms (district ↔ `!(...)`, bare ↔
`$(...)`, siblings ↔ `;` bounded to print-only bodies by probe —
inner lets spell `in`, commands take `;` as argv — and Stroustrup ↔
inline), the composition property now spanning every flip at once,
invariant 3 (span soundness over renderer-tagged expression lines),
invariant 4 (fmt: succeeds/idempotent/sexpr-shape/output-neutral),
CI smoke wired (.gitlab-ci.yml + ci/local.sh, pinned seed, after
publish), tools/fuzz.sh (fresh-seed deep driver, prints its seed),
the PROCESS grammar-membership rule, NOTES closure of the
widen-the-net park. FINDS: invariant 3 caught two span classes on
its first smoke — (1) a parse error after a district anchors on the
wrapped segment (col past the physical line), true site only in a
backtrack note; (2) junk in a nested arm after a completed outer arm
triggers the consumed-`|` fatal upstream, the teaching hint
mis-aimed. Both hand-minimized, pinned as current behavior in
Weir.Tests; the re-anchor policy (who wins the furthest-error
competition across wraps and fatals) is left as an OPEN DECISION —
strict positional assertion gated behind WEIR_FUZZ_STRICT_SPANS
until it lands. DEEP RUN (seed 8675309, 10k/invariant): invariants
1/2/4 clean; invariant 3's hard floor caught the PREDICTED bug — a
check/run verdict split (check rc0 + warning, run rejects) where a
block-let RHS headed by a KNOWN binding is claimed by the
assume-resolver as a command and junk becomes argv. Prediction
regraded FOUND (NOTES). Deep runs stay red on that class until the
one-clause assume-resolver fix (stop claiming env-known names) is
blessed and lands.

## Session 1 report (2026-07-24)

Executed as planned; FsCheck held (the fallback stayed unused —
state-passing repetition combinators reconcile the gen builder
with scope threading). Landed: tests/Weir.Fuzz (generator,
renderer-as-transform for re-indent, stamp-gated runner, shrink
with dependency closure), invariant 1 with blank/comment/re-indent
PLUS their composition, invariant 2 with delete/perturb/duplicate/
swap mutators, tests/fuzz/GRAMMAR.md. Smoke: pinned seed 1789001,
200 cases/invariant, ~5s. Deep: three 10k runs, fresh seeds
(424242, 777, 20260724) — zero product findings; the prediction is
graded MISSED against the committed denominator (NOTES entry).
Bring-up findings: three generator-grammar corrections (bare
commands top-level-only; block-body command RHS `;`-join; ambiguous
same-field-set records) and one claim-vs-behavior doc find
(SKILL.md still taught the retired blank-line-ends-statement law —
fixed to the col-0 law). Equality detector carries a graded
positive control.
