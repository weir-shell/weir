# weir — `Seq.force` naming hygiene + seq patterns (design-on-file)

Status: Part 1 BLESSED (user 2026-07-23) — EXECUTED 2026-07-23, see
the addenda. Part 2 DESIGN-ON-FILE (the district/classes precedent —
settled while the ledger honestly shows the trigger unfired). One
plan because the halves share their subject: what materialization
means in a one-sequence-type language, settled by the no-split
decision.

## Does `force` survive the patterns design? (yes — its customers)

Seq patterns (Part 2) memoize their scrutinee internally, which
deletes the match-site materialization customer. Two customers
stand: (1) effects-once under re-enumeration (a command-backed seq
re-runs its process per enumeration — the echo double-force class,
user-side); (2) the timing/Cwd seam (a lazy `ls` enumerated after a
`cd` sees the new directory — force pins the data NOW). SKILL
carries the two-line answer: reuse, or timing.

## Part 1 — the naming session (abridged; full text in the blessing)

- DECIDED — canonical materializer `Seq.force` (shortest honest
  word; Haskell prior; ZERO F# collision — toList made a false
  parity claim, force makes none). Semantics unchanged; strictness
  sentence moves with the name. NOT Seq.cache-shaped (F#'s cache is
  a lazy memoizer — different tool, not built).
- DECIDED — bare `force` takes the bare slot; both old names retire
  with teaching hints; `Seq.collect` RESERVED, not built (flatMap,
  F#-parity, receipts-gated — the reservation is the point: later
  is breaking, now is a grep).
- DECIDED — the Option bundle rides: `defaultTo` →
  `defaultValue` + `defaultWith` (the parity audit's known delta).

### Part 1 completion addenda (2026-07-23)

`force`/`Seq.force` materialize everywhere collect/toList did (the
effects-once and Cwd-snapshot pins renamed and passing); all FOUR
retired spellings teach — `Seq.toList` ("weir has no list type;
Seq.force is the materializer"), bare `toList`, `Seq.collect` ("F#'s
Seq.collect is flatMap — reserved; the materializer is Seq.force"),
bare `collect`, plus `Option.defaultTo` → defaultValue/defaultWith —
via ONE retired-names table consulted at both lookup sites (bare
unbound + module-member miss; the measures-transition precedent).
`Option.defaultWith` landed (thunk runs only on None, pinned). The
bundle WAS taken. Migration swept 9 script/doc files + both test
suites; oracle snippets stay F#-legal (`Seq.toList` in pin text
became `Seq.length` — parity kept, materialization wasn't those
pins' point). SKILL gained the when-do-I-force lines and the Part 2
stranded-pattern must-fail block with taught spellings. 783 unit /
132 oracle / full e2e / 50 doc blocks; timing unchanged.

## Part 2 — seq patterns (DESIGN-ON-FILE, verbatim from the bless)

The seq/list split was the wrong axis (commands yield seq;
stranding streams abandons the data weir exists for). The right
axis is FORCING SEMANTICS AT THE MATCH SITE:

    match $(git status --porcelain) with
    | [] -> print "clean"
    | line :: rest -> ...
    | [a; b] -> ...          // fixed arity

- Statically bounded force: `[a; b]` pulls exactly 3; `x :: rest`
  pulls 1; the bound composes across arms AT CHECK TIME
  (maxArity+1 over the pattern set).
- Memoize-once law: a match with ANY seq pattern materializes the
  pulled prefix ONCE — arms probe the buffer; the double-force
  class is unrepresentable by construction. Infinite streams work.
- Exhaustiveness: `[]` + `x :: rest` complete (F#'s list rule
  transplanted); fixed-arity literals never complete alone.
- Refutability: all seq patterns refutable → banned in binders.
- Nesting: element positions take full patterns.
- Fidelity: F# REJECTS list patterns on seq → a weir-accepts row
  of the param-ful-RHS class (F#'s exact spelling, semantics
  extended to the type weir has); oracle pins record F#'s
  rejection — the row born refereed.
- Ceremony when opened: checker arms ⇒ full deferral tax;
  POSITIONS sweep; products (× exhaustiveness, × guards,
  × classes [patterns bind, never compare — one pin], × Regex
  sibling arms, × the memoize law by PULL-COUNT PIN); the SKILL
  must-fail block retires (flips).
- NOT included ever-until-receipts: `@`-patterns, mid-list rest
  patterns, array patterns.

**Trigger**: the first stranded cons-pattern in the friction/agent
log, OR user call — whichever first.

## Parked

- Seq.collect/flatMap — reserved above; own receipts.
- Seq.cache (lazy memoizer) — different tool; no receipt.
- List type — the no-split decision stands; re-askable only
  against the theorizing entry.
