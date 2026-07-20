# weir — command-mode sigils: !(...) and $(...)

Status: BLESSED-ON-PASTE (2026-07-20), executed same day — completion
notes at bottom. Full blessed text in the conversation record. Digest:
$(chain) captures the chain's value; !(chain) = (chain) |> print
(eager, unit, raises). Interior grammar IDENTICAL to statement-level
command chains (no second dialect); desugar-only (zero new AST nodes,
zero checker surface — stop-and-report if violated); heads resolve at
check time; nesting via splices unrestricted (depth-2 pin); both forms
ship together (bash-prior alignment of $() recorded as a helping
prior; !() divergence row + must-fail skill block); eager-unit binding
anti-idiom replaced by bare if-blocks; forward archaeology on
greedy-`;`; composition pins MANDATORY (sigils × assembler, ×
greedy-;, × splices, × |complete, × strict). jira-branch final form is
the done-when. Parked: spliced heads, capture-with-exit sigil,
deferred thunks.

## Completion notes (2026-07-20)

Executed as blessed; zero checker surface CONFIRMED (both sigils are
parser desugars). In-session decisions: bare-command let-RHS stays
legal (plan recommendation accepted); interior `| complete` composes
(uniform grammar — the marker lookahead learned the sigil closer).
Composition battery green incl. effect-counted branch pins on the AOT
binary. jira-branch final form is the flagship; spelling tax = 2 chars.

## Sequel note: PLAN-command-district executed 2026-07-20 (see NOTES —
budget amendment human-reviewed at 2x with the metric lesson recorded;
keep conditions discharged by the battery incl. two mechanism pins).
