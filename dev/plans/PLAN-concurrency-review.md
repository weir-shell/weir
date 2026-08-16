# weir — adversarial review of the concurrency claims

Status: EXECUTED IN PART. Phase 0 complete; Phase 1 covers 4 of 14 claims with
rates. The 10 untested claims are listed by name below — per the plan, an
untested claim recorded as untested is worth more than one silently assumed to
hold. Nothing fixed; findings-shaped, like its three predecessors.

    ci/concurrency-flake.sh ./path/to/weir [N]

Every probe reports a RATE, never a verdict, and timing is FORCED rather than
awaited.

## Phase 0 — the four answers, before attacking

**0.1 Where the concurrency lives: .NET tasks on dedicated threads, not
processes.** `Builtins.fs:933` `runParallelWith` uses
`Task.Factory.StartNew(worker, TaskCreationOptions.LongRunning)` — a dedicated
OS thread per active worker — with a work-stolen index
(`Interlocked.Increment`), a `Task.WaitAll` join, and errors collected into a
`ConcurrentDictionary<int, exn>` then rethrown as `raise errors[Seq.min
errors.Keys]`. `[D:tasks-underneath]`.

So C2's "first error by INPUT order" is STRUCTURAL — `Seq.min` over the index
keys — not a timing accident. Worth stating plainly because SKILL says
"processes and pipelines are the concurrency model"; that describes the
LANGUAGE SURFACE. `pmap` underneath is threads. The two statements do not
conflict, but a reader reasoning about failure modes from the doc sentence
would reason about the wrong machine.

**0.2 Tree-kill is .NET's, not POSIX process groups.** Every kill site
(`Session.fs:90,101,149`, `Proc.fs:71`) calls `p.Kill true` —
`Process.Kill(entireProcessTree: true)`. Not `killpg`, not job objects
directly; whatever the BCL does per platform. The plan's macOS/Linux
process-group concern therefore lands on .NET's implementation rather than on
weir's, and a claim verified on Linux is NOT verified on macOS.

**0.3 The session fork carries cwd explicitly and env by execution context.**
All three session slots — `localCwd`, `localEnvOverlay`, `raceGroup` — are
`AsyncLocal`, deliberately (`[D:tasks-underneath]`: "AsyncLocal, not
ThreadLocal"). `enterWorker parentCwd` sets ONLY cwd; `exitWorker()` clears
cwd AND env.

The plan asked whether `within env` around a `pmap` reaches the arms. It does,
and by a path worth writing down: a top-level `within env` runs with
`localCwd.Value = None`, so `pushEnvOverlay` writes `rootEnvOverlay` (a plain
ref); a worker's `envOverlay()` finds `localEnvOverlay.Value = None` and falls
back to `rootEnvOverlay`. Inside a worker, `pushEnvOverlay` forks over the
ROOT snapshot, so an arm's own `within env` stays worker-local and
`exitWorker` clears it between items. That is a correct design, and it is NOT
the baked-overlay shape `[D:reifier-env-overlay]` found on the reifier path.

HYPOTHESIS, UNTESTED, recorded with its mechanism: in a NESTED fan-out, an
overlay pushed inside an OUTER worker lives in `localEnvOverlay`. Inner tasks
inherit it by AsyncLocal flow, but the inner `exitWorker()` sets
`localEnvOverlay.Value <- None` between items, so the SECOND and later items
on a given inner worker thread fall back to `rootEnvOverlay` and lose the
outer arm's overlay. Requires: `within env` inside a `pmap` arm, wrapping a
nested `pmap` of ≥2 items per inner worker. Cheap to test; not tested here.

**0.4 There is no shared pool, and nesting MULTIPLIES rather than starves.**
Zero `SemaphoreSlim` in `Builtins.fs`; each `runParallelWith` call creates its
own `min degree (max 1 length)` LongRunning tasks. Measured rather than
inferred — nested `piter`, each innermost arm spawning a 1.31s child:

    4x4  =  16 arms -> 1413ms
    8x8  =  64 arms -> 1408ms
    12x12 = 144 arms -> 1508ms

144 arms finish in the wall time of 16. If the 64 ceiling were shared, 144
arms of 1.31s would need two rounds (≈2.6s). So the ceiling is PER CALL. A
64×64 nest would attempt 4096 concurrent children and ~4160 dedicated threads
— the plan asked about starvation and the answer is its opposite. Whether that
wants a shared ceiling is a design question, not a defect: the cap's stated
purpose (`[D:tasks-underneath]`) is that "an unbounded fan-out over 10k items
is not a well-mannered fork bomb", and nesting reopens exactly that.

## Phase 1 — measured, with rates

Machine: 12 cores, Linux 7.0.10, load 6.2–9.8 across runs (contended, which
strengthens rather than weakens these). Probes written FROM THE DOCS.

| claim | skew pattern | rate |
|---|---|---|
| C1 `pmap` returns input order | item 1 sleeps longest — time order is the exact reverse of input order | **200/200** |
| C2/C6 first error by INPUT order | arm 1 fails at 250ms, arm 5 fails immediately | **200/200** |
| C5 `pfirst` loser tree-kill, incl. GRANDCHILD | loser spawns `sh -c "sleep & sleep"`, winner returns at 60ms | **0/30 leaked** |
| 0.4 nested ceiling | 12×12 nested `piter` | 144 concurrent, no residue |

Inverted controls (the assertion must be able to fail): C1 inverted 0/20, C2
inverted 0/20. Ledger control: a deliberately backgrounded child IS seen, so
the orphan zeros are measurements rather than blindness.

**No finding.** The three claims tested at N=200 held under schedules chosen
to break them.

## Untested claims — recorded as untested

C3 (the 64 ceiling on a single call, and `pmapWith` moving it — only the
NESTED question was measured); C4 (concurrent `cd` in two arms, and the
documented force-inside-the-worker repair); C7/C9 (`within proc` teardown at
every exit, and LIFO order asserted directly rather than inferred); C8
(SIGINT/SIGTERM mid-scope, mid-fan-out, and during teardown; the second signal
during handling; the `kill -9` residue including spill directories); C10/C11
(exit-as-data, `watch=` failing immediately with last output); C12 (escaped
handle answering gracefully post-kill); C13 (spill files never reaching the
parent's stdout); C14 (TTY contention between two concurrent arms); output
interleaving / torn lines, which no claim currently covers; and 0.3's nested
env-overlay hypothesis.

The plan's flagged REPL/script SIGINT split is untested in both paths, so the
observation that the REPL does not forward SIGINT to a foreground child
remains unconfirmed here and is neither corroborated nor refuted by this
review.

## Denominator — what held, with N

- **C1 input-order: 200/200** under reversed time order, load 6.2.
- **C2/C6 first-error-by-input-order: 200/200** under a schedule where the
  input-first arm fails 250ms after the input-last arm. Structural in the
  implementation (`Seq.min` over error keys), which is why it does not flake.
- **C5 grandchild tree-kill: 0/30 leaks**, ledger positive-controlled.
- **No process residue** after any batch, including the 144-arm nested run.

## Instrument honesty

**The machine.** 12 cores, Linux, load 6.2–9.8. A 200-iteration pass on a
contended box is better evidence than an idle one, but this is ONE platform:
0.2 established that tree-kill is the BCL's per-platform implementation, so
none of these rates transfer to macOS or Windows. This is the review where
that matters most and it is the review's largest gap.

**Probes were written from the docs**, so they can in principle catch the docs
and the binary disagreeing. They found no such case in the four claims tested.

**N is stated everywhere.** C5 ran at N=30 rather than 200 because each
iteration spawns and reaps a process tree; that is a weaker number than C1's
and is labelled as such.

**The instrument was, again, the least trustworthy thing in the review.**
Three separate self-match failures, all the genus `dev/PROCESS.md` already
documents ("count processes by name, never by a pattern the measuring command
also carries" — a rule whose origin note even uses the word "self-kills"):

1. `pgrep -fc "sleep 3133"` reported **1 at baseline** — it matched the shell
   running it.
2. `ps -eo args= -C sleep` **silently ignored `-C`** on this box and listed
   every process, so two DISTINCT markers both "matched" 4 lines — the
   harness's own argv contained both.
3. `pkill -f "sleep 2.31341"` **killed the harness shell** (exit 143). This
   surfaced as two runs producing no output at all, and one phantom "11
   surviving processes" that was nearly written up as a weir orphan leak.

A fourth, different: the first nested probe used `sleep 31341` — 8.7 hours —
so the fan-out could never join, and the timeout killed the harness rather
than weir. The residue that produced was mine, not weir's.

All four were caught before they reached a finding, but only because the
numbers looked wrong, not because a control fired. The fix is structural:
`ledger()` and `reap()` filter on `comm` via awk, so the measuring shell is
excluded by construction. The header of `ci/concurrency-flake.sh` records this
so the next editor does not reintroduce `pgrep -f`.

**This is the fourth consecutive review whose instrument was defective before
its subject was.** The security review's oracle control borrowed a finding's
payload; the DX census counted a cascading error as a teach; the upgrade
corpus used post-window surface and reported a clean zero; this one counted
and then killed itself. In every case the instrument's failure mode was
toward a PASS. That is a pattern about how these harnesses get written, not
four coincidences, and it is the strongest argument in the series for the
positive-control discipline the plans keep demanding.
