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
  — THIN: ~1% of base programs (4/400 measured); a 200-case smoke
  sees a handful, the shape leans on deep runs for real sweep
- `print` statements (interp markers); `if ... then` unit bodies
  (nested ifs, prints, headed command groups)
- record type decls: inline, Stroustrup, aligned styles; `///` field
  docs in the Stroustrup style (doc line + field line as one aligned
  entry, governed by the doc-alignment lint)
- union type decls: single-line and multi-line `|` case lists;
  tuple-free payloads (`of int` / `of string`)
- record literals: inline, Stroustrup, aligned (measured anchors)
- list literals: inline, Stroustrup, aligned; int and string elements
- multi-line pipelines (`|>` stages under a dangling let)
- argv splat: `echo $@([...])` on echo statements — TRANSFORM-ONLY
  spelling [D:argv-splat]: the base render always emits inline words
  (measured 0/400 base programs), the splat form appears when the
  splat-of-literal transform flips the config
- command lines: top-level bare `echo`; command-backed `let` (top
  level AND block bodies — the spine flag); `seq |> print`;
  `(xs |> Seq.length)` forcing command output in expressions
- bare command GROUPS (the retired districts' coverage, retargeted
  [D:district-retirement]): standalone runs at statement level, headed
  `if … then` bodies of bare commands — with the per-line `!(...)`
  spelling as the bare-vs-sigil EQUIVALENCE transform (the arming
  rule's own metamorphic property [D:interior-arming])
- yaml districts (top-level only): 1–3 literal keys, int/word/splice
  values (splices draw existing int/string binders), one optional
  nested map, each rendered with a trailing `d |> to yaml |> print`
  so output identity SEES the district; the marker line and content
  lines are NON-error territory for the span invariants (junk on the
  marker re-reads as command argv under the assume resolver — the
  agreement property's quarry, not the span property's)
- multiline lambdas [D:multiline-lambda]: dangling `(fun p ->` opening
  a body block (block-let + prints inside), closer attached AND alone,
  as a bare iter statement and as a map on a let-RHS pipeline
- float statements (top-level only): `let f = a op b` over exact
  quarter literals (+ - *; division excluded — a generated zero
  divisor would RAISE by the finite-only law), a `show`/interp print,
  and an optional `Ord` comparison print (`f < c`). **The Eq
  exclusion shaped this production** — the first time a class
  decision shaped the generator: float expressions can NEVER join the
  `CCmp "=="` arms (`==` on floats is a check error by design), so
  floats are not wired into the shared typed-expression grammar; they
  are `==`-free but NOT comparison-free (`<`/`>` are admitted — Ord
  holds). Trailing comments ride the append-trailing metamorphic over
  these lines like any other code line.
- retry/poll statements (top-level only): `let v = retry attempts=1
  delay=0ms` / `poll timeout=1s interval=0ms` heads, an int body
  block, a col-0 `until r` segment with its predicate block, and a
  trailing print — single-attempt DETERMINISTIC (the threshold sits
  below the value; the loop never sleeps). Exercises the two-segment
  compound join (the col-0 `until` routing) and the key=value head
  desugar on every deep run.
- placement laws the probes established (generator-enforced): bare
  command lines and type declarations are top-level only; if bodies
  are expression territory (bare commands are statements there now [D:interior-arming]);
  record field sets are unique per type (ambiguity)

## CANNOT yet produce

- yaml `for` entries, key splices, block scalars, and `schema=`
  declarations — the district PRODUCTION (above) covers plain
  maps/scalars/value-splices only; these shapes remain outside it,
  on record (they are pinned at unit/e2e).
- multiline string LITERALS (never — weir strings are single-line
  [D:raw-strings]; multiline string VALUES exist via yaml block
  scalars, covered by the yaml-district entry above)
- the RETIRED `!`/`!name` district markers (now a teaching error) and env sigils (`$e`/`!e`);
  `sh -c` lines; `| from jsonl` adapters; exit reifiers; a third of flat
matches render as `(function | arms) scrut` [D:function-keyword]
  (`succeeds`/`complete`/`orFail`/`exitCode` — outside the MAIN
  grammar; reifier CHAINS are swept by invariant 6's dedicated
  splat-reifier generator, safe words only)
- float `/` (a generated zero divisor would raise — the float
  production stays total by construction), floats inside the SHARED
  int/string expression grammar (the Eq-exclusion wiring stated in
  the CAN entry), and float literal PATTERNS (declined by design
  [D:floats])
- multi-attempt retry/poll (a failing attempt would SLEEP — fuzz runs
  stay fast by construction), the bool-bodied unit form, and computed
  options records (the desugar equivalence is unit-pinned instead)
- `let ... in` inline form; param-ful lets / function defs;
  seq patterns; Regex patterns; tuples; copy-and-update literals;
  `Args.load` / `Env.load`; raw strings; `#loose` mode
- `for … do` effect loops, `do !` command blocks, and
  `[for … -> …]` comprehensions [D:for-do] — statement line-shapes
  landed 2026-07-30, never added here (the coverage audit's
  staleness pass put them on record); their desugar equivalences are
  named exclusions below
- `module` files and `import` statements [D:modules-v1] — multi-file
  shapes; the generator emits single programs only
- plain `//` comments, and blanks INSIDE a statement (they arrive
  only via the transform layer). The base render DOES contain
  between-statement blank separators and `///` field docs (8/400
  measured) — the CAN list's doc entry, not this exclusion

## Shrinking

Delta debugging on top-level statements with dependency closure
(program-unique names make the closure set arithmetic); inner-block
statement removal is not shrunk. FsCheck drives the shrink loop; the
reported counterexample is the minimal statement subset that still
fails.

## Invariants wired

**Positive controls [D:walk-findings]** — what makes the invariant
claims non-vacuous. Each detector below has a standing must-fail
control in the SMOKE ("Positive controls" testList, Main.fs): an
output-changing transform and an rc-changing one both fire invariant
1's comparison and the control asserts the failure carries "transform
changed behavior" (by name, not merely a throw); a throwing pipeline
and a deliberate hang both fire invariant 2's totality detector by
name; invariant 4's shape language is asserted to DISTINGUISH two
different programs (its equality cannot pass vacuously). Invariant
3's control is sized, not built: its expecting-a-diagnostic checker
is inline in two properties and needs a small extraction first. A
control that ran once at bring-up is not a control — the DECISIONS
walk found exactly that gap here.


1. Metamorphic equivalence on the AOT binary — (rc, stdout, stderr)
   byte-identical under: blank insertion (any gap), comment insertion
   (any gap, any indent 0–12), whole-block re-indent (+1..6 on one
   block: let bodies, if bodies, command groups, match-arm groups,
   Stroustrup bracket groups, pipeline stages); yaml marker form
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
   expression-territory line (command lines are argv territory and
   `///` doc lines are comment territory — both excluded by the
   renderer's tags; the doc-line exclusion is the coverage audit's
   fresh-seed find: junk on a doc line is legal doc TEXT, and
   targeting it made deep runs red while the pinned smoke stayed
   green) must be diagnosed. The HARD floor
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
6. Check agrees with run [PLAN-refactor-followups 1]: for every
   generated program, each logical line parses to the SAME sexpr and
   checks to the SAME verdict under the assume-resolver (tooling)
   and the hard resolver (the runner) — the five-incident resolver
   seam, finally asserted. Generated heads are real on PATH, so the
   hard side resolves exactly as the runner would; an agreed
   rejection stops the walk (valid-by-construction programs should
   never reach it). Divergences found by this property are FINDINGS
   with sizes, never same-session fixes.
7. Splat-in-reifier equivalence [D:splat-reifier-chains]:
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
generated as a base style but not flipped by a transform),
`for p in xs do body` ↔ `xs |> Seq.iter (fun p -> body)` and
`[for x in xs -> e]` ↔ `Seq.map`+force ([D:for-do]'s desugar-at-parse
claims — the grammar has no for production), and the yaml district's
marker ↔ node-construction spellings (no yaml production).

Smoke: pinned seed 1789001, 200 cases/invariant (~15s measured 2026-08-01; deep 1200-case runs ~65s/seed with the yaml production + agreement property — the noted ~20% lengthening), wired into CI
after publish + e2e. Deep: `tools/fuzz.weir [--seed N] [--count N]` (fresh
seed, 10k default). The equality detector's graded positive
control (a deliberately non-neutral edit fails the property) was
verified at BRING-UP — it is not a standing test; re-verify by hand
if the detector is ever touched.
