# PLAN — `serve` on Kestrel

Status: ACTIVE (user, 2026-10-16) — picked up after hunt 3 round 1 found
`serve` behind most of its HTTP findings (see "Hunt 3 findings in scope").
Supersedes the "later" of [D:serve-pipelining-later].

## Problem

`serve` runs on the managed `HttpListener` [D:http-serve]. Three defects
trace to it, and none has an in-place fix:

- **Pipelining:** `HttpListener` discards pipelined request bytes on one
  connection, so only the first request is answered (BACKLOG hunt-2
  item; PLAN-review-fixes.md session 2026-10-15).
- **F10:** duplicate request headers collapse before weir sees them;
  a repeated `X-Forwarded-For` (the documented reverse-proxy posture)
  reaches the handler as the last hop only.
- **F8 depends on .NET internals:** the cut-off body after a producer
  failure sets the private `_trailer_sent` field by reflection
  [D:serve-stream-truncate]. It's guarded and unit-pinned, but a
  runtime bump can rename the field.

On Windows `HttpListener` sits on http.sys, a different stack from the
Linux/macOS managed one — so `serve` is really two servers today.

## Hunt 3 findings in scope (PLAN-hunt3.md, round 1)

Platform (HttpListener) — gone by construction, each pinned:

- **H3-E6:** after a listener-generated 404 (foreign Host, absolute-form
  URI) or 505, the same connection also gets an unsolicited
  `200 OK` + `Connection: close` — a keep-alive client or proxy pairs it
  with its next request.
- **H3-A3:** `[::1]` is registered but every request gets the
  listener's `400 Bad Request (Invalid url …)`, never the handler.
- **H3-E5:** one slow request body holds a handler slot for the whole
  `bodyTimeout`; `maxConcurrent` slow clients deny service. Kestrel's
  minimum request body data rate closes it at the connection, before a
  slot is taken.

weir's own `serve` loop — rewritten by phase 1 anyway, fixed there:

- **H3-C3 (high):** a handler still running 2 s after the scope closes
  crashes weir (exit 134): teardown joins each handler with a 2 s cap,
  disposes the gate, and the late handler's `gate.Release()` throws on
  a worker thread. The joins are also serial (2 s × N to close).
  Required: no crash, ever; a bounded, parallel drain; a late handler's
  result discarded, not raised.
- **H3-E7:** a raising handler (and an invalid status: 0, -1, 99, 1000)
  answers `500 internal error` with no trace for the script — record it
  on `Server.streamErrors` (or a sibling channel; a DECISIONS row).
- **Lows:** a queued request whose client already left is still
  started during close; a `Stream` handler keeps its slot after the
  client aborts, and the abort is not recorded.

## Measured (2026-10-16, native AOT, linux-x64)

Probes in `/output/tmp/kestrel-probe` (scratch): each serves one
request; sizes on top of a baseline that already uses `HttpClient`, as
weir does.

| server | binary | added |
|---|---|---|
| `HttpClient` only | 5.71 MB | — |
| + `HttpListener` | 5.91 MB | +0.2 MB |
| + Kestrel driven directly (`UseKestrelCore`, no web-app layer) | 8.55 MB | +2.8 MB |
| Kestrel via `WebApplication.CreateSlimBuilder` (no `HttpClient`) | 9.14 MB | ~+3.4 MB |

weir's AOT binary is 18.8 MB, so the expected cost is ~+2.8 MB (~15%).
Not measured yet: startup to first response, RSS, thread count.

## Phase 0 results (2026-10-16, native AOT, linux-x64, 15 vCPU)

A = this branch's base (80cbdf6, HttpListener), B = the Kestrel bridge
(`KestrelServer` driven directly), both published by the same SDK
(10.0.402) in one session. Runs alternate A/B (ABBA…), 8 each.

| target | A | B | verdict |
|---|---|---|---|
| AOT size ≤ +4 MB | 18.84 MB | 20.55 MB | +1.71 MB — met |
| no new IL warnings | IL2104 ×2, IL3053 (FSharp.Core, FParsec) | the same three | met |
| startup → first 200 (spawn, `within serve`, one GET) | median 22 ms (11–26) | median 24 ms (20–41) | +2 ms — met |
| idle RSS / threads (1 s after first response) | 24.2 MB / 10–11 | 25.8 MB / 11 | +1.6 MB, +0–1 thread |
| under load: 64 clients × 3 s, `maxConcurrent = 16`, 300 ms handler — peak RSS / peak threads | 34.4 MB / 29 (max 30) | 29.8 MB / 31 (max 32) | −4.6 MB, +2 threads; same 208 requests served |
| no console output, no signal handlers | — | stdout/stderr empty; `SigCgt` identical to A (0x300024486); SIGINT exit 130 as A | met |

Thread use stays far inside the 2048-pid cap: Kestrel adds its
heartbeat thread and runs on the shared pool; each in-flight handler
is still one weir worker thread (unchanged).

The direct path: `new KestrelServer(Options.Create opts,
SocketTransportFactory(…, NullLoggerFactory.Instance),
NullLoggerFactory.Instance)` + `StartAsync` with an
`IHttpApplication<IFeatureCollection>` — public API, AOT-clean, no
host, no logging, no lifetime. No DECISIONS fallback row needed.

Pinned behaviours, probed raw on both (`Date` ignored):

- **Port in use:** weir's prefix holds — `serve: cannot listen on
  127.0.0.1:N — …`; the tail is the runtime's (A: "Failed to listen on
  prefix … conflicts with an existing registration", B: "Failed to bind
  to address http://127.0.0.1:N: address already in use"). The
  harness-ports retry matches on "cannot listen", unaffected.
- **IPv6-less host** (`DOTNET_SYSTEM_NET_DISABLEIPV6=1`): both serve
  127.0.0.1 and `localhost`; `[::1]` refused on both. B binds v4 alone
  when the OS reports no IPv6, and falls back on an
  AddressFamilyNotSupported/AddressNotAvailable bind error.
- **Bad method** (`G\x01T`, `BA\x01D`): 400 on both, before weir code
  (A's body was the listener's own HTML, B's is empty); weir's
  "malformed request method" stays for a token Kestrel admits.
- **Oversized header:** a 20 KB header is served by both; 40 KB → A
  400, B 431. Kestrel's 32 KB total is kept; the request line is raised
  to 256 KiB (A served a 200 KB URL) and the header count to 1000 (A
  served 150; Kestrel's default is 100).
- **Oversized body:** 413 with weir's text on both. Kestrel's global
  `MaxRequestBodySize` is NOT used: it counts chunked framing (a 3-byte
  chunked body was refused at a 10-byte bound). A declared length is
  enforced by Kestrel per request (its refusal closes the connection
  at once, no drain); a chunked body is counted by weir.
- **`Host: localhost`:** reaches the handler on B over any loopback
  address (A answered it only on the address `localhost` resolved to).
- **Pipelining / F10 / E6 / A3:** A answers one of two pipelined
  requests, collapses `X-D: one/two/three` to `three`, sends an
  unsolicited `200` after a 404/505, and 400s every `[::1]` request; B
  answers both in order, keeps all three, one response per request,
  and serves `[::1]`.
- Not reproducible on B, recorded: A served `/x%00y` (as `/x%00y`), B
  400s a NUL in the target; A refused CL+TE (400), B reads it as
  chunked and closes after (RFC 9112 §6.3); request headers keep wire
  order per name, but Kestrel enumerates known headers in its own order
  before the rest (A kept full wire order).

**5. Windows and macOS CI runs: not done** — no push from this
environment. The bridge has no OS branch (Kestrel's socket transport on
all three); the first CI run of the branch is the probe.

Go: every phase-0 target met.

## Phases 1–2 (2026-10-16, branch feat/kestrel)

Landed as planned — `HttpListener` is gone from `Serve.fs`, Eval's pull
loop and error arms kept — with these departures, each found while
probing and recorded in [D:serve-kestrel]/[D:serve-drain]:

- **E5 is closed by reading the body in the request delegate**, before
  the request is parked — not by Kestrel's minimum data rate. The rate
  rule is OFF: `bodyTimeout` stays the one documented bound (a slow but
  steady upload inside it still succeeds).
- **`MaxRequestBodySize`** is set per request, only for a declared
  length (Kestrel's refusal then closes at once instead of draining); a
  chunked body is counted by weir — Kestrel's global limit refused a
  3-byte chunked body at a 10-byte bound.
- **F8 without reflection needs weir's own chunk framing** on HTTP/1.1:
  Kestrel's `Abort()` is a TCP reset (curl 56, not 18), and its own
  chunking writes the terminator on every graceful end. With
  `Transfer-Encoding: chunked` set by weir, a producer failure asks
  `IConnectionLifetimeNotificationFeature.RequestClose()` and completes
  with no terminator: a FIN mid-body, curl 18.
- **A foreign `Host` is refused in the delegate** (404 `unknown host`,
  one response, the connection stays in frame) — HttpListener's prefix
  match did this implicitly; kept so the loopback posture and its
  DNS-rebinding guard don't widen. Any port is accepted.
- **Teardown (C3):** `Serve.stop h drainMs` — port freed first, queued
  requests detached unstarted, in-flight ones get the drain together,
  then detached (abort + writes dropped). The gate is never disposed;
  the accept loop takes its slot before dequeuing; the signal sweep
  stops with no drain.
- `Serve.start` takes the body limits (the delegate needs them);
  `readRequest` takes the parked request only; `Serve.finish` releases
  the delegate; `canTruncate`/`trailerSentField`/`abortWithoutTrailer`
  are deleted, and so is the `WebHeaderCollection` scratch check (the
  shared predicate plus the add's own raise cover it).

Pins (finding → pin):

| finding | unit (tests/Weir.Tests serveTests) | e2e (ci/e2e.sh, AOT) |
|---|---|---|
| pipelining | two and three on one connection, in order | `PIPE2`/`PIPE3` |
| F10 | three `X-Forwarded-For`, in order | `XFF`; multi-repro F10 asserts `one two three` in order |
| F8, no reflection | terminator absent after a raise, present on a clean stream | `TERMINATED=False` (now on every OS); curl exit 18 |
| H3-E6 | foreign Host, absolute-form, `HTTP/9.9`: one response each, follow-up pairs with its own | `E6-HOST`/`E6-ABS`/`E6-VERSION` |
| H3-A3 | `[::1]` reaches the handler (when bound) | curl `-g http://[::1]` (skips without IPv6) |
| H3-E5 | a fast request overtakes a slow body | 3 slow bodies, 1 slot, fast < 2 s |
| H3-C3 | — | 3 s handler after close: exit 0, `always`, port free, rebind; 8 stuck handlers close in ≤ 6 s |
| H3-E7 | — | raise and `status = 0` → 500 + recorded lines |
| lows | — | 6 gone clients, 2 slots → 2 starts; aborted Stream frees its slot |
| shutdown | port frees, rebind (start/stop pin) | SIGINT/SIGTERM with a request in flight → 130/143, `always`, port free |

Each new e2e cell was run against the base binary too: all fail there
except F8-curl, shutdown and the Stream-slot half of the lows (parity
pins). The cross-platform kit (`tools/hunt3-xplat.weir`, branch
hunt3/xplat) should gain the pipelining, F10, E6, A3 and F8-curl cells
— not on this branch.

Gates at cd6de6b (AOT 20.55 MB, no new IL warnings): unit 2197/2197,
Weir.Fuzz builds, ci/e2e.sh all green (868 ok), multi-repro / sweep-repro
/ hunt2-repro all fixed, skill-doc 315 blocks, fsharp-oracle 194 pass,
dead-privates 0, decision-keys ok, commit-area 80cbdf6..HEAD ok.
Windows/macOS CI not run (no push from here).

Open for a user ruling (not decided here):

- **Stream client aborts stay unrecorded.** The hunt-3 low says the
  abort "is not recorded"; [D:serve-stream] rules a client disconnect is
  NOT reported (a monitoring loop reads `streamErrors` as "something
  failed"; browsers closing an SSE tab would flood it). The slot half
  is fixed; recording is left to a ruling.
- **Cross-name header order.** Kestrel enumerates its known headers
  (Host, Connection, …) before the rest; values of one name keep wire
  order. Docs now say "in wire order within the name".
- **Foreign `Host` refusal** kept (above). A reverse proxy configured
  with `proxy_set_header Host $host` is refused — as it was before.

## Design

**Dependency:** Kestrel is part of the ASP.NET Core shared framework
that ships with the .NET SDK — `<FrameworkReference
Include="Microsoft.AspNetCore.App" />` in Weir.fsproj, no NuGet package
(the standalone Kestrel packages ended at 2.x). AOT publish pulls the
`Microsoft.AspNetCore.App.Runtime.<rid>` pack, version-locked to the
SDK; security updates ride the SDK like HttpListener's do.

**Keep `Serve.fs`'s surface; swap its insides.** `HttpListener` appears
nowhere outside `Serve.fs`. Eval drives a PULL loop (`start`, `accept`,
`readRequest`, `writeResponse`, `stop`, `streamErrors`) and owns the
concurrency ceiling (`maxConcurrent`) around it. Kestrel is PUSH (a
request delegate per request). Bridge them inside `Serve.fs`:

- the request delegate wraps the `HttpContext` in an opaque `SCtx`,
  writes it to a bounded channel, and awaits a completion the worker
  sets after `writeResponse` returns;
- `accept` reads the channel (None once stopped);
- Eval's accept loop, worker pool and every error arm stay as they are
  — only the context type changes from `HttpListenerContext` to `SCtx`.

**Drive `KestrelServer` directly, never the generic host.** The host's
console lifetime installs its own SIGINT/SIGTERM handling, which would
fight weir's signal sweep and `within serve`'s finally path; its
default logging writes to the console, and weir's stdout is data. So:
no `HostBuilder`/`WebApplication`, logging is a null factory, the
server is started and stopped by `Serve.start`/`Serve.stop`. Phase 0
confirms which public constructor/factory gives that in AOT; if only
the host path is AOT-clean, it must run with the console lifetime and
every log provider removed, and that's a DECISIONS row.

**Settings to pin explicitly** (Kestrel defaults differ from weir's laws):

- HTTP/1.1 only; no HTTP/2/3 (loopback, reverse-proxy posture).
- Bind 127.0.0.1, ::1 when present, and answer `Host: localhost`
  [D:serve-loopback-names]; never a public bind.
- `AddServerHeader = false`; decide whether `Date` stays.
- Body limits: `MaxRequestBodySize` = weir's `maxBody`
  [D:serve-max-body]; the body read timeout [D:serve-body-timeout] maps
  to a cancellation, not Kestrel's min-data-rate (different semantics).
- Header limits match [D:serve-header-limit]; method validation
  [D:serve-method] — check whether Kestrel's own 400 pre-empts weir's
  "malformed request method" and keep weir's wording either way.
- Synchronous IO is off by default in Kestrel: write the response
  asynchronously (preferred) rather than flipping `AllowSynchronousIO`.
- Streams: response buffering off, flush per element, so a client
  sees each SSE event as it's pulled [D:serve-stream].

**What gets simpler:**

- F8: a producer failure calls `HttpContext.Abort()` — the connection
  closes with the chunked body unterminated, no reflection.
  `trailerSentField`/`canTruncate`/`abortWithoutTrailer` are deleted.
- F10: headers arrive as `StringValues` per name — emit one pair per
  value, in wire order.
- Pipelining: handled by Kestrel.
- One server on every OS.

## Phases

**Phase 0 — probes, go/no-go (half a session).** On weir itself, in a
branch:

1. AOT publish size delta and trim warnings (target ≤ +4 MB, no new
   IL warnings).
2. Startup to first response, RSS and thread count idle and under
   `maxConcurrent` load, vs today. The container caps pids at 2048 —
   Kestrel's thread use matters for parallel agents and CI.
3. The direct-`KestrelServer` path builds and runs under AOT with no
   console output and no signal handlers installed.
4. Each pinned behaviour from the settings list, probed on Kestrel
   before writing the bridge: port-in-use error text, IPv6-less host,
   bad method, oversized header/body, `Host: localhost`.
5. Windows and macOS CI runs of the probe.

Stop and bring the numbers to the user if any target misses.

**Phase 1 — the swap (one session).** Bridge as above; port each arm of
`readRequest`/`writeResponse`; delete the reflection; update the
module header comment ("~0 dependency bytes" is no longer true).

**Phase 2 — prove it (with phase 1, or the next session).**

- Every existing `serve` pin stays green unchanged: the e2e serve cells,
  the `Weir.Tests` serveTests, `tools/multi-repro.weir` (F3/F4/F8/F9/F11).
- New pins: pipelining (two and three requests on one connection, raw
  socket, responses in order); F10 with THREE duplicates (two can't tell
  last-wins from a merge); F8 still exits curl 18 with no reflection;
  one per hunt-3 finding above (E6 one response per request, A3 `[::1]`
  reaches the handler, E5 slow bodies don't block a fast client, C3 a
  late handler never crashes and the scope closes in bounded time, E7 a
  raising handler is recorded). The hunt-3 repros are under
  /output/tmp/h3-E/, h3-A/repro/, h3-C/ — port them into pins, don't
  depend on the scratch paths.
- Windows/macOS CI jobs run the serve cells (the W3 cross-platform hunt
  can reuse them).
- Shutdown: Ctrl-C and SIGTERM inside `within serve`, scope exit with
  requests in flight, port free immediately after (the
  "second bind succeeds after close" pin).

## Exit criteria

- All existing serve pins green with message text unchanged; new
  pipelining and F10 pins green on Linux, macOS and Windows CI.
- No reflection into framework internals in `Serve.fs`.
- Every hunt-3 finding listed above pinned and green.
- Size delta and startup within the phase-0 targets.
- DECISIONS: a new row for the Kestrel move; [D:serve-stream-truncate]
  and [D:serve-pipelining-later] updated to point at it; F10 closed in
  PLAN-review-fixes.md; the BACKLOG pipelining item struck; CHANGELOG
  entries for pipelining and F10.

## Estimate

Phase 0 half a session; phases 1–2 one to two sessions. The swing
factor is how many of Kestrel's defaults disagree with weir's pinned
wording (method, limits, error text) and need shimming.
