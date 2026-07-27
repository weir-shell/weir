# weir — the dogfooding diagnostics arc: ten findings, five sessions

Status: BLESSED (user 2026-07-26, six notes folded below). Origin: one evening of the author
using weir in VS Code (bicep-deploy.weir) — ten findings, five real
classes, every one reproduced in-container before this draft.
Value-ordered; each session independently committable.

## Session A — the teaching-and-spans riders (small, bundled)

STATUS: EXECUTED (2026-07-26; all four, pinned at every level).

1. **Typed-boundary near-miss teaching** [#1]: `Args.load C md`
   (a space inside the type name makes TWO arguments) falls through
   the bespoke arm to "module Args has no member 'load'" — a lie:
   `load` is an arm, not a member, so the generic module lookup
   disowns it. A fallback arm for `Args.load`/`Env.load` with any
   other argument shape teaches "takes ONE record/union type name".
   BLESS NOTE: the fallback is exactly-one-type-name and everything
   else teaches — zero arguments and `Args.load Cli extra` included.
   Pins: the two-arg repro, zero-arg, extra-arg + the Env.load twin.
2. **Hover on inner-let binders** [#2]: `let e = targetEnv t` inside
   a function hovers as `unit` — binder names are not expression
   nodes, so nodeAt finds the enclosing let-expression, whose type
   is the BODY's type (unit at the end of an effect function). Fix:
   when the cursor sits on an inner binder NAME (textual locate —
   the definitionFor machinery already does this), show the RHS
   type. Pin: hover `e` = `seq<EnvVar>` in the bicep shape.
3. **`from json T` definition** [#8]: the type name in the adapter
   position doesn't jump. definitionFor gains the from-json typed
   node → `typeSite T`. Pin included.
4. **Full-word squiggles** [#9]: cmd-not-found warnings carry no
   EndCol → a one-character squiggle. Set the span to the head
   word's extent. Pin: the diag's EndCol in `check --json`.

## Session B — the visibility pair (the confusing-error killers)

STATUS: EXECUTED (2026-07-27; the hole pick sharpened — see NOTES).

5. **Command-RHS heads warn** [#4]: `let e = targ etEnv t` — the
   let-RHS command grammar claims `targ` as a command head, but only
   STATEMENT-level heads get cmd-not-found warnings; the user's
   first signal was a type error on the NEXT line ("expected EnvVar,
   got string" at the `!e`). Extend the warning walk to TECmd nodes
   anywhere in the typed tree.
6. **Failed-let cascade suppression** [#6]: a statement that errors
   binds nothing, so every downstream use reports "unbound
   'deploy'. Did you mean 'Deploy'?" — wrong twice (it IS declared,
   and the did-you-mean points at the union case). On a failed
   `let NAME`, bind NAME to a hole scheme (∀a. a) for the REST of
   the check pass: one real error, zero echoes. One-pipeline change;
   pins on the check path; the run path is unaffected (runs stop on
   errors). BLESS NOTE — the subtlety named: a hole scheme unifies
   with ANYTHING, so downstream statements that would legitimately
   fail against the real type now typecheck silently — the honest
   framing is SUPPRESSION WITH DEFERRAL, not repair (fix error #1,
   meet error #7 next run — still beats N echoes). The alternative:
   a POISON type that unifies with anything AND suppresses downstream
   errors mentioning the poisoned name, so later genuine mismatches
   don't mislead either. Pick deliberately, state the pick and its
   trade in the report.

## Session C — the park opens: binder spans [#7, user-demanded]

STATUS: EXECUTED (2026-07-27).

The binder-span park's reopen criterion — "a real user demands
definition/rename/references on locals" — has FIRED (2026-07-26,
the author, in the first hour of VS Code dogfooding). Check records
binder spans (params, block/inner lets, match payload binders);
definitionFor's conservative nulls become jumps. This is the medium
session the park priced; it also unlocks rename and references
later — do NOT build those here. The spans are the session;
definition-on-locals is the acceptance. BLESS NOTE: update the
park's own ledger entry recording the trigger FIRING as written —
user-demanded, first hour of dogfooding; the criterion working
exactly as designed.

## Session D — row-constraint provenance [#5, highest locality value]

STATUS: EXECUTED (2026-07-27). The bless hypothesis was HALF-right:
spans exist and within-statement reporting was already origin-exact —
the 62/107 case is CROSS-statement, where the row escapes via
generalization (schemes carried no spans) and instantiate re-stamps
fields with the call-site span. Not a rider: schemes now carry
PHYSICAL origins (logical spans cannot cross the statement boundary),
recorded at generalization via a Script-set translator, rehydrated at
instantiation, and the no-field discharge error POSITIONS at the
access with the meet as the message note. Scope held to no-field;
the field-type MISMATCH sibling still reports at the meet (flagged
in NOTES). See [D:row-provenance].

`t.BicepPath2` written at 62:32 errors at 107:15 — the row
constraint from the field access carries no provenance, so the
error surfaces where the row MEETS the nominal record (the call
site), 45 lines away, and nothing points back. Fix: row fields
carry the SPAN of the access that introduced them; the
row-vs-record unification failure reports at the field's origin,
with the meet site as a note. BLESS NOTE — diagnose first: row
fields may ALREADY carry a span (ctx.Rows : Map<string, Map<string,
Ty * Span>>) — if so this is a REPORTING choice (dischargeRow/
mergeRows reporting at the meet's span where they should use the
field's fspan), not a representation gap, and D shrinks from
careful-session to rider. Check before planning surgery. The pins
either way: the bicep 62/107 shape reduced, plus zero movement
anywhere else.

## Session E — the backtrack-to-EOF dump [#10, diagnosis-first]

STATUS: DIAGNOSED then FIXED by the blessed successor
PLAN-sibling-sentinel (Option B), EXECUTED 2026-07-27 — see
[D:sibling-sentinel]. Root cause found
and it is NOT the typo: `head er` alone parses (both become command
tokens). The trigger is a bare EXTERNAL command as the FIRST sibling
statement of a multi-line body followed by an inner `let…in` —
minimal repro `let f t =` / `git status` / `let e = "x"` / `print e`.
The assembler joins to `let f t = git status ; let e = "x" in print
e`; topLet (Parser.fs:1531) tries command mode first
(`cmdLineLetRhs <|> seqExpr`), `;` is a cmdWordChar (Parser.fs:1028)
so command mode swallows the sibling `;` and the inner `let e = "x"`
as barewords, stops at bareword `in`, succeeds mid-statement; the
leftover `in print e` fails `.>> eof`, the attempt rolls back, and
the expression fallback runs to EOF — hence the useless position and
the raw expecting-list (cleanParseDump touches neither Note: nor
Expecting:). BOTH plan smells trace to this one over-consumption.
MERGE ANSWER (bless note): E does NOT merge with the parked bare-pipe
narrow question — that is a `|`-fatal POSITION law; E is a
command-mode `;`-boundary MIS-PARSE. They share only the
furthest-reached family and the symptom shape; disjoint fixes; the
park stays open. STOP per the plan: the fix wants a commit-point /
grammar boundary decision (three options, in NOTES) — grammar
surgery, not diagnostics polish. Awaiting the direction bless before
any code.

A space at 78:8 (`head er` inside the ten-line `azureLogin`
statement) reports at 86:73 as "Note: The error occurred at the end
of the input stream" plus a raw FParsec expecting-list. Two smells,
possibly two fixes:
- the POSITION: the parser consumed far past the mistake and failed
  at the joined logical line's EOF — the translate-through-segments
  machinery faithfully reports a position that is technically
  correct and diagnostically useless;
- the DUMP: the expecting-list escaped the clean-parse-dump
  discipline [D:clean-parse-dump].
Diagnosis before design — this is the arm-commit/seq-commit family
(commit points), and it may merge with the parked bare-pipe narrow
question (the same "where should a fatal point in a multi-line
statement" law). STOP-and-report if the fix wants new commit
points; those are grammar surgery, not diagnostics polish.
BLESS NOTE: the diagnosis must explicitly answer whether E and the
parked bare-pipe narrow question MERGE (same law: where should a
fatal point when a statement spans lines) — if yes, one session
closes both and the park dies; if no, the report says why they
differ.

## Not in the arc

- **`//!` comments render red** [#3]: NOT weir — proven with the
  real TextMate engine (vscode-textmate + oniguruma: every `//!`
  variant scopes as comment.line.double-slash.weir) and over the
  LSP protocol (zero semantic tokens on comment lines). It is the
  Better Comments extension's `//!`-alert convention (or a theme
  borrowing it) on the user's machine. Documented here, closed.

## Order and rules

**A → B → C → D → E.** A and B are the quick confusion-killers; C is
the demanded feature; D the biggest single locality win; E cannot be
sized honestly before its diagnosis. Standing rules apply: zero
behavior change outside the named fix, no message text changes
except the ones that ARE the fix (each named), every fix pinned,
zero pin movement elsewhere, findings-not-fixes for anything that
turns out non-mechanical.
