# weir — message domination: the teaching fatal must win its spot

Status: EXECUTED (2026-07-27). Origin: finding-class (b) from
[D:anchor-before-read]'s sweep table (NOTES). One session,
diagnosis-first — the fix is context-sensitive, not a blanket flag.

DIAGNOSIS TABLE (site / fall-through? / clean after consume-then-anchor? / verdict):
- splat head `$@` cmd / no / yes / FIXED (consume `$@`, anchor back)
- splice mid-word `$x` / no / yes / FIXED (notMidWord consumes `$`)
- splat mid-word `$@` / no / yes / FIXED (same)
- reserved word in BINDER name (`let function/rec/mutable`) / the
  shared `ident` HAS fall-through (`1 in y`, `let x = if …`), so a
  gated `letKeywordGuard` fires OUTSIDE topLet's attempt / yes / FIXED
- neg-int-out-of-range / — / NO: contested between `negIntLit` and the
  unary-minus operator's operand (both hit int-oor at col 9 vs 10),
  neither dominates / FINDING (left as-is: the clean message wins)
- reserved word in PARAM / FIELD position (`let f rec`, `{ let = 1 }`,
  `type T = { let: int }`) / distinct sites, generic "expecting '='"
  not the keyword teaching / FINDING (out of the binder-name scope)

## The defect

Several teaching errors have the CORRECT caret but their message is
BURIED under a raw FParsec expecting-list ("Other error messages:
…"). The anchor-before-read sweep proved the position is right; the
MESSAGE is the gap. Live repros (caret / buried teach):

    $@xs foo            1:1  — "a splat cannot head a command"
    echo --flag=$x      1:13 — "a splice cannot join a word …"
    echo a$@x           1:7  — same, splat form
    let function = 1    1:5  — "'function' is a keyword"
    let rec = 1         1:5  — "'rec' is a keyword"
    let mutable = 1     1:5  — "'mutable' is a keyword"
    let x = -9999…9     1:9? — int out of range, coupled below

Root: these fire from a NON-consuming `fail`/`failFatally` (a
`lookAhead` guard, or a `notKeyword` inside an `attempt`). Because
nothing was consumed at the failing spot, FParsec MERGES the
competing "expected" errors that legitimately sit there into the
report. (Contrast the anchor sweep's FIXED sites: they consume their
trigger first, which CLEARS the competitors, then seek back — the
`failFatallyAt` shape.)

## The crux — why this is careful, not mechanical

The reserved-word case CANNOT just go fatal. `notKeyword` (Parser.fs)
fails NON-FATALLY on purpose: `identSpanned` tries a word as an
ident, and when the word is a keyword the non-fatal fail BACKTRACKS
so the keyword reaches its own parser — `let x = if c then …` needs
`if` to fall through to `ifExpr`. A blanket fatal would break the
grammar. The teaching must dominate ONLY in NAME/BINDER position (a
`let <name>`, a param, a field name) where the keyword is
unambiguously meant as an identifier; the fall-through position must
stay non-fatal. That split is the session's real work.

The splat sites are simpler: a `$@`-headed command is ALWAYS an
error (no legitimate fall-through), so consume-then-anchor
(`failFatallyAt` over the `$@`) should dominate cleanly — but
diagnose each, because the seek lands clean only where the anchor
position has no surviving competitor.

## Diagnose first (before any edit)

For EACH site, determine: (1) does the trigger have a legitimate
non-error fall-through at that position? If yes → the fatal must be
GATED to the error-only context, not global. (2) After
consume-then-anchor, does the message come out CLEAN (no
expecting-list), or does a competitor survive the consumption? If a
competitor survives, that is a FINDING (report the surviving
alternative), not a silent partial fix.

Candidate sites (from the sweep):
- splat head (`lookAhead "$@" >>. failFatally`, commandSegment);
- splat mid-word / bare `$@` (`notMidWord`, the `previousChar…`
  guard) ×2;
- reserved-word in binder/name position (`notKeyword` via
  `identSpanned`) — GATED, see crux;
- neg-int-out-of-range — the COUPLED case: anchoring at the `-`
  merges the unary-minus operator's expecting-list. It cleans ONLY
  IF the message-domination mechanism lets the literal-fatal win the
  `-` spot over the operator. Fold it in here or confirm it stays a
  finding.

## Ground rules

- Message TEXT does not change — the teachings are the product. This
  session SURFACES the existing message; it does not reword it. If a
  site's clean surfacing would require new wording, STOP and report.
- Every site is pinned with BOTH the exact line:col AND the clean
  message (a `not-contains "Expecting:"` assertion — the burial is
  the bug, so its absence is the pin).
- No caret regresses (the sweep's positions are pinned; keep them).
- The strict-spans deep run stays part of the acceptance.
- Any site whose fatal cannot be made to dominate without breaking a
  fall-through is a FINDING with the mechanism, not a forced fix.

## Work items

1. Diagnose each candidate: fall-through? clean after
   consume-then-anchor? — the table (site / gated? / clean? /
   verdict).
2. Splat family: consume-then-anchor via `failFatallyAt`; pin
   caret + clean message.
3. Reserved-word in name position: GATE the domination to
   binder/param/field contexts; prove `let x = if …` and every
   keyword-fall-through still parses (the risk pins).
4. neg-int: fold in if the mechanism cleans the `-` spot; else keep
   as a stated finding.
5. Strict-spans deep run; every moved/clarified pin named.
6. DECISIONS row (extends [D:anchor-before-read] or a sibling key);
   NOTES: retire finding-class (b) with its table; note any residue.

## Done when

Every message-domination site surfaces its teaching CLEANLY (no
expecting-list) at its already-correct caret; the reserved-word
domination is gated and every keyword fall-through still parses; the
splat family is clean; neg-int is either folded in or a stated
finding; message text is unchanged; strict spans green; the sweep's
finding-class (b) is retired in NOTES.

## Not in scope

- finding-class (c), the multi-external/reifier `foldChain` drift
  (needs per-segment positions threaded through foldChain — a
  restructure, its own session);
- the Session D field-type MISMATCH sibling (row provenance, a
  different subsystem);
- any caret already CLEAN in the sweep (`;`/`>`/`>>`, cmd-not-found,
  depth-guard).
