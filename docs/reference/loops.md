# Loops and parallelism

There is no `while`. The loops:

| loop | runs | ends |
|---|---|---|
| `for` over a seq | once per item | at the end of the seq |
| `pfor`, `Seq.pmap`, `Seq.piter` | once per item, in parallel | when every item is done |
| `retry` | until success | after a number of attempts |
| `poll` | until ready | after a time limit |
| `repeat` | every interval | when the program is asked to stop; the iteration in hand finishes and the code after the loop runs |

A `for` over `Seq.initInfinite` also runs until a signal, but the
signal ends it by teardown: the iteration is cut wherever it is. For
a long-running loop (a watcher, a publisher, a subscriber), use
`repeat`.

To keep a server or sidecar up with no loop at all, hold the scope:
`within … until stopped` ([scopes](scopes.md#until-stopped)).

## `retry` and `poll`

Both take `key=value` options after the keyword, a block whose last
statement is its value, and an optional `until` section that binds
that value and tests it. If the block's value is a `bool`, that is the
success condition, and the form returns unit:

```weir
retry attempts=3 delay=100ms
    weir -e "print 1" | succeeds
```

To keep the successful attempt's output, yield a value and bind it
in `until`:

```weir
let out = retry attempts=3 delay=100ms
    let r = weir -e "print 42" | complete
    r
until r
    r.exitCode == 0

print (out.stdout |> Seq.head)
```

`retry` also takes a total-time ceiling, `timeout=(Some 2m)` (an
`Option<Duration>`, default `None`). When it passes, the retry fails
without waiting out the pending delay.

`poll timeout=5m interval=10s` has the same form, limited by time: the
loop for waiting until something is ready. `watch=<proc handle>` makes
it fail at once if the watched child dies.

When attempts or time run out, the error states the attempts made and
the time elapsed.

The options are an ordinary record, so you can compute and share them:
`let fast = { Retry.defaults with attempts = 3 }`, then `retry fast`.

## `repeat`

`repeat interval=1s` runs its block, waits the interval, and runs it
again until the program is asked to stop. It's the loop for a watcher,
a publisher or a subscriber.

```weir-demo
let sub handle =
    repeat interval=1s
        for m in newMessages () do
            handle m
        saveCursor ()
    print "subscriber stopped"
```

The first stop signal (SIGINT, SIGTERM or SIGHUP; Ctrl+C on Windows)
is a request, not a cut:

- the iteration in hand runs to its end
- a pending `interval` wait ends at once
- no new iteration starts
- `repeat` returns, the code after it runs, and the program exits 0

A second signal is the usual teardown: the iteration is cut, `always`
blocks run, weir exits 130/143/129. So Ctrl+C twice still stops a
stuck iteration.

Details:

- Waits inside the block (`Duration.sleep`, `poll`, `retry`) are the
  iteration's work and the request doesn't end them. Only the
  `interval` wait ends.
- A command running in the iteration keeps running: `kill -TERM` of
  weir reaches weir alone, and the command finishes. A terminal Ctrl+C
  reaches the command too, as in bash; if it dies of it, that's the
  stop, not an error, and the loop ends there.
- The request is process-wide and stays set. Every `repeat` in a
  `Seq.piter`/`pmap` arm sees it and returns, and the fan-out completes
  normally. A `repeat` that starts after the request returns at once
  without running its block. At the prompt the request ends with the
  entry, so the next `repeat` you type runs.
- `interval=` is required and must be positive.

The other ways out mirror `poll`. A `bool` block ends the loop when
true; `until r` binds a value block and returns the value that passed;
`timeout=(Some 5m)` raises when time is up:

```weir
let n = repeat interval=10ms
    1 + 1
until r
    r == 2

repeat interval=10ms
    print $"got {n}"
    true
```

`repeat` is refused inside `plan` (a dry run can't honour a loop only
a signal ends). It takes no `until stopped`: it already ends when
stopped. To hold a scope open with no loop, end the `within` with
`until stopped` ([scopes](scopes.md#until-stopped)).

## `Seq.pmap` / `Seq.piter`

These run a function over a seq in parallel. Results come back in
input order and every item is processed. If any fail, the earliest
item's error (in input order) is raised once all have finished:

```weir
[30; 10; 20] |> Seq.pmap (fun ms -> ms * 2) |> Seq.freeze |> Seq.map show |> print
```

Each worker gets its own copy of the session: a `cd` or env change in
one affects only that worker and is gone afterwards.

At most 64 run at once. The limit protects system resources rather
than matching CPU count, since the work is usually I/O-bound.
`Seq.pmapWith` / `Seq.piterWith` set it explicitly; below 1 is an
error:

```weir-error
[1] |> Seq.pmapWith 0 (fun x -> x) |> Seq.freeze // degree must be >= 1
```

## `pfor`

`pfor x in xs do body` is `xs |> Seq.piter (fun x -> body)` written as
a loop. The binder takes the same patterns as `for`; the body is an
indented block (or the rest of the line) run once per item, commands
included:

```weir
pfor n in [3; 1; 2] do
    Duration.sleep (Duration.ms (n * 10))
    print $"slept {n}"
```

Everything else is `Seq.piter`'s:

- The body must be unit. Arms finish in no particular order.
- Every arm runs even if one fails; the earliest item's error (in
  input order) is raised once all have finished.
- At most 64 arms at once, 8 inside another fan-out's arm, one at a
  time deeper than that. There's no ceiling syntax: use
  `xs |> Seq.piterWith n (fun x -> …)` to choose.
- Refused inside `plan`.
- On Ctrl+C or SIGTERM running arms stop and are cleaned up the same
  way.

Arms write straight to weir's stdout as they run, so output from
different arms interleaves. A command in an arm gets the terminal when
weir has one; when weir's output is redirected, its lines pass through
whole. For tidy output, capture each arm with `| complete` and print
it in one go, or collect results with `Seq.pmap` and print at the end.

`pfor` runs effects only. To collect a value per item, use `Seq.pmap`;
`pfor x in xs -> e` and a `yield` body are refused with that pointer:

```weir-error
pfor x in [1; 2] -> x * 2 // collects nothing: use Seq.pmap
```

There is no async/await, and there won't be: concurrency comes from
processes and pipelines. A task that really needs async belongs in
full F#.

## Background processes

`within proc h = cmd` runs a background process for the length of a
block. The process and its children are killed however the block
exits.

- `poll watch=h` catches a process that crashes on startup.
- The child's output goes to files; `Proc.tail` reads the last lines.
- Its exit status is a value from `Proc.wait`, not an error.

The [guide](../GUIDE.md#parallelism) walks through an example with a
running server; the [scopes page](scopes.md) covers scopes.
