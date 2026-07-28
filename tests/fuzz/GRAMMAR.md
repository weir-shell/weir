# The fuzzer's generator grammar — the coverage denominator

"The fuzzer passed" is a claim with this file as its denominator: the
generator (tests/Weir.Fuzz/Grammar.fs) produces exactly the shapes
listed under CAN; anything under CANNOT-YET is outside the net, on
purpose and on record. New assembler features add their shapes here
and their equivalence claims to the transform library — the
metamorphic law is part of the feature.

Programs are valid-by-construction: type-correct, casing-lawful,
exhaustive matches, exact alignment, program-unique names, effects
carrying unique markers (`m<n>`) so output identity sees ORDER.
Expression bodies are trivial (small ints, safe words, `$"..."`
interpolation with int-typed holes) — the subject is line-shape
composition, not type complexity.

## CAN produce (Session 1)

- top-level `let` with single-line RHS; block-bodied `let` (unit
  statements + result line, nesting depth ≤ 2)
- `let` = multi-line `match` (dangling head): int/string literal arms,
  optional `when`-guard arm, catch-all last; nested match as an arm
  RHS (the offside-close shape)
- full-coverage match over a union value (payload binders int/string)
- `print` statements (interp markers); `if ... then` unit bodies
  (nested ifs, prints, headed districts)
- record type decls: inline, Stroustrup, aligned styles; `///` field
  docs in the Stroustrup style (doc line + field line as one aligned
  entry, governed by the doc-alignment lint)
- union type decls: single-line and multi-line `|` case lists;
  tuple-free payloads (`of int` / `of string`)
- record literals: inline, Stroustrup, aligned (measured anchors)
- list literals: inline, Stroustrup, aligned; int and string elements
- multi-line pipelines (`|>` stages under a dangling let)
- argv splat: `echo $@([...])` on echo statements (the
  splat-of-literal transform target [D:argv-splat])
- command lines: top-level bare `echo`; command-backed `let` (top
  level AND block bodies — the spine flag); `seq |> print`;
  `(xs |> Seq.length)` forcing command output in expressions
- districts: standalone `!` (top level), headed `if ... then !` (top
  level, if bodies, block bodies)
- multiline lambdas [D:multiline-lambda]: dangling `(fun p ->` opening
  a body block (block-let + prints inside), closer attached AND alone,
  as a bare iter statement and as a map on a let-RHS pipeline
- placement laws the probes established (generator-enforced): bare
  command lines and type declarations are top-level only; if bodies
  are expression territory (districts are the command spelling there);
  record field sets are unique per type (ambiguity)

## CANNOT yet produce

- multiline lambdas (not a weir feature yet — this harness is that
  feature's acceptance rig when it lands)
- multiline strings (never — not a weir feature)
- env-parameterized districts (`!name`) and env sigils (`$e`/`!e`);
  `sh -c` lines; `| from porcelain/json` adapters; exit reifiers
  (`succeeds`/`complete`/`orFail`/`exitCode` — the family is outside
  the generator's shape list)
- `let ... in` inline form; param-ful lets / function defs;
  seq patterns; Regex patterns; tuples; copy-and-update literals;
  `Args.load` / `Env.load`; raw strings; `#loose` mode
- comments/blanks INSIDE the generated program (they arrive only via
  the transform layer)

## Shrinking

Delta debugging on top-level statements with dependency closure
(program-unique names make the closure set arithmetic); inner-block
statement removal is not shrunk. FsCheck drives the shrink loop; the
reported counterexample is the minimal statement subset that still
fails.

## Invariants wired

1. Metamorphic equivalence on the AOT binary — (rc, stdout, stderr)
   byte-identical under: blank insertion (any gap), comment insertion
   (any gap, any indent 0–12), whole-block re-indent (+1..6 on one
   block: let bodies, if bodies, district bodies, match-arm groups,
   Stroustrup bracket groups, pipeline stages); district marker form
   ↔ explicit `!(...)` lines; bare command RHS ↔ `$(...)`; block
   siblings ↔ single-line `;` (print-only bodies — the probed
   boundary: inner lets spell `in`, commands take `;` as argv).
   [D:sibling-sentinel] block statement-siblings now assemble with the
   MACHINE SENTINEL (U+001F), not `;`, so this transform routes every
   block-form print-sibling THROUGH the sentinel and asserts it stays
   equivalent to the single-line `;` form — the load-bearing span/
   behavior check the sentinel must preserve (strict-spans deep run,
   4000+ cases, green; same 3-char join width keeps `translate`
   byte-identical). The restriction to print bodies is now BY
   NECESSITY, not convenience: for a bare command sibling the two
   forms are DELIBERATELY NOT equivalent (block-sentinel sequences a
   real ESeq; single-line `;` is a swallowed argv word), so the
   command-first shape cannot be a metamorphic pair — it is pinned
   directly instead (Tests "Sibling sentinel");
   Stroustrup ↔ inline bracket styles; multiline lambda ↔ its
   single-line `;`/`in`-joined form; and ALL of it COMPOSED in one
   property (random subsets of every flip + re-indent + comments +
   blanks — the laws must hold under composition).
2. Totality of `Script.analyzeLines` (assemble → parse → check, the
   one pipeline) on every generated program and mutated neighbor
   (line deletion, ±1..3 indent perturbation, line duplication,
   adjacent-line swap, and stacked pairs): no exception, no >5s hang.
   DEPTH AXIS [D:depth-guard]: the generators favor breadth (nesting
   ≤ 2 above), so extreme depth is pinned separately — the three
   safe-by-design-review fixtures (deep parens and a long operator
   spine, both once SEGV; nested brackets, once O(2^n)) as standing
   seeds, plus a generated sweep of over-ceiling (600–4600) parens/
   brackets/operator-spines — each must diagnose (an error, not
   silent acceptance) within the hang bound. A process crash here
   takes the runner down, so survival IS the no-crash safety pin.
3. Span soundness: a bad token (` ?!?`) appended to a random
   expression-territory line (command lines are argv territory —
   excluded by the renderer's tags) must be diagnosed. The HARD floor
   (some error diagnostic exists) holds unconditionally; the strict
   positional assertion (the injected line, col within extent, a
   translated backtrack note counts) runs under
   `WEIR_FUZZ_STRICT_SPANS` (default ON — GRADUATED once both span
   classes closed: the district wrap by [D:seq-commit], the bare-pipe
   fatal by [D:arm-commit]; the consumed-separator law). The strict
   positional assertion is a standing guarantee in the CI smoke.
4. fmt roundtrip: `formatLines` succeeds on every generated program,
   is idempotent, preserves per-statement sexpr shape (the respace
   guard's own predicate), and the formatted program is
   output-identical on the binary.
5. Value-headed pipe equivalence RETIRED [D:drop-command-builtins]:
   the law compared `xs | prog args` to `xs |> feed …`; `feed` is
   dropped (the command-value tier retired — every head is a literal),
   so there is no second spelling. The value-headed pipe is pinned by
   e2e + unit.
6. Splat-in-reifier equivalence [D:splat-reifier-chains]:
   `echo m $@([ws]) | reifier` ≡ the inline-words spelling,
   byte-identical (rc, stdout, stderr) across the four reifiers — the
   splat's elements ride the builtin's argv with word integrity
   intact. A DEDICATED generator (reifier chains are outside the main
   grammar's shape list, like the depth axis); adversarial words are
   pinned by unit + e2e, safe words swept here.

Ledger equivalence claims NOT yet in the transform library (named
exclusions): env-district/`$e`/`!e` spellings (env features are
outside the grammar), `let ... in` ↔ block lets (grammar has no
inline `in` form yet), aligned ↔ Stroustrup/inline (aligned is
generated as a base style but not flipped by a transform).

Smoke: pinned seed 1789001, 200 cases/invariant (~7s), wired into CI
after publish + e2e. Deep: `tools/fuzz.weir [--seed N] [--count N]` (fresh
seed, 10k default). The equality detector has a graded positive
control (a deliberately non-neutral edit fails the property —
verified at bring-up).
