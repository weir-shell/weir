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
| fan-out arm (`pmap`/`piter` + `With`) | a LongRunning task per worker (`runParallelWith`, `Session.armRunning`) | teardown, REPL ^C, an enclosing race |
| race arm (`pfirst` + `With`) | the same, plus a race token and a `RaceGroup` per arm (`runRaceWith`) | race win, teardown, REPL ^C |
| serve handler | a thread per request with its own flow token and `RaceGroup` (`Eval` `WithinServe` `runHandler`) | scope close past the drain, teardown, REPL ^C |
| `always` cleanup | whatever flow reached it, marked by `Session.runningCleanup` (an AsyncLocal depth, so a fan-out inside it is marked too) | a second REPL ^C, a second signal |
| REPL evaluation | the REPL thread, bracketed by `beginReplEval`/`endReplEval` | REPL ^C |

Cancellation sources:

- **race win**: the winning arm's `settle` condemns every other arm's
  `RaceGroup` and cancels the race token.
- **teardown**: SIGINT, SIGTERM, SIGHUP (the exit hook in
  `installExitHook`, or the foreground reaper in `registerForeground`),
  or EPIPE on weir's stdout (`EpipeWriter`). `requestTeardown` records
  the first code (130/143/129/141), sets `teardownWake` and cancels
  `teardownCut`.
- **REPL ^C**: the REPL's SIGINT registration calls `interruptRepl`,
  which cancels `replCut` on the first press and `replCutCleanup` too
  on the second.
- **scope close**: `within serve` closing past its 2s drain cancels the
  handlers' flow token and condemns their groups.

A **boundary** is where a flow checks whether it was stopped: every
builtin call (`Eval.apply` → `checkCancelled`), every spawn
(`gatedSpawn`), every runtime-owned wait (`sleepInterruptible`,
`waitOrCancel`, `waitProcess`, `withWaitToken` around an Http send,
`cutRead`/`cutJoin`/`cutBlocking`, `Stdin` reads, `PolledFd` reads),
and, on the main thread only, every evaluated node (`Eval.eval` →
`checkTeardown`). Pure computation has no boundary.

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
[D:repl-interrupt], [D:serve-cut]. Enforced by `flowCut` (race/serve token,
gated by the `racesLive` counter), `teardownCuts`, `checkCancelled`,
`checkCancels`, `cutTokens`/`withWaitToken`, and `Eval` `TETry` (which
re-raises `OperationCanceledException`).

**I3. A stopped flow's children die with it.** On a race win, the
winner condemns every other group before it releases its result, and a
child that registers after the condemn is killed as it registers. Past
the serve drain, each handler's group is condemned. On a signal, every
live child not started by a cleanup is tree-killed once, and spawning
stops. Rests on [D:seq-pfirst], [D:serve-cut], [D:signal-main-unwind],
[D:sigint-reaps]. Enforced by `RaceGroup.Register`/`Condemn`,
`registerChild` (called from `Proc.spawn` inside `gatedSpawn`),
`killLiveChildren` (takes the spawn gate exclusively, then sets
`spawningStopped`; `killedProcs` makes each kill happen once).

**I4. No fan-out, race or serve scope returns before its flows have
joined.** Two things may hold a join: pure computation and a running
cleanup. Only a teardown may abandon a flow: a fan-out arm parked in a
read that no cut reaches is left behind once every live arm has been
parked for two polls in a row, and if the read ever returns, the arm
raises without using the bytes. A read that a race, a REPL ^C or the
serve cut stops is abandoned in the same way: its bytes are never used.
Rests on [D:pfirst-join], [D:teardown-joins-arms],
[D:teardown-abandons-reads], [D:serve-cut-joins-cleanup]. Enforced by
`Session.waitTask` (`WhenAll` for pmap/piter, `WhenAny` then `all` for
pfirst), `joinThread` (serve, no cap), `armRunning`/`parking`/`parked`/`allArmsParked`,
and `cutRead`/`cutBlocking`.

**I5. A stopped fan-out takes no new arm and never returns a partial
result.** After a teardown it raises `ExitRequest code`. After a REPL ^C
it raises `Interrupted`, and an arm whose command died of the ^C counts
as the interrupt, not as a failure. A race takes no new arm once it has
been won. Rests on [D:teardown-stops-arms], [D:repl-interrupt-fanout],
[D:seq-pfirst]. Enforced by `take` in `runParallelWith`, the loop guard
in `runRaceWith`, `stopTakingArms`, `interruptRequested`, and the checks
that run after the join.

**I6. An `always` cleanup runs whole.** Nothing preempts it: not the
race cut, the serve cut, the teardown, or the first REPL ^C. Its spawns
start even after the spawn gate has shut. Its children are spared the
teardown's kill and never join a race group. The signal watchdog stands
down while the main thread is in a cleanup. It runs exactly once,
because its claim is a removal from `liveAlways`. Only a second signal
or a second REPL ^C cuts it. Rests on [D:within-always],
[D:within-always-any], [D:teardown-always-spawns],
[D:teardown-never-preempts], [D:repl-interrupt]. Enforced by
`runningCleanup`/`inCleanup` (consulted by `flowCut`, `teardownCuts`,
`checkTeardown`, `interruptible`, `waitTokens`, `replToken`, `gatedSpawn`,
`stopTakingArms`), `cleanupProcs` (filled in `registerForeground`), the
`RaceGroup.Register` skip, `watchdogGate`/`mainCleanups`, and `Eval`
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
`sweepLiveTmpDirs` (ProcessExit, watchdog, REPL handler) and the
foreground reaper.

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
script with no scope hook.** When only the foreground reaper is
installed (no `tmp`/`proc`/`serve`/`always` scope has registered) and
weir's group is the terminal's foreground group, weir kills nothing. It
requests 130, waits for the child, unwinds, and then dies by SIGINT. If
no child is alive, it does so after a 1s grace. Every other SIGINT is a
tree kill: a pid-only `kill -INT`, one with no tty, a background group,
or any SIGINT once the full hook is installed. The same holds for every
TERM and HUP. The tty REPL survives its SIGINT. Rests on
[D:sigint-reaps], [D:sigint-terminal-group], [D:sighup-teardown],
[D:repl-isig]. Enforced by `registerForeground`'s handler,
`Libc.terminalForeground`, `dieBySigint`/`raiseSigint`, and
`installExitHook`.

**I12. A second signal during a teardown exits at once with 128 + the
signal.** If the main thread is blocked where no boundary comes, the
watchdog runs the sweep and exits after 1s. It stands down while the
main thread is in a cleanup, joining arms (`mainJoining`), or already
unwinding. Rests on [D:signal-teardown], [D:teardown-joins-arms],
[D:teardown-never-preempts]. Enforced by `tearingDown` and the watchdog
in `installExitHook`.

**I13. A REPL ^C cuts what the evaluation waits on, never pure
computation.** The REPL token is global, so it reaches every flow of
the evaluation. Waits inside a cleanup honour only a second ^C. A spawn
is also a ^C boundary, but a builtin call is not. Tty REPL only: a
piped REPL has no `interruptRepl`. Rests on [D:repl-interrupt],
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
unbound when there is a controlling tty ([D:signal-teardown],
[D:sigint-reaps]), and SIGHUP always stays unbound
([D:sighup-teardown]).

## Interactions

| case | outcome (the rule that wins) |
|---|---|
| teardown while a pfirst loser runs its cleanup, after the winner returned | I6: the cleanup runs whole and its commands are spared; the second `waitTask all` joins it (I4); then `ExitRequest` |
| teardown while the main thread is in a cleanup, or between a body and its cleanup | I6 over I1: `checkTeardown` skips inside the cleanup and raises at the first node after it |
| teardown while an arm does pure computation | I4: the join holds and the watchdog stands down (`mainJoining`); only a second signal ends it (I12) |
| teardown while every arm is parked in an uncut read, one of them holding a `within lock` | I4: the join abandons them after about 100ms and the main flow unwinds. The arm's lock stays held until the process exits, when the kernel releases it. The arm's pending `always` still sits in `liveAlways`, and the ProcessExit sweep runs it (Discrepancies 9) |
| script signal while `within serve` drains | handlers are cut by the teardown (I2); the drain lasts at most 2s; the join is uncapped once it is reached (I4), but see Discrepancies 3 |
| REPL ^C while `within serve` drains | the drain is not cut (bounded, 2s); handlers' waits raise `Interrupted`, the handler catch-all answers 500 and records a line on `Server.streamErrors`; the join is uncapped, so a pure-compute handler holds the prompt |
| race cut, teardown and REPL ^C on one wait | `raiseWoken`: `checkTeardown` (main), then `Cancelled`, then `Interrupted` |
| `exit n` in a race arm | ends the race with that raise; losers are condemned and joined |
| `exit n` in a pmap arm | an ordinary arm error: other arms keep running and the first error by input order is raised (Discrepancies 10) |
| `pfirst` or `within serve` inside a cleanup | I6 wins over I2/I3: the arms inherit the cleanup mark, so losers are neither cut nor killed (Discrepancies 2) |
| terminal ^C after any `tmp`/`proc`/`serve`/`always` scope has run | the full hook owns SIGINT from then on: it tree-kills and exits 130 (I11) |

## Known residuals

- A pure-compute flow holds a race, a serve close and a teardown join
  until it reaches a boundary ([D:pfirst-join], [D:serve-cut-joins-cleanup],
  [D:teardown-joins-arms]).
- An arm parked in a FIFO read, or in a pipe held by an orphan, is
  abandoned rather than joined ([D:teardown-abandons-reads]).
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
  ([D:signal-main-unwind]).
- The REPL has no main thread: TERM/HUP run the sweep in the signal
  handler and default termination follows ([D:repl-isig]).

## Discrepancies

Places where the code and a row or the SKILL disagree, as of this
page. Each needs a ruling, either a code fix or a reworded row.

1. **A teardown can return from pfirst without joining the losers.**
   `runRaceWith` calls `waitTask (WhenAny(outcome, all))` and then
   `waitTask all`. If an arm wins after the teardown is requested, the
   first `waitTask` sees `WhenAny` complete and `checkTeardown` raises
   `ExitRequest` before the second join runs. The losers are condemned
   and cut, but a loser that is in its cleanup or in pure computation
   keeps running while the main flow unwinds. This contradicts
   [D:pfirst-join] and [D:teardown-joins-arms] ("joins the arms
   before raising its ExitRequest").
2. **A race or serve scope inside an `always` cleanup cuts nothing.**
   `cleanupDepth` flows into the arms (AsyncLocal), so `flowCut` and
   `waitTokens` ignore the race token and `RaceGroup.Register` registers
   no child. The losers run to completion and pfirst waits for all of
   them. Serve handlers opened inside a cleanup are never cut either.
   This contradicts [D:seq-pfirst], [D:pfirst-join] and [D:serve-cut],
   which state the contract without that exception.
3. **The watchdog can fire during the serve drain.** A signal that
   lands while `Serve.stop handle drainMs` waits (up to 2s) finds the
   watchdog's conditions all false: `teardownRaised = 0`,
   `mainJoining = 0` (it is set only in `joinThread`), and
   `mainCleanups = 0`. After 1s the watchdog sweeps and exits, so a
   handler in its cleanup is not joined whole. This contradicts
   [D:serve-cut-joins-cleanup] ("a teardown arriving meanwhile stands
   the watchdog down").
4. **The terminal-foreground SIGINT path has no second-signal escape,
   and pure computation does not hold its join.** `registerForeground`'s
   handler never consults `tearingDown`, so with a child that ignores
   ^C, a second ^C sets `Cancel` again and weir waits on. Its grace
   thread calls `raiseSigint` after about 1s with no live child, even
   while the main thread joins pure-compute arms, and it does so
   without `settleKills`. This contradicts [D:signal-teardown] and the
   SKILL ("a second signal mid-teardown hard-exits"),
   [D:teardown-joins-arms] ("pure compute holds the join") and
   [D:kill-settle] ("every exit").
5. **A closed stdout kills no child until exit.** EPIPE
   (`EpipeWriter.Guard`) calls only `requestTeardown`, and children die
   in `exitCodeOr` after the unwind. Foreground waits
   (`p.WaitForExit` in `Proc.linesOf`/`streamCodeOf`/…,
   `Session.waitProcess`) are not teardown boundaries, so an arm (or
   the main flow) waiting on a running command holds the exit until the
   command ends by itself. This contradicts [D:stdout-epipe]
   ("children killed") and [D:teardown-joins-arms] (wait is a
   boundary). The `waitProcess` comment ("a teardown kills the child")
   holds only for signals.
6. **"Unscoped" is sticky.** `hookInstalled` is never reset, so once a
   `tmp`/`proc`/`serve`/`always` scope has registered, even one that
   has already closed, every later terminal ^C tree-kills. The SKILL
   says "a terminal ^C at an unscoped script" and [D:sigint-terminal-group]
   says "the scoped paths … unchanged"; neither says the switch is
   per-process.
7. **`kill -INT` cannot reach a weir that inherited SIGINT ignored
   while it has a tty.** The carve-out in the SKILL and in
   [scopes.md](scopes.md) says "send SIGTERM, or `kill -INT` its pid
   directly". But `resetDetachedSigint` resets SIG_IGN only without a
   controlling tty, and [D:sigint-reaps] says that SIGINT "stays
   unbound", so the kernel drops a pid-directed SIGINT too.
8. **Some signal paths run user code off the main thread.**
   [D:signal-main-unwind]'s headline is unqualified, and the SKILL says
   "on the script's own thread". But the REPL (no main thread: the
   handler sweeps), Windows (`CancelKeyPress` sweeps) and the watchdog
   backstop all run pending `always` cleanups from `sweepLiveTmpDirs`
   on another thread.
9. **An abandoned arm's cleanup runs after its scopes are released.** A
   parked arm inside `within … always` leaves its entry in
   `liveAlways`, and the ProcessExit sweep runs it after the main flow
   has released the enclosing locks and tmp dirs. [D:teardown-abandons-reads]
   does not mention this, and it contradicts [D:teardown-joins-arms]
   ("no arm effect lands after a lock releases").
10. **`exit n` in a pmap arm is ranked like any arm error.**
    `runParallelWith` stores the arm's `ExitRequest` in `errors`, keeps
    taking arms, and raises the lowest-index error, so an earlier arm's
    failure masks the exit. The SKILL's pfirst bullet says "`exit n` in
    an arm ends the race and the script, as in `pmap`".
11. **A serve handler turns a REPL interrupt into a 500.** `runHandler`'s
    catch-all records `Interrupted` as a handler failure (a 500 and a
    `serve handler … failed` line), where [D:repl-interrupt] says the
    interrupt passes through like `exit`.
12. **On a script, a signal still runs the serve drain.**
    [D:serve-drain] says "the signal sweep closes with no drain", but on
    a script a signal unwinds through the `within serve` finally, which
    drains for up to 2s. Only the backstop sweeps (watchdog, REPL,
    ProcessExit, Windows) close with no drain.
