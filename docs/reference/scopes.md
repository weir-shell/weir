# Scopes

`within` holds a resource for the length of a block and releases it
however the block exits: normal completion, an error, `exit n`,
SIGINT or SIGTERM. This works both in a terminal and detached: a
`kill -INT`/`kill -TERM` sent to a `setsid` or backgrounded weir
cleans up the same way, then dies by the signal (`$?` 130/143). While
a `repeat` runs or a scope is held by [`until stopped`](#until-stopped),
the first SIGINT, SIGTERM or SIGHUP is only a request to stop
([`repeat`](loops.md#repeat)): nothing is cut or killed, the loop
finishes its iteration, the held scope stops holding, and the scopes
close by their normal path; the second signal is the teardown
described here. The
cleanup runs on the flow's own thread (a script's, a REPL entry's),
and no statement after the signal runs: a sleep
or a wait on a scoped process is cut short, and the block unwinds from
there. Running `pmap`, `piter` and `pfirst` arms stop at their next
builtin call, command or sleep, and the script waits for them before
any scope releases its resource. A reader closing weir's stdout
(`weir s.weir | head -1`) ends the script the same way, exiting with 141 (bash's SIGPIPE status) and
no message. A second signal during cleanup exits immediately, so
pressing Ctrl+C twice gets you out: no cleanup runs after it, every
child is killed, and weir dies by the first signal. At a terminal, the
commands an `always` cleanup runs start with Ctrl+C and Ctrl+\ ignored,
so a Ctrl+C reaches the script but does not kill them; they keep the
terminal, so a pager in a cleanup works. There are two exceptions, both by design. `kill -9` of weir
itself skips cleanup (only a lock is still released, by the kernel).
And a weir started in the background where job control is off (`weir … &`
in a script, `sh -c 'weir … &'`, after `set +m`) inherits SIGINT ignored,
the shell's rule for a background command; while it keeps a controlling
terminal, Ctrl+C in the terminal does not reach it, and neither does a
`kill -INT` of its pid. Send it SIGTERM, or SIGHUP unless `nohup` ignored
that too. With job control on (`weir … &` or `nohup weir … &` typed at an
interactive shell) the job runs in its own process group with SIGINT
live: a terminal Ctrl+C is not sent to it, and `kill -INT` works as usual. The block is an ordinary block: its statements run
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
Inside the block, `Env.get`, `Env.vars` and `Env.load` see the overlay
too, as `pwd` sees `within cd`, and so do `Path.home`, the XDG helpers
(`Path.configHome` and the rest) and a typed `~`; after it, they read
the unchanged process environment. A command started in the block is
looked up on the overlay's `PATH` when it sets one, as the child would
look it up — a `^$tool` head included, and `Path.which`. Nested overlays combine, with inner values winning, and an
env given directly on a capture (`$e(...)`) wins over any enclosing
`within env`:

```weir
let vars = [Env.pair "GREETING" "scoped"]

within env vars
    sh -c "echo child sees $GREETING"

print (Env.get "GREETING" |> Option.defaultValue "parent stays clean")

within env HOME="/srv/app"
    print (Path.home ())
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

## `until stopped`

Any `within` can end with `until stopped`, the bare one included. The
block runs, then the scope is held until the program is asked to
stop, then it closes by its normal path: a `serve` drains its
handlers, a `proc` kills its process tree, `always` runs whole. The
statements after the scope run, and the program exits 0. It is how a
server or a sidecar stays up until the operator stops it:

```weir-demo
within serve srv = { port = 8080; maxConcurrent = 4 } handler
    print $"listening on {Server.port srv}"
until stopped

print "server stopped"
```

The request is the first SIGINT, SIGTERM or SIGHUP (Ctrl+C on
Windows) while the scope is held. Nothing is cut or killed by it:
handlers in flight get the normal two-second drain, not the signal
path's immediate cut. A second signal is the usual teardown. A signal
that arrives while the block itself is still running is the usual
teardown too; the scope only listens for the request once it is
held. If a stop was already requested (by a `repeat` that ended on
it, say), the scope does not hold at all and closes right after its
block. At the prompt, the first Ctrl+C ends the hold and the prompt
returns.

`always` comes after `until stopped`: the scope is held, then the
cleanup runs, then the resource is released. Undoing a set-up when
the operator stops the script needs no resource at all:

```weir-demo
within
    kubectl scale deploy/worker --replicas=1
until stopped
always
    kubectl scale deploy/worker --replicas=0
```

Scoped processes nest. A tunnel held for as long as the script runs,
with a server behind it:

```weir-demo
within proc fwd = kubectl port-forward svc/db 5432:5432
    poll timeout=30s interval=200ms watch=fwd
        Net.portOpen 5432
    within proc tun = ssh -N -R 9000:localhost:8080 bastion
        within serve srv = { port = 8080; maxConcurrent = 4 } handler
            print "up"
        until stopped
print "tunnels closed"
```

The inner scope holds; when it is asked to stop, the server drains,
then `ssh` and `kubectl` are killed in reverse order of starting.

`until stopped` is refused inside a `plan` (a dry run cannot wait for
a signal) and inside `pure`. `stopped` is not a value: it is only a
word after a `within`'s `until`, and any other word there is an
error. Written after `always`, it is an error that gives the right
order.

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
proxy in front of it for a public address. A request whose `Host`
names anything else gets a 404 before the handler runs, so a proxy
must pass a loopback `Host` (nginx's default `proxy_pass
http://127.0.0.1:8080` does).

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
is written chunked and flushed per element (SSE-shaped: one event per
element, one `data:` line per line of it, so a newline cannot forge an
event),
so a lazily produced seq streams as it goes: the client sees the
first elements before the sequence is finished. If a `Stream` producer
raises partway through, the error is not raised out of the handler.
Instead it is recorded on the handle, and you read it with
`Server.streamErrors srv`. The client can tell too: the connection
closes without the chunked body's final empty chunk, so its HTTP layer
reports a truncated body (curl exits 18) rather than a complete
stream. A response header that can't be sent — a name that isn't a
valid header token, or a value with a control or non-ASCII character
— is never dropped quietly: the client gets a 500 and
`Server.streamErrors` names the header and why. A handler that raises,
or returns a status outside 100–999, also gets the client a 500 and
leaves a line there (`serve handler on GET /x failed: …`). `exit n`
in a handler stops the scope: no new request, the body and the other
handlers are stopped with their cleanups whole, the scope closes, and
the script exits n. A REPL ^C
that interrupts a handler is not a failure: the connection closes
without an answer and nothing is recorded.
`maxConcurrent` limits how many handlers run at once, as with
`Seq.pmapWith`; extra requests wait in a queue, and pipelined requests
on one connection are answered in order.

The config record is `{ port; maxConcurrent }`, optionally with
`bodyTimeout` (a `Duration`, default 30s) that limits how long
reading the request body may take, and `maxBody` (a `Size`, default
16MiB) that limits its size. The body is read before a handler slot
is taken, so a slow upload never ties one up: a client that sends its
body too slowly gets a 408, and a body larger than `maxBody` gets a
413 and is never buffered whole. The handler's `req.body` is decoded
per the request's `Content-Type` charset (UTF-8 when none is given); a
body in a charset weir cannot decode — anything but utf-8, utf-16,
utf-32, us-ascii and iso-8859-1 — gets a 415 and no handler runs.

When the block exits, the port is freed at once and requests still
waiting in the queue are dropped. Handlers already running get two
seconds, together, to finish their responses. A handler still running
after that is stopped at its next builtin call, command, sleep or
request (its commands are killed) and its response is discarded, so
none of its effects happens after the block has exited.

`HttpServerRequest` carries:

- `method` — an `HttpMethod`. A valid method the union has no case
  for arrives as `Other of string` (match it with `| Other v ->`),
  `QUERY` arrives as `Query`, and a malformed method gets a 400
  before your handler runs
- `path`
- `query` — the raw string without the leading `?`; split it with
  `Str.trySplitOnce`
- `headers` — pairs, one per value: a repeated header (a proxied
  `X-Forwarded-For` chain) arrives whole, its values in the order
  received. Compare names case-insensitively
- `body` — the request text

Deliberately not supported in v1:

- TLS — put a reverse proxy in front
- a routing DSL — the `match` is the router
- WebSockets
- request-body streaming
- HTTP/2
