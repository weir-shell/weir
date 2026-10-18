# Concurrency and cancellation

The contract the runtime keeps when flows run side by side and get
stopped. It is written for whoever changes `Session.fs`, `Builtins.fs`
(the fan-outs), `Eval.fs` (`within`) or `Proc.fs`, and for whoever tests
them: each invariant names the decisions it rests on and the function
that enforces it. The user-facing promises are in
[Scopes](scopes.md) and the SKILL; this page is their mechanism.

## Actors

| flow | runs on | stopped by |
|---|---|---|
| main flow | the script's main thread (`Session.registerMainThread`; scripts and `-e` only, the REPL never registers one) | teardown, REPL ^C |
| fan-out arm (`pmap`/`piter` + `With`) | a LongRunning task per worker (`runParallelWith`, `Session.armRunning`) | teardown, REPL ^C, an enclosing race, an `exit n` in a sibling arm |
| race arm (`pfirst` + `With`) | the same, plus a race token and a `RaceGroup` per arm (`runRaceWith`) | race win, teardown, REPL ^C |
| serve handler | a thread per request with its own flow token and `RaceGroup` (`Eval` `WithinServe` `runHandler`) | scope close past the drain, teardown, REPL ^C |
| `always` cleanup | whatever flow reached it, marked by `Session.runningCleanup` (an AsyncLocal depth, so a fan-out inside it is marked too); a race, fan-out or serve opened inside it records that depth (`flowBase`) and still cuts its own flows | a second REPL ^C, a second signal |
| REPL evaluation | the REPL thread, bracketed by `beginReplEval`/`endReplEval` | REPL ^C |

Cancellation sources:

- **race win**: the winning arm's `settle` condemns every other arm's
  `RaceGroup` and cancels the race token.
- **teardown**: SIGINT, SIGTERM, SIGHUP (the exit hook in
  `installExitHook`, or the foreground reaper in `registerForeground`),
  or EPIPE on weir's stdout (`EpipeWriter`). `requestTeardown` records
  the first code (130/143/129/141), sets `teardownWake` and cancels
  `teardownCut`; every source but the terminal ^C at no live scope
  then runs `killLiveChildren`.
- **REPL ^C**: the REPL's SIGINT registration calls `interruptRepl`,
  which cancels `replCut` on the first press and `replCutCleanup` too
  on the second.
- **scope close**: `within serve` closing past its 2s drain cancels the
  handlers' flow token and condemns their groups.
- **fan-out exit**: an `exit n` in a `pmap`/`piter` arm cancels the
  fan-out's `fanCancel` token and condemns each running arm's group.

A **boundary** is where a flow checks whether it was stopped: every
builtin call (`Eval.apply` → `checkCancelled`), every spawn
(`gatedSpawn`), every runtime-owned wait (`sleepInterruptible`,
`waitOrCancel`, `waitProcess`, `withWaitToken` around an Http send,
`cutRead`/`cutJoin`/`cutBlocking`, `Stdin` reads, `PolledFd` reads),
and, on the main thread only, every evaluated node (`Eval.eval` →
`checkTeardown`). A foreground wait on a child (`p.WaitForExit` in the
`Proc` consumers, `waitProcess`) ends at a teardown because the
teardown's kill ends the child; then `raiseNonzero` raises. The terminal
^C's teardown kills nothing, so there the wait lasts until the child
exits. Pure computation has no boundary.

## Invariants

**I1. No statement runs on the main flow after a teardown is requested.**
Scripts and `-e`. The next evaluated node or interruptible wait raises
`ExitRequest code`, once, and the flow unwinds through its scopes. Rests on
[D:signal-main-unwind], [D:stdout-epipe]. Enforced by `Session.checkTeardown`
(called from `Eval.eval`, `raiseWoken`, `waitTask`, `Proc.raiseNonzero`,
`Proc.wait`), `teardownRaised` (the raise happens once), and
`interruptible`/`wakeHandles`, which make main-thread waits wake on
`teardownWake`.

**I2. Every flow off the main thread, outside a cleanup, raises at its
next boundary once it is stopped.** A stopped arm raises `Cancelled`, a
REPL interrupt raises `Interrupted`. Both pass through `try` and retry
like `exit` does. Rests on [D:pfirst-join], [D:teardown-joins-arms],
[D:repl-interrupt], [D:serve-cut]. A helper task reading for a flow (a
capture's stderr, a chain's pumps) is off the flow, so the teardown cuts
it; the flow joining it raises its own stop instead, so the main thread
raises `ExitRequest` ([D:epipe-kills-at-teardown]). Enforced by `flowCut`
(the race, serve or fan-out token, gated by the `racesLive` counter and
taken relative to the flow's cleanup base, [D:cleanup-owns-its-cuts]),
`teardownCuts`,
`checkCancelled`, `checkCancels`, `cutTokens`/`withWaitToken`, `cutJoin`
(`raiseWoken` before it rethrows a cut helper's cancel), and `Eval`
`TETry` (which re-raises `OperationCanceledException`).

**I3. A stopped flow's children die with it.** On a race win, the
winner condemns every other group before it releases its result, and a
child that registers after the condemn is killed as it registers. Past
the serve drain, each handler's group is condemned. On a fan-out exit,
each in-flight arm's group is condemned. Groups nest: a child of a
nested race or fan-out registers with each enclosing arm's group too, so
condemning an arm reaches it ([D:fanout-exit]). On a signal or a
closed stdout, every live child not started by a cleanup is tree-killed
once, and spawning stops. The exception is a terminal ^C at no live
scope, which kills nothing (I11). Rests on [D:seq-pfirst],
[D:serve-cut], [D:signal-main-unwind], [D:sigint-reaps],
[D:epipe-kills-at-teardown]. Enforced by `RaceGroup.Register`/`Condemn`,
`registerChild` (called from `Proc.spawn` inside `gatedSpawn`),
`killLiveChildren` (takes the spawn gate exclusively, then sets
`spawningStopped`; `killedProcs` makes each kill happen once; called by
the signal handlers and `EpipeWriter.Guard`).

**I4. No fan-out, race or serve scope returns before its flows have
joined.** Two things may hold a join: pure computation and a running
cleanup. Only a teardown or a fan-out exit may abandon a flow: a
fan-out arm parked in a read or wait that no cut reaches is left behind
once every arm the join waits on has been parked for two polls in a row
(for the teardown, every live arm; for an `exit n`, that fan-out's own
arms). The join proposes, re-checks and only then commits, so an arm
whose call returns meanwhile is never abandoned, and an abandoned arm
whose call returns later stops there and never runs again. A read that
a race, a REPL ^C or the serve cut stops is abandoned too: its bytes are
never used. When the join abandons parked arms, the joining thread
first runs their pending `always` cleanups, each in its arm's context
and claimed by removal, so none runs twice or lands after an enclosing
scope releases; the watchdog has stood down for the join, so a slow
one runs whole. Rests on [D:pfirst-join],
[D:teardown-joins-arms], [D:teardown-abandons-reads],
[D:abandoned-arm-cleanup], [D:fanout-exit-reads],
[D:serve-cut-joins-cleanup]. Enforced by `Session.waitTask` (one
`waitTask all` for pfirst), `FanArms.Join` (pmap/piter: `waitTask`, or
the fan-out's own abandon once its cut fires), `joinThread` (serve, no
cap), `armRunning`/`parking`/`parked`/`fanParks`/`abandonParked`/`tryAbandon`,
`armAlways`/`runAbandonedCleanups`, and `cutRead`/`cutBlocking`.

**I5. A stopped fan-out takes no new arm and never returns a partial
result.** After a teardown it raises `ExitRequest code`. After a REPL ^C
it raises `Interrupted`, and an arm whose command died of the ^C counts
as the interrupt, not as a failure. An `exit n` in a `pmap`/`piter` arm
stops the fan-out: no new arm, the in-flight arms cut and joined, then
the exit, raised before any arm error. A race takes no new arm once it
has been won. Rests on [D:teardown-stops-arms], [D:repl-interrupt-fanout],
[D:seq-pfirst], [D:fanout-exit]. Enforced by `take` and `stop` in
`runParallelWith`, the loop guard in `runRaceWith`, `stopTakingArms`,
`interruptRequested`, and the checks that run after the join.

**I6. An `always` cleanup runs whole.** Nothing preempts it: not the
race cut, the serve cut, the teardown, or the first REPL ^C. Its spawns
start even after the spawn gate has shut. Its children are spared the
teardown's kill and never join a race group. The signal backstops (the
watchdog and the terminal ^C's grace) stand by while the main thread is
in a cleanup or closing a `within serve` (its drain and joins). It runs
exactly once, because its claim is a removal from `liveAlways`. In a
script on POSIX it runs on the flow that reached it; a backstop sweep
(the watchdog, the REPL's handler, Windows' `CancelKeyPress`) runs a
pending one on the signal thread, still whole, once and LIFO. Only a
second signal or a second REPL ^C cuts it. A race, fan-out or serve
opened inside a cleanup still cuts its own flows (`inFlowCleanup`, the
depth past the flow's base, in `flowCut`, `waitTokens`,
`RaceGroup.Register` and `flowToken`); the absolute mark (`inCleanup`)
still guards the teardown, the spawn gate, `stopTakingArms` and
`replToken`. Rests on [D:within-always], [D:cleanup-owns-its-cuts],
[D:within-always-any], [D:teardown-always-spawns],
[D:teardown-never-preempts], [D:repl-interrupt], [D:serve-drain-signal],
[D:signal-thread-cleanups]. Enforced by
`runningCleanup`/`inCleanup` (consulted by `flowCut`, `teardownCuts`,
`checkTeardown`, `interruptible`, `waitTokens`, `replToken`, `gatedSpawn`,
`stopTakingArms`), `cleanupProcs` (filled in `registerForeground`), the
`RaceGroup.Register` skip, `backstopVerdict` (`watchdogGate`,
`mainCleanups`, `mainClosing` from `closingScope`), and `Eval`
`TEAlways` `claimAndRun` (`deregisterAlways`).

**I7. A command killed by the teardown is the teardown, never a
`command failed`.** Rests on [D:teardown-kill-not-failure],
[D:sigint-terminal-group]. Enforced by `Proc.raiseNonzero`, which calls
`settleInterrupt` (a 130 death while weir is in the foreground waits up
to 250ms for the request), then `checkTeardown`, then `checkCancelled`.
The foreground reaper requests the teardown before it kills.

**I8. Every exit settles the tree kills in flight.** It waits up to 2s
for the in-flight count to reach zero, so no SIGSTOPped orphan
outlives weir. Rests on [D:kill-settle]. Enforced by `killTree` (counts
itself in flight), and by `settleKills` in `exitCodeOr`,
`sweepLiveTmpDirs` (ProcessExit, watchdog, REPL handler, a hard exit),
`raiseSigint` (every death by the terminal ^C) and the foreground
reaper.

**I9. A tree kill reaches every descendant still linked by parent pid,
and only those.** On Linux, it walks `/proc/PID/task/TID/children`,
stops each process before listing its children, and kills leaves first.
Elsewhere it uses `Process.Kill(true)`. Rests on [D:tree-kill-walk]. Enforced
by `ProcTree.killWalk`, `killTree`.

**I10. weir has one stdin reader, and a cut never consumes or splits a
line.** `Self.prompt`, `Self.stdin` and the piped REPL all read through
`Session.Stdin`, one reader at a time. Bytes are read only while a
reader waits, and a prompt reads one byte at a time up to its newline.
A cut that lands while a line is half read finishes that line from the
bytes fd 0 already holds and leaves it buffered. A line that completes
as the cut lands is put back. The piped REPL's lookahead line goes back
into the same buffer. Children read fd 0 directly, so they see only the
bytes weir has not read. Rests on [D:stdin-reader], [D:prompt],
[D:prompt-cut-whole-line], [D:repl-lookahead-unread],
[D:prompt-tty-keys]. Enforced by `Stdin.readWith` (`gate`, `awaitReadable`,
put-back on `raiseWoken`), `finishBegunLine`, `unread`, `dropKeySequences`.

**I11. A SIGINT kills children unless it is the terminal's ^C at a
script with no live scope.** When no `tmp`/`proc`/`serve`/`always` scope
is open (one that has closed no longer counts) and weir's group is the
terminal's foreground group, weir kills nothing. It requests 130, waits
for the child, unwinds, and then dies by SIGINT. If no child is alive,
it does so after a 1s grace, held as the watchdog is (I12). Every other
SIGINT is a tree kill: one with no tty, a background group, a pid-only
`kill -INT` while weir is not the foreground group, or any SIGINT while
a scope is open. Once the full hook is installed, a pid-only `kill -INT`
unwinds the full hook's way even with no scope open. The same holds for
every TERM and HUP. The tty REPL survives its SIGINT. Rests on
[D:sigint-reaps], [D:sigint-terminal-group], [D:sigint-live-scopes],
[D:sighup-teardown], [D:repl-isig]. Enforced by `registerForeground`'s
handler (until the full hook is installed), the full hook's dispatch in
`installExitHook` (`scoped`: a live tmp dir, which a `within proc`'s
spill dir is, or a pending `always`), `terminalInterrupt`,
`Libc.terminalForeground`, and `dieBySigint`/`raiseSigint`.

**I12. A second signal during a teardown exits at once with 128 + the
signal.** After a terminal ^C at no live scope, the second one kills the
live children, then weir dies by SIGINT. If the main thread is blocked
where no boundary comes, the watchdog runs the sweep and exits after 1s.
The terminal ^C's grace exits the same way once no child has been alive
for 1s. Both stand by while the main thread is in a cleanup or closing
a serve scope, and stand down for good once it joins arms
(`mainJoining`, so pure compute holds the join) or is already
unwinding. Rests on [D:signal-teardown], [D:teardown-joins-arms],
[D:teardown-never-preempts], [D:terminal-sigint-teardown]. Enforced by
`tearingDown` (shared by both handlers), `hardExit`, `backstopVerdict`,
the watchdog in `installExitHook` and the grace in `terminalInterrupt`.

**I13. A REPL ^C cuts what the evaluation waits on, never pure
computation.** The REPL token is global, so it reaches every flow of
the evaluation. Waits inside a cleanup honour only a second ^C. A spawn
is also a ^C boundary, but a builtin call is not. A serve handler the
^C cuts closes its connection unanswered (`Serve.abandon`) and records
nothing on `Server.streamErrors`. Tty REPL only: a piped REPL has no
`interruptRepl`. Rests on [D:repl-interrupt],
[D:repl-interrupt-fanout], [D:repl-isig]. Enforced by `replToken`,
`interruptRepl`, `checkInterrupted`, and `Repl.evalChecked`.

**I14. The platform split.** On Linux, FIFO and unseekable `File.read`
opens non-blocking and polls beside the cut, so the cut closes the fd
and no thread stays in `read(2)` ([D:fifo-read-polled], `openPolled`,
`PolledFd`). The tree walk is Linux-only too (I9). On macOS, FIFO reads
are abandoned (`cutBlocking`, which parks under a teardown), and an
abandoned reader may take one later write. Stdin cuts work on all of
POSIX. On Windows, stdin reads block uncut, and Ctrl+C runs
`sweepLiveTmpDirs` from `Console.CancelKeyPress`: there is no
main-thread unwind, no foreground reaper and no EPIPE teardown
([D:signal-teardown], [D:windows-v1]).

**I15. Non-claims.** `kill -9` of weir runs nothing; only a held lock is
released, by the kernel ([D:scoped-procs], [D:lock-advisory]). The tree
kill is SIGKILL, so a child's own cleanup never runs. A descendant that
was reparented away (`(cmd &)`, a daemon) is out of reach
([D:tree-kill-walk]). An inherited SIG_IGN is honoured: SIGINT stays
unbound when there is a controlling tty, so a pid-directed `kill -INT`
is dropped too; SIGTERM always works, and SIGHUP does unless it was
ignored too ([D:signal-teardown], [D:sigint-reaps],
[D:sigint-ignored-tty]). An ignored SIGHUP always stays unbound
([D:sighup-teardown]).

## Interactions

| case | outcome (the rule that wins) |
|---|---|
| teardown while a pfirst loser runs its cleanup, after the winner returned | I6: the cleanup runs whole and its commands are spared; `waitTask all` joins it (I4); then `ExitRequest` |
| teardown while the main thread is in a cleanup, or between a body and its cleanup | I6 over I1: `checkTeardown` skips inside the cleanup and raises at the first node after it |
| teardown while an arm does pure computation | I4: the join holds and the watchdog stands down (`mainJoining`); only a second signal ends it (I12) |
| teardown while every arm is parked in an uncut read, one of them holding a `within lock` | I4: the join abandons them after about 100ms and the main flow unwinds. The arms' pending `always` cleanups run first, on the main thread in each arm's context, before the main flow unwinds (I4). The arm's lock stays held until the process exits, when the kernel releases it |
| script signal while `within serve` is open or closing | the close still drains: the handlers are cut by the teardown at once (I2), the drain lasts at most 2s, then the cut and the uncapped join (I4); a signal landing during the close holds the watchdog until the close ends (I6) |
| REPL ^C while `within serve` drains | the drain is not cut (bounded, 2s); handlers' waits raise `Interrupted`, and the handler ends unanswered (its connection closed), with nothing on `Server.streamErrors` (I13); the join is uncapped, so a pure-compute handler holds the prompt |
| race cut, teardown and REPL ^C on one wait | `raiseWoken`: `checkTeardown` (main), then `Cancelled`, then `Interrupted` |
| `exit n` in a race arm | ends the race with that raise; losers are condemned and joined |
| `exit n` in a pmap/piter arm | stops the fan-out as a race win does: no new arm, the in-flight arms cut (their children killed by their groups) and joined, cleanups whole; then `exit n`, never masked by an arm error (I5) |
| `pfirst` or `within serve` inside a cleanup | the scope cuts its own losers and late handlers, children killed (I2, I3); the cleanup mark still spares them the outer cuts, so a first signal or REPL ^C does not cut them and a second does (I6) |
| terminal ^C while a `tmp`/`proc`/`serve`/`always` scope is open | the full hook tree-kills and the main flow unwinds, exit 130 (I11) |
| terminal ^C after every scope has closed | the unscoped rule: no kill, wait for the child, die by SIGINT (I11) |
| terminal ^C at no live scope while arms compute | the grace stands down once the main thread joins (`mainJoining`); the join holds until the arms reach a boundary, or a second ^C hard-exits (I4, I12) |
| closed stdout while an arm waits on a command | the EPIPE's kill ends the command at once; the arm raises and is joined, then `ExitRequest 141` (I3, I4) |

## Known residuals

- A pure-compute flow holds a race, a serve close and a teardown join
  until it reaches a boundary ([D:pfirst-join], [D:serve-cut-joins-cleanup],
  [D:teardown-joins-arms]).
- An arm parked in a FIFO read, or in a pipe held by an orphan, is
  abandoned rather than joined by a teardown or a fan-out exit
  ([D:teardown-abandons-reads], [D:fanout-exit-reads]). The abandoned
  thread stays blocked until the process exits.
- stdin ([D:prompt-cut-whole-line]): a line a cut finished, or one the
  writer had only partly written, stays in weir's buffer, so a child
  spawned next does not see it. The stream reads buffer ahead in 64KiB
  chunks. Typeahead that the REPL's tty editor already pulled in is
  unseen by `Self.prompt`. On Windows, reads block uncut.
- A `kill -INT` aimed at a weir that is the terminal's foreground group
  looks the same as the terminal's ^C, so nothing is killed
  ([D:sigint-terminal-group]).
- When a main thread is blocked with no boundary (a pipe held open by
  an orphan, a main-thread Http send), only the 1s watchdog ends it.
  The watchdog runs the pending cleanups on its own thread, then exits
  ([D:signal-main-unwind], [D:signal-thread-cleanups]).
- An abandoned arm's cleanup runs while its body is still parked, and
  the arm's own inner lock or tmp dir is released only at process exit
  ([D:abandoned-arm-cleanup]).
- A terminal ^C at no live scope waits for a child that ignores it;
  only a second ^C ends that wait ([D:terminal-sigint-teardown]).
- The REPL has no main thread: TERM/HUP run the sweep in the signal
  handler and default termination follows ([D:repl-isig]).

## Discrepancies

Places where the code and a row or the SKILL disagree, as of this
page. Each needs a ruling, either a code fix or a reworded row. A
resolved entry keeps its number and names its row.

1. Resolved: a race won under a teardown joins its losers
   ([D:pfirst-join], [D:teardown-joins-arms]).
2. Resolved: a race, fan-out or serve opened inside a cleanup cuts its
   own flows ([D:cleanup-owns-its-cuts]).
3. Resolved: a signal during the serve close holds the watchdog
   ([D:serve-drain-signal]).
4. Resolved: the terminal ^C's teardown has the second-signal escape,
   its grace is held as the watchdog is, and its SIGINT death settles
   the kills ([D:terminal-sigint-teardown]).
5. Resolved: a closed stdout kills the children at the teardown, which
   ends the foreground waits ([D:epipe-kills-at-teardown]).
6. Resolved: the terminal ^C rule follows live scopes
   ([D:sigint-live-scopes]).
7. Resolved: the carve-out says send SIGTERM, or SIGHUP
   ([D:sigint-ignored-tty]).
8. Resolved: "on the script's own thread" is scripts on POSIX; the
   backstop sweeps run cleanups on the signal thread
   ([D:signal-thread-cleanups]).
9. Resolved: an abandoned arm's pending cleanup runs on the main
   thread before the scopes release ([D:abandoned-arm-cleanup]).
10. Resolved: `exit n` in a pmap/piter arm stops the fan-out and is
    never masked ([D:fanout-exit]).
11. Resolved: a REPL ^C in a serve handler closes the connection
    unanswered and records nothing ([D:repl-interrupt]).
12. Resolved: a script's signal still drains serve
    ([D:serve-drain-signal]).
