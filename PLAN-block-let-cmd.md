# weir — block-let command RHS: the uniformity fix

Status: BLESSED (user 2026-07-23). EXECUTED 2026-07-23 — see the
addenda. Origin: the parked half of the param-ful-RHS session,
reopened on three independent encounters (the git-subrepo port
hoisted twice; the user hit the wall live KNOWING the workaround).
The asymmetry was the case: same RHS, same meaning,
position-dependent parse with nothing in the spelling signaling it.

## Pre-made decisions (abridged; full text in the blessing message)

- DECIDED — drop the block-let restriction: same head rules,
  splices, pipes, reifiers, `^` force as the top-level bare RHS —
  ONE grammar, one more position; semantics pinned equal to the
  sigil spelling.
- DECIDED — the `in`-stop is the load-bearing pin, exercised from
  inside (bareword stop, quote-to-pass, the pathological adjacency).
- DECIDED — block-let names shadow PATH at their depth (the
  param-ful law one scope deeper; the block twin written FAILING
  first).
- DECIDED — products: pending-let nesting, districts, the
  compound-paren prune, greedy-`;`/offside, sigil equivalence,
  fmt, missing-command diagnosis.
- DECIDED — the e2e is the un-hoisting (the port's wrappers inline
  back).
- DECIDED — rider: `function` reserved (hint teaches the spelling
  and holds the park; `^function` reaches PATH; oracle both ways).
- POSITIONS: top let unchanged; block let NEW; single-line
  `let ... in` still EXCLUDED (the in-swallow park stands, pinned).

## Completion addenda (2026-07-23)

### Done-when, discharged

The forms block runs (command RHS binds, pipes, and reifies inside
bodies); the port's treeExists/isAncestor wrappers are inlined back
and the lifecycle smoke is green; `let zzshadow = fun a -> a` in a
body shadows a real PATH zzshadow — pinned FAILING FIRST (SPAWNED
observed live on the guard-dropped build); the in-stop holds from
inside with the quoted-"in" case; `function` is reserved with its
teaching hint, `^function` reaches a PATH binary live, and FCS
agrees the binder rejection is Same; single-line let-in still
rejects (pinned); parens and lambda interiors still reject
(pinned). 803 unit / 137 oracle / full e2e / 50 doc blocks; timing
unchanged.

### The mechanism: a spine flag

A ThreadLocal boolean true only along the statement spine a block
assembles into — topLet's RHS and its let-in chain — with parens
and lambda bodies switching it off. The in-swallow park's boundary
is therefore held BY CONSTRUCTION: the excluded positions cannot
reach the command grammar, and the pins hold the line from both
sides. The resolver extension is the param-ful mkR one scope
deeper: each let-in binder extends the ambient resolver for its
body parse, so later block-let RHS heads see earlier block names.

### Found by the depth battery

`reifierEnd` (pipe / `)` / eof) did not know the ` in ` boundary:
`| succeeds in body` demoted the reifier to a bareword stage. The
in-stop now reaches reifierEnd, gated on the same spine flag —
zero effect outside let-RHS chains.

### Zero checker surface

As the param-ful session predicted for its own scope: no checker
arms; ELet-of-ECmd is existing typed machinery. The stop-and-report
clause stayed cold.

## Parked (updated)

- Command mode in single-line `let ... in` — the exclusion STANDS,
  now held by pins on both sides of the boundary.
- The `function` sugar — unchanged park, its name held by
  reservation, the restricted single-line form pre-scoped.
