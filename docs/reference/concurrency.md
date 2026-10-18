# Concurrency and cancellation

The contract the runtime keeps when flows run side by side and get
stopped. It is written for whoever changes `Session.fs`, `Builtins.fs`
(the fan-outs), `Eval.fs` (`within`) or `Proc.fs`, and for whoever tests
them: each invariant names the decisions it rests on and the function
that enforces it. The user-facing promises are in
[Scopes](scopes.md) and the SKILL; this page is their mechanism.

## The scope tree

Cancellation and cleanup ownership live in one tree of nodes
([D:scope-tree]). The current node rides one AsyncLocal
(`Session.currentNode`), so a flow's helper tasks (a capture's stderr
reader, a chain's pumps, a `cutBlocking` call) run in the flow's node.

| node | made by | its flow runs on | cut by |
|---|---|---|---|
| root | the process | nobody | a signal or a closed stdout (the teardown) |
| flow | the main flow (`runOnMain`); a REPL entry (`beginReplEval`) | the main thread; the REPL thread, which stands in for it during an entry | its parent; a REPL ^C (the entry's node) |
| race | `runRaceWith` | nobody (the opener joins) | its parent; its win closes it to new arms |
| fan-out | `runParallelWith` | nobody (the opener joins) | its parent; an `exit n` in an arm |
| arm | each item a race or fan-out takes | a LongRunning worker | its parent; a race win cuts every arm but the winner |
| serve | `within serve` | the body (the flow enters it) | its parent; an `exit n` in a handler |
| handler | each request | a thread per request | its parent; the close past the drain |
| shield | a running `always` cleanup (`runningCleanup`) | the cleanup | only a cut that passes shields: a second REPL ^C |

What a node owns:

- **Its children.** A spawn registers the child with the current node
  (`registerChild`); the child leaves it as it exits (`forgetChild`, from
  the `Exited` event), and a node that closes hands its live children to
  its parent (`closeNode`), so ownership never outlives a flow and
  never grows with the spawns a flow made.
- **Its cut.** `cutNode` marks the node and every node under it,
  stopping at shields (a level-2 cut, the second REPL ^C, passes them),
  with a reason (a teardown, a race lost, a fan-out exit, the serve
  cut, a handler's exit, a REPL ^C). A killing cut also tree-kills
  every child the nodes it reached own, and a child registering into a
  node a killing cut reached dies at registration. A node opening under
  a cut parent is born cut, under the parent's lock, so no node escapes
  a cut in flight. The token a wait hands to an API is the node's own,
  made at the first wait that needs one.
- **On the main flow, its frames.** Every scope the main flow opens
  (`always`, `tmp`, `proc`, `serve`, `lock`, `cd`, `env`) pushes its
  release (`pushFrame`), which the scope's own exit claims and runs
  (`releaseFrame`). The one other owner is the signal backstop, which
  adopts a main flow stuck where no boundary comes (`adoptMain`).

Cancellation sources:

- **race win**: the winning arm closes the race to new arms and cuts
  every other arm, killing.
- **teardown**: SIGINT, SIGTERM, SIGHUP (`onSignal`) or EPIPE on weir's
  stdout (`EpipeWriter`). `requestTeardown` records the first code
  (130/143/129/141) and cuts the root; `killLiveChildren true` cuts it
  again, killing, sparing what shields own. The terminal ^C at no live
  scope is the soft teardown: no kill, and a wait on a child lasts
  while the child does (the child took the ^C too).
- **REPL ^C**: `interruptRepl` cuts the entry's node, the second press
  through its shields too. Nothing is killed: the terminal's ^C reached
  the foreground child.
- **scope close**: `within serve` closing past its 2s drain cuts each
  live handler, killing.
- **fan-out exit**: an `exit n` in a `pmap`/`piter` arm records the exit
  on the fan-out's node and cuts it, killing.
- **handler exit**: an `exit n` in a serve handler records the exit on
  the serve node and cuts it, killing; the body is in that node, so it
  is cut too ([D:serve-handler-exit]).

A **boundary** is where a flow checks its node: every evaluated node on
every flow (`Eval.eval` → `evalBoundary`), every builtin call
(`Eval.apply` → `checkCancelled`), every spawn (`gatedSpawn`), every
runtime-owned wait (`sleepInterruptible`, `waitOrCancel`,
`waitProcess`, `withWaitToken` around an Http send, `cutJoin`,
`cutBlocking`), every read of a child's pipe (`CutStream`, once per
fill, never per line), every stdin read (`Stdin.readWith`) and every
FIFO read (`PolledFd`). One volatile read (`cutsPending`) while no node
is cut. The only computation with no boundary is a builtin's own loop
(`[1..n] |> Seq.length`).

## Invariants

**I1. No statement runs on the main flow after a teardown is requested.**
Scripts, `-e`, and a REPL entry. The next evaluated node or wait raises
`ExitRequest code`, once, and the flow unwinds through its scopes. Rests on
[D:signal-main-unwind], [D:stdout-epipe], [D:scope-tree]. Enforced by
`checkTeardown` (called from `evalBoundary`, `raiseWoken`, `waitTask`,
`Proc.raiseNonzero`, `Proc.wait`), `teardownRaised` (the raise happens
once; the unwind's waits then run as written, `unwinding`).

**I2. Every flow off the main thread raises at its next boundary once its
node is cut.** A cut arm or handler raises `Cancelled`, a REPL ^C raises
`Interrupted`. Both pass through `try` and retry like `exit` does. A
helper task runs in its flow's node, so the flow's cut stops it, and the
flow joining it raises its own stop (`cutJoin`). Rests on [D:pfirst-join],
[D:teardown-joins-arms], [D:repl-interrupt], [D:serve-cut],
[D:epipe-kills-at-teardown], [D:scope-tree]. Enforced by `cancelledBy`,
`checkCancelled`, `checkCancels`, `evalBoundary`, `tokenOf`, `CutStream`,
and `Eval` `TETry` (which re-raises `OperationCanceledException`).

**I3. A stopped flow's children die with it.** A killing cut tree-kills
every child the nodes it reached own and kills a child that registers
after it. Ownership follows the tree: a child of a nested race or
fan-out is owned under the enclosing arm, so cutting the arm reaches it,
and a child that outlives its node is its parent's. On a signal or a
closed stdout every child not owned by a cleanup dies once, and spawning
stops outside cleanups. The terminal ^C at no live scope kills nothing
(I11). Rests on [D:seq-pfirst], [D:serve-cut], [D:signal-main-unwind],
[D:sigint-reaps], [D:epipe-kills-at-teardown], [D:scope-tree].
Enforced by `cutFrom`, `registerChild`, `closeNode`, `killEntry` (each
kill once), `killLiveChildren`, `gatedSpawn`.

**I4. No fan-out, race or serve scope returns before its flows have
joined.** Every wait and read is cut by its node, so every cut flow
reaches its next boundary; no arm is ever abandoned. Two things may hold
a join: a builtin's own loop and a running cleanup. Rests on
[D:pfirst-join], [D:teardown-joins-arms], [D:serve-cut-joins-cleanup],
[D:scope-tree]. Enforced by `waitTask` (pfirst, pmap/piter), `joinThread`
(serve, no cap).

**I5. A stopped fan-out takes no new arm and never returns a partial
result.** After a teardown it raises `ExitRequest code`. After a REPL ^C
it raises `Interrupted`, and an arm whose command died of the ^C counts
as the interrupt, not as a failure. An `exit n` in a `pmap`/`piter` arm
stops the fan-out: no new arm, the in-flight arms cut and joined, then
the exit, raised before any arm error. A race takes no new arm once won,
and any fan-out or race none once an enclosing cut reached its node.
Rests on [D:teardown-stops-arms], [D:repl-interrupt-fanout],
[D:seq-pfirst], [D:fanout-exit]. Enforced by `take` in `runParallelWith`,
`tryOpenArm` and the loop guard in `runRaceWith`, `closeToArms`,
`stopTakingArms`, `interruptRequested`, and the checks after the join.

**I6. An `always` cleanup runs whole.** It runs in a shield node, which
no cut from outside reaches: not a race win, the serve cut, the teardown
or the first REPL ^C. Its spawns start after the teardown's kill; its
children are spared that kill; at a foreground terminal its commands run
in their own process group (I16). The backstops stand by while the main
thread runs a cleanup or closes a scope. It runs exactly once, on the
flow that reached it — or, on a main flow the backstop adopted, on the
backstop's thread (I12), never both: the claim is the frame's. A race,
fan-out or serve opened inside a cleanup is a node under the shield, so
it still cuts its own flows. Only a second signal (I12) or a second REPL
^C stops a cleanup. Rests on [D:within-always], [D:cleanup-owns-its-cuts],
[D:within-always-any], [D:teardown-always-spawns],
[D:teardown-never-preempts], [D:repl-interrupt], [D:serve-drain-signal],
[D:scope-tree]. Enforced by `runningCleanup`, `cutFrom` (the shield
rule), `releaseFrame`, `backstopVerdict` (`mainCleanups`, `mainClosing`
from `closingScope`), and `Eval` `TEAlways` `claimAndRun`.

**I7. A command killed by the teardown is the teardown, never a
`command failed`.** Rests on [D:teardown-kill-not-failure],
[D:sigint-terminal-group]. Enforced by `Proc.raiseNonzero`, which calls
`settleInterrupt` (a 130 death while weir is the foreground group waits up
to 250ms for the request; not in the REPL), then `checkTeardown`, then
`checkCancelled`. The dispatch requests the teardown before it kills.

**I8. Every exit settles the tree kills in flight.** It waits up to 2s
for the in-flight count to reach zero, so no SIGSTOPped orphan
outlives weir. Rests on [D:kill-settle]. Enforced by `killTree` (counts
itself in flight) and `settleKills` in `exitCodeOr`, `sweepDirs`,
`dieBy` and `hardExit`.

**I9. A tree kill reaches every descendant still linked by parent pid,
and only those.** On Linux it walks `/proc/PID/task/TID/children`, stops
each process before listing its children, and kills leaves first.
Elsewhere it uses `Process.Kill(true)`. Rests on [D:tree-kill-walk].
Enforced by `ProcTree.killWalk`, `killTree`.

**I10. weir has one stdin reader, and a cut never consumes or splits a
line.** `Self.prompt`, `Self.stdin` and the piped REPL all read through
`Session.Stdin`, one reader at a time. Bytes are read only while a
reader waits, and a prompt reads one byte at a time up to its newline.
A cut that lands while a line is half read finishes that line from the
bytes fd 0 already holds and leaves it buffered; a line that completes
as the cut lands is put back. Before a child that reads fd 0 starts, a
line weir began is finished (the wait cut as the flow is), so the child
starts at the next line and no reader sees a line that was not written.
A `Self.stdin` enumeration cut before its first read is no enumeration.
On Windows one read runs off the flow and every reader waits for it
beside its token, so a cut loses no byte. Rests on [D:stdin-reader],
[D:prompt], [D:prompt-cut-whole-line], [D:repl-lookahead-unread],
[D:prompt-tty-keys]. Enforced by `Stdin.readWith` (`gate`,
`awaitReadable`, put-back on `raiseWoken`), `finishBegunLine`,
`finishForChild` (from `Proc.spawn`), `unread`, `awaitPending`, and
`Script`'s `Self.stdin` (`consumed`: untouched, began, read).

**I11. A SIGINT kills children unless it is the terminal's ^C at a
script with no live scope.** When no `tmp`/`proc`/`serve`/`always` scope
is open (one that has closed no longer counts) and weir's group is the
terminal's foreground group, weir kills nothing: the soft teardown
waits for the child, unwinds, and dies by the SIGINT. If no child is
alive, the grace backstop ends it after 1s (I12). Every other SIGINT is
a teardown that kills: one with no tty, a background group, a pid-only
`kill -INT` while weir is not the foreground group, or any SIGINT while
a scope is open. The same holds for every TERM and HUP. The tty REPL
survives its SIGINT. Rests on [D:sigint-reaps],
[D:sigint-terminal-group], [D:sigint-live-scopes], [D:sighup-teardown],
[D:repl-isig]. Enforced by `onSignal` (`scoped`: a live tmp dir, which a
`within proc`'s spill dir is, or a pending `always` or serve scope),
`terminalInterrupt`, `Libc.terminalForeground`.

**I12. A second signal hard-exits at once.** Every flow parks at its
next runtime point (a boundary, a spawn, a frame claim, a cleanup's
start, the exit), every child dies (a cleanup's too), the kills settle,
the registered dirs go, the terminal is restored, and weir dies by the
FIRST signal of the teardown (141 for a closed stdout, which also counts
as the first). No cleanup runs and none runs on; a third signal changes
nothing. A main flow stuck where no boundary comes (a builtin's own
loop, a stalled write) is adopted by the backstop: 1s after the first
signal (the terminal ^C's grace: once no child has lived for 1s), unless
the main thread runs a cleanup, closes a scope, joins arms or is already
unwinding, the backstop claims the main flow's pending frames, runs them
innermost first in shields, and dies by the signal; the main thread, if
it returns, parks at its next claim. Rests on [D:hard-exit],
[D:signal-teardown], [D:teardown-joins-arms], [D:teardown-never-preempts],
[D:terminal-sigint-teardown], [D:scope-tree]. Enforced by `tearingDown`,
`hardExit` (`hardExiting`, `park`), `startBackstop`, `backstopVerdict`,
`adoptMain`.

**I13. A REPL ^C cuts what the entry waits on, never pure computation.**
One entry, however many statements, is one evaluation: the ^C cuts its
node, so its flows stop and its remaining statements never run; its
first failure stops the rest as well. Waits inside a cleanup honour only
a second ^C. A spawn is also a ^C boundary, but a builtin call and an
evaluated node are not. A serve handler the ^C cuts closes its
connection unanswered (`Serve.abandon`) and records nothing on
`Server.streamErrors`. Tty REPL only: a piped REPL has no
`interruptRepl`. Rests on [D:repl-interrupt], [D:repl-interrupt-fanout],
[D:repl-isig]. Enforced by `interruptRepl`, `checkInterrupted`,
`beginReplEval`/`endReplEval` (nested: an entry's), and the REPL's
multi-statement fold.

**I14. The platform split.** On Linux, FIFO and unseekable `File.read`
opens non-blocking and polls beside the cut, so the cut closes the fd
([D:fifo-read-polled], `openPolled`, `PolledFd`); on macOS a FIFO's
open and read run off the flow (`cutBlocking`), and an abandoned read
may take one later write. A child's pipe is polled before each fill on
POSIX; on Windows the fill waits beside the token. The tree walk is
Linux-only (I9). On Windows, Ctrl+C (`Console.CancelKeyPress`) is the
same teardown — the main flow unwinds, a second Ctrl+C hard-exits —
with no terminal-group rule, no EPIPE teardown and no death by signal:
weir exits as the console ends a process ([D:signal-teardown],
[D:windows-v1], [D:scope-tree]).

**I15. Non-claims.** `kill -9` of weir runs nothing; only a held lock is
released, by the kernel ([D:scoped-procs], [D:lock-advisory]). The tree
kill is SIGKILL, so a child's own cleanup never runs. A descendant that
was reparented away (`(cmd &)`, a daemon) is out of reach
([D:tree-kill-walk]). An inherited SIG_IGN is honoured: SIGINT stays
unbound when there is a controlling tty, so a pid-directed `kill -INT`
is dropped too. A shell ignores SIGINT for a background command only
where job control is off (a script, `sh -c '… &'`, `set +m`); an
interactive shell's `&` and `nohup … &` start weir with SIGINT live
([D:sigint-ignored-jobcontrol]); SIGTERM always works, and SIGHUP does unless it was
ignored too ([D:signal-teardown], [D:sigint-reaps],
[D:sigint-ignored-tty]). An inherited SIG_IGN SIGINT without a tty is
reset at start. An ignored SIGHUP always stays unbound
([D:sighup-teardown]).

**I16. A cleanup's commands at a terminal run in their own process
group.** When weir's group is the terminal's foreground group, a command
a cleanup starts goes through weir's exec shim: it moves into a new
group, ignores SIGTTIN/SIGTTOU (a terminal read gets EIO, never a stop),
takes /dev/null for a terminal stdin, and execs the command, so the pid
weir holds is the command's. The terminal signals are blocked on the
starting thread, so one landing before the shim left weir's group stays
pending in it and is discarded. A terminal ^C, ^\ or ^Z cannot reach the
command; its output still shows; a second signal kills it (I12). On
Windows the command starts in a new process group, which Ctrl+C does
not reach. Rests on [D:cleanup-pgrp]. Enforced by `cleanupGroupShim`,
`Pgrp.blockedForStart`, `Pgrp.execInNewGroup` (the `--weir-exec-pgrp`
entry in `Program.main`), `Proc.spawn`.

**I17. A signal's exit is a death by the signal.** After the unwind (the
flow's, the backstop's or the hard exit's), INT/TERM/HUP end weir by the
signal itself, scoped or not, so a calling shell sees the signal (`$?` is
128+n, and bash's wait-and-cooperative-exit stops its loop). The handler
.NET installed is replaced by the default first; the terminal mode the
REPL set is restored. A closed stdout's 141 is an exit. Rests on
[D:die-by-signal]. Enforced by `dieBy` (`exitCodeOr`, `startBackstop`,
`hardExit`, `onSignal` with no flow to unwind).

## Interactions

| case | outcome (the rule that wins) |
|---|---|
| teardown while a pfirst loser runs its cleanup, after the winner returned | I6: the cleanup runs whole and its commands are spared; `waitTask` joins it (I4); then `ExitRequest` |
| teardown while the main thread is in a cleanup, or between a body and its cleanup | I6 over I1: the cleanup's node is a shield, so `checkTeardown` raises at the first node after it |
| teardown while an arm is in a builtin's own loop | I4: the join holds and the backstop stands down (`mainJoining`); only a second signal ends it (I12) |
| teardown while every arm waits on a pipe an orphan holds | I2: the reads are cut; the arms run their own cleanups and join; the main flow unwinds |
| script signal while `within serve` is open or closing | the close still drains: the handlers are cut by the teardown at once (I2), the drain lasts at most 2s, then the cut and the uncapped join (I4); a signal landing during the close holds the backstop until the close ends (I6) |
| `exit n` in a serve handler | the body and every handler are cut, their cleanups whole; the scope closes; then `exit n` (I2, I4) |
| REPL ^C while `within serve` drains | the drain is not cut (bounded, 2s); handlers' waits raise `Interrupted`, and the handler ends unanswered, with nothing on `Server.streamErrors` (I13); the join is uncapped |
| race cut, teardown and REPL ^C on one wait | `raiseWoken`: `checkTeardown` (main), then `Cancelled`, then `Interrupted` |
| `exit n` in a race arm | ends the race with that raise; losers are cut and joined |
| `exit n` in a pmap/piter arm | stops the fan-out as a race win does: no new arm, the in-flight arms cut (their children killed) and joined, cleanups whole; then `exit n`, never masked by an arm error (I5) |
| `pfirst` or `within serve` inside a cleanup | a node under the shield: the scope cuts its own losers and late handlers, children killed (I2, I3); a first signal or REPL ^C does not reach them and a second does (I6) |
| terminal ^C while a `tmp`/`proc`/`serve`/`always` scope is open | a teardown that kills; the main flow unwinds, weir dies by the SIGINT (I11, I17) |
| terminal ^C during an `always` running a command | I16: the command never sees it; the cleanup ends whole, then the unwind and the SIGINT's death |
| terminal ^C after every scope has closed | the soft teardown: no kill, wait for the child, die by the SIGINT (I11) |
| second signal while a cleanup runs and an outer one is pending | I12: weir dies at once by the first signal; neither cleanup runs on |
| closed stdout while an arm waits on a command | the EPIPE's kill ends the command at once; the arm raises and is joined, then `ExitRequest 141` (I3, I4) |

## Known residuals

- A builtin's own loop holds a race, a serve close and a teardown join
  until it returns ([D:pfirst-join], [D:serve-cut-joins-cleanup],
  [D:teardown-joins-arms]); so does a write to a stalled stdout or a
  hung filesystem call in an arm. A second signal ends it.
- stdin ([D:prompt-cut-whole-line]): a line a cut finished, or one
  finished for a child, stays in weir's buffer, so the child does not
  see it. The stream reads buffer ahead in 64KiB chunks. Typeahead that
  the REPL's tty editor already pulled in is unseen by `Self.prompt`.
- A `kill -INT` aimed at a weir that is the terminal's foreground group
  looks the same as the terminal's ^C, so nothing is killed
  ([D:sigint-terminal-group]).
- A terminal ^C at no live scope waits for a child that ignores it;
  only a second ^C ends that wait ([D:terminal-sigint-teardown]).
- A cleanup's command at a terminal cannot read it (I16), and its
  descendants inherit SIGTTIN/SIGTTOU ignored ([D:cleanup-pgrp]).
- A hard exit leaves a running cleanup where it stood (I12): a cleanup
  that must finish needs one signal, not two.

## Discrepancies

Places where the code and a row or the SKILL disagree, as of this
page. Each needs a ruling, either a code fix or a reworded row. A
resolved entry keeps its number and names its row.

1. Resolved: a race won under a teardown joins its losers
   ([D:pfirst-join], [D:teardown-joins-arms]).
2. Resolved: a race, fan-out or serve opened inside a cleanup cuts its
   own flows ([D:cleanup-owns-its-cuts]).
3. Resolved: a signal during the serve close holds the backstop
   ([D:serve-drain-signal]).
4. Resolved: the terminal ^C's teardown has the second-signal escape,
   its grace is held as the backstop is, and its death settles the
   kills ([D:terminal-sigint-teardown]).
5. Resolved: a closed stdout kills the children at the teardown, which
   ends the foreground waits ([D:epipe-kills-at-teardown]).
6. Resolved: the terminal ^C rule follows live scopes
   ([D:sigint-live-scopes]).
7. Resolved: the carve-out says send SIGTERM, or SIGHUP
   ([D:sigint-ignored-tty]).
8. Resolved, then superseded: no cleanup runs on the signal thread; a
   stuck main flow's cleanups run on the backstop's, after it adopted
   the flow ([D:scope-tree]).
9. Resolved, then superseded: no arm is abandoned ([D:scope-tree]).
10. Resolved: `exit n` in a pmap/piter arm stops the fan-out and is
    never masked ([D:fanout-exit]).
11. Resolved: a REPL ^C in a serve handler closes the connection
    unanswered and records nothing ([D:repl-interrupt]).
12. Resolved: a script's signal still drains serve
    ([D:serve-drain-signal]).
