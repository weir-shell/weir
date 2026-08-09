# weir — block sequencing, the Seq access family, Args

Status: BLESSED (proposal 2026-07-20; advisor pass folded in same day).
EXECUTED — completion notes at bottom. Origin: the nu-script
translation (jira-branch); NOTES-agent 2026-07-20 entries are the
receipts. Blessed decisions as received (see conversation record for
full text): Session 1 — Seq.contains with sentinel equatability +
sentinel-ledger NOTES entry (customer three; qualified-types stays
parked, ledger accrues); Seq.exists/forall; Seq.item/tryItem/skip
(+ skip-raises/first-truncates asymmetry sentence in SEMANTICS);
run = the desugar of `cmd prog argv |> print` on the SHARED path
(byte-identity pinned); Args.flag/Args.value with long-only = empty
short form. Session 2 — `e1 ; e2` ESeq (e1 ⇐ unit, hard error);
assembler `;`-insertion for same-indent block siblings under a ~30
line stop-budget; the `;` mode-boundary prior-bleed hint (bash chains
with `;`, weir command-mode `;` is argv) + divergence row + skill
must-fail; precedence-trap oracle pin; no commands in blocks (parked).

## Completion notes (2026-07-20)

Session 1: DONE as blessed (443 tests at close; jira-branch frictions
one call each; run byte-identity pinned; sentinel ledger recorded).
Session 2: DONE with one stop-and-report AMENDMENT — lowest-precedence
`;` made flat-joined if-blocks silently unconditional; shipped greedy
body-scoped `;` instead, with the F#-verbose grouping difference as
named divergence semicolon-greedy-bodies (full analysis in NOTES).
Assembler sibling rule: ~6 lines, within budget. The `;`-boundary
warning, rows, and pins landed as blessed. Multi-line record
separators surfaced as a NEW candidate (telemetry-logged, not taken).
