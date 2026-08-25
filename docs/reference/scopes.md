# Scopes

`within` holds a resource for a block and releases it on EVERY
exit — normal completion, a raise, `exit n`, SIGINT and SIGTERM.
`kill -9` of weir itself is the one exception (the lock is the one
kind the kernel still releases). The block is an ordinary
expression block: statements run, the last expression is the value;
statement position works too.

| form | holds | on every exit |
|---|---|---|
| `within tmp d` | a fresh directory | removes it |
| `within cd "path"` | the working directory | restores it |
| `within env vars` | an env overlay for child spawns | drops it |
| `within` … `always` | nothing — a body plus cleanup | runs the `always` block |
| `within lock "path"` | an advisory file lock | releases it — the kernel does, even on `kill -9` |
| `within proc h = cmd` | a background process | kills and reaps its tree |

## `tmp`

Binds a fresh directory; the exit tolerates a block that already
removed its own directory:

```weir
let digest = within tmp d
    ["payload"] |> File.write $"{d}/f.txt"
    Str.sha256 (File.read $"{d}/f.txt" |> Str.join "-")

print (Str.sub 0 12 digest)
```

## `cd`

Runs its block in the directory and restores on every exit. A
missing path errors BEFORE the block runs, naming the absolute
path.

## `env`

Overlays child spawns for the block — weir's own env is untouched
(`Env.get` does not see the overlay). Nested overlays compose,
inner keys winning; an explicit sigil env (`$e(...)`) wins over
ambient layers:

```weir
let vars = [Env.pair "GREETING" "scoped"]

within env vars
    sh -c "echo child sees $GREETING"

print (Env.get "GREETING" |> Option.defaultValue "parent stays clean")
```

## Bare `within` and `always`

Holds nothing; the `always` block runs on every exit. When both the
body and the cleanup fail, the ORIGINAL error propagates and the
cleanup's failure goes to stderr with a marker; teardown continues
outward:

```weir
within tmp d
    within lock $"{d}/demo.lock" timeout=10s
        within
            print "one holder at a time"
        always
            print "released either way"
```

## `lock`

An advisory file lock: blocking by default, `timeout=30s` raises on
exhaustion, safe across processes and `pmap` arms alike.

## `proc`

Binds a handle to a background process; at every block exit the
process TREE is killed and reaped. Scoped children release
last-in-first-out; a child's own exit is data (`Proc.wait`), not a
raise. The full teaching — `watch=`, spill files, `Proc.tail` —
lives in the [guide](../GUIDE.md#parallelism). A process that must
outlive the script is a daemon and belongs to systemd; weir has no
`nohup`.
