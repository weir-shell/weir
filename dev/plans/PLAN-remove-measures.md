# weir — remove measures

Status: BLESSED (user decision 2026-07-18, advisor scoping same day).
EXECUTED same day — completion note at the bottom. One session, mostly
deletion. Sequenced BEFORE the read — removal shrinks the read scope
(§4 retires entirely, measure cases vanish from `bind`, the splice rule
simplifies); the read anchor moves to the post-removal commit and
TRANSCRIPTION.md regenerates once, smaller.

(Blessed plan body as received — decision archaeology lives in NOTES.md
"Remove measures — the evidence-standard case study"; pre-made
decisions: complete removal not deprecation; FileRow.Size deleted and
Bytes stays bare; magic-number ergonomics deferred as the named
successor question (multiplier builtins / underscore literals, on
dogfood evidence); transition error UX ships with the removal, retired
at 1.0 grammar freeze; SEMANTICS tombstone; retire-loudly for the
no_unit_algebra tripwire, the gb-vs-mb pin, §4 rows, and the
measured-range pins.)

## Completion note (2026-07-18)

Executed on branch `remove-measures`, all done-when criteria green:
- **THE NEW READ ANCHOR is the squash-merge of this branch into main.**
  Branch-tip hash recorded pre-merge in the session report; the
  content-stable anchors are TRANSCRIPTION.md's file:line references
  against `src/Weir/Check.fs`, which survive the squash unchanged.
- Verified before deleting, per work item 3: `Option<int>` generic
  arguments and measure brackets shared NO parser code path (tySyn's
  "int" arm vs the named-type arm — separate; the plan's model held).
- No measure token survives outside NOTES / the SEMANTICS tombstone /
  the transition recognizer; grep-verified.
- Consequences ledger: README leads with nothing (it is one line), so
  the positioning item was moot; sortBy's runtime scalar-key check is
  again the only qualified-types customer; the range session's
  measured-range pin became a plain-behavior pin.

## Parked

- Multiplier builtins / underscore int literals — the named successor
  question; reopens on dogfood complaint about magic numbers.
- Transition recognizer removal — at 1.0 grammar freeze.
- Quantities-with-conversion (ratios, display formatting) — reopens
  only as an evidenced plan; the NOTES arc is mandatory prior reading.
