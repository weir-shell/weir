# Grammar/assembler decision products — the retroactive sweep matrix

Written 2026-07-20 (hardening sweep), BEFORE the missing-cell pins.
Origin: the greedy-`;` silent swallow — the composition-product rule
would have caught it, but postdated the decision it needed to guard.
This matrix applies the rule to every grammar/assembler decision that
predates it. Cells: pin name (EXISTS), NEW (added this sweep), or
N/A + reason. Update when a decision is added or amended.

Decisions:
- **A** greedy-`;` + offside close (sequencing session, amended by
  grammar consolidation)
- **B** block lets (pending-let stack, " in " insertion)
- **C** `|`-inertness (pipe/arm lines extend; inert to sibling rule)
- **D** statement rule × command mode (SCmd streams; SExpr must be unit)
- **E** blank-line-ends-statement
- **F** comment transparency (comment-only lines filtered pre-assemble)
- **G** sibling rule (same-indent block siblings join with `;`)

| ×   | B | C | D | E | F | G |
|-----|---|---|---|---|---|---|
| **A** | EXISTS — "let-close beats sibling", offside-at-let-close amended pins | EXISTS — "else extends the compound", match-arm pins | EXISTS — semicolon-command-argv warn pin (`;` in command mode is argv) | NEW — blank ends a compound body; the dedent tail runs UNCONDITIONALLY (effect-counted) | NEW — comment inside a compound body is transparent; grouping unchanged (effect-counted) | EXISTS — the offside battery is this product |
| **B** |   | EXISTS — dedented-arm pins + oracle F#-rejects pair | EXISTS — let-RHS command battery (builtin heads, `in`-stop) | EXISTS — noBodyBlank ("blank line ends the statement") | EXISTS — "comment lines are transparent inside blocks" | EXISTS — "let-close beats sibling; sequence resumes after" |
| **C** |   |   | EXISTS — col-0 `\|` chain continuation (command-mode battery) | NEW — pipe line after a blank is the located continuation error | NEW — comment between pipe stages is transparent | EXISTS — "pipes stay inert to the sibling rule" |
| **D** |   |   |   | N/A — blank between col-0 statements IS the default statement separator; every multi-statement fixture exercises it | EXISTS — comment-transparency battery incl. the bareword-URL command pin | N/A — command mode exists only at col-0 statements, sigil interiors, and districts; the sibling rule never sees a command piece (districts have their own joins, pinned in the district battery) |
| **E** |   |   |   |   | NEW — comment-only after a blank is invisible; an indented line after it is the continuation-after-blank error | NEW — indented line after a blank is the located error even at a former sibling level |
| **F** |   |   |   |   |   | NEW — comment between same-indent siblings; `;` still joins across it (effect-counted) |

Red-cell triage protocol: any pin that lands red is a finding of the
silent-swallow class — fix + archaeology, its own follow-up if
non-trivial; the sweep does not absorb deep fixes silently.

## Sweep result (2026-07-20, at close)

Six NEW cells pinned (A×E, A×F, C×E, C×F, E×F, E×G, F×G — assemble
level; A×E/A×F/F×G also effect-counted e2e). All landed GREEN — the
invariants were held-by-behavior and are now held-by-test. The
sweep's two RED findings came from the OTHER work items, exactly per
the triage protocol:
1. Fixture backfill (nested record): a field value opening on the
   next line got a spurious separator — fixed in the classifier
   (StartsField; the separator goes before field-start lines only),
   pinned at assemble + e2e.
2. ExitRequest fifth site: the REPL swallowed the carrier as
   "error:" and exited 0 — red pin written first, then fixed
   (tryRun rethrows, run returns the code). Mechanism chosen:
   PIN-PER-SITE (script/-e/REPL e2e), not helper unification — the
   three sites return different shapes (exec fold, int main arm,
   Result loop) and a shared protect() would obscure more than it
   insures. The known-seam risk note stands in dev/PROCESS.md's index.

## Axis 2: class constraints × existing machinery (Session C, 2026-07-21)

Written FIRST, per the composition-product rule. Decisions on the new
axis: Eq/Show/Ord demand rules, constraint scooping (generalization),
instantiation freshening, row riding/discharge, the ambiguity rule.
Products against: generic unions/records, rows, nested
generalization, match guards, the print sentinel, splices, parallel
combinators. POSITIONS.md note: classes add NO expression form or
token — no position sweep required; the exclusion is this sentence.

| product | cell |
|---------|------|
| Eq × generic unions (deep) | NEW — `Option<Option<int>>` accepts; function payload rejects through two levels |
| Eq × generic records | NEW + FINDING — a fn-typed field is UNREACHABLE by declaration but REACHABLE via generic instantiation (`Box<'a>` at `{ V = print }`); Eq must reject through it. Session A's "unreachable" scope note was WRONG for generics — this cell is the correction |
| classes × rows × double instantiation | NEW — one row-constrained scheme, two records: clean record passes, seq-carrying record rejects, both from the same scheme |
| constraint × mergeRows | EXISTS (unit hook) + NEW behavioral — two constrained rows unify; the moved constraint still fires |
| constraint escape through nested generalization | NEW — inner constrained scheme, outer scoop: `Eq` climbs to the outer scheme and rejects at the outer use site |
| ambiguity × let (constraint off the value type) | EXISTS — "nothing determines" pin (A) |
| classes × match guards | NEW — `==` in a `when` guard demands through the scrutinee |
| classes × print sentinel | NEW — `print (show x)` composes; print's scalar rule untouched by Show |
| classes × splices | NEW — a constrained helper's result in a command splice |
| classes × pmap | NEW (e2e) — constrained closure across workers; erasure means nothing crosses threads but values |
| classes × districts/assembler | N/A — classes are checker-only; no line-shape surface |
| Ord × decomposition | EXISTS — tripwire (B) |
| Show/Eq shared var | EXISTS — B battery (strictest class decides) |
