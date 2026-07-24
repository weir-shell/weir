# weir — `[<Default>]`: the resting point moves

Status: BLESSED (proposal 2026-07-24; advisor additions + user
bless same day). One session + a gated follow-up (Env.load).
Origin: fuzz.weir's `count` — the `Option<int>` + match-to-fill
dance for a value that is not optional-in-meaning, just defaulted.
The attribute machinery is already shaped for it (literal args,
the closed registry with did-you-mean, check-time-erased).

The law, one sentence: **`Default` moves the resting point** — and
for bool, the twin flag is how you leave it.

PARK CLOSED, named (advisor addition): the attributes session's
field-defaults door entry predicted this — "field defaults...
unpressured now that attributes cover the metadata cases" — and
this session is that prediction CASHING. The LANGUAGE door (record
field defaults in type declarations) stays shut; its two named
customers (Args now, Env follow-up) are served by the attribute at
zero grammar cost. The DECISIONS row cross-references the door
entry; the forward-archaeology gets its grading line.

## Pre-made decisions

- DECIDED — **`[<Default v>]` on `string`/`int` fields**: the
  field stays non-Option; an absent flag fills the literal;
  `--help` shows `default: v` in place of `required`. The
  missing-required set shrinks by exactly these fields.
- DECIDED — **`[<Default true>]` on `bool` mints the `--no-X`
  twin** (kebab `--no-<flag>`): resting point true, `--no-x` sets
  false, the positive form stays legal-and-idempotent
  (explicitness allowed; pinned). `--no-*` never derives a short
  and never contests a letter (ruled at bless: minted names join
  COLLISION checks but not SHORT derivation — the contested-letter
  surface is already the design's subtlest machinery, and --no-*
  flags are typed rarely enough that shorts buy nothing). Help:
  `default: on — --no-x disables`. Both forms in one invocation is
  the given-twice family error, naming both spellings.
- DECIDED — **Rejection cells, each with teaching text**:
  `Default false` on bool (redundant — presence already rests at
  false); `Default` on `Option<...>` (contradictory — "optional
  with a default IS a default; drop the Option or the attribute");
  literal type ≠ field type (`Default "x"` on int); `Default` on
  the union subcommand slot (no flag derives there — the
  shared-flags law). All check-time, at the Args.load consumption
  site per the standing pattern.
- DECIDED — **Minted flags join the collision namespace, both
  routes**: a shared-tier `[<Default true>] color: bool` mints
  `--no-color`; a payload field `noColor` derives the same kebab —
  the declared-once law sees MINTED names too (within one record
  and cross-tier), rejected at declaration. The one genuinely new
  interaction; pinned in both directions.
- DECIDED — **Minted names join did-you-mean too** (advisor
  addition): a typo'd `--no-quiet` (where quiet has no
  Default-true) candidates against the minted set, tier-aware —
  one line extending the collision decision to the hint machinery;
  one typo pin.
- DECIDED — **Attribute composition, Positional named** (advisor
  addition): `[<Default 5; Positional>]` — Positional's not-yet
  FIRES AND WINS (the not-yet is the stronger claim); one pin so
  the precedence is decided, not incidental. [<Short>]/[<Doc>]
  compose normally (Doc text joins the default note in help;
  pinned on one field carrying all three).
- DECIDED — **Scope this session is Args; Env.load is the gated
  follow-up**: the attribute registers globally (the closed set
  grows by one; did-you-mean covers the NAME everywhere) but is
  consumed this session only by Args.load — on an Env.load record
  it stays legal-and-inert per the standing attribute law until
  the follow-up lands. The follow-up gates on THIS session's
  REPORT per the dependency rule. (Follow-up pre-notes: absent env
  var fills the literal; Option stays the absent-is-data spelling;
  the rejection cells re-derive under Env.load's field law; env
  bools are true/false text — no presence semantics, so
  `Default true` is plain fill, NO twin to mint; the Arquidev Env
  lib's `[<Env.Default>]` is prior art, noted, not authority.)
- DECIDED — **fuzz.weir is the receipt and the e2e — and the
  boundary's living example**: `count` becomes
  `[<Default 10000>] count: int` and its match deletes; `seed`
  STAYS `Option<int>` + `defaultWith` (the computed default —
  spawning `date +%N` only when absent). Both shapes in one Cli,
  said in GUIDE: literal defaults take the attribute; computed
  defaults keep Option.
- Products: × shared-flags tiers (Default on shared AND payload
  fields; two-tier help renders defaults; case-scoped help
  likewise); × the untyped floor (Args.flag/Args.value untouched);
  × unchanged-shape zero-diff (records without Default keep
  byte-identical behavior — the regression bar).
- Ceremony: loader arm (check-time validation + eval fill) ⇒
  battery + tripwires + a TRANSCRIPTION addendum line; no new
  syntax ⇒ POSITIONS n/a; oracle n/a; no assembler surface ⇒ no
  fuzzer obligation (stated for the scope rule); timing.

## Work items

1. Registry + rejection cells (pins first, failing) + the
   Positional-precedence pin.
2. The fill (string/int) + help rendering; missing-required
   shrinkage.
3. The bool twin: minting, parse, both-given error, idempotent
   positive, short non-derivation; the minted-collision cell both
   routes; the minted did-you-mean pin.
4. Shared-flags products; help both tiers (the help-shape change
   pinned in the help e2e, per the bless flag).
5. fuzz.weir receipt rewrite; docs (SKILL bullet; GUIDE: the
   resting-point sentence + the literal-vs-computed boundary with
   the count/seed example); DECISIONS row with the door
   cross-reference; NOTES (the prediction-cashing grading line);
   timing.

**Done when:** fuzz.weir's count match is deleted and its help
shows the default; every rejection cell pinned; `--no-x` sets,
`--color --no-color` errors naming both, bare `--color` is an
idempotent no-op; the minted-collision cell rejects at declaration
both routes; the minted did-you-mean fires; Positional's not-yet
wins its composition; unchanged shapes zero-diff; all green.

## Follow-up session (planned, gated on this one's REPORT)

- **Env.load consumes `[<Default>]`** per the pre-notes above.

## Parked

- **Computed defaults** (`[<DefaultFrom ...>]`-style) — no; the
  boundary IS the design: literals in the attribute, computation
  in `Option` + code. The count/seed pair in fuzz.weir is the
  living example. Re-askable only against this entry.
- **The field-defaults LANGUAGE door** — stays shut, now with its
  customers served; the door entry gains the pointer here.
