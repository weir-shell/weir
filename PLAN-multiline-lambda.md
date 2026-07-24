# weir — multiline lambdas: `(fun ... ->` opens a body block

Status: BLESSED (user 2026-07-24). One session. GATED on fuzzer
Session 1 landing (the sequencing the fuzzer plan settled: this
feature adds offside surface to the layer currently consuming the
user's hours; the harness is the hardening, and this is its first
new-grammar customer — fuzzer-grammar membership is part of THIS
plan's acceptance, per the new PROCESS obligation).

Origin: receipts at two — the git-subrepo port's friction entry
(multi-statement lambdas spelled single-line with greedy-`;`,
"the parens-spanning park's face") and the user's direct dislike
at review. The park this opens is NARROW: not general
parens-spanning-lines (that park stands), but exactly one shape —
a lambda whose `(fun ... ->` dangles at EOL opens a body block
closed by its own `)`.

(Blessed plan text as delivered; see the session report below for
the executed decisions and premise corrections.)

## Session report (2026-07-24)

Gate held: fuzzer Session 1 landed, harness green on HEAD. The
probes-first pass found MOST of the feature already emergent from
the indent machinery (deeper-line bodies, block lets, siblings,
compounds via the prune, districts, blanks, nesting, the
pipeline-stage-spanning receipt shape) — the session's real cells:

- The Lambdas stack member on Pend (open line, opener indent,
  depth-before, restore level), armed by the lambdaOpens scanner
  (classify layer, per the formalization rule): closer-alone lines
  join at any indent (col-0 law suspends while open), EOF names the
  opener, a line LEFT of the opener is a named leak error.
- FCS verdicts: body at the opener's indent ACCEPTS (F#-parity —
  a pre-change silent weir-reject, fidelity gain); body left of the
  opener is weir-stricter (F# floors at the enclosing context) —
  NEW divergence row lambda-body-offside; closer-alone accepts
  everywhere (pre-change reject, gain).
- The RESTORE rule earned two harness catches in one session: the
  fuzzer (attached closer + block sibling misjoined as application —
  a swallow shape, pre-existing) and e2e (the fold-init counter-
  shape: opener-level restore over-sequenced Seq.fold's init).
  Resolution: Pend tracks StmtLevel (where the current statement
  started: sibling/`in` joins and first-line-after-dangling-head);
  the lambda records it at arm and restores it on pop. Both shapes
  pinned.
- Parser: lambda bodies inherit the letCmdOk spine (command
  block-lets legal on a let-RHS spine — the block-let-cmd boundary
  widened by this plan's decision) AND lambda params extend the
  ambient resolver (the tools/test-counts regression: a param-headed
  let RHS became a phantom command under the assume-resolver;
  pinned).
- Premise corrected: multiline lambdas inside $() were ALREADY
  legal (physical continuation; the single-LOGICAL-line law holds
  unchanged) — the planned rejection pin replaced by the actual-law
  pin.
- fmt: both closer placements accepted, bodies canonicalize to +4,
  no line surgery — exactly the plan's expectation; safety guard
  green over the corpus.
- Fuzzer membership: SIterLambda/SMapLambda shapes + the
  lambdaSingle transform (multiline ↔ single-line `;`/`in` form) +
  composition; deep run (10k, fresh seed) green after the catches.
- Port: the friction site rewrote multiline; flagship checks and
  lifecycle e2e green. Docs: SKILL + GUIDE sections, SEMANTICS
  rules, POSITIONS row, divergence row, DECISIONS row.

Budget: the assembler mechanism landed ~90 lines against the ~40
estimate — the overage IS the session story (the restore rule's two
counter-shapes); reported per the budget clause.

## Parked (as blessed, standing)

- General parens-spanning-lines (non-lambda) — the park stands,
  content shrunk to non-lambda parens; reopen on its own receipts.
- `function`-sugar multiline — unchanged; inherits this bracket
  kind if its restricted form ever opens.
- Multiline lambdas in sigil interiors: NOT an exclusion after all —
  already legal via continuation (premise corrected above).
