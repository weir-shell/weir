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
