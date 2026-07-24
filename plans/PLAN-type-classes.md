# weir — inferred type classes: Eq, Show, Ord

Status: SESSION A EXECUTED (2026-07-20, branch type-classes-a) —
opened by user fiat (trigger unfired, recorded), MACHINE-REGIME-ONLY
by user choice (the scoped-read pre-made overruled — the experiment's
boldest test; NOTES has it in those words). Session B EXECUTED (2026-07-21, branch
type-classes-b, machine-regime): Show + Ord landed; all three show
sentinel arms + hasFunction deleted; sortBy Ord-constrained and THE
RUNTIME CHECK DIED (check-first e2e: bad key, zero effects);
bare-show default retired (one pin amended with archaeology — the
session's only churn); Ord no-decomposition tripwired; oracle Same
x2 incl. the generic sort helper. 564 unit x2 / 130 e2e / 47 oracle
green. Session C EXECUTED (2026-07-21, branch
type-classes-c): axis-2 product matrix written first (13 cells);
battery all green with ONE scope correction, no code change (fn-typed
record fields reachable via generic instantiation — Box<fn> Eq
rejection pinned; A's "unreachable" note corrected); products across
generic unions/records, rows x2, mergeRows movement, nested
generalization escape, match guards, print sentinel, splices, pmap;
TRANSCRIPTION A/B addenda consolidated; the sentinel ledger CLOSED
with its arc in NOTES (the measure-arc's counterpart). 573 unit x2 /
132 e2e / 47 oracle green. ALL THREE SESSIONS COMPLETE.
Session A completion: machinery + Eq landed in budget (Scheme.Cs,
Ctx.Cons, demand/discharge, four arms; zero parser/eval/value
touches); == and Seq.contains re-typed, sentinels DELETED; dedupe
shape + rows x Eq battery + 2 tripwires + TRANSCRIPTION addendum
(flag 6) + oracle Same pins; ZERO existing-pin edits; oracle-mirror
drift caught by its own pin and fixed (formalization candidate
logged). 553 unit x2 / 127 e2e / 45 oracle green. The named trigger has NOT
fired (zero user-code generic-equality receipts — the sentinel
ledger stays honest). Session A additionally gates on an explicit
user blessing of the read regime (scoped constraint-core read, the
plan's own DECIDED lean, vs machine-regime-only as the deferral
experiment's boldest test); neither blessed yet. Drafted ahead of
trigger per user direction — the district precedent: settle the
design so the opened session is mechanical.

The named trigger, standing since the sentinel ledger was opened:
**receipts that user code cannot be generic over equality/show/
order** — a `let`-bound helper using `==` on a parameter, rejected
today by the reject-unresolved rule. The sentinel ledger (Eq:
`==`/`Seq.contains`; Show: `show`; Ord: `sortBy`) is the evidence
base; three customers, zero user-code receipts yet.

Scope correction recorded: GENERICS ALREADY EXIST (Damas-Milner,
generic unions/records, constructor schemes — shipped and probed).
This plan is the layer above: qualified types over the existing
machinery, Haskell-INFERRED in style (constraints attach silently
from use), Rust-like in feel at call sites, with weir's own
restrictions.

## The payoff, stated up front

1. The sentinel family RETIRES into ordinary constrained schemes:
   `(==) : Eq a => a -> a -> bool`, `show : Show a => a -> string`,
   `Seq.contains : Eq a => a -> seq<a> -> bool`,
   `Seq.sortBy : Ord b => (a -> b) -> seq<a> -> seq<a>`.
2. **The sole runtime type check DIES**: sortBy's scalar-key runtime
   rule is replaced by a static Ord constraint — "zero runtime type
   checks" becomes fully true for the first time. This is the
   headline; the e2e that proves it is a sortBy-on-function-key
   script rejected at CHECK time.
3. User code generalizes over the classes:
   `let dedupe = fun xs -> ...uses == ...` gets `Eq a =>` inferred
   and works at any equatable type — the capability sentinels
   structurally cannot provide.
4. **F#-fidelity GAIN, oracle-refereeable**: F# has exactly this as
   `when 'a : equality` / `'a : comparison` constraints, inferred
   the same way. Shapes that diverge today (generic-eq lambdas
   rejected) become Same. The oracle referees the flagship shapes;
   the divergence rows for reject-unresolved-`==` AMEND rather than
   grow.

## Pre-made decisions

- DECIDED — **Three classes, closed, compiler-owned, structural. No
  class declarations, no user instances, ever (in this plan).**
  "Instances" are the existing recursive shape predicates, promoted:
  Eq = no function anywhere in the type (the hasFunction walk,
  reused); Show = same predicate (today's rule — if they remain
  identical, ONE predicate serves both, noted, but the classes stay
  distinct in the type language because they will diverge the day
  Show gets custom rendering); Ord = int | string | bool exactly
  (today's sortBy runtime rule, made static; no record/union
  ordering — no receipts). Constraint solving on a concrete type =
  run the predicate; on an applied constructor = structural
  decomposition (Eq (Option a) ==> Eq a); on a bare var = the
  constraint RIDES the var.
- DECIDED — **Inference regime**: constraints accumulate on type
  variables during checking; at generalization, constraints on
  generalized vars move into the scheme (Forall gains a constraint
  set); instantiation freshens constraints with the vars (deep-copy
  discipline — the instantiate rules from the audit apply verbatim,
  now with one more thing to copy). A constraint on a var that
  resolves = solve immediately (fail with a located, demanding-site
  error). A constraint stranded on an unresolved var at statement
  end = error asking for context — the reject-don't-guess posture,
  IDENTICAL in spirit to today's `==` rule, just later and more
  permissive (today rejects at the operator; classes reject only if
  the whole statement leaves it unresolved).
- DECIDED — **Rows × classes is the novel surface and reads hardest**
  (the UoM×rows precedent: nobody else's test suite has been here).
  The rule: Eq on a row variable defers — recorded as a row-level
  constraint — and discharges when the row does (all fields' types
  must then satisfy Eq, recursively). The adversarial battery's
  center of mass is here: Eq demanded through a row field that
  discharges against a record containing a function field (reject,
  span at the demanding site); a generalized function with both a
  row constraint AND a class constraint on the same var;
  class-constraint residue after failure paths (no trial resolution
  exists — confirm nothing backtracks constraints; if anything does,
  the snapshot discipline applies).
- DECIDED — **The splice family does NOT become Show.** Command
  argv, interpolation holes, and print's scalar rule stay
  str/int/bool exactly — narrower than Show by design (the standing
  warning: `show` is a function producing string, not a widening of
  the splice family). `show` remains the explicit bridge.
- DECIDED — **No defaulting, no ambiguity resolution.** An ambiguous
  constrained var is an error naming the constraint and asking for
  an annotation-shaped fix ("pipe data in" / "bind with a concrete
  use") — weir has no ascription syntax and this plan does not add
  it; if class errors make ascription's absence bite, THAT is the
  ascription receipt (parked entry cross-referenced, §2.3
  discipline pre-noted).
- DECIDED — **Read-tax, confronted rather than inherited** (this is
  the plan's hardest honesty): this is the exact surgery class —
  constraint sets threading through generalize/instantiate/
  envFreeVars/bind, the audited arms — that the measure-variable arc
  was cancelled to avoid, and it lands under the read deferral. The
  deferral's reopen trigger ("first suspected soundness incident")
  is reactive; this feature deserves a proactive clause. DECIDED:
  the machinery session carries a SCOPED human read of the
  constraint core (the constraint-set operations + their
  touch-points in the four audited arms — judgment-on-paper, hours
  not days), regardless of the wider deferral. Not the full READ.md;
  the constraint delta only. If the user overrules this and runs
  machine-regime-only, that is the experiment's boldest test and the
  NOTES entry says so in those words. [User blesses one of these two
  explicitly before the session starts.]
- DECIDED — **Phasing, three sessions, each shippable**:
  - **Session A — machinery + Eq only.** Constraint sets on Scheme;
    accumulate/solve/generalize/instantiate/envFreeVars threading;
    `==` and `Seq.contains` re-typed; the sentinel arms for both
    DELETED (the retirement is the proof the machinery is real);
    user-code generalization over Eq pinned (the dedupe shape). The
    rows×Eq rules and their battery. Oracle Same pins for the
    F#-constraint shapes. Deferral-regime tax at maximum + the
    scoped read.
  - **Session B — Show + Ord; the runtime check dies.** `show`
    re-typed, its sentinel deleted; sortBy's Ord constraint replaces
    the runtime scalar rule (the deletion is the headline e2e);
    `sort`/`min`/`max` become writable if wanted (only sortBy's
    re-type is in scope; new members wait for receipts).
  - **Session C — hardening.** The retroactive product sweep pattern
    applied to the new axis: classes × rows × generics ×
    generalization pairwise battery; TRANSCRIPTION addenda
    consolidated; the sentinel ledger CLOSED with its arc written
    (opened → three customers → machinery → retired) — the
    measure-arc's counterpart, the speculative-machinery precedent's
    other outcome: machinery built when the ledger justified it.
- DECIDED — **Stop-and-report budget**: if constraint threading
  wants changes beyond the four named arms + Scheme + the solve
  function — in particular, if it wants to touch the parser, the
  value domain, or eval — STOP; the plan's model is "static filter
  only, zero runtime presence" (the interpreter dispatches on Value
  shape already; classes must be fully erased). Any runtime
  representation of a constraint is a model violation, not an
  implementation detail.

## Error-message contract (decided now; message quality is the
feature's usability)

Constraint failures locate at the DEMANDING site with the chain
visible when indirect: "cannot sort by this key: `_.Handler` is
`int -> unit`, and functions cannot be ordered" (direct);
"`dedupe` requires its elements support `==`; `Proc` contains a
function field `OnExit`" (through a scheme, naming the instantiation
site). The battery pins message SHAPES, not just rejection.

## Parked

- User instances / class declarations — reopens only with receipts
  AND a coherence design; the closed-structural regime is the reason
  this plan is tractable.
- Custom Show rendering (the day Show differs from Eq's predicate) —
  with user instances.
- Ord on records/unions (lexicographic/declaration-order) — no
  receipts; the error message names the limitation.
- Ascription syntax — cross-referenced above; class-error friction
  is its named receipt source.
- `Seq.sort`/`min`/`max`/`Seq.dedupe` — one members-session after B,
  on receipts; the classes make them one-liners.
