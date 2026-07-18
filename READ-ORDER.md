# Consolidated read — order and verdict protocol

Anchored to the measure-removal commit (see PLAN-remove-measures.md for
the hash; supersedes the original d12aefd anchor — TRANSCRIPTION.md was
regenerated there, smaller: §4 retired with the measures). Method: per arm, (1) verify the
TRANSCRIPTION.md rule matches the code — mechanical; (2) verify the rule
is standard against the reading (Christiansen for bidirectional mode
discipline; the HM walkthrough for bind/instantiate/generalize; Leijen
§1–3 for rows). Verdict per arm: recognized / derived-sound / finding.
Output: READ.md with arm-by-arm status and the closing sentence
("the checker has been read by a human against the checklist; debt
closed as of <commit>"). Part 2 of the plan is gated on READ.md.

The path (a→g), with what each step is for:

a. `instantiate` (L141) + the row deep-copy — the original audit's #1
   question, now also serving constructor schemes and module members.
   Read with TRANSCRIPTION flag 1 in hand.
b. `bind` (L185) — the TNamed pairwise arm is new since the audit;
   re-verify substitute-before-recurse remained structural after
   generics (flags 2). Occurs (L122) now walks TNamed args — checklist
   §1.1 re-verdict happens here.
c. `mergeRows` (L233) / `dischargeRow` (L209) — unchanged shape since
   the audit; the quick re-skim is for the generics interaction: a row
   constraint whose field type is an applied constructor (the ⟦ps↦as⟧
   premise). Composition probe (a) fails here first if anything is
   wrong.
d. `substParams` (L60) and its four call-site families: checkPattern
   payloads (L364), EField nominal (L533), dischargeRow (L209),
   isEquatable. The question at each site: are decl-side names fully
   eliminated before ctx names can appear?
e. `checkDecl` constructor schemes (L934) — verify σc = ∀params.
   payload → Self<params> and that Forall covers exactly the params
   (checklist §3 re-verdict: constructor schemes freshen like lets).
f. `envFreeVars` (L181) — transitive reachability, now composed with
   TNamed: a var reachable only inside an applied-constructor argument
   inside a row constraint of an env-free var. Composition probe (b) is
   this as a test; the read confirms the mechanism (finalTy expands
   both layers) rather than the instance.
g. EField module arm (L517) — three-way precedence (value → module →
   row-field is syntactic first-match; the value case *falls through*
   rather than being tested inside the arm — confirm the when-guard
   encodes it) and member instantiation (§3 discipline again; same
   instantiate).

Checklist items formally reopened and requiring re-verdict in READ.md:
§1.1 (occurs through TNamed args), §1.5 (the `==` unify-then-equatable
fix — verify rule, not patch; typeBinOp L273), §3.1/§3.3 (freshening now
covers constructor schemes and module members).

Composition probes live in tests/Weir.Tests/Tests.fs ("Read probes"
group) — all green as of this commit; a future failure gives the read a
concrete entry point.
