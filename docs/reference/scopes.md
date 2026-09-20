# Scopes

`within` holds a resource for a block and releases it on every
exit — normal completion, a raise, `exit n`, SIGINT and SIGTERM, at a
tty AND detached (a `kill -INT`/`kill -TERM` on a `setsid` or
backgrounded weir unwinds the same way, exiting 130/143; a second
signal mid-teardown hard-exits — the double-Ctrl+C escape). Two
carve-outs, both by design: `kill -9` of weir itself (the lock is the
one kind the kernel still releases), and a weir backgrounded in an
interactive shell (`weir … &`/`nohup`, which keeps a controlling
terminal) inherits SIGINT ignored — the job-control nohup convention,
so a terminal Ctrl+C does not reach it; send SIGTERM or `kill -INT`
the pid directly. The block is an ordinary
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
| `within serve s = cfg handler` | an HTTP listener | closes the socket — the port frees |

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
missing path errors before the block runs, naming the absolute
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
body and the cleanup fail, the original error propagates and the
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
process tree is killed and reaped. Scoped children release
last-in-first-out; a child's own exit is data (`Proc.wait`), not a
raise. The full teaching — `watch=`, spill files, `Proc.tail` —
lives in the [guide](../GUIDE.md#parallelism). A process that must
outlive the script is a daemon and belongs to systemd; weir has no
`nohup`.

## `serve`

An HTTP listener held for the block. `within serve srv = { port =
8080; maxConcurrent = 4 } handler` opens the socket on loopback, runs
`handler` — a plain synchronous `HttpServerRequest ->
HttpServerResponse` function — once per request, and closes the socket
at every block exit (normal, raise, SIGINT, SIGTERM), so the port
frees. The scope is the lifetime, exactly as with `proc`; there is no
`stop` member — `Server.port` and `Server.running` are the handle's
whole surface.

The listener accepts the common loopback names beside each other on the
one port — `127.0.0.1`, `localhost`, and `[::1]` — so a client
addressing the server by any of them reaches the handler (the `[::1]`
name is dropped gracefully on a host without IPv6 loopback). It stays a
loopback listener: it never binds all interfaces, so it is not
reachable beyond loopback — put a reverse proxy in front for a public
address.

The handler routes on `req.path` with an ordinary `match` — weir's
union dispatch, not a routing framework:

```
let handler = fun req ->
    match req.path with
    | "/health" -> HttpServerResponse { status = 200; headers = []; body = Text "ok" }
    | _ -> HttpServerResponse { status = 404; headers = []; body = Text "not found" }
```

The response `body` is an `HttpBody`, shared with the `Http` client —
`NoBody`, `Text`, `Json`, or `Stream of seq<string>`. A `Stream` body
is written chunked and flushed per element (SSE-shaped `data:` lines),
so a lazy producer streams incrementally: the client sees early
elements before the sequence completes. `maxConcurrent` bounds how many
handlers run at once (the `Seq.pmapWith` concurrency law); excess
requests queue.

`HttpServerRequest` carries `method`, `path`, `query` (the raw string
without the leading `?` — split it with `Str.trySplitOnce`), `headers`
(pairs, wire order), and `body` (the request text). Out of scope for
v1, each a deliberate non-goal: TLS (put a reverse proxy in front), a
routing DSL (the `match` is the router), WebSockets, request-body
streaming, and HTTP/2.
