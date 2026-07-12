# Spike Notes

## Operator completeness — backlog #1 landed (2026-07-12)

`<>`, `>=`, `<=`, `&&`, `||` as operators; `not` as a `bool -> bool` builtin. 185 tests. Both pre-commitments honored and pinned:
- `<>` inherits `==`'s full equatability path (one rule pattern `("==" | "<>")` — `nats <> nats` rejected with the same message shape).
- `&&`/`||` short-circuit: dedicated eval cases *before* the generic binop case (which evaluates both sides); pinned with division-by-zero as the effect proxy (`false && (1/0 == 1)` → `false`; `true && (1/0 == 1)` → raises).
- Precedence: `||` (2) < `&&` (3) < comparisons (4); all left-assoc. FParsec longest-match handles the `|>`/`||` and `<`/`<=`/`<>` prefix families; the measure-literal `attempt` still wins (`1<mb> <= 2<mb>` parses).
- Var-var `&&`/`||` bind both operands to `bool` (their only typing) — same deterministic-defaulting family as `*`/`/`, noted in SEMANTICS.md.
- The day-one filter shape now works: `ls |> where (fun f -> f.Name <> "tmp" && not f.ReadOnly)`.

SEMANTICS.md updated: operator surface stated as complete, short-circuit promoted from pre-commitment to rule, backlog renumbered (`collect` is #1, measure algebra #2 — still flagged as reopening §4.2 and the `*`-defaulting rule).

## Tripwires, semantics doc, and the two re-aimed read questions (2026-07-12)

Response to the advisor's second pass. Three deliverables:

**1. `Tripwires.fs`** — tests named for the incidental protections, with comments stating which checklist item reopens if the named mechanism changes: funParams-shields-occurs (§1.1, reopens with arrow-var unification), no-unit-algebra (§4.2, reopens with measure arithmetic), no-annotation-syntax (§2.3, reopens with ascription), plus the two generalization pins below. Confirmed empirically along the way: `f.Size * 2` rejects ("expected int<mb>, got int") — the day-one ergonomic cliff is real; measure algebra is the top post-review backlog item.

**2. `SEMANTICS.md`** — the accidental-looking rules written down as language rules: the HOF-inference restriction, the generalization regime (deliberately upgraded from the v0.1 "frozen at definition" rule during the rows work — the advisor is right that this happened without a decision point; it's now a documented decision), measure exactness, `|>`-only, `==`-only equality, laziness/re-enumeration semantics.

**3. The two re-aimed read questions, pre-answered with pins**:
- *instantiate × Rows aliasing (new #1)*: **deep copy, not aliasing.** `instantiate` renames every quantified var — row names included — recursing into the row snapshot (field types renamed first), then installs a fresh `ctx.Rows[r']` entry per use site from the scheme's snapshot, which is an immutable `Ty` inside the env map and is never written after generalization. Sibling instantiations use distinct keys; discharge writes `Subst[r']` only. Pinned by a tripwire whose comment states the failure mode (sibling poisoning), in the dangerous order (use A fully discharged before use B instantiates).
- *envFreeVars transitive reachability*: **covered, structurally.** `envFreeVars` collects vars from `finalTy` of each env entry, and `finalTy` expands row constraints (deep) before `tyVars` runs — so a var reachable only through an env-free parameter's row constraints is still subtracted from the quantifier set. Pinned by a tripwire where `'a` occurs in the enclosing param's type *only* inside its row constraints and a second contradictory use must (and does) error.

172 tests. The human line-read now has its two hardest questions answered-with-evidence and its remaining scope: `bind`/`dischargeRow`/`mergeRows` (verify substitute-before-recurse is structural), then judgment-on-paper for `infer`'s EField/ELambda and `check`'s lambda rule.

## Row-soundness checklist — pre-read probes + implementation map (2026-07-12)

Ran the advisor's checklist probes before the line-read; all pass (167 tests). Map of checklist → implementation for the read:

**§1 Row unification**
- 1.1 occurs/self-application: `fun f -> f.x f` rejects (no hang) — but note *why*: fslite never unifies a TVar with a function type at application (`funParams` on an unresolved var → "not a function"), which blocks the standard cycle constructions before `occurs` is even consulted. `occurs` (TVar case in `bind`) covers var-mediated cycles; rows enter `Subst` only via occurs-checked TVar bindings or `dischargeRow`/`mergeRows`, both of which substitute the row var *before* recursing into constraints — that ordering is what makes potential cycles terminate in `bind`. `finalTy` additionally carries a seen-set now (defensive; a cyclic row prints `{ .. }` instead of hanging the formatter).
- 1.2 var-var merge: `mergeRows` binds shared field types (`bind ft2 ft`), never name-unions. Probe: two lambdas' rows merged through a shared arg, conflicting `A` demands → "expected int, got string". Pinned.
- 1.3 intra-lambda: same code path as 1.2 — `EField` on a row var returns the *existing* constraint's type var, so a second conflicting demand collides on that var. Pinned.
- 1.4 closed rows: `dischargeRow` sets `Subst[r] := TNamed n` — after that, `resolve` yields the nominal type and field access takes the nominal path, so the row is genuinely closed. Pinned (`Nonexistent` after a discharged stage → nominal rejection).
- 1.5 stale-compare: `bind` shallow-resolves at the top and re-resolves in structural recursion; the `e = a` shortcut is safe because equal-but-unresolved compares only misfire toward the *structural* case, which resolves. Binop operands are atomic types. Good-code sanity pinned.

**§2 Propagation** — 2.1: a discharged row can't be written to (its var is substituted away; `Rows` entries go stale-but-unread). 2.2: all argument positions go through `check` (uniform since the rows rewrite); record literals push declared field types; the exact-field-set rule means no subset-leniency. 2.3: N/A — no annotation syntax exists.

**§3 Generalization** — regime is *generalize at let/REPL, freshen per use* (Damas-Milner style), not freeze: pinned by 3.1 test (one `map _.V` used at `int` and `string` field types in one line, both accepted). Soundness edge (generalizing an enclosing lambda's live var) excluded by `envFreeVars` subtraction — pinned since the rows commit. 3.2 value restriction: no purchase — the language has no mutable bindings, and data sources (streams) are concretely typed; only functions are polymorphic. 3.3: types are erased at runtime; closures carry no row-store references; cross-line types are baked snapshots re-instantiated per use, and per-line fresh-name collisions are impossible because REPL-stored types are fully generalized (every var renamed at instantiation).

**§4 UoM × rows** — 4.1: field demands are measure-*exact* (`f.Size > 1<mb>` demands `int<mb>`, no conversion, measures nominal by name); discharge against `int<byte>` would reject — pinned by the gb-vs-mb conflict test. 4.2: N/A by construction — no unit algebra exists (measures are `string option`; `*`/`/` are unitless-only), so there's no non-normalized representation to mis-compare. 4.3: no dimensionless collapse hole — `f.Size / f.Size` is *rejected* (division on measured ints unsupported; `int<1>` inexpressible). Pinned.

**§5** — 5.1: constructor patterns on unsolved rows reject; var/wildcard arms bind (harmless). 5.2: shadow binds a fresh var; constraints are keyed by var id, not source name — pinned. 5.3: constraint spans travel with each field demand; discharge errors point at the demanding span (or use site across a generalization boundary — deliberate, documented in the rows entry). 5.4: `unreachable` inventory re-probed with row-typed code; field-on-missing-VRecord requires a 1.4 leak, which is pinned shut.

**Read order for the human pass** (matches advisor's §1→§2→§3): `bind`/`dischargeRow`/`mergeRows` (~60 lines), then `checkSpine`+`check`, then `instantiate`/`envFreeVars`/`ELet`. The judgment-on-paper exercise applies mainly to `infer`'s EField/ELambda rules and `check`'s lambda rule.

## Depth audit — poking each spike where it's most likely hollow (2026-07-12)

Ran the adversarial probe list against the row-poly branch. Results:

- **Spike 1 (checker)**: 9 new adversarial tests, all rejected at check time with correct messages/spans — wrong arity, UoM mismatch both directions, shadowing with a different type, element type contradicting use two stages later, row constraint vs declared measure conflict, lambda/constructor piped as data, field access on a union. **The line-read debt remains open** — these tests raise confidence but only a human read of Check.fs rules out unsoundness that green tests can't see. The read target is the post-row-polymorphism Check.fs.
- **Spike 2 (unreachable arms)**: every attempted source-level route to an `unreachable` arm is blocked at check time (now pinned by tests). None reached.
- **Spike 4 (process lifecycle)**: `cmd "yes" |> first 3` and a truncated print of unforced infinite `cmd "yes"` both terminate; `pgrep` confirms zero leaked children in both cases (the `seq{} try/finally + Kill` path works under partial consumption). Pull-count tests were already in the suite.
- **Spike 5 (porcelain)**: **HOLE FOUND, exactly where predicted** — not the space itself but git's C-quoting it triggers: `"spaced name.txt"` and `"qu\"ote.txt"` passed through with quotes and escapes intact. Fixed: `unquoteGitPath` (full C-style unquote incl. octal escapes for unicode under default `core.quotePath`) + quote-aware rename-target splitting. Live retest on a repo with rename + space + quote + untracked: clean paths. Regression test covers all cases incl. `caf\303\251.txt` → `café.txt`.
- **Spike 7 (honest numbers)**: the 6ms was already the `-c`-path measurement (`-e "1 + 2 |> double"` = parse+check+eval+exit), warm cache. Full typed pipeline `-e 'ls |> where (fun f -> f.Size > 1<mb>) |> first 5'`: **7ms median** (min 6, max 15). No suppression flags anywhere in the fsproj (`NoWarn`/`TrimmerSingleWarn` absent) — the 3 dependency-aggregate warnings are surfaced and empirically triaged, not silenced.
- **Cross-cutting integration on the AOT binary**: declare `type Pkg = { Name: string; Size: int<mb> }` at the prompt → `cmd` emitting NDJSON → `from json Pkg` → `where (fun p -> p.Size > 2<mb>)` → `map _.Name` → `first 3` → `["big"; "huge"; "mid"]`. The skeleton threads end to end natively.

159 tests. Verdict: one real hole (porcelain quoting), found and closed; everything else held at depth. Outstanding: the human line-read of Check.fs.

## Row polymorphism + |> only (2026-07-11, session 1) — first parked item, unparked

**Two changes**, committed separately on `row-poly`:

**1. Dropped bare `|`** (user decision). Single operator table again, `armExpr` deleted — match arm bodies are now full expressions (`| Running n -> n |> double` works without parens). Piping a whole match now needs parens (`(match ...) |> f`) because arm bodies are greedy — same as F#. Nested match in an arm still needs parens.

**2. Row polymorphism** — the predicted "biggest checker-complexity jump", and it restructured Check.fs into a miniature Damas-Milner with rows:
- `TRowVar of name * fields` in `Ty`: a record type with *at least* these fields, displayed `{ Size: int<mb>; .. }`. `Scheme = { Forall; Ty }` replaces bare `Ty` in `TypeEnv.Values` — proper generalization, so the classic unsoundness (generalizing a variable free in the environment, e.g. an enclosing lambda's parameter) is excluded by construction and pinned by a test.
- Per-line mutable `Ctx` (fresh counter, substitution, row-constraint store with **spans per constraint**). `bind` is one-way-matching upgraded to unification-lite with an occurs check. Lambda params get fresh vars; field access on an unknown *upgrades* it to a row var and accumulates constraints; constraints discharge nominally when the var meets a `TNamed` (wrong field → "FileRow has no field 'Sze'. Did you mean 'Size'?" at the constraint's span; through a let-generalization the error lands at the use site, which is the right model for multi-line REPL sessions).
- Instantiation is freshen-on-use from schemes — let-bound row values are genuinely polymorphic: one `map _.X` reuses across two record types (tested), and `sizes = map _.Size : seq<{ Size: 'a; .. }> -> seq<'a>` is polymorphic in the row *and* the field type, so measures flow through.
- **Net simplification in places**: the two special-case lambda rules (EApp-of-lambda, pipe-into-lambda) are gone — bare lambdas just infer (`fun x -> x : 'a -> 'a`). checkSpine's two-pass argument dance collapsed to uniform `check` calls. The Spike 5 casualty (`let staged = where (fun f -> f.ReadOnly)`) is un-killed.
- Deliberate limits: binops on two unknowns stay errors (except `*`/`/` which bind to unitless int — the only sound reading); no higher-order inference (`fun f -> f 1` rejected); constructor patterns need concrete scrutinees; rows are records-only. Adapters/decls unchanged; runtime untouched (types erase).

**Numbers**: 149 tests; AOT still clean (same 3 dependency-aggregate warnings), cold start unchanged ~6–8ms with row-polymorphic expressions.

**Review note**: Check.fs is a full rewrite of the inference core — this is a gate-grade review, bigger than Spike 5's. Reading order: `Ctx`/`resolve`/`finalTy` → `instantiate`/`envFreeVars` (the generalization pair) → `bind`/`dischargeRow`/`mergeRows` (the heart) → `infer`'s `ELambda`/`ELet`/`EField` rules → `checkSpine`/`check`. The soundness-critical spots: occurs check, `envFreeVars` subtraction in `ELet`, and discharge-before-recurse ordering in `dischargeRow`.

## Spike 7 — AOT reality check (2026-07-11, session 1) — TARGET MET

**Result: 6ms median cold start** (min 6 / max 9 over 20 runs of `FsLite -e "1 + 2 |> double"`), vs the 5–20ms target and ~70–135ms for the same dll under JIT `dotnet`. Binary: 5.5MB self-contained. The no-FCS, no-reflection, no-printf discipline paid off in full.

**Setup**: `PublishAot=true`, `InvariantGlobalization=true`, `StripSymbols=true`, `OptimizationPreference=Speed`; `dotnet publish -c Release -r linux-x64` (needs clang + zlib1g-dev + binutils; container is Ubuntu 26.04 — note: the `.fc44` kernel string is the Fedora *host* kernel, containers share it).

**Warnings**: zero from fslite's own code. Three aggregate dependency warnings — FSharp.Core (IL2104 trim + IL3053 AOT) and FParsecCS (IL2104) — from reflection fallback paths (structural equality/printf in FSharp.Core, FParsec's dynamic bits). Empirically benign: every feature exercised against the native binary works — FParsec parsing, checker, declarations, match, UoM errors, streaming with process spawn/kill (`cmd "yes" | first 3`), porcelain and JSON adapters, roundtrips. Custom equality on `Value`, hand-rolled `formatTy`/`formatValue`, and interpolation-only output mean the flagged paths are never hit.

**Also built**: `fslite -e "<expr>"` eval-and-exit mode (the honest thing to measure, and a real shell wants it) — `value : type` on stdout, errors to stderr, exit codes 0/1/2.

**Verdict**: the plan's last hard question answered yes. All 8 spikes done (0–7) in one day against a 12–20 session estimate. What remains is the parked list: row polymorphism (now concretely motivated by mono-builtins strain + the `where`-lambda-standalone casualty), adapter automation, LSP, daemon (moot — 6ms needs no daemon).

## Spike 6 — REPL ergonomics (2026-07-11, session 1)

**Built**: line editor (ReadLine nuget — history, tab completion), checker-powered completion (`Complete.fs`, pure + unit-tested), `_.Field` lambda shorthand, string escapes (`\" \\ \n \t`), history persisted to `~/.fslite_history`. 136 tests.

**Completion design**: `Complete.suggest : TypeEnv -> text -> wordStart -> string list`, pure so it's testable without a terminal.
- Dot-completion resolves the target: env-bound record vars directly; unbound names (lambda params) fall back to the *pipeline element type* — parse+typecheck everything before the last `|`, take the seq element. So `ls | where (fun f -> f.<TAB>` offers FileRow fields, and after `| from porcelain |` the same keystroke offers Change fields. Field chains resolve through nested records.
- `from json <TAB>` completes declared record names. Otherwise: values in scope + keywords.
- The REPL runs completion against the live TypeEnv (a ref updated per loop), so user-declared types/lets complete immediately.

**`_.Field`**: parser-level desugar in `postfixAtom` — `_.A.B` becomes `fun _ -> _.A.B` (the param is literally named `_`). Zero checker changes; rides the lambda rules including pipe-directed instantiation: `ls | where _.ReadOnly`, `ls | map _.Size` both work. Bare `_` stays an unbound-variable error, as in F#.

**Bug found by the escapes**: lazy adapter errors (e.g. invalid JSON in `from json`) escaped the REPL's try — eval returns an unforced seq, and the throw happened at `formatValue` time, crashing the process (SIGABRT). Fix: force/format inside the guard. Lesson filed: with lazy values, *printing is evaluation* — any REPL boundary must treat formatting as effectful.

**Piped-stdin fallback**: when input is redirected the REPL bypasses ReadLine (it needs a real terminal) and reads plainly — keeps automated smoke tests working.

**Open, deliberately**: the spike's real question — does checker-powered completion feel like the payoff? — needs the user's hands on an interactive terminal; unit tests can't answer it. Also pending the user's `|` vs `|>` verdict (drop bare `|` and the dual-OPP grammar simplifies; keep it if the shell feel wins).

**Verdict**: build complete; experience verdict pending user. → Spike 7 (AOT) is the last planned spike.

## Spike 5 — External command boundary (2026-07-11, session 1)

**Built**: `cmd`/`into` process builtins, `from json <Record>` / `from porcelain` / `to json` syntax forms, `Change` record, real-git acceptance test — **plus pipe-directed parametric instantiation in the checker**, which the acceptance forced. 121 tests.

**Done-when verified**: `cmd "git status --porcelain" | from porcelain | where (fun c -> c.Staged)` works on a real repo (temp-repo test + live REPL). One deviation from the plan's literal expression: commands are `cmd "..."` strings, not bare words — bare-command syntax is command-position parsing (a frontend question for Spike 6+), not a typed↔bytes question.

**The forced checker change (REVIEW THIS)**: the acceptance pipes `seq<Change>` into `where`, which was FileRow-mono — unmeetable without polymorphic combinators. Added the minimal version: `TVar` in `Ty`, and spine-directed instantiation (`checkSpine` in Check.fs). The pipe rule now infers the piped value FIRST, binds the combinator's type variables from it (one-way matching, no unification variables), and only then checks lambda arguments — whose parameter types are concrete by that point. Two-pass argument checking (non-lambdas bind first, lambdas after) makes full application `where p ls` work too. No generalization, no let-polymorphism, no row polymorphism — those stay parked; `didYouMean`-quality errors preserved.

**Casualty**: `let staged = where (fun f -> ...)` no longer checks (lambda in polymorphic position, no data to instantiate from; error hints "pipe the data in first"). Partial application with inferable args still works and stays polymorphic (`let firstTwo = first 2` : `seq<'a> -> seq<'a>`). Pipe-first is the shell idiom anyway.

**Typed↔bytes verdict (the spike's question)**: less painful than feared, with clear division of labor. The checker guarantees everything inside the pipeline; the adapter validates at the boundary and fails loudly per line (`from json: missing field 'Size' in: {...}`). Runtime boundary errors are honest — bytes are untyped, so check-at-the-edge is the contract. `from`/`to` as syntax (not builtins) works because a format+record isn't a value — and `from porcelain` still first-classes fine (`let p = from porcelain in ...`).

**Mechanics that mattered**:
- `TEFrom` carries the `RecordDef` (not just the name), so eval needs no TypeEnv — checker resolves, runtime trusts.
- Process streams: `seq {}` with `try/finally` kills the child when the consumer stops early — `cmd "yes" | first 3` terminates and reaps. Nonzero exit raises at stream end with stderr. `into` writes stdin from a background task (no deadlock on full pipes).
- JSON via `JsonDocument`/`Utf8JsonWriter` — no reflection, AOT-safe. Serialization is value-driven (VRecord knows its shape); only parsing needs the def.
- fslite string literals have no escapes, so you can't type JSON at the prompt — roundtrip demos via `ls | to json | from json FileRow`. Escape syntax → Spike 6.

**Surprised**: how little the poly machinery needed to be — ~100 lines, no unification state, because bidirectional + pipe-first gives instantiation order for free. Dunfield & Krishnaswami would call this a degenerate special case, and it's exactly enough for a shell.

**Verdict**: continue. → Spike 6 (REPL ergonomics) or the parked polymorphism/adapter work.

## Spike 4 — Streaming pipelines (2026-07-11, session 1)

**Built**: infinite `nats` builtin, lazy `map`/`take`/`sum` (int-mono), `==` equatability check in the checker, pull-count acceptance tests. 93 tests.

**Acceptance verified**: infinite source `| first 5` terminates; a counting source proves `first 5` pulls exactly 5 elements, `where ... | first 2` pulls exactly what the filter examined (4), and an unforced pipeline pulls 0. Laziness survives eval boundaries — including fslite lambdas as filter/map stages (closures apply per-pull inside Seq.filter/Seq.map).

**The honest finding**: Spike 2's architecture had already answered this spike's question. `VSeq` wraps .NET `seq<Value>` (an enumerator factory), and `where`/`first` were built on `Seq.filter`/`Seq.truncate` from day one — nothing in the eval path materializes. This spike was proof + hardening, done in a fraction of the estimate.

**Hardening that was real**:
- Spike 2's flagged footgun closed: `==` on a seq would have hung on infinite input (Value equality materializes both sides). Fixed at the type level — `isEquatable` recursively rejects `==` on seqs, functions, and any record/union that transitively carries one (cycle-safe via a seen-set). Runtime equality on seqs is now unreachable through checked code.
- `formatValue` already truncated at 20 elements, so the REPL prints `nats` (an infinite value) safely.

**Naming pressure**: `map`/`take`/`sum` carry generic names but int-mono types, while `where`/`first` are FileRow-mono. Two element types now exist and the builtin table is already showing the strain — this is the concrete motivation for the parked polymorphism work, on schedule (revisit after Spike 5).

**Caveat noted**: `let s = ls | where p in ...` re-enumerates per use (standard seq semantics) — side-effecting sources run again. Fine for now; caching combinators are a product question, not a spike question.

**Verdict**: continue. → Spike 5 (external command boundary).

## Spike 3 — Type declarations (2026-07-11, session 1)

**Built**: `type X = { ... }` / `type X = A of t | B` statements at the prompt, record literals, `match` with constructor/var/wildcard patterns (nested allowed), exhaustiveness + unreachable-arm warnings, session persistence. 83 tests.

**Done-when verified at the REPL**: declare `type Proc = Running of int | Stopped`, construct (`Running 42`), match, get span-underlined exhaustiveness warnings.

**Design decisions**:
- `TRecord` → `TNamed`: the parser can't know record-vs-union when reading a type name, so `Ty` holds just the name and `env.Types` maps to `TypeDef = Record | Union`. Mechanical rename through checker/builtins.
- Constructors enter `Values` as ordinary typed entries (`Running : int -> Proc`, `Stopped : Proc`), so construction is just application — no new checker rule, and constructor typos get did-you-mean hints for free. Runtime counterparts built by `Eval.constructorValues`; a redeclared union shadows its constructors, but old values still match (pattern checking resolves cases via the scrutinee's type def, not a global ctor table).
- Case identifiers must start uppercase (F# convention) — that's what disambiguates `PCase` from `PVar` in patterns.
- **The `|` ambiguity**: match arms vs pipe. Resolution: arm bodies parse with a second OPP that omits the `|` operator (`|>` stays legal); a failed arm parse backtracks, so a trailing `| double` after the last arm becomes a pipe of the whole match — coherent and tested. Arm bodies containing `let`/`fun`/nested `match` need parens. Real fix is the offside rule — Spike 6 question at the earliest.
- Record literals resolve nominally by exact field-name set; ambiguity (two records, same fields) is an error. Type ascription syntax is the eventual disambiguator if needed.
- Exhaustiveness is a separate pure pass (`Check.warnings : TypeEnv -> TypedExpr -> Warning list`) walking the typed tree — zero signature churn on infer/check, no writer-monad plumbing, trivially testable. Top-level coverage only: a case counts as covered when some arm has its constructor with an irrefutable argument; nested refutations are conservatively "not covered". Proper usefulness matrices parked.
- Non-exhaustive match is a warning, not an error (per plan) — so `match failure` at runtime is reachable and is a `failwith`, not an `unreachable`.

**Surprised**: constructors-as-env-entries made construction genuinely free — the entire "constructor table" is `checkDecl` extending Values. The `|` grammar collision was the only real fight, and backtracking arms turned it into a feature (pipe-after-match without parens).

**Verdict**: continue. → Spike 4 (streaming pipelines).

## Spike 2 — Typed interpreter over checked AST (2026-07-11, session 1)

**Built**: `Eval.fs` rewritten over `TypedExpr` — untyped Spike 0 eval deleted. Value domain grown: `VRecord` (name + field map), `VUnion` (shape only, constructed in Spike 3), `VSeq`. All type-impossible arms are `unreachable` calls. New `Builtins.fs`: each builtin is one `(name, Ty, Value)` entry, so the TypeEnv and value env derive from a single list and can't drift. `ls` is real (`Seq.delay` over cwd — fresh listing per enumeration), typed `seq<FileRow>`. 52 tests.

**Checker→interpreter handoff**: holds. Spike 1's acceptance expression evaluates over records (fake `ls` fixture in tests, real one in the REPL). All former "fails at runtime" tests are now "rejected at check time" tests — the runtime error class they covered is unreachable through the checked pipeline.

**Learned**:
- The gate exposed a Spike-0-era fixture as untypeable: `let add = fun a -> fun b -> ...` — let-bound bare lambdas can't infer in bidirectional checking without annotations. Not a bug; the idiomatic replacement is partial application of typed functions (`let staged = where (fun f -> ...)`), which infers fine and is more shell-like anyway. Parameter annotation syntax is the eventual fix if the limitation bites.
- Runtime type errors did disappear. What remains at runtime is honest: division by zero, IO failures. Those are not the checker's job.
- `VSeq` equality materializes both sides — fine for tests, will be a footgun with infinite seqs in Spike 4 (flagged there).
- `unreachable` messages name the checker guarantee they rely on — each one is a soundness assertion; if one ever fires, it points at the checker rule that lied.

**Surprised**: how mechanical this spike was after Spike 1 — the typed eval is *simpler* than the untyped one (no defensive error paths, just `unreachable`).

**Verdict**: continue. → Spike 3 (type declarations) or Spike 4 (streaming).

## Spike 1 — Bidirectional checker, nominal only (2026-07-11, session 1)

**Built**: spanned AST (`Expr = { Kind; Span }`), `Ty` (int-with-optional-measure/str/bool/fn/seq/record-by-name), `TypeEnv` (Values + Types), `infer`/`check` pair in `Check.fs`, typo hints via edit distance, REPL now typechecks before eval and prints caret-underlined span errors. 43 tests.

**Acceptance**: `ls | where (fun f -> f.Size > 1<mb>) | first 5` checks to `seq<FileRow>`; `f.Sze` rejected with span exactly on `Sze` + "Did you mean 'Size'?". Perf: ~µs per check, 10ms bound trivially met.

**Design decisions**:
- Binops promoted from desugared builtins to `EBinOp` — overloading (`+` on int/str, measure-preserving arithmetic) doesn't fit monomorphic env entries. `typeBinOp` is the single overload table.
- Builtins are monomorphic (`where : (FileRow -> bool) -> seq<FileRow> -> seq<FileRow>`); polymorphism deliberately absent, revisit with row polymorphism (parked).
- UoM = `TInt of string option`, equality by name, erased at runtime. `+`/`-`/comparison require same measure; `*`/`/` unitless only (no measure algebra).
- Lambdas don't infer, but two refinement rules cover the shell idioms: lambda applied to a known arg, and pipe-into-lambda (arg type flows into the param).
- `EField` carries the field's own span so typo errors point at `Sze`, not all of `f.Sze`.
- `==` not `=` for equality (avoids let ambiguity). Composite spans are unions of child spans; leaf tokens capture position before ws-skip.
- Spans compose via `Span.union`; retrofitting confirmed as the right fear — touching every parser production once was enough, but only because the AST was 8 cases.

**Surprised**: how little the bidirectional core is — `check` has 3 real rules (lambda, let, fallback-to-infer-and-compare). The complexity lives in `infer`'s per-node rules and error message quality, not the discipline itself.

**Verdict (provisional)**: checker felt tractable to write. GATE CONDITION: user line-by-line review of Check.fs pending — spike isn't closed until then.

## Spike 0 — Toy interpreter (2026-07-11)

**Built**: `Expr` DU (int/str/bool/var/let/lambda/app/pipe), FParsec parser, tree-walk eval/apply, REPL with persistent top-level `let`. 23 tests. `1 + 2 |> double` → 6 end to end.

**Learned**:
- FParsec's `OperatorPrecedenceParser` handles the whole binop/pipe layer; binops desugar to `EApp(EApp(EVar "+", l), r)` against builtin env entries, so eval has no operator special cases.
- Lambda/let-in must be *terms* of the OPP (not alternatives outside it) or they can't appear on a pipe RHS (`5 |> fun x -> x * x`). Greedy lambda body = F# semantics for free.
- `Value` can't derive structural equality once `VBuiltin of (Value -> Value)` exists — custom equality (structural for data, reference for functions) needed for test assertions. Will matter again for `VSeq` in Spike 4.
- FParsec error messages come with line/col and a caret out of the box — good omen for Spike 1 span work.

**Surprised**: nothing structural. Keyword-vs-identifier ambiguity (`true`, `fun`) needed the usual `attempt` + `notFollowedBy` dance.

**Verdict**: continue. Eval/apply shape clicks, FParsec workable. → Spike 1.
