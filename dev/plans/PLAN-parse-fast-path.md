# PLAN: the parse fast path — dispatch on the next character, fall back to the grammar for the error

**Status: CLOSED-P0** (2026-10-27) — the spike was built, correct,
and parity-identical, and gained 10 ms of the 20 the bar asked for.
Nothing landed; the report below is the ledger. P1 is not blessed on
this shape — see "Read on P1".

## Session report — P0 (2026-10-27)

One line: the `atom` dispatch works exactly as designed (byte-identical
diagnostics over the corpus, 2686 unit + 55 fuzz green) and the
starship import drops 258→248 ms — the atom choice is not where the
parse's 110 ms live.

### What was built (not committed; patch at /output/tmp/lex-atom-dispatch.patch)

`atom = deepen (fun stream -> …)`: peek the next character, run the one
alternative (or opener group, in oracle order) that can succeed; return
only a success; on any failure `BacktrackTo` the saved state and run
the unchanged `choice` (`atomChoice`) so every error — status, position,
merged Expecting list — is the grammar's. The oracle list was not
reordered. Opener groups: `(` → `choice [unitLit; opValue; parens]`,
`[` → `choice [attempt comprehensionLit; listLit]`; the sub-choice's
success reply equals the full choice's because every atom success
consumes input (the state tag changes, so FParsec drops the earlier
alternatives' merged errors in both).

### Disjointness — the oracle's alternatives by first token

| alternative | first token | dispatch |
|---|---|---|
| attrsRejectHere | `[<` | oracle (teaching) |
| negAtom | `-` (prev-char rule inside its attempt) | fast: negAtom |
| intLit | digit | fast: intLit |
| dotFloatTeaching | `.` digit | oracle (teaching) |
| holeBackslashTeaching | `\` in a hole | oracle (teaching) |
| tildeTeaching | `~` | oracle (teaching) |
| bangNegationTeaching | `!` ident | oracle (teaching) |
| tripleLit | `"""` | fast: tripleLit |
| verbatimLit | `@"` | fast: verbatimLit |
| strLit | `"` (not `"""`) | fast: strLit |
| dollarAtTeach | `$@"` | oracle (teaching) |
| interpRawLit | `$"""` | fast: interpRawLit |
| interpLit | `$"` (not `$"""`) | fast: interpLit |
| captureSigil | `$` [word] `(` | fast: captureSigil (`$` not followed by `"`/`@`) |
| unitLit / opValue / parens | `(` | fast: the `(` group, oracle order |
| anonRecordLit | `{\|` | fast: anonRecordLit |
| recordLit | `{` (not `{\|`) | fast: recordLit |
| comprehensionLit / listLit | `[` (not `[<`) | fast: the `[` group, oracle order |
| tryAtomTeaching / orFailAtomTeaching | `try` / `orFail` | both are keywords: wordAtom fails cleanly, the oracle replays and the teaching fires |
| wordAtom | letter or `_` | fast: wordAtom |

Per first character the classes are disjoint (`[` `-` digit `.` `\` `~`
`!` `"` `@` `$` `(` `{` letter/`_`); within a class the oracle order is
kept by the group choice or by the second-character split. Every
alternative ahead of a dispatched one fails without consuming and
without a fatal on that character (the teachings' triggers are the
excluded openers). Any other character (closers, operators, eof) goes
straight to the oracle, as does every clean failure of a fast
alternative (keywords at chain ends: `then`, `in`, `with`, `|>`…).

### Numbers (AOT, `taskset -c 5`)

The box carried external load (load avg ~2) during the session, so the
honest instrument is the alternating A/B (`/output/tmp/ab.sh`: one
run of each binary in turn, medians of wall and user+sys CPU);
before = perf/checker-speed's publish, after = the spike.

| benchmark | before | after | delta |
|---|---|---|---|
| starship import (n=31 alternating) | 258 ms wall / 254 CPU | 248 / 244 | −10 ms |
| starship import (n=21 alternating) | 249 / 245 | 240 / 236 | −9 ms |
| sys.weir (n=21) | 95 / 92 | 92 / 88 | −3 ms |
| bare startup (n=21) | 12 / 9 | 12 / 10 | 0 |

bench.sh's sequential medians of 9 read 277→241, 281→257, 293→261 —
the "before" runs landed on load spikes (the ledger's 254 matches the
alternating A), so those deltas are not claimed.

Ceiling probe (measurement only, not parity-safe, reverted): the same
dispatch returning a bare failure instead of replaying the oracle —
the cost of `atom` if its failure path were free — reads 253→232 ms
(CPU 249→227, n=31). So the whole atom choice is worth ~21 ms, the
parity-safe half of which P0 takes; the other half is the clean
failure at every chain end, whose merged Expecting list must be built
because a sibling failing at the same position merges with it.

Verification: Debug build warning-free; `dotnet test tests/Weir.Tests`
2686/2686; parity over the 80-file corpus (62 error cases)
byte-identical stdout+stderr+exit; `tools/fuzz.weir --seed 4242
--count 10` 55/55, "deep fuzz clean … (observed)". No e2e (bar not met).

### Read on P1

Not on this evidence. The dispatch shape is sound and cheap to carry,
but its premise — that scanning the alternatives is the cost — held
for only ~20 of the parser's 110 ms at the one choice with two dozen
alternatives; the other hot choices are shorter, so dispatching them
buys less. The receipt's 870 bytes per character points at allocation
on the SUCCESS path (spans/positions, `attempt` and `many` replies,
merged error lists carried through successful `many`), and at the
double parse of statements (`choice [commandSegment …; segExpr]`,
`ifCondCmd <|> expr`). Next step, if any: a sampling or allocation
profile of `parseLineFull` on the starship import before another
dispatch is written — i.e. P2's measurement first, P1 only where a
profile names a choice.

## The receipt

After perf/checker-speed the starship import spends 110 of its 250 ms
in `Parser.parseLineFull` (FParsec), allocating about 870 bytes per
character; most of the 70 ms of GC pauses are that allocation. The
`atom` parser is a `choice` of roughly two dozen alternatives tried in
order before the common case (`wordAtom`); each alternative scans and
backtracks. The session ruled reordering out: FParsec's merged
"Expecting:" lists reach users verbatim (`Script.rebuildExpecting`,
Parser.fs ~612 and ~3211 shape them), so changing the alternatives or
their order changes diagnostics.

## The idea

Keep the grammar as the oracle for errors; skip it on the happy path.

A fast path peeks at the next character (or a short prefix) and runs
ONLY the alternative it determines — `"` → string, `$"` → interpolated,
`@"` → verbatim, `[` → list, `(` → parens/unit/tuple, `{` → record,
a digit → number, `fun` → lambda, an identifier start → `wordAtom`, and
so on. If that alternative SUCCEEDS, its result is what `choice` would
have produced (the alternatives are mutually exclusive on their first
token — this must be proved per alternative, P0 lists them). If it
FAILS, the fast path does not report: it restores the position and runs
the ORIGINAL `choice`, which fails the same way it does today, with the
same merged Expecting list. Diagnostics are identical by construction:
every error is produced by the unchanged grammar; the fast path only
ever returns successes the grammar would have returned.

The same shape applies to any `choice` on the hot path (statement
heads, command segments vs expressions, patterns) one at a time, each
with its own parity run.

## Anchors

- `Parser.fs`: the `atom`/`wordAtom` choice (find it — `choice [` with
  many alternatives in the expression grammar), `appChain` (~1811),
  `parens` (~1014), `letInBody` (~2082), `deepen`/`undeepen` (the depth
  guard around nesting, TLS), `litHead` (~4477), `commandSegment`
  (~4625).
- `Script.rebuildExpecting` (~3477) — the one place Expecting text is
  reshaped; proves which errors carry alternative lists.
- The parity corpus and comparison script from the perf session in
  /output/tmp (bench.sh and the 80-file diff; 62 error cases) — the
  acceptance instrument; promote to `tools/check-parity.weir` if the
  parallel-imports branch has not already (coordinate: that branch
  owns the promotion; this one uses it).
- The perf session's measurement discipline: AOT binary only,
  `taskset -c 5`, medians of 9, the three benchmarks (starship import,
  `sys.weir`, bare).

## Phases

- **P0 — the spike (this session).** Implement the dispatch for `atom`
  only, exactly as described (peek, one alternative, on failure replay
  the original choice from the saved position). Enumerate the
  alternatives with their first-token classes and show they are
  disjoint (any overlap → that alternative stays on the slow path).
  Measure the three benchmarks. Run the parity corpus. BAR: starship
  import improves by at least 20 ms with parity byte-identical;
  otherwise the plan closes with the numbers and the reason.
- **P1 — the other hot choices** (statement head, command-vs-expression
  segment, pattern atoms), one commit each with numbers and parity.
- **P2 — allocation.** With dispatch in place, the per-character
  allocation profile (which combinators allocate on success) — only
  what the trace shows; no speculative rewrites.
- Ledger: CHANGELOG under `## v0.0.81` → `### Fixed` (extend the
  launch-speed bullets); DECISIONS row `parse-fast-path` (the
  oracle-grammar law: errors always come from the unchanged grammar);
  this plan's status and session report.

## Not here

- A hand-written lexer or parser replacing FParsec (the redesign this
  plan avoids).
- Any change to error text, position, or which error is first.
- Reordering `choice` alternatives.
