# weir — adversarial review of the concurrency claims

Status: EXECUTED. Phase 0 complete; 13 of 14 claims measured with rates, one
recorded untestable here with its reason. **One finding**, predicted from the
source in Phase 0 and then confirmed with an exact numeric match. Nothing
fixed; findings-shaped, like its three predecessors.

    ci/concurrency-flake.sh ./path/to/weir [N]

Every probe reports a RATE, never a verdict; timing is FORCED rather than
awaited; every section carries a control that must fail.

## Phase 0 — the four answers

**0.1 Tasks on dedicated threads, not processes.** `Builtins.fs:933`
`runParallelWith` uses `Task.Factory.StartNew(worker,
TaskCreationOptions.LongRunning)` — a dedicated OS thread per active worker —
a work-stolen index (`Interlocked.Increment`), a `Task.WaitAll` join, and
errors collected into `ConcurrentDictionary<int, exn>` then rethrown as `raise
errors[Seq.min errors.Keys]`. `[D:tasks-underneath]`.

C2's "first error by INPUT order" is therefore STRUCTURAL — `Seq.min` over
index keys — not a timing accident, which is why it does not flake. Worth
stating plainly: SKILL says "processes and pipelines are the concurrency
model", which describes the LANGUAGE SURFACE; `pmap` underneath is threads. No
contradiction, but a reader reasoning about failure modes from that sentence
reasons about the wrong machine.

**0.2 Tree-kill is the BCL's, not POSIX process groups.** Every kill site
(`Session.fs:90,101,149`, `Proc.fs:71`) calls `p.Kill true`. Corroborated
downstream: C12's escaped handle reports `wait` = **137** (128+9), so the tree
kill lands as SIGKILL. A child that traps SIGTERM cannot survive it — and
equally, nothing gets a chance to clean up. Platform note: these are the BCL's
per-platform semantics, so none of the rates below transfer to macOS/Windows.

**0.3 The session fork carries cwd explicitly and env by execution context.**
`localCwd`, `localEnvOverlay` and `raceGroup` are all `AsyncLocal`
(`[D:tasks-underneath]`: "AsyncLocal, not ThreadLocal"). `enterWorker
parentCwd` sets ONLY cwd; `exitWorker()` clears cwd AND env.

A top-level `within env` around a `pmap` DOES reach the arms, by a path worth
writing down: at top level `localCwd.Value = None`, so `pushEnvOverlay` writes
`rootEnvOverlay`; a worker's `envOverlay()` finds `localEnvOverlay.Value =
None` and falls back to it. Inside a worker, `pushEnvOverlay` forks over the
root snapshot, so an arm's own overlay stays worker-local. That is correct,
and it is NOT the baked-overlay shape `[D:reifier-env-overlay]` found.

**Reading it also produced the review's one finding — F1 below.**

**0.4 No shared pool; nesting MULTIPLIES rather than starves.** Zero
`SemaphoreSlim`; each `runParallelWith` creates its own `min degree (max 1
length)` LongRunning tasks. Measured, nested `piter` with a 1.31s child at each
leaf:

    4x4  =  16 arms -> 1413ms
    8x8  =  64 arms -> 1408ms
    12x12 = 144 arms -> 1508ms

144 arms in the wall time of 16, so all ran concurrently: the ceiling is PER
CALL. A 64×64 nest would attempt 4096 concurrent children and ~4160 dedicated
threads. The plan asked about starvation; the answer is its opposite. Not a
defect — but `[D:tasks-underneath]` gives the cap's purpose as "an unbounded
fan-out over 10k items is not a well-mannered fork bomb", and nesting reopens
precisely that.

## F1 — `within env` is lost across a nested fan-out (silent)

**Predicted from `Session.fs` in Phase 0, then measured.** An overlay pushed
INSIDE a worker lives in `localEnvOverlay`. Inner tasks inherit it by
AsyncLocal flow, but the inner `exitWorker()` sets `localEnvOverlay.Value <-
None` between items — so every item after the FIRST on a given inner worker
thread falls back to `rootEnvOverlay` and loses it.

    [1] |> Seq.piter (fun _ ->
        within env [Env.pair "MARK" "inner"]
            [1..200]
            |> Seq.pmap (fun _ -> $(sh -c "echo [$MARK]") |> Seq.head)
            |> Seq.iter print)

    64 items print [inner]      <- exactly the worker count
    136 items print []          <- the overlay is simply gone

The match is exact: 64 kept = `parallelCeiling`, i.e. precisely one item per
worker thread. With `pmapWith 1` the shape is starker — 1 kept, 5 lost of 6.

CONTROL: the top-level path (`within env` outside any worker, then `pmap`)
keeps the overlay on **20/20** items, so the probe can show success and the
loss is specific to the nested shape.

ROOT CAUSE, and it is an asymmetry rather than an omission:

    enterWorker parentCwd  ->  localCwd.Value <- Some parentCwd   // RESTORES
    exitWorker ()          ->  localCwd.Value <- None
                               localEnvOverlay.Value <- None       // CLEARS

cwd is cleared and then RESTORED on the next item's `enterWorker`; env is
cleared and never restored. Confirmed by the mirror probe: the same nested
shape under a worker-pushed `within cd` keeps the directory on **20/20**
items.

WHY IT MATTERS. It is silent — no error, the variable is just empty — and env
is the channel the language nominates for secrets (`Env.load` with a `Secret`
field is "the main producer", SECURITY.md). A script that wraps a nested
fan-out in `within env` to pass credentials gets arms that run without them,
and the failure surfaces as whatever the child does with an empty variable.

FIX SHAPE: `enterWorker` should capture and restore the parent's env overlay
the way it already does cwd — one more parameter, symmetric with the existing
line.

## Phase 1 — the claims, measured

Machine: 12 cores, Linux 7.0.10, load 6.2–9.8 across runs (contended, which
strengthens these). Probes written FROM THE DOCS.

| claim | how it was forced | result |
|---|---|---|
| C1 `pmap` input order | item 1 sleeps longest — time order reversed | **200/200** |
| C2/C6 first error by input order | arm 1 fails at 250ms, arm 5 immediately | **200/200** |
| C3 ceiling moves with `pmapWith` | 8 arms × 300ms | 2414ms at n=1 vs **314ms** default |
| C4 worker-local `cd` | two arms `cd` elsewhere, each asks the OS | each saw its own; parent's cwd survived the join |
| C5 `pfirst` loser tree-kill (grandchild) | loser spawns `sh -c "sleep & sleep"` | **0/30 leaked** |
| C7 teardown at BLOCK exit | sampled inside the scope, then after it, while weir still runs | **10/10** dead at scope exit |
| C7 teardown on raise | `fail` inside the scope | **10/10** |
| C8 SIGINT mid-scope | signal at 400ms | **10/10** reaped |
| C8 SIGTERM mid-scope | signal at 400ms | **10/10** reaped |
| C8 second signal during handling | two SIGTERMs 20ms apart | **10/10** reaped |
| C8 `kill -9` carve-out | SIGKILL at 400ms | **10/10 leaked — the non-claim holds** |
| C9 LIFO teardown | sampled in the gap between inner and outer close | **10/10** inner dead while outer alive |
| C10 exit is DATA | `Proc.wait` on a child exiting 7 | code 7, no raise |
| C11 `watch=` fails fast with last output | child dies at 300ms under an 8s poll | failed at **1021ms**, message carried `STARTING-MARKER` |
| C12 escaped handle post-kill | handle bound out of the scope | `running false`, `tail []`, `wait 137` |
| C13 spill never reaches parent stdout | child emits 300 lines then sleeps | **10/10** clean |

C7's "dead at scope exit" is measured against a control that the tree was
ALIVE inside the scope (**10/10**), so it separates teardown-at-scope-exit
from teardown-at-process-exit rather than conflating them. C9's LIFO is
asserted DIRECTLY — the inner tree observed dead while the outer is still
running — not inferred from a final state. C13's control reads the spill back
through `Proc.tail` (**100 lines, contains the noise**), so "no noise on
stdout" cannot be satisfied by a child that produced nothing.

## Untested — recorded as untested

**C14, TTY contention.** Two concurrent arms opening `/dev/tty`. Not testable
in this environment: there is no controlling terminal, so the single-arm claim
cannot be established either, let alone the concurrent case the docs do not
cover. It needs a pty harness on an interactive machine.

**The REPL/script SIGINT split.** The plan flagged an observation that the
REPL does not forward SIGINT to a running foreground child. C8 is verified for
the SCRIPT path only; the REPL path is a different code path and is neither
corroborated nor refuted here. Testing it needs the same pty harness as C14,
which is why both are parked together.

## Denominator — what held, with N

Fourteen measurements, none flaked. The strongest are C1 and C2/C6 at
**200/200 under schedules chosen to break them**, and both are structural in
the implementation rather than incidental. The teardown family (C7–C9, C12)
held at 10/10 each with the `kill -9` carve-out behaving exactly as the
non-claim states — which is the best kind of denominator entry, because the
documented limit is as true as the documented guarantee.

## Instrument honesty

**The controls did real work this time.** `kill -9` leaking 10/10 is not just
a finding, it proves the ledger sees survivors in the exact setup where the
other rows report zero. C7's inside-scope sample proves the tree existed
before it was reaped. C13's `Proc.tail` control proves the noise was produced.
The env control proves the overlay probe can show success. Every zero above
has a matching non-zero from the same instrument.

**One platform.** 0.2 established that tree-kill is the BCL's per-platform
implementation, so none of these transfer to macOS or Windows. That is this
review's largest gap and the reason C8's rates should be read as
Linux-specific.

**N is uneven and labelled.** C1/C2 at 200; the teardown family at 10, because
each iteration spawns and reaps a process tree and samples on a clock. Ten is
thin for a concurrency claim and is stated as such.

**The instrument was, again, defective before the subject was.** Three
self-match failures in one session, all the genus `dev/PROCESS.md` documents:
`pgrep -fc` counted its own shell (baseline 1, not 0); `ps -eo args= -C sleep`
silently ignored `-C` and matched the harness's own argv for two distinct
markers; and `pkill -f` **killed the harness shell**, which surfaced as two
silent no-output runs and one phantom orphan count nearly written up as a weir
defect. A fourth, different: the first nested probe slept 8.7 hours per arm,
so the fan-out could never join and the timeout killed the harness rather than
weir. All four failed TOWARD A PASS, and all four were caught because the
numbers looked wrong — not because a control fired. `ledger()` and `reap()`
now filter on `comm` so the measuring shell is excluded by construction, and
the harness header says why.

**Four reviews, four defective instruments.** The security review's oracle
control borrowed a finding's payload; the DX census counted a cascading error
as a teach; the upgrade corpus used post-window surface and reported a clean
zero; this one counted and then killed itself. In every case the failure mode
was toward a pass. That is a property of how these harnesses get written, and
the only reliable defence found so far is the one this review finally applied
throughout: **every zero needs a matching non-zero from the same instrument.**
