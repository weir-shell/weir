# Checker transcription — judgment forms per arm

REGENERATED post measure-removal (2026-07-18) — the second edition,
smaller: §4 (measures) retired entirely, measure cases gone from bind
and typeBinOp, the splice rule simplified, and the post-anchor addenda
of the first edition (interpolation, unit, print) folded in as ordinary
arms. **The read anchor is the measure-removal commit** (recorded in
PLAN-remove-measures.md on completion; the first edition's anchor
d12aefd is historical). `src/Weir/Check.fs` line anchors below are
against that commit.

Notation: `Γ ⊢ e ⇒ τ ⊣ Δ` (infer: synthesize τ, constraint-store delta
Δ), `Γ ⊢ e ⇐ τ ⊣ Δ` (check against τ). `Δ` covers mutations of `Ctx` =
(Subst S, Rows R, Fresh counter). `σ` ranges over schemes; `ρ` over row
variables; `⟦ps↦as⟧τ` is `substParams`. Read alongside READ-ORDER.md.

Arms marked **FLAG** resist single-rule transcription (two jobs, or
order-dependent side effects) — flags are findings before the read
starts.

## Substrate

### substParams — L62
    ⟦ps ↦ as⟧ : capture-free simultaneous substitution of decl params by
    argument types; structural through TFun/TSeq/TNamed args/TRowVar
    field snapshots; identity elsewhere.
Pure. Single job. Soundness rests on decl-param names never colliding
with ctx-fresh names (decl side substituted before ctx interaction).

### occurs — L124
    occurs(v, τ): v ∈ vars(resolve*(τ)), walking TFun, TSeq, TNamed
    args, and row *constraints* via R (with a seen-set on row names).
Single job. Consulted only from bind's TVar arm — row-var cycles rely
on substitute-before-recurse ordering instead (see dischargeRow).

### instantiate — L143  **FLAG (two jobs)**
    σ = ∀V. τ    fresh renaming θ : V → fresh names (rows→rN, else aN)
    ─────────────────────────────────────────────────────────────────
    Γ ⊢ x : σ ⇒ θτ ⊣ R += {θρ ↦ θ(snapshot(ρ)) @ use-span | ρ ∈ V}
Two jobs: (1) alpha-rename quantified vars; (2) install renamed row
snapshots into R with the use site's span. Job 2 is the deep-copy
discipline (audit §3.1) — the snapshot in the *type* is the source of
truth crossing generalization boundaries; R is derived state. Verify θ
is applied inside snapshots before installation.

### envFreeVars — L183
    FV(Γ) = ⋃ { vars(finalTy(σ.Ty)) \ σ.Forall | σ ∈ Γ }
Single job. Transitive reach through row constraints holds because
finalTy expands R before vars() runs. Composition question (read item
f): a var reachable only as `ρ ↦ {X: Option<'a>}` with 'a env-free —
probe (b) in ReadProbes pins the instance; the read confirms the
mechanism.

## Unification

### bind — L187
Equal (after shallow resolve):
    ────────────
    τ ~ τ ⊣ ∅
TVar (either side), with occurs:
    v ∉ occurs(τ)
    ─────────────────
    v ~ τ ⊣ S += v↦τ
TNamed pairwise:
    n = n'   |as| = |as'|   aᵢ ~ a'ᵢ ⊣ Δᵢ  (left to right)
    ──────────────────────────────────────────────────────
    n<as> ~ n'<as'> ⊣ ⋃Δᵢ
Row ~ nominal → dischargeRow; row ~ row → mergeRows; row ~ other →
error. TFun/TSeq structural. Else mismatch (expected-first orientation).
Post-removal note: int-vs-int is now the plain equal case — the measure
comparison that lived here is gone, and with it the mismatch subclass.

### dischargeRow — L211  **FLAG (order-dependent side effect, load-bearing)**
    Γ(n) = record(ps, fields)
    S += ρ ↦ n<as>                                   ← BEFORE premises
    ∀(f, τf, span_f) ∈ R(ρ):
        f ∈ fields with decl type d ⟹ ⟦ps↦as⟧d ~ τf ⊣ Δf @ span_f
        f ∉ fields ⟹ error at span_f (did-you-mean)
    ──────────────────────────────────────────────────────────────
    ρ ~ n<as> ⊣ S ∪ ⋃Δf
Substitution installed before recursing into constraints — the
termination argument for row cycles (audit §1.1).

### mergeRows — L235  **FLAG (same ordering discipline)**
    S += ρ₁ ↦ ρ₂                                     ← BEFORE premises
    ∀(f, τf, span) ∈ R(ρ₁):
        f ∈ R(ρ₂) with τ'f ⟹ τ'f ~ τf ⊣ Δf
        f ∉ R(ρ₂) ⟹ R(ρ₂) += (f, τf, span)
    ────────────────────────────────────────────────
    ρ₁ ~ ρ₂ ⊣ ...
Field types unify (never name-union) — audit §1.2. R(ρ₂) re-read per
field because the loop mutates it.

## Operators and splices

### typeBinOp — L298  **FLAG (retry recursion)**
Var-defaulting family: `*`,`/` on two unresolved ⟹ bind both int then
retry; `&&`,`||` similarly to bool; one-var-one-prim ⟹ bind, retry.
Retry terminates because binding strictly shrinks the unresolved set.
Concrete arms are textbook on bare int/string/bool — the
measure-preservation side conditions (`+`/`-`/compare same-measure)
collapsed to plain int rules with the removal.
Equality family:
    a ~ b ⊣ Δ    equatable(finalTy(a))
    ──────────────────────────────────
    a ==/<> b ⇒ bool ⊣ Δ
Unify-then-equatable is the rule, not a patch; no syntactic fast path.

### checkScalarSplice — L947 (shared by ECmd args and interp holes)
    Γ ⊢ e ⇒ τ,  resolve(τ) ∈ {str, int, bool}   (TVar ⟹ bind to str)
    ─────────────────────────────────────────────────────────────────
    splice(e) ok
One rule for both splice kinds. "int" is now unqualified — the "any
measure" clause retired.

### printArgTy — L286 (print's argument rule)
    scalar family as above, or seq with str/TVar elements ↦ seq<string>;
    seq<unit> errors with the Seq.iter hint.
Guarded by the unforgeable print sentinel scheme (∀__print); a user
`let print = ...` shadows it and every print rule falls through to
normal paths.

## Inference — infer (L416)

Literals: axioms (EInt ⇒ int, EStr ⇒ string, EBool ⇒ bool,
EUnit ⇒ unit at L433).
### EVar — L438
    print (sentinel, unshadowed) ⇒ str → unit    (defaulted bare-value form)
    Γ(x) = σ ⟹ x ⇒ instantiate(σ)
    x ∉ Γ: module / moved-name / unbound error shapes
### ELet — L474  **FLAG (generalization side condition)**
    Γ ⊢ e ⇒ τ ⊣ Δ    τ* = finalTy(τ)    V = vars(τ*) \ FV(Γ)
    Γ, x:∀V.τ* ⊢ body ⇒ τb ⊣ Δ'
    ────────────────────────────────────────────────────────
    Γ ⊢ let x = e in body ⇒ τb
Reached from surface syntax by explicit `let ... in` AND by block lets
(assembler token insertion). FV(Γ) under the Δ-updated ctx; finalTy
bakes row snapshots at generalization — paired with instantiate's job 2.
### ELambda: fresh param, body inferred.
### EApp → checkSpine (L825)  **FLAG (three jobs)**
    arity/message selection; piped-first binding (the semantic core —
    the reason lambdas in poly positions check); typed-chain
    reconstruction. Read separately. Print-headed spines intercept
    before this with printArgTy.
### EPipe arms: ETo special-case; ECmd (arg ⇐ seq<string>); print
(printArgTy); general (spine with piped=Some).
### EField — module arm L585 (value-shadow falls through; member
instantiation rides the audited path), nominal (⟦ps↦as⟧d), TVar-upgrade
**FLAG (side-effectful premise: creates ρ)**, row-extend (span
discipline: discharge errors point at the demand site).
### EBinOp → typeBinOp. ERecord: exact field set, fresh args, fields
checked under ⟦ps↦as⟧. EFrom/ETo: adapter typing. ECmd — L736: args
via checkScalarSplice; seq<string>. EInterp — L751: holes via
checkScalarSplice; ⇒ string; literal parts inert. EList: empty ⇒
seq<fresh>; else head infers, rest check. EMatch: scrutinee resolved;
arm 1 infers, later arms check; patterns bind via ⟦ps↦as⟧ payloads
(L389).

## check (L895)
Lambda vs TFun: param ⇐ dom; body checked if cod ground, else
inferred-and-bound. Lambda vs non-fun: error. Let (L918): same
generalization as infer-ELet — **FLAG: two code sites for one rule
(drift risk, not unsoundness)**. Fallback: infer then bind.

## checkDecl — L1030
    params distinct    fields/payloads validated
    union ctor c of payload p: σc = ∀params. p → Self<params>
Constructor schemes are ordinary Schemes — instantiation rides the
audited path (§3).

## Post-anchor addenda (bool-branching session — after the measure-removal anchor)

### EIf
    Γ ⊢ c ⇐ bool    Γ ⊢ t ⇒ τ    Γ ⊢ e ⇐ τ
    ─────────────────────────────────────────
    Γ ⊢ if c then t else e ⇒ τ
    Γ ⊢ c ⇐ bool    Γ ⊢ t ⇒ unit
    ─────────────────────────────    (no else: unit-valued; tailored
    Γ ⊢ if c then t ⇒ unit           error otherwise)
Row merge across branches is the match-arm discipline (else checks
against then's type); no new unification machinery.

### EMatch extensions
Arms are now (pattern, guard?, body). Guard: Γ+bindings ⊢ g ⇐ bool.
PBool patterns: scrutinee must be bool; an unresolved scrutinee with a
bool pattern pre-binds to bool (defaulting precedent). Exhaustiveness:
guarded arms never count (coverage or terminal reachability); bool
scrutinee is exhaustive iff unguarded true AND false appear.

## Post-anchor addenda (typed-env session, 2026-07-20)

### Env.load T (bespoke arm — from-json's first expression-position sibling)
    shape: (Env.load) tyName   [Env unshadowed, module present]
    Γ_types(tyName) = record(∅, fields)    ∀(f, τf): τf ∈ {str, int,
    bool, Option⟨str|int|bool⟩}
    ──────────────────────────────────────────────────────────────
    Γ ⊢ Env.load tyName ⇒ tyName    [TEEnvLoad def]
The type-name-in-special-position machinery is EFrom's (monomorphic-
record guard included), relocated; no new resolution concepts. Runtime
is boundary validation (collect-then-raise-once), joining the existing
failure class; TEEnvLoad is inert to finalize/warnings (carries a
RecordDef, no types to walk).

## Flags summary (pre-read findings)

1. instantiate: two jobs (rename + R installation).
2. dischargeRow / mergeRows: substitute-before-recurse is load-bearing.
3. checkSpine: three jobs braided; piped-first is the semantic core.
4. EField TVar-upgrade: side-effectful premise.
5. ELet/check-ELet duplicate the generalization computation.
(The first edition's §4/measure content: retired — see the NOTES
measure-removal arc.)

## Class constraints (consolidated — Sessions A/B/C, 2026-07-21)

Machine-regime feature (no human read at any session — the deferral
experiment's boldest test, NOTES has the arc). Closed class family
{Eq, Show, Ord}: compiler-owned, structural, inferred, fully erased.

    demand:  Γ; C ⊢ K τ           (K ∈ {Eq, Show, Ord})
      τ = α                        → C := C ∪ {α : K}
      τ = ρ (row var)              → C := C ∪ {ρ : K}   (rides; the
                                     row's discharge re-demands)
      Eq:   {int,str,bool,unit} ⊤ ; fun,seq ⊥ ; N⟨τ̄⟩ decompose
      Show: {int,str,bool,unit} ⊤ ; fun ⊥ ; seq⟨σ⟩ → K σ ;
            N⟨τ̄⟩ decompose         (Show ⊃ Eq: seqs render)
      Ord:  {int,str,bool} ⊤ ; ALL else ⊥ — no decomposition
            (tripwired: orderable fields ≠ orderable record)
      decompose is cycle-keyed on formatTy; failure formats the
      ORIGINAL demanded τ at the DEMANDING span (legacy message
      families kept verbatim for Eq/Show; Ord speaks the plan's
      error contract).

    discharge (the ONLY solve trigger; nothing backtracks):
      bind α := τ        → demand C(α) against τ
      mergeRows ρ1 → ρ2  → C(ρ1) moves to ρ2      (product-pinned)
      dischargeRow ρ → N → demand C(ρ) against N (before field binds
                           — substitute-before-recurse extends to
                           the discharge: it must see the nominal)

    generalize (both ELet arms + statement boundary):
      Forall = fv(τ) − envFree;  Cs = C↾Forall;  C := C ∖ Forall
      — env-free constraints stay AMBIENT (tripwired); nested
      escape climbs to the outer scheme (product-pinned)

    instantiate: mapping α→α′ freshens Cs(α) onto α′, instantiation
      span = new demanding site, describe per class (deep-copy and
      per-use independence tripwired)

    statement boundary (typecheckWith): pending on α ∈ fv(τ_result)
      → residue (Script/REPL/oracle-mirror generalize via
      generalizeWith — the mirror drifted first, its own pin caught
      it); α ∉ fv(τ_result) → ambiguity error (no defaulting).

    Erasure: Cs never reaches finalize, TypedExpr, or the value
    domain (products pinned across splices and pmap workers). The
    sole runtime type check (sortBy's scalar keys) is DEAD —
    check-first e2e: bad key ⇒ zero effects.

Reachability correction (Session C matrix): fn-typed record fields
are undeclarable but REACHABLE via generic instantiation
(Box⟨'a⟩ at { V = print }) — Eq decomposition must and does reject
through them; Session A's "unreachable" scope note was wrong for
generics.

Flag 6: dischargeRow has three jobs braided (subst, class
discharge, field binds).

## Addendum — literal patterns and () params (2026-07-21)

Pattern kinds gain PInt/PStr/PUnit. checkPattern pins the scrutinee
per kind (mismatch = located error); the EMatch defaulting family
extends (unresolved scrutinee + literal arms → bind the literal's
type — same precedent as bool). Exhaustiveness: int/string literal
arms NEVER complete (missing "_" unless an irrefutable arm exists —
F#'s rule; the weir-vs-F# difference is only error-vs-warning
severity, now a named divergence row); PUnit is irrefutable alone.

ELambda gains ONE bespoke arm: param "()" (unforgeable name) types
the param as TUnit and binds NOTHING — the generalization trap is
the arm's reason (an unconstrained fresh param would generalize and
`cleanup 5` would typecheck; tripwired). Eval is untouched for
params (the "()" name is unreferenceable); pattern eval adds literal
equality and the always-match unit pattern.
