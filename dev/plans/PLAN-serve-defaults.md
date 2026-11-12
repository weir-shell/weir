# PLAN: `within serve` joins the options family

**Status: DONE** — [D:serve-defaults]; rulings 1–4 below are the user's (2026-11-11).

## The receipt

```weir
within serve flume = { port = Some 8080; maxConcurrent = 8 } handle
    print $"Flume serving at: {flume |> Server.port}"
until stopped
```

- `maxConcurrent` has no default; not caring about the port still costs `port = None`.
- No `key=value` sugar, unlike `retry` / `poll` / `repeat`.
- The handler is a second positional atom, outside the record.
- In the editor, a half-typed `within serve` hovers nothing: no way to learn the shape.

## The proposal

```weir
within serve flume
until stopped

within serve flume port=8080 handler=route
    print $"serving at {Server.port flume}"
until stopped

let cfg = { Server.defaults with port = Some 8080; handler = route }
within serve flume = cfg
until stopped
```

One rule, `retry`'s: `k=v …` is `{ Server.defaults with k = v; … }`.

## Rulings

1. **The handler is a `ServerConfig` field** (`handler: HttpServerRequest -> HttpServerResponse`).
   `within serve s = cfg` is the record form. The two-atom `= cfg handler` keeps parsing; its
   handler atom overrides the record's.
2. **`maxConcurrent` defaults to the CPU count, at least 8.** `Server.defaults`: `port = None`,
   `maxConcurrent = max 8 cpus`, `bodyTimeout = 30s`, `maxBody = 16MiB`, `handler = <landing page>`.
   The floor: handlers are mostly I/O-bound (they run commands), and a 1-CPU container must not
   serve serially — a long handler would block `/health`.
3. **A plain `T` for an `Option<T>` field means `Some` — in `key=value` options only.**
   `port=8080`, and `retry timeout=2m`. `port=(Some 8080)` / `port=None` still check. The record
   form stays strict.
4. **Announce only when the block has no body.** One stderr line:
   `serving on http://localhost:<port> — Ctrl+C to stop`. A body is the opt-out; no knob.

## Details

- **Body-less form**: `within serve s [opts]` directly followed by `until stopped` (and/or `always`).
  Not the F# `use` shape PLAN-within-always-any rejected — the edge stays visible. A body-less
  `within serve` with neither is a parse error naming the shape.
- **Default handler**: `GET /` → 200 text/plain: weir is serving, no handler is set, and the
  snippet that sets one. Any other request → 404 with the same hint. The listener is loopback-only.
- **Literal compatibility**: `{ port; maxConcurrent }` (± `bodyTimeout`, `maxBody`) literals keep
  checking — missing fields rest at `Server.defaults`, `handler` included.
- **Internal key** `|serveDefaults` (the `|retryDefaults` pattern, [D:desugar-capture]); public
  spelling `Server.defaults`.
- **Option lift**: the kv desugar marks its values; the checker lifts where the field is
  `Option<T>` and the value checks as `T`. Never in a written record.
- **Fast parser**: new shapes may `refuse` (the FParsec fallback) unless parity tooling demands
  a native arm; `tools/check-parity.weir` green either way.

## Editor

- Hover on a within kind / `retry` / `poll` / `repeat` answers at token level — the statement need
  not parse: the kind's doc, the shape line, the option keys with defaults (read off the defaults
  record, one source).
- Completion offers the option keys after `within serve s ` (and the loop heads, if not already).
- A bare `within serve` parse error names the shape.

## Stages (one commit each, battery pieces green per stage)

1. `ServerConfig.handler`, `Server.defaults`, default handler, legacy literal + two-atom forms intact.
2. Grammar: kv head, `= cfg`, body-less + `until stopped`; announce.
3. Option lift in kv (serve + loops).
4. Editor: token-level hover, key completion, the shape error.
5. `weir fmt`, tree-sitter / TextMate grammars, docs (GUIDE, reference/scopes, repl, SKILL), DECISIONS
   row `serve-defaults`, CHANGELOG, e2e pins.

## Out of scope

Route patterns (`| POST @"/topic/([^/]+)" topic ->`) — a separate proposal.
