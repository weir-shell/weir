# Scopes

`within` holds a resource for the length of a block and releases it
however the block exits: normal completion, an error, `exit n`,
SIGINT or SIGTERM. This works both in a terminal and detached: a
`kill -INT`/`kill -TERM` sent to a `setsid` or backgrounded weir
cleans up the same way, exiting with 130/143. A second signal during
cleanup exits immediately, so pressing Ctrl+C twice gets you
out. There are two exceptions, both by design. `kill -9` of weir
itself skips cleanup (only a lock is still released, by the kernel).
And a weir started in the background from an interactive shell
(`weir … &`/`nohup`, which keeps a controlling terminal) starts with
SIGINT ignored, following the usual job-control convention, so Ctrl+C
in the terminal does not reach it; send it SIGTERM, or `kill -INT`
its pid directly. The block is an ordinary block: its statements run
and the last expression is its value. It also works as a plain
statement.

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

Creates a fresh directory and binds its path. It is fine for the
block to remove the directory itself before it exits:

```weir
let digest = within tmp d
    ["payload"] |> File.write $"{d}/f.txt"
    Str.sha256 (File.read $"{d}/f.txt" |> Str.join "-")

print digest[..11]
```

## `cd`

Runs its block in the given directory and restores the previous one
however the block exits. A missing directory is an error, raised
before the block runs, that shows the absolute path.

## `env`

Adds environment variables for every process started in the block.
weir's own environment is unchanged (`Env.get` does not see the
overlay). Nested overlays combine, with inner values winning, and an
env given directly on a capture (`$e(...)`) wins over any enclosing
`within env`:

```weir
let vars = [Env.pair "GREETING" "scoped"]

within env vars
    sh -c "echo child sees $GREETING"

print (Env.get "GREETING" |> Option.defaultValue "parent stays clean")
```

The variables can also be written right after `within env`, as you
would for one command (`NAME=value cmd`):

```weir
let stage = "prod"
within env STAGE=$stage REGION=eu-1
    sh -c "echo $STAGE in $REGION"
```

A body of one command can go on the same line: `within env A=1 make`,
`within cd "src" make`.

## Bare `within` and `always`

Holds no resource; the `always` block runs however the body exits.
When both the body and the cleanup fail, the body's error is the one
raised, the cleanup's failure is printed to stderr with a marker,
and cleanup of the enclosing scopes continues:

```weir
within tmp d
    within lock $"{d}/demo.lock" timeout=10s
        within
            print "one holder at a time"
        always
            print "released either way"
```

## `always` after any kind

Every kind of `within` can take a trailing `always`. It runs inside
the scope while the resource is still held, and the resource is
released afterwards. So the cleanup still sees the `tmp` directory,
runs in the `cd` directory with the `env` overlay, holds the `lock`,
and finds a `proc` or `serve` still running (they are stopped after
it). The bound name is still in scope:

```weir
within tmp d
    ["report"] |> File.write $"{d}/report.txt"
always
    cp $"{d}/report.txt" report.txt
```

It behaves exactly like a bare `within` … `always` nested inside the
scope, so all the rules above apply.

## `lock`

An advisory file lock. It waits for the lock by default; with
`timeout=30s` it raises an error if the time runs out. It works
across processes and across parallel `pmap` workers.

## `proc`

Starts a background process and binds a handle to it. However the
block exits, the process and its children are killed and cleaned up.
Several scoped processes are stopped in reverse order of starting. A
child's own exit status is a value (`Proc.wait`), not an error. The
[guide](../GUIDE.md#parallelism) covers the details: `watch=`, output
files and `Proc.tail`. A process that must outlive the script is a
daemon and belongs under systemd; weir has no `nohup`.

## `serve`

An HTTP listener held for the block. `within serve srv = { port =
8080; maxConcurrent = 4 } handler` opens the socket on loopback, runs
`handler` — a plain synchronous `HttpServerRequest ->
HttpServerResponse` function — once per request, and closes the socket
however the block exits (normally, by an error, SIGINT, SIGTERM), so
the port is freed. As with `proc`, the server lives exactly as long as
the block, and there is no `stop` function. `Server.port`,
`Server.running` and `Server.streamErrors` are all you can do with
the handle.

The listener answers on the same port for all the common loopback
names — `127.0.0.1`, `localhost` and `[::1]` — so a client using any of
them reaches the handler (`[::1]` is skipped quietly on a host without
IPv6 loopback). It only listens on loopback and never binds all
interfaces, so it can't be reached from other machines; put a reverse
proxy in front of it for a public address.

The handler routes on `req.path` with an ordinary `match`; there is
no routing framework:

```
let handler = fun req ->
    match req.path with
    | "/health" -> HttpServerResponse { status = 200; headers = []; body = Text "ok" }
    | _ -> HttpServerResponse { status = 404; headers = []; body = Text "not found" }
```

The response `body` is an `HttpBody`, shared with the `Http` client —
`NoBody`, `Text`, `Json`, `Form`, or `Stream of seq<string>`. A `Stream` body
is written chunked and flushed per element (SSE-shaped `data:` lines),
so a lazily produced seq streams as it goes: the client sees the
first elements before the sequence is finished. If a `Stream` producer
raises partway through, the error is not raised out of the handler.
Instead it is recorded on the handle, and you read it with
`Server.streamErrors srv` (the response is aborted, so a careful
client can tell it was cut short). `maxConcurrent` limits how many
handlers run at once, as with `Seq.pmapWith`; extra requests wait in a
queue.

The config record is `{ port; maxConcurrent }`, optionally with a third
field `bodyTimeout` (a `Duration`, default 30s) that limits how long
reading the request body may take. A client that sends its body too
slowly gets a 408 instead of tying up a handler.

`HttpServerRequest` carries:

- `method` — an `HttpMethod`. A valid method the union has no case
  for arrives as `Other of string` (match it with `| Other v ->`),
  `QUERY` arrives as `Query`, and a malformed method gets a 400
  before your handler runs
- `path`
- `query` — the raw string without the leading `?`; split it with
  `Str.trySplitOnce`
- `headers` — pairs, in the order received. The underlying listener
  keeps only the last value of a repeated header, so a proxied
  `X-Forwarded-For` chain shows only the last hop
- `body` — the request text

Deliberately not supported in v1:

- TLS — put a reverse proxy in front
- a routing DSL — the `match` is the router
- WebSockets
- request-body streaming
- HTTP/2
