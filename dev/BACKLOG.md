# Backlog

(Moved from SEMANTICS.md at its retirement — planning residue never
belonged in a semantics document.)

0. **Block effect-sequencing** (`print "a"` mid-block — F#'s other half of
   light syntax): needs an ESeq node checked `unit` in non-final
   positions, the statement rule's discipline applied inside blocks.
   Revive on dogfood demand; until then a block is bindings + one result
   expression.
1. ~~**Measure algebra**~~ — superseded: **measures were removed
   entirely** (2026-07-18; see the tombstone section and the NOTES arc).
   The 2026-07-17 drop decision and the `no_unit_algebra` tripwire
   retired with them *and* the `*`/`/`-defaulting rule above.
(Done: backlog #1 and the exit-code policy — old #3 — landed as
`Seq.force` (né `collect`) and `complete`; see "Processes and the
session".)

(Done: comparison/boolean completeness — landed with `<>` inheriting `==`'s
equatability and short-circuit `&&`/`||`, as pre-committed.)
