# Checker transcription — judgment forms per arm

Anchored to commit d12aefd, `src/Weir/Check.fs`. Notation: `Γ ⊢ e ⇒ τ ⊣ Δ`
(infer: synthesize τ, constraint-store delta Δ), `Γ ⊢ e ⇐ τ ⊣ Δ` (check
against τ). `Δ` covers mutations of `Ctx` = (Subst S, Rows R, Fresh
counter). `σ` ranges over schemes; `ρ` over row variables; `⟦ps↦as⟧τ` is
`substParams`. Read alongside READ-ORDER.md.

Arms marked **FLAG** resist single-rule transcription (two jobs, or
order-dependent side effects) — per the plan, these flags are findings
before the read starts.

## Substrate

### substParams — L60
    ⟦ps ↦ as⟧ : capture-free simultaneous substitution of decl params by
    argument types; structural through TFun/TSeq/TNamed args/TRowVar
    field snapshots; identity elsewhere.
Pure. Single job. Soundness rests on decl-param names never colliding
with ctx-fresh names (decl side substituted before ctx interaction).

### occurs — L122
    occurs(v, τ): v ∈ vars(resolve*(τ)), walking TFun, TSeq, TNamed
    args, and row *constraints* via R (with a seen-set on row names).
Single job. Note: consulted only from bind's TVar arm — row-var cycles
rely on substitute-before-recurse ordering instead (see dischargeRow).

### instantiate — L141  **FLAG (two jobs)**
    σ = ∀V. τ    fresh renaming θ : V → fresh names (rows→rN, else aN)
    ─────────────────────────────────────────────────────────────────
    Γ ⊢ x : σ ⇒ θτ ⊣ R += {θρ ↦ θ(snapshot(ρ)) @ use-span | ρ ∈ V}
Two jobs: (1) alpha-rename quantified vars; (2) install renamed row
snapshots into R with the use site's span. Job 2 is the deep-copy
discipline (audit §3.1) — the snapshot in the *type* is the source of
truth crossing generalization boundaries; R is derived state. The read
should verify θ is applied inside snapshots before installation
(fields renamed first — L169-171).

### envFreeVars — L181
    FV(Γ) = ⋃ { vars(finalTy(σ.Ty)) \ σ.Forall | σ ∈ Γ }
Single job. Transitive reach through row constraints holds because
finalTy expands R before vars() runs. **Open composition question for
the read (item f)**: finalTy also maps TNamed args (L~113) — verify a
var reachable only as `ρ ↦ {X: Option<'a>}` with 'a env-free is caught.
Probe: composition test (b) in ReadProbes.

## Unification

### bind — L185
Equal (after shallow resolve):
    ────────────
    τ ~ τ ⊣ ∅
TVar (either side), with occurs:
    v ∉ occurs(τ)
    ─────────────────
    v ~ τ ⊣ S += v↦τ
TNamed pairwise (generics session — read target b):
    n = n'   |as| = |as'|   aᵢ ~ a'ᵢ ⊣ Δᵢ  (left to right)
    ──────────────────────────────────────────────────────
    n<as> ~ n'<as'> ⊣ ⋃Δᵢ
Row ~ nominal → dischargeRow; row ~ row → mergeRows; row ~ other →
error. TFun/TSeq structural. Else mismatch (expected-first orientation).
Single dispatch job; arms individually single-job.

### dischargeRow — L209  **FLAG (order-dependent side effect, load-bearing)**
    Γ(n) = record(ps, fields)
    S += ρ ↦ n<as>                                   ← BEFORE premises
    ∀(f, τf, span_f) ∈ R(ρ):
        f ∈ fields with decl type d ⟹ ⟦ps↦as⟧d ~ τf ⊣ Δf @ span_f
        f ∉ fields ⟹ error at span_f (did-you-mean)
    ──────────────────────────────────────────────────────────────
    ρ ~ n<as> ⊣ S ∪ ⋃Δf
The substitution is deliberately installed before recursing into
constraints: a constraint mentioning ρ then resolves to n<as> and
terminates. This ordering is the termination argument for row cycles
(audit §1.1) — verify it survived the generics change (the ⟦ps↦as⟧
premise is new).

### mergeRows — L233  **FLAG (same ordering discipline)**
    S += ρ₁ ↦ ρ₂                                     ← BEFORE premises
    ∀(f, τf, span) ∈ R(ρ₁):
        f ∈ R(ρ₂) with τ'f ⟹ τ'f ~ τf ⊣ Δf
        f ∉ R(ρ₂) ⟹ R(ρ₂) += (f, τf, span)
    ────────────────────────────────────────────────
    ρ₁ ~ ρ₂ ⊣ ...
Field types unify (never name-union) — audit §1.2. R(ρ₂) is re-read per
field because the loop mutates it.

## Operators

### typeBinOp — L273  **FLAG (retry recursion)**
Var-defaulting family: `*`,`/` on two unresolved ⟹ bind both int then
retry; `&&`,`||` similarly to bool; one-var-one-prim ⟹ bind, retry.
Retry terminates because binding strictly shrinks the unresolved set.
Equality family (post-fix, read item §1.5):
    a ~ b ⊣ Δ    equatable(finalTy(a))
    ──────────────────────────────────
    a ==/<> b ⇒ bool ⊣ Δ
Verify this is the rule (unify-then-equatable), not a patch: the old
syntactic `a = b` fast path is gone entirely.
Concrete arithmetic/comparison arms are textbook; measure-preservation
(`+`/`-` same measure) and unitless-only `*`,`/` per SEMANTICS.

## Inference — infer (L391)

Literals (L392-407): axioms, τ from the literal.
### EVar — L408
    Γ(x) = σ ⟹ x ⇒ instantiate(σ)        (see instantiate FLAG)
    x ∉ Γ, x ∈ Modules ⟹ error "is a module"
    x ∉ Γ, x member of module(s) ⟹ moved-name error
    else unbound error (did-you-mean)
Lookup + three error shapes; the typing content is one rule.

### ELet — L438  **FLAG (generalization side condition)**
    Γ ⊢ e ⇒ τ ⊣ Δ    τ* = finalTy(τ)    V = vars(τ*) \ FV(Γ)
    Γ, x:∀V.τ* ⊢ body ⇒ τb ⊣ Δ'
    ────────────────────────────────────────────────────────
    Γ ⊢ let x = e in body ⇒ τb
The V computation is the soundness point (audit §3); FV(Γ) is computed
under the Δ-updated ctx. finalTy bakes row snapshots at generalization —
paired with instantiate's job 2.

### ELambda — L459
    fresh a    Γ, x:a ⊢ body ⇒ τ ⊣ Δ
    ─────────────────────────────────
    Γ ⊢ fun x -> body ⇒ a → τ ⊣ Δ

### EApp — L469 → checkSpine (L751)  **FLAG (three jobs)**
checkSpine(head, args, piped?):
    Γ ⊢ head ⇒ τh ⊣ Δ
    funParams(resolve*, arity) τh = (p₁..pk, r)   else arity error
    piped = Some(τp, span) ⟹ p_last ~ τp ⊣ Δ'    ← piped binds FIRST
    ∀i: Γ ⊢ argᵢ ⇐ resolve(pᵢ) ⊣ Δᵢ              (left to right)
    ───────────────────────────────────────────────────────────
    result: application chain typed with finalTy'd params/result
Three jobs: arity/message selection, piped-first instantiation (the
Spike-5 ordering — the reason lambdas in poly positions check), and
typed-chain reconstruction. The middle job is the semantics; the outer
two are bookkeeping. Read them separately.

### EPipe — three arms
ETo (L472): arg ⇒ seq<e>, jsonable(e) ⟹ seq<string>. Special-cased
because `to json`'s domain is its argument's type.
ECmd (L492): arg ⇐ seq<string>; whole pipe ⇒ seq<string> (stdin wiring
is eval-side).
General (L503): spine route with piped = Some.

### EField — four arms
Module (L517, modules session — read target g):
    target ≡ EVar m,  m ∉ Γ_values,  m ∈ Modules,  Modules(m)(f) = σ
    ────────────────────────────────────────────────────────────────
    m.f ⇒ instantiate(σ)      [runtime: mangled flat name]
Precedence is syntactic and first-match: value shadow falls through to
the nominal/row arms below (three-way precedence, pinned by test).
Nominal (L533):
    Γ ⊢ t ⇒ n<as>    Γ(n) = record(ps, fields)    fields(f) = d
    ────────────────────────────────────────────────────────────
    Γ ⊢ t.f ⇒ ⟦ps↦as⟧d
TVar upgrade (L~556)  **FLAG (side-effectful premise)**:
    Γ ⊢ t ⇒ v (unresolved)    fresh ρ, a
    ─────────────────────────────────────────────────────
    Γ ⊢ t.f ⇒ a ⊣ S += v↦ρ ;  R += ρ ↦ {f: a @ fieldSpan}
Row extend (L~565): existing constraint returns its var; new field adds
a fresh one with the access span (span discipline = discharge errors
point at the demand site).

### EBinOp — L582: infer both, typeBinOp.
### ERecord — L593
    fields' names = decl field set (exact, unique across Γ_types)
    fresh as for ps    ∀f: Γ ⊢ vf ⇐ ⟦ps↦as⟧df ⊣ Δf
    ──────────────────────────────────────────────────
    {fs} ⇒ n<as>
### EFrom/ETo (L640): adapter typing; monomorphic-record guards.
### ECmd — L668: args infer then must resolve to str/int/bool
(unresolved defaults to string — soundness condition documented in
SEMANTICS: command segments cannot occur under a generalizing let).
### EList — L697: empty ⇒ seq<fresh>; else head infers, rest check.
### EMatch — L721: scrutinee infers (resolved); arm 1 infers the result
type, later arms check against it; checkPattern binds via ⟦ps↦as⟧ on
payloads (L364).

## check (L821)
Lambda vs TFun: param ⇐ dom; body checked if cod is ground, else
inferred-and-bound (the row-era generalization of the rule).
Lambda vs non-fun: error. Let: same generalization as infer-ELet.
Fallback: infer then bind (subsumption-by-unification).

## checkDecl — L934 (generics session — read target e)
    params distinct    fields/payloads validated (arity, allowed vars)
    union: ctor c of payload p gets σc = ∀params. p → Self<params>
           (nullary: ∀params. Self<params>)
    ──────────────────────────────────────────────────────────────
    Γ += type def; Γ_values += ctor schemes
Constructor schemes are ordinary Schemes — instantiation rides the
audited path; that identity is the §3 reopening argument.

## Flags summary (pre-read findings, per plan)

1. instantiate: two jobs (rename + R installation). Verify snapshot
   renaming precedes installation.
2. dischargeRow / mergeRows: substitute-before-recurse is load-bearing
   for termination — re-verify structural after the ⟦ps↦as⟧ premise.
3. checkSpine: three jobs braided; the piped-first binding is the
   semantic core.
4. EField TVar-upgrade: side-effectful premise (creates ρ) — order
   matters relative to the fresh field var.
5. ELet/check-ELet duplicate the generalization computation — two code
   sites for one rule (drift risk, not unsoundness).
