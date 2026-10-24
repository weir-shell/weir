# PLAN: `repeat` — the loop a stop signal ends

**Status: RULED** (user, 2026-10-23) — every decision took the recommendation:
D1 graceful then cut in the REPL; D2 exit 0; D3 a stop-caused failure is the stop;
D4 sticky; D5 mirror `poll`; D6 inner waits not ended; D7 `repeat interval=…`;
D8 `Self.waitForStop ()` — **superseded** (user, 2026-10-24): the explicit
wait is a `within … until stopped` section, and `Self.waitForStop` is removed.

## The receipt

A pub/sub pair over a JSON-lines file (user, 2026-10-23): a publisher
appends a message every second, a subscriber reads what is new, handles
it and records a cursor. Both run until the operator stops them.

```weir
[ pub; fun () -> sub handle ] |> Seq.piter (fun f -> f ())
```

Today there are two spellings, both wrong:

- `for n in [1..900] do` — pretends to be bounded. The bound is a guess,
  and the loop ends for no reason the program cares about.
- `for _ in Seq.initInfinite id do` — runs forever, but a stop is a
  **teardown**: the iteration is cut wherever it is (mid-handler, before
  the cursor is written), `always` cleanups run, and weir exits 143/130
  ([D:signal-main-unwind]). The program gets no chance to finish the
  message in hand.

The loops reference also claims "There is no `while`, and no unbounded
loop in any form" — the second spelling already contradicts it.

What a watcher needs is a **graceful stop**: stop taking new work, finish
the work in hand, run the code after the loop, exit cleanly. A second
signal must still stop a stuck loop the hard way.

## The shape

`repeat` joins the `retry`/`poll` family: one form, `key=value` options
after the keyword, a block, an optional `until` section.

```weir
let sub f =
    repeat interval=1s
        let ms = File.read C.topic |> from jsonl Message |> Seq.freeze
        for u in ms |> Seq.where (fun m -> m.at > lastSeen ()) do
            f u
        ...
    print "subscriber stopped"
```

- Runs the block, waits `interval`, runs it again.
- Ends when:
  1. a **stop signal** arrives (SIGINT, SIGTERM, SIGHUP — the set that
     tears down today, [D:sighup-teardown]), or
  2. the optional `until` section's condition holds (as in `poll`), or
  3. the optional `timeout=` passes (D5).
- Returns `unit` (or the `until` binding's value, as `poll` does — D5).

## Semantics

### The first signal is a stop request

While a `repeat` is running, the first stop signal does **not** start
the teardown. It sets a stop request:

- the pending `interval` wait ends at once;
- no new iteration starts;
- the iteration in flight runs to its end;
- `repeat` returns, and the statements after it run;
- the program then exits normally (exit code: D2).

### The second signal is today's teardown

A second stop signal (or the first one when no `repeat` is running) is
exactly today's path: the handler flags cancellation, the foreground
reaper and fan-out kills act ([D:sigint-reaps], [D:sigint-live-scopes]),
the main thread unwinds through every `always`, weir exits 130/143/129.
So a stuck iteration is always killable: Ctrl+C, then Ctrl+C.

### Child processes

- **Terminal ^C** reaches the whole foreground group
  ([D:sigint-terminal-group]): a command running in the iteration gets
  its own ^C, as under bash. If it dies, the iteration sees the failure
  as any command failure (D3: does a command failure caused by the
  graceful-stop ^C abort the iteration?).
- **`kill -TERM <weir pid>`** reaches weir only: the reaper must NOT
  tree-kill the foreground command or fan-out arms on a stop request,
  so the current command finishes. This is the main mechanical change:
  [D:sigint-reaps]/the TERM reaper act only on the teardown path.

### Parallel arms

A stop request is process-wide. Every `repeat` running in any arm
(`Seq.piter`, `pmap`, `pfor`) sees it, finishes its iteration and
returns; the fan-out then completes normally. In the receipt, `pub` and
`sub` both stop gracefully and the program exits.

A `repeat` nested inside another `repeat` (or inside a fan-out inside a
`repeat`) stops the same way; the outer one stops after the inner
returns, because the request is still set when it checks.

### What "graceful" does not cover

- Code after the last `repeat` returns runs with the stop request still
  set. A later `repeat` returns immediately without running its block
  (D4).
- `Duration.sleep`, `poll`, `retry` waits inside the iteration are not
  ended by the stop request — only `repeat`'s own `interval` wait is.
  (D6: should they be?)

## `until stopped` — the held scope (servers, sidecars)

Ruled by the user, 2026-10-24, superseding D8's `Self.waitForStop ()`
[D:held-scope].

`repeat` observes the stop request implicitly: its own interval wait is
stop-aware. A scope that should simply be held until the operator stops
the program — a `within serve`, a `within proc` sidecar — says so with
a section after its block:

```weir
within serve srv = { port = 8080; maxConcurrent = 4 } handler
    print $"listening on {Server.port srv}"
until stopped

print "server stopped"
```

- Any `within` kind takes it: serve, proc, tmp, cd, env, lock, bare. The
  block runs; then the scope is HELD until the stop request; then it
  closes by its NORMAL path — `within serve` gets the normal close drain
  (2s together, then the cut, cleanups joined whole — [D:serve-cut],
  [D:serve-cut-joins-cleanup]), not the signal path's immediate cut
  ([D:serve-drain-signal]); `within proc` its tree-kill and spill
  removal; `always` cleanups run whole. Then the statements after it
  run, and the exit is 0 (D2).
- While a scope is held the process is stop-aware: it counts in
  `stopAwareDepth`, like a `repeat`. The first signal is the request, the
  second today's teardown. A signal during the block itself is the
  teardown — only the hold listens.
- With the stop already requested (sticky, D4) when the block finishes,
  the hold returns at once.
- The REPL: the `repl-stop-request` rules apply unchanged.
- **Order with `always`:** `until stopped` comes before `always` — the
  scope holds, then the cleanup runs, then the release:

  ```weir
  within
      kubectl scale deploy/worker --replicas=1
  until stopped
  always
      kubectl scale deploy/worker --replicas=0
  ```

  `always` after `until stopped` works on every kind ([D:within-always-any]);
  `until stopped` after `always` is a parse error teaching the order.
  The bare `within` with `until stopped` needs no `always`.
- **Grammar:** `stopped` is a contextual word after a within's `until`,
  not a value and not reserved. The loops' `until <binder>` stays as is.
  `within … until <anything but stopped>` is a parse error teaching
  `until stopped`; `until stopped` takes no block. `repeat … until
  stopped` is refused ("a repeat already ends when stopped — drop
  `until stopped`; use `until x` for a condition").
- The parse desugar: `within K b` + BODY + `until stopped` [+ `always` C]
  ≡ `within K b` + (held BODY [always C]) — one new node (`EHold`), the
  [D:within-always-any] desugar otherwise unchanged.
- `within serve` itself is unchanged: the server lives exactly as long as
  the scope. No blocking variant of `serve` — one scope rule, and the
  same section serves any scope.
- Refused in `plan` (a dry run cannot wait for a signal) and `pure`;
  `readonly` admits it.

## Checker and parser

- New compound form `repeat`, parsed beside `retry`/`poll` (same
  options-record desugar: a `Repeat` prelude options record with
  `interval: Duration` required, `timeout: Option<Duration>` default
  `None`). The key=value machinery, hover and completion of keys come
  from the record, as for `Retry`/`Poll` ([D:hover-residue]).
- `interval` is required: a missing `interval=` is a check error with
  the teaching ("a repeat needs interval= — the wait between
  iterations"). A zero or negative interval is a check error when
  literal, a run-time error otherwise. No hot loop by accident.
- `repeat` is refused inside a `plan` (as the parallel combinators are,
  [D:plan-parallel-refusal]) — a plan's dry run cannot honour a loop
  that only a signal ends. (D7)
- `repeat` becomes a reserved word; tree-sitter grammar + Zed pin +
  VS Code/micro grammars follow (the `pfor` route).

## Runtime

- One process-wide `Session.stopRequested` flag and a count of live
  stop-aware constructs, `repeat`s and held scopes
  (`Session.stopAwareDepth`).
- The signal handler: when `stopAwareDepth > 0` and no stop request is set,
  set it, wake every `repeat` wait (a shared `ManualResetEventSlim` or
  cancellation source the interval wait observes), and return without
  flagging the teardown or reaping. Otherwise the existing path.
- `repeat`'s loop: check the flag before each iteration and after each
  wait; on set, return.
- Windows: `Console.CancelKeyPress` sets the same flag on the first
  Ctrl+C (and cancels the default termination); a second Ctrl+C takes
  the existing path. There is no SIGTERM on Windows.
- REPL: D1.

## Docs

- docs/reference/loops.md: the opening rule becomes "every loop has a
  limit: attempts (`retry`), time (`poll`), a seq (`for`), or a stop
  signal (`repeat`)"; a `repeat` section; a note that `for` over
  `Seq.initInfinite` stops by teardown, not gracefully.
- docs/reference/scopes.md / the signals section: the first-signal rule
  while a `repeat` runs.
- SKILL.md, LEXICON, CHANGELOG, the FAQ (the "forever loop" question).
- DECISIONS rows: `repeat` (the form), and refinements of
  [D:signal-main-unwind], [D:sigint-reaps], [D:sighup-teardown] for the
  stop-request path.

## Tests

- Unit: parse/check (options, missing interval, plan refusal, `until`),
  the stop-request state machine (first signal → request, second →
  teardown, no repeat → teardown).
- e2e / pty, in the hunt-harness style:
  - TERM to weir mid-iteration: the iteration finishes (a marker written
    after a sleep inside it), the code after `repeat` runs, exit D2;
  - TERM during the interval wait: returns at once (bounded wall time);
  - two TERMs: today's teardown, `always` runs, exit 143;
  - terminal ^C at a pty with a child running in the iteration;
  - the receipt's two-arm `Seq.piter`: both arms stop, both after-lines
    print, exit D2;
  - `within serve` + `until stopped`: a request in flight when TERM
    lands still gets its response (normal drain), the line after the
    scope prints, exit D2; a second TERM cuts as today;
  - TERM with a `kill -TERM` to weir only while a `sleep 3` command runs
    in the iteration: the command is NOT killed, it completes;
  - Windows: Ctrl+C equivalent where the harness can drive it.
- REPL storms unchanged (if D1 keeps the REPL as today).

## Decisions to rule

- **D1 — the REPL.** A `repeat` typed at the prompt: does the first ^C
  stop it gracefully (finish the iteration, return to the prompt), or
  cut it as ^C cuts any entry today ([D:repl-interrupt])? *Recommend:
  graceful, then a second ^C cuts* — the same rule as scripts.
- **D2 — exit code after a graceful stop.** 0 (most daemons exit 0 on a
  handled SIGTERM; systemd treats it as success), or 128+signal so a
  caller sees it was signal-initiated. *Recommend: 0* — the program
  handled the signal; a caller that needs to know can check its own
  logs. Code after `repeat` may still `exit n`.
- **D3 — a command the ^C killed.** At a terminal, ^C also reaches the
  iteration's child, which dies 130 and raises a command failure inside
  the iteration. Does that failure (a) propagate as usual — the
  iteration aborts, `repeat` raises, the program fails — or (b) get
  absorbed because a stop was requested, ending the loop cleanly?
  *Recommend: (b)* — a failure whose cause is the stop request is the
  stop, not an error; any other failure propagates.
- **D4 — after the stop.** Is the stop request one-shot (cleared when
  the outermost `repeat` returns, so a later `repeat` runs) or sticky
  for the rest of the process? *Recommend: sticky* — the operator asked
  the program to stop; a second loop starting afterwards would ignore
  that.
- **D5 — options.** `interval=` (required), `until` (as `poll`),
  `timeout=` (as `poll`). Does `repeat` return the `until` value like
  `poll`, and does a `timeout` raise like `poll` or return quietly?
  *Recommend:* mirror `poll` exactly for `until`/`timeout`; a stop
  signal returns quietly (unit, or the last iteration's value when
  `until` is used — D5a).
- **D6 — waits inside the iteration.** Should a stop request also end a
  `Duration.sleep`/`poll`/`retry` wait inside the current iteration?
  *Recommend: no* — "finish the iteration" means the iteration's own
  waits are part of its work; the second signal is there for a stuck one.
- **D8 — the explicit wait's name.** *Superseded (user, 2026-10-24):
  a `within … until stopped` section, see above.* `Self.waitForStop ()`,
  `Signal.wait ()`, `Self.awaitStop ()`. *Recommend: `Self.waitForStop`*
  — `Self` holds the process facts; no new module for one member.
- **D7 — the name.** `repeat`, `every 1s`, `serve`, `loop`. *Recommend:
  `repeat interval=…`* — fits the `retry`/`poll` key syntax; `serve` is
  taken (`within serve`), `loop` reads as unbounded, `every` breaks the
  key=value shape.

## Order

1. ~~Rulings on D1–D8.~~ Done.
2. Runtime state + handler change behind a unit-tested state machine.
3. The form (parser, checker, options record, plan refusal).
4. e2e/pty battery, Windows cell.
5. Docs, grammar repos.

One branch, the usual gates (unit, Fuzz, AOT e2e, REPL storms, hunt
harnesses — the signal harnesses especially, since the handler changes).
