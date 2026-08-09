# weir — record update: `{ r with F = v }`

Status: EXECUTED (landed 2026-07-22) — as blessed: LANDED 2026-07-22 (proposed same day). Origin: the re-mine's
headline find — no-record-update, the first corpus find that is a
MISSING FEATURE rather than a named boundary. The row retires.

Completion addenda (2026-07-22):
- All FCS probes ran FIRST; verdicts: general-expression sources
  accepted UNPARENTHESIZED (application included); nested I.X is
  accepted (F# 8 in the oracle's FCS); add-fields rejects; bare
  match source rejects (parens required) — both guesses held.
- BONUS FCS fact from a probe-naming accident: with a TYPE named
  like the path head (`type I` + `{ o with I.X = v }`), F#'s name
  resolution captures the type and REJECTS; weir's paths are
  field-only and accept — named divergence update-path-plain,
  pinned Diverges (designed, not gold).
- The stop-and-report clause FIRED as written: a parser desugar of
  nested paths duplicates the source expression (double evaluation
  of effectful sources). Resolution taken in-session: paths live in
  the AST, the checker walks them, eval binds the source ONCE —
  zero grammar surface beyond the plan's, one checker arm as
  budgeted, and the eval-once decision holds by construction.
- One assembler ride-along: the brace-continuation sibling rule
  needed a `with`-header case (the first field after `{ r with` is
  not a field sibling) — [D:record-update] at the join site, pinned
  by the multi-line e2e.
- The poster pin is green: a row-typed updater generalizes and the
  result IS the source's row variable (tripwired on formatted
  domain == codomain).

[Blessed text: forms, pre-made decisions (probes-first; nominal AND
row typing with identity results; nested as desugar with
stop-and-report; bounded parse backtrack; eval-once; full ceremony;
corpus snippets as e2e) — see the plan message; decisions held
except as amended above.]

## Parked (unchanged)

- Update on tuples — no (F# agrees).
- Anonymous-record update — with anonymous records, if ever.
- Deep paths beyond what FCS accepts — the desugar-equivalent walk
  recurses naturally; nothing bounded out in-session.
