# Report: predicates over fields — explored, priced, recommended

Status: EXPLORATION COMPLETE (2026-08-11); DECLINED (user, 2026-08-12)
— [D:field-predicates] records the ruling and the re-opening trigger.
The report recommended A; the ruling weighed the prompt-side pain as
not worth the surface. Kept on the shelf, sized, safety table checked.
Every claim below is probed against the binary of 2026-08-11.

## The recommendation, first

**Design A — operator lifting — scoped to comparisons plus pointwise
`&&`/`||`, with `not` staying the existing `>> not` composition.**
Estimated cost: one session, checker-only (no parser change, so no
fuzz seeds owed). Design B declined on the shadowing class. Design C
declined because the receipt is real, just invisible to the corpus
(see the honest-question section).

## 1 · The always-a-type-error claim, checked per operator

Probed with function-on-left against the current binary
(`ls |> Seq.where (_.age OP 24h)` and variants):

| operator | today | notes |
|---|---|---|
| `>` `<` `>=` `<=` | type error | `expected 'a -> 'b, got Duration` — the note already names the lambda |
| `==` | type error | errors via row-typed unification; `fn == fn` errors via `'==' is not defined for functions` |
| `<>` | type error | weir's inequality spelling (`!=` does not parse — it does not exist) |
| `&&` `\|\|` | type error | two `_.field` operands: `expected 'a2 -> 'a4, got 'a5 -> 'a7` |
| `not _.f` | type error | `expected bool, got a function` — but see §3: `not` is APPLICATION, not an operator |
| `+ - * /` | type error | liftable later under the same rule; out of scope (the receipt is predicates) |

**The safety argument holds for every operator in the proposed set:
nothing legal changes meaning.** The checker's existing
`_.Field is a whole function` note proves the DETECTION already
exists — lifting replaces teaching at the same site.

## 2 · The deciding cases (answered first, per the plan)

### Two placeholders: `_.isDirectory && _.hidden`

**Ruled for A: all function-shaped operands of a lifted operator share
ONE parameter** — `fun x -> x.isDirectory && x.hidden`. This is the
POINTWISE reading (`&&` over predicates is predicate conjunction), an
algebraic law rather than a syntax trick. Scala's
two-underscores-two-parameters answer is explicitly rejected — it is
exactly wrong for this receipt.

**Nesting falls out**: `_.age > 24h && _.bytes > 1MiB` — the inner
comparisons lift first, each becoming an `'a -> bool`; the `&&` arm
then sees two predicate functions and lifts pointwise, unifying the
parameter. No extent rule exists anywhere: **the extent is always the
operator's own operands**, and a lifted child is itself just a
predicate function.

### Prefix `not`

**`not` is application, not an operator** — lifting application is
Scala's extent footgun and stays out. But the gap is smaller than
"half a feature":

- `where _.isDirectory` **already works today** (probed) — a bool
  field IS a predicate.
- `where (_.isDirectory >> not)` **already works today** (probed) —
  and under A's pointwise `&&`, it COMPOSES:
  `_.age > 24h && (_.isDirectory >> not)` lifts to
  `fun x -> x.age > 24h && not x.isDirectory`, because the `&&` arm
  accepts ANY predicate-shaped operand, composed or lifted.

So A's answer to `not` is: the composition spelling, documented as
the form's companion, participating fully in conjunctions. Not a
lambda fallback.

## 3 · The four-way side-by-side (the same pipeline)

```
today  ls |> Seq.where (fun f -> f.age > 24h)
          |> Seq.where (fun f -> not f.isDirectory)
          |> Seq.sortBy _.bytes |> Seq.map _.name

A      ls |> Seq.where (_.age > 24h)
          |> Seq.where (_.isDirectory >> not)
          |> Seq.sortBy _.bytes |> Seq.map _.name

       -- or, folded into one stage under pointwise &&:
       ls |> Seq.where (_.age > 24h && (_.isDirectory >> not))
          |> Seq.sortBy _.bytes |> Seq.map _.name

B      ls |> where (age > 24h) |> where (not isDirectory)
          |> sortBy bytes |> map name              -- #loose only

C      (today's spelling, kept)
```

B reads best; A reads nearly as well and costs three characters per
comparison over B. Half the pipeline was never broken — `sortBy
_.bytes` and `map _.name` are today's spellings.

## 4 · Costs

**A** (~1 session): a checker elaboration at the binary-operator arms
— when an operand is function-typed where a scalar is expected AND the
operator is in the lifted set, rewrite to the shared-parameter lambda
and re-check; span provenance so a still-wrong lift (`_.age > "x"`)
blames the ORIGINAL operand spans; pins per operator + nesting + the
`>> not` composition; docs. Parser untouched (the forms already
parse), so no grammar work and no fuzz seeds. LSP/hover ride the
checker and need nothing.

**B** (~2-3 sessions + a permanent tax): a `#loose`-only row-DSL for
the argument of a PARTICIPATING-MEMBER LIST (`where`? `sortBy`?
`map`? — any boundary drawn is arbitrary and teaches unevenly); a
shadowing rule for `where (age > limit)` where `age` is a field and
`limit` a binding — **the bindings-beat-PATH shape, which has five
resolver incidents on the ledger**; and a dialect split the checker,
LSP, hover, completion, and colorizer must all carry twice.

**C** (free, but the question returns): the pain is real at the
prompt and the message already teaches the lambda. If chosen, the row
must name placeholder-extent and DSL-shadowing as the reasons.

## 5 · The honest question: is the pain real?

The corpus says: **not in scripts.** Four predicate lambdas total
across examples/SKILL/GUIDE/COMING-FROM; zero multi-predicate
pipelines in examples; the showcase has none. But the corpus is
SCRIPTS, and the receipt came from a live sitting — the pain is a
PROMPT phenomenon, where four stages get composed incrementally and
retyped often. The prompt is also where nushell's comparison bites.
So the corpus neither supports nor refutes; the live receipt stands,
with this stated honestly rather than laundered through corpus
numbers.

## 6 · Why A over B, in one paragraph

A is three characters worse to read and roughly ten times cheaper to
own. It needs no dialect, no participating-member list, no shadowing
rule (the `_.` prefix already marks the element unambiguously — B's
`age > limit` ambiguity cannot arise because A spells it
`_.age > limit`, field and binding visibly distinct), and no LSP
surface doubling. Its two subtle cases have principled answers: the
pointwise reading for shared placeholders, and composition for `not`.
The one thing B buys — dropping the `_.` — is exactly the thing that
reopens the five-incident shadowing class.

## Done-when mapping (the plan's bars)

- Zero code: held (probes only).
- Always-a-type-error: checked per operator, table above.
- Two-placeholder: ruled (pointwise, one parameter).
- Prefix `not`: ruled (composition, participating in `&&`).
- Corpus: checked; the receipt is prompt-side, stated.
- Recommendation: A, scoped; implementation plan sized at one
  checker-only session.
