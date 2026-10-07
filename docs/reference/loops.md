# Loops and parallelism

There is no `while`, and no unbounded loop in any form. Every loop
has a limit: `retry` (a number of attempts), `poll` (a time limit),
`for` (a seq), and the parallel fan-outs.

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

`poll timeout=5m interval=10s` has the same form but is limited by
time; it is the loop for waiting until something is ready.
`watch=<proc handle>` makes it fail immediately if the watched child
process dies. When attempts or time run out, the error states the
attempts made and the time elapsed. The options are an ordinary
record, so you can compute and share them:
`let fast = { Retry.defaults with attempts = 3 }`, then `retry fast`.

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
