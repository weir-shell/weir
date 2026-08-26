# Loops and parallelism

There is no `while`, and no unbounded loop of any spelling. The
bounded forms are `retry` (by attempts), `poll` (by time), `for`
(by a seq), and the parallel fan-outs.

## `retry` and `poll`

One shape: a `key=value` head, a block body whose last statement is
the value, and an optional `until` section that binds the value for
the condition. A `bool` body is its own predicate (the form then
yields unit):

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

`poll timeout=5m interval=10s` is the same shape bounded by time —
the wait-for-ready loop; `watch=<proc handle>` fails fast when a
watched child dies. Exhaustion raises naming the attempts and
elapsed time. The options are a record underneath — compute and
share them: `let fast = { Retry.defaults with attempts = 3 }`, then
`retry fast`.

## `Seq.pmap` / `Seq.piter`

Parallel fan-out over a seq: results in INPUT order, every arm
runs, and the first error BY INPUT ORDER rethrows after the join:

```weir
[30; 10; 20] |> Seq.pmap (fun ms -> ms * 2) |> Seq.force |> Seq.map show |> print
```

Workers fork the session — a `cd` or env change inside an arm is
arm-local and gone at the join. The concurrency ceiling is 64
(resource protection, not CPU sizing — arms are I/O-bound by
domain); `Seq.pmapWith` / `Seq.piterWith` set it explicitly, and a
degree below 1 raises naming the constraint:

```weir-error
[1] |> Seq.pmapWith 0 (fun x -> x) |> Seq.force // degree must be >= 1
```

There is no async/await and there never will be — processes and
pipelines are the concurrency model; a task that truly needs async
belongs in full F#.

## Background processes

`within proc h = cmd` scopes a background process: the tree is
killed and reaped at every block exit, `poll watch=h` covers the
crashed-at-startup case, the child's streams spill to files
(`Proc.tail` reads the last lines), and its exit is data
(`Proc.wait`), not a raise. Taught with a running server in the
[guide](../GUIDE.md#parallelism); the scope law on the
[scopes page](scopes.md).
