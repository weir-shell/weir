# Loops and parallelism

There is no `while`. The loops are:

- `for` over a seq, and the parallel fan-outs over one. A seq is
  usually finite; over `Seq.initInfinite` the loop runs until a
  signal, and the signal ends it by teardown: the iteration is cut
  wherever it is.
- `retry`, limited by a number of attempts.
- `poll`, limited by time.
- `repeat`, which runs until the program is asked to stop, and stops
  gracefully: the iteration in hand finishes and the code after the
  loop runs.
- With no loop at all, a held scope: `within … until stopped` keeps a
  server or a sidecar up until the program is asked to stop
  ([scopes](scopes.md#until-stopped)).

For a long-running loop (a watcher, a publisher, a subscriber), use
`repeat`, not `for` over `Seq.initInfinite`. For a scope that only
has to stay open, such as a server, use `until stopped`.

## `retry` and `poll`

Both have the same form: `key=value` options after the keyword, a
block whose last statement is its value, and an optional `until`
section that binds that value and tests it. If the block's value is a
`bool`, that is the success condition, and the whole form returns
unit:

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

`retry` also takes an optional total-time ceiling,
`timeout=(Some 2m)` (an `Option<Duration>`, `None` by default): when
it passes, the retry fails without waiting out the pending delay.

`poll timeout=5m interval=10s` has the same form but is limited by
time; it is the loop for waiting until something is ready.
`watch=<proc handle>` makes it fail immediately if the watched child
process dies. When attempts or time run out, the error states the
attempts made and the time elapsed. The options are an ordinary
record, so you can compute and share them:
`let fast = { Retry.defaults with attempts = 3 }`, then `retry fast`.

## `repeat`

`repeat interval=1s` runs its block, waits the interval, and runs it
again, until the program is asked to stop. It is the loop for a
watcher, a publisher or a subscriber: work that runs until the
operator stops it.

```weir-demo
let sub handle =
    repeat interval=1s
        for m in newMessages () do
            handle m
        saveCursor ()
    print "subscriber stopped"
```

The first stop signal (SIGINT, SIGTERM or SIGHUP; Ctrl+C on Windows)
does not cut the loop. It is a request: the iteration in hand runs to
its end, a pending `interval` wait ends at once, no new iteration
starts, `repeat` returns and the code after it runs. The program then
exits 0. A second signal is the usual teardown (the iteration is cut,
`always` blocks run, weir exits 130/143/129), so a stuck iteration is
still stopped by pressing Ctrl+C twice.

- Waits inside the block (`Duration.sleep`, `poll`, `retry`) are part
  of the iteration's work and are not ended by the request; only the
  `interval` wait is.
- A command running in the iteration keeps running: `kill -TERM` of
  weir reaches weir alone, and the command finishes. A Ctrl+C at a
  terminal reaches the command too, as in bash; if it dies of it, that
  failure is the stop, not an error, and the loop ends there.
- The request is process-wide and stays set. Every `repeat` running in
  a `Seq.piter`/`pmap` arm sees it and returns, and the fan-out
  completes normally. A `repeat` that starts after the request returns
  at once without running its block. (At the prompt, the request ends
  with the entry: the next `repeat` you type runs.)
- `interval=` is required, and must be positive.

The other ways out mirror `poll`. A `bool` block ends the loop when it
is true; `until r` binds a value block and returns the value that
passed; `timeout=(Some 5m)` raises when the time is up:

```weir
let n = repeat interval=10ms
    1 + 1
until r
    r == 2

repeat interval=10ms
    print $"got {n}"
    true
```

`repeat` is refused inside a `plan` (a dry run cannot honour a loop
only a signal ends). A loop over `Seq.initInfinite` also runs until a
signal, but a signal stops it by teardown, cutting the iteration;
`repeat` is the loop that stops gracefully.

To hold a scope open until the operator stops the program, with no
loop, end the `within` with `until stopped`
([scopes](scopes.md#until-stopped)). `repeat` takes no
`until stopped`: it already ends when stopped.

## `Seq.pmap` / `Seq.piter`

These run a function over a seq in parallel. Results come back in
input order, every item is processed, and if any fail, the error from
the earliest item (in input order) is raised once all have finished:

```weir
[30; 10; 20] |> Seq.pmap (fun ms -> ms * 2) |> Seq.freeze |> Seq.map show |> print
```

Each worker gets its own copy of the session, so a `cd` or env change
inside one only affects that worker and is gone afterwards. At most
64 run at once. The limit protects system resources rather than
matching CPU count, since the work is usually I/O-bound.
`Seq.pmapWith` / `Seq.piterWith` set the limit explicitly, and a
value below 1 is an error:

```weir-error
[1] |> Seq.pmapWith 0 (fun x -> x) |> Seq.freeze // degree must be >= 1
```

There is no async/await, and there won't be: concurrency in weir
comes from processes and pipelines. A task that really needs async
belongs in full F#.

## Background processes

`within proc h = cmd` runs a background process for the length of a
block. The process and its children are killed and cleaned up
however the block exits. `poll watch=h` catches a process that
crashes on startup. The child's output goes to files (`Proc.tail`
reads the last lines), and its exit status is a value you get from
`Proc.wait` rather than an error. The
[guide](../GUIDE.md#parallelism) walks through an example with a
running server, and the [scopes page](scopes.md) covers how scopes
work.
