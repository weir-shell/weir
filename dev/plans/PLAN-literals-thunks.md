# weir — literal patterns, `()` thunks, and tuples

Status: Session 1 EXECUTED (2026-07-21, branch literals-thunks).
Blessed as received; the full proposal text lives in the
conversation record. Session 1 scope: int/string literal patterns
(F# completion rule; guard idiom stays legal; no-literal-patterns
row retired, corpus pin flipped Same), `()` unit params (checker
arm pinning TUnit — the generalization trap tripwired as the arm's
reason), `()` match pattern (irrefutable). Evidence honesty: the
thunk receipt had NOT arrived — user choice, recorded.

## Session 1 completion (2026-07-21)

14-pin battery + tripwire + TRANSCRIPTION addendum; position sweep
(nested constructor literals, both let forms, lambdas, mixed
params); SKILL must-fail flipped to must-pass (doc-test proved);
GUIDE updated; oracle: 2 Same + 1 Diverges — the naive exhaustive
pin REFUSED by the oracle, surfacing the never-pinned
exhaustiveness-hard-error divergence (new row). One old pin flipped
with archaeology. 588 unit x2 / 135 e2e / 51 oracle green.

## Session 2+ — tuples: EXECUTED (2026-07-21, branch tuples)

Reversal archaeology was the first commit (as required). Comma
VERIFIED unclaimed; pairwise VERIFIED record-shaped and migrated
(breaking, archaeologized). Tuples were boring — stop-and-report
never fired. from json: REJECT via the existing field whitelist (no
new code — reported as the plan asked). Corpus re-mine skipped:
WEIR_CORPUS_DIR absent (reported, not dropped). 3 rows retired /
3 born; oracle 57 green incl. lexicographic-Ord and product-
exhaustiveness divergence pins. 601 unit x2 / 139 e2e green.

## (original gate text follows)

The structural gate (type-classes landed) is satisfied. Not started;
opens on user call. First commit must be the reversal archaeology
(records-are-the-product, dated, what held, what pressured, scope).
Pre-mades as blessed: F# tuple types/literals/patterns arity 2+,
comma-claim VERIFY, multi-payload constructors un-restrict, no Ord
on tuples (row), not spliceable/not Env.load, from json REJECT
default, records stay the taught product, Seq.zip ships with
(pairwise re-type VERIFY), full ceremony + corpus re-mine
time-boxed. STOP-AND-REPORT if tuples are not boring.

## Pattern-binders session: EXECUTED (2026-07-21, branch pattern-binders)

(The user-blessed binders plan — full text in the conversation
record.) All six form-examples run (e2e); refutable binders reject
with the contract message; per-name generalization pinned (polymorphic
component beside ground component; class constraints ride the right
name; env-free containment tripwired). Bare comma at full F#
precedence; comma x `;` decided (comma tighter); command argv pinned
inert from both sides. REPL per-binding display pinned by probe.
Session catches: uppercase-shadow fall-through; the check-mode
ELambdaPat twin (TRANSCRIPTION flag 7). 616 unit x2 / 144 e2e / 60
oracle green; rows: shipped shapes flipped Same, refutable-binder
remains as the row content.

## Casing-law session: EXECUTED (2026-07-21, branch casing-law)

Check-time (reported): checkBinderName at every binder arm (ELambda
infer+check twins, both ELet arms, binderShape PVar) + the three SLet
folds (Script/REPL/oracle mirror). Binder-first error ordering (the
binder is what the user wrote — judged before the value). Intent-
aware PCase diagnosis: unknown uppercase name → casing hint; KNOWN
constructor → "can fail; use match" (the parked single-case-union
binder's pre-scoping updates: under the law, uppercase in a binder
pattern is unambiguously a constructor if that park opens — the
env-lookup disambiguator already exists). Migration grep yield: ZERO
(the convention held). Shadow pins flipped with archaeology
(grammar-dead, EField logic stays as depth); `_x` lowercase-class
and `_` wildcard pinned; AWS_REGION shape end-to-end; match-leak pin
green. Divergence row lowercase-binds says the honest thing: the
strictness family's first STYLISTIC member. 621 unit x2 / 145 e2e /
63 oracle / 28 doc blocks green.
