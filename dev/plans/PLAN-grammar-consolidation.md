# weir — grammar consolidation: the grouping seam pays its debts

Status: EXECUTED (landed 2026-07-20) — as blessed: BLESSED (2026-07-20). Origin: the bicep-script translation.
Executed on branch grammar-consolidation (stacked on fn-body-seq).
Blessed decisions as received (see conversation record for full
text): greedy-`;` DESIGN REVIEW in-session, two candidates
(keep-greedy-fix-surface / revert-to-lowest), stop-and-report if
neither closes cleanly; fmt if/else roundtrip fixed AFTER the decided
grammar, refuse-on-mismatch retained; multi-line record continuations
ship (records ONLY, sibling rule inert inside braces, blank-in-brace
errors naming the brace); Exit.code imitating fail's mechanism
(deferral regime if checker-touching); composition pins per the
standing rule; origin-script shapes verbatim as e2e. Parked: general
bracket continuations; use!-cleanup as GUIDE idiom (complete-ization);
shEnv/child-env gets its OWN plan.

## Completion notes (2026-07-20)

All six work items DONE, one branch (grammar-consolidation, stacked on
fn-body-seq). Item 1 decided (a) after the written comparison — but
the principled form, not the plan's sketch: the OFFSIDE CLOSE (an
open if/match-headed piece paren-wraps when a sibling arrives at its
head indent or shallower; else/| extend). Review discovery upgraded
the receipt: the bite class had a SILENT member (same-level sibling
swallowed into then — conditional execution the user never wrote) and
same-indent `else` was a parse error (the fmt refusal's root).
Candidate (b) died on layer separation: lowest-`;` needs parens
INSIDE pieces at then/else/-> positions — grammar-interior surgery.
Divergence row AMENDED (single-line-typed `;` only), not retired; the
bare-sibling question dissolved (no grammar change, so no district
exclusivity needed). Item 2: fmt's root cause was its let-only depth
model flattening if/match bodies — replaced with the general
indent-level stack (preserves every relational comparison the
assembler makes); repro + nested variant pass; safety check retained.
Item 3: record continuations per the pre-made (string-aware brace
counter; both spellings; col-0 close; blank/EOF errors naming the
brace); NEW divergence record-fields-ignore-indent (F# offside
rejects col-0 fields, weir braces are indentation-blind) — oracle
caught it live at pin time. Item 4: Exit.code needed NO deferral tax
(fail is a plain builtin, not a checker form); silent-exit carrier
exception; runner/-e catch sites. Items 5-6: 19 unit pins + 5 oracle
pins + 10 e2e (origin shapes verbatim); two old text pins amended
with archaeology (offside parens at let-close); GUIDE gained the
cleanup idiom (complete-ization) same session; timing holds 7/23ms.
500 unit + 43 oracle + 102 e2e green.
