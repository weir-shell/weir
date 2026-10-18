# PLAN — hunt 3 round 4 (invariant-driven)

Spec: docs/reference/concurrency.md (the invariants I1-I15 and its Discrepancies #1-#12 are the stage-0 work list).
Approach (user 2026-10-17): patch, redesign on evidence. Stage 0 = repro each discrepancy, fix plain bugs against the invariants, apply rulings; then black-box round 4 + nightly signal-stress job. Consolidate into one cancellation-scope tree only if round 4 finds highs in this core again.

Plain bugs (fix): #1 pfirst under teardown returns without joining losers (I4); #2 pfirst/serve inside an always cleanup cut nothing (I2/I4); #3 watchdog fires during serve drain (I6); #4 registerForeground: no double-^C escape, 1s grace exits during a pure-compute join, skips settleKills (I8/I12); #5 EPIPE kills no child until exit, foreground waits not teardown boundaries (I3); #9 an abandoned parked arm's always runs after its scopes released (I6); #11 serve handler records a REPL Interrupted as a 500 (I13).

Rulings (user, all as recommended):
- #6 the terminal-^C rule follows LIVE scopes (reset when scopes close).
- #7 fix the docs: an inherited SIG_IGN with a tty stays ignored; say send SIGTERM/SIGHUP.
- #8 qualify the docs: "on the script's own thread" is scripts on POSIX; REPL and Windows run cleanups on the signal thread, still whole/once/LIFO.
- #10 exit n in a pmap/piter arm stops the fan-out like pfirst (no new arm, in-flight cut + joined, cleanups whole), exits n, never masked.
- #12 a signal still drains serve (≤2s, then cut, cleanups joined); correct the serve-drain row.

Stage 0 status (2026-10-18): done. Fixed #1 #2 #3 #4 #5 #9 #10 #11 (plus #5's capture-reader "errors occurred" line); rulings #6 #7 #8 #10 #12 applied in code and docs; docs/reference/concurrency.md Discrepancies 1-12 all resolved. Gate: tools/hunt3r4-repro.weir 9/9 with its controls, rides linux-hunts and local.sh. Residual (concurrency.md): an abandoned arm's cleanup runs while its body is still parked. A Windows e2e failure (an exit-stopped fan-out waiting on a read the kill missed) is fixed with the black-box round's C21/C22: the exit's join abandons arms parked in uncut reads and runs their cleanups first, a sibling's Proc.wait is cut, a nested fan-out takes no new arm once its enclosing race/exit/serve stopped ([D:fanout-exit-reads]).

Black-box round 4 (2026-10-18): done — 7 high, 26 medium (A clean on all OSes). C21 and C22 fixed on this branch; the rest owed in PLAN-hunt3r4-fixes. The core-redesign trigger is met (C21, C22, C1-1 a second signal sweeps cleanups on the signal thread concurrently with a running one, C1-5 an abandoned arm's late cleanup on its own thread): the core becomes one cancellation-scope tree (ruling R6).

Rulings (user 2026-10-18, all as recommended):
- R1 (E5) runtime errors escape the data they quote, as the echo does; weir's own diagnostic styling keeps its SGR.
- R2 (C25) `exit n` in a serve handler stops the scope (no new request, handlers cut and joined, cleanups whole), then exits n.
- R3 (F5) a cut Self.prompt never splices: no reader sees a line that wasn't written.
- R4 (B2) a plan's captured op checks its preconditions against the plan's simulated state and raises at capture.
- R5 (B9) from xml decodes per the declared encoding; an unknown one refuses.
- R6 one cancellation-scope tree: every flow, wait and arm takes its cut from its parent scope; one owner runs cleanups LIFO; a second signal hard-exits with no concurrent sweep.
- R7 (C1-4) after the unwind weir dies by the signal (INT/TERM/HUP), scoped or not.
- R8 (C1-2) commands spawned inside an `always` run in their own process group: a terminal ^C cannot reach them.
Severity: C22 high; F3 medium; B8, B11 low.
Next: PLAN-hunt3r4-fixes — harness first, then stage A (the scope tree) beside the safety and formats stages; the nightly signal-stress job from the round's storm driver. Round 4 is not done.
