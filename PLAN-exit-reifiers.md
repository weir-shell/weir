# weir — exit-code reifiers: `| succeeds` and `| orFail "msg"`

Status: LANDED 2026-07-23 (blessed same day). Origin: flagship review;
the bash priors (`if cmd; then`, `cmd || error "msg"`) mapped onto the
reifier seam `| complete` proved.

Completion addenda:
- The multi-segment check ran FIRST as ordered: complete hard-errors
  with a named message; the three reifiers now share ONE generalized
  fold arm and the message differs only in the stage name (pinned).
- Verified-not-assumed corrections to the plan's own text: complete
  ALLOWS downstream stages (types gate them — a bool into Seq.head is
  a type error, pinned); "nothing follows" was folklore, the real rule
  inherited.
- The flagged !( ) cell decided: unit became printable-as-NOTHING —
  one rule (printArgTy + printImpl) instead of a shadow drain builtin
  twinning print's typing. Consequence pinned explicitly: `print ()`
  is now silent (was an error); the seq<unit> rejection stands. This
  unlocked orFail in !( ) AND districts (both hit the same wall —
  the district desugar routes through the same print wrap).
- Two runner seams closed en route: printResult skips VUnit (orFail
  statements are silent on success, not "()"-printing), and
  bool-valued command statements join the discard family (bare
  `cmd | succeeds` is a check error with a bind-or-condition hint;
  record-valued complete statements keep their standing echo).
- orFail's raise carries the code (`msg (exit N)`) and replays the
  child's captured stderr; succeeds captures-and-discards (a
  predicate is silent) — both on Proc.completeWith, the one spawn
  path, with env twins for sigil/district env threading.
- grep-no-match through succeeds is FALSE, pinned in a doc-tested
  SKILL block beside the | complete spelling (the exit-zero sentence).

[Blessed text: see the plan message. All DECIDED items held; the two
amendments above are the verify-clause corrections it invited.]

## Parked (unchanged)

- Expression-side twins — computed-prog-name receipts.
- `| exitCode` int reifier — complete carries it; no receipt.
