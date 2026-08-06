# weir — `Http`: the typed request boundary

**SESSION 1 SHIPPED (2026-08-06) [D:http].** The core landed: the
`HttpRequest`/`HttpResponse` records, the `Auth`/`HttpBody`/`HttpMethod`
unions (all prelude types — field names are public API), `Http.defaults`,
`Http.send`, the 30s timeout, status-is-data, transport-raises with the
contracts shapes, `Secret` auth with `show` masking, and the byte-exact
mangling pin. **The body ruling** (the plan left it open with a `?`):
`HttpBody = NoBody | Json of seq<string> | Text of string` — monomorphic,
the caller serializes with the existing `to json` (`body = Json (payload
|> to json)`), because weir has no Jsonable class to constrain a generic
`Json of 'a`. So the whole feature is prelude types + ordinary builtins:
no new `Ty`, no bespoke node, no generic records. **Session 2** (below,
"The rest") is unstarted: remaining methods incl `Query`, `Http.fetch`,
the query-string builder, `insecure`.

**Two settled non-goals from session 1, recorded here so they stay
closed:**

- **Reifiers over `HttpResponse` — DEFERRED, benefit/cost not
  tractability.** Grounded in the code: the reifier family is
  spawn-and-reify FUSED (`|succeeded` etc. call `Proc.completeWith`)
  and pure PARSE MARKERS (`succeeds` is not a nameable value —
  `unbound`), hard-matched to `ECmd` in `foldChain`. Only `succeeds`
  (2xx→true) and `orFail` (non-2xx→raise) map; `exitCode` has no HTTP
  analogue; `complete` is redundant because `HttpResponse` IS the
  record. The clean generalization ("a reifier attaches to a value
  with a success predicate") needs a RUNTIME-witnessed class, which
  breaks weir's closed/erased Eq/Show/Ord invariant (a foundational
  change, its own bless). The cheap version is a hard-coded
  `Completed`+`HttpResponse` pair that saves one line over
  `resp.status >= 400`. **So it is deferred because it buys almost
  nothing** — `HttpResponse` already exposes `.status`, and `Http.fetch`
  (session 2) covers the raise-on-non-2xx shorthand. If a
  success-predicate law ever earns a bless, `Completed` and
  `HttpResponse` are its two customers.
- **`Map` PARKED** (headers stay `seq<string * string>`). Two standing
  receipts: HTTP response headers, and `from json` into a `Map<string,
  T>`. Ramifications past API surface: keys need `Ord`, so it is weir's
  first CONSTRAINED container; literal syntax or none; and `show`'s
  ordering (sorted for determinism).

---

Status: DRAFT for bless (2026-08-01). Origin: the parked sixth
boundary-loader instance (SEMANTICS "http parked"), unparked by two
events — the AOT leg resolved (HttpClient verified end-to-end on the
published binary, HTTPS included, zero new dependency bytes), and the
TRIGGER named: a typed request body cannot reach the wire through
curl without flag arcana whose wrong variant SILENTLY MANGLES the
bytes (`-d @-` strips newlines — form-encoding semantics wearing a
JSON costume; the correct `--data-binary @-` is one flag away and
nothing errors). That is the silent-wrong-answer class, and the typed
value degrading to an untyped stdin stream for exactly one hop runs
weir's pitch backwards.

Functional target: the surface of Arquidev.Fetch
(gitlab.com/arquidevio/tools/fetch — read at draft time, 256 lines),
translated out of its computation expression into weir's own
composition idiom. **The CE was F#'s spelling for "build a request by
accumulating fields, then run it" — weir already has that spelling:
a value flowing through a pipeline.**

## The translation table (every CE op accounted for)

| Arquidev.Fetch CE | weir | note |
|---|---|---|
| `fetch<'T> { … }` | a pipeline ending in `\|> Http.send` | the CE shell itself |
| `GET url` / `POST url` | `Http.get url` / `Http.post url` | request constructors |
| `Authorization v` | `\|> Http.auth v` | dedicated (it earns it — every API script sets it) |
| `Accept v` / `UserAgent v` | `\|> Http.header "Accept" v` | the CE minted per-header ops for CE-syntax reasons; one generic op suffices |
| `body obj` (JSON-serialized) | `\|> Http.json payload` | payload must be JSONABLE — the existing law, reused verbatim; content-type set |
| `ensureOk` (default true) | the reifier-family split, below | |
| `insecure` | LEANING: `\|> Http.insecure` | real ops need (self-signed); loud name; DECIDE at bless |
| `hack (HttpRequestMessage -> unit)` | REJECTED | a raw escape hatch into the platform object is exactly the hole weir refuses; the record is the surface |
| `fetchTask` / `fetchAsync` | NOTHING, deliberately | weir has no async by design; parallel fetches are `urls \|> Seq.pmap (fun u -> Http.get u \|> Http.send)` — the existing concurrency model covers the CE's whole reason for three builders |
| `'T` result typing | `resp.body \|> from json T` | the ONE typed-read door stays; no second deserialization path |
| `FETCH_DEBUG` env logging | NOTHING in v1 | print the record; env-magic behavior switches are the class weir rejects |

## The spelling

    let resp =
        Http.post $"https://api.example.com/items"
        |> Http.auth $"Bearer {tok}"
        |> Http.header "Accept" "application/json"
        |> Http.json { Name = "x"; Count = 3 }
        |> Http.send

    if resp.status >= 400 then fail $"api said {resp.status}"
    let items = resp.body |> from json Item

- **AMENDED for `Secret` [D:secret]:** the spelling above interpolates
  `$"Bearer {tok}"`, which now REFUSES if `tok` is a `Secret` (the
  whole point — a token must not reach a string). So `Http.auth` takes
  a `Secret` directly (`Http.auth : Secret -> HttpRequest ->
  HttpRequest`), and a `Bearer ` prefix is applied with
  `Secret.map (fun t -> "Bearer " + t) tok` — which stays secret. The
  same holds for a `Http.header` carrying a credential: a `Secret`
  overload (or a `Secret`-typed value) keeps it off `show`. Two
  consequences to settle in the session: `HttpRequest`'s `show` MUST
  render auth/secret headers as `***` (it consults the field, like a
  record does), and the value reaches the socket in the clear at
  `send` (the one deliberate reveal — a stated non-claim, the argv
  analogue).
- `Http.get/post : string -> HttpRequest` — constructors. LEANING:
  `put`/`delete` too (four verbs, cheap, no model growth).
- Builder ops are data-last (`Http.header : string -> string ->
  HttpRequest -> HttpRequest`) so `|>` composes — the CE's
  accumulate-then-run, in the idiom scripts already use everywhere.
- `HttpRequest` is an OPAQUE builtin value (the `Env.fromFile`
  precedent: a value whose only consumers are its own operations;
  `show` renders a summary, `==` refuses like functions).
- `Http.send : HttpRequest -> HttpResponse` where
  `type HttpResponse = { status: int; headers: seq<string * string>;
  body: seq<string> }` — the `complete`-record precedent: OUTPUT
  GOES WHERE THE MEANING GOES.

## The posture (the reifier-family split, settled)

- **`Http.send` never raises on STATUS** — status is data, exactly
  as `| complete` treats exit codes. It raises only on TRANSPORT
  failure (unreachable, TLS, timeout) — the spawn-failure analog —
  with the contracts fetch's ruled message shapes (host named,
  status named).
- **The raising shorthand for the 80% case**: LEANING —
  `Http.fetch url : seq<string>` (GET, raise on non-2xx naming the
  status, body only): the `curl -sf` one-liner analog, so the
  simple read does not pay record ceremony. Mirrors
  `succeeds`/`orFail` living beside `complete`.
- Arquidev's `ensureOk=true` default becomes: use `Http.fetch` when
  you want raising, `Http.send` when status is data. Two names, no
  boolean.

## What this is NOT (the drop-command-builtins bar, addressed)

`feed` was retired because it duplicated a spelling commands already
had. `Http` clears that bar on the REQUEST side only: the response
side (`curl url | from json T`) is already well-spelled and stays
the recommended read for plain GETs — the docs should SAY so.
`Http` exists for (a) the typed request body (the mangling trap),
(b) typed status/headers without curl flag archaeology, (c) the
no-curl environments the Windows arc may meet. It does not grow
weir an async model, an HTTP client config surface, or a second
JSON door.

## The family fit

This is the boundary-loader family's sixth instance, request
direction: `Http.json` consumes the JSONABLE law (the same walker
`to json` uses — no new type list; the refactor-analysis's
three-walkers observation stays at three). The response record's
`body` feeds the existing adapters. Check-time: nothing — builtins
run at eval; never-during-check is inherited, not implemented.

## The district question, answered (asked at draft review)

An `http` district (a checked block literal shaped like the
`.http`/REST-client file format — method+url line, `k: v` headers,
a body splice) was considered and DEFERRED, not rejected. Against
the criteria that paid for the yaml district: HTTP requests have no
build-hostile structure (the one dangerous slot is the body — one
splice), thin check-time validation surface, and a paste corpus
(`.http` files) that is really about collections. Against the known
cost: a third district species buys the full surface tax (assembler
species, template parser, three grammars, fmt, REPL tint,
completion — the yaml district took four sessions of polish across
that surface) for a four-line literal. THE NO-LOCK-IN PROPERTY
decides the ordering: a district would produce an `HttpRequest`
value and end in `|> Http.send` anyway — sugar over this plan's
machinery — so the pipeline forecloses nothing. THE TRIGGER,
recorded: heavy real use of the builtin AND requests in practice
looking like pasted `.http` files rather than two-liners. Revisit
then; not before.

## Sessions (each its own bless)

1. **The core**: `HttpRequest`/`HttpResponse` types, `get`/`post`,
   `header`/`auth`, `json` (jsonable-law reuse), `send`, transport
   errors with ruled messages. Pins: the mangling case (a multiline
   jsonable body arrives byte-exact — THE trigger, pinned against a
   local server echoing bytes); status-is-data (a 404 binds, never
   raises); parallel `Seq.pmap` fetches; `show`/`==` on the opaque
   request; e2e on the AOT binary via the local-server harness the
   contracts battery already owns. SECURITY.md: the non-claim
   (weir types your request; it does not vet your endpoint).
2. **The conveniences, per demand**: `put`/`delete`, `Http.fetch`
   shorthand, `query k v`, `insecure` (with its loud-name decision),
   timeout posture (default? configurable? — measure real scripts
   first).

## Bars

- Zero movement on everything existing; `curl | from json` stays
  documented as the plain-GET spelling.
- The mangling pin is the acceptance: a payload whose `to json`
  output spans lines round-trips byte-exact through `Http.json` →
  wire → echo — the exact bytes curl's `-d` would have eaten.
- Transport-failure messages ruled and pinned (the contracts fetch
  precedent — reuse its shapes, do not invent a third).
- No `hack`, no env-var switches, no async — rejections stated in
  DECISIONS so they stay closed.
- e2e is offline (local server); the HTTPS path is already
  AOT-verified (refactor-analysis report, the HttpClient row).
