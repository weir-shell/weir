# weir — REPL completion: record fields through pipes, and the empty-prompt flood

Status: PROPOSED (2026-09-16, from a measured probe against the real
`Weir.Complete.suggest` — every claim below is empirical, not code-read).
Three independent fixes, all in `src/Weir/Complete.fs`; no grammar/parser
change, `check` untouched.

## Driver

Two user-hit gaps, surfaced by the `#infer`/record workflow:
1. Tab offers no fields on a record piped into `_.field` or into a
   lambda param — the payoff of `#infer` (typed record → field
   completion) misfires in the pipe positions people actually use.
2. Tab on an EMPTY prompt dumps 1130 candidates (954 PATH executables +
   modules + keywords + constructors), sorted — unusable.

`#infer` itself is sound: `r.field` on a bound record already completes,
and `x |> from yaml T |> _.some` evaluates correctly — the types are
real; only the COMPLETER misses them in pipe/lambda/empty positions.

## Fix 1 — record fields through a pipe (`pipelineElemTy`)

MEASURED root cause (single): `pipelineElemTy` (Complete.fs:60) only
unwraps `TSeq elem` and returns `None` for anything else. `from yaml T`
yields a SCALAR record (`Check.fs:3710-3720`: plain `from yaml T` = one
document → `TNamed(name,[])`, not a seq). So for a record piped into
`_.`, the pipe-element type is a bare record, the `TSeq` match misses,
and no fields surface.

MEASURED failing shapes, ALL from this one cause:
- `x |> from yaml Test2 |> _.` → `[]`
- `r |> _.` (r a bound record) → `[]`
- `x |> from yaml Test2 |> (fun row -> row.` → NOISE (every field of
  every declared record, via the lexically-bound fallback)

Working, for contrast (the mechanism already exists):
- `r.` / `r.so` → `[some]` (bound value, direct)
- `xs |> Seq.map _.` and `xs |> Seq.map (fun elt -> elt.` → `[some]`
  (seq element unwrapped — the SAME path, minus the scalar case)

THE FIX: `pipelineElemTy` returns the type ITSELF for a non-seq record
prefix (keep `TSeq elem -> Some elem`; add a record arm, or a
`| t -> Some t` catch-all). The sole caller is the `headTy` fold in
`suggestScoped`, which routes through `recordFields` — which returns
`None` for non-records, so a catch-all stays safe (`Some ty` →
`recordFields None` → `[]`, the existing "known non-record offers
nothing" rule, preserved and pin-verified: `recordFields (TNamed
"Test2") = Some [some]`).

Fixes all three broken shapes at once — including the lambda-param
case (shape 7): once the pipe prefix `x |> from yaml Test2` types to
`Test2` instead of `None`, the unbound-head path resolves it and never
reaches the noisy fallback. NO new "type a lambda param from its pipe"
plumbing is needed — the prefix-typing already does it; the only gap is
the seq-only unwrap. Broadening `pipelineElemTy`'s contract from
"element of the piped seq" to "type flowing into this position" is the
real change; rename or re-comment it so the widened meaning is explicit.

PINS: shapes 4/5/7 → `[some]`; shape 6 unchanged; the control
`x |> Seq.map (fun e -> e.` over `seq<string>` → `[]` (a non-record
element offers nothing); a bound record `r.` still `[some]`.

## Fix 2 — the empty-prompt flood

MEASURED: `suggest env "" 0` → 1130 candidates — 954 PATH executables,
27 module names, 28 keywords, 135 bare-name values (functions AND
constructors). The final "command HEADS" `else` branch (Complete.fs
~669-699) unions `Extern.names() @ commandCallable` + user values +
modules + keywords, then filters `n.StartsWith word`; with `word = ""`,
`StartsWith ""` matches EVERYTHING. Prefix filtering is otherwise sane
(`Fi` → `[File; FileCheck-21]`, `Wr` → `[WriteFile]`) — the pathology is
empty-prefix ONLY.

THE FIX: guard `word = ""` in that head branch. The designer ruling to
make (RECOMMEND the first):
- return `[]` on an empty statement-head prefix — simplest, "nothing to
  complete yet; type a character"; OR
- a CURATED small set (keywords only, or keywords + module names) —
  offers the language's own starters without the 954-binary PATH dump.
Either way: DO NOT dump PATH executables on an empty prefix. The branch
also serves argv positions (`before` non-empty, adds `cwdEntries`) — the
guard must key on the statement-head context (`before = ""`), leaving an
intentional bare-Tab in argv position alone unless we rule otherwise.

PIN: `suggest env "" 0` returns the curated set (or `[]`), not 1130;
`suggest env "Fi" 2` still `[File; FileCheck-21]` (filtered completion
unaffected).

## Fix 3 — constructors are not statement heads

MEASURED: 33 union-case constructors leak into head completion —
`Basic/Bearer/NoAuth` (Auth), `Get/Post/Put/Patch/Delete/Head/Options`
(HttpMethod), `Some/None`, `YStr/YInt/…` (Yaml), `Copy/Move/MakeDir/
WriteFile/DeleteFile/DeleteDir/HttpSend` (plan/apply Op — new in
v0.0.36, which is why `Wr` → `WriteFile` at a head), `Regular/Directory/
Symlink` (FileKind), `NoBody/Json/Text/Query` (HttpBody). They pass
`isUserName` (Types.fs:7 rejects only `|`-reifiers), so the head branch
can't tell a constructor from a function. A constructor never starts a
statement (`WriteFile …` / `Bearer …` as a statement head is a
discarded value — a check error).

THE FIX: subtract the union-case set (derivable from `env.Types`' Union
defs — the probe verified the leaked set EQUALS `env.Types`' cases) from
the head-position name pool, keeping constructors available in
expression / argument / dotted positions (where they're valid). Context
IS distinguishable at the call site: the final `else` with `before = ""`
is unambiguously the statement-head/name-pool context; the dotted and
argv branches are separate, so the exclusion scopes cleanly there.

PIN: `Wr` at a head → not `[WriteFile]` (constructor excluded);
`Some`/`WriteFile` still offered in an argument/expression position;
a function bare-name still offered at a head.

## Independence & ordering

The three are independent (Fix 1 = the dotted/`_.` `headTy` source;
Fix 2 = empty-prefix volume; Fix 3 = constructor noise even with a
prefix). One branch, three commits (or one), all `Complete.fs` + pins.
Fixes 2 and 3 compose (an empty prefix guarded → [] or curated; a typed
prefix → constructors excluded at heads).

## Footprint

- Complete.fs: `pipelineElemTy` non-seq arm (Fix 1); the head `else`
  branch — empty-prefix guard (Fix 2) + union-case subtraction (Fix 3),
  the case-set computed from `env.Types`.
- Tests (tests/Weir.Tests): the shape table above — 4/5/7 → `[some]`,
  6 + controls unchanged, empty-prefix curated/`[]`, `Fi`/`Wr` filtered,
  constructor-at-head excluded / constructor-in-expression kept.
- Docs: a docs/repl.md completion note if behavior is user-visible;
  CHANGELOG `## Unreleased` Fixed bullets; DECISIONS row(s) only if a
  genuine ruling (Fix 2's empty-prefix policy and Fix 3's
  constructors-not-heads are rulings worth a row; Fix 1 is a bugfix
  citing the completion behavior).
- Gates: build ×4, units, publish, skill-doc, e2e (the REPL completion
  cells), no fuzz owed (Complete.fs only, no parser/grammar move).

## Boundaries

- No new lambda-param inference (Fix 1 reuses prefix-typing).
- No change to `check`, the grammar, or the type system — completion is
  display-only.
- The `_.`-shorthand and dotted-head share the `headTy` source, so Fix 1
  serves both with one change; do not fork them.
