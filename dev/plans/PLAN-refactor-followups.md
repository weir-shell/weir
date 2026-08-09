# weir — the refactoring analysis' follow-ups, ordered

Status: BLESSED (user 2026-08-01); ITEM 1 EXECUTED same day (branch
resolver-invariant). Items 2–4 are RIDERS recorded here for their
host sessions; item 5 waits on recorded triggers.

## 1 · EXECUTED — check-agrees-with-run, WITH the yaml production

Both halves landed. The property (Main.fs, invariant 6): every
generated program's logical lines parse to the same sexpr and check
to the same verdict under the assume-resolver and the hard resolver —
the five-incident seam, finally asserted. The yaml production:
top-level districts (1–3 literal keys, int/word/splice values,
optional nested map, trailing `to yaml |> print` so output identity
sees them), 44/120 sampled programs carry one; marker and content
lines are non-error territory for the span invariants (marker junk
re-reads as command argv under assume — the agreement property's
quarry, deliberately).

THE REDS WERE THE DELIVERABLE, twice over — but from the PRODUCTION,
not the property: the transform battery caught a real product bug in
the district template parser's comment transparency, in two faces.
(1) `firstContentRel` derived a nested block's indent from a
SHALLOW `// comment` between a key and its content; (2) the extent
scan STOPPED at a comment sitting at the unit's own indent,
orphaning the nested block. Root cause: comment transparency was
taught to the units loop and hasNested at the midline-# session but
lived as three (then four) inline copies — now ONE predicate
(`Parser.tplTransparent`) consumed by all four structure loops, both
faces unit-pinned, both failing seeds (994247893, 589527467) green,
five fresh 1200-case seeds clean total. The agreement property
itself found no live divergence — the five historical incidents are
fixed in today's tree; the property now guards the seam against the
sixth.

Timing: deep 1200-case runs ~65s/seed (the noted ~20% lengthening),
recorded in GRAMMAR.md. The CAN/CANNOT diff was run (the audit's
meta-lesson): the production joined CAN, the coverage-gap row
narrowed to the shapes still outside (for-entries, key splices,
block scalars, schema=), no row appears in both lists.

## 2 · RIDER on the modules arc's next session — extract Modules.fs

~500 lines of module loading (Script.fs:2183-2500+:
resolveImportPath, loadModuleCached, module envs) move to
Modules.fs. Zero pin movement expected — the diff is cut-paste of
private functions plus one open; a mover is a finding. The host
session's report states that it rode.

## 3 · RIDER on the next schema session — F-A via the parameter

Types.didYouMean gains a SEPARATOR parameter (existing callers keep
today's default); Contracts drops its private levenshtein/didYouMean
and passes its separator — NO schema pin moves (they were re-pinned
in schema-polish; moving them twice in two sessions is avoidable).
Fallback if the parameter reads badly: the report's direction, six
pins move — decide in-session, state which.

## 4 · RIDER on F-A's session — F-B, scalar self-typing

The plain-scalar typing rule (3→int, true→bool, ""→null) extracts to
Yaml.fs; Eval.evalYamlTpl:~1490 and Contracts.literalKind:~511 both
consume. Zero pin movement claimable: both sites pinned, the rule is
supposed to be identical — if it is not, the extraction proves it
(value either way). PROCESS gains the lesson line (new-module
authors reach for local helpers before grepping; re-run the dedupe
token-hash after any session that ADDS A MODULE).

## 5 · WAIT — recorded triggers

- Program.fs command plumbing: trigger = the THIRD contracts command
  (same trigger as restore-rename's noun-scoping note; they land
  together).
- colorizeRepl → Repl: no trigger; ride any session already in both
  files.

## NOT scheduled (recorded so it stays closed)

- Splitting the assembler — the state sharing IS the machine; item 1
  is what reduces its risk. Alignment-stack trigger unfired.
- Hover.fs — falsified by the +4-line six-week delta; re-ask when
  signatures/schemas add hover data. Precedent: a proposed
  extraction gets a DELTA CHECK before it gets a session.
- Merging the boundary-loader walkers — each walker IS its law;
  trigger: a FIFTH loader.
- Any BCL swap — closed by the analysis' table (exact AOT-clean APIs
  in use; owned parsers carry measured decisions).
