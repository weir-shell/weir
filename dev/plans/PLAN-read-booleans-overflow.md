# weir — consolidated read, booleans, overflow policy, data-range battery

Status: EXECUTED (landed 2026-07-17).

(Advisor plan, received 2026-07-17. Part 1 prep executed by Claude Code;
the read itself is human. Parts 2-3 await bless + the READ.md gate.
Full text as received — see conversation record; operative structure:)

- Part 1: consolidated read. Prep = TRANSCRIPTION.md (judgment form per
  checker arm, file:line anchors, two-jobs flags), READ-ORDER.md
  (a-g audited path), composition probes as tests. Gate: Part 2 does not
  start until READ.md exists (human verdict file).
- Part 2: boolean branching — if/then/else (else mandatory) + bool
  patterns + when-guards (decide in-session). Checker session; tripwires;
  battery incl. row-merge across branches; fix shadowed-cd hint while
  the diagnostic file is open.
- Part 3: overflow policy — checked int64 arithmetic raises (joins the
  named runtime-failure class); data-range battery (int64 boundaries,
  >2GB sparse file, 0-byte, long-seq laziness, megabyte strings) as a
  permanent test layer; timing pins re-verified.
- Parked: parse-error column mapping, REPL continuation, wrapping
  intrinsics. Resolved during prep: &&/|| short-circuit pins exist
  (operator session, div-by-zero proxy; spawn-count variant added with
  the composition probes).

## Completion note — Part 2 (2026-07-18)

Executed on branch bool-branching with the READ.md gate waived by the
gate owner (recorded in NOTES). In-session decisions: when-guards
SHIPPED (the blessed |-inertness fix plan already used one in its
canonical example); `elif` parked (`else if` chains suffice); EIf is a
dedicated checker arm for error quality (TRANSCRIPTION addenda).
Riders done: shadowed-cd hint fixed; warnings surfacing in -e and the
script runner fixed (found in-session — they were silently dropped
everywhere but the REPL). Part 3 (overflow policy, data-range battery)
remains open, still sequenced after READ.md by default.

## Completion note — Part 3 (2026-07-18)

Executed on branch overflow-policy with the gate waived by the gate
owner (same waiver pattern as Part 2; READ.md remains the human's).
int64 literals landed as part of the policy (literals were silently
int32 while runtime was int64 — the data-range battery's first catch,
found as an AOT crash on `weir -e 9223372036854775807`). Checked
+,-,*,/ and sum; ranges terminate at the boundary; DataRange.fs is the
permanent layer; e2e pins >2GB/0-byte/Max-literal/overflow-raise on
the AOT binary. Timing pins re-verified. THE PLAN IS COMPLETE except
Part 1's READ.md, which has no code component.
