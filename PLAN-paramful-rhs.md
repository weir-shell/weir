# weir — param-ful command RHS: `let f r = git rev-parse $r | Seq.head`

Status: LANDED 2026-07-23 (blessed same day). Origin: the git-subrepo
flagship's wrappers + the friction log's most common reflex-error.
The first feature enabled by a bug fix: splice-default-last's
boundary-defaulting architecture WAS the load-bearing wall.

Completion addenda:
- Work item 1 ran exactly as ordered: the guard-dropped prototype
  demonstrated the hazard live (`let f x = x` printed SPAWNED with an
  executable x on PATH) BEFORE the resolver fix; the pin was red,
  then green. Params shadow PATH via leaf-name extension of the
  per-statement resolver (tuple-pattern leaves included, pinned).
- The soundness note restated (third edition) — the stop clause did
  not fire: the old no-command-under-lambda premise retires; the
  boundary-defaulting argument carries alone.
- Zero checker arms added (the splice queue reused verbatim);
  no TRANSCRIPTION addendum needed beyond the SEMANTICS note —
  reported per the expected-none clause.
- The retired rule's own pin flipped with archaeology in its name
  (the one deliberate pin edit, plan-sanctioned).
- Ledger row check: no bare-RHS divergence row EXISTED to widen
  (command mode is bash-prior/n-a class) — reported; SEMANTICS
  carries the scope statement instead.
- Flagship rewrite surfaced one wild finding: mid-word splices
  (`--file=$file`) pass LITERALLY (the whole-argv law) — caught by
  the live smoke, taught in SKILL, friction-logged.
- Advisor pins both green first try: sigil equivalence
  (bare == `$()` behaviorally) and the splice-typo did-you-mean
  (`$path` vs param `pth` — env.Values carries params at body-check).

[Blessed text: see the plan message. All DECIDED items held.]

## Parked (unchanged)

- Block-let command RHS inside bodies — receipts via the friction log.
- Command mode in `let ... in` single-line form — the in-swallow seam.
