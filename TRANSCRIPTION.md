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

## Flags summary (pre-read findings)

1. instantiate: two jobs (rename + R installation).
2. dischargeRow / mergeRows: substitute-before-recurse is load-bearing.
3. checkSpine: three jobs braided; piped-first is the semantic core.
4. EField TVar-upgrade: side-effectful premise.
5. ELet/check-ELet duplicate the generalization computation.
(The first edition's §4/measure content: retired — see the NOTES
measure-removal arc.)
