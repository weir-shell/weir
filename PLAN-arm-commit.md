# weir — mini-plan: arm-commit (the consumed-separator law, unified)

Status: BLESSED (user 2026-07-24). One small session, one parser
site (the arms loop), zero assembler surface. Origin: the fuzzer's
second invariant-3 span find.

(Blessed plan text as delivered; the executed decisions below.)

## Session report (2026-07-24)

One line: the arms loop's `attempt (str_ws "|" >>. matchArm)` lost
its attempt — a consumed `|` commits to its arm [D:arm-commit]. The
pinned repro reports at 6:23, ON the junk, with no bare-pipe hint;
FCS run as the referee reports the NEXT arm's line (7) for the same
junk — weir beats its oracle on placement (recorded in NOTES; the
oracle compares verdicts, not positions, so no new machinery).

The probe set, executed:
(a) CORRECTED PREMISE: a bare `|` in an arm RHS is an ARM SEPARATOR
    (F# reads it identically) — never absorbed by chain consumption;
    command chains in arms ride `$()`. Both spellings pinned.
(b) Or-patterns: weir REJECTS (located at the second bar), F#
    accepts — the or-patterns divergence row filed with no receipt
    and a reopen condition; fidelity Diverges pin added. Either
    answer produced its artifact, as designed.
(c) Guards pass under commit (pinned).
(d) Regex arms pass (the standing battery re-ran green).
(e) A reserved-word arm head errors located AT the word (pinned as
    found — a pattern-position failure, not a commit casualty).
(f) The REPL one-line grammar commits identically (pinned at the
    junk's column).
Soundness coupling: the premise ("a `|` after a completed arm at the
same paren depth can only be another arm") is pinned onto the
nested-match offside-close assemble pin it rides ([D:arm-commit]
cross-ref on the pin).

barePipeHint's original customers re-ran byte-identical (the
statement/let-RHS pins, in-suite). The two span pins flipped to
TRUE-SITE assertions in the designed direction. GRADUATION:
WEIR_FUZZ_STRICT_SPANS defaults ON — the strict positional
assertion is a standing guarantee in the CI smoke; the strict deep
run is the done-when's evidence. The law is stated ONCE in
SEMANTICS covering both instances; seq-commit's DECISIONS row
gained the cross-reference.

ADDENDUM, same session: the first strict deep run (the graduation's
own evidence run) found the law's THIRD instance — record-literal
deep-field junk rewound by the literal's whole-attempt into the
update alternative's dump, reported a line early with "expecting
'with'". Fixed in-session with the same move: the literal commits on
its `ident =` head (`==`-guarded — an update source can start that
way); pinned; updates unaffected; trailing `;}` was already a
reject. Strict deep runs green on both the finding seed and a fresh
one.
