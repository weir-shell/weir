# PLAN — `serve` on Kestrel

Status: PARKED — ruled later (user, 2026-10-16) [D:serve-pipelining-later].
Pick up when a `serve` limitation bites (a pipelining client, F10, a
Windows/macOS divergence from W3) or as a deliberate session of its own.

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
  last-wins from a merge); F8 still exits curl 18 with no reflection.
- Windows/macOS CI jobs run the serve cells (the W3 cross-platform hunt
  can reuse them).
- Shutdown: Ctrl-C and SIGTERM inside `within serve`, scope exit with
  requests in flight, port free immediately after (the
  "second bind succeeds after close" pin).

## Exit criteria

- All existing serve pins green with message text unchanged; new
  pipelining and F10 pins green on Linux, macOS and Windows CI.
- No reflection into framework internals in `Serve.fs`.
- Size delta and startup within the phase-0 targets.
- DECISIONS: a new row for the Kestrel move; [D:serve-stream-truncate]
  and [D:serve-pipelining-later] updated to point at it; F10 closed in
  PLAN-review-fixes.md; the BACKLOG pipelining item struck; CHANGELOG
  entries for pipelining and F10.

## Estimate

Phase 0 half a session; phases 1–2 one to two sessions. The swing
factor is how many of Kestrel's defaults disagree with weir's pinned
wording (method, limits, error text) and need shimming.
