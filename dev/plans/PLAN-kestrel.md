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

## Design

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
